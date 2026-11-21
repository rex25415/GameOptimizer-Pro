// ============================================================================
//  ShutdownPipeline.cs — 退出顺序的唯一实现
//
//  为什么要有一个"管线"而不是让每个入口各拆一遍：
//    · 退出有四条路：关窗（WM_CLOSE → Window.Closing）、Application.Exit、
//      托盘菜单的 ExitApp、系统注销（SessionEnding），外加进程被 Environment.Exit 掐断。
//      旧实现里 DaemonHost.Shutdown() 被其中三条各调一次，第四条根本没拆——
//      谁先跑到就谁拆，顺序取决于用户从哪个按钮离开。
//    · 托盘图标残留是用户唯一每天都能看见的 bug：任何一步卡住（杀软拦注册表回滚、
//      战报线程池排队）都会让 NIM_DELETE 永远轮不到执行，图标就留在任务栏上。
//      所以这里把"停服务"变成一串带预算的步骤：某一步超时就记一笔、继续下一步，
//      而托盘删除排在第二个（只等一次 Shell_NotifyIcon 调用），跑完再兜底一次。
//
//  为什么 UI 步不放到工作线程去"超时打断"：
//    WPF 的 HwndSource / Window 只能在创建它的线程上销毁。把 Tray.Remove() 丢到
//    工作线程上不是"更安全"，是 CrossThread 崩溃。所以 UI 步就地执行、只量耗时，
//    超预算同样记账——假装能打断一个卡住的 UI 调用才是撒谎。
//    （真能超时的是注册表回滚、Join、文件 IO 这些不碰 WPF 的步骤，它们跑在工作线程上。）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace Go.Daemon
{
    /// <summary>一步的结果。</summary>
    public sealed class ShutdownStepResult
    {
        /// <summary>ok / skipped / failed / timeout。</summary>
        public string State;
        public string Name;
        public long Ms;
        public string Detail;

        public bool Ok { get { return State == "ok" || State == "skipped"; } }
    }

    /// <summary>一次退出的完整账：每一步花了多久、哪一步超时/失败。</summary>
    public sealed class ShutdownReport
    {
        public readonly List<ShutdownStepResult> Steps = new List<ShutdownStepResult>();
        public long TotalMs;
        public string Trigger;
        public int Timeouts;
        public int Failures;
        public int Skipped;
        /// <summary>是否有步骤是被"超时放弃"的（线程可能还在跑）。</summary>
        public bool AbandonedThreads;

        /// <summary>一行摘要：进审计日志/Diag 用，能直接看出慢在哪一步。</summary>
        public string Summary()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("trigger=").Append(Trigger)
              .Append(" steps=").Append(Steps.Count)
              .Append(" total=").Append(TotalMs).Append("ms")
              .Append(" timeout=").Append(Timeouts)
              .Append(" failed=").Append(Failures);
            if (Skipped > 0) sb.Append(" skipped=").Append(Skipped);
            if (AbandonedThreads) sb.Append(" abandoned=1");
            for (int i = 0; i < Steps.Count; i++)
            {
                ShutdownStepResult s = Steps[i];
                if (s.State == "ok") continue;
                sb.Append(" | ").Append(s.Name).Append(":").Append(s.State)
                  .Append("(").Append(s.Ms).Append("ms)");
                if (!string.IsNullOrEmpty(s.Detail)) sb.Append(" ").Append(s.Detail);
            }
            return sb.ToString();
        }

        public ShutdownStepResult Find(string name)
        {
            for (int i = 0; i < Steps.Count; i++) if (Steps[i].Name == name) return Steps[i];
            return null;
        }
    }

    /// <summary>
    /// 有序步骤表。按注册顺序**正序**执行（先注册 = 先执行）。
    /// RunOnce 幂等：第二次调用拿到同一份报告，不会真的再拆一遍。
    ///
    /// Rev87 把执行方向从"逆序"改成"正序"。旧契约是"后注册 = 更靠近用户，先拆"，
    /// 但 DaemonHost 是按执行顺序注册的（托盘 → 停服务 → 复原 → 放锁），两边对
    /// 同一份列表的理解正好相反：实际跑成了"先放锁、先删崩溃落点、最后才摘托盘" ——
    /// 复原失败时落点已经没了，另一个实例还能在复原途中接管账本。
    /// 现在只有一种读法：注册顺序就是执行顺序；并且有总预算兜住最坏情况。
    /// </summary>
    public sealed class ShutdownPipeline
    {
        private sealed class Step
        {
            public string Name;
            public int BudgetMs;
            public bool UiThread;
            public bool Critical;
            public Func<bool> When;
            public Action Run;
        }

        private readonly List<Step> _steps = new List<Step>();
        private readonly object _gate = new object();
        private ShutdownReport _report;

        /// <summary>整条管线的总预算。超过后只跳过非关键步骤，关键步骤仍按各自的预算执行。</summary>
        public int TotalBudgetMs = 8000;

        public int Count { get { return _steps.Count; } }

        /// <summary>
        /// 注册一步。name 会进报告与审计，必须是固定的英文短名（便于按标签统计）。
        /// budgetMs 是这一步的预算：工作线程步超时即放弃并继续；UI 步只记账。
        /// when 为 null 或返回 false 时该步记为 skipped。
        /// </summary>
        public void Add(string name, int budgetMs, bool uiThread, Func<bool> when, Action run)
        {
            Add(name, budgetMs, uiThread, when, run, false);
        }

        /// <summary>critical=true 的步骤不受总预算裁剪（例如账本复原、释放会话锁）。</summary>
        public void Add(string name, int budgetMs, bool uiThread, Func<bool> when, Action run, bool critical)
        {
            if (run == null) return;
            Step s = new Step();
            s.Name = name;
            s.BudgetMs = budgetMs < 1 ? 1 : budgetMs;
            s.UiThread = uiThread;
            s.When = when;
            s.Run = run;
            s.Critical = critical;
            _steps.Add(s);
        }

        public void Add(string name, int budgetMs, bool uiThread, Action run)
        {
            Add(name, budgetMs, uiThread, null, run, false);
        }

        public void Add(string name, int budgetMs, bool uiThread, Action run, bool critical)
        {
            Add(name, budgetMs, uiThread, null, run, critical);
        }

        /// <summary>按注册顺序执行全部步骤。第二次调用返回第一次的报告（幂等）。</summary>
        public ShutdownReport RunOnce(string trigger)
        {
            lock (_gate)
            {
                if (_report != null) return _report;
                ShutdownReport rep = new ShutdownReport();
                rep.Trigger = trigger == null ? "?" : trigger;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < _steps.Count; i++)
                {
                    Step s = _steps[i];

                    // 总预算：一次退出不该被"十步各自 6 秒"串成十几秒。
                    // 关键步骤（复原/落点/会话锁）不受裁剪，否则宁可慢也不能丢状态。
                    if (TotalBudgetMs > 0 && !s.Critical && sw.ElapsedMilliseconds >= TotalBudgetMs)
                    {
                        ShutdownStepResult skipped = new ShutdownStepResult();
                        skipped.Name = s.Name;
                        skipped.State = "skipped";
                        skipped.Detail = "total-budget>" + TotalBudgetMs + "ms";
                        rep.Steps.Add(skipped);
                        rep.Skipped++;
                        Diag.Hit("shutdown.budget:" + s.Name);
                        continue;
                    }

                    ShutdownStepResult r;
                    if (s.When != null)
                    {
                        bool go;
                        try { go = s.When(); }
                        catch (Exception wex)
                        {
                            // 条件本身抛异常 = 不知道该不该拆：按"要拆"处理。
                            // 少拆一步最坏是留个句柄，多拆一步最多是重复一次幂等清理。
                            Diag.Fail("shutdown.when:" + s.Name, wex);
                            go = true;
                        }
                        if (!go)
                        {
                            r = new ShutdownStepResult();
                            r.Name = s.Name;
                            r.State = "skipped";
                            rep.Steps.Add(r);
                            rep.Skipped++;
                            continue;
                        }
                    }
                    r = Exec(s);
                    rep.Steps.Add(r);
                    if (r.State == "timeout") { rep.Timeouts++; rep.AbandonedThreads = true; }
                    else if (r.State == "failed") rep.Failures++;
                }
                sw.Stop();
                rep.TotalMs = sw.ElapsedMilliseconds;
                _report = rep;
                return rep;
            }
        }

        /// <summary>只读地看一眼报告（没跑过 = null）。</summary>
        public ShutdownReport Report { get { return _report; } }

        private static ShutdownStepResult Exec(Step s)
        {
            ShutdownStepResult r = new ShutdownStepResult();
            r.Name = s.Name;
            r.State = "ok";
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            if (s.UiThread)
            {
                // 就地跑：WPF 对象只能在自己线程上销毁，见文件头。
                try { s.Run(); }
                catch (Exception ex)
                {
                    r.State = "failed";
                    r.Detail = ex.GetType().Name + ": " + ex.Message;
                    Diag.Fail("shutdown." + s.Name, ex);
                }
                sw.Stop();
                r.Ms = sw.ElapsedMilliseconds;
                // UI 步没有超时打断，只能报告"它慢"：这一步会进 Summary 与 Diag。
                if (r.State == "ok" && r.Ms > s.BudgetMs)
                {
                    r.Detail = "slow>" + s.BudgetMs + "ms";
                    Diag.Hit("shutdown.slow:" + s.Name);
                }
                return r;
            }

            // 不碰 WPF 的步骤放到工作线程上，才能真的用 Join(budget) 放弃一个卡死的调用。
            // 放弃的线程是后台线程：它随进程退出，不会拖住关闭。
            Exception err = null;
            Thread th = new Thread(delegate ()
            {
                try { s.Run(); }
                catch (Exception ex) { err = ex; }
            });
            th.IsBackground = true;
            th.Name = "go.shutdown." + s.Name;
            try { th.Start(); }
            catch (Exception ex)
            {
                r.State = "failed";
                r.Detail = "start: " + ex.GetType().Name + ": " + ex.Message;
                Diag.Fail("shutdown.start:" + s.Name, ex);
                return r;
            }
            bool joined = th.Join(s.BudgetMs);
            sw.Stop();
            r.Ms = sw.ElapsedMilliseconds;
            if (!joined)
            {
                r.State = "timeout";
                r.Detail = "abandoned>" + s.BudgetMs + "ms";
                Diag.Hit("shutdown.timeout:" + s.Name);
                return r;
            }
            if (err != null)
            {
                r.State = "failed";
                r.Detail = err.GetType().Name + ": " + err.Message;
                Diag.Fail("shutdown." + s.Name, err);
            }
            return r;
        }
    }
}
