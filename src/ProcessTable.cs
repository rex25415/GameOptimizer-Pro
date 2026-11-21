// ============================================================================
//  ProcessTable.cs — 全应用共享的进程表快照（后端数据服务）
//
//  为什么要有它：进程页每 2 秒用 Process.GetProcesses() 重扫一次，实测 28.6ms/次、
//  432 个 Process 对象、约 1300 次/秒的句柄往返 —— 这是整个应用最大的单点常驻开销，
//  而它读到的每个字段（PID / 映像名 / CPU 累计 / 工作集 / 线程数）在
//  NtQuerySystemInformation 的一次遍历里全部都有。
//
//  设计：
//   · 唯一数据源：一次非托管遍历（Daemon/ProcessScan），不打开任何进程句柄；
//   · 双缓冲发布：读者拿到的数组在两拍之内不会被覆写（同 Perf 的三缓冲协议）；
//   · 名字复用：同一个 (pid, 创建时间) 的进程名直接复用上一代的字符串，
//     稳态下这份服务每拍只分配一个很小的快照对象；
//   · 新鲜度策略 Get(maxAgeMs)：调用方声明"能接受多旧的数据"。页面 1.5s、
//     需要即时读数的路径传 0 —— 共享的是扫描，不是陈旧。
//   · 失败不伪造：扫描失败时 Ok=false + Error，读者显示 — 并沿用上一份数据，
//     绝不把"没读到"说成"没有进程"。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Go.Daemon;

namespace Go
{
    /// <summary>进程表的一行（对外只读）。</summary>
    public sealed class ProcEntry
    {
        public int Pid;
        public long Born;
        /// <summary>映像名，含扩展名（匹配游戏 exe 用这个）。</summary>
        public string Image;
        /// <summary>显示名，去掉 .exe（与 Process.ProcessName 同口径）。</summary>
        public string Name;
        /// <summary>User + Kernel 累计，100ns 刻度。</summary>
        public long CpuTicks;
        /// <summary>工作集字节；0 = 本次没读到。</summary>
        public long WorkingSetBytes;
        /// <summary>线程数；-1 = 读不到。</summary>
        public int Threads;
        /// <summary>缓存的 Win32 优先级类别与排序秩/中文徽标（高/正常/低于/空闲）。</summary>
        public uint PriorityClass;
        public int PriorityRank;
        public string PriorityLabel = "\u2014";
        /// <summary>缓存的亲和性掩码；0 = 尚未读取或无权读取。</summary>
        public long AffinityMask;
        /// <summary>缓存的 Win11 EcoQoS 状态与可读标志。</summary>
        public bool EcoQoS;
        public bool EcoQoSKnown;
        public bool DetailsSynced;
        internal int LastBasePriority;
    }

    /// <summary>一代进程表快照。Items 只读，Count 之前的条目有效。</summary>
    public sealed class ProcSnapshot
    {
        public readonly ProcEntry[] Items;
        public readonly int Count;
        public readonly int Version;
        public readonly bool Ok;
        public readonly string Error;
        /// <summary>这一代数据有多旧（毫秒）。</summary>
        public readonly int AgeMs;
        /// <summary>采集这一代时的 Environment.TickCount。CPU 差分用它算真实窗口，
        /// 不拿"页面两次刷新的墙钟差"冒充采样间隔。</summary>
        public readonly int Tick;
        /// <summary>采集这一代花的时间（微秒）。</summary>
        public readonly long ScanUs;

        internal ProcSnapshot(ProcEntry[] items, int count, int version, bool ok, string error, int ageMs, int tick, long scanUs)
        {
            Items = items;
            Count = count;
            Version = version;
            Ok = ok;
            Error = error;
            AgeMs = ageMs;
            Tick = tick;
            ScanUs = scanUs;
        }
    }

    /// <summary>共享进程表。线程安全：内部一把锁串行化扫描，读者只拿已发布的数组。</summary>
    public sealed class ProcessTable : IDisposable
    {
        private const int InitialSlots = 512;

        private readonly ProcessScan _scan = new ProcessScan();
        private readonly object _gate = new object();

        // 双缓冲：_pub 指向哪一份，下一拍就写另一份。每份缓冲独立持有自己的 ProcEntry 实例集与缓存。
        private ProcEntry[] _a = new ProcEntry[0];
        private ProcEntry[] _b = new ProcEntry[0];
        private int _countA;
        private int _countB;
        private Dictionary<int, ProcEntry> _cacheA = new Dictionary<int, ProcEntry>(InitialSlots);
        private Dictionary<int, ProcEntry> _cacheB = new Dictionary<int, ProcEntry>(InitialSlots);
        private Dictionary<int, ProcEntry> _staging = new Dictionary<int, ProcEntry>(InitialSlots);
        private ProcEntry[] _pub;
        private int _pubCount;
        private int _version;

        private int _lastScanTick = int.MinValue / 2;
        private long _lastScanUs;
        private bool _lastOk = true;
        private string _lastError;
        private int _scans;
        private int _failures;
        private long _totalUs;
        private long _maxUs;
        private readonly ProcessRowVisitor _visitor;

        public ProcessTable()
        {
            _visitor = new ProcessRowVisitor(Collect);
        }

        // ---- 诊断读数 --------------------------------------------------------
        public int Scans { get { lock (_gate) { return _scans; } } }
        public int Failures { get { lock (_gate) { return _failures; } } }
        public long LastScanUs { get { lock (_gate) { return _lastScanUs; } } }
        public long MaxScanUs { get { lock (_gate) { return _maxUs; } } }
        public long AverageScanUs
        {
            get
            {
                lock (_gate) { return _scans == 0 ? 0 : _totalUs / _scans; }
            }
        }
        public string LastError { get { lock (_gate) { return _lastError; } } }

        /// <summary>当前已发布的快照（可能很旧；需要新鲜数据请用 Get）。</summary>
        public ProcSnapshot Current
        {
            get { lock (_gate) { return MakeSnapshot(); } }
        }

        /// <summary>
        /// 取一份快照。maxAgeMs 之内已有数据就直接复用，否则重扫。
        /// 扫描是串行的：两个线程同时要新数据只会真正扫一次。
        /// </summary>
        public ProcSnapshot Get(int maxAgeMs)
        {
            lock (_gate)
            {
                int age = AgeLocked();
                if (_pub != null && age >= 0 && age <= maxAgeMs) return MakeSnapshot();
                RefreshLocked();
                return MakeSnapshot();
            }
        }

        /// <summary>强制重扫一代。</summary>
        public ProcSnapshot Refresh()
        {
            lock (_gate)
            {
                RefreshLocked();
                return MakeSnapshot();
            }
        }

        private int AgeLocked()
        {
            if (_lastScanTick == int.MinValue / 2) return int.MaxValue;
            return unchecked(Environment.TickCount - _lastScanTick);
        }

        private ProcSnapshot MakeSnapshot()
        {
            if (_pub == null)
            {
                return new ProcSnapshot(new ProcEntry[0], 0, _version, false, "尚未采集", int.MaxValue, 0, 0);
            }
            return new ProcSnapshot(_pub, _pubCount, _version, _lastOk, _lastError, AgeLocked(), _lastScanTick, _lastScanUs);
        }

        private void RefreshLocked()
        {
            // 目标缓冲与它自己的实例缓存一起轮转：绝不触碰 _pub 正在向读者暴露的实例。
            bool writeB = ReferenceEquals(_pub, _a);
            ProcEntry[] target = writeB ? _b : _a;
            Dictionary<int, ProcEntry> cache = writeB ? _cacheB : _cacheA;
            Dictionary<int, ProcEntry> peer = writeB ? _cacheA : _cacheB;
            if (target.Length == 0) target = new ProcEntry[InitialSlots];
            _staging.Clear();

            _rows = 0;
            _target = target;
            _cache = cache;
            _peer = peer;
            _next = _staging;

            long t0 = Stopwatch.GetTimestamp();
            bool ok;
            try
            {
                ok = _scan.ForEachRow(_visitor);
            }
            catch (Exception ex)
            {
                // 扫描器自身抛异常只降级这一代：上一份数据继续发布，并让读者看到 Ok=false。
                Diag.Fail("proctable.scan", ex);
                _lastError = ex.GetType().Name + ": " + ex.Message;
                ok = false;
            }
            long us = (Stopwatch.GetTimestamp() - t0) * 1000000L / Stopwatch.Frequency;

            _scans++;
            _lastScanUs = us;
            _totalUs += us;
            if (us > _maxUs) _maxUs = us;

            if (!ok)
            {
                _failures++;
                _lastOk = false;
                if (_lastError == null) _lastError = _scan.LastError == null ? "进程表扫描失败" : _scan.LastError;
                _staging.Clear();
                _target = null; _cache = null; _peer = null; _next = null;
                return;
            }

            int prevCount = writeB ? _countB : _countA;
            if (_rows < prevCount) Array.Clear(_target, _rows, prevCount - _rows);

            // 把扩容后的数组与最新实例映射写回目标槽位，原目标槽旧映射转为下轮 _staging。
            if (writeB)
            {
                _b = _target;
                _countB = _rows;
                _cacheB = _next;
                _staging = cache;
            }
            else
            {
                _a = _target;
                _countA = _rows;
                _cacheA = _next;
                _staging = cache;
            }
            _staging.Clear();

            _pub = _target;
            _pubCount = _rows;
            _version++;
            _lastOk = true;
            _lastError = null;
            _lastScanTick = Environment.TickCount;
            _target = null; _cache = null; _peer = null; _next = null;
        }

        // ---- 扫描回调（只有采样线程在锁内调用）--------------------------------
        private ProcEntry[] _target;
        private Dictionary<int, ProcEntry> _cache;
        private Dictionary<int, ProcEntry> _peer;
        private Dictionary<int, ProcEntry> _next;
        private int _rows;

        private bool Collect(ProcessRow row)
        {
            ProcEntry e;
            if (!_cache.TryGetValue(row.Pid, out e) || e.Born != row.CreateTime)
            {
                e = new ProcEntry();
                e.Pid = row.Pid;
                e.Born = row.CreateTime;

                ProcEntry peer;
                if (_peer.TryGetValue(row.Pid, out peer) && peer.Born == row.CreateTime)
                {
                    e.Image = peer.Image;
                    e.Name = peer.Name;
                    if (peer.DetailsSynced) CopySyncedDetails(peer, e);
                }
                else
                {
                    string image = row.Name();
                    e.Image = image;
                    e.Name = StripExe(image);
                }
            }
            else if (!e.DetailsSynced)
            {
                ProcEntry peer;
                if (_peer.TryGetValue(row.Pid, out peer) && peer.Born == row.CreateTime && peer.DetailsSynced)
                    CopySyncedDetails(peer, e);
            }
            e.CpuTicks = row.CpuTicks;
            e.WorkingSetBytes = row.WorkingSetBytes;
            e.Threads = row.Threads;
            if (row.BasePriority > 0 && row.BasePriority != e.LastBasePriority)
            {
                e.LastBasePriority = row.BasePriority;
                uint cls = ProcessPower.BasePriorityToClass(row.BasePriority);
                if (cls != 0)
                {
                    e.PriorityClass = cls;
                    e.PriorityRank = ProcessPower.PriorityRank(cls);
                    e.PriorityLabel = ProcessPower.PriorityLabel(cls);
                }
            }

            if (_rows >= _target.Length)
            {
                int cap = _target.Length <= 0 ? InitialSlots : _target.Length * 2;
                Array.Resize(ref _target, cap);
            }
            _target[_rows] = e;
            _rows++;
            _next[e.Pid] = e;
            return false;
        }

        private static void CopySyncedDetails(ProcEntry src, ProcEntry dst)
        {
            dst.PriorityClass = src.PriorityClass;
            dst.PriorityRank = src.PriorityRank;
            dst.PriorityLabel = src.PriorityLabel;
            dst.AffinityMask = src.AffinityMask;
            dst.EcoQoS = src.EcoQoS;
            dst.EcoQoSKnown = src.EcoQoSKnown;
            dst.DetailsSynced = true;
            dst.LastBasePriority = src.LastBasePriority;
        }

        /// <summary>按需读取并缓存单行亲和性 / EcoQoS / 精确优先级，同步双缓冲两份条目。</summary>
        public bool EnrichRow(ProcEntry row)
        {
            if (row == null || row.Pid <= 0) return false;
            uint cls;
            long mask;
            bool ecoKnown, eco;
            bool ok = ProcessPower.ReadPowerAndAffinity(row.Pid, out cls, out mask, out ecoKnown, out eco);
            lock (_gate)
            {
                ApplyDetails(row, cls, mask, ecoKnown, eco);
                SyncPeerDetails(row.Pid, row.Born, cls, mask, ecoKnown, eco);
            }
            return ok;
        }

        public int EnrichRange(ProcSnapshot snap, int start, int count)
        {
            if (snap == null || snap.Items == null || count <= 0) return 0;
            int end = Math.Min(snap.Count, Math.Max(0, start) + count);
            int hits = 0;
            for (int i = Math.Max(0, start); i < end; i++)
            {
                if (EnrichRow(snap.Items[i])) hits++;
            }
            return hits;
        }

        public bool SetRowPriority(int pid, uint priorityClass, out string err)
        {
            return SetRowPriority(pid, priorityClass, false, out err);
        }

        public bool SetRowPriority(int pid, uint priorityClass, bool track, out string err)
        {
            if (!ProcessPower.SetPriority(pid, priorityClass, track, out err)) return false;
            lock (_gate)
            {
                UpdateCachedPriority(_cacheA, pid, priorityClass);
                UpdateCachedPriority(_cacheB, pid, priorityClass);
            }
            return true;
        }

        public bool SetRowEcoQoS(int pid, bool enable, out string err)
        {
            return SetRowEcoQoS(pid, enable, false, out err);
        }

        public bool SetRowEcoQoS(int pid, bool enable, bool track, out string err)
        {
            if (!ProcessPower.SetEcoQoS(pid, enable, track, out err)) return false;
            lock (_gate)
            {
                UpdateCachedEco(_cacheA, pid, enable);
                UpdateCachedEco(_cacheB, pid, enable);
            }
            return true;
        }

        public bool SetRowAffinity(int pid, long mask, out string err)
        {
            if (!Affinity.Apply(pid, mask, out err)) return false;
            lock (_gate)
            {
                UpdateCachedAffinity(_cacheA, pid, mask);
                UpdateCachedAffinity(_cacheB, pid, mask);
            }
            return true;
        }

        private static void ApplyDetails(ProcEntry e, uint cls, long mask, bool ecoKnown, bool eco)
        {
            if (cls != 0)
            {
                e.PriorityClass = cls;
                e.PriorityRank = ProcessPower.PriorityRank(cls);
                e.PriorityLabel = ProcessPower.PriorityLabel(cls);
            }
            if (mask != 0) e.AffinityMask = mask;
            if (ecoKnown)
            {
                e.EcoQoSKnown = true;
                e.EcoQoS = eco;
            }
            e.DetailsSynced = true;
        }

        private void SyncPeerDetails(int pid, long born, uint cls, long mask, bool ecoKnown, bool eco)
        {
            ProcEntry a, b;
            if (_cacheA.TryGetValue(pid, out a) && a.Born == born) ApplyDetails(a, cls, mask, ecoKnown, eco);
            if (_cacheB.TryGetValue(pid, out b) && b.Born == born) ApplyDetails(b, cls, mask, ecoKnown, eco);
        }

        private static void UpdateCachedPriority(Dictionary<int, ProcEntry> map, int pid, uint cls)
        {
            ProcEntry e;
            if (!map.TryGetValue(pid, out e)) return;
            e.PriorityClass = cls;
            e.PriorityRank = ProcessPower.PriorityRank(cls);
            e.PriorityLabel = ProcessPower.PriorityLabel(cls);
            e.DetailsSynced = true;
        }

        private static void UpdateCachedEco(Dictionary<int, ProcEntry> map, int pid, bool eco)
        {
            ProcEntry e;
            if (!map.TryGetValue(pid, out e)) return;
            e.EcoQoSKnown = true;
            e.EcoQoS = eco;
            e.DetailsSynced = true;
        }

        private static void UpdateCachedAffinity(Dictionary<int, ProcEntry> map, int pid, long mask)
        {
            ProcEntry e;
            if (!map.TryGetValue(pid, out e)) return;
            e.AffinityMask = mask;
            e.DetailsSynced = true;
        }

        private static string StripExe(string image)
        {
            if (string.IsNullOrEmpty(image)) return "";
            if (image.Length > 4 && image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return image.Substring(0, image.Length - 4);
            return image;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _scan.Dispose();
                _pub = null;
                _pubCount = 0;
                _countA = 0;
                _countB = 0;
                _cacheA.Clear();
                _cacheB.Clear();
                _staging.Clear();
                _a = new ProcEntry[0];
                _b = new ProcEntry[0];
            }
        }
    }
}
