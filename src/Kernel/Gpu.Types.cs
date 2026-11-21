// ============================================================================
//  Gpu.Types.cs — GPU 逐卡/聚合快照类型与无托管分配 PDH 实例名缓存
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Go
{
    public sealed class GpuAdapterSample
    {
        public string Luid = "";
        public string ShortName = "";
        public string DriverDesc = "";
        public string DriverVersion = "";
        public bool Discrete;
        public bool Available;
        public double Load3D;
        public double LoadCompute;
        public double LoadCopy;
        public double MaxUtilPct;
        public bool MemKnown;
        public long VramUsedBytes;
        public long VramTotalBytes;

        internal void ResetFrame()
        {
            Available = false;
            Load3D = 0;
            LoadCompute = 0;
            LoadCopy = 0;
            MaxUtilPct = 0;
            MemKnown = false;
            VramUsedBytes = 0;
        }
    }

    public sealed class GpuSnapshot
    {
        public const int MaxAdapters = 8;

        public bool Available;
        public double UtilPct;        // 0..100，整卡（最高引擎）
        public string TopEngine = ""; // 占用最高引擎类型（3D / Copy / VideoDecode…）
        public int Engines;           // 有效引擎键数
        public bool MemKnown;
        public long LocalMemBytes;    // 本地显存用量（进程求和）
        public long VramUsedBytes { get { return LocalMemBytes; } }

        /// <summary>按物理显卡 LUID 区分的逐卡遥测（三缓冲预分配，稳态零分配）。</summary>
        public readonly GpuAdapterSample[] Adapters;
        public int AdapterCount;

        public GpuSnapshot()
        {
            Adapters = new GpuAdapterSample[MaxAdapters];
            for (int i = 0; i < MaxAdapters; i++) Adapters[i] = new GpuAdapterSample();
        }

        public GpuAdapterSample FindAdapter(string luid)
        {
            if (string.IsNullOrEmpty(luid)) return null;
            for (int i = 0; i < AdapterCount; i++)
            {
                if (string.Equals(Adapters[i].Luid, luid, StringComparison.OrdinalIgnoreCase))
                    return Adapters[i];
            }
            return null;
        }

        internal static string FormatFallbackLuidName(string luid)
        {
            if (string.IsNullOrEmpty(luid)) return "虚拟/副显卡";
            int lastHex = luid.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (lastHex >= 0)
            {
                string tail = luid.Substring(lastHex + 2).TrimStart('0').ToUpperInvariant();
                if (tail.Length == 0) tail = "0";
                return "虚拟/副显卡 (" + tail + ")";
            }
            return "虚拟/副显卡 (" + luid + ")";
        }
    }

    /// <summary>
    /// 基于非托管 UTF-16 字符指针的稳态零分配查找缓存（仅 GPU 采样单线程访问）。
    /// 命中已有条目时不构造托管 string，彻底消除每秒 PDH 实例名解析的 GC 压力。
    /// </summary>
    internal sealed class UniPtrCache<T>
    {
        private sealed class Entry
        {
            public char[] Chars;
            public int Next;
            public T Value;
        }

        private readonly List<Entry> _items = new List<Entry>(128);
        private readonly Dictionary<ulong, int> _heads = new Dictionary<ulong, int>(256);

        public delegate T Factory(string name);

        public T GetOrAdd(IntPtr namePtr, Factory factory)
        {
            int len;
            ulong hash = HashUniPtr(namePtr, out len);
            int idx;
            if (_heads.TryGetValue(hash, out idx))
            {
                while (idx >= 0)
                {
                    Entry e = _items[idx];
                    if (EqualsUniPtr(namePtr, e.Chars, len)) return e.Value;
                    idx = e.Next;
                }
            }

            if (_items.Count > 4096)
            {
                _items.Clear();
                _heads.Clear();
            }

            string name = namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(namePtr, len);
            T val = factory(name);
            Entry node = new Entry();
            node.Chars = name.ToCharArray();
            node.Value = val;
            int head;
            node.Next = _heads.TryGetValue(hash, out head) ? head : -1;
            int pos = _items.Count;
            _items.Add(node);
            _heads[hash] = pos;
            return val;
        }

        private static ulong HashUniPtr(IntPtr ptr, out int len)
        {
            len = 0;
            if (ptr == IntPtr.Zero) return 0;
            ulong h = 14695981039346656037UL;
            while (len < 512)
            {
                short ch = Marshal.ReadInt16(ptr, len * 2);
                if (ch == 0) break;
                h ^= (ushort)ch;
                h *= 1099511628211UL;
                len++;
            }
            return h;
        }

        private static bool EqualsUniPtr(IntPtr ptr, char[] chars, int len)
        {
            if (chars.Length != len) return false;
            for (int i = 0; i < len; i++)
            {
                if ((ushort)Marshal.ReadInt16(ptr, i * 2) != chars[i]) return false;
            }
            return true;
        }
    }
}
