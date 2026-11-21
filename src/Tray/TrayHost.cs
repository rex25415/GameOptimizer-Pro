// ============================================================================
//  TrayHost.cs — 托盘常驻（Shell_NotifyIcon，不引入 WinForms）
//
//  【Rev64 关键修复】
//  A. Win11 默认把图标塞进"隐藏图标"面板，那里面板不转发点击（真鼠标实测左右键
//     都无回调）——"点托盘没反应"不是消息码的问题，是图标不在任务栏上。安装后把
//     自己的 NotifyIconSettings 条目提升为"已显示"（只在值缺失时写一次）。
//  B. 事件去重：外壳双击会连发两次 NIN_SELECT，右键偶尔补旧码；同类 260ms 内只处理一次。
//  C. 全链路留痕：GO_TRAY_TRACE=1 时把每次托盘回调写进 tray-trace.log。
// ============================================================================
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Go.Tray
{
    public enum TrayVisualState
    {
        Idle = 0,
        Guard = 1,
        Alert = 2
    }

    public sealed partial class TrayHost : IDisposable
    {
        private const int WM_APP = 0x8000;
        private const int WM_TRAY = WM_APP + 1;
        private const int WM_NULL = 0x0000;
        private const int WM_CONTEXTMENU = 0x007B;

        // ---- 旧版（v0）语义：仅作兜底 ----
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        // ---- 版本 4 语义 ----
        private const int NIN_SELECT = 0x0400;
        private const int NIN_KEYSELECT = 0x0401;
        private const int NIN_BALLOONSHOW = 0x0402;
        private const int NIN_BALLOONHIDE = 0x0403;
        private const int NIN_BALLOONTIMEOUT = 0x0404;
        private const int NIN_BALLOONUSERCLICK = 0x0405;

        private const int NIM_ADD = 0x00;
        private const int NIM_MODIFY = 0x01;
        private const int NIM_DELETE = 0x02;
        private const int NIM_SETVERSION = 0x04;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        /// <summary>NIM_SETVERSION 协商到 v4 了吗（决定 wParam 是坐标还是图标 ID）。</summary>
        private bool _v4;

        private const int NIF_MESSAGE = 0x01;
        private const int NIF_ICON = 0x02;
        private const int NIF_TIP = 0x04;
        private const int NIF_INFO = 0x10;

        private const int NIIF_INFO = 0x1;
        private const int NIIF_WARNING = 0x2;
        private const int NIIF_NOSOUND = 0x10;

        private const int IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const int SM_CXSMICON = 49;
        private const int SM_CYSMICON = 50;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NID
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guid;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
        [DllImport("shell32.dll")]
        private static extern int Shell_NotifyIconGetRect(ref NID id, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr inst, string name, int type, int cx, int cy, uint load);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, int msg, int action, IntPtr filterStruct);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);

        private HwndSource _src;
        private NOTIFYICONDATA _data;
        private IntPtr _icon;
        private IntPtr _fallbackIcon;
        private bool _fallbackOwned;    // true 表示兜底 LoadIcon 返回的 HICON 归我们所有
        private TrayVisualState _visualState = TrayVisualState.Idle;
        private string _baseTip = "GameOptimizer";
        private bool _installed;
        private int _tipHash;
        private int _lastEventCode;
        private long _lastEventMs;
        private uint _wmTaskbarCreated;   // 外壳重建托盘时广播的注册消息
        private DispatcherTimer _retry;   // NIM_ADD 失败后的重试（Explorer 重启期间）
        private DispatcherTimer _probe;   // 图标存活探针（Explorer 重建后的兜底）
        private int _probeFails;
        private DispatcherTimer _promoteRetry;   // 首次安装后延迟提升 IsPromoted 的一次性定时器
        private int _retryLeft;
        // TaskbarCreated 补注册的可重入闸门：外壳广播与 NIM_ADD 之间可能再插一条广播，
        // 两条路径同时删/加会让刚加好的图标被后到的那条删掉（见 OnTaskbarCreated）。
        private bool _readding;
        private string _tip = "";

        public event Action LeftUp;
        public event Action DoubleClick;
        public event Action RightUp;

        public int AnchorX { get; private set; }
        public int AnchorY { get; private set; }
        public int LastEvent { get; private set; }
        public int EventCount { get; private set; }
        public bool Installed { get { return _installed; } }
        public TrayVisualState VisualState { get { return _visualState; } }
        public string LastError { get; private set; }
        public IntPtr Handle { get { return _src == null ? IntPtr.Zero : _src.Handle; } }

        public string BaseTip
        {
            get { return _baseTip; }
            set { _baseTip = string.IsNullOrEmpty(value) ? "GameOptimizer" : value; }
        }

        /// <summary>图标是否被提升到任务栏（而不是躲在溢出面板里）。null = 尚未判定。</summary>
        public bool? Promoted { get; private set; }

        /// <summary>图标当前所在的屏幕矩形（仅自检用；拿不到返回 false）。</summary>
        public bool TryGetRect(out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            if (!_installed || _src == null) return false;
            try
            {
                NID id = new NID();
                id.cbSize = Marshal.SizeOf(typeof(NID));
                id.hWnd = _src.Handle;
                id.uID = 1;
                RECT r;
                if (Shell_NotifyIconGetRect(ref id, out r) != 0) return false;
                x = r.L; y = r.T; w = r.R - r.L; h = r.B - r.T;
                return w > 0 && h > 0;
            }
            catch { /* 可忽略：自检用——外壳还没把图标摆好时取不到矩形，属正常态，调用方按 false 处理 */ return false; }
        }

        public bool Install(string tip)
        {
            if (_installed) { UpdateTip(tip); return true; }
            // 先看注册表里是不是已经"已显示"：能在加图标之前就把它摆到任务栏，
            // 避免"加进去 → 再提升 → 外壳重建托盘"这段空窗期（首次运行才会走到）。
            TryPromote(2);

            try
            {
                _tip = tip == null ? "" : tip;
                if (!string.IsNullOrEmpty(_tip))
                {
                    int sep = _tip.IndexOf(" · ", StringComparison.Ordinal);
                    _baseTip = sep > 0 ? _tip.Substring(0, sep) : _tip;
                }
                EnsureSink();

                // 启动/安装时预生成并缓存三态高质量 DIB HICON（Idle/Guard/Alert）
                EnsureStateIcons();
                _icon = IconForState(_visualState);
                if (_icon == IntPtr.Zero)
                {
                    LastError = "托盘图标资源加载失败（将使用系统默认图标兜底）";
                    TrayTrace.Write("icon load failed");
                }

                NOTIFYICONDATA d = new NOTIFYICONDATA();
                d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
                d.hWnd = _src.Handle;
                d.uID = 1;
                d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                d.uCallbackMessage = WM_TRAY;
                d.hIcon = _icon;
                d.szTip = Clip(_tip, 126);
                d.szInfo = "";
                d.szInfoTitle = "";
                _data = d;

                if (!AddIcon())
                {
                    // 壳可能正在重启（Explorer 崩溃/更新）：此时 NIM_ADD 一定失败。
                    // 旧代码直接返回 false 且不留任何重试，图标就永久消失了。
                    LastError = "Shell_NotifyIcon(NIM_ADD) 失败（稍后自动重试）";
                    TrayTrace.Write("NIM_ADD failed");
                    ScheduleRetry();
                    return false;
                }
                TrayTrace.Write("installed hwnd=" + _src.Handle.ToInt64() + " tip=" + _tip);

                TryPromote(0);
                StartProbe();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                TrayTrace.Write("install exception " + LastError);
                // 半成品必须自己收拾干净：窗口与图标句柄不能留给下一次 Install 泄漏。
                ReleaseSinkAndIcon();
                return false;
            }
        }

        /// <summary>
        /// 建消息窗并注册外壳广播消息。可重复调用：已建过就直接复用，
        /// 避免"上次安装失败留下的窗口被覆盖"这类句柄泄漏。
        /// </summary>
        private void EnsureSink()
        {
            if (_src != null) return;

            HwndSourceParameters prm = new HwndSourceParameters("GoTraySink");
            prm.Width = 0;
            prm.Height = 0;
            prm.WindowStyle = 0;
            prm.ParentWindow = IntPtr.Zero;
            _src = new HwndSource(prm);
            _src.AddHook(WndProc);

            // 外壳重启后会给所有顶层窗口广播 TaskbarCreated；名字是系统级的，
            // 必须用 RegisterWindowMessage 取编号（数值不固定）。取不到也没关系：
            // 下面的 NIM_ADD 重试仍然是兜底。
            _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

            // 用户是本机管理员，通常以提权方式运行。提权进程的窗口在高完整性级别，
            // 中完整性的外壳（explorer）默认被 UIPI 拦住，发不进 WM_TRAY——
            // 表现就是"图标在，点了没反应"。显式放行这一条自定义消息即可。
            //
            // Rev87：TaskbarCreated 也必须放行。它是注册消息（> WM_USER），explorer
            // 重启时的广播同样会被 UIPI 丢掉；收不到它，_installed 会一直保持 true，
            // 重注册逻辑永不执行，图标在 Explorer 重启后永久消失。
            try
            {
                bool ok = ChangeWindowMessageFilterEx(_src.Handle, WM_TRAY, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
                if (!ok) TrayTrace.Write("filter ex failed: " + Marshal.GetLastWin32Error());
                if (_wmTaskbarCreated != 0)
                {
                    bool ok2 = ChangeWindowMessageFilterEx(_src.Handle, unchecked((int)_wmTaskbarCreated), 1, IntPtr.Zero);
                    if (!ok2) TrayTrace.Write("filter ex taskbar failed: " + Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                // 放行失败只影响"提权进程被 UIPI 拦住"这一种情况，托盘本身仍可用；
                // 但"图标在、点了没反应"正是它造成的，必须同时进 Diag 与 trace。
                Diag.Fail("tray.filter", ex);
                TrayTrace.Write("filter ex threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>真正把图标交给外壳。成功时顺带设版本、记 tip 哈希。</summary>
        private bool AddIcon()
        {
            if (_src == null) return false;
            IntPtr cur = IconForState(_visualState);
            if (cur != IntPtr.Zero) _icon = cur;
            NOTIFYICONDATA d = _data;
            d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            d.hIcon = _icon;
            d.szTip = Clip(_tip, 126);
            if (!Shell_NotifyIcon(NIM_ADD, ref d))
            {
                // NIM_ADD 失败有两种原因：a) 外壳确实没注册（Explorer 刚重启）→ 必须重试；
                // b) 图标本来就在（重复广播）→ NIM_ADD 会拒绝。用 NIM_MODIFY 探一下：
                // 能改说明是 b，直接当作已安装，否则会把"图标还在"误判成"托盘不可用"。
                NOTIFYICONDATA probe = d;
                if (!Shell_NotifyIcon(NIM_MODIFY, ref probe)) return false;
            }
            _data = d;
            _installed = true;
            _tipHash = _tip.GetHashCode();

            // 版本 4 才能收到 NIN_SELECT / WM_CONTEXTMENU。设版本失败不致命：
            // 图标已经在了，退回 v0 消息码仍能点（WndProc 两种码都解）。
            NOTIFYICONDATA v = d;
            v.uTimeoutOrVersion = 4;
            // 记住版本协商结果：只有 v4 的回调里 wParam 才是"坐标"，
            // v0 下 wParam 是图标 ID(1)，解析出来是 (1,0) —— 菜单会弹到屏幕左上角。
            _v4 = Shell_NotifyIcon(NIM_SETVERSION, ref v);
            if (!_v4) TrayTrace.Write("NIM_SETVERSION failed");
            return true;
        }

        /// <summary>
        /// 图标存活探针（20s 一次）。存在的理由：TaskbarCreated 在提权实例上曾被 UIPI
        /// 拦住，Explorer 重启后图标消失，而 _installed 仍为 true —— 用户关窗后
        /// Shell 会认为"可以收进托盘"，结果窗口不可见、托盘也没有图标，只能杀进程。
        /// 放行 TaskbarCreated 是主修复；这里是兜底：连续两拍取不到图标矩形就重装。
        /// </summary>
        private void StartProbe()
        {
            try
            {
                if (_probe == null)
                {
                    _probe = new DispatcherTimer();
                    _probe.Interval = TimeSpan.FromSeconds(20);
                    _probe.Tick += delegate { ProbeIcon(); };
                }
                if (!_probe.IsEnabled) _probe.Start();
            }
            catch (Exception ex)
            {
                // 没有 Dispatcher（自检裸线程）：探针不可用，但图标本身仍然已安装。
                Diag.Fail("tray.probe.start", ex);
            }
        }

        private void ProbeIcon()
        {
            if (!_installed) return;
            int x, y, w, h;
            if (TryGetRect(out x, out y, out w, out h)) { _probeFails = 0; return; }
            _probeFails++;
            if (_probeFails < 2) return;   // 外壳忙/正在重建：连续两拍都取不到才判定丢失
            _probeFails = 0;
            TrayTrace.Write("probe: icon missing, re-adding");
            _installed = false;
            LastError = "托盘图标丢失（外壳重建），正在重新注册";
            ScheduleRetry();
        }

        /// <summary>NIM_ADD 失败后的有限重试：每 1.5s 一次，共 8 次（约 12s）。</summary>
        private void ScheduleRetry()        {
            try
            {
                if (_retry == null)
                {
                    _retry = new DispatcherTimer();
                    _retry.Interval = TimeSpan.FromMilliseconds(1500);
                    _retry.Tick += delegate { RetryTick(); };
                }
                _retryLeft = 8;
                if (!_retry.IsEnabled) _retry.Start();
            }
            catch (Exception ex)
            {
                // 没有 Dispatcher（自检里的裸线程）时无法定时重试。
                // 这不是静默失败：调用方拿到 Install=false，并且进 Diag + trace。
                Diag.Fail("tray.retry", ex);
                TrayTrace.Write("retry timer unavailable: " + ex.Message);
            }
        }

        private void RetryTick()
        {
            if (_installed) { StopRetry(); return; }
            if (AddIcon())
            {
                TrayTrace.Write("NIM_ADD retry ok");
                StopRetry();
                TryPromote(0);
                return;
            }
            _retryLeft--;
            if (_retryLeft <= 0)
            {
                StopRetry();
                LastError = "Shell_NotifyIcon(NIM_ADD) 连续重试失败，托盘图标不可用";
                TrayTrace.Write("NIM_ADD retry gave up");
            }
        }

        private void StopRetry()
        {
            try { if (_retry != null) _retry.Stop(); }
            catch (Exception ex) { /* 降级：定时器已释放时 Stop 会抛，之后不再需要它 */ Diag.Hit("tray.retry.stop"); TrayTrace.Write("retry stop: " + ex.Message); }
            try { if (_promoteRetry != null) { _promoteRetry.Stop(); _promoteRetry = null; } }
            catch (Exception ex) { Diag.Fail("tray.promoteretry.stop", ex); }
        }

        /// <summary>释放消息窗与图标句柄（安装失败、Remove 共用）。</summary>
        private void ReleaseSinkAndIcon()
        {
            if (_probe != null)
            {
                try { _probe.Stop(); }
                catch (Exception ex) { Diag.Hit("tray.probe.stop"); TrayTrace.Write("probe stop: " + ex.Message); }
                _probe = null;
            }
            StopRetry();
            if (_src != null)
            {
                try { _src.RemoveHook(WndProc); _src.Dispose(); }
                catch (Exception ex)
                {
                    // 释放消息窗失败会留下一个空窗口（进程退出时回收），不能因此中断托盘移除。
                    Diag.Fail("tray.dispose", ex);
                    TrayTrace.Write("dispose sink: " + ex.Message);
                }
                _src = null;
            }
            ReleaseIcon();
        }

        public void UpdateTip(string tip)
        {
            if (!_installed) return;
            int h = tip == null ? 0 : tip.GetHashCode();
            if (h == _tipHash) return;
            _tip = tip == null ? "" : tip;      // 记住最新文案：外壳重启后重加图标要用它，否则会退回安装时的旧提示
            _data.szTip = Clip(_tip, 126);
            _data.uFlags = NIF_TIP;
            // 只有真的改成功才更新哈希：先记哈希再发消息的话，一次失败会让同样的文案
            // 永远被"哈希相同"挡在门外，托盘提示就此停在旧值（界面变了、提示不变）。
            if (Shell_NotifyIcon(NIM_MODIFY, ref _data)) _tipHash = h;
            else TrayTrace.Write("tip modify failed");
        }

        /// <summary>气泡通知：窗口在托盘里时，这是唯一能让用户看见消息的通道。</summary>
        public bool Balloon(string title, string text, bool warn)
        {
            if (!_installed) return false;
            try
            {
                _data.szInfoTitle = Clip(title, 62);
                _data.szInfo = Clip(text, 250);
                _data.dwInfoFlags = (uint)((warn ? NIIF_WARNING : NIIF_INFO) | NIIF_NOSOUND);
                _data.uFlags = NIF_INFO;
                bool ok = Shell_NotifyIcon(NIM_MODIFY, ref _data);
                _data.uFlags = NIF_TIP;
                return ok;
            }
            catch (Exception ex)
            {
                // 气泡只是可选反馈，失败已经体现在返回值 false 上（调用方会退到 Toast）。
                // 但"窗口在托盘里时唯一的提示通道"失效必须记账。
                Diag.Fail("tray.balloon", ex);
                return false;
            }
        }

        public void Remove()
        {
            // 旧代码第一行是 if (!_installed) return; —— 一次失败的 Install 会留下
            // 消息窗和一张 HICON，Remove 却因为"没装成"提前返回，两者再也释放不掉。
            // 现在无条件走完整释放路径（幂等）。
            StopRetry();
            if (_installed)
            {
                try
                {
                    if (!Shell_NotifyIcon(NIM_DELETE, ref _data)) TrayTrace.Write("NIM_DELETE failed");
                }
                catch (Exception ex)
                {
                    // 外壳已经没了的时候 NIM_DELETE 会抛；图标本来就没了，继续释放本地资源。
                    // 但"移除后图标还在"是用户可见的残留，必须能查。
                    Diag.Fail("tray.delete", ex);
                    TrayTrace.Write("NIM_DELETE threw " + ex.GetType().Name + ": " + ex.Message);
                }
                TrayTrace.Write("removed");
            }
            _installed = false;
            ReleaseSinkAndIcon();
        }

        /// <summary>
        /// 只把图标从外壳的托盘里摘掉，不碰任何 WPF 对象（HwndSource 只能在创建线程销毁）。
        /// 给"进程被 Environment.Exit 掐断"这条路径用：NIM_DELETE 是纯 Win32 调用，
        /// 可以在任意线程执行，而这是用户唯一看得见的退出痕迹（图标残影）。
        /// 幂等：图标不在/从未安装时返回 false，不抛。
        /// </summary>
        public bool RemoveIconNow()
        {
            if (!_installed) return false;
            try
            {
                bool ok = Shell_NotifyIcon(NIM_DELETE, ref _data);
                if (!ok) TrayTrace.Write("NIM_DELETE failed (fast path)");
                if (ok) _installed = false;
                return ok;
            }
            catch (Exception ex)
            {
                // 外壳已退出（注销/Explorer 崩溃）：图标本来就不在了，这里失败不改变事实。
                Diag.Hit("tray.delete.fast");
                TrayTrace.Write("NIM_DELETE threw (fast path): " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ---- 消息解包 ------------------------------------------------------
        //  实现拆到 TrayHost.Messages.cs（partial）：单文件 800 行上限。
        //  分四段：外壳广播 → 取坐标 → 过滤噪音/去重 → 分派。


        // ====================================================================
        //  把图标从"隐藏图标"面板里请出来：Win11 把状态存在 NotifyIconSettings，
        //  IsPromoted=1 = 显示在任务栏，值缺失 = 默认藏在溢出面板。
        //  只在缺失时写一次；用户手动隐藏过（IsPromoted=0）绝不覆盖。
        // ====================================================================
        private const string NotifyRoot = @"Control Panel\NotifyIconSettings";

        /// <param name="attempt">0=加图标后首试（失败会排一次重试）1=重试 2=加图标前预检</param>
        private void TryPromote(int attempt)
        {
            string exe = SafeExePath();
            if (string.IsNullOrEmpty(exe)) return;
            try
            {
                using (Microsoft.Win32.RegistryKey root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NotifyRoot, false))
                {
                    if (root == null) return;
                    string[] keys = root.GetSubKeyNames();
                    for (int i = 0; i < keys.Length; i++)
                    {
                        using (Microsoft.Win32.RegistryKey k = root.OpenSubKey(keys[i], false))
                        {
                            if (k == null) continue;
                            string path = k.GetValue("ExecutablePath", "") as string;
                            if (string.IsNullOrEmpty(path)) continue;
                            if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;

                            object cur = k.GetValue("IsPromoted", null);
                            if (cur != null)
                            {
                                Promoted = Convert.ToInt32(cur) == 1;
                                TrayTrace.Write("promote: already set to " + Promoted);
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 读不到 NotifyIconSettings 只影响"图标能否出现在任务栏上"，
                // 而那正是"用户点不到托盘"的根因之一，必须记账。
                Diag.Fail("tray.promote.read", ex);
                TrayTrace.Write("promote read failed: " + ex.Message);
                return;
            }

            // 条目还没建出来（首次安装）：稍后再试一次
            if (attempt == 0)
            {
                DispatcherTimer t = new DispatcherTimer();
                t.Interval = TimeSpan.FromMilliseconds(1200);
                t.Tick += delegate
                {
                    t.Stop();
                    // 只清自己：若期间又排了一个新定时器，旧 Tick 把字段置空会让
                    // 真正被纳管的那个从此刻起不可停（Remove 之后仍会写注册表）。
                    if (object.ReferenceEquals(_promoteRetry, t)) _promoteRetry = null;
                    TryPromote(1);
                };
                if (_promoteRetry != null) { try { _promoteRetry.Stop(); } catch { /* 旧定时器已停：Stop 幂等，失败不影响新排程 */ } }
                _promoteRetry = t;   // 纳管：Remove/StopRetry 时一并停掉，不再于关停后写注册表
                t.Start();
                return;
            }

            try
            {
                using (Microsoft.Win32.RegistryKey root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NotifyRoot, true))
                {
                    if (root == null) return;
                    string[] keys = root.GetSubKeyNames();
                    for (int i = 0; i < keys.Length; i++)
                    {
                        using (Microsoft.Win32.RegistryKey k = root.OpenSubKey(keys[i], true))
                        {
                            if (k == null) continue;
                            string path = k.GetValue("ExecutablePath", "") as string;
                            if (string.IsNullOrEmpty(path)) continue;
                            if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                            if (k.GetValue("IsPromoted", null) != null) continue;
                            k.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                            Promoted = true;
                            TrayTrace.Write("promote: wrote IsPromoted=1");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 写不进去（权限/键被系统保护）→ 图标继续躲在溢出面板里，用户看不到。
                Diag.Fail("tray.promote.write", ex);
                TrayTrace.Write("promote write failed: " + ex.Message);
            }
        }

        private static string SafeExePath()
        {
            try
            {
                System.Reflection.Assembly a = System.Reflection.Assembly.GetEntryAssembly();
                if (a == null) return null;
                string loc = a.Location;
                if (string.IsNullOrEmpty(loc)) return null;
                return Path.GetFullPath(loc);
            }
            catch (Exception ex)
            {
                // 取不到入口程序路径 → 托盘提升逻辑直接放弃（返回 null 已表达失败）。
                Diag.Fail("tray.exepath", ex);
                return null;
            }
        }

        /// <summary>
        /// 托盘回调里的异常必须留痕。吞掉异常会把"菜单没出现"变成不可解释的现象。
        /// </summary>
        public static void LogTrayError(Exception ex)
        {
            try
            {
                TrayTrace.Write("EX " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
            }
            catch (Exception traceEx)
            {
                // trace 文件写不进去。Diag 不碰 IO，所以这里正好是它该接管的地方：
                // 回调异常的次数/最后一条消息从此有据可查，而不是只存在于一个写不进去的文件里。
                Diag.Fail("tray.callback", ex);
                Diag.Fail("tray.callback.trace", traceEx);
            }
        }

        private static string Clip(string s, int max)
        {
            return Ui.Trunc(s, max, "");
        }

        public void Dispose() { Remove(); }

    }

    /// <summary>
    /// 托盘链路留痕。默认关闭；GO_TRAY_TRACE=1 写入 %LOCALAPPDATA%\GameOptimizer\tray-trace.log。
    /// 只追加、不轮转（每个文件上限 256KB，超了截断重来）。
    /// </summary>
    public static class TrayTrace
    {
        private static readonly bool _on = Init();
        private static readonly string _path = Target();
        private static readonly object _lock = new object();

        private static bool Init()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("GO_TRAY_TRACE");
                return !string.IsNullOrEmpty(v) && v != "0";
            }
            catch { /* 可忽略：读环境变量失败 = 不开启追踪，这正是默认状态 */ return false; }
        }

        private static string Target()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameOptimizer");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "tray-trace.log");
            }
            catch { /* 可忽略：建不出日志目录 → 返回 null，Write 整体跳过（追踪默认关闭时的常态） */ return null; }
        }

        public static bool Enabled { get { return _on; } }

        public static void Write(string what)
        {
            if (!_on || _path == null) return;
            try
            {
                lock (_lock)
                {
                    FileInfo fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > 262144) File.Delete(_path);
                    StringBuilder sb = new StringBuilder();
                    sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ").Append(what).Append("\r\n");
                    File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // 追踪日志是诊断辅助：写失败绝不能影响托盘本身的行为。
                // 但"开了追踪却什么都没有"必须能解释——Diag 不碰 IO，记在这里。
                Diag.Fail("traytrace.write", ex);
            }
        }
    }
}
