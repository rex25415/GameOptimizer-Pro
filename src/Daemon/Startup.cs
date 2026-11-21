// ============================================================================
//  Startup.cs — 启动阶段的三个基础件：选项、阶段结果、单实例仲裁
//
//  为什么把启动拆成"阶段 + 结果对象"：
//    旧 App.Run() 是一条 319 行、嵌套 4 层、37 个 try/39 个 catch 的直筒子。
//    任何一处失败都只能靠"就近弹一个框"表达，于是：
//      · 同一个进程生命周期里用户可能看到两个框（一个来自 catch，一个来自 Main）；
//      · 失败原因里没有"哪一阶段"，error.log 只有栈没有上下文；
//      · 想在阶段之间复用（自检拿同一套装配、离屏渲染跳过消息循环）只能靠 if 套 if。
//    现在每个阶段返回 PhaseResult：成功=继续，失败=带着 stage/reason 交给唯一的呈现点，
//    提前结束（自检、第二实例、离屏渲染）=exit code + 说明。
//
//  为什么单实例仲裁单独成类：
//    互斥体必须挂在静态字段上（被 GC 回收 = 句柄关闭 = 锁消失 = 允许第二份启动），
//    这件事只能是类级静态状态；把它从 Program 里搬出来是为了让"锁的语义"
//    （谁算已经在跑、打不开时怎么办）集中在一处可读、可测。
// ============================================================================
using System;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace Go.Daemon
{
    /// <summary>
    /// 命令行解析结果。字段与旧 Run() 的 15 个局部变量一一对应，行为零变化：
    /// 缺值的旗子按"没给"处理（而不是启动失败），未知旗子忽略。
    /// </summary>
    public sealed class StartupOptions
    {
        public string RenderDir;
        public string OnlyPage;
        public string SelfTestOut;
        public string BenchOut;
        public string SizeArg;
        public string TrayShot;
        public string ProbePath;
        public string Theme;
        public string DiagOut;
        public string UiTestOut;
        public string TimingOut;
        public bool Palette;
        public bool Tray;
        public bool DaemonTest;
        public bool FakeGame;
        public bool AllocTest;
        public bool StressTest;
        public bool Elevated;
        public int DiagSecs;
        /// <summary>提权重启交接：等这个 pid 退出再抢单实例锁。</summary>
        public int WaitPid;
        /// <summary>缺值旗子的清单（空 = 没有）。只用于留证，不改变解析结果。</summary>
        public string Malformed;

        public StartupOptions() { DiagSecs = 9; }

        /// <summary>自检/渲染/托盘预览：写沙箱账本，不碰用户数据（见 HANDOFF 约束 10）。</summary>
        public bool UsesSandboxLedger
        {
            get
            {
                return SelfTestOut != null || BenchOut != null || DaemonTest || AllocTest || StressTest
                    || TrayShot != null || ProbePath != null || UiTestOut != null;
            }
        }

        /// <summary>无消息循环的模式：跑完就返回退出码。</summary>
        public bool Headless
        {
            get
            {
                return SelfTestOut != null || BenchOut != null || DaemonTest || AllocTest || StressTest
                    || TrayShot != null || UiTestOut != null;
            }
        }

        public static StartupOptions Parse(string[] args)
        {
            StartupOptions o = new StartupOptions();
            if (args == null) return o;
            StringBuilder bad = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                // 需要取值的旗子：缺值时不消费下一个参数，也不报错（与旧实现一致）。
                if (a == "--render") { if (Take(args, i, ref o.RenderDir, a, ref bad)) i++; }
                else if (a == "--page") { if (Take(args, i, ref o.OnlyPage, a, ref bad)) i++; }
                else if (a == "--selftest") { if (Take(args, i, ref o.SelfTestOut, a, ref bad)) i++; }
                else if (a == "--backend-bench") { if (Take(args, i, ref o.BenchOut, a, ref bad)) i++; }
                else if (a == "--size") { if (Take(args, i, ref o.SizeArg, a, ref bad)) i++; }
                else if (a == "--palette") o.Palette = true;
                else if (a == "--tray") o.Tray = true;
                else if (a == "--daemon-test") o.DaemonTest = true;
                else if (a == "--fake-game") o.FakeGame = true;
                else if (a == "--alloc-test") o.AllocTest = true;
                else if (a == "--stress-test") o.StressTest = true;
                else if (a == "--tray-shot") { if (Take(args, i, ref o.TrayShot, a, ref bad)) i++; }
                else if (a == "--tray-probe") { if (Take(args, i, ref o.ProbePath, a, ref bad)) i++; }
                else if (a == "--theme") { if (Take(args, i, ref o.Theme, a, ref bad)) i++; }
                else if (a == "--diag") { if (Take(args, i, ref o.DiagOut, a, ref bad)) i++; }
                else if (a == "--uitest") { if (Take(args, i, ref o.UiTestOut, a, ref bad)) i++; }
                else if (a == "--timing") { if (Take(args, i, ref o.TimingOut, a, ref bad)) i++; }
                else if (a == "--elevated") o.Elevated = true;
                else if (a == "--diag-secs")
                {
                    int secs;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out secs) && secs > 0 && secs <= 600)
                    {
                        o.DiagSecs = secs;
                        i++;
                    }
                    else if (i + 1 < args.Length) i++;   // 值给了但不合法：吞掉，保持默认 9 秒
                }
                else if (a == "--wait-pid")
                {
                    int pid;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out pid) && pid > 0) o.WaitPid = pid;
                    if (i + 1 < args.Length) i++;
                }
            }
            if (bad != null) o.Malformed = bad.ToString();
            return o;
        }

        private static bool Take(string[] args, int i, ref string into, string flag, ref StringBuilder bad)
        {
            if (i + 1 >= args.Length)
            {
                // 缺值：整条旗子作废。旧实现同样忽略，但那时没有任何留证——
                // 一个 --diag 打错成 --diag 且没给路径的调用，看起来就像"应用没诊断功能"。
                if (bad == null) bad = new StringBuilder();
                else bad.Append(',');
                bad.Append(flag);
                return false;
            }
            into = args[i + 1];
            return true;
        }
    }

    /// <summary>一个启动阶段的结果。Fail 必须带 stage 与 reason，交给 BootPresenter 统一呈现。</summary>
    public sealed class PhaseResult
    {
        public string Stage;
        public bool Ok;
        /// <summary>true = 正常提前结束（自检跑完、第二实例、离屏渲染），不是失败。</summary>
        public bool Stop;
        public int ExitCode;
        public string Reason;
        public Exception Error;
        public long Ms;

        public static PhaseResult Continue(string stage, long ms)
        {
            PhaseResult r = new PhaseResult();
            r.Stage = stage;
            r.Ok = true;
            r.Ms = ms;
            return r;
        }

        public static PhaseResult End(string stage, int code, string reason, long ms)
        {
            PhaseResult r = new PhaseResult();
            r.Stage = stage;
            r.Ok = true;
            r.Stop = true;
            r.ExitCode = code;
            r.Reason = reason;
            r.Ms = ms;
            return r;
        }

        public static PhaseResult Fail(string stage, string reason, Exception ex, long ms)
        {
            PhaseResult r = new PhaseResult();
            r.Stage = stage;
            r.Ok = false;
            r.Reason = reason;
            r.Error = ex;
            r.Ms = ms;
            r.ExitCode = 1;
            return r;
        }
    }

    /// <summary>阶段名：唯一呈现点的输出与 error.log 的标签都用它，便于按阶段统计失败。</summary>
    public static class Stages
    {
        public const string Parse = "parse-args";
        public const string Instance = "instance";
        public const string Environment = "environment";
        public const string Compose = "compose";
        public const string Loop = "message-loop";
        public const string Unhandled = "unhandled";
    }

    // ========================================================================
    //  单实例仲裁
    // ========================================================================
    /// <summary>
    /// 命名互斥体 = "这个用户会话里只允许一份 GameOptimizer 动系统写入"。
    /// 它保护的不是界面，而是账本：两份进程同时写/回滚同一批注册表项，谁也不知道对方改了什么。
    /// </summary>
    public static class InstanceArbiter
    {
        /// <summary>必须是静态字段：局部变量会被 GC 回收，句柄一关命名互斥体就消失了。</summary>
        private static Mutex _guard;
        private static EventWaitHandle _show;
        private static Thread _listener;
        private static volatile bool _listenStop;

        /// <summary>裸名字（不带命名空间前缀）。GO_INSTANCE_KEY 是显式多开开关（自检/并行对比用）。</summary>
        private static string BareName()
        {
            string suffix = Environment.GetEnvironmentVariable("GO_INSTANCE_KEY");
            return "GameOptimizerProRev60" + (string.IsNullOrEmpty(suffix) ? "" : ("_" + suffix));
        }

        /// <summary>互斥体名（全局命名空间）。</summary>
        /// <remarks>
        /// Global\ 前缀的意义：跨会话（快速用户切换/远程终端）也只允许一份实例动系统写入。
        /// 代价是标准用户没有 SeCreateGlobalPrivilege 时建不出对象——这条路径在 Acquire 里
        /// 单独处理（放行并记 instance.noprivilege），不会被误判成"已经有实例在跑"。
        /// </remarks>
        public static string Key()
        {
            return @"Global\" + BareName();
        }

        /// <summary>返回 true = 本进程拿到锁（或无锁也要放行）；false = 已经有一份在跑。</summary>
        public static bool Acquire()
        {
            string name = Key();
            try
            {
                bool created = false;
                _guard = new Mutex(true, name, out created);
                if (!created) { _guard.Dispose(); _guard = null; return false; }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 这一个异常对应两种完全不同的处境，不能用同一个结论：
                //   a) 另一份实例以更高完整性级别运行，同名对象不可打开 → 必须当作"已在运行"；
                //   b) 标准用户没有 SeCreateGlobalPrivilege，Global\ 下根本建不出对象 → 必须放行。
                //      （manifest 是 asInvoker，"STANDARD" 是产品里真实存在的工作状态；
                //        在 b 上返回 false 会让标准用户双击后什么都不发生，这比双开更糟。）
                // 区分靠 OpenExisting：对象存在 = a；不存在 = b。
                Diag.Hit("instance.denied");
                Mutex adopted;
                try
                {
                    if (Mutex.TryOpenExisting(name, out adopted))
                    {
                        bool got;
                        try { got = adopted.WaitOne(0); }
                        catch (AbandonedMutexException) { got = true; Diag.Hit("instance.abandoned"); }
                        if (got)
                        {
                            // 上一个实例已退出、锁还挂在对象上：接管，而不是去开第二份。
                            _guard = adopted;
                            return true;
                        }
                        adopted.Dispose();
                        return false;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // 对象存在但打不开：另一份更高权限实例持有。
                    return false;
                }
                catch (Exception ex)
                {
                    // 探测本身失败：保守地按"有人在跑"处理（宁可少开一份，也不两份同时写账本）。
                    Diag.Fail("instance.probe", ex);
                    return false;
                }
                // 见 b：没有创建 Global\ 对象的权限。退回**会话局部**命名空间再仲裁一次：
                // 同一登录会话里仍然只允许一份（这是"绝不重复"真正要保住的场景）；
                // 跨会话（快速用户切换）才允许并存——那种情况下两份进程操作的本来也是同一个用户配置。
                Diag.Hit("instance.noprivilege");
                try
                {
                    bool createdLocal = false;
                    _guard = new Mutex(true, @"Local\" + BareName(), out createdLocal);
                    if (createdLocal) return true;
                    _guard.Dispose();
                    _guard = null;
                    return false;
                }
                catch (Exception lex)
                {
                    // 连会话局部对象都建不出来：环境已经不正常，放行让用户至少能看到界面。
                    Diag.Fail("instance.localfallback", lex);
                    return true;
                }
            }
            catch (Exception ex)
            {
                // 连内核对象都建不出来（资源耗尽/被沙箱拦）：先探测同名对象是否存在。
                // 存在 = 别人在跑；不存在 = 环境坏了，此时必须放行，否则用户双击毫无反应。
                Diag.Fail("instance.acquire", ex);
                bool exists = false;
                try
                {
                    using (Mutex probe = Mutex.OpenExisting(name)) exists = true;
                }
                catch (WaitHandleCannotBeOpenedException) { exists = false; }
                catch (UnauthorizedAccessException) { exists = true; }
                catch (Exception pex) { Diag.Fail("instance.probe", pex); }
                return !exists;
            }
        }

        /// <summary>提权重启：主动放锁，让新进程能接上（见 Shell.RelaunchElevated）。</summary>
        public static void Release()
        {
            StopListener();
            try
            {
                if (_guard != null)
                {
                    _guard.ReleaseMutex();
                    _guard.Dispose();
                    _guard = null;
                }
            }
            catch (Exception ex)
            {
                // 释放失败 = 本次进程退出时内核会收走，不影响正确性；但重启交接可能因此卡一拍。
                Diag.Fail("instance.release", ex);
            }
        }

        /// <summary>
        /// 第二个实例：给已在运行的那一份发"请露面"的信号。
        /// 普通权限的后启动进程要给管理员权限的实例发信号，必须有宽松 ACL——
        /// 否则 TryOpenExisting 直接 AccessDenied，用户看到的就是"又点了一次，没反应"。
        /// </summary>
        public static bool SignalExisting()
        {
            try
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(Key() + "_SHOW",
                        System.Security.AccessControl.EventWaitHandleRights.Modify
                        | System.Security.AccessControl.EventWaitHandleRights.Synchronize, out ev))
                {
                    ev.Set();
                    ev.Dispose();
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                // 连信号都发不过去（权限边界）：调用方会退到"明确告知已在运行"，不是静默。
                Diag.Hit("instance.signal");
                Diag.Fail("instance.signal.detail", ex);
                return false;
            }
        }

        /// <summary>主实例：常驻一个后台等待线程，收到信号就把窗口唤回并置前。</summary>
        public static void ListenForShow(Dispatcher ui, Action surface)
        {
            if (ui == null || surface == null) return;
            if (_listener != null) return;
            try
            {
                // 宽松 ACL：允许较低完整性级别的进程给这一份发信号
                System.Security.AccessControl.EventWaitHandleSecurity sec =
                    new System.Security.AccessControl.EventWaitHandleSecurity();
                sec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.WorldSid, null),
                    System.Security.AccessControl.EventWaitHandleRights.Modify
                    | System.Security.AccessControl.EventWaitHandleRights.Synchronize,
                    System.Security.AccessControl.AccessControlType.Allow));
                bool createdEv;
                _show = new EventWaitHandle(false, EventResetMode.AutoReset, Key() + "_SHOW", out createdEv, sec);
            }
            catch (Exception ex)
            {
                // 建不出信号通道 = 用户双击第二份时只会看到"已在运行"提示，窗口不会被唤到前台。
                Diag.Fail("instance.listen", ex);
                return;
            }

            _listenStop = false;
            Thread th = new Thread(delegate ()
            {
                while (!_listenStop)
                {
                    EventWaitHandle ev = _show;
                    if (ev == null) break;
                    try
                    {
                        if (!ev.WaitOne(500)) continue;
                    }
                    catch (Exception ex)
                    {
                        // 事件对象被释放（关停路径）→ 线程自然结束，不是错误；但只在真异常时记一次。
                        if (!_listenStop) Diag.Fail("instance.listen.wait", ex);
                        break;
                    }
                    try { ui.BeginInvoke(new Action(delegate { GuardSurface(surface); })); }
                    catch (Exception ex)
                    {
                        // 调度器已关：退出时必然发生，不记账（否则每次正常退出都会留一条假失败）。
                        if (!_listenStop) Diag.Fail("instance.listen.post", ex);
                        break;
                    }
                }
            });
            th.IsBackground = true;
            th.Name = "go.showlistener";
            _listener = th;
            th.Start();
        }

        private static void GuardSurface(Action surface)
        {
            try { surface(); }
            catch (Exception ex) { Diag.Fail("instance.surface", ex); }
        }

        private static void StopListener()
        {
            _listenStop = true;
            EventWaitHandle ev = _show;
            _show = null;
            if (ev != null)
            {
                try { ev.Set(); } catch (Exception ex) { Diag.Hit("instance.listen.wake"); Diag.Fail("instance.listen.wake.detail", ex); }
                try { ev.Dispose(); } catch (Exception ex) { Diag.Hit("instance.listen.dispose"); Diag.Fail("instance.listen.dispose.detail", ex); }
            }
            _listener = null;
        }

        /// <summary>有界等待某个进程退出（提权重启的交接）。超时就继续，后面抢锁失败会走"已在运行"分支。</summary>
        public static void WaitForExit(int pid, int timeoutMs)
        {
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid))
                {
                    if (!p.WaitForExit(timeoutMs)) Diag.Hit("instance.waitp.timeout");
                    // 让内核真正回收句柄与互斥体
                    Thread.Sleep(120);
                }
            }
            catch (ArgumentException) { /* 进程已退出：正常，继续抢锁 */ }
            catch (Exception ex) { Diag.Fail("instance.waitp", ex); }
        }
    }

    // ========================================================================
    //  唯一的失败呈现点
    // ========================================================================
    /// <summary>
    /// 启动期异常/失败只有这一个出口：关掉原生闪屏 → 写 error.log → 弹一次框说明"哪一阶段、为什么"。
    /// 阶段内部一律不再弹框；阶段返回 PhaseResult，由 Run 统一交给这里。
    /// </summary>
    public static class BootPresenter
    {
        public static void Fail(PhaseResult r)
        {
            if (r == null) return;
            Fail(r.Stage, r.Reason, r.Error);
        }

        public static void Fail(string stage, string reason, Exception ex)
        {
            if (ex != null) BootReport.Log("fatal:" + stage, ex);
            else BootReport.Log("fatal:" + stage, new InvalidOperationException(reason == null ? "(无异常对象)" : reason));

            // 闪屏是独立的原生窗口：主窗口没建起来时它不会自己关，留在屏幕上就是"卡住了"。
            try { Go.SplashNative.Close(); }
            catch (Exception sx) { Diag.Fail("startup.splash.close", sx); }

            try
            {
                System.Windows.MessageBox.Show(
                    "启动失败（阶段：" + (stage == null ? "?" : stage) + "）\n\n"
                    + (string.IsNullOrEmpty(reason) ? "没有更多信息。" : reason)
                    + "\n\n详细信息已写入程序目录下的 error.log。",
                    "GameOptimizer", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch (Exception mx) { Diag.Fail("startup.dialog", mx); }
        }

        /// <summary>第二实例的提示：不是失败，但必须让用户知道"已经在跑"，且走同一个出口。</summary>
        public static void AlreadyRunning()
        {
            try
            {
                System.Windows.MessageBox.Show(
                    "GameOptimizer 已经在运行。\n\n"
                    + "它可能以管理员身份启动，当前权限无法把它唤到前台。\n"
                    + "请点击任务栏右下角的托盘图标打开，或右键以管理员身份运行。",
                    "GameOptimizer", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                // 提示框弹不出来不改变事实：另一个实例确实在跑，用户仍可用托盘图标。
                Diag.Fail("startup.alreadyrunning.dialog", ex);
            }
        }
    }
}
