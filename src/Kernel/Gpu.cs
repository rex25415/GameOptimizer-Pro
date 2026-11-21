// ============================================================================
//  Gpu.cs — GPU 真实占用 / 显存采样（PDH "GPU Engine" / "GPU Process Memory"）
//
//  红线：绝不伪造读数。计数器不存在（Win7 / 无 WDDM2 驱动）、预热中、采集失败
//  一律 Available=false，界面显示 —。温度无通用用户态 API，本工具不采集。
//
//  口径（与任务管理器 / Steam 叠加一致）：整卡占用 = 各 (luid,eng,engtype) 引擎
//  跨进程求和后取**最大值**，不是全部相加（3D+Copy+Decode 并行会 >100%，属伪造）。
//
//  线程与生命周期（Rev86 重写）：
//   · 专用后台线程独占 PDH 句柄（PDH 查询是非线程安全的）；节拍由 SampleClock
//     统一排程，Stop() 用 WaitHandle 立刻打断，不再"最坏等 1 秒"。
//   · 句柄所有权只在一个地方：PdhQuery。Open 失败、Add 失败、Collect 连续失效、
//     采样抛异常、线程退出——所有路径都穿过 q.Close()，Close 幂等。
//   · 打开失败 / 计数器失效都进 SampleHealth 与退避表：哪层、什么码、几秒后再试。
//     旧实现用 "retryBeat % 30" 这种拍数取模表达退避，读代码的人算不出下一次尝试。
//   · 快照三缓冲发布（与 Perf 同一套协议）：每拍不再 new GpuSnapshot，读者永远
//     只看到已发布的那一份。Dictionary 复用，实例名解析结果跨拍缓存。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Go
{
    public sealed class GpuSampler
    {
        private const int GpuIntervalMs = 1000;
        private const int OpenBackoffMinMs = 5000;
        private const int OpenBackoffMaxMs = 60000;
        private const int RebuildAfterBadBeats = 3;
        private const string UtilPath = @"\GPU Engine(*)\Utilization Percentage";
        private const string MemPath = @"\GPU Process Memory(*)\Local Usage";
        private const string AdapterMemPath = @"\GPU Adapter Memory(*)\Dedicated Usage";

        private const byte CatOther = 0;
        private const byte Cat3D = 1;
        private const byte CatCompute = 2;
        private const byte CatCopy = 3;

        private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
        private const int PDH_NO_DATA = unchecked((int)0x800007D5);
        private const uint PDH_FMT_DOUBLE = 0x200;
        private const uint PDH_FMT_LARGE = 0x400;

        // 健康账簿的层号（与 _health 的构造顺序一致）。
        private const int LayerOpen = 0;
        private const int LayerUtil = 1;
        private const int LayerMem = 2;
        private const int LayerCollect = 3;
        private const int LayerRead = 4;

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct PdhValue
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double Double;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint fmt, ref uint bufSize, out uint itemCount, IntPtr buf);
        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern int PdhCloseQuery(IntPtr query);

        // ---- 线程与时钟 ------------------------------------------------------
        private Thread _th;
        private SampleClock _clock;

        // ---- 快照三缓冲（只有采样线程写，发布换引用）--------------------------
        private volatile GpuSnapshot _pub = new GpuSnapshot();
        private GpuSnapshot _write = new GpuSnapshot();
        private GpuSnapshot _spare = new GpuSnapshot();

        // ---- 复用容器（仅 GPU 线程访问，无需锁）------------------------------
        private readonly Dictionary<string, double> _sums = new Dictionary<string, double>(64);
        private readonly Dictionary<string, EngineMeta> _keyMeta = new Dictionary<string, EngineMeta>(64);
        private readonly UniPtrCache<EngineMeta> _utilCache = new UniPtrCache<EngineMeta>();
        private readonly UniPtrCache<int> _memCache = new UniPtrCache<int>();

        private struct EngineMeta
        {
            public string Key;
            public string Kind;
            public int Slot;
            public byte Cat;
        }

        private sealed class AdapterMeta
        {
            public string Luid = "";
            public string ShortName = "";
            public string DriverDesc = "";
            public string DriverVersion = "";
            public bool Discrete;
            public long VramTotalBytes;
        }

        private readonly AdapterMeta[] _adapters = new AdapterMeta[GpuSnapshot.MaxAdapters];
        private int _adapterCount;
        private bool _adaptersSeeded;
        private readonly Dictionary<string, int> _luidToSlot = new Dictionary<string, int>(8, StringComparer.OrdinalIgnoreCase);
        private readonly long[] _procLuidMem = new long[GpuSnapshot.MaxAdapters];
        private readonly bool[] _procLuidMemKnown = new bool[GpuSnapshot.MaxAdapters];
        private IntPtr _pdhBuf = IntPtr.Zero;
        private int _pdhBufCap;

        // ---- 退避（只有 GPU 线程碰）------------------------------------------
        private int _openBackoff;
        private int _nextOpen;       // Environment.TickCount 时刻；0 = 立即

        /// <summary>托盘待机时置真：采样线程空转休眠，幽灵般近零开销。</summary>
        public volatile bool Paused;

        private readonly SampleHealthBoard _health = new SampleHealthBoard(new string[]
        {
            "gpu.open", "gpu.util", "gpu.mem", "gpu.collect", "gpu.read"
        });

        /// <summary>PDH 各层的健康度：打开/加计数器/采集/读缓冲，哪层失败、何时重试。</summary>
        public SampleHealthBoard Health { get { return _health; } }

        public GpuSnapshot Current { get { return _pub; } }

        // ====================================================================
        //  生命周期
        // ====================================================================
        public void Start()
        {
            if (_th != null && _th.IsAlive) return;   // 幂等
            if (_th != null)
            {
                JoinQuiet(_th);
                if (_th.IsAlive)
                {
                    // 上一轮还没退干净（Stop 之后 500ms 仍未结束）：拒绝再叠一条采样线程。
                    Diag.Hit("gpu.start.busy");
                    return;
                }
                _th = null;
            }

            // 结构体偏移断言：union 因 double 按 8 对齐，x86/x64 均为 16 字节。
            if (Marshal.SizeOf(typeof(PdhValue)) != 16)
            {
                // 平台/布局不支持不是"驱动不支持"：把它记进健康账簿，诊断面板才能给出正确归因。
                Diag.Hit("gpu.platform.layout");
                _health.At(LayerOpen).Fail(0, SampleHealth.StageParse, 0);
                return;
            }
            // 计数器数组的 item stride（IntPtr.Size + 16）与字段偏移按 x64 校验。
            // x86 下 double 的对齐会改变 item 布局，照 x64 偏移读会读出假数乃至野指针——
            // 宁可显示 —（不可用），绝不伪造占用。本工具的目标平台是 x64。
            if (IntPtr.Size != 8)
            {
                Diag.Hit("gpu.platform.x86");
                _health.At(LayerOpen).Fail(0, SampleHealth.StageParse, 0);
                return;
            }

            SampleClock old = _clock;
            _clock = new SampleClock();
            if (old != null) old.Dispose();     // 上一轮的线程已确认退出，句柄可以安全释放

            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "go.gpu";
            _th = t;
            t.Start();
        }

        public void Stop()
        {
            SampleClock c = _clock;
            if (c != null)
            {
                try { c.Stop(); }
                catch (ObjectDisposedException) { /* 已停过 */ }
            }
            Thread t = _th;
            JoinQuiet(t);
            // 线程确认死了才释放时钟：还在 WaitAny 里的线程拿到已释放的句柄会抛异常。
            if (t == null || !t.IsAlive)
            {
                _clock = null;
                if (c != null) c.Dispose();
            }
        }

        private static void JoinQuiet(Thread t)
        {
            if (t == null || !t.IsAlive) return;
            try { t.Join(500); }
            catch (ThreadStateException) { /* 从未启动 */ }
            catch (Exception ex) { Diag.Fail("gpu.stop.join", ex); }
        }

        // ====================================================================
        //  采样循环
        // ====================================================================
        private void Loop()
        {
            SampleClock clock = _clock;
            PdhQuery q = new PdhQuery();
            int badBeats = 0;
            bool wasPaused = false;
            try
            {
                Run(clock, q, ref badBeats, ref wasPaused);
            }
            catch (Exception ex)
            {
                // 与 Perf.Loop 同一条铁律：后台线程上的未处理异常会终止整个进程。
                // Run 内层只兜"一拍"，OnBeatError 本身抛异常（Diag/PDH/发布任一环）
                // 仍会逃出 while——这里必须是最后一道网。
                Diag.Fail("gpu.loop", ex);
                RecordGpuFail(LayerCollect, 0, SampleHealth.StageException, GpuIntervalMs);
                PublishUnavailable();
            }
            finally
            {
                q.Close();      // 唯一的句柄出口：正常退出、异常退出都从这里走
                FreeScratchBuf();
                _th = null;
            }
        }

        private void Run(SampleClock clock, PdhQuery q, ref int badBeats, ref bool wasPaused)
        {
            while (clock.WaitTurn(GpuIntervalMs))
            {
                try
                {
                    Beat(q, ref badBeats, ref wasPaused);
                }
                catch (Exception ex)
                {
                    OnBeatError(q, ex, ref badBeats);
                }
                clock.Done(GpuIntervalMs);
            }
        }

        private void Beat(PdhQuery q, ref int badBeats, ref bool wasPaused)
        {
            // 休眠先判：旧实现先 Collect 再判 Paused，收托盘后全实例采集反而
            // 从 1Hz 变 2Hz，"近零开销"名不副实。
            if (Paused) wasPaused = true;
            else if (!q.Opened) OpenOrRetry(q);
            else SampleOnce(q, ref badBeats, ref wasPaused);
        }

        /// <summary>一拍里的托管异常：只降级这一拍，句柄立刻关掉，重新打开走退避表。</summary>
        private void OnBeatError(PdhQuery q, Exception ex, ref int badBeats)
        {
            Diag.Fail("gpu.sample", ex);
            RecordGpuFail(LayerCollect, 0, SampleHealth.StageException, GpuIntervalMs);
            PublishUnavailable();
            q.Close();
            badBeats = 0;
            int backoff = NextBackoff(ref _openBackoff);
            _nextOpen = unchecked(Environment.TickCount + backoff);
        }

        private void OpenOrRetry(PdhQuery q)
        {
            if (!RetryDue(_nextOpen)) return;

            int layer, code;
            if (q.Open(out layer, out code))
            {
                _openBackoff = 0;
                _nextOpen = 0;
                // 速率型计数器要先建立基线：不采这一下，下一拍格式化出来的就是
                // PDH_CSTATUS_INVALID_DATA（实测 0xC0000BBA），界面会闪一次 —。
                // 旧实现是"open 后 collect 一次再丢弃"，职责在这里，不在采样循环里。
                int baseStatus = PdhCollectQueryData(q.Query);
                if (baseStatus != 0 && baseStatus != PDH_NO_DATA)
                    RecordGpuFail(LayerCollect, baseStatus, SampleHealth.StageQuery, GpuIntervalMs);
                else _health.At(LayerCollect).Ok();
                _health.At(LayerUtil).Ok();
                _health.At(LayerOpen).Ok();
                if (q.MemCode != 0)
                {
                    RecordGpuFail(LayerMem, q.MemCode, SampleHealth.StageOpen, 0);
                }
                else _health.At(LayerMem).Ok();
                return;
            }

            int backoff = NextBackoff(ref _openBackoff);
            _nextOpen = unchecked(Environment.TickCount + backoff);
            RecordGpuFail(layer, code, SampleHealth.StageOpen, backoff);
            PublishUnavailable();
        }

        private void SampleOnce(PdhQuery q, ref int badBeats, ref bool wasPaused)
        {
            int st = PdhCollectQueryData(q.Query);
            if (st != 0 && st != PDH_NO_DATA)
            {
                badBeats++;
                PublishUnavailable();
                RecordGpuFail(LayerCollect, st, SampleHealth.StageQuery, GpuIntervalMs);
                if (badBeats < RebuildAfterBadBeats) return;

                // 查询失效（驱动重启 / 设备丢失 / 计数器注销）：关掉重建，并退避重试。
                q.Close();
                badBeats = 0;
                int backoff = NextBackoff(ref _openBackoff);
                _nextOpen = unchecked(Environment.TickCount + backoff);
                return;
            }
            badBeats = 0;
            _health.At(LayerCollect).Ok();

            // 刚从休眠恢复：速率型计数器的基线横跨整个暂停期，第一拍读数无效，
            // 丢弃重建基线，避免把暂停期的均值当成当前占用。
            if (wasPaused)
            {
                wasPaused = false;
                return;
            }
            PublishFrame(q);
        }

        private void PublishFrame(PdhQuery q)
        {
            SeedAdaptersOnce();
            _sums.Clear();

            GpuSnapshot s = _write;
            PrepareAdapterSlots(s);

            int n = ReadUtil(q.Util);
            s.Available = n > 0;
            s.UtilPct = 0;
            s.TopEngine = "";
            s.Engines = 0;

            if (n > 0)
            {
                string bestKind;
                s.UtilPct = BestUtil(s, out bestKind);
                s.TopEngine = bestKind;
                s.Engines = _sums.Count;
            }

            long bytes = 0;
            s.MemKnown = q.Mem != IntPtr.Zero && ReadMem(q.Mem, s, out bytes);
            s.LocalMemBytes = s.MemKnown ? bytes : 0;

            bool gotAdapterMem = q.AdapterMem != IntPtr.Zero && ReadAdapterMem(q.AdapterMem, s);
            if (!gotAdapterMem && s.MemKnown)
            {
                for (int i = 0; i < s.AdapterCount; i++)
                {
                    if (_procLuidMemKnown[i])
                    {
                        s.Adapters[i].MemKnown = true;
                        s.Adapters[i].VramUsedBytes = _procLuidMem[i];
                    }
                }
            }

            Publish();
        }

        private void PrepareAdapterSlots(GpuSnapshot s)
        {
            for (int i = 0; i < _adapterCount; i++)
            {
                _procLuidMem[i] = 0;
                _procLuidMemKnown[i] = false;
            }
            s.AdapterCount = 0;
            SyncNewSlots(s);
        }

        private void SyncNewSlots(GpuSnapshot s)
        {
            while (s.AdapterCount < _adapterCount)
            {
                int i = s.AdapterCount;
                GpuAdapterSample dst = s.Adapters[i];
                AdapterMeta meta = _adapters[i];
                dst.ResetFrame();
                if (!object.ReferenceEquals(dst.Luid, meta.Luid)) dst.Luid = meta.Luid;
                if (!object.ReferenceEquals(dst.ShortName, meta.ShortName)) dst.ShortName = meta.ShortName;
                if (!object.ReferenceEquals(dst.DriverDesc, meta.DriverDesc)) dst.DriverDesc = meta.DriverDesc;
                if (!object.ReferenceEquals(dst.DriverVersion, meta.DriverVersion)) dst.DriverVersion = meta.DriverVersion;
                dst.Discrete = meta.Discrete;
                dst.VramTotalBytes = meta.VramTotalBytes;
                s.AdapterCount = i + 1;
            }
        }

        /// <summary>整卡占用 = 各引擎求和后的最大值（不是全部相加，否则 3D+Copy+Decode 会 >100%）。</summary>
        private double BestUtil(GpuSnapshot s, out string bestKind)
        {
            SyncNewSlots(s);
            double best = 0;
            bestKind = "";
            foreach (KeyValuePair<string, double> kv in _sums)
            {
                double v = kv.Value;
                if (v < 0) v = 0;
                if (v > 100) v = 100;

                EngineMeta meta;
                if (_keyMeta.TryGetValue(kv.Key, out meta))
                {
                    if (v > best) { best = v; bestKind = meta.Kind; }
                    int slot = meta.Slot;
                    if (slot >= 0 && slot < s.AdapterCount)
                    {
                        GpuAdapterSample a = s.Adapters[slot];
                        a.Available = true;
                        if (v > a.MaxUtilPct) a.MaxUtilPct = v;
                        if (meta.Cat == Cat3D && v > a.Load3D) a.Load3D = v;
                        else if (meta.Cat == CatCompute && v > a.LoadCompute) a.LoadCompute = v;
                        else if (meta.Cat == CatCopy && v > a.LoadCopy) a.LoadCopy = v;
                    }
                }
            }
            return best;
        }

        private void Publish()
        {
            GpuSnapshot old = _pub;
            _pub = _write;
            _write = _spare;
            _spare = old;
        }

        private void PublishUnavailable()
        {
            SeedAdaptersOnce();
            GpuSnapshot s = _write;
            s.Available = false;
            s.UtilPct = 0;
            s.TopEngine = "";
            s.Engines = 0;
            s.MemKnown = false;
            s.LocalMemBytes = 0;
            PrepareAdapterSlots(s);
            Publish();
        }

        private void RecordGpuFail(int layer, int code, int stage, int retryMs)
        {
            SampleHealth h = _health.At(layer);
            Diag.Hit(h.Layer);          // 直接复用层名：不为 Diag 再拼一次 tag
            h.Fail(code, stage, retryMs);
        }

        private static int NextBackoff(ref int current)
        {
            if (current < OpenBackoffMinMs) current = OpenBackoffMinMs;
            else current = Math.Min(current * 2, OpenBackoffMaxMs);
            return current;
        }

        /// <summary>TickCount 回绕安全的"到点了吗"。</summary>
        private static bool RetryDue(int at)
        {
            return at == 0 || unchecked(Environment.TickCount - at) >= 0;
        }

        // ====================================================================
        //  PDH 句柄所有权
        // ====================================================================
        /// <summary>
        /// 一次打开的全部句柄。Open 内部已经处理了"打开一半"的清理，
        /// 外部只需要在离开作用域时 Close 一次（幂等）。
        /// </summary>
        private sealed class PdhQuery
        {
            public IntPtr Query;
            public IntPtr Util;
            public IntPtr Mem;
            public IntPtr AdapterMem;
            public bool Opened;
            /// <summary>显存计数器添加失败时的状态码（可选计数器，不算整条查询失败）。</summary>
            public int MemCode;

            public bool Open(out int layer, out int code)
            {
                layer = LayerOpen;
                code = 0;
                Close();

                code = PdhOpenQueryW(null, IntPtr.Zero, out Query);
                if (code != 0)
                {
                    Query = IntPtr.Zero;
                    return false;
                }

                layer = LayerUtil;
                code = PdhAddEnglishCounterW(Query, UtilPath, IntPtr.Zero, out Util);
                if (code != 0)
                {
                    Close();            // 查询已打开：这里必须关掉，否则每次重试泄漏一个查询句柄
                    return false;
                }

                layer = LayerMem;
                MemCode = PdhAddEnglishCounterW(Query, MemPath, IntPtr.Zero, out Mem);
                if (MemCode != 0) Mem = IntPtr.Zero;   // 显存计数器可选

                if (PdhAddEnglishCounterW(Query, AdapterMemPath, IntPtr.Zero, out AdapterMem) != 0)
                {
                    AdapterMem = IntPtr.Zero;
                    Diag.Hit("gpu.adaptermem.open");
                }

                Opened = true;
                return true;
            }

            public void Close()
            {
                if (Query != IntPtr.Zero) PdhCloseQuery(Query);
                Query = IntPtr.Zero;
                Util = IntPtr.Zero;
                Mem = IntPtr.Zero;
                AdapterMem = IntPtr.Zero;
                MemCode = 0;
                Opened = false;
            }
        }

        // ====================================================================
        //  缓冲区读取（复用非托管缓冲，稳态零分配）
        // ====================================================================
        private void FreeScratchBuf()
        {
            if (_pdhBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pdhBuf);
                _pdhBuf = IntPtr.Zero;
                _pdhBufCap = 0;
            }
        }

        private IntPtr EnsureScratchBuf(int size)
        {
            if (size <= 0) return IntPtr.Zero;
            if (_pdhBuf == IntPtr.Zero || _pdhBufCap < size)
            {
                int doubled = _pdhBufCap > int.MaxValue / 2 ? int.MaxValue : _pdhBufCap * 2;
                int alloc = Math.Max(size, Math.Max(4096, doubled));
                IntPtr next = Marshal.AllocHGlobal(alloc);
                if (_pdhBuf != IntPtr.Zero) Marshal.FreeHGlobal(_pdhBuf);
                _pdhBuf = next;
                _pdhBufCap = alloc;
            }
            return _pdhBuf;
        }

        private IntPtr ReadBuffer(IntPtr counter, uint fmt, out uint count)
        {
            count = 0;
            uint size = 0;
            int st = PdhGetFormattedCounterArrayW(counter, fmt, ref size, out count, IntPtr.Zero);
            if (st == PDH_NO_DATA)
            {
                _health.At(LayerRead).Ok();
                return IntPtr.Zero;
            }
            if (st != 0 && st != PDH_MORE_DATA)
            {
                RecordGpuFail(LayerRead, st, SampleHealth.StageQuery, GpuIntervalMs);
                return IntPtr.Zero;
            }
            // 计数器本身可读（哪怕当前没有实例）：读失败的状态到这里就算恢复了，
            // 否则一次瞬时失败会让诊断面板永远显示"降级"。
            _health.At(LayerRead).Ok();
            if (size == 0) return IntPtr.Zero;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr buf = EnsureScratchBuf((int)size);
                uint bufSz = (uint)_pdhBufCap;
                st = PdhGetFormattedCounterArrayW(counter, fmt, ref bufSz, out count, buf);
                if (st == 0) return buf;
                if (st == PDH_NO_DATA)
                {
                    _health.At(LayerRead).Ok();
                    return IntPtr.Zero;
                }
                if (st != PDH_MORE_DATA)
                {
                    RecordGpuFail(LayerRead, st, SampleHealth.StageQuery, GpuIntervalMs);
                    return IntPtr.Zero;
                }
                size = Math.Max(bufSz, size * 2);      // 实例集合在两拍之间变大：按新尺寸重试
            }
            RecordGpuFail(LayerRead, 0, SampleHealth.StageParse, GpuIntervalMs);
            return IntPtr.Zero;
        }

        private void SeedAdaptersOnce()
        {
            if (_adaptersSeeded) return;
            _adaptersSeeded = true;
            try
            {
                List<Hardware.GpuAdapterInfo> list = Hardware.ReadAdapters();
                for (int i = 0; i < list.Count && _adapterCount < GpuSnapshot.MaxAdapters; i++)
                {
                    Hardware.GpuAdapterInfo a = list[i];
                    if (a.Software || string.IsNullOrEmpty(a.Luid)) continue;
                    if (_luidToSlot.ContainsKey(a.Luid)) continue;
                    int slot = _adapterCount++;
                    AdapterMeta meta = new AdapterMeta();
                    meta.Luid = a.Luid;
                    meta.ShortName = string.IsNullOrEmpty(a.ShortName) ? "GPU" : a.ShortName;
                    meta.DriverDesc = a.DriverDesc;
                    meta.DriverVersion = a.DriverVersion;
                    meta.Discrete = a.Discrete;
                    meta.VramTotalBytes = a.VramBytes;
                    _adapters[slot] = meta;
                    _luidToSlot[a.Luid] = slot;
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("gpu.seed", ex);
            }
        }

        private int SlotForLuid(string luid)
        {
            if (string.IsNullOrEmpty(luid)) return -1;
            int slot;
            if (_luidToSlot.TryGetValue(luid, out slot)) return slot;
            if (_adapterCount >= GpuSnapshot.MaxAdapters) return -1;
            slot = _adapterCount++;
            AdapterMeta meta = new AdapterMeta();
            meta.Luid = luid;
            meta.ShortName = GpuSnapshot.FormatFallbackLuidName(luid);
            _adapters[slot] = meta;
            _luidToSlot[luid] = slot;
            _procLuidMem[slot] = 0;
            _procLuidMemKnown[slot] = false;
            return slot;
        }

        private int ReadUtil(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return 0;
            uint count;
            IntPtr buf = ReadBuffer(counter, PDH_FMT_DOUBLE, out count);
            if (buf == IntPtr.Zero) return 0;

            int stride = IntPtr.Size + 16;
            int valid = 0;
            long p = buf.ToInt64();
            for (int i = 0; i < count; i++)
            {
                IntPtr namePtr = Marshal.ReadIntPtr(new IntPtr(p));
                uint cs = (uint)Marshal.ReadInt32(new IntPtr(p + IntPtr.Size));
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(new IntPtr(p + IntPtr.Size + 8)));
                p += stride;
                if (cs != 0 && cs != 1) continue;
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) continue;
                EngineMeta meta = _utilCache.GetOrAdd(namePtr, ParseAndBindEngine);
                double cur;
                _sums.TryGetValue(meta.Key, out cur);
                _sums[meta.Key] = cur + v;
                valid++;
            }
            return valid;
        }

        private EngineMeta ParseAndBindEngine(string name)
        {
            string key, kind, luid;
            byte cat;
            ParseEngine(name, out key, out kind, out luid, out cat);
            EngineMeta meta = new EngineMeta();
            meta.Key = key;
            meta.Kind = kind;
            meta.Cat = cat;
            meta.Slot = SlotForLuid(luid);
            _keyMeta[key] = meta;
            return meta;
        }

        private int ParseAndBindMemSlot(string name)
        {
            return SlotForLuid(ExtractLuid(name));
        }

        private bool ReadMem(IntPtr counter, GpuSnapshot s, out long bytes)
        {
            bool ok = ReadMemCounter(counter, null, out bytes);
            if (s != null) SyncNewSlots(s);
            return ok;
        }

        private bool ReadAdapterMem(IntPtr counter, GpuSnapshot s)
        {
            long ignored;
            return ReadMemCounter(counter, s, out ignored);
        }

        private bool ReadMemCounter(IntPtr counter, GpuSnapshot adapterTarget, out long totalBytes)
        {
            totalBytes = 0;
            uint count;
            IntPtr buf = ReadBuffer(counter, PDH_FMT_LARGE, out count);
            if (buf == IntPtr.Zero) return false;

            int stride = IntPtr.Size + 16;
            bool any = false;
            long p = buf.ToInt64();
            for (int i = 0; i < count; i++)
            {
                IntPtr namePtr = Marshal.ReadIntPtr(new IntPtr(p));
                uint cs = (uint)Marshal.ReadInt32(new IntPtr(p + IntPtr.Size));
                long v = Marshal.ReadInt64(new IntPtr(p + IntPtr.Size + 8));
                p += stride;
                if (cs != 0 && cs != 1) continue;
                if (v < 0) continue;
                totalBytes += v;
                int slot = _memCache.GetOrAdd(namePtr, ParseAndBindMemSlot);
                if (slot >= 0 && slot < _adapterCount)
                {
                    if (adapterTarget != null)
                    {
                        SyncNewSlots(adapterTarget);
                        adapterTarget.Adapters[slot].MemKnown = true;
                        adapterTarget.Adapters[slot].VramUsedBytes += v;
                    }
                    else
                    {
                        _procLuidMem[slot] += v;
                        _procLuidMemKnown[slot] = true;
                    }
                }
                any = true;
            }
            return any;
        }

        private static string ExtractLuid(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int li = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (li < 0) return "";
            int pi = name.IndexOf("_phys_", li, StringComparison.OrdinalIgnoreCase);
            int ei = name.IndexOf("_eng_", li, StringComparison.OrdinalIgnoreCase);
            int end = name.Length;
            if (pi > li && pi < end) end = pi;
            if (ei > li && ei < end) end = ei;
            return name.Substring(li, end - li);
        }

        // 实例名形如 pid_2912_luid_0x00000000_0x79F42F03_phys_0_eng_3_engtype_3D
        private static void ParseEngine(string name, out string key, out string kind, out string luid, out byte cat)
        {
            kind = "";
            luid = "";
            cat = CatOther;
            if (string.IsNullOrEmpty(name)) { key = ""; return; }
            int li = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            int ei = name.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
            int ti = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            luid = ExtractLuid(name);
            string luidPart = li >= 0 ? name.Substring(li, (ei > li ? ei - li : name.Length - li)) : "";
            string eng = ei >= 0 ? name.Substring(ei, (ti > ei ? ti - ei : name.Length - ei)) : "";
            if (ti >= 0) kind = name.Substring(ti + 8);
            key = luidPart + eng + kind;

            if (kind.IndexOf("3D", StringComparison.OrdinalIgnoreCase) >= 0) cat = Cat3D;
            else if (kind.IndexOf("Compute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     kind.IndexOf("Cuda", StringComparison.OrdinalIgnoreCase) >= 0) cat = CatCompute;
            else if (kind.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0) cat = CatCopy;
        }
    }
}
