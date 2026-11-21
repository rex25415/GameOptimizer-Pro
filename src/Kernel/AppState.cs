// ============================================================================
//  AppState.cs — 全局状态总线（唯一真源）+ 定时器租约
//  设计：页面不再各自持有"真相"，只订阅变化并渲染；写入口只有一个。
//
//  SPINE：让总线真的通电
//   1) 订阅带令牌：On / OnToken 返回 int 令牌，Off / OffToken 撤销。
//      lambda 无法比较，于是页面重建（Page.Invalidate）时只能靠令牌精确退订，
//      否则每换一次主题就漏一组订阅。
//   2) 合并广播：Post(Dirty) 把域累积进一个原子掩码，由 Dispatcher 以
//      Background 优先级合并成一次分发；没有 Dispatcher 可用时（自检线程）
//      退化成同步 Raise。守护引擎 1 秒内 Raise 5 次，界面只需要知道"变了"。
//   3) Raise 保持同步语义（自检依赖它），并统计每域广播次数 / 命中订阅次数，
//      由 Snapshot() 暴露给诊断（日志页 / 自检 / 压力测试）。
//   4) 线程安全：订阅表是写时复制——订阅/退订在锁内换一份新数组并发布，
//      广播只读那份引用。于是守护线程 Raise 与 UI 线程订阅不会互相撕裂。
//      计数用 Interlocked（多线程上报不丢数）；分发只发生在 UI 线程。
//
//  订阅回调里不许再 Post 同一域：那会让"派发 → 再排派发"自己喂自己。
//  回调只读状态、只写控件。
// ============================================================================
using System;
using System.Threading;
using System.Windows.Threading;

namespace Go
{
    /// <summary>可被广播的全局状态域。用位掩码，订阅一次可同时关心多个域。</summary>
    [Flags]
    public enum Dirty
    {
        None = 0,
        Timer = 1 << 0,     // NtSetTimerResolution 租约
        Profile = 1 << 1,   // 当前战术档案
        Power = 1 << 2,     // 电源 / 调度策略
        Daemon = 1 << 3,    // 守护引擎（游戏监测 / 自动挂载）
        Ledger = 1 << 4,    // 还原账本条目变化
        Tray = 1 << 5,      // 托盘存活状态
        Elevated = 1 << 6,  // 提权状态
        Target = 1 << 7,    // 当前目标进程（锁核对象）
        All = Timer | Profile | Power | Daemon | Ledger | Tray | Elevated | Target
    }

    /// <summary>
    /// 总线计数快照（诊断用）。它不是热路径：每次 Snapshot() 会复制三张小数组，
    /// 页面 / 日志页按需取一次即可，别在 Tick 里每帧取。
    /// </summary>
    public sealed class BusSnapshot
    {
        /// <summary>当前订阅数。0 = 总线没人听，广播全空转。</summary>
        public readonly int Subscribers;
        /// <summary>Post 累计调用次数。</summary>
        public readonly int Posted;
        /// <summary>被合并掉、没有单独派发的 Post 次数。</summary>
        public readonly int Coalesced;
        /// <summary>真正派发的次数（每次派发 = 一次 Raise）。</summary>
        public readonly int Dispatched;
        /// <summary>订阅回调抛出的异常数（已被总线吞下并计入 Diag）。</summary>
        public readonly int Failed;
        internal BusSnapshot(int subs, int posted, int coalesced, int dispatched, int failed)
        {
            Subscribers = subs;
            Posted = posted;
            Coalesced = coalesced;
            Dispatched = dispatched;
            Failed = failed;
        }
    }

    /// <summary>
    /// 轻量状态总线。单实例挂在 Core 上，全应用只有一个。
    /// Raise 是同步的：处理函数必须只做"把新值写进控件"，不得在其中再次广播同一域。
    /// </summary>
    public sealed class AppState
    {
        private struct Sub
        {
            public Dirty Mask;
            public Action<Dirty> Fn;
            public int Token;
        }

        // ---- 订阅表（写时复制）----------------------------------------------
        private Sub[] _live = new Sub[0];
        private readonly object _write = new object();
        private int _nextToken = 1;

        // ---- 计数（诊断标量；每域数组已移除——无消费方，徒增 Raise/Post 热路径的 Interlocked）----
        private int _postedTotal;
        private int _coalesced;
        private int _dispatched;
        private int _failed;

        // ---- 合并广播 --------------------------------------------------------
        private int _pending;      // 待派发掩码（Interlocked）
        private int _armed;        // 0/1：已排过一次 BeginInvoke
        private volatile Dispatcher _ui;
        private readonly Action _flush;
        [ThreadStatic] private static bool _inFlush;   // 同一线程上的重入闸门

        public AppState()
        {
            _flush = new Action(Flush);
            // Core 是在 UI 线程上构造的（Application 先于 Core），这里就先绑定一次：
            // 否则第一次 Post 若来自守护线程，总线会一直以为"没有调度器"，
            // 于是合并广播永远退化。自检线程上没有调度器，绑定为 null 也正合语义。
            _ui = Dispatcher.FromThread(Thread.CurrentThread);
        }

        /// <summary>当前订阅数（诊断用）。</summary>
        public int Subscribers { get { return _live.Length; } }

        // ====================================================================
        //  订阅
        // ====================================================================
        /// <summary>订阅某些域的变化。构建期调用；用 Off(fn) 注销（fn 必须是同一实例）。</summary>
        public void On(Dirty mask, Action<Dirty> fn)
        {
            OnToken(mask, fn);
        }

        /// <summary>
        /// 订阅并返回令牌。页面在重建时用 OffToken 精确退订——
        /// 这正是不用 Off(委托) 的原因：每次构建都新建 lambda，比不出同一性。
        /// </summary>
        public int OnToken(Dirty mask, Action<Dirty> fn)
        {
            if (fn == null || mask == Dirty.None) return 0;
            lock (_write)
            {
                Sub[] old = _live;
                Sub[] next = new Sub[old.Length + 1];
                Array.Copy(old, next, old.Length);
                int token = _nextToken++;
                if (_nextToken <= 0) _nextToken = 1;
                next[old.Length].Mask = mask;
                next[old.Length].Fn = fn;
                next[old.Length].Token = token;
                _live = next;                       // 发布一份完整的新表
                return token;
            }
        }

        public void Off(Action<Dirty> fn)
        {
            if (fn == null) return;
            lock (_write)
            {
                Sub[] old = _live;
                for (int i = 0; i < old.Length; i++)
                {
                    if (!object.ReferenceEquals(old[i].Fn, fn)) continue;
                    _live = RemoveAt(old, i);
                    return;
                }
            }
        }

        /// <summary>按令牌退订。返回 true 表示确实移除了一个订阅。</summary>
        public bool OffToken(int token)
        {
            if (token == 0) return false;
            lock (_write)
            {
                Sub[] old = _live;
                for (int i = 0; i < old.Length; i++)
                {
                    if (old[i].Token != token) continue;
                    _live = RemoveAt(old, i);
                    return true;
                }
                return false;
            }
        }

        private static Sub[] RemoveAt(Sub[] old, int idx)
        {
            if (old.Length <= 1) return new Sub[0];
            Sub[] next = new Sub[old.Length - 1];
            if (idx > 0) Array.Copy(old, 0, next, 0, idx);
            if (idx < old.Length - 1) Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);
            return next;
        }

        // ====================================================================
        //  广播（同步）
        // ====================================================================
        /// <summary>广播。返回命中数（调试用）；订阅回调若抛异常只计数，不打断其余订阅者。</summary>
        public int Raise(Dirty d)
        {
            if (d == Dirty.None) return 0;

            Sub[] live = _live;   // 快照引用：遍历期间新增/移除订阅不会让本次越界
            int hit = 0;
            for (int i = 0; i < live.Length; i++)
            {
                Dirty m = live[i].Mask & d;
                if (m == Dirty.None) continue;
                Action<Dirty> fn = live[i].Fn;
                if (fn == null) continue;

                hit++;

                try { fn(d); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    Diag.Fail("appstate.raise", ex);
                }
            }
            return hit;
        }

        // ====================================================================
        //  合并广播（异步）
        // ====================================================================
        /// <summary>
        /// 把域累积到待发送掩码，并用 Dispatcher.BeginInvoke（Background 优先级）
        /// 合并成一次 Raise：守护引擎 1 秒内 Raise 5 次 Dirty.Daemon，界面只收到一次。
        /// 没有 Dispatcher 可用时（自检线程 / 控制台宿主）退化成同步 Raise。
        /// 订阅回调里不要再 Post 同一域。
        /// </summary>
        public void Post(Dirty d)
        {
            if (d == Dirty.None) return;
            Interlocked.Increment(ref _postedTotal);

            OrPending((int)d);

            Dispatcher ui = Ui();
            if (ui == null) { Flush(); return; }       // 退化：同步送达
            // 调度器已经在关停：BeginInvoke 会被静默丢弃，_armed 永远停在 1，
            // 之后所有 Post 都被当"已合并"直接 return —— 总线静音。
            // 与 DaemonHost.Post 的闸门保持同一条语义。
            if (ui.HasShutdownStarted || ui.HasShutdownFinished) return;

            if (Interlocked.CompareExchange(ref _armed, 1, 0) != 0)
            {
                Interlocked.Increment(ref _coalesced); // 已排过一次，本次只并进掩码
                return;
            }
            try { ui.BeginInvoke(DispatcherPriority.Background, _flush); }
            catch (Exception ex)
            {
                // 排不进调度器：只清闸门，不在后台线程就地 Flush。
                // 就地 Flush 会让订阅回调在非 UI 线程写控件（跨线程异常/未定义行为），
                // 而"这一拍没送达"远好过"在错误的线程上送达"。
                Interlocked.Exchange(ref _armed, 0);
                Diag.Fail("appstate.post", ex);
            }
        }

        /// <summary>派发一次累积的掩码。只在 UI 线程或退化路径上执行。</summary>
        private void Flush()
        {
            Interlocked.Exchange(ref _armed, 0);
            int mask = Interlocked.Exchange(ref _pending, 0);
            if (mask == 0) return;

            // 订阅回调里再次 Post 同一域会让"派发 → 再排派发"自己喂自己。
            // 契约上不允许，但退化路径（没有 Dispatcher 的自检线程）里那就是同步无限递归。
            // 这里把域放回待派发掩码并直接返回：下一次 Post 会一起带走。
            if (_inFlush)
            {
                OrPending(mask);
                Diag.Hit("appstate.reentrant");
                return;
            }

            _inFlush = true;
            try
            {
                Interlocked.Increment(ref _dispatched);
                Raise((Dirty)mask);
            }
            finally { _inFlush = false; }

            // 派发期间又被 Post 的域：补排一次。Background 优先级保证消息泵
            // 仍有输入/渲染的机会，不会因为"派发 → 再排派发"锁死界面。
            // 没有 Dispatcher（自检线程）时不补排：那会变成同步递归，
            // 剩下的掩码留给下一次 Post 一起带走。
            if (_ui != null && Volatile.Read(ref _pending) != 0) Arm();
        }

        private void Arm()
        {
            Dispatcher ui = _ui;
            if (ui == null) return;
            if (ui.HasShutdownStarted || ui.HasShutdownFinished) return;
            if (Interlocked.CompareExchange(ref _armed, 1, 0) != 0) return;
            try { ui.BeginInvoke(DispatcherPriority.Background, _flush); }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _armed, 0);
                Diag.Fail("appstate.post", ex);
            }
        }

        private void OrPending(int bits)
        {
            int cur = _pending;
            while (true)
            {
                int next = cur | bits;
                int seen = Interlocked.CompareExchange(ref _pending, next, cur);
                if (seen == cur) return;
                cur = seen;
            }
        }

        /// <summary>
        /// 绑定 UI 调度器：优先用已绑定的那份；否则看当前线程有没有调度器。
        /// 采样线程 / 守护线程上没有调度器，返回 null → Post 退化成同步 Raise。
        /// </summary>
        private Dispatcher Ui()
        {
            Dispatcher d = _ui;
            if (d != null) return d;
            d = Dispatcher.FromThread(Thread.CurrentThread);
            if (d == null) return null;
            _ui = d;
            return d;
        }

        // ====================================================================
        //  诊断
        // ====================================================================
        /// <summary>
        /// 计数快照。诊断用，允许分配；不要放进每帧路径。
        /// 计数在并发上报时可能相差一两个（复制数组不加锁），对诊断没有影响。
        /// </summary>
        public BusSnapshot Snapshot()
        {
            return new BusSnapshot(_live.Length,
                Volatile.Read(ref _postedTotal), Volatile.Read(ref _coalesced), Volatile.Read(ref _dispatched),
                Volatile.Read(ref _failed));
        }
    }

    /// <summary>定时器租约的持有者。按位组合，可同时被多个模块持有。</summary>
    [Flags]
    public enum TimerOwner
    {
        None = 0,
        Overview = 1 << 0,
        Scheduler = 1 << 1,
        Benchmark = 1 << 2,
        Profile = 1 << 3,
        Daemon = 1 << 4,
        SelfTest = 1 << 5,
        GameMode = 1 << 6
    }

    /// <summary>
    /// 0.5ms 精确定时的唯一持有者。
    /// 旧实现里三个页面各自 Request / Release：A 与 B 都请求后，A 释放会把 B 的请求一起撤掉，
    /// 于是界面开关和系统实际精度对不上。这里改成引用计数——最后一个持有者退场才真正释放。
    ///
    /// 广播走 Post(Dirty.Timer)：租约可能由电池切换（守护线程）改动，
    /// 合并一次通知比"每改一次就叫醒全部订阅者"更稳；一帧之内的连续请求只算一次。
    /// </summary>
    public sealed class TimerService
    {
        private readonly PerfSampler _perf;
        private readonly AppState _state;
        private TimerOwner _owners;
        private double _grantedMs;
        private double _requestedMs;
        private readonly System.Collections.Generic.Dictionary<TimerOwner, double> _ownerReq
            = new System.Collections.Generic.Dictionary<TimerOwner, double>(8);
        // 租约是"读-改-写"的位域：UI 线程（开关）与 go.watcher（守护挂载）会并发进出。
        // 不加锁时两边的读-改-写会互相覆盖：可能 +1 了两次只 -1 一次（进程直到退出都钉在
        // 0.5ms，收进托盘也在耗电），也可能把守护刚申请的租约当成自己这次释放掉。
        private readonly object _leaseLock = new object();

        public TimerService(PerfSampler perf, AppState state)
        {
            _perf = perf;
            _state = state;
        }

        public bool Active { get { lock (_leaseLock) { return _owners != TimerOwner.None; } } }
        public double GrantedMs { get { lock (_leaseLock) { return _grantedMs; } } }
        public double RequestedMs { get { lock (_leaseLock) { return _requestedMs; } } }

        /// <summary>请求精度。重复请求同一 owner 是幂等的；首次请求或要求更细粒度（ms &lt; _requestedMs）时真正下发。</summary>
        public bool Request(TimerOwner owner, double ms)
        {
            if (owner == TimerOwner.None || ms <= 0) return false;
            bool ok;
            lock (_leaseLock)
            {
                TimerOwner prevOwners = _owners;
                double prevOwnerMs;
                bool hadOwner = _ownerReq.TryGetValue(owner, out prevOwnerMs);
                bool first = prevOwners == TimerOwner.None;
                bool tighter = first || ms < _requestedMs;

                _owners |= owner;
                _ownerReq[owner] = ms;
                ok = true;
                if (tighter)
                {
                    double got = _perf.RequestTimerResolution(ms);
                    if (got <= 0)
                    {
                        _owners = prevOwners;
                        if (hadOwner) _ownerReq[owner] = prevOwnerMs;
                        else _ownerReq.Remove(owner);
                        ok = false;
                    }
                    else
                    {
                        _requestedMs = ms;
                        _grantedMs = got;
                    }
                }
            }
            _state.Post(Dirty.Timer);
            return ok;
        }

        /// <summary>释放指定持有者。只有全部持有者退场才真正恢复系统默认。</summary>
        public void Release(TimerOwner owner)
        {
            Clear(owner);
        }

        /// <summary>面向用户的三个入口（总览 / 调度 / 档案）。</summary>
        public const TimerOwner UserBits = TimerOwner.Overview | TimerOwner.Scheduler | TimerOwner.Profile;

        /// <summary>
        /// 界面上那个开关的唯一写入入口：
        /// 打开时挂上自己的租约；关闭时清掉全部"用户位"，
        /// 但不碰 Benchmark / Daemon 这类临时内部租约（它们不代表用户的意图）。
        /// </summary>
        public void Set(TimerOwner owner, bool on, double ms)
        {
            if (on) Request(owner, ms);
            else Clear(UserBits);
        }

        /// <summary>清除一组持有者。全部清空时恢复系统默认；仍有剩余持有者时按其最细请求重算。</summary>
        public void Clear(TimerOwner mask)
        {
            lock (_leaseLock)
            {
                TimerOwner hit = _owners & mask;
                if (hit == TimerOwner.None) return;
                _owners &= ~mask;
                if (mask != TimerOwner.None)
                {
                    foreach (TimerOwner key in new System.Collections.Generic.List<TimerOwner>(_ownerReq.Keys))
                    {
                        if ((key & mask) != TimerOwner.None) _ownerReq.Remove(key);
                    }
                }
                if (_owners == TimerOwner.None)
                {
                    _perf.ReleaseTimerResolution();
                    _grantedMs = 0;
                    _requestedMs = 0;
                }
                else
                {
                    double minMs = double.MaxValue;
                    foreach (double req in _ownerReq.Values)
                    {
                        if (req > 0 && req < minMs) minMs = req;
                    }
                    if (minMs < double.MaxValue && minMs > _requestedMs)
                    {
                        double got = _perf.RequestTimerResolution(minMs);
                        if (got > 0)
                        {
                            _requestedMs = minMs;
                            _grantedMs = got;
                        }
                    }
                }
            }
            _state.Post(Dirty.Timer);
        }
    }
}
