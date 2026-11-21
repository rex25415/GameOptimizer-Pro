// ============================================================================
//  PowerMonitor.cs — 供电状态与节能模式感知（Win32 事件驱动 + 轮询兜底）
//
//  Rev88 升级：
//   · 独立隐藏顶层消息窗线程（_th）注册 RegisterPowerSettingNotification：
//     - GUID_ACDC_POWER_SOURCE   (5D3E9A59-E9D5-4B00-A6BD-FF34FF516548)
//     - GUID_POWER_SAVING_STATUS (E00958C0-C213-4ACE-AC77-FECCED2EEEA5)
//     配合 WM_POWERBROADCAST (PBT_APMPOWERSTATUSCHANGE / PBT_POWERSETTINGCHANGE)，
//     实现 <50ms 插拔电源与 Win11 节能模式瞬时感知。
//   · 保留 PollOnce() / Read() 定时兜底与 HandleStatus() 无头测试入口。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Go.Daemon
{
    public sealed class PowerMonitor : IDisposable
    {
        private const int WM_DESTROY = 0x0002;
        private const int WM_CLOSE = 0x0010;
        private const int WM_TIMER = 0x0113;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;

        private static readonly Guid GUID_ACDC_POWER_SOURCE =
            new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
        private static readonly Guid GUID_POWER_SAVING_STATUS =
            new Guid("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;       // 0 离线 / 1 在线 / 255 未知
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;   // bit 0 = BatterySaverOn
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS st);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetMessageW(out MSG lpMsg, IntPtr hwnd, uint minFilter, uint maxFilter);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, uint flags);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool KillTimer(IntPtr hwnd, IntPtr uIDEvent);

        private readonly object _applyLock = new object();
        private readonly object _winLock = new object();
        private volatile bool _hasBattery;
        private volatile bool _onAc;
        private volatile int _batteryPercent;
        private volatile bool _powerSaverActive;
        private volatile string _lastTransition;

        public bool HasBattery { get { return _hasBattery; } }
        public bool OnAc { get { return _onAc; } }
        public int BatteryPercent { get { return _batteryPercent; } }
        public bool PowerSaverActive { get { return _powerSaverActive; } }
        public string LastTransition { get { return _lastTransition; } }

        /// <summary>供电状态变化（true = 已插电）。</summary>
        public event Action<bool> Changed;
        /// <summary>Win10/11 节能模式状态变化（true = 节能模式已开启）。</summary>
        public event Action<bool> PowerSaverChanged;

        private Thread _th;
        private volatile bool _run;
        private volatile bool _disposed;
        private int _loopExited;
        private int _handlesClosed;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private readonly WndProcDelegate _wndProc;
        private IntPtr _hwnd;
        private IntPtr _notifyAcDc;
        private IntPtr _notifySaver;
        private bool _timerActive;
        public int IntervalMs = 10000;

        public PowerMonitor()
        {
            _onAc = true;
            _batteryPercent = 100;
            _wndProc = new WndProcDelegate(WndProc);
        }

        public void Start()
        {
            if (_disposed || _run) return;
            Thread prior = _th;
            if (prior != null && prior.IsAlive)
            {
                try
                {
                    if (!prior.Join(1000))
                    {
                        Diag.Hit("power.start.busy");
                        return;
                    }
                }
                catch (Exception ex) { Diag.Fail("power.start.join", ex); return; }
            }
            _th = null;

            PollOnce();
            _stop.Reset();
            _ready.Reset();
            Interlocked.Exchange(ref _loopExited, 0);
            _run = true;
            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "go.power";
            _th = t;
            t.Start();
            try { _ready.WaitOne(500); }
            catch (Exception ex) { Diag.Fail("power.start.ready", ex); }
        }

        /// <summary>停止事件窗与兜底轮询。返回时消息线程已被叫醒并 Join。</summary>
        public void Stop()
        {
            _run = false;
            try { _stop.Set(); }
            catch (ObjectDisposedException) { Diag.Hit("power.stop.disposed"); }

            IntPtr hwnd;
            lock (_winLock) { hwnd = _hwnd; }
            if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            Thread t = _th;
            if (t == null || !t.IsAlive) return;
            try
            {
                if (!t.Join(800))
                {
                    IntPtr lateHwnd;
                    lock (_winLock) { lateHwnd = _hwnd; }
                    if (lateHwnd != IntPtr.Zero)
                    {
                        PostMessageW(lateHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        if (t.Join(400)) return;
                    }
                    Diag.Hit("power.stop.timeout");
                }
            }
            catch (Exception ex) { Diag.Fail("power.stop.join", ex); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            Thread t = _th;
            if (Interlocked.CompareExchange(ref _loopExited, 0, 0) != 0 || t == null || !t.IsAlive)
                CloseWaitHandlesOnce();
        }

        private void CloseWaitHandlesOnce()
        {
            if (Interlocked.Exchange(ref _handlesClosed, 1) != 0) return;
            try { _stop.Close(); } catch (Exception ex) { Diag.Fail("power.dispose.stop", ex); }
            try { _ready.Close(); } catch (Exception ex) { Diag.Fail("power.dispose.ready", ex); }
        }

        private void Loop()
        {
            string className = null;
            IntPtr hInst = IntPtr.Zero;
            IntPtr hwnd = IntPtr.Zero;
            try
            {
                hwnd = CreatePowerSinkWindow(out className, out hInst);
                if (hwnd != IntPtr.Zero)
                {
                    lock (_winLock) { _hwnd = hwnd; }
                    if (!_disposed)
                    {
                        try { _ready.Set(); }
                        catch (ObjectDisposedException) { Diag.Hit("power.loop.ready"); }
                    }

                    if (!_run) PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                    MSG msg;
                    while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessageW(ref msg);
                        if (!_run)
                        {
                            IntPtr cur;
                            lock (_winLock) { cur = _hwnd; }
                            if (cur != IntPtr.Zero) PostMessageW(cur, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    return;
                }

                // 无窗口兜底：保持轮询能力不中断
                if (!_disposed)
                {
                    try { _ready.Set(); }
                    catch (ObjectDisposedException) { Diag.Hit("power.fallback.ready"); }
                }
                while (_run)
                {
                    int waitMs = IntervalMs < 1000 ? 1000 : IntervalMs;
                    if (_stop.WaitOne(waitMs)) break;
                    if (!_run) break;
                    PollOnce();
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("power.loop", ex);
            }
            finally
            {
                if (!_disposed)
                {
                    try { _ready.Set(); }
                    catch (ObjectDisposedException) { /* Dispose 竞态关闭时不报伪故障 */ }
                    catch (Exception ex) { Diag.Fail("power.loop.ready.fin", ex); }
                }
                CleanupSinkWindow(className, hInst);
                Interlocked.Exchange(ref _loopExited, 1);
                if (_disposed) CloseWaitHandlesOnce();
            }
        }

        private IntPtr CreatePowerSinkWindow(out string className, out IntPtr hInst)
        {
            className = "GoPowerSink_" + Guid.NewGuid().ToString("N");
            hInst = GetModuleHandleW(null);
            WNDCLASSEXW wc = new WNDCLASSEXW();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW));
            wc.lpfnWndProc = _wndProc;
            wc.hInstance = hInst;
            wc.lpszClassName = className;

            if (RegisterClassExW(ref wc) == 0)
            {
                Diag.Hit("power.win.regclass");
                className = null;
                return IntPtr.Zero;
            }

            IntPtr hwnd = CreateWindowExW(
                WS_EX_TOOLWINDOW, className, "", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Diag.Hit("power.win.create");
                UnregisterClassW(className, hInst);
                className = null;
                return IntPtr.Zero;
            }

            Guid acdc = GUID_ACDC_POWER_SOURCE;
            Guid saver = GUID_POWER_SAVING_STATUS;
            _notifyAcDc = RegisterPowerSettingNotification(hwnd, ref acdc, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (_notifyAcDc == IntPtr.Zero) Diag.Hit("power.notify.acdc");
            _notifySaver = RegisterPowerSettingNotification(hwnd, ref saver, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (_notifySaver == IntPtr.Zero) Diag.Hit("power.notify.saver");

            uint pollMs = (uint)(IntervalMs < 1000 ? 1000 : IntervalMs);
            _timerActive = SetTimer(hwnd, new IntPtr(1), pollMs, IntPtr.Zero) != IntPtr.Zero;
            return hwnd;
        }

        private void CleanupSinkWindow(string className, IntPtr hInst)
        {
            lock (_winLock)
            {
                if (_notifyAcDc != IntPtr.Zero) { UnregisterPowerSettingNotification(_notifyAcDc); _notifyAcDc = IntPtr.Zero; }
                if (_notifySaver != IntPtr.Zero) { UnregisterPowerSettingNotification(_notifySaver); _notifySaver = IntPtr.Zero; }
                if (_hwnd != IntPtr.Zero)
                {
                    if (_timerActive) { KillTimer(_hwnd, new IntPtr(1)); _timerActive = false; }
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
            }
            if (!string.IsNullOrEmpty(className)) UnregisterClassW(className, hInst);
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_POWERBROADCAST:
                    HandlePowerBroadcast(wParam.ToInt64(), lParam);
                    return new IntPtr(1);
                case WM_TIMER:
                    PollOnce();
                    return IntPtr.Zero;
                case WM_CLOSE:
                    CleanupSinkWindow(null, IntPtr.Zero);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    lock (_winLock) { _hwnd = IntPtr.Zero; }
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                default:
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        private void HandlePowerBroadcast(long wp, IntPtr lp)
        {
            if (wp == PBT_APMPOWERSTATUSCHANGE)
            {
                PollOnce();
                return;
            }
            if (wp == PBT_POWERSETTINGCHANGE && lp != IntPtr.Zero)
            {
                try
                {
                    POWERBROADCAST_SETTING s = (POWERBROADCAST_SETTING)Marshal.PtrToStructure(lp, typeof(POWERBROADCAST_SETTING));
                    uint val = s.DataLength >= 4
                        ? (uint)Marshal.ReadInt32(lp, 20)
                        : (uint)Marshal.ReadByte(lp, 20);

                    if (s.PowerSetting == GUID_ACDC_POWER_SOURCE)
                    {
                        bool ac = val == 0;
                        Apply(ac, _powerSaverActive, "事件");
                        PollOnce();
                    }
                    else if (s.PowerSetting == GUID_POWER_SAVING_STATUS)
                    {
                        bool saver = val != 0;
                        Apply(_onAc, saver, "事件");
                        PollOnce();
                    }
                }
                catch (Exception ex)
                {
                    Diag.Fail("power.broadcast.parse", ex);
                    PollOnce();
                }
            }
        }

        public bool PollOnce()
        {
            try
            {
                SYSTEM_POWER_STATUS st;
                if (!GetSystemPowerStatus(out st)) return false;
                _hasBattery = st.BatteryFlag != 128 && st.BatteryFlag != 255;
                _batteryPercent = st.BatteryLifePercent == 255 ? -1 : st.BatteryLifePercent;
                bool ac = st.ACLineStatus != 0;
                bool saver = (st.SystemStatusFlag & 1) != 0;
                Apply(ac, saver, "轮询");
                return true;
            }
            catch (Exception ex)
            {
                Diag.Fail("power.read", ex);
                return false;
            }
        }

        /// <summary>兼容旧接口，内部委托 PollOnce()。</summary>
        public bool Read() { return PollOnce(); }

        /// <summary>状态落点（自检直接调用这里，不需要真的拔电源）。</summary>
        public void HandleStatus(bool onAc) { Apply(onAc, _powerSaverActive, "测试"); }

        public void HandleStatus(bool onAc, bool powerSaver) { Apply(onAc, powerSaver, "测试"); }

        private void Apply(bool ac, bool saver, string why)
        {
            bool acChanged;
            bool saverChanged;
            lock (_applyLock)
            {
                acChanged = ac != _onAc;
                saverChanged = saver != _powerSaverActive;
                _onAc = ac;
                _powerSaverActive = saver;
                if (acChanged)
                    _lastTransition = (ac ? "已接电源" : "已切电池") + " · " + DateTime.Now.ToString("HH:mm:ss");
                else if (saverChanged)
                    _lastTransition = (saver ? "已开启节能模式" : "已关闭节能模式") + " · " + DateTime.Now.ToString("HH:mm:ss");
            }

            if (acChanged)
            {
                Action<bool> h = Changed;
                if (h != null) { try { h(ac); } catch (Exception ex) { Diag.Fail("power.changed", ex); } }
            }
            if (saverChanged)
            {
                Action<bool> hs = PowerSaverChanged;
                if (hs != null) { try { hs(saver); } catch (Exception ex) { Diag.Fail("power.saver.changed", ex); } }
            }
        }

        public string Describe()
        {
            string baseStr = !_hasBattery
                ? "台式 / 无电池"
                : ((_onAc ? "接电" : "电池") + (_batteryPercent >= 0 ? " " + _batteryPercent + "%" : ""));
            return _powerSaverActive ? (baseStr + " · 节能模式") : baseStr;
        }
    }
}
