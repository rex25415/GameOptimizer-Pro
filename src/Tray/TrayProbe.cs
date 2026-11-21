// ============================================================================
//  TrayProbe.cs — 托盘链路"真鼠标"自检
//
//  为什么必须用真鼠标事件：
//  上一轮的托盘自检用 PostMessage 伪造事件码，于是"我们以为对的码"给自己判了合格，
//  而产品在真机上两次都没反应。测量工具本身必须被验证——这次直接让系统产生输入：
//  SetCursorPos + mouse_event，走完整的外壳 → 窗口消息 → WPF 分发链路。
//
//  三条硬规则（都是踩出来的）：
//   1. 合成输入绝不能在本进程 UI 线程上做。外壳会用 SendMessage 把结果送回来，
//      而 UI 线程正卡在 mouse_event 里——事件被自己堵住，实测延迟可达 2 秒。
//   2. 断言必须等待目标状态出现，不能等固定毫秒。系统调度抖动比想象大得多。
//   3. 每一步先写 journal 再动作，进程若意外死掉，日志能说明死在哪一步。
//
//  自检会移动鼠标，因此只在 --tray-probe 下运行；结束时把光标放回原处并干净摘掉图标。
// ============================================================================
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Go.Tray
{
    public static class TrayProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        private const uint LEFTDOWN = 0x0002;
        private const uint LEFTUP = 0x0004;
        private const uint RIGHTDOWN = 0x0008;
        private const uint RIGHTUP = 0x0010;

        private sealed class Step
        {
            public string Name;
            public Action Act;
            public Func<bool> Until;
            public int Ms;
            public int Retries;      // 超时后重试次数（托盘图标刚添加时有一段不可点窗口）
        }

        private static string _path;
        private static Go.Shell _shell;
        private static Go.Daemon.DaemonHost _daemon;
        private static DispatcherTimer _tmr;
        private static Step[] _steps;
        private static int _i;
        private static bool _acted;
        private static int _elapsed;
        private static int _retries;
        private static long _t0;
        private static int _x, _y;
        private static POINT _saved;
        private static bool _haveSaved;

        private static bool _menuAfterRight;
        private static int _menuRows;
        private static string _menuRect = "";
        private static bool _menuClosedAfterSecondRight;
        private static bool _windowAfterLeft;
        private static bool _windowStillAfterSecondLeft;

        public static void Run(Go.Shell shell, Go.Daemon.DaemonHost daemon, string outPath)
        {
            _shell = shell;
            _daemon = daemon;
            _path = outPath;
            try { if (GetCursorPos(out _saved)) _haveSaved = true; }
            catch { /* 降级：记不住原光标位置就跳过收尾复位，自检仍可继续 */ Diag.Hit("trayprobe.cursor.save"); }

            Note("probe.start");
            Note("tray.installed=" + daemon.Tray.Installed);
            Note("tray.promoted=" + (daemon.Tray.Promoted.HasValue ? daemon.Tray.Promoted.Value.ToString() : "unknown"));

            _steps = new Step[]
            {
                // 外壳注册托盘图标到"可点击"之间有一段不确定的延迟（实测 1.5s 时还可能没落地）。
                // 自检等足 3.5 秒，模拟真人打开应用后再去点托盘的节奏。
                new Step { Name = "settle", Ms = 3500 },
                new Step { Name = "icon.rect", Act = ReadRect },
                // 外壳在添加图标后有一段"已显示但还不接受点击"的窗口（实测可达数秒）。
                // 真人不介意多等，自检必须等；这里用重试把它测出来并记进日志。
                new Step { Name = "right-click", Act = delegate { Click(RIGHTDOWN, RIGHTUP, _x, _y); },
                           Until = delegate { return _daemon.MenuOpen; }, Ms = 900, Retries = 12 },
                new Step { Name = "menu.open", Act = RecordMenu, Ms = 0 },
                new Step { Name = "right-click-again", Act = delegate { Click(RIGHTDOWN, RIGHTUP, _x, _y); },
                           Until = delegate { return !_daemon.MenuOpen; }, Ms = 4000 },
                new Step { Name = "menu.closed", Act = RecordClosed, Ms = 0 },
                new Step { Name = "settle", Ms = 500 },
                new Step { Name = "left-click", Act = delegate { Click(LEFTDOWN, LEFTUP, _x, _y); },
                           Until = delegate { return _shell.IsVisible && !_shell.Parked; }, Ms = 900, Retries = 8 },
                new Step { Name = "window.shown", Act = RecordShown, Ms = 0 },
                new Step { Name = "left-click-again", Act = delegate { Click(LEFTDOWN, LEFTUP, _x, _y); }, Ms = 800 },
                new Step { Name = "window.still", Act = RecordStill, Ms = 0 }
            };

            _i = 0;
            _acted = false;
            _elapsed = 0;
            _retries = 0;
            _t0 = ClickClock.NowMs;

            _tmr = new DispatcherTimer();
            _tmr.Interval = TimeSpan.FromMilliseconds(50);
            _tmr.Tick += delegate { Tick(); };
            _tmr.Start();
        }

        private static void Tick()
        {
            try
            {
                if (_i >= _steps.Length) { Finish(null); return; }
                Step s = _steps[_i];

                if (!_acted)
                {
                    _acted = true;
                    _elapsed = 0;
                    if (s.Act != null)
                    {
                        try { s.Act(); }
                        catch (Exception ex)
                        {
                            // 步骤内异常会让该步必然 TIMEOUT；journal 看得见，Diag 也要看得见。
                            Diag.Fail("trayprobe.step", ex);
                            Note("EX(" + s.Name + ") " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                    return;
                }

                _elapsed += 50;
                bool done = s.Until == null ? _elapsed >= s.Ms : s.Until();
                if (done)
                {
                    Note(s.Name + " ok" + (_retries > 0 ? (" (重试 " + _retries + " 次 · " + (ClickClock.NowMs - _t0) + "ms)") : ""));
                }
                else if (_elapsed >= s.Ms && _retries < s.Retries)
                {
                    _retries++;
                    Note(s.Name + " retry " + _retries);
                    _acted = false;
                    _elapsed = 0;
                    return;
                }
                else if (_elapsed < s.Ms)
                {
                    return;
                }
                else
                {
                    Note(s.Name + " TIMEOUT");
                }

                _i++;
                _acted = false;
                _elapsed = 0;
                _retries = 0;
            }
            catch (Exception ex)
            {
                // 自检自身崩了：必须让"这次探测无效"变成一条明确记录，而不是 0/4。
                Diag.Fail("trayprobe.tick", ex);
                Note("EX " + ex.GetType().Name + ": " + ex.Message);
                Finish("exception");
            }
        }

        private static void ReadRect()
        {
            int w, h;
            bool ok = _daemon.Tray.TryGetRect(out _x, out _y, out w, out h);
            Note("icon.rect ok=" + ok + " x=" + _x + " y=" + _y + " w=" + w + " h=" + h);
            if (!ok) return;
            _x = _x + w / 2;
            _y = _y + h / 2;
            Note("icon.center=" + _x + "," + _y);
        }

        private static void RecordMenu()
        {
            _menuAfterRight = _daemon.MenuOpen;
            TrayMenu m = _daemon.Menu;
            if (m != null)
            {
                _menuRows = m.RowCount;
                _menuRect = (int)m.Left + "," + (int)m.Top + " " + (int)m.ActualWidth + "x" + (int)m.ActualHeight;
            }
            Note("menu.open=" + _menuAfterRight + " rows=" + _menuRows + " rect=" + _menuRect + " fg=" + FgInfo());
        }

        private static void RecordClosed()
        {
            _menuClosedAfterSecondRight = !_daemon.MenuOpen;
            Note("menu.closed=" + _menuClosedAfterSecondRight);
        }

        private static void RecordShown()
        {
            _windowAfterLeft = _shell.IsVisible && !_shell.Parked;
            Note("window.shown=" + _windowAfterLeft + " fg=" + FgInfo());
        }

        private static void RecordStill()
        {
            _windowStillAfterSecondLeft = _shell.IsVisible && !_shell.Parked;
            Note("window.stillShown=" + _windowStillAfterSecondLeft);
        }

        private static string FgInfo()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return pid.ToString();
            }
            catch { /* 降级：前台窗口信息只写进日志，取不到就写 ?，不影响断言 */ Diag.Hit("trayprobe.fg"); return "?"; }
        }

        /// <summary>合成输入放到线程池：UI 线程必须保持泵消息，否则会被自身阻塞。</summary>
        private static void Click(uint down, uint up, int x, int y)
        {
            int cx = x, cy = y;
            Note("input " + (down == RIGHTDOWN ? "right" : "left") + "-click -> " + cx + "," + cy);
            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                try
                {
                    SetCursorPos(cx, cy);
                    Thread.Sleep(80);
                    mouse_event(down, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(60);
                    mouse_event(up, 0, 0, 0, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    // 合成输入失败只让这一步超时（Tick 会记 TIMEOUT），不能把异常抛到线程池外。
                    // 但"鼠标没动"会让后续断言看起来像产品问题——计数才能分清。
                    Diag.Fail("trayprobe.click", ex);
                }
            });
        }

        private static void Note(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n";
            try { File.AppendAllText(_path + ".journal", line, Encoding.UTF8); }
            catch { /* 降级：journal 只是进程意外死亡时的事后证据；写不进去也要继续跑自检 */ Diag.Hit("trayprobe.journal"); }
        }

        private static void Finish(string reason)
        {
            try { _tmr.Stop(); } catch { /* 可忽略：定时器已停/已释放，Stop 再抛也无所谓，Finish 之后不再需要它 */ }
            if (_haveSaved) { try { SetCursorPos(_saved.X, _saved.Y); } catch { /* 可忽略：光标复位失败不影响自检结果，只是让用户自己挪一下鼠标 */ } }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"reason\": ").Append(Q(reason == null ? "" : reason)).Append(",\n");
            sb.Append("  \"installed\": ").Append(_daemon.Tray.Installed ? "true" : "false").Append(",\n");
            sb.Append("  \"promoted\": ").Append(_daemon.Tray.Promoted.HasValue ? (_daemon.Tray.Promoted.Value ? "true" : "false") : "null").Append(",\n");
            sb.Append("  \"icon\": [").Append(_x).Append(",").Append(_y).Append("],\n");
            sb.Append("  \"menuAfterRight\": ").Append(_menuAfterRight ? "true" : "false").Append(",\n");
            sb.Append("  \"menuRows\": ").Append(_menuRows).Append(",\n");
            sb.Append("  \"menuRect\": ").Append(Q(_menuRect)).Append(",\n");
            sb.Append("  \"menuClosedAfterSecondRight\": ").Append(_menuClosedAfterSecondRight ? "true" : "false").Append(",\n");
            sb.Append("  \"windowShownAfterLeft\": ").Append(_windowAfterLeft ? "true" : "false").Append(",\n");
            sb.Append("  \"windowStillShownAfterSecondLeft\": ").Append(_windowStillAfterSecondLeft ? "true" : "false").Append(",\n");
            sb.Append("  \"alive\": true\n");
            sb.Append("}\n");

            try { File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex)
            {
                // 结果 JSON 写失败时控制台仍会打印 pass/total；但产物缺文件必须记账。
                Diag.Fail("trayprobe.result", ex);
            }

            int pass = 0, total = 0;
            total++; if (_menuAfterRight) pass++;
            total++; if (_menuClosedAfterSecondRight) pass++;
            total++; if (_windowAfterLeft) pass++;
            total++; if (_windowStillAfterSecondLeft) pass++;
            Console.WriteLine("tray-probe " + pass + "/" + total + " -> " + _path);

            try
            {
                Application app = Application.Current;
                if (app != null) app.Shutdown();
            }
            catch { /* 可忽略：自检结束后进程要退，Shutdown 抛异常也不该阻止这一点 */ }
        }

        private static string Q(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
