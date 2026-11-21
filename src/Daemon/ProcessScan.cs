// ============================================================================
//  ProcessScan.cs — 零分配进程枚举（NtQuerySystemInformation · SystemProcessInformation）
//
//  为什么不用 Process.GetProcesses()：那会为每个进程创建一个 Process 对象并打开句柄。
//  进程页每 2 秒重扫一次时，那是 400+ 个对象、1300+ 次句柄往返；本类只做一次非托管遍历。
//
//  Rev87 把这一层当成"内核缓冲的所有者"重写，修掉三处会真正把进程带崩的东西：
//   1) Grow 先 Free 后 Alloc：AllocHGlobal 抛 OOM 时 _buf 仍指向已释放内存、_cap 却已变大，
//      下一次 EnsureBuffer 见 _buf != 0 就把悬空指针交给内核写 —— 堆破坏。
//      现在先分配新块，成功后才交换并释放旧块；失败时旧块原样保留（还能继续用）。
//   2) 缓冲没有所有权闸门：巡检线程与页面线程可以同时进 ForEach，Grow 会释放对方正在读的块。
//      现在所有缓冲访问都在 _gate 内；回调在锁内执行（Monitor 可重入，回调里再调本类不会自锁）。
//   3) 没有终结器：任何一条路径漏掉 Dispose，512 KB 非托管内存就留到进程结束。
//      现在有终结器兜底，Dispose 幂等。
//
//  行数据（ProcessRow）一次给出：映像名 / PID / 创建时间 / CPU 累计 / 工作集 / 线程数，
//  全部来自同一次系统调用 —— 消费方不需要再为每个进程开句柄去读 WorkingSet64。
//  这些字段的偏移是跨版本硬编码，所以每个都带语义断言：越界或不可能的值一律置 0，
//  绝不让"读错了一位"变成一个看起来合理的数字。
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace Go.Daemon
{
    /// <summary>进程表里的一行。只在 visitor 回调内有效：缓冲区下一次扫描会被覆盖。</summary>
    public struct ProcessRow
    {
        public IntPtr NameBuffer;
        public int NameChars;
        public int Pid;
        public long CreateTime;
        /// <summary>User + Kernel 累计，100ns 刻度（与 Process.TotalProcessorTime 同单位）。</summary>
        public long CpuTicks;
        /// <summary>工作集字节数。0 = 本次没读到（不是"占用 0"）。</summary>
        public long WorkingSetBytes;
        /// <summary>线程数。-1 = 读不到。</summary>
        public int Threads;
        /// <summary>基础优先级（来自 SYSTEM_PROCESS_INFORMATION；0 = 未知）。</summary>
        public int BasePriority;

        public string Name() { return ProcessScan.NameOf(NameBuffer, NameChars); }
        public bool NameIs(string s) { return ProcessScan.NameEquals(NameBuffer, NameChars, s); }
    }

    /// <summary>返回值 true 表示提前结束遍历。</summary>
    public delegate bool ProcessVisitor(IntPtr nameBuf, int nameChars, int pid, long createTime);

    /// <summary>行访问器：一次拿到 CPU / 工作集 / 线程数。返回 true 提前结束。</summary>
    public delegate bool ProcessRowVisitor(ProcessRow row);

    /// <summary>进程表的只读游标。一次 Scan 复用同一块缓冲，稳态不产生托管分配。</summary>
    public sealed class ProcessScan : IDisposable
    {
        private const int SysProcessInfo = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const int InitialCap = 512 * 1024;
        private const int MaxCap = 32 * 1024 * 1024;
        /// <summary>遍历保护：即使内核数据损坏也不允许无限循环。</summary>
        private const int MaxRows = 1000000;

        // 记住进程表高水位容量：后续新建实例直接按实际需求一次分配，消除每次冷建都要先失败再扩容的双倍系统调用。
        private static int _highWaterCap = InitialCap;
        // 供 FindPidsByName 复用的线程安全共享扫描器（内部有 _gate 护卫，稳态零 Alloc/Free 与零扩容）。
        private static readonly ProcessScan _shared = new ProcessScan();

        private readonly object _gate = new object();
        private IntPtr _buf = IntPtr.Zero;
        private int _cap;
        private int _size;
        private readonly bool _x64 = IntPtr.Size == 8;

        // 诊断读数（只增不减；跨线程读用 Volatile，写在本类锁内）。
        private int _growths;
        private int _failures;
        private volatile string _lastError;

        /// <summary>最近一次扫描的条目数。</summary>
        public int Count { get; private set; }
        /// <summary>最近一次扫描实际拿到的字节数。</summary>
        public int LastSize { get { return _size; } }
        /// <summary>当前缓冲容量（字节）。</summary>
        public int Capacity { get { return _cap; } }
        /// <summary>因条目变多而扩容过几次。</summary>
        public int Growths { get { return _growths; } }
        /// <summary>系统调用失败次数（与"扫到 0 个进程"是两件事）。</summary>
        public int Failures { get { return _failures; } }
        /// <summary>最近一次失败原因；空 = 没失败过。</summary>
        public string LastError { get { return _lastError; } }

        // SYSTEM_PROCESS_INFORMATION 关键字段偏移（x64 / WOW64）。
        private int OffImageName { get { return 56; } }
        private int OffBasePriority { get { return _x64 ? 72 : 64; } }
        private int OffPid { get { return _x64 ? 80 : 68; } }
        private int OffNext { get { return 0; } }
        private int OffCreateTime { get { return 32; } }
        private int OffUserTime { get { return 40; } }
        private int OffKernelTime { get { return 48; } }
        private int OffThreads { get { return 4; } }
        private int OffWorkingSet { get { return _x64 ? 0x90 : 0x68; } }

        /// <summary>
        /// 遍历当前进程表（只要名字/PID）。visitor 收到的是**只读**缓冲指针，
        /// 回调里不得保存它——下一次扫描会覆盖。
        /// 返回 false 表示本次系统调用失败（缓冲不足/权限）：调用方据此区分
        /// "扫不到进程"与"扫描本身失败"，绝不能把后者当成"进程已退出"。
        /// </summary>
        public bool ForEach(ProcessVisitor visitor)
        {
            if (visitor == null) return false;
            lock (_gate)
            {
                if (_forward == null) _forward = new ProcessRowVisitor(ForwardRow);
                ProcessVisitor prev = _forwardVisitor;
                _forwardVisitor = visitor;
                try { return ForEachRow(_forward); }
                finally { _forwardVisitor = prev; }
            }
        }

        // 转发用的缓存委托：每次 ForEach 都 new 一个闭包/委托，会让"零分配巡检"名存实亡。
        private ProcessVisitor _forwardVisitor;
        private ProcessRowVisitor _forward;

        private bool ForwardRow(ProcessRow row)
        {
            ProcessVisitor v = _forwardVisitor;
            return v != null && v(row.NameBuffer, row.NameChars, row.Pid, row.CreateTime);
        }

        /// <summary>
        /// 遍历当前进程表，一次给出 CPU 累计 / 工作集 / 线程数。
        /// 返回 false 表示扫描本身失败（LastError 有原因）。
        /// </summary>
        public bool ForEachRow(ProcessRowVisitor visitor)
        {
            if (visitor == null) return false;
            lock (_gate)
            {
                int ret;
                int st;
                if (!Query(out ret, out st)) return false;

                _size = ret;
                int n = 0;
                long p = _buf.ToInt64();
                long end = p + _size;
                int guard = 0;
                bool stop = false;

                while (p + 64 <= end && guard++ < MaxRows)
                {
                    int next = Marshal.ReadInt32(new IntPtr(p + OffNext));
                    int pid = Marshal.ReadInt32(new IntPtr(p + OffPid));

                    // 名字字段是 UNICODE_STRING（Length + MaximumLength + 对齐 + Buffer）。
                    // 指针字段在 x64 上偏移 +8、x86 上 +4；先确认整个字段落在缓冲区内。
                    int nameField = OffImageName;
                    if (p + nameField + 16 > end) break;
                    int nameLen = (int)Marshal.ReadInt16(new IntPtr(p + nameField));
                    IntPtr nameBuf = Marshal.ReadIntPtr(new IntPtr(p + nameField + (_x64 ? 8 : 4)));
                    long create = SafeReadInt64(p, OffCreateTime, end);
                    long cpu = SafeAdd(SafeReadInt64(p, OffUserTime, end), SafeReadInt64(p, OffKernelTime, end));
                    long ws = SafeReadInt64(p, OffWorkingSet, end);
                    int threads = SafeReadInt32(p, OffThreads, end);
                    int basePrio = SafeReadInt32(p, OffBasePriority, end);

                    // 语义断言：内核结构布局一旦变化，宁可给 0（界面显示 —），也不给假数字。
                    if (ws < 0 || ws > (1L << 48)) ws = 0;
                    if (cpu < 0) cpu = 0;
                    if (threads < 0 || threads > 65535) threads = -1;
                    if (basePrio < 0 || basePrio > 31) basePrio = 0;
                    if (nameLen < 0 || (nameLen & 1) != 0) nameLen = 0;

                    if (pid > 0)
                    {
                        n++;
                        ProcessRow row = new ProcessRow();
                        row.NameBuffer = nameBuf;
                        row.NameChars = nameLen / 2;
                        row.Pid = pid;
                        row.CreateTime = create;
                        row.CpuTicks = cpu;
                        row.WorkingSetBytes = ws;
                        row.Threads = threads;
                        row.BasePriority = basePrio;
                        if (visitor(row)) { stop = true; }
                    }

                    if (stop) break;
                    if (next <= 0) break;
                    if (next < 16) { SetError("进程表条目长度非法：" + next); break; }
                    p += next;
                }

                Count = n;
                if (n > 0) _lastError = null;
                return true;
            }
        }

        private bool Query(out int ret, out int st)
        {
            ret = 0;
            if (!EnsureBuffer()) { st = -1; return false; }

            st = Native.NtQuerySystemInformation(SysProcessInfo, _buf, _cap, out ret);
            if (st == StatusInfoLengthMismatch || (st >= 0 && ret > _cap))
            {
                // 进程数在两次调用之间增长：按返回长度扩容后重试一次。
                int want = ret > _cap ? ret + 65536 : _cap * 2;
                if (!Grow(want))
                {
                    SetError("进程表缓冲扩容失败（需要 " + want + " 字节）");
                    return false;
                }
                st = Native.NtQuerySystemInformation(SysProcessInfo, _buf, _cap, out ret);
            }
            if (st < 0)
            {
                SetError("NtQuerySystemInformation 失败 0x" + st.ToString("X8"));
                return false;
            }
            if (ret <= 0 || ret > _cap) ret = _cap;
            return true;
        }

        /// <summary>
        /// 把所有进程的 CPU 累计时间（User+Kernel，100ns）写进调用方的字典。
        /// 不解析名字、不分配字符串——名字只在最终出报告时对前几名解析一次。
        /// 返回 false 表示扫描失败（调用方应跳过本拍差分，而不是当成全进程退出）。
        /// </summary>
        public bool SnapshotCpu(System.Collections.Generic.Dictionary<int, long> into)
        {
            return SnapshotCpu(into, null);
        }

        /// <summary>
        /// 同上，并顺带写出每个 PID 的创建时间（born 可为 null）。
        /// 为什么要它：PID 会被系统复用。只按 PID 累计 CPU，会把"新进程"算进"旧进程"的账上。
        /// </summary>
        public bool SnapshotCpu(System.Collections.Generic.Dictionary<int, long> into,
                                System.Collections.Generic.Dictionary<int, long> born)
        {
            if (into == null) return false;
            lock (_gate)
            {
                if (_cpuVisitor == null) _cpuVisitor = new ProcessRowVisitor(CollectCpu);
                System.Collections.Generic.Dictionary<int, long> prevInto = _cpuInto;
                System.Collections.Generic.Dictionary<int, long> prevBorn = _cpuBorn;
                _cpuInto = into;
                _cpuBorn = born;
                try { return ForEachRow(_cpuVisitor); }
                finally { _cpuInto = prevInto; _cpuBorn = prevBorn; }
            }
        }

        private ProcessRowVisitor _cpuVisitor;
        private System.Collections.Generic.Dictionary<int, long> _cpuInto;
        private System.Collections.Generic.Dictionary<int, long> _cpuBorn;

        private bool CollectCpu(ProcessRow row)
        {
            if (_cpuInto != null) _cpuInto[row.Pid] = row.CpuTicks;
            if (_cpuBorn != null) _cpuBorn[row.Pid] = row.CreateTime;
            return false;
        }

        // ---- 非托管缓冲：所有权全在本类 --------------------------------------
        private bool EnsureBuffer()
        {
            if (_buf != IntPtr.Zero) return true;
            int cap = _highWaterCap;
            if (cap < InitialCap) cap = InitialCap;
            if (cap > MaxCap) cap = MaxCap;
            try
            {
                _buf = Marshal.AllocHGlobal(cap);
                _cap = cap;
                // 只清零头部 64B（ForEachRow 严格以内核回写的 _size 截断边界）；
                // 旧实现逐 4B 写零 131,072 次（512KB），白耗数毫秒并拖慢冷查。
                for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(_buf, i, 0);
                return true;
            }
            catch (Exception ex)
            {
                _buf = IntPtr.Zero;
                _cap = 0;
                SetError("进程表缓冲分配失败：" + ex.Message);
                Diag.Fail("procscan.alloc", ex);
                return false;
            }
        }

        /// <summary>
        /// 扩容：先分配新块，成功后才交换并释放旧块。
        /// 旧实现先 Free 再 Alloc —— Alloc 抛异常时 _buf 是悬空的，下一次调用就是堆破坏。
        /// </summary>
        private bool Grow(int want)
        {
            if (want <= _cap) return true;
            if (want > MaxCap) want = MaxCap;
            IntPtr fresh;
            try { fresh = Marshal.AllocHGlobal(want); }
            catch (Exception ex)
            {
                SetError("进程表缓冲扩容失败：" + ex.Message);
                Diag.Fail("procscan.grow", ex);
                return false;   // 旧缓冲原样保留，下一拍还能用
            }
            IntPtr old = _buf;
            _buf = fresh;
            _cap = want;
            if (want > _highWaterCap && want <= MaxCap) _highWaterCap = want;
            _size = 0;
            _growths++;
            if (old != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(old); }
                catch (Exception ex) { Diag.Fail("procscan.grow.free", ex); }
            }
            return true;
        }

        private void SetError(string msg)
        {
            _lastError = msg;
            _failures++;
        }

        private long SafeReadInt64(long baseAddr, int offset, long end)
        {
            if (offset < 0 || baseAddr + offset + 8 > end) return 0;
            return Marshal.ReadInt64(new IntPtr(baseAddr + offset));
        }

        private int SafeReadInt32(long baseAddr, int offset, long end)
        {
            if (offset < 0 || baseAddr + offset + 4 > end) return -1;
            return Marshal.ReadInt32(new IntPtr(baseAddr + offset));
        }

        private static long SafeAdd(long a, long b)
        {
            if (a < 0) a = 0;
            if (b < 0) b = 0;
            if (a > long.MaxValue - b) return long.MaxValue;
            return a + b;
        }

        /// <summary>把一段 UTF-16 进程名与给定字符串做不区分大小写的比较，不构造字符串（ASCII 快速路径）。</summary>
        public static bool NameEquals(IntPtr buf, int chars, string name)
        {
            if (chars <= 0 || buf == IntPtr.Zero || name == null) return false;
            if (chars != name.Length) return false;
            for (int i = 0; i < chars; i++)
            {
                char a = (char)Marshal.ReadInt16(buf, i * 2);
                char b = name[i];
                if (a == b) continue;
                if (a < 128 && b < 128)
                {
                    int la = (a >= 'A' && a <= 'Z') ? (a + 32) : a;
                    int lb = (b >= 'A' && b <= 'Z') ? (b + 32) : b;
                    if (la != lb) return false;
                    continue;
                }
                if (char.ToLowerInvariant(a) != char.ToLowerInvariant(b)) return false;
            }
            return true;
        }

        /// <summary>按进程名或文件名（兼容带/不带 .exe）收集匹配 PID，零句柄开销。</summary>
        public int[] FindPids(string nameOrExe)
        {
            if (string.IsNullOrWhiteSpace(nameOrExe)) return new int[0];
            string bare = nameOrExe.Trim();
            string withExe = bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? bare : (bare + ".exe");
            string noExe = bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? bare.Substring(0, bare.Length - 4) : bare;
            System.Collections.Generic.List<int> list = new System.Collections.Generic.List<int>(4);
            ForEach(delegate (IntPtr buf, int chars, int pid, long createTime)
            {
                if (pid > 4 && (NameEquals(buf, chars, withExe) || NameEquals(buf, chars, noExe)))
                    list.Add(pid);
                return false;
            });
            return list.ToArray();
        }

        /// <summary>静态按名查 PID（复用线程安全共享缓冲，稳态零非托管分配与零扩容）。</summary>
        public static int[] FindPidsByName(string nameOrExe)
        {
            if (string.IsNullOrWhiteSpace(nameOrExe)) return new int[0];
            return _shared.FindPids(nameOrExe);
        }

        /// <summary>
        /// 批量补齐受限进程的短名（复用静态高水位 _shared 缓冲，零 1.5MB 重复分配）。
        /// </summary>
        public static void FillPidNames(System.Collections.Generic.Dictionary<int, string> unresolved)
        {
            if (unresolved == null || unresolved.Count == 0) return;
            int left = unresolved.Count;
            _shared.ForEach(delegate (IntPtr nameBuf, int nameChars, int pid, long createTime)
            {
                string existing;
                if (unresolved.TryGetValue(pid, out existing) && string.IsNullOrEmpty(existing))
                {
                    string raw = NameOf(nameBuf, nameChars);
                    if (!string.IsNullOrEmpty(raw))
                    {
                        if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            raw = raw.Substring(0, raw.Length - 4);
                        unresolved[pid] = raw;
                    }
                    if (--left <= 0) return true;
                }
                return false;
            });
        }

        /// <summary>按单个 PID 通过共享扫描器取短名（供 OpenProcess 受限时零分配兜底）。</summary>
        public static string LookupNameByPid(int targetPid)
        {
            if (targetPid <= 0) return "";
            string found = "";
            _shared.ForEach(delegate (IntPtr nameBuf, int nameChars, int pid, long createTime)
            {
                if (pid == targetPid)
                {
                    string raw = NameOf(nameBuf, nameChars);
                    if (!string.IsNullOrEmpty(raw))
                    {
                        if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            raw = raw.Substring(0, raw.Length - 4);
                        found = raw;
                    }
                    return true;
                }
                return false;
            });
            return found;
        }

        /// <summary>把映像名转成托管字符串（仅在确认命中后调用一次）。</summary>
        public static string NameOf(IntPtr buf, int chars)
        {
            if (chars <= 0 || buf == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(buf, chars);
        }

        /// <summary>
        /// 释放缓冲。本类可以被复用：下一次 ForEach 会重新分配（旧实现就是这个语义）。
        /// 因此**不**抑制终结器——复用后再次泄漏时它仍要能兜住。
        /// </summary>
        public void Dispose()
        {
            lock (_gate) { ReleaseBuffers(); }
        }

        private void ReleaseBuffers()
        {
            IntPtr b = _buf;
            _buf = IntPtr.Zero;
            _cap = 0;
            _size = 0;
            Count = 0;
            if (b == IntPtr.Zero) return;
            try { Marshal.FreeHGlobal(b); }
            catch (Exception ex) { Diag.Fail("procscan.free", ex); }
        }

        /// <summary>终结器兜底：漏掉 Dispose 也不会把 512 KB 非托管内存留到进程结束。</summary>
        ~ProcessScan()
        {
            try
            {
                lock (_gate) { ReleaseBuffers(); }
            }
            catch (Exception ex)
            {
                // 进程正在退出：终结器里不能再抛，但也不能完全静默（Diag 是最后一条记录通道）。
                Diag.Fail("procscan.finalize", ex);
            }
        }
    }
}
