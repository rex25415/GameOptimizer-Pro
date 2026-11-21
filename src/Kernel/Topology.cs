// ============================================================================
//  Topology.cs — 真实 CPU 拓扑探测（CCD / 混合架构 / 大小核）
//  为什么要自己算：旧代码把 "2 个 CCD × 8 核 × 2 线程" 写死在总览与调度页，
//  换一台单 CCD 的机器，界面会画出两个不存在的 CCD，
//  而守护进程会往 0xFFFF 上绑亲和度——在 8 核机器上那是无效掩码。
//
//  判定依据（全部来自内核，不猜）：
//    · 物理核心数  = RelationProcessorCore 条目数
//    · CCD 数      = Level-3 缓存实例数（Zen 每个 CCD 一块独立 L3）
//    · 混合架构    = 核心条目里 EfficiencyClass 出现多于一种取值
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace Go
{
    public sealed class Topology
    {
        public int Logical;          // 逻辑处理器数
        public int Cores;            // 物理核心数
        public int Ccds;             // 簇数（AMD = CCD，Intel = L3 域）
        public int CoresPerCcd;
        public int ThreadsPerCore;
        public bool Hybrid;          // 大核 / 小核混合
        public bool L3Probed;        // L3 是否可读（不可读则 Ccds 退化为 1）
        /// <summary>核心关系是否真的读到了。false 表示走了估算回退——界面必须说明，不能假装。</summary>
        public bool CoreProbed;
        public long Ccd0Mask;        // 第一个簇的逻辑处理器掩码

        public string CcdLabel { get { return Ccds <= 1 ? "单簇" : Ccds + " CCD"; } }

        public static Topology Detect()
        {
            Topology t = new Topology();
            t.Logical = Environment.ProcessorCount;
            t.Ccds = 1;
            try
            {
                t.ProbeCores();
                t.ProbeCaches();
            }
            catch (Exception ex)
            {
                // 探测异常：下面按 Logical 估算并把 CoreProbed 留 false，界面据此刻出"估算"。
                // 这是一条静默降级路径（界面会显示一个合理的数字），必须计数才能发现。
                Diag.Fail("topology.detect", ex);
            }

            FinalizeDerived(t);
            return t;
        }

        internal static void FinalizeDerived(Topology t)
        {
            if (t == null) return;
            if (t.Logical <= 0) t.Logical = Math.Max(1, Environment.ProcessorCount);
            if (t.Cores <= 0) t.Cores = Math.Max(1, t.Logical / 2);
            if (t.Ccds <= 0) t.Ccds = 1;
            if (t.Ccds > t.Cores) t.Ccds = 1;
            t.CoresPerCcd = (int)Math.Ceiling((double)t.Cores / t.Ccds);
            t.ThreadsPerCore = Math.Max(1, t.Logical / Math.Max(1, t.Cores));
            t.Ccd0Mask = MaskForFirstCluster(t);
        }

        /// <summary>第一个簇的亲和度掩码。单簇机器上它等于全部可用逻辑处理器。</summary>
        internal static long MaskForFirstCluster(Topology t)
        {
            int logical = t != null ? t.Logical : Environment.ProcessorCount;
            int allBits = Math.Min(Affinity.MaxBits, Math.Max(0, logical));
            if (allBits <= 0) return 0;
            long all = allBits >= Affinity.MaxBits ? long.MaxValue : ((1L << allBits) - 1);
            if (t == null || t.Ccds <= 1) return all;

            int bits = t.CoresPerCcd * Math.Max(1, t.ThreadsPerCore);
            if (bits <= 0) bits = allBits;
            if (bits >= Affinity.MaxBits) return all;
            long mask = ((1L << bits) - 1) & all;
            return mask != 0 ? mask : all;
        }

        // ---- P/Invoke ------------------------------------------------------
        private const int RelationProcessorCore = 0;
        private const int RelationCache = 2;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int type, IntPtr buffer, ref uint length);

        private static IntPtr Query(int type, out int size)
        {
            size = 0;
            uint len = 0;
            GetLogicalProcessorInformationEx(type, IntPtr.Zero, ref len);
            if (len == 0) return IntPtr.Zero;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            if (!GetLogicalProcessorInformationEx(type, buf, ref len))
            {
                Marshal.FreeHGlobal(buf);
                return IntPtr.Zero;
            }
            size = (int)len;
            return buf;
        }

        /// <summary>遍历关系条目：回调收到 (条目指针, 关系类型)。返回 false 提前结束。</summary>
        private delegate bool EntryVisitor(IntPtr entry, int relationship);

        private static void Walk(int type, EntryVisitor visit)
        {
            int size;
            IntPtr buf = Query(type, out size);
            if (buf == IntPtr.Zero) return;
            try
            {
                long p = buf.ToInt64();
                long end = p + size;
                while (p < end)
                {
                    // 头部是 4 字节枚举 Relationship + 4 字节 Size（不是 WORD+WORD），
                    // 联合体从 +8 开始。偏移错一位会让长度读成 0，整个探测静默退化成估算。
                    int rel = Marshal.ReadInt32(new IntPtr(p));
                    int len = Marshal.ReadInt32(new IntPtr(p + 4));

                    // ---- 偏移断言 -------------------------------------------------
                    // 1) 条目长度必须容得下我们要读的最深字段：ProbeCores 读 EfficiencyClass@+9，
                    //    所以至少 10 字节（头部 8 + Flags@8 + Eff@9）。只判 len<8 会让 len=8/9
                    //    的畸形条目越界读。内核实际不会返回这种条目，但防御缺口要堵上。
                    // 2) 条目类型必须与查询类型一致。NtQuerySystemInformation 按类型返回，
                    //    所以这是"整体错一位"最直接、最不可能误报的判据。
                    if (len < 10) break;
                    if (len > end - p) break;
                    if (rel != type) break;

                    if (!visit(new IntPtr(p), rel)) break;
                    p += len;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void ProbeCores()
        {
            int cores = 0;
            int firstEff = -1;
            bool hybrid = false;

            Walk(RelationProcessorCore, delegate (IntPtr e, int rel)
            {
                cores++;
                // 联合体起点 +8，PROCESSOR_RELATIONSHIP: Flags@+8, EfficiencyClass@+9
                int eff = Marshal.ReadByte(e, 9);
                if (firstEff < 0) firstEff = eff;
                else if (eff != firstEff) hybrid = true;
                return true;
            });

            if (cores > 0) { Cores = cores; CoreProbed = true; }
            Hybrid = hybrid;
        }

        private void ProbeCaches()
        {
            int l3 = 0;
            Walk(RelationCache, delegate (IntPtr e, int rel)
            {
                // 联合体起点 +8，CACHE_RELATIONSHIP: Level@+8, CacheSize@+12
                int level = Marshal.ReadByte(e, 8);
                if (level == 3) l3++;
                return true;
            });
            if (l3 > 0)
            {
                Ccds = l3;
                L3Probed = true;
            }
        }

        /// <summary>一行摘要，用于日志与自检。</summary>
        public string Describe()
        {
            return Logical + "T / " + Cores + "C / " + Ccds + "簇 × " + CoresPerCcd + " 核 / "
                 + ThreadsPerCore + " 路" + (Hybrid ? " / 混合架构" : "")
                 + " / CCD0=0x" + Ccd0Mask.ToString("X");
        }
    }
}
