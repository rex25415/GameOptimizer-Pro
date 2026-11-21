// ============================================================================
//  AutoPilot.cs — 战术自动挂载（带权限边界识别与失败归因）
//  进入：CCD0 亲和度 / 独显优先 / 0.5ms 定时器租约。
//  退出：账本逆序复原系统参数；亲和度随进程消亡自然消失。
//
//  实战要点：
//    · 带反作弊（VAC / EAC / Vanguard）的游戏进程常被内核保护，
//      SetProcessAffinityMask 会返回 ERROR_ACCESS_DENIED。这是**预期行为**，
//      不能当成"失败"沉默掉——必须明确告诉用户"被保护，未生效"，
//      并且不能因此回滚其它已经生效的策略。
//    · 反作弊通常在主窗口就绪后才施加保护，因此亲和度做一次延迟重试。
// ============================================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace Go.Daemon
{
    public enum PolicyState { Skipped = 0, Applied = 1, Denied = 2, Failed = 3, Pending = 4 }

    public sealed class PolicyResult
    {
        public string Key;
        public PolicyState State;
        public string Note;
    }

    public sealed class AutoPilot
    {
        private readonly Core _core;
        private readonly List<PolicyResult> _last = new List<PolicyResult>();
        // _last 被三方读写：挂载（巡检线程）、延迟重试（go.affinity-retry 线程）、
        // LastHadDenied（UI 线程 foreach）。List 索引 setter 会 _version++，
        // 与并发 foreach 相撞即抛迭代期修改异常。全部读写收敛到这把锁。
        private readonly object _lastLock = new object();
        private volatile string _lastReport = "";
        private int _attachSeq;
        private volatile bool _run;

        /// <summary>退出时是否按账本复原系统参数。</summary>
        public bool RevertOnExit = true;

        /// <summary>
        /// 供电判定（true = 接电）。由 DaemonHost 注入 PowerMonitor.OnAc。
        /// 为什么挂载时要看它：拔电那一刻 OnPowerChanged 只撤一次租约；之后在电池下
        /// 再启动一个游戏（或托盘切档案）会无条件重取 0.5ms，整局把 CPU 钉在最高精度，
        /// 正好抵消"拔电自动降档"的省电设计。null = 不做判定（离屏/自检）。
        /// </summary>
        public Func<bool> PowerOk;

        private readonly object _retryLock = new object();
        private readonly Timer _retryTimer;
        private GameProfile _retryProfile;
        private int _retryPid;
        private int _retrySeq;
        private int _ecoThrottledCount;

        public int EcoThrottledCount { get { return _ecoThrottledCount; } }

        public AutoPilot(Core core)
        {
            _core = core;
            _run = true;
            _retryTimer = new Timer(RetryTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public string LastReport { get { return _lastReport; } }

        public bool LastHadDenied
        {
            get
            {
                lock (_lastLock)
                {
                    foreach (PolicyResult r in _last) if (r.State == PolicyState.Denied) return true;
                }
                return false;
            }
        }

        public void Stop()
        {
            _run = false;
            try { _retryTimer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { Diag.Hit("autopilot.stop.timer"); }
            try { _retryTimer.Dispose(); }
            catch (Exception ex) { Diag.Fail("daemon.retry.dispose", ex); }
            try { ProcessPower.RevertTracked(); }
            catch (Exception ex) { Diag.Fail("daemon.stop.reverttracked", ex); }
        }

        public void Attach(GameProfile p, int pid)
        {
            if (p == null) { _lastReport = ""; lock (_lastLock) { _last.Clear(); } return; }
            int seq = Interlocked.Increment(ref _attachSeq);
            _ecoThrottledCount = 0;
            lock (_lastLock) { _last.Clear(); }

            // 崩溃落点必须在**任何系统写入之前**落盘。
            // 落点写不下去时直接放弃本次挂载——"宁可不优化，也不能改完还原不回来"。
            if (!SaveLanding(p, pid))
            {
                _lastReport = "崩溃落点写入失败，已放弃本次挂载（不改系统）";
                _core.Engine.Ledger.AppendAudit("DAEMON attach aborted " + p.Name + " :: " + SessionStore.LastError);
                _core.State.Post(Dirty.Daemon);
                return;
            }

            if (p.HighGpu) Append(true, "GPU", AttachGpu(p));

            string ccdMode = EffectiveCcdMode(p);
            if (!string.Equals(ccdMode, "Auto", StringComparison.OrdinalIgnoreCase))
                Append(true, "CCD0", AttachAffinity(p, pid, seq, 0, ccdMode));

            if (!string.IsNullOrEmpty(p.PriorityMode) &&
                !string.Equals(p.PriorityMode, "Keep", StringComparison.OrdinalIgnoreCase))
                Append(true, "PRIO", AttachPriority(p, pid, seq, 0));

            if (p.EcoBackground)
            {
                PolicyResult ecoRes = new PolicyResult();
                ecoRes.Key = "ECO";
                ecoRes.State = PolicyState.Applied;
                ecoRes.Note = "已开启后台高占用 EcoQoS 压制";
                Append(true, "ECO", ecoRes);
            }

            if (p.Timer) Append(true, "TIMER", AttachTimer(p));

            if (ProcessPower.TrackedCount > 0) SaveLanding(p, pid);

            _lastReport = Summarize();
            _core.State.Post(Dirty.Daemon);
            _core.Engine.Ledger.AppendAudit("DAEMON attach " + p.Name + " pid=" + pid + " -> " + _lastReport);
        }

        private static string EffectiveCcdMode(GameProfile p)
        {
            if (p == null) return "Auto";
            string m = p.CcdMode;
            if (!string.IsNullOrEmpty(m)) return m;
            return p.PinCcd0 ? "Ccd0" : "Auto";
        }

        /// <summary>写崩溃落点。返回 false 时 LastReport 里已经带上原因。</summary>
        private bool SaveLanding(GameProfile p, int pid)
        {
            SessionRecord rec = new SessionRecord();
            rec.ProfileId = p.Id;
            rec.ProfileName = p.Name;
            rec.Exe = p.Exe;
            rec.GamePid = pid;
            try
            {
                using (Process self = Process.GetCurrentProcess())
                    rec.AppPid = self.Id;
            }
            catch (Exception ex)
            {
                Diag.Hit("autopilot.pid");
                Diag.Fail("autopilot.pid.detail", ex);
            }
            rec.AppBorn = SessionStore.SelfBorn();
            rec.AttachedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            rec.TrackedProcs = ProcessPower.SnapshotTrackedRecords();
            if (SessionStore.Save(rec)) return true;
            Diag.Hit("autopilot.landing");
            return false;
        }

        private void Append(bool wanted, string key, PolicyResult r)
        {
            if (!wanted) return;
            lock (_lastLock) { _last.Add(r); }
        }

        private string Summarize()
        {
            lock (_lastLock)
            {
                int ok = 0, denied = 0, fail = 0;
                foreach (PolicyResult r in _last)
                {
                    if (r.State == PolicyState.Applied) ok++;
                    else if (r.State == PolicyState.Denied) denied++;
                    else if (r.State == PolicyState.Failed) fail++;
                }
                if (_last.Count == 0) return "未配置策略";
                string s = ok + " 项生效";
                if (denied > 0) s += " · " + denied + " 项被进程保护拒绝";
                if (fail > 0) s += " · " + fail + " 项失败";
                return s;
            }
        }

        // ---- 各项策略 ------------------------------------------------------
        private PolicyResult AttachGpu(GameProfile p)
        {
            PolicyResult r = new PolicyResult();
            r.Key = "GPU";
            if (string.IsNullOrEmpty(p.Exe)) { r.State = PolicyState.Skipped; return r; }
            try
            {
                Op op = new Op();
                op.Kind = "Reg";
                op.Hive = "HKCU";
                op.Path = @"Software\Microsoft\DirectX\UserGpuPreferences";
                op.Name = p.Exe;
                op.Type = "String";
                op.Value = "GpuPreference=2;";
                OpResult res = Ops.Apply(op, GpuActionId(p), "守护·独显优先 " + p.Name, _core.Engine.Ledger);
                r.State = res.Ok ? PolicyState.Applied : PolicyState.Failed;
                r.Note = res.Ok ? "已强制高性能" : res.Message;
            }
            catch (Exception ex)
            {
                // 独显偏好写失败：Note 会进 LastReport 显示给用户，同时进 Diag。
                Diag.Fail("daemon.gpu.apply", ex);
                r.State = PolicyState.Failed;
                r.Note = ex.Message;
            }
            return r;
        }

        /// <summary>
        /// 亲和度。attempt 0 = 立即；attempt 1 = 1.4s 后重试（反作弊常在此之后才就绪）。
        /// 返回 Denied 表示"被内核保护"，这是游戏的自我保护，不是我们的 bug。
        /// </summary>
        private PolicyResult AttachAffinity(GameProfile p, int pid, int seq, int attempt, string ccdMode)
        {
            PolicyResult r = new PolicyResult();
            r.Key = "CCD0";
            Topology topo = _core.Topo;

            long mask = 0;
            string modeLabel = ccdMode;
            if (string.Equals(ccdMode, "Physical", StringComparison.OrdinalIgnoreCase))
            {
                mask = Affinity.PhysicalOnlyMask(topo);
                modeLabel = "仅物理核心";
            }
            else if (string.Equals(ccdMode, "All", StringComparison.OrdinalIgnoreCase))
            {
                mask = Affinity.AllMask();
                modeLabel = "全部核心";
            }
            else if (string.Equals(ccdMode, "Ccd1", StringComparison.OrdinalIgnoreCase))
            {
                if (topo.Ccds <= 1)
                {
                    r.State = PolicyState.Skipped;
                    r.Note = "单簇平台，无 CCD1";
                    return r;
                }
                mask = Affinity.CcdMask(topo, 1);
                modeLabel = "CCD1";
            }
            else
            {
                if (topo.Ccds <= 1)
                {
                    r.State = PolicyState.Skipped;
                    r.Note = "单簇平台，无需绑定";
                    return r;
                }
                mask = Affinity.CcdMask(topo, 0);
                modeLabel = topo.CcdLabel;
            }

            if (mask == 0)
            {
                r.State = PolicyState.Skipped;
                r.Note = "目标核心掩码为空";
                return r;
            }

            int okCount = 0, denied = 0, gone = 0;
            string why = null;
            try
            {
                int[] targetPids = ResolveTargetPids(p, pid);
                for (int i = 0; i < targetPids.Length; i++)
                {
                    int tpid = targetPids[i];
                    if (tpid <= 0) { gone++; continue; }
                    string err;
                    if (Affinity.Apply(tpid, mask, out err))
                    {
                        okCount++;
                    }
                    else if (!string.IsNullOrEmpty(err) && err.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0)
                    {
                        denied++;
                    }
                    else if (!string.IsNullOrEmpty(err) && err.IndexOf("已退出", StringComparison.Ordinal) >= 0)
                    {
                        gone++;
                    }
                    else
                    {
                        why = err ?? "设置亲和性失败";
                    }
                }
                if (targetPids.Length == 0) gone++;
            }
            catch (Exception ex)
            {
                Diag.Fail("daemon.affinity", ex);
                why = ex.GetType().Name;
            }

            if (okCount > 0)
            {
                r.State = PolicyState.Applied;
                r.Note = "绑定到 " + modeLabel + " (0x" + mask.ToString("X") + ")";
            }
            else if (denied > 0)
            {
                r.State = PolicyState.Denied;
                r.Note = "进程受反作弊/内核保护，亲和度未生效";
                if (attempt < 1 && seq == _attachSeq && _run) ScheduleRetry(p, pid, seq);
            }
            else if (gone > 0)
            {
                r.State = PolicyState.Skipped;
                r.Note = "进程已退出";
            }
            else
            {
                r.State = PolicyState.Failed;
                r.Note = why ?? "未知原因";
            }
            return r;
        }

        private PolicyResult AttachPriority(GameProfile p, int pid, int seq, int attempt)
        {
            PolicyResult r = new PolicyResult();
            r.Key = "PRIO";
            uint targetCls = ProcessPower.ParsePriorityMode(p.PriorityMode);
            if (targetCls == 0)
            {
                r.State = PolicyState.Skipped;
                r.Note = "保持系统默认优先级";
                return r;
            }

            int[] targetPids = ResolveTargetPids(p, pid);
            if (targetPids.Length == 0)
            {
                r.State = PolicyState.Skipped;
                r.Note = "目标进程未运行";
                return r;
            }

            int ok = 0;
            bool denied = false;
            string lastErr = null;
            for (int i = 0; i < targetPids.Length; i++)
            {
                string err;
                if (ProcessPower.SetPriority(targetPids[i], targetCls, true, out err)) ok++;
                else
                {
                    lastErr = err;
                    if (err != null && err.IndexOf("拒绝", StringComparison.Ordinal) >= 0) denied = true;
                }
            }

            if (ok > 0)
            {
                r.State = PolicyState.Applied;
                r.Note = "优先级已设为「" + ProcessPower.PriorityLabel(targetCls) + "」";
            }
            else if (denied)
            {
                r.State = PolicyState.Denied;
                r.Note = "进程受保护，优先级未生效";
                if (attempt < 1 && seq == _attachSeq && _run) ScheduleRetry(p, pid, seq);
            }
            else
            {
                r.State = PolicyState.Failed;
                r.Note = lastErr ?? "优先级设置失败";
            }
            return r;
        }

        private static int[] ResolveTargetPids(GameProfile p, int pid)
        {
            if (pid > 0) return new int[] { pid };
            try
            {
                return ProcessScan.FindPidsByName(StripExt(p));
            }
            catch (Exception ex)
            {
                Diag.Fail("autopilot.resolve.pids", ex);
                return new int[0];
            }
        }

        public int OnPulse(GameProfile p, int gamePid, SessionRecorder recorder)
        {
            if (!_run || p == null || !p.EcoBackground || recorder == null) return 0;
            List<int> hot = recorder.CollectRecentHighCpuPids(5.0);
            if (hot.Count == 0) return 0;

            int suppressed = 0;
            string gameBase = string.IsNullOrEmpty(p.Exe) ? "" : StripExt(p);
            for (int i = 0; i < hot.Count; i++)
            {
                string err;
                if (TrySuppressBackgroundPid(hot[i], gamePid, gameBase, out err)) suppressed++;
            }
            if (suppressed > 0) SaveLanding(p, gamePid);
            return suppressed;
        }

        public bool TrySuppressBackgroundPid(int bgPid, int gamePid, string gameProcName, out string err)
        {
            err = null;
            if (bgPid <= 4 || bgPid == gamePid || bgPid == SessionStore.SelfPidSafe())
            {
                err = "跳过自身或目标进程";
                return false;
            }
            if (ProcessPower.IsTracked(bgPid)) return false;

            string pname = ProcName.Of(bgPid);
            if (!string.IsNullOrEmpty(pname))
            {
                if (ProcessPower.IsProtectedBackgroundName(pname)) { err = "系统关键进程不压制"; return false; }
                if (!string.IsNullOrEmpty(gameProcName) &&
                    string.Equals(pname, gameProcName, StringComparison.OrdinalIgnoreCase))
                {
                    err = "目标游戏同族进程不压制";
                    return false;
                }
            }

            if (ProcessPower.SetEcoQoS(bgPid, true, true, out err))
            {
                Interlocked.Increment(ref _ecoThrottledCount);
                return true;
            }
            return false;
        }

        public int RecoverTrackedProcs(SessionRecord rec)
        {
            if (rec == null || rec.TrackedProcs == null || rec.TrackedProcs.Length == 0) return 0;
            return ProcessPower.RevertTrackedRecords(rec.TrackedProcs);
        }

        /// <summary>
        /// 延迟重试一次亲和度（反作弊通常在主窗口就绪后才施加保护）。
        /// 用单实例 Timer，而不是"每次被拒就 new Thread + Sleep(1400)"：
        /// 反复挂载/切档案时进程里会短暂堆叠一堆睡眠线程，Detach 之后它们还会跑完。
        /// Change 会覆盖上一次排程，同一时刻最多一个待执行重试。
        /// </summary>
        private void ScheduleRetry(GameProfile p, int pid, int seq)
        {
            lock (_retryLock)
            {
                _retryProfile = p;
                _retryPid = pid;
                _retrySeq = seq;
            }
            try { _retryTimer.Change(1400, Timeout.Infinite); }
            catch (ObjectDisposedException) { Diag.Hit("autopilot.retry.disposed"); }
        }

        private void RetryTick(object state)
        {
            if (!_run) return;
            GameProfile p;
            int pid, seq;
            lock (_retryLock)
            {
                p = _retryProfile;
                pid = _retryPid;
                seq = _retrySeq;
            }
            if (p == null || seq != _attachSeq) return;

            string ccdMode = EffectiveCcdMode(p);
            PolicyResult rAff = !string.Equals(ccdMode, "Auto", StringComparison.OrdinalIgnoreCase)
                ? AttachAffinity(p, pid, seq, 1, ccdMode)
                : null;
            PolicyResult rPrio = (!string.IsNullOrEmpty(p.PriorityMode) &&
                                  !string.Equals(p.PriorityMode, "Keep", StringComparison.OrdinalIgnoreCase))
                ? AttachPriority(p, pid, seq, 1)
                : null;
            lock (_lastLock)
            {
                // 锁内复检序号：检查与写之间若发生新 Attach/Detach，旧重试不得覆盖新结果。
                if (seq != _attachSeq) return;
                for (int i = 0; i < _last.Count; i++)
                {
                    if (_last[i].Key == "CCD0" && rAff != null) _last[i] = rAff;
                    else if (_last[i].Key == "PRIO" && rPrio != null) _last[i] = rPrio;
                }
                _lastReport = Summarize();
            }
            _core.State.Post(Dirty.Daemon);
        }

        private PolicyResult AttachTimer(GameProfile p)
        {
            PolicyResult r = new PolicyResult();
            r.Key = "TIMER";

            Func<bool> ac = PowerOk;
            if (ac != null)
            {
                bool onAc;
                try { onAc = ac(); }
                catch (Exception ex)
                {
                    // 判定失败按"接电"处理：宁可多提升一次精度，也不能让插着电的台式机
                    // 因为一次探针异常而永远拿不到 0.5ms。
                    Diag.Fail("daemon.timer.power", ex);
                    onAc = true;
                }
                if (!onAc)
                {
                    r.State = PolicyState.Skipped;
                    r.Note = "电池供电 · 暂不提升精度（插电后自动恢复）";
                    return r;
                }
            }

            _core.Timer.Request(TimerOwner.Daemon, 0.5);
            if (_core.Timer.Active)
            {
                r.State = PolicyState.Applied;
                r.Note = "本进程 " + _core.Timer.GrantedMs.ToString("0.0") + " ms";
            }
            else { r.State = PolicyState.Failed; r.Note = "系统拒绝提升精度"; }
            return r;
        }

        // ---- 复原 ----------------------------------------------------------
        public void Detach(GameProfile p, int pid)
        {
            if (p == null) return;
            Interlocked.Increment(ref _attachSeq);   // 取消尚未执行的重试
            _core.Timer.Release(TimerOwner.Daemon);

            bool revertOk = true;
            string revertWhy = null;
            ProcessPower.RevertTracked();
            if (RevertOnExit)
            {
                try
                {
                    ApplyReport r = _core.Engine.RevertAction(GpuActionId(p));
                    if (r.Applied > 0)
                        _core.Engine.Ledger.AppendAudit("DAEMON revert gpu " + p.Name + " entries=" + r.Applied);
                    if (!r.Ok) { revertOk = false; revertWhy = r.Failed + " 项失败"; }
                }
                catch (Exception ex)
                {
                    // 复原失败意味着系统停在改过的状态——用户必须看到，绝不能报"已复原"。
                    Diag.Fail("daemon.revert", ex);
                    revertOk = false;
                    revertWhy = ex.Message;
                }
            }
            _ecoThrottledCount = 0;

            _lastReport = revertOk ? "已复原" : ("复原失败：" + revertWhy + " —— 需要到还原中心手动处理");
            lock (_lastLock) { _last.Clear(); }
            // 只有复原成功（或本来就不打算复原）才删崩溃落点。
            // 旧实现无条件 Clear：复原失败时落点也没了，下次启动没有任何依据重试，
            // 账本里的 daemon-gpu-* 永久变成孤儿。
            if (revertOk || !RevertOnExit) SessionStore.Clear();
            else Diag.Hit("daemon.landing.kept");
            _core.State.Post(Dirty.Daemon);
            _core.Engine.Ledger.AppendAudit("DAEMON detach " + p.Name + " pid=" + pid);
        }

        /// <summary>托盘/界面的"一键极致模式"：三件套全开并按当前游戏重挂。</summary>
        public void ApplyExtreme(GameProfile p)
        {
            if (p == null) return;
            bool updated = !string.IsNullOrEmpty(p.Id) && _core.Profiles.UpdateProfile(p.Id, delegate (GameProfile gp)
            {
                gp.PinCcd0 = true;
                gp.CcdMode = "Ccd0";
                gp.HighGpu = true;
                gp.Timer = true;
            }, true);
            if (!updated)
            {
                p.PinCcd0 = true;
                p.CcdMode = "Ccd0";
                p.HighGpu = true;
                p.Timer = true;
                _core.Profiles.Save();
            }
            int pid = 0;
            if (_core.Daemon != null && _core.Daemon.Watcher.Active == p)
                pid = _core.Daemon.Watcher.ActivePid;
            Attach(p, pid);
        }

        private static string GpuActionId(GameProfile p) { return "daemon-gpu-" + p.Id; }

        private static string StripExt(GameProfile p)
        {
            string n = System.IO.Path.GetFileName(p.Exe);
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n;
        }
    }
}
