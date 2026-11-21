// ============================================================================
//  App.cs — 入口：五个启动阶段 + 一个退出管线
//
//  阶段（任何失败都带着"哪一阶段、为什么"交给唯一的呈现点 BootPresenter）：
//    1 ParseArgs    解析命令行 → 无头模式（自检/渲染/托盘预览）在这里分流
//    2 Instance     提权交接（--wait-pid）+ 命名互斥体仲裁
//    3 Environment  计时器/预热线程/原生闪屏/WPF Application 与三层兜底
//    4 Compose      Core → Registry → Shell → Daemon，或离屏渲染后直接退出
//    5 MessageLoop  app.Run() → 退出管线 → 放锁
//
//  为什么必须分段：旧实现是一条 319 行、嵌套 4 层、37 个 try/39 个 catch 的直筒子。
//  失败只能靠"就近弹一个框"表达（同一个启动过程最多能弹两个），error.log 里没有
//  "哪一阶段"，而装配步骤（自检、离屏渲染、真实窗口）之间只能靠 if 套 if 复用。
// ============================================================================
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IOPath = System.IO.Path;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Go.Daemon;

namespace Go
{
    /// <summary>启动分段打点（--timing 时输出）。</summary>
    public static class Boot
    {
        public static System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
        public static System.Collections.Generic.List<string> Marks = new System.Collections.Generic.List<string>();
        public static void M(string tag) { Marks.Add(tag + "=" + Sw.ElapsedMilliseconds); }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                // 最后一道网：Run 内部已经有唯一的阶段异常出口，能走到这里说明是
                // 呈现逻辑本身出了错。仍然只呈现一次，绝不两个框。
                BootPresenter.Fail(Stages.Unhandled, ex.Message, ex);
                return 1;
            }
        }

        /// <summary>供内部"就地吞掉但留证"用（Shell 的节拍在调，签名不能变）。</summary>
        public static void ReportSwallow(string tag, Exception ex) { BootReport.ReportSwallow(tag, ex); }

        /// <summary>提权重启：主动放锁，让新进程能接上（见 Shell.RelaunchElevated）。</summary>
        internal static void ReleaseInstanceLock() { InstanceArbiter.Release(); }

        /// <summary>阶段之间唯一的数据通道：显式列出装配产物，而不是一串 static 字段。</summary>
        private sealed class BootContext
        {
            public StartupOptions Opt;
            public Application App;
            public Core Core;
            public Shell Shell;
            public DaemonHost Daemon;
            public long TParse;
            public long TApp;
            public long TCore;
            public long TShell;
            public long TShow;
            public long TPage;
            public long TLayout;
        }

        private static int Run(string[] args)
        {
            BootContext ctx = new BootContext();
            string stage = Stages.Parse;
            System.Diagnostics.Stopwatch total = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                PhaseResult r = PhaseParse(ctx, args);
                if (!r.Ok) { BootPresenter.Fail(r); return r.ExitCode; }
                if (r.Stop) return r.ExitCode;

                stage = Stages.Instance;
                r = PhaseInstance(ctx);
                if (!r.Ok) { BootPresenter.Fail(r); return r.ExitCode; }
                if (r.Stop) return r.ExitCode;

                stage = Stages.Environment;
                r = PhaseEnvironment(ctx);
                if (!r.Ok) { BootPresenter.Fail(r); return r.ExitCode; }
                if (r.Stop) return r.ExitCode;

                stage = Stages.Compose;
                r = PhaseCompose(ctx);
                if (!r.Ok) { BootPresenter.Fail(r); return r.ExitCode; }
                if (r.Stop) return r.ExitCode;

                stage = Stages.Loop;
                r = PhaseLoop(ctx);
                if (!r.Ok) { BootPresenter.Fail(r); return r.ExitCode; }
                return r.ExitCode;
            }
            catch (Exception ex)
            {
                // 启动路径上唯一的异常呈现点：阶段名 + 原因，一次，就在这里。
                PhaseResult f = PhaseResult.Fail(stage, ex.Message, ex, total.ElapsedMilliseconds);
                BootPresenter.Fail(f);
                return 1;
            }
        }

        // ====================================================================
        //  阶段 1：解析命令行 + 无头模式分流
        // ====================================================================
        private static PhaseResult PhaseParse(BootContext ctx, string[] args)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            ctx.Opt = StartupOptions.Parse(args);
            StartupOptions o = ctx.Opt;
            if (o.Malformed != null)
            {
                // 缺值的旗子不会让启动失败（与旧实现一致），但"以为传了、其实没传"
                // 是排查时最费时间的一类，必须留一笔。
                Diag.Hit("boot.argv.malformed");
                BootReport.Log("boot.argv", new ArgumentException("缺值参数：" + o.Malformed));
            }

            // 离屏渲染不占单实例锁、可以和真实实例并存，但它会构造 Engine → Ledger / Power。
            // 账本损坏时 Load 会 Quarantine 并 Save（移走/覆盖用户真实 ledger.json），
            // Power 静态构造曾无条件恢复 power-restore.txt，Benchmark 会追加历史。先统一定死为只读。
            if (o.RenderDir != null || o.TrayShot != null)
                Go.Power.ReadOnlyMode = true;
            if (o.RenderDir != null)
            {
                Go.Ledger.ReadOnlyMode = true;
                Go.BenchHistory.ReadOnlyMode = true;
            }

            // 自检 / 渲染 / 托盘预览一律写沙箱账本与沙箱审计日志。
            // 理由不是洁癖：自检会真的去读写 HKCU\Software\GameOptimizer\SelfTest，
            // 并把"SELFTEST RoundTrip -> CHANGED"这类行写进用户真实的 audit-YYYYMM.log。
            // 用户打开「审计日志」第一屏看到的是测试残留，会以为软件坏了或被污染了——
            // 而这个页面存在的全部意义，是让人相信这里的每一条都是真实发生过的。
            //  注意 --render 不在其中：离屏渲染是纯读（守护宿主以 Passive 模式起，不写盘），
            //  而截图必须反映用户真实的账本读数，否则评审看到的是一个空系统。
            if (o.UsesSandboxLedger)
            {
                try { Go.Ledger.UseSandboxDir(); }
                catch (Exception ex)
                {
                    // 沙箱建不出来 → 后面的自检会写用户账本。这是"测试污染用户数据"，必须能归因。
                    Diag.Fail("ledger.sandbox", ex);
                }
            }

            if (o.SelfTestOut != null) return PhaseResult.End(Stages.Parse, SelfTest.Run(o.SelfTestOut), "selftest", sw.ElapsedMilliseconds);
            if (o.BenchOut != null) return PhaseResult.End(Stages.Parse, BackendBench.Run(o.BenchOut), "backend-bench", sw.ElapsedMilliseconds);
            if (o.DaemonTest) return PhaseResult.End(Stages.Parse, Go.Daemon.DaemonTest.Run(), "daemon-test", sw.ElapsedMilliseconds);
            if (o.AllocTest) return PhaseResult.End(Stages.Parse, AllocTest.Run(), "alloc-test", sw.ElapsedMilliseconds);
            if (o.StressTest) return PhaseResult.End(Stages.Parse, StressTest.Run(), "stress-test", sw.ElapsedMilliseconds);
            if (o.TrayShot != null)
            {
                // 托盘菜单预览需要 WPF Application（离屏渲染路径），跑完就关，不进消息循环。
                Application ta = new Application();
                Go.Tray.TrayMenu.RenderPreview(o.TrayShot);
                ta.Shutdown();
                return PhaseResult.End(Stages.Parse, 0, "tray-shot", sw.ElapsedMilliseconds);
            }
            if (o.UiTestOut != null) return PhaseResult.End(Stages.Parse, UiTest.Run(o.UiTestOut), "uitest", sw.ElapsedMilliseconds);
            return PhaseResult.Continue(Stages.Parse, sw.ElapsedMilliseconds);
        }

        // ====================================================================
        //  阶段 2：单实例仲裁
        // ====================================================================
        private static PhaseResult PhaseInstance(BootContext ctx)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            StartupOptions o = ctx.Opt;

            // 提权重启时旧进程还握着单实例锁：新进程必须等它真的退出再抢锁，
            // 否则"以管理员身份重启"会变成"新进程立刻退出、旧进程缩进托盘"。
            if (o.WaitPid > 0) InstanceArbiter.WaitForExit(o.WaitPid, 20000);

            // 离屏渲染不装托盘、不写系统、不写会话落点，因此不需要单实例锁：
            // 它必须能在正常实例运行的同时被跑（截图/评审）。
            if (o.RenderDir == null && !InstanceArbiter.Acquire())
            {
                // 第二份进程不该"悄悄消失"：它要把已在托盘里的那一份唤到前台。
                // 否则用户双击桌面图标时看到的是"什么都没发生"。
                if (!InstanceArbiter.SignalExisting()) BootPresenter.AlreadyRunning();
                return PhaseResult.End(Stages.Instance, 0, "already-running", sw.ElapsedMilliseconds);
            }
            return PhaseResult.Continue(Stages.Instance, sw.ElapsedMilliseconds);
        }

        // ====================================================================
        //  阶段 3：环境探测（计时 / 预热 / 闪屏 / WPF 与三层兜底）
        // ====================================================================
        private static PhaseResult PhaseEnvironment(BootContext ctx)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            StartupOptions o = ctx.Opt;

            Boot.Sw = System.Diagnostics.Stopwatch.StartNew();
            System.Diagnostics.Stopwatch boot = Boot.Sw;
            ctx.TParse = boot.ElapsedMilliseconds;

            // 几何预热：在后台线程把整套图标的解析 + 缩放 + 冻结先做完，
            // 让 UI 线程在 BuildRail 时直接命中缓存。只预热几何——字体不行，
            // 文本首次测量是线程级缓存，后台预热实测零收益（A2 注入探针，各测 3 次）。
            if (o.RenderDir == null)
            {
                Thread warm = new Thread(delegate ()
                {
                    try { Ui.PrewarmIcons(); }
                    catch (Exception ex) { Diag.Fail("ui.prewarm.icons", ex); }
                });
                warm.IsBackground = true;
                warm.Name = "go.iconwarm";
                warm.Priority = ThreadPriority.BelowNormal;
                warm.Start();
            }

            // 原生启动卡：约 40ms 出图，不占 WPF 的初始化时间，主窗口一出现就撤掉
            if (o.RenderDir == null) Go.SplashNative.Show();
            Boot.M("pre_app");
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            ctx.App = app;

            // ---- 顶层兜底（Rev78）------------------------------------------
            // 审计实测：DispatcherTimer.Tick 抛异常会导致 IsTerminating=True，进程终止，
            // 且因为没有任何 handler，什么证据都不留。三条线程各接一层：
            // UI 线程留活口（性能工具挂掉比降级更糟），域级与任务级只留证。
            app.DispatcherUnhandledException += delegate (object s, DispatcherUnhandledExceptionEventArgs e)
            {
                BootReport.Log("ui", e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                BootReport.Log("domain", e.ExceptionObject as Exception);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += delegate (object s, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
            {
                BootReport.Log("task", e.Exception);
                e.SetObserved();
            };
            Boot.M("wpf_init");

            // 启动页已移除：实测它自己吃掉 949ms（构造，WPF 字体栈冷初始化）
            // + 1144ms（创建第一个窗口），而主窗口的 Show 在它之后只要 162ms。
            // 也就是说这块"品牌画面"净亏约 1.7s，而用户只看到 0.9s。
            // 品牌呈现要重做的话，必须是不依赖 WPF 的原生窗口。
            ctx.TApp = boot.ElapsedMilliseconds;
            return PhaseResult.Continue(Stages.Environment, sw.ElapsedMilliseconds);
        }

        // ====================================================================
        //  阶段 4：装配服务（或离屏渲染后直接退出）
        // ====================================================================
        private static PhaseResult PhaseCompose(BootContext ctx)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            StartupOptions o = ctx.Opt;
            System.Diagnostics.Stopwatch boot = Boot.Sw;
            Application app = ctx.App;

            Core core = new Core();
            if (o.RenderDir != null) core.HeadlessRender = true;
            core.DetectHardware();
            Registry.Load(core);
            ctx.Core = core;
            ctx.TCore = boot.ElapsedMilliseconds;

            if (o.RenderDir != null) Shell.SkipStateRestore = true;
            if (o.Theme != null) T.ApplyTheme(o.Theme);
            Shell shell = new Shell(core);
            app.MainWindow = shell;
            ctx.Shell = shell;
            ctx.TShell = boot.ElapsedMilliseconds;

            if (o.RenderDir != null)
            {
                Directory.CreateDirectory(o.RenderDir);

                // 截图专用：给守护面板一个"运行中"的确定状态，不装托盘、不做巡检、不写系统。
                if (o.FakeGame)
                {
                    GameProfile gp = new GameProfile();
                    gp.Id = "render-demo";
                    gp.Name = "Counter-Strike 2";
                    gp.Exe = @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe";
                    gp.PinCcd0 = true; gp.HighGpu = true; gp.Timer = true;
                    core.Profiles.AddNoSave(gp);
                    Go.Daemon.DaemonHost dh = new Go.Daemon.DaemonHost(core);
                    dh.Passive = true;
                    core.Daemon = dh;
                    dh.Watcher.ForceAttach(gp, 48120);
                }
                if (o.SizeArg != null)
                {
                    string[] parts = o.SizeArg.Split('x');
                    double w, h;
                    if (parts.Length == 2 && double.TryParse(parts[0], out w) && double.TryParse(parts[1], out h))
                    {
                        shell.Width = w;
                        shell.Height = h;
                        shell.Relayout();
                    }
                }
                Render(shell, core, o.RenderDir, o.OnlyPage, o.Palette);
                // 采样器与 GPU 采样线程是前台线程，Main 返回后进程并不会结束。
                // 旧版本因此每渲染一次就泄漏一个进程，还会锁住 out\GameOptimizer.exe，
                // 让下一次编译报 CS0016——一个纯粹的假故障。
                try { Console.Out.Flush(); }
                catch (Exception ex) { Diag.Fail("render.flush", ex); }
                Environment.Exit(0);
                return PhaseResult.End(Stages.Compose, 0, "render", sw.ElapsedMilliseconds);
            }

            // 先把内容准备好再 Show：旧顺序是先亮一个空壳，再触发首屏构建与第二次整窗布局，
            // 用户能看到"白框一帧"，而且第一个 Tick 的 CPU/内存还是 0。
            long tShow0 = boot.ElapsedMilliseconds;
            Go.Daemon.DaemonHost daemon = new Go.Daemon.DaemonHost(core);
            core.Daemon = daemon;
            ctx.Daemon = daemon;
            Boot.M("daemon_ctor");
            daemon.LoadAutostart();
            Boot.M("autostart");
            daemon.StartHidden = o.Tray;

            core.Perf.Start();
            Boot.M("perf_start");
            core.Gpu.Start();
            Boot.M("gpu_start");
            // 首帧前不热跑抖动探针：RestorePage 会点亮 JitterHot，若立刻 Highest+1ms，
            // 整个 show 段都在和渲染抢核。首帧上屏后再放行（离屏渲染不走这条，保持默认 true）。
            core.Perf.BootDone = false;
            shell.RestorePage();
            Boot.M("restore_page");
            shell.RefreshDaemonUi();
            Boot.M("refresh_daemon");
            Boot.M("content_ready");

            // 首帧到底落在哪一刻：Show() 返回不等于像素已经上屏。
            // ContentRendered 是 WPF 里"第一帧已经画完"的官方时点，留一个标记，
            // 让 --timing 的 show 段能拆成「调 Show 花多久」和「首帧还要多久」。
            shell.ContentRendered += delegate { core.Perf.BootDone = true; Go.SplashNative.Close(); Boot.M("first_frame"); };

            shell.Show();
            // 启动卡看门狗：--tray 路径会在首帧之前 Park 窗口，ContentRendered 可能
            // 永远不触发，SplashNative.Close() 与 BootDone 就一起丢了 —— 屏幕正中
            // 停一张 396×132 的置顶卡片，抖动探针也永远停在冷态。2.5s 后无条件收尾。
            DispatcherTimer splashGuard = new DispatcherTimer();
            splashGuard.Interval = TimeSpan.FromMilliseconds(2500);
            splashGuard.Tick += delegate
            {
                splashGuard.Stop();
                try { Go.SplashNative.Close(); }
                catch (Exception ex) { Diag.Fail("splash.guard", ex); }
                core.Perf.BootDone = true;
            };
            splashGuard.Start();
            // 某些启动上下文会把 SW_SHOWMINIMIZED 传下来（从隐藏的父进程启动、计划任务、
            // 登录自启），于是"双击图标/开机自启"看到的只是一个缩在任务栏里的窗口 ——
            // 用户主动启动就是要看到窗口，除 --tray 之外一律强制回到 Normal。
            if (!o.Tray && shell.WindowState == WindowState.Minimized) shell.WindowState = WindowState.Normal;
            // 单实例"请露面"监听（建 ACL + 起线程）挪到首帧后：不压在 Show→app.Run 的关键路径上。
            shell.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                InstanceArbiter.ListenForShow(shell.Dispatcher, delegate { shell.Unpark(); });
            }));
            ctx.TShow = boot.ElapsedMilliseconds;
            Boot.M("shown");

            // 停顿时钟只在诊断会话里开：它每 15ms 唤醒一次 UI 线程，
            // 常驻开着等于用一个尺子改变被测物的功耗与调度。离屏渲染/自检没有消息循环，
            // 也就不该开（那边 Begin 会返回 false，但根本不必走到那儿）。
            if (o.DiagOut != null)
            {
                Jank.Phase("boot");
                Jank.Begin();
            }

            // daemon.Start() 在 UI 线程上要 37~53ms（账本恢复 + 托盘注册 + 扫描器起转），
            // 而这段时间正好压在窗口刚上屏、用户第一眼看到它的那一刻：
            // --diag 的 --- jank --- 段量到的第一处停顿就在这里。
            // 常驻守护晚一帧起转没有任何行为差异，所以常规启动改成空闲投递；
            // 两条路必须保持同步：--tray 要靠 d.Tray.Installed 判断能不能收进托盘，
            // --tray-probe 进门第一件事就是读 Tray.Installed 并驱动托盘菜单。
            if (o.Tray || o.ProbePath != null)
            {
                daemon.Start();
                shell.RefreshDaemonUi();
                Boot.M("daemon_start");
            }
            else
            {
                Go.Daemon.DaemonHost dd = daemon;
                Shell sh = shell;
                shell.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    try { dd.Start(); sh.RefreshDaemonUi(); }
                    catch (Exception ex) { ReportSwallow("daemon.start", ex); }
                    Boot.M("daemon_start");
                }));
            }

            // 退出只有一条实现：ShutdownOnce → DaemonHost.Shutdown() → ShutdownPipeline。
            // 关窗/WM_CLOSE/托盘菜单/系统注销/--diag 到期全部汇到这里，重复调用是幂等的。
            app.Exit += delegate { ShutdownOnce(ctx, "app.Exit"); };
            app.SessionEnding += delegate (object s, SessionEndingCancelEventArgs e) { ShutdownOnce(ctx, "session-ending"); };
            // 进程被 Environment.Exit 掐断时 App.Exit 不会触发：这里兜的只是托盘图标，
            // 因为 NIM_DELETE 是纯 Win32、可在任意线程调用；完整管线必须留在 UI 线程。
            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                try { if (ctx.Daemon != null) ctx.Daemon.RemoveTrayIconNow(); }
                catch (Exception ex) { Diag.Fail("processexit.tray", ex); }
            };

            if (o.Tray) shell.Park();
            if (!string.IsNullOrEmpty(o.ProbePath)) Go.Tray.TrayProbe.Run(shell, daemon, o.ProbePath);
            ctx.TPage = boot.ElapsedMilliseconds;
            shell.UpdateLayout();
            Boot.M("update_layout");
            ctx.TLayout = boot.ElapsedMilliseconds;

            // 这里原本有一处「空闲预建」：窗口显示后用 ApplicationIdle 优先级，
            // 每 1.1 秒把一页的视觉树先建出来，好让首访没有 50~110ms 空白。
            //
            // Rev81 把它删了。用自研的 UI 停顿时钟（Kernel/Jank.cs）量 30 秒真实进程，
            // 结论很干净：整个会话里成规模的 UI 停顿**全部**来自它——
            // 每个 11~102ms、一秒一次、持续十几秒；停顿时钟的每一条都能和
            // 「预建那一页自己的构建耗时」逐条对上（102ms↔tuning 101ms、90ms↔processes 87ms…）。
            //
            // 也就是说：那 50~110ms 从来没有消失，只是从「用户点开那一页时」搬到了
            // 「用户正在看首屏的头十几秒里」——搬到了一个更贵的地方。
            // 首访的构建成本只能靠把页面本身建得更快来降，不能靠挪时间。
            Jank.Phase("idle:ready");
            Boot.M("prewarm_begin");

            // --diag：让真实进程跑一会儿，把"这一会儿里发生了什么"落成一份支持包。
            // 它回答的是截图答不了的问题：预建有没有失败、总线投递了几次、有没有异常被吞。
            if (o.DiagOut != null) BootReport.ScheduleDiag(shell, core, daemon, app, o.DiagOut, tShow0, o.DiagSecs);

            if (o.TimingOut != null)
            {
                // --timing 等首帧再落盘：Window.Show() 返回 ≠ 像素已经上屏，
                // first_frame 这一行只有 ContentRendered 之后才有值。
                // 这也是"show（Show 到首帧）"能被拆开的原因。
                // 3 秒兜底：窗口万一没走 ContentRendered，文件也不能丢。
                // 落盘本身在线程池上：一份 1KB 的计时文件没理由卡在窗口线程。
                string tpath = o.TimingOut;
                long m1 = ctx.TParse, m2 = ctx.TApp, m3 = ctx.TCore, m4 = ctx.TShell;
                long m5 = ctx.TShow, m6 = ctx.TPage, m7 = ctx.TLayout;
                Action writeT = delegate { BootReport.WriteTiming(tpath, m1, m2, m3, m4, m5, m6, m7); };
                shell.ContentRendered += delegate { writeT(); };
                DispatcherTimer fb = new DispatcherTimer();
                fb.Interval = TimeSpan.FromSeconds(3);
                fb.Tick += delegate { fb.Stop(); writeT(); };
                fb.Start();
            }

            return PhaseResult.Continue(Stages.Compose, sw.ElapsedMilliseconds);
        }

        // ====================================================================
        //  阶段 5：消息循环
        // ====================================================================
        private static PhaseResult PhaseLoop(BootContext ctx)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int code = ctx.App.Run();
            ctx.Core.Perf.Stop();
            // GPU 采样线程持有 PDH 查询句柄；正常退出路径上必须停它。
            // 旧版本全仓没有 Gpu.Stop 调用点，句柄只能等进程结束被内核回收。
            try { ctx.Core.Gpu.Stop(); }
            catch (Exception ex) { Diag.Fail("gpu.stop", ex); }
            // app.Run 返回时 Exit 事件已经跑过；这里再调一次是幂等的兜底——
            // 万一 Exit 没被接通（比如窗口在 Show 之前就被拆了），服务也不能留着。
            ShutdownOnce(ctx, "loop.return");
            InstanceArbiter.Release();
            return PhaseResult.End(Stages.Loop, code, null, sw.ElapsedMilliseconds);
        }

        /// <summary>
        /// 唯一的退出入口：无论谁触发（关窗/Application.Exit/托盘菜单/系统注销/--diag），
        /// 都走同一条 ShutdownPipeline。第二次调用拿到同一份报告，不会重复拆服务。
        /// </summary>
        private static void ShutdownOnce(BootContext ctx, string trigger)
        {
            if (ctx == null || ctx.Daemon == null) return;
            ShutdownReport rep;
            try { rep = ctx.Daemon.Shutdown(trigger); }
            catch (Exception ex)
            {
                // 管线内部每步已经各自兜底；能走到这里说明是管线自身坏了。
                // 即便如此也把图标摘掉：托盘残影是用户唯一每天都能看见的退出 bug。
                Diag.Fail("shutdown." + trigger, ex);
                try { ctx.Daemon.RemoveTrayIconNow(); }
                catch (Exception tex) { Diag.Fail("shutdown.tray.fallback", tex); }
                return;
            }
            if (rep != null && (rep.Timeouts > 0 || rep.Failures > 0 || rep.TotalMs > 1000))
            {
                // 退出慢/有步骤被放弃：必须在支持包与审计里看得见，否则"关得慢"永远无解。
                Diag.Hit("shutdown.slow");
                BootReport.Log("shutdown", new InvalidOperationException(rep.Summary()));
            }
        }

        // ====================================================================
        //  离屏渲染
        // ====================================================================
        private static void Render(Shell shell, Core core, string dir, string onlyPage, bool withPalette)
        {
            const double scale = 1.4;
            Ui.Animate = false;
            core.Perf.Start();
            // 截图也要有 GPU 数据：否则图形页在离屏渲染里永远显示"PDH 不可用"，
            // 而真实运行时它是有数的 —— 截图和产品不是同一个东西，评审就失去意义。
            try { core.Gpu.Start(); }
            catch (Exception ex) { Diag.Fail("gpu.start", ex); }

            double w = shell.Width;
            double h = shell.Height;
            FrameworkElement root = shell.Content as FrameworkElement;
            if (root == null) throw new InvalidOperationException("shell.Content is not a FrameworkElement");
            root.Width = w;
            root.Height = h;

            int i = 0;
            bool first = true;
            foreach (Page p in core.Pages)
            {
                if (onlyPage != null && p.Key != onlyPage) continue;
                try
                {
                    shell.Go(p.Key);
                    if (first) { Pump(3000); first = false; }
                    else if (p.Key == "bench") Pump(9400);
                    else if (p.Key == "processes") Pump(2700);
                    else if (p.Key == "graphics") Pump(1100);
                    else Pump(140);

                    Layout(root, w, h);
                    int pw = (int)Math.Round(w * scale);
                    int ph = (int)Math.Round(h * scale);
                    RenderTargetBitmap rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    rtb.Render(root);
                    PngBitmapEncoder enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    string path = IOPath.Combine(dir, string.Format("{0}-{1}.png", p.Idx, p.Key));
                    using (FileStream fs = File.Create(path)) enc.Save(fs);
                    Console.WriteLine("shot " + path);
                    if (p.Key == "overview")
                    {
                        try
                        {
                            File.WriteAllText(IOPath.Combine(dir, "_brand-debug.txt"), shell.BrandDebug + "\n", new UTF8Encoding(false));
                        }
                        catch (Exception ex)
                        {
                            // 调试旁证文件写不出来不影响渲染结果本身。
                            Diag.Fail("render.brand.debug", ex);
                        }
                    }
                    i++;
                }
                catch (Exception ex)
                {
                    string msg = "PAGE " + p.Key + " FAILED: " + ex.GetType().Name + " / " + ex.Message;
                    Console.WriteLine(msg);
                    try { File.AppendAllText(IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "render-errors.log"), msg + "\n" + ex.StackTrace + "\n", new UTF8Encoding(false)); }
                    catch (Exception wex)
                    {
                        // 错误已经打到 stdout：落盘只是给事后查证，写不了不能覆盖原异常。
                        Diag.Fail("render.error.log", wex);
                    }
                }
            }
            if (withPalette)
            {
                shell.Go("overview");
                Pump(200);
                shell.ShowPalette();
                Pump(260);
                Layout(root, w, h);
                int pw2 = (int)Math.Round(w * scale);
                int ph2 = (int)Math.Round(h * scale);
                RenderTargetBitmap rtb2 = new RenderTargetBitmap(pw2, ph2, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                rtb2.Render(root);
                PngBitmapEncoder enc2 = new PngBitmapEncoder();
                enc2.Frames.Add(BitmapFrame.Create(rtb2));
                try { File.WriteAllText(IOPath.Combine(dir, "_palette-debug.txt"), shell.PaletteDebug + "\n", new UTF8Encoding(false)); }
                catch (Exception ex) { Diag.Fail("render.palette.debug", ex); }
                string p2 = IOPath.Combine(dir, "_palette.png");
                using (FileStream fs2 = File.Create(p2)) enc2.Save(fs2);
                Console.WriteLine("shot " + p2);
            }

            core.Perf.Stop();
            try { core.Gpu.Stop(); }
            catch (Exception ex) { Diag.Fail("gpu.stop", ex); }
            Console.WriteLine("rendered " + i + " pages");
            // 离屏渲染是无守护/无托盘/无窗口的安全测量路径：把启动分段（含 BuildRail 细分）
            // 打到控制台，供性能回归对表，不必起一个会清掉用户会话落点的真实窗口实例。
            Console.WriteLine("--- boot marks (ms) ---");
            for (int m = 0; m < Boot.Marks.Count; m++) Console.WriteLine(Boot.Marks[m]);
        }

        private static void Layout(FrameworkElement root, double w, double h)
        {
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        }

        /// <summary>在不运行 Application.Run 的情况下推进调度器，让计时器与动画结算。</summary>
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
