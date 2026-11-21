// ============================================================================
//  Shell.Beat.cs — 节拍与通知（Shell 分片）
//  1Hz 采样分发 / Toast / 前台目标跟踪 / 关窗保存
//  来源：Shell.cs（1324 行单体）按职责拆分，partial 共享字段，行为零变化。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IOPath = System.IO.Path;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Go
{
    public partial class Shell : Window
    {
        // ====================================================================
        //  通知与状态
        // ====================================================================
        /// <summary>提权重启是"交接"不是"再来一份"。</summary>
        private bool _relaunching;
        private bool _uninstalling;

        public void RelaunchElevated()
        {
            if (_core.Elevated) { Toast("已具备管理员权限", true); return; }
            try
            {
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(System.Reflection.Assembly.GetEntryAssembly().Location);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                // --wait-pid 让新进程等本进程真正退出后再抢单实例锁；
                // 否则新进程会看到锁还在（created=false）而立刻退出，
                // 这一边又 Close() 进了托盘 —— 用户看到的就是"点了提权，什么都没发生"。
                psi.Arguments = "--elevated --wait-pid " + pid;
                System.Diagnostics.Process.Start(psi);
                SaveState();
                Program.ReleaseInstanceLock();
                // 必须真的退出：OnClosing 的"隐入托盘"在交接路径上会让两个进程都活着。
                _relaunching = true;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try { Application.Current.Shutdown(); }
                    catch (Exception ex) { Diag.Fail("shell.relaunch.shutdown", ex); }
                }), DispatcherPriority.Background);
            }
            catch (System.ComponentModel.Win32Exception) { Toast("已取消提权，继续以只读模式运行", false); }
            catch (Exception ex)
            {
                Diag.Fail("shell.relaunch", ex);
                Toast("提权重启失败，继续以只读模式运行", false);
            }
        }

        private string _lastToastText;
        private bool _lastToastOk;
        public string LastToastText { get { return _lastToastText ?? ""; } }
        public bool LastToastOk { get { return _lastToastOk; } }

        public void Toast(string message, bool ok)
        {
            _lastToastText = message;
            _lastToastOk = ok;
            // 窗口在托盘里时，界面 Toast 谁也看不见——改走系统气泡。
            // 否则"已挂载 / 战报 / 断电降档"这些关键反馈在常驻态全部丢失。
            if (_parked)
            {
                Go.Daemon.DaemonHost d = _core == null ? null : _core.Daemon;
                if (d != null && d.Tray != null && d.Tray.Installed)
                {
                    d.Tray.Balloon(Brand.ProductName, message, !ok);
                    return;
                }
            }
            if (!Ui.Animate) return;
            Border b = new Border();
            b.CornerRadius = new CornerRadius(T.RadiusSm);
            b.Background = T.S(0xF50E141D);
            b.BorderBrush = T.S(ok ? 0x583FE0A4 : 0x58FF5C63);
            b.BorderThickness = new Thickness(1);
            b.Margin = new Thickness(0, 6, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Right;
            b.Cursor = Cursors.Hand;

            Grid wrap = new Grid();
            wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border bar = new Border();
            bar.Background = T.S(ok ? T.Ok : T.Bad);
            bar.CornerRadius = new CornerRadius(T.RadiusSm, 0, 0, T.RadiusSm);
            wrap.Children.Add(bar);

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.Margin = new Thickness(11, 10, 14, 10);
            sp.Children.Add(Ui.Icon(ok ? Glyph.Check : Glyph.Warn, 13, ok ? T.Ok : T.Bad, 1.5));
            TextBlock t = Ui.Tx(message, T.FsSmall, T.Text, T.FCn);
            t.Margin = new Thickness(9, 0, 0, 0);
            sp.Children.Add(t);
            Grid.SetColumn(sp, 1);
            wrap.Children.Add(sp);
            b.Child = wrap;

            _toastHost.Children.Add(b);
            Ui.Enter(b, 0, 8);

            DispatcherTimer tmr = new DispatcherTimer();
            tmr.Interval = TimeSpan.FromMilliseconds(3400);
            tmr.Tick += delegate { tmr.Stop(); Dismiss(b); };
            tmr.Start();
            // 点一下立即消掉：Toast 不该强留。用 Pressable，保证"按下后拖走"不会误关。
            Pressable.On(b, delegate { tmr.Stop(); Dismiss(b); });

            while (_toastHost.Children.Count > 3) _toastHost.Children.RemoveAt(0);
        }

        private void Dismiss(Border b)
        {
            if (!_toastHost.Children.Contains(b)) return;
            DoubleAnimation a = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(220)));
            a.Completed += delegate { try { _toastHost.Children.Remove(b); } catch { /* 元素已被别处移除：Remove 无副作用，动画回调不能抛 */ } };
            b.BeginAnimation(UIElement.OpacityProperty, a);
        }

        private void TrackForeground()
        {
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == 0 || pid == (uint)_selfPid) return;
                _core.LastForeignPid = (int)pid;
                CheckFullscreen(h, (int)pid);
            }
            catch (Exception ex) { Diag.Fail("beat.foreground", ex); }
        }

        private int _clockMin = -1;

        // Jank 相位：把这一拍切成三段，卡顿才能归因——是页面的 Tick 慢，
        // 还是外壳自己的刷新慢，还是两者都不在跑（预建/导航/GC 的锅）。
        // 字符串只在换页时拼两个，节拍路径上零分配。
        private Page _jankPage;
        private string _jankTick = "tick:-";
        private string _jankIdle = "idle:-";

        private void OnBeat(object s, EventArgs e)
        {
            Snapshot snap = _core.Perf.Current;
            if (!object.ReferenceEquals(_jankPage, _cur))
            {
                _jankPage = _cur;
                string k = _cur == null ? "-" : _cur.Key;
                _jankTick = "tick:" + k;
                _jankIdle = "idle:" + k;
            }
            Jank.Phase(_jankTick);
            // 页面 Tick 是自定义代码，任何一个空引用都不该把整个进程带走。
            // 顶层虽然已经接了 DispatcherUnhandledException，但在节拍里就地拦住
            // 可以让"某一页坏了"止步于那一页，其余页面继续刷新。
            if (_cur != null)
            {
                try { _cur.Tick(snap); }
                catch (Exception ex)
                {
                    try { global::Go.Program.ReportSwallow("tick:" + _cur.Key, ex); } catch { /* 记账失败：一拍失败不能连累下一拍 */ }
                }
            }
            Jank.Phase("beat:misc");
            // 环境光随负载呼吸：合成层 Opacity，零布局；减少动效时恒定。
            if (_glow != null && Ui.Animate && !Rs.ReduceMotion)
                _glow.Opacity = 0.55 + Math.Min(1, snap.CpuTotal) * 0.45;
            if (_pulse != null) _pulse.Push(snap.CpuTotal);
            RefreshDrift();
            DateTime now = DateTime.Now;
            int hm = now.Hour * 60 + now.Minute;
            if (hm != _clockMin) { _clockMin = hm; _clock.Text = now.ToString("HH:mm"); }
            bool el = _core.Elevated;
            _statLine.Text = el ? "ADMIN" : "STANDARD";
            _statDot.Fill = T.S(el ? T.Ok : T.Warn);
            EnsureChromeHooks();
            RefreshPowerChip();
            RefreshChromeGameMode();
            SyncTrayState();
            if (_ticker != null && _ticker.Visibility == Visibility.Visible)
            {
                // 抖动只在"有页面在看"时才采样（见 PerfSampler._jhot）。
                // Rev130 修复：切离抖动热页瞬间 JitterP99Us 归零时不闪烁伪 0 µs，直接显示 —。
                string jit = (snap.JitterLive && snap.JitterP99Us > 0.5)
                    ? ((int)Math.Round(snap.JitterP99Us)).ToString()
                    : "\u2014";
                GpuSnapshot gs = _core.Gpu == null ? null : _core.Gpu.Current;
                string gpuPart = (gs != null && gs.Available)
                    ? ("   GPU " + ((int)Math.Round(gs.UtilPct)).ToString().PadLeft(2) + "%")
                    : "";
                _ticker.Text = string.Format(
                    "CPU {0,3}%{6}   MEM {1,5}/{2} GB   JIT {3,5} \u00B5s   TIMER {4} ms",
                    (int)Math.Round(snap.CpuTotal * 100),
                    (snap.MemUsedMB / 1024.0).ToString("0.0"),
                    (snap.MemTotalMB / 1024.0).ToString("0"),
                    jit,
                    snap.TimerMs.ToString("0.0"),
                    snap.ProcessCount,
                    gpuPart);
            }
            // 这一拍结束：此后到下一个 1s 之间发生的停顿都不是节拍的锅。
            Jank.Phase(_jankIdle);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 关窗 = 隐入托盘（可关闭该行为）。
            // 真正退出走 DaemonHost.Shutdown()：它先 Tray.Remove()，于是这里 Installed 已为 false，
            // 不会再被拦下来——这就是"退出"能真的退出的原因。
            Go.Daemon.DaemonHost d = _core == null ? null : _core.Daemon;
            if (!_relaunching && !_uninstalling && d != null && d.CloseToTray && d.Tray.Installed)
            {
                e.Cancel = true;
                Park();
                return;
            }
            _closing = true;
            ReleaseChromeHooks();
            if (!_uninstalling) SaveState();
            base.OnClosing(e);
        }
    }
}
