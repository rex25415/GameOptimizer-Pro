// ============================================================================
//  GameWatcher.cs — 游戏启动感知（事件驱动的低频进程巡检）
//  规则：只要用户为某个 exe 建过战术档案，那个 exe 就是"目标游戏"。
//        不做启发式猜测，不误伤 IDE / 启动器 / 直播工具。
//  代价：空闲期 250ms 一次进程表遍历（约 0.2ms 的纯非托管读），命中后降到 1s。
//
//  Rev87 把这一层当"常驻服务"重写，修掉三处会长期啃 CPU / 卡 UI 的结构问题：
//   1) _wake 是 ManualResetEvent，全类只在 Start 里 Reset 一次。任何一次 Set()
//      （关闭守护、Reload）之后 WaitOne 永久立即返回 —— 循环退化成无休眠紧循环。
//      关闭守护后是纯空转（单核 100%），重开则变成不停全表扫描。
//      现在用 AutoResetEvent：一次 Set 只唤醒一次。
//   2) Step 在 _scanLock 内直接触发 Started/Stopped 回调，而回调会做注册表写入 /
//      账本落盘 / 事件日志；UI 线程的 PollOnce / ForceAttach 要等同一把锁 ——
//      杀软拦一次注册表，托盘"档案切换"就整个卡住。现在锁内只改状态并生成
//      StepOutcome，回调在锁外统一派发。
//   3) Stop 在 Join 超时后仍无条件 Detach，并允许 Start 覆盖 _thread：旧线程看到
//      _run 又变 true 会继续跑，两条巡检线程共用同一组探针字段，可把 A 进程的 PID
//      挂到 B 档案上。现在 Join 超时只记账、不动状态；Start 前检查 IsAlive。
//
//  另外：手动挂载一个尚未启动的游戏时，不再在下一拍立刻判定"进程未运行"并解挂，
//  而是给 15 秒等待窗（用户从托盘选了档案、再去点开游戏，本来就有一个间隔）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Go.Daemon
{
    public sealed class GameWatcher
    {
        private readonly Core _core;
        private readonly ProcessScan _scan = new ProcessScan();
        // 巡检线程与 PollOnce（自检/托盘，可能在别的线程）共用同一块非托管缓冲，
        // Grow 会 Free+Alloc，必须串行化，否则使用已释放内存。Monitor 可重入。
        private readonly object _scanLock = new object();
        private Thread _thread;
        private volatile bool _run;
        private volatile bool _enabled;
        // 一次 Set 只唤醒一次等待：ManualResetEvent 的残留信号会让 while 循环空转。
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        /// <summary>窗口收进托盘/最小化时置真：空闲巡检降频，幽灵般近零开销。</summary>
        public volatile bool Dormant;

        // 目标表（档案变更时重建；比较时不分配字符串）
        private string[] _names = new string[0];
        private GameProfile[] _profs = new GameProfile[0];
        private int _sig = -1;

        private GameProfile _active;
        private int _activePid;
        /// <summary>挂载时目标的创建时间（0 = 未知）。PID 会被系统复用，只比 PID 会把
        /// 另一个进程当成"游戏还活着"，策略永不回滚。有创建时间就一起比。</summary>
        private long _activeBorn;
        /// <summary>手动挂载但还没找到进程的起始时刻（0 = 已在运行）。</summary>
        private int _pendingSince;

        /// <summary>空闲巡检间隔（毫秒）。</summary>
        public int IdleIntervalMs = 250;
        /// <summary>已挂载时的巡检间隔（毫秒）。</summary>
        public int ActiveIntervalMs = 1000;
        /// <summary>休眠且空闲时的巡检间隔（毫秒）。</summary>
        public int DormantIdleIntervalMs = 1500;
        /// <summary>手动挂载后等待目标进程出现的最长时间。</summary>
        public int AttachGraceMs = 15000;

        public event Action<GameProfile, int> Started;
        public event Action<GameProfile, int> Stopped;
        /// <summary>已挂载期间的每秒心跳（战报采样用）。</summary>
        public event Action Pulse;

        public GameWatcher(Core core) { _core = core; }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (value)
                {
                    EnsureRunning();
                }
                else
                {
                    // 锁内只改状态，锁外派发：Detach 的对外回调会做账本追加 / 注册表回滚，
                    // 被安全软件拦一下就是几百毫秒——那段绝不能挡住 UI 线程。
                    StepOutcome o = null;
                    lock (_scanLock) { if (_active != null) o = DetachLocked("用户关闭守护"); }
                    Dispatch(o);
                    _wake.Set();
                }
                _core.State.Post(Dirty.Daemon);
            }
        }

        /// <summary>Stop() 是否真的 Join 成功。false = 巡检线程仍可能在写状态，
        /// 调用方不得随后 Dispose 战报缓冲（use-after-free），也不能假定没有在途一拍。</summary>
        public bool StopJoined { get { return _stopJoined; } }
        private bool _stopJoined = true;

        public GameProfile Active { get { return _active; } }
        public int ActivePid { get { return _activePid; } }
        /// <summary>挂载目标的创建时间（0 = 未知）。判活必须和 PID 一起比，见 ProbeOne。</summary>
        public long ActiveBorn { get { return _activeBorn; } }
        public int ScanCount { get; private set; }

        public void Start()
        {
            if (_run) return;
            // 上一轮 Stop 的 Join 超时：旧线程可能还活着，再开一条会共用探针字段。
            Thread prior = _thread;
            if (prior != null && prior.IsAlive)
            {
                Diag.Hit("daemon.watcher.start.busy");
                return;
            }
            _run = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.BelowNormal;
            _thread.Name = "go.watcher";
            _thread.Start();
        }

        private void EnsureRunning() { if (!_run) Start(); }

        public void Stop()
        {
            _run = false;
            _wake.Set();
            Thread t = _thread;
            bool stopped = true;
            // 有界 Join：_wake 已叫醒，正常在一拍内退出；1500ms 只是兜底上限。
            if (t != null && t.IsAlive)
            {
                try { stopped = t.Join(1500); }
                catch (Exception ex) { Diag.Fail("daemon.watcher.join", ex); stopped = false; }
            }
            _stopJoined = stopped;

            if (!stopped)
            {
                // 线程还卡在 Step 里：此时改 _active 会与它并发，释放缓冲会 use-after-free。
                // 状态与缓冲都随进程退出回收；这里只留证。
                Diag.Hit("daemon.watcher.stop.timeout");
                return;
            }

            StepOutcome o = null;
            lock (_scanLock)
            {
                if (_active != null) o = DetachLocked("停止");
                _scan.Dispose();
                _thread = null;
            }
            Dispatch(o);
        }

        /// <summary>档案变更后调用：重建目标表，并让当前会话在下一次巡检时重新判定。</summary>
        public void Reload()
        {
            _sig = -1;
            _wake.Set();
            _core.State.Post(Dirty.Daemon);
        }

        private void RebuildTargets()
        {
            GameProfile[] items = _core.Profiles.Snapshot();
            int n = items.Length;
            string[] names = new string[n];
            GameProfile[] profs = new GameProfile[n];
            for (int i = 0; i < n; i++)
            {
                GameProfile p = items[i];
                string exe = p.Exe;
                if (string.IsNullOrEmpty(exe)) { names[i] = ""; profs[i] = p; continue; }
                names[i] = Path.GetFileName(exe);
                profs[i] = p;
            }
            _names = names;
            _profs = profs;
            _sig = _core.Profiles.Version;
        }

        // ====================================================================
        //  巡检循环
        // ====================================================================
        private void Loop()
        {
            while (_run)
            {
                int wait = 1000;
                try
                {
                    StepOutcome o = null;
                    lock (_scanLock) { o = StepLocked(); }
                    wait = o != null ? o.WaitMs : 1000;
                    Dispatch(o);
                }
                catch (Exception ex)
                {
                    // 巡检线程是后台服务：一拍异常只记账并退避，绝不能带异常终止。
                    Diag.Fail("daemon.loop", ex);
                    wait = 1000;
                }
                if (wait < 50) wait = 50;
                if (!_run) break;
                _wake.WaitOne(wait);
            }
        }

        /// <summary>一步的结果：锁内只描述"发生了什么"，回调由 Dispatch 在锁外执行。</summary>
        private sealed class StepOutcome
        {
            public int WaitMs = 1000;
            public GameProfile Started;
            public int StartedPid;
            public GameProfile Stopped;
            public int StoppedPid;
            public string StopReason;
            public bool Pulse;
        }

        private void Dispatch(StepOutcome o)
        {
            if (o == null) return;
            if (o.Stopped != null) RaiseStopped(o.Stopped, o.StoppedPid, o.StopReason);
            if (o.Pulse)
            {
                Action pl = Pulse;
                // 订阅者（SessionRecorder）抛异常不能让巡检线程停摆；但战报会因此缺拍。
                if (pl != null) { try { pl(); } catch (Exception ex) { Diag.Fail("daemon.pulse", ex); } }
            }
            if (o.Started != null) RaiseStarted(o.Started, o.StartedPid);
        }

        /// <summary>一次巡检。调用方持 _scanLock；**不**在这里触发任何对外回调。</summary>
        private StepOutcome StepLocked()
        {
            StepOutcome o = new StepOutcome();
            if (!_enabled)
            {
                o.WaitMs = Dormant ? DormantIdleIntervalMs : 1000;
                return o;
            }
            // 用档案库的版本号而不是条数：编辑既有档案的 exe 后条数不变，
            // 旧实现（Count != _sig）永远不重建目标表，守护按旧映像名匹配、永不触发。
            if (_core.Profiles.Version != _sig) RebuildTargets();

            if (_active != null)
            {
                // 已挂载：只需确认目标还活着——一次遍历即可，不再 name+alive 扫两遍。
                if (_activePid > 0)
                {
                    if (_probe == null) _probe = ProbeOne;
                    _probePid = _activePid;
                    _probeBorn = _activeBorn;
                    _probeAlive = false;
                    bool ok = _scan.ForEach(_probe);
                    // 扫描失败时保持挂载：把"扫不到"当成"进程退出"会误复原系统参数。
                    if (ok && !_probeAlive) return DetachLocked("进程退出");
                }
                else if (!string.IsNullOrEmpty(_active.Exe))
                {
                    // 手动挂载但进程还没起来（或 ForceAttach 没解析到 PID）：按映像名轮询。
                    // 旧实现在这里整段跳过，状态永不解除；后来改成"找不到就立刻解挂"，
                    // 于是"先选档案、再开游戏"会在一拍内被回滚。现在给一个有限等待窗。
                    int pid = FindPidByNameLocked(_active);
                    if (pid > 0)
                    {
                        _activePid = pid;
                        _activeBorn = _foundByNameBorn;
                        _pendingSince = 0;
                        _core.State.Post(Dirty.Daemon);
                    }
                    else
                    {
                        if (_pendingSince == 0) _pendingSince = Environment.TickCount;
                        int waited = unchecked(Environment.TickCount - _pendingSince);
                        if (waited > AttachGraceMs) return DetachLocked("目标进程未运行");
                    }
                }
                o.WaitMs = ActiveIntervalMs;
                o.Pulse = true;
                return o;
            }

            _pendingSince = 0;
            if (_names.Length > 0)
            {
                _foundPid = 0;
                _foundIdx = -1;
                _foundBorn = 0;
                // 方法组转委托每次都会新建对象：缓存一份，稳态巡检零分配。
                if (_onProcess == null) _onProcess = OnProcess;
                bool ok = _scan.ForEach(_onProcess);
                ScanCount++;
                if (ok && _foundIdx >= 0)
                {
                    GameProfile p = _profs[_foundIdx];
                    _active = p;
                    _activePid = _foundPid;
                    _activeBorn = _foundBorn;
                    o.Started = p;
                    o.StartedPid = _foundPid;
                    _core.State.Post(Dirty.Daemon);
                }
            }
            o.WaitMs = _active != null ? ActiveIntervalMs : (Dormant ? DormantIdleIntervalMs : IdleIntervalMs);
            return o;
        }

        /// <summary>锁内摘下挂载状态并生成通知；调用方在锁外派发。</summary>
        private StepOutcome DetachLocked(string reason)
        {
            StepOutcome o = new StepOutcome();
            o.Stopped = _active;
            o.StoppedPid = _activePid;
            o.StopReason = reason;
            _active = null;
            _activePid = 0;
            _activeBorn = 0;
            _pendingSince = 0;
            o.WaitMs = Dormant ? DormantIdleIntervalMs : IdleIntervalMs;
            _core.State.Post(Dirty.Daemon);
            return o;
        }

        /// <summary>解挂的对外通知（Stopped 回调 + 审计）。锁外调用。</summary>
        private void RaiseStopped(GameProfile p, int pid, string reason)
        {
            Action<GameProfile, int> h = Stopped;
            // 退出回调失败 = 系统参数没被复原（最坏情况），必须记账。
            if (h != null) { try { h(p, pid); } catch (Exception ex) { Diag.Fail("daemon.stopped", ex); } }
            _core.Engine.Ledger.AppendAudit("DAEMON detach " + (p == null ? "?" : p.Name) + " pid=" + pid + " (" + (reason == null ? "?" : reason) + ")");
        }

        private void RaiseStarted(GameProfile p, int pid)
        {
            Action<GameProfile, int> h = Started;
            // 挂载回调失败 = 游戏起来了但策略没挂上（用户只看到"没生效"），必须记账。
            if (h != null) { try { h(p, pid); } catch (Exception ex) { Diag.Fail("daemon.started", ex); } }
        }

        private int _foundPid;
        private int _foundIdx;
        private long _foundBorn;
        private ProcessVisitor _onProcess;
        private ProcessVisitor _findBorn;
        private ProcessVisitor _findByName;

        private bool OnProcess(IntPtr nameBuf, int chars, int pid, long createTime)
        {
            if (chars <= 0) return false;
            for (int i = 0; i < _names.Length; i++)
            {
                string want = _names[i];
                if (want == null || want.Length != chars) continue;
                if (!ProcessScan.NameEquals(nameBuf, chars, want)) continue;
                if (_foundIdx < 0) { _foundIdx = i; _foundPid = pid; _foundBorn = createTime; }
                return true;
            }
            return false;
        }

        private int _probePid;
        private long _probeBorn;
        private bool _probeAlive;
        private ProcessVisitor _probe;

        private bool ProbeOne(IntPtr nameBuf, int chars, int pid, long createTime)
        {
            if (pid != _probePid) return false;
            // PID 被复用：同一个号但创建时间不同 = 不是当初那个进程。
            if (_probeBorn != 0 && createTime != 0 && createTime != _probeBorn) return false;
            _probeAlive = true;
            return true;
        }

        /// <summary>立即巡检一次（托盘菜单 / 自检调用）。与巡检线程串行化共用缓冲。</summary>
        public void PollOnce()
        {
            StepOutcome o;
            lock (_scanLock) { o = StepLocked(); }
            Dispatch(o);
        }

        /// <summary>
        /// 手动挂载（托盘「档案切换」/ 离屏演示）。pid&lt;=0 时按映像名找一个正在运行的实例，
        /// 找不到就进入等待窗（不再立即解挂）。回调在锁外派发。
        /// </summary>
        public void ForceAttach(GameProfile p, int pid)
        {
            if (p == null) return;
            StepOutcome o;
            lock (_scanLock)
            {
                // 切档案时若当前已有活跃游戏，必须先走 DetachLocked 触发旧游戏的 Stopped 结算与回滚
                o = _active != null ? DetachLocked("切换档案") : new StepOutcome();
                if (pid <= 0)
                {
                    pid = FindPidByNameLocked(p);
                    _activeBorn = _foundByNameBorn;
                    _pendingSince = pid > 0 ? 0 : Environment.TickCount;
                }
                else
                {
                    // 外部直接给的 PID：必须自己解析创建时间，否则号码被系统回收后，
                    // 一个无关进程会让守护以为"游戏还在"，策略永不回滚。
                    _activeBorn = FindBornByPidLocked(pid);
                    _pendingSince = 0;
                    if (_activeBorn == 0) Diag.Hit("watcher.born.unknown");
                }
                _active = p;
                _activePid = pid;
                _core.State.Post(Dirty.Daemon);
                o.Started = p;
                o.StartedPid = pid;
            }
            Dispatch(o);
        }

        private int _bornPid;
        private long _bornFound;

        /// <summary>按 PID 查创建时间（0 = 查不到）。调用方持 _scanLock（Monitor 可重入）。</summary>
        private long FindBornByPidLocked(int pid)
        {
            if (pid <= 0) return 0;
            _bornPid = pid;
            _bornFound = 0;
            if (_findBorn == null) _findBorn = FindBorn;
            _scan.ForEach(_findBorn);
            return _bornFound;
        }

        private bool FindBorn(IntPtr nameBuf, int chars, int pid, long createTime)
        {
            if (pid != _bornPid) return false;
            _bornFound = createTime;
            return true;
        }

        private string _probeName;
        private int _foundByName;
        private long _foundByNameBorn;

        private int FindPidByNameLocked(GameProfile p)
        {
            string exe = p.Exe;
            if (string.IsNullOrEmpty(exe)) return 0;
            _probeName = Path.GetFileName(exe);
            _foundByName = 0;
            _foundByNameBorn = 0;
            if (_findByName == null) _findByName = FindByName;
            _scan.ForEach(_findByName);
            return _foundByName;
        }

        private bool FindByName(IntPtr nameBuf, int chars, int pid, long createTime)
        {
            if (ProcessScan.NameEquals(nameBuf, chars, _probeName))
            {
                _foundByName = pid;
                _foundByNameBorn = createTime;
                return true;
            }
            return false;
        }
    }
}
