// ============================================================================
//  BootReport.cs — 启动期留证：致命日志 / 就地吞掉的计数 / --timing / --diag
//
//  为什么在 Daemon/ 而不是留在 App.cs：
//    App.cs 已经贴着 800 行上限（HANDOFF 约束 7），而这一整块是"进程生命周期"的
//    观测面（启动分段、支持包、日志轮转），和退出管线属于同一件事的两端。
//    Program 只保留 ReportSwallow 这个对外签名（Shell 的节拍在调它）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Go.Daemon
{
    public static class BootReport
    {
        private static bool _timingDone;

        // ---- 留证 ----------------------------------------------------------
        /// <summary>
        /// 统一留证。Main 的 try 只包住启动，包不住消息循环本身——
        /// DispatcherTimer.Tick 里抛一个异常，进程直接消失，连 error.log 都不会写。
        /// 对一个常驻托盘的工具来说，"崩了但不知道为什么"比崩溃本身更糟。
        /// </summary>
        public static void Log(string tag, Exception ex)
        {
            try
            {
                string path0 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                try
                {
                    FileInfo fi0 = new FileInfo(path0);
                    if (fi0.Exists && fi0.Length > 524288) File.Delete(path0);   // 超 512KB 轮换，别把磁盘灌满
                }
                catch (Exception ex0)
                {
                    // 旧日志删不掉只会多占 512KB，不能因此不写新日志。
                    Diag.Hit("boot.log.rotate");
                    Diag.Fail("boot.log.rotate.detail", ex0);
                }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  [" + tag + "] ===");
                int depth = 0;
                Exception e = ex;
                while (e != null && depth < 6)
                {
                    sb.AppendLine("[" + depth + "] " + e.GetType().FullName + ": " + e.Message);
                    sb.AppendLine(e.StackTrace);
                    e = e.InnerException;
                    depth++;
                }
                File.AppendAllText(path0, sb.ToString());
            }
            catch (Exception wx)
            {
                // 连日志都写不了：Diag 不碰 IO，是最后一层。
                Diag.Fail("boot.log", wx);
            }
        }

        /// <summary>供内部的"就地吞掉但留证"调用；同一处只记前十次，避免刷盘。</summary>
        private static readonly Dictionary<string, int> _swallow = new Dictionary<string, int>();

        public static void ReportSwallow(string tag, Exception ex)
        {
            lock (_swallow)
            {
                int n;
                if (_swallow.TryGetValue(tag, out n))
                {
                    if (n >= 10) return;
                    _swallow[tag] = n + 1;
                }
                else _swallow[tag] = 1;
            }
            Log("swallow:" + tag, ex);
        }

        // ---- --timing ------------------------------------------------------
        /// <summary>写 --timing 文件（只写一次；ContentRendered 触发，3 秒兜底）。</summary>
        public static void WriteTiming(string path, long parse, long app, long core,
                                       long shell, long show, long page, long layout)
        {
            if (_timingDone) return;
            _timingDone = true;
            string text;
            try
            {
                text = string.Join("\n", Go.Boot.Marks.ToArray()) + "\n" +
                    "parse=" + parse + "ms\n" +
                    "app_created=" + app + "ms\n" +
                    "core_ready=" + core + "ms\n" +
                    "shell_construct=" + shell + "ms\n" +
                    "show=" + show + "ms\n" +
                    "first_page_built=" + page + "ms\n" +
                    "first_layout=" + layout + "ms\n";
            }
            catch (Exception ex)
            {
                // 拼装失败（Marks 被并发改）→ 本次没有计时文件；不能影响启动。
                Diag.Fail("timing.build", ex);
                return;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate (object o)
            {
                try { File.WriteAllText(path, (string)o, new UTF8Encoding(false)); }
                catch (Exception ex) { Diag.Fail("report.write", ex); }
            }, text);
        }

        /// <summary>
        /// 本进程自启动以来的单核占用（100% = 占满一个核，含所有线程）。
        /// 它和 cpu= 不是一回事：cpu= 是**系统**总占用，会被别的进程（编译、扫描、别的实例）抬高。
        /// </summary>
        public static string SelfCpu(System.Diagnostics.Process me)
        {
            try
            {
                me.Refresh();
                double up = (DateTime.Now - me.StartTime).TotalSeconds;
                if (up <= 0.5) return "-";
                return (me.TotalProcessorTime.TotalSeconds / up * 100.0).ToString("0.0");
            }
            catch (Exception ex)
            {
                // 进程信息取不到（权限/已退出）：报告里写 -，不能编一个数字。
                Diag.Hit("diag.selfcpu");
                Diag.Fail("diag.selfcpu.detail", ex);
                return "-";
            }
        }

        // ---- --diag --------------------------------------------------------
        /// <summary>
        /// 真实进程的支持包：跑 --diag-secs 秒（默认 9），然后写一份"这段时间里的内部状态"。
        /// 截图能证明画面对，不能证明"运行过程中没有东西在静默失败"——这份文件补的就是那一半。
        /// </summary>
        public static void ScheduleDiag(Go.Shell shell, Go.Core core, DaemonHost daemon,
                                        Application app, string path, long t0, int secs)
        {
            // 窗口内的本进程 CPU：自启动平均会被启动那几秒摊薄，
            // 所以单独记一份"从这一拍开始算"的增量——空闲会话到底花了多少 CPU，只有这个数说得清。
            System.Diagnostics.Process pm = System.Diagnostics.Process.GetCurrentProcess();
            DateTime w0 = DateTime.Now;
            TimeSpan cpu0;
            try { pm.Refresh(); cpu0 = pm.TotalProcessorTime; }
            catch (Exception ex) { Diag.Fail("diag.selfcpu.start", ex); cpu0 = TimeSpan.Zero; }

            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromSeconds(secs);
            t.Tick += delegate
            {
                t.Stop();
                double cpuWin = -1;
                try
                {
                    pm.Refresh();
                    double wall = (DateTime.Now - w0).TotalSeconds;
                    if (wall > 0.5) cpuWin = (pm.TotalProcessorTime - cpu0).TotalSeconds / wall * 100.0;
                }
                catch (Exception ex) { Diag.Hit("diag.selfcpu"); Diag.Fail("diag.selfcpu.window", ex); }
                // 先停表再写盘：报告落盘本身也要几毫秒，不该算进被测数据里。
                string jank = "";
                try { Jank.Stop(); jank = Jank.Report(); }
                catch (Exception jx)
                {
                    // 停顿时钟报告失败 → 支持包里这一段缺失，必须说明而不是留空。
                    jank = "report failed: " + jx.GetType().Name + "\n";
                    Diag.Fail("diag.jank", jx);
                }
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("GameOptimizer diag  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.AppendLine("elevated=" + core.Elevated
                        + "  proc=" + System.Diagnostics.Process.GetCurrentProcess().Id
                        + "  pages=" + core.Pages.Count
                        + "  window=" + secs + "s");
                    System.Diagnostics.Process me = System.Diagnostics.Process.GetCurrentProcess();
                    me.Refresh();
                    sb.AppendLine("memory.workingSet=" + (me.WorkingSet64 / 1048576.0).ToString("0.0") + " MB"
                        + "  handles=" + me.HandleCount
                        + "  threads=" + me.Threads.Count
                        + "  cpu=" + (core.Perf.Current.CpuTotal * 100).ToString("0") + "%"
                        + "  cpu.self=" + SelfCpu(me) + "%"
                        + "  cpu.window=" + (cpuWin < 0 ? "-" : cpuWin.ToString("0.0")) + "%");
                    sb.AppendLine();
                    sb.AppendLine("--- jank ---");
                    sb.Append(jank);
                    sb.AppendLine();
                    sb.AppendLine("--- bus ---");
                    Go.BusSnapshot bus = core.State.Snapshot();
                    sb.AppendLine("subscribers=" + bus.Subscribers
                        + " posted=" + bus.Posted
                        + " coalesced=" + bus.Coalesced
                        + " dispatched=" + bus.Dispatched
                        + " failed=" + bus.Failed);
                    sb.AppendLine();

                    sb.AppendLine("--- swallowed exceptions (Diag) ---");
                    sb.AppendLine("total=" + Diag.Total + "  tags=" + Diag.Tags);
                    List<string> rows = Diag.Rows();
                    if (rows.Count == 0) sb.AppendLine("(none)");
                    for (int i = 0; i < rows.Count; i++) sb.AppendLine(rows[i]);
                    sb.AppendLine();
                    sb.AppendLine("--- ledger ---");
                    sb.AppendLine("pendingRestore=" + core.Engine.PendingCount
                        + "  dir=" + Go.Ledger.CurrentDir);

                    // 报告也交给别的线程写：UI 线程此刻紧接着要 daemon.Shutdown()，
                    // 一份 5~10KB 的支持包没有理由卡在窗口线程上。
                    // 前台线程（IsBackground=false）：Main 返回后进程也会等它写完。
                    string text = sb.ToString();
                    string dir = Path.GetDirectoryName(path);
                    string fpath = path;
                    System.Threading.Thread w = new System.Threading.Thread(delegate ()
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                Directory.CreateDirectory(dir);
                            File.WriteAllText(fpath, text, new UTF8Encoding(false));
                        }
                        catch (Exception ex) { Diag.Fail("diag.write", ex); }
                    });
                    w.IsBackground = false;
                    w.Name = "go.diag.write";
                    w.Start();
                }
                catch (Exception ex)
                {
                    // 支持包拼装失败：这是诊断路径，不能让退出流程跟着断。
                    ReportSwallow("diag", ex);
                }
                try { daemon.Shutdown(); } catch (Exception ex) { Diag.Fail("daemon.shutdown.diag", ex); }
                try { app.Shutdown(); } catch (Exception ex) { Diag.Fail("app.shutdown.diag", ex); }
            };
            t.Start();
        }
    }
}
