// ============================================================================
//  StressTest.cs — 极限压测 / 泄漏守护（--stress-test）
//  无头驱动：快速切页 / 主题切换 / 命令面板开合 / Toast 生命周期 / 定时器离页停摆。
//  私有字段用反射读取（不改产品代码），判定对准状态机契约而非注释。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading;
using Go.Daemon;

namespace Go
{
    public static class StressTest
    {
        private static StringBuilder _log = new StringBuilder();
        private static int _pass, _fail;

        private static void Check(bool ok, string name, string detail)
        {
            if (ok) _pass++; else _fail++;
            _log.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail == null ? "" : "  ::  " + detail));
        }

        public static int Run()
        {
            _pass = 0; _fail = 0;
            _log.AppendLine("GameOptimizer MAGNUM stress probe  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Application app = new Application();
            Shell.SkipStateRestore = true;
            Ui.Animate = false;
            Core core = new Core();
            core.DetectHardware();
            Shell shell = new Shell(core);
            shell.Width = 1300; shell.Height = 800;

            try
            {
                // ---- 泄漏：每页离页后所有 DispatcherTimer 必须停 ----
                bool allStopped = true;
                string offender = "";
                foreach (Page p in core.Pages)
                {
                    shell.Go(p.Key);
                    Pump(120);
                    shell.Go("overview");
                    Pump(60);
                    foreach (DispatcherTimer t in TimersOf(p))
                    {
                        if (t.IsEnabled) { allStopped = false; offender = p.Key; }
                    }
                }
                Check(allStopped, "泄漏：所有页面离页后定时器停摆", offender);

                shell.Suspend();
                bool shellStopped = true;
                foreach (DispatcherTimer t in TimersOf(shell))
                    if (t.IsEnabled) shellStopped = false;
                Check(shellStopped && core.Perf.Dormant, "泄漏：Suspend 后外壳定时器停且采样休眠", null);
                shell.Resume();

                // ---- 压力：快速切页 100 轮，视觉树不累积 ----
                FrameworkElement root = shell.Content as FrameworkElement;
                Layout(root);
                int nodesBefore = CountNodes(root);
                for (int round = 0; round < 10; round++)
                    foreach (Page p in core.Pages) { shell.Go(p.Key); Pump(10); }
                Layout(root);
                int nodesAfter = CountNodes(root);
                Check(nodesAfter <= nodesBefore * 1.05 + 8, "压力：切页 100 轮视觉树不累积", nodesBefore + " -> " + nodesAfter);

                // ---- 压力：主题切换 20 次，当前页保持、强调色正确 ----
                string[] ids = new string[] { "ember", "violet", "mint", "ice" };
                string keep = shell.CurrentKey;
                bool themeOk = true;
                for (int i = 0; i < 20; i++)
                {
                    shell.ApplyThemeLive(ids[i % 4]);
                    Pump(20);
                    if (shell.CurrentKey != keep) themeOk = false;
                }
                Check(themeOk && T.ThemeId == "ice", "压力：主题切换 20 次当前页保持且落回冰蓝", shell.CurrentKey);

                // ---- 压力：命令面板开合 50 次 + 逐字输入，面板只建一次 ----
                shell.ShowPalette(); shell.HidePalette();   // 首开懒建面板
                int rootKidsBefore = (shell.Content as Grid).Children.Count;
                for (int i = 0; i < 50; i++) { shell.ShowPalette(); shell.HidePalette(); }
                shell.TestPalette("调度矩阵");
                int results = shell.PaletteResultCount;
                shell.HidePalette();
                int rootKidsAfter = (shell.Content as Grid).Children.Count;
                Check(results <= 12 && rootKidsAfter == rootKidsBefore, "压力：面板开合 50 次只建一次且结果<=12", results + " kids " + rootKidsBefore + "->" + rootKidsAfter);

                // ---- 泄漏：Toast 超 3 条裁剪且 3.4s 后清空 ----
                Ui.Animate = true;
                for (int i = 0; i < 10; i++) shell.Toast("压测消息 " + i, true);
                FieldInfo toastHost = Field(shell.GetType(), "_toastHost");
                StackPanel host = toastHost == null ? null : toastHost.GetValue(shell) as StackPanel;
                bool capped = host != null && host.Children.Count <= 3;
                // "到期清空"是最终一致：淡出 220ms + 生命期 3s，机器忙时会晚一点。
                // 旧实现只固定 Pump(3800) 再判一次 —— 慢一拍就红。这条断言因此常年 flaky
                // （本机 4 次里红 1 次），而它守的其实是"最终会清空"，那就该有界地等它清空。
                long until = Environment.TickCount + 8000;
                while (host != null && host.Children.Count > 0 && Environment.TickCount < until) Pump(100);
                bool emptied = host != null && host.Children.Count == 0;
                Ui.Animate = false;
                Check(capped && emptied, "泄漏：Toast 裁剪<=3 且到期清空", (host == null ? -1 : host.Children.Count) + "");

                // ---- 生命周期：每个后台线程都是 IsBackground，且 Stop 后 2 秒内真的退出 ----
                // 这不是"读代码觉得应该停"：真的起一遍、真的 Stop、真的等它死。
                // 关窗路径不 Join UI 线程（SplashNative.Close 已改为 50ms 有界等待），
                // 所以"退出快"只能靠 IsBackground + 线程自己看标志。
                PerfSampler lp = new PerfSampler();
                GpuSampler lg = new GpuSampler();
                PowerMonitor lpm = new PowerMonitor();
                GameWatcher lw = new GameWatcher(core);
                lp.Start(); lg.Start(); lpm.Start(); lw.Start();
                Thread[] ths = new Thread[]
                {
                    AsThread(lp, "_thread"), AsThread(lp, "_jit"),
                    AsThread(lg, "_th"), AsThread(lpm, "_th"), AsThread(lw, "_thread")
                };
                bool bgAll = true;
                string bgBad = "";
                for (int i = 0; i < ths.Length; i++)
                {
                    if (ths[i] == null) { bgAll = false; bgBad += "null#" + i + " "; continue; }
                    if (!ths[i].IsBackground) { bgAll = false; bgBad += ths[i].Name + " "; }
                }
                Check(bgAll, "生命周期：采样/守护线程全部 IsBackground（进程退出不被拖延）",
                      bgBad.Length == 0 ? "5 条线程" : bgBad);

                lp.Stop(); lg.Stop(); lpm.Stop(); lw.Stop();
                long stopAt = Environment.TickCount;
                bool allDead = false;
                while (Environment.TickCount - stopAt < 2000)
                {
                    bool alive = false;
                    for (int i = 0; i < ths.Length; i++) if (ths[i] != null && ths[i].IsAlive) alive = true;
                    if (!alive) { allDead = true; break; }
                    Pump(50);
                }
                int stopMs = (int)(Environment.TickCount - stopAt);
                Check(allDead, "生命周期：Stop 后 2 秒内全部线程退出（无 UI Join）",
                      stopMs + " ms · " + AliveList(ths));
            }
            catch (Exception ex)
            {
                _log.AppendLine("[FAIL] 未捕获异常: " + ex);
                _fail++;
            }

            _log.AppendLine("");
            _log.AppendLine("RESULT  pass=" + _pass + "  fail=" + _fail);
            string text = _log.ToString();
            try
            {
                string outJson = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "out", "stress-test.json");
                Directory.CreateDirectory(Path.GetDirectoryName(outJson));
                File.WriteAllText(outJson, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 报告落不了盘：stdout 仍有全文，但 --stress-test 的产物读取方是文件。
                Diag.Fail("stress.output", ex);
            }
            Console.WriteLine(text);
            app.Shutdown();
            return _fail == 0 ? 0 : 1;
        }

        // ---- 工具 ----------------------------------------------------------
        private static IEnumerable<DispatcherTimer> TimersOf(object o)
        {
            List<DispatcherTimer> r = new List<DispatcherTimer>();
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo[] fs = t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                for (int i = 0; i < fs.Length; i++)
                {
                    if (fs[i].FieldType != typeof(DispatcherTimer)) continue;
                    DispatcherTimer v = fs[i].GetValue(o) as DispatcherTimer;
                    if (v != null) r.Add(v);
                }
            }
            return r;
        }

        private static FieldInfo Field(Type t, string name)
        {
            for (Type k = t; k != null; k = k.BaseType)
            {
                FieldInfo f = k.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f;
            }
            return null;
        }

        private static Thread AsThread(object o, string field)
        {
            FieldInfo f = Field(o.GetType(), field);
            return f == null ? null : f.GetValue(o) as Thread;
        }

        private static string AliveList(Thread[] ts)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ts.Length; i++)
            {
                if (ts[i] == null || !ts[i].IsAlive) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(ts[i].Name);
            }
            return sb.Length == 0 ? "none-alive" : ("alive=" + sb);
        }

        private static int CountNodes(DependencyObject d)
        {
            int n = 1;
            int c = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < c; i++) n += CountNodes(VisualTreeHelper.GetChild(d, i));
            return n;
        }

        private static void Layout(FrameworkElement root)
        {
            root.Measure(new Size(1300, 800));
            root.Arrange(new Rect(0, 0, 1300, 800));
            root.UpdateLayout();
        }

        private static void Pump(int ms)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer t = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
                delegate { frame.Continue = false; }, Dispatcher.CurrentDispatcher);
            t.Start();
            Dispatcher.PushFrame(frame);
            t.Stop();
        }
    }
}