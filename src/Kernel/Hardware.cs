// ============================================================================
//  Hardware.cs — 硬件标识读取（只读注册表，一次探测）+ 实时 CPU 频率
//  为什么不用 WMI：WMI 首查 300ms+ 且会拉起服务；这两个名字注册表里就有。
//  失败一律回退到中性文案，绝不让界面出现 "null" 或空字符串。
//
//  Rev86：
//   · GpuShortName 原来是 5 层嵌套的「for → using → if → if」链，读的人要在脑子里
//     记住四个前提才能看懂一个 continue。现在"取适配器描述"单独一个方法，
//     主循环只剩"挑最好的那一个"。
//   · CPU 频率走 powrprof（CallNtPowerInformation 11）。这是采样路径上唯一
//     不进 Perf 的 powrprof 调用，失败时进 SampleHealth：状态码 + 下次重试。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Go
{
    public static class Hardware
    {
        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private const int LayerMhz = 0;
        private static readonly SampleHealthBoard _health = new SampleHealthBoard(new string[] { "hw.cpu.mhz" });

        /// <summary>powrprof 读频率这一层的健康度（诊断面板读它）。</summary>
        public static SampleHealthBoard Health { get { return _health; } }

        public static string CpuShortName()
        {
            try
            {
                RegValue v = Reg.Read("HKLM", @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
                if (!v.Existed || v.Value == null) return "CPU";
                string s = CleanCpuName(v.Value.ToString().Trim());
                return s.Length == 0 ? "CPU" : s;
            }
            catch (Exception ex)
            {
                // 注册表读不到 CPU 名：回退中性文案，绝不显示 null（该函数只负责显示）。
                // 但页头写着 "CPU" 时要知道是"读不到"还是"本来就没有"。
                Diag.Fail("hw.cpu.name", ex);
                return "CPU";
            }
        }

        /// <summary>"AMD Ryzen 9 8945HX 16-Core Processor" → "RYZEN 9 8945HX"。</summary>
        private static string CleanCpuName(string s)
        {
            s = s.Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "").Trim();
            // "AMD Ryzen 9 8945HX with Radeon Graphics" → 在 with 处截断
            int w = s.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
            if (w > 0) s = s.Substring(0, w);
            if (s.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
            if (s.StartsWith("Intel ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6);

            // 核数后缀不能写死 "16-Core"：别的机器上是 "8-Core"，
            // 落在 "-Core" 上截会留下 "Ryzen 7 5800X 8"。按"最后一个空格之前"截断。
            int cut = s.IndexOf("-Core", StringComparison.OrdinalIgnoreCase);
            if (cut < 0) cut = s.IndexOf(" Core", StringComparison.OrdinalIgnoreCase);
            if (cut > 0)
            {
                int sp = s.LastIndexOf(' ', cut - 1);
                if (sp > 0) cut = sp;
            }
            if (cut > 0) s = s.Substring(0, cut);
            s = s.Trim();
            return s.Length > 20 ? s.Substring(0, 20) : s;
        }

        public static string GpuShortName()
        {
            try
            {
                string best = BestAdapter();
                return best == null ? "GPU" : Shorten(best);
            }
            catch (Exception ex)
            {
                // 同上：GPU 名是装饰信息，失败回退 GPU 字样；计数以便区分"没有独显"与"读不到"。
                Diag.Fail("hw.gpu.name", ex);
                return "GPU";
            }
        }

        private static string BestAdapter()
        {
            using (RegistryKey k = Reg.Base("HKLM").OpenSubKey(DisplayClassKey, false))
            {
                if (k == null) return null;
                return BestAdapterIn(k);
            }
        }

        /// <summary>在显示类键下挑一个名字：优先独显（NVIDIA / Radeon RX）。</summary>
        private static string BestAdapterIn(RegistryKey k)
        {
            string best = null;
            string[] subs = k.GetSubKeyNames();
            for (int i = 0; i < subs.Length; i++)
            {
                string d = AdapterDesc(k, subs[i]);
                if (d == null) continue;
                if (best == null || (IsDiscrete(d) && !IsDiscrete(best))) best = d;
            }
            return best;
        }

        /// <summary>读一个适配器子键的 DriverDesc。null = 不是我们要的那一条。</summary>
        private static string AdapterDesc(RegistryKey parent, string sub)
        {
            if (sub.Length != 4) return null;      // 只要 0000 / 0001
            using (RegistryKey a = parent.OpenSubKey(sub, false))
            {
                if (a == null) return null;
                object desc = a.GetValue("DriverDesc", null);
                if (desc == null) return null;
                string d = desc.ToString();
                if (d.Length == 0) return null;
                if (d.IndexOf("Basic Display", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                if (d.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                return d;
            }
        }

        public static string ShortenGpuName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "GPU";
            return Shorten(s);
        }

        public static bool IsDiscreteGpu(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return IsDiscrete(s);
        }

        public static bool IsVirtualGpu(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            return l.IndexOf("virtual", StringComparison.Ordinal) >= 0
                || l.IndexOf("basic render", StringComparison.Ordinal) >= 0
                || l.IndexOf("basic display", StringComparison.Ordinal) >= 0
                || l.IndexOf("mumu", StringComparison.Ordinal) >= 0
                || l.IndexOf("parsec", StringComparison.Ordinal) >= 0
                || l.IndexOf("luminon", StringComparison.Ordinal) >= 0
                || l.IndexOf("spacedesk", StringComparison.Ordinal) >= 0
                || l.IndexOf("iddsample", StringComparison.Ordinal) >= 0
                || l.IndexOf("indirect display", StringComparison.Ordinal) >= 0
                || l.IndexOf("miracast", StringComparison.Ordinal) >= 0
                || l.IndexOf("rdp", StringComparison.Ordinal) >= 0
                || l.IndexOf("citrix", StringComparison.Ordinal) >= 0
                || l.IndexOf("vmware", StringComparison.Ordinal) >= 0
                || l.IndexOf("virtualbox", StringComparison.Ordinal) >= 0
                || l.IndexOf("vbox", StringComparison.Ordinal) >= 0;
        }

        private static bool IsDiscrete(string s)
        {
            if (IsVirtualGpu(s)) return false;
            string l = s.ToLowerInvariant();
            if (l.IndexOf("nvidia") >= 0 || l.IndexOf("geforce") >= 0 || l.IndexOf("quadro") >= 0) return true;
            if (l.IndexOf("radeon rx") >= 0 || l.IndexOf("arc a") >= 0 || l.IndexOf("arc b") >= 0) return true;
            return false;
        }

        private static string Shorten(string s)
        {
            s = s.Replace("NVIDIA ", "").Replace("AMD ", "")
                 .Replace("Radeon ", "").Replace("GeForce ", "")
                 .Replace("Laptop GPU", "Laptop").Replace("Graphics", "").Trim();
            return s.Length > 20 ? s.Substring(0, 20) : s;
        }

        // ---- 实时 CPU 频率 --------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcPowerInfo
        {
            public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
        }

        [DllImport("powrprof.dll")]
        private static extern int CallNtPowerInformation(int infoLevel, IntPtr inBuf, int inLen, IntPtr outBuf, int outLen);

        /// <summary>sizeof(PROCESSOR_POWER_INFORMATION) = 6×4 = 24。预计算一次。</summary>
        private static readonly int ProcPowerStride = Marshal.SizeOf(typeof(ProcPowerInfo));
        private static readonly object _mhzLock = new object();
        private static IntPtr _mhzBuf = IntPtr.Zero;
        private static int _mhzBufCap;

        /// <summary>处理器频率与限频实测摘要（全来自 CallNtPowerInformation(ProcessorInformation)）。</summary>
        public struct CpuPowerSummary
        {
            public bool Valid;
            public int CurrentMhz;
            public int MaxMhz;
            public int LimitMhz;
            /// <summary>电源/温控限制上限低于标称最大频率超过 3% 视为受限频。</summary>
            public bool Throttled { get { return Valid && MaxMhz > 0 && LimitMhz > 0 && LimitMhz < (int)(MaxMhz * 0.97); } }
        }

        /// <summary>读取当前 CPU 频率、最大频率与电源限频上限（复用非托管缓冲，稳态零堆分配）。</summary>
        public static CpuPowerSummary ReadCpuPowerSummary()
        {
            CpuPowerSummary res = new CpuPowerSummary();
            if (ProcPowerStride != 24)
            {
                _health.At(LayerMhz).Fail(0, SampleHealth.StageParse, 0);
                return res;
            }
            try
            {
                int n = Environment.ProcessorCount;
                int size = ProcPowerStride * n;
                lock (_mhzLock)
                {
                    if (_mhzBuf == IntPtr.Zero || _mhzBufCap < size)
                    {
                        IntPtr next = Marshal.AllocHGlobal(size);
                        IntPtr old = _mhzBuf;
                        _mhzBuf = next;
                        _mhzBufCap = size;
                        if (old != IntPtr.Zero) Marshal.FreeHGlobal(old);
                    }
                    int st = CallNtPowerInformation(11 /*ProcessorInformation*/, IntPtr.Zero, 0, _mhzBuf, size);
                    if (st != 0)
                    {
                        Diag.Hit("hw.cpu.mhz");
                        _health.At(LayerMhz).Fail(st, SampleHealth.StageQuery, 0);
                        return res;
                    }
                    long sumCur = 0, sumMax = 0, sumLim = 0;
                    int cntCur = 0, cntMax = 0, cntLim = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int off = i * ProcPowerStride;
                        uint max = (uint)Marshal.ReadInt32(_mhzBuf, off + 4);
                        uint cur = (uint)Marshal.ReadInt32(_mhzBuf, off + 8);
                        uint lim = (uint)Marshal.ReadInt32(_mhzBuf, off + 12);
                        if (cur > 0) { sumCur += cur; cntCur++; }
                        if (max > 0) { sumMax += max; cntMax++; }
                        if (lim > 0) { sumLim += lim; cntLim++; }
                    }
                    res.CurrentMhz = cntCur == 0 ? 0 : (int)(sumCur / cntCur);
                    res.MaxMhz = cntMax == 0 ? 0 : (int)(sumMax / cntMax);
                    res.LimitMhz = cntLim == 0 ? 0 : (int)(sumLim / cntLim);
                    res.Valid = cntCur > 0;
                    _health.At(LayerMhz).Ok();
                    return res;
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("hw.cpu.mhz", ex);
                _health.At(LayerMhz).Fail(0, SampleHealth.StageException, 0);
                return res;
            }
        }

        /// <summary>当前 CPU 频率（MHz，全核平均）。读不到返回 0，界面显示 —，绝不伪造。</summary>
        public static int CpuCurrentMhz()
        {
            return ReadCpuPowerSummary().CurrentMhz;
        }

        // ====================================================================
        //  Rev88: 显卡适配器（注册表 + DXGI LUID）与无 WMI 显示器拓扑
        // ====================================================================
        public sealed class GpuAdapterInfo
        {
            public string RegKey = "";
            public string DriverDesc = "";
            public string ShortName = "";
            public string DriverVersion = "";
            public long VramBytes;
            public string MatchingDeviceId = "";
            public uint VendorId;
            public uint DeviceId;
            public string Luid = "";
            public string AuxLuid = "";
            public long LuidValue;
            public bool Discrete;
            public bool Virtual;
            public bool Software;
        }

        public sealed class DisplayInfo
        {
            public string DeviceName = "";
            public string Name = "";
            public string AdapterName = "";
            public bool Main;
            public int Width;
            public int Height;
            public int RefreshHz;
            public int BitsPerPel;
            public int PosX;
            public int PosY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public LUID AdapterLuid;
            public uint Flags;
        }

        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        private static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IID_IDXGIAdapter1 = new Guid("29038f61-3839-4626-91fd-086879011a05");

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppFactory);

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
            [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr pWindowHandle);
            [PreserveSig] int CreateSwapChain([MarshalAs(UnmanagedType.IUnknown)] object pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
            [PreserveSig] int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 ppAdapter);
            [PreserveSig] bool IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
            [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
            [PreserveSig] int GetDesc(IntPtr pDesc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
        }

        private sealed class DxgiEntry
        {
            public string Desc = "";
            public uint VendorId;
            public uint DeviceId;
            public long VramBytes;
            public string Luid = "";
            public long LuidValue;
            public bool Software;
            public bool Matched;
        }

        public static List<GpuAdapterInfo> ReadAdapters()
        {
            List<GpuAdapterInfo> list = new List<GpuAdapterInfo>();
            List<DxgiEntry> dxgi = ReadDxgiAdapters();

            try
            {
                using (RegistryKey k = Reg.Base("HKLM").OpenSubKey(DisplayClassKey, false))
                {
                    if (k != null)
                    {
                        string[] subs = k.GetSubKeyNames();
                        Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < subs.Length; i++)
                        {
                            GpuAdapterInfo info = ReadRegAdapter(k, subs[i]);
                            if (info != null) list.Add(info);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("hw.adapters.reg", ex);
            }

            // 将 DXGI 枚举到的 LUID 与显存绑定到对应注册表适配器。
            for (int i = 0; i < list.Count; i++)
            {
                GpuAdapterInfo a = list[i];
                DxgiEntry m = MatchDxgi(dxgi, a);
                if (m != null)
                {
                    m.Matched = true;
                    a.Luid = m.Luid;
                    a.LuidValue = m.LuidValue;
                    a.Software = m.Software;
                    if (a.VendorId == 0) a.VendorId = m.VendorId;
                    if (a.DeviceId == 0) a.DeviceId = m.DeviceId;
                    if (a.VramBytes <= 0 && m.VramBytes > 0) a.VramBytes = m.VramBytes;
                }
            }

            // 若注册表漏掉某块通过 DXGI 可见的硬件卡，兜底补入；
            // 若 DXGI 因 MUX / Advanced Optimus 为同一物理显卡暴露第二个 LUID，将其归并到主卡的 AuxLuid，避免重复计为多张独显。
            for (int i = 0; i < dxgi.Count; i++)
            {
                DxgiEntry d = dxgi[i];
                if (d.Matched || d.Software) continue;
                GpuAdapterInfo twin = FindPhysicalTwin(list, d);
                if (twin != null)
                {
                    if (!string.IsNullOrEmpty(d.Luid) && !string.Equals(twin.Luid, d.Luid, StringComparison.OrdinalIgnoreCase))
                        twin.AuxLuid = string.IsNullOrEmpty(twin.AuxLuid) ? d.Luid : (twin.AuxLuid + ", " + d.Luid);
                    continue;
                }
                GpuAdapterInfo extra = new GpuAdapterInfo();
                extra.DriverDesc = d.Desc;
                extra.ShortName = Shorten(d.Desc);
                extra.VramBytes = d.VramBytes;
                extra.VendorId = d.VendorId;
                extra.DeviceId = d.DeviceId;
                extra.Luid = d.Luid;
                extra.LuidValue = d.LuidValue;
                extra.Virtual = IsVirtualGpu(d.Desc);
                extra.Discrete = !extra.Virtual && IsDiscrete(d.Desc);
                list.Add(extra);
            }

            list.Sort(delegate (GpuAdapterInfo x, GpuAdapterInfo y)
            {
                if (x.Software != y.Software) return x.Software ? 1 : -1;
                if (x.Virtual != y.Virtual) return x.Virtual ? 1 : -1;
                if (x.Discrete != y.Discrete) return x.Discrete ? -1 : 1;
                int vc = y.VramBytes.CompareTo(x.VramBytes);
                if (vc != 0) return vc;
                return string.Compare(x.RegKey, y.RegKey, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private static GpuAdapterInfo FindPhysicalTwin(List<GpuAdapterInfo> list, DxgiEntry d)
        {
            if (list == null || d == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                GpuAdapterInfo a = list[i];
                if (a.VendorId != 0 && a.DeviceId != 0 && a.VendorId == d.VendorId && a.DeviceId == d.DeviceId)
                    return a;
                if (!string.IsNullOrEmpty(a.DriverDesc) && string.Equals(a.DriverDesc, d.Desc, StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            return null;
        }

        private static GpuAdapterInfo ReadRegAdapter(RegistryKey parent, string sub)
        {
            if (sub.Length != 4) return null;
            using (RegistryKey a = parent.OpenSubKey(sub, false))
            {
                if (a == null) return null;
                object rawDesc = a.GetValue("DriverDesc", null);
                if (rawDesc == null) return null;
                string desc = rawDesc.ToString().Trim();
                if (desc.Length == 0) return null;
                if (desc.IndexOf("Basic Display", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                if (desc.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0) return null;

                GpuAdapterInfo g = new GpuAdapterInfo();
                g.RegKey = sub;
                g.DriverDesc = desc;
                g.ShortName = Shorten(desc);
                object ver = a.GetValue("DriverVersion", null);
                g.DriverVersion = ver == null ? "" : ver.ToString().Trim();
                g.VramBytes = ReadQword(a.GetValue("HardwareInformation.qwMemorySize", null));
                object mid = a.GetValue("MatchingDeviceId", null);
                g.MatchingDeviceId = mid == null ? "" : mid.ToString().Trim();
                ParsePciIds(g.MatchingDeviceId, out g.VendorId, out g.DeviceId);
                g.Virtual = IsVirtualGpu(desc);
                g.Discrete = !g.Virtual && IsDiscrete(desc);
                return g;
            }
        }

        private static long ReadQword(object val)
        {
            if (val == null) return 0;
            if (val is long) return (long)val;
            if (val is int) return (int)val;
            if (val is ulong) return (long)Math.Min((ulong)val, (ulong)long.MaxValue);
            byte[] b = val as byte[];
            if (b != null)
            {
                if (b.Length >= 8) return BitConverter.ToInt64(b, 0);
                if (b.Length >= 4) return BitConverter.ToUInt32(b, 0);
            }
            long parsed;
            if (long.TryParse(val.ToString(), out parsed)) return parsed;
            return 0;
        }

        private static void ParsePciIds(string match, out uint ven, out uint dev)
        {
            ven = 0; dev = 0;
            if (string.IsNullOrEmpty(match)) return;
            ven = ParseHexAfter(match, "ven_");
            dev = ParseHexAfter(match, "dev_");
        }

        private static uint ParseHexAfter(string s, string tag)
        {
            int idx = s.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            int start = idx + tag.Length;
            int len = 0;
            while (start + len < s.Length && len < 8)
            {
                char c = s[start + len];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) break;
                len++;
            }
            uint res;
            if (len > 0 && uint.TryParse(s.Substring(start, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out res))
                return res;
            return 0;
        }

        private static List<DxgiEntry> ReadDxgiAdapters()
        {
            List<DxgiEntry> res = new List<DxgiEntry>();
            object facObj = null;
            try
            {
                Guid iid = IID_IDXGIFactory1;
                int hr = CreateDXGIFactory1(ref iid, out facObj);
                if (hr != 0 || facObj == null)
                {
                    Diag.Hit("hw.dxgi.create");
                    return res;
                }
                IDXGIFactory1 fac = (IDXGIFactory1)facObj;
                for (uint i = 0; i < 32; i++)
                {
                    IDXGIAdapter1 adapter;
                    int ehr = fac.EnumAdapters1(i, out adapter);
                    if (ehr == DXGI_ERROR_NOT_FOUND || adapter == null) break;
                    if (ehr != 0) { Diag.Hit("hw.dxgi.enum"); break; }
                    try
                    {
                        DXGI_ADAPTER_DESC1 d;
                        if (adapter.GetDesc1(out d) == 0)
                        {
                            DxgiEntry e = new DxgiEntry();
                            e.Desc = d.Description == null ? "" : d.Description.Trim();
                            e.VendorId = d.VendorId;
                            e.DeviceId = d.DeviceId;
                            ulong vram = d.DedicatedVideoMemory.ToUInt64();
                            e.VramBytes = vram > (ulong)long.MaxValue ? long.MaxValue : (long)vram;
                            e.Software = (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                            e.LuidValue = ((long)d.AdapterLuid.HighPart << 32) | d.AdapterLuid.LowPart;
                            e.Luid = string.Format(CultureInfo.InvariantCulture, "luid_0x{0:X8}_0x{1:X8}", d.AdapterLuid.HighPart, d.AdapterLuid.LowPart);
                            res.Add(e);
                        }
                    }
                    finally
                    {
                        if (Marshal.IsComObject(adapter)) Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("hw.dxgi", ex);
            }
            finally
            {
                if (facObj != null && Marshal.IsComObject(facObj)) Marshal.ReleaseComObject(facObj);
            }
            return res;
        }

        private static DxgiEntry MatchDxgi(List<DxgiEntry> dxgi, GpuAdapterInfo a)
        {
            if (a.VendorId != 0 && a.DeviceId != 0)
            {
                for (int i = 0; i < dxgi.Count; i++)
                {
                    DxgiEntry d = dxgi[i];
                    if (!d.Matched && !d.Software && d.VendorId == a.VendorId && d.DeviceId == a.DeviceId)
                        return d;
                }
            }
            for (int i = 0; i < dxgi.Count; i++)
            {
                DxgiEntry d = dxgi[i];
                if (!d.Matched && string.Equals(d.Desc, a.DriverDesc, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        // ---- 无 WMI 显示器拓扑 ----------------------------------------------
        private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICEW
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

        public static List<DisplayInfo> ReadDisplays()
        {
            List<DisplayInfo> list = new List<DisplayInfo>();
            try
            {
                int ddSize = Marshal.SizeOf(typeof(DISPLAY_DEVICEW));
                int dmSize = Marshal.SizeOf(typeof(DEVMODEW));
                if (dmSize != 220)
                {
                    Diag.Hit("hw.display.layout");
                    return list;
                }

                for (uint i = 0; i < 64; i++)
                {
                    DISPLAY_DEVICEW dd = new DISPLAY_DEVICEW();
                    dd.cb = ddSize;
                    if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

                    DisplayInfo di = new DisplayInfo();
                    di.DeviceName = dd.DeviceName == null ? "" : dd.DeviceName.Trim();
                    di.AdapterName = dd.DeviceString == null ? "" : dd.DeviceString.Trim();
                    di.Main = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;

                    DISPLAY_DEVICEW mon = new DISPLAY_DEVICEW();
                    mon.cb = ddSize;
                    if (!string.IsNullOrEmpty(di.DeviceName) && EnumDisplayDevicesW(di.DeviceName, 0, ref mon, 0) && !string.IsNullOrEmpty(mon.DeviceString))
                        di.Name = mon.DeviceString.Trim();
                    else
                        di.Name = di.AdapterName.Length > 0 ? di.AdapterName : di.DeviceName;

                    DEVMODEW dm = new DEVMODEW();
                    dm.dmSize = (ushort)dmSize;
                    if (!string.IsNullOrEmpty(di.DeviceName) && EnumDisplaySettingsW(di.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                    {
                        di.Width = (int)dm.dmPelsWidth;
                        di.Height = (int)dm.dmPelsHeight;
                        di.RefreshHz = (int)dm.dmDisplayFrequency;
                        di.BitsPerPel = (int)dm.dmBitsPerPel;
                        di.PosX = dm.dmPositionX;
                        di.PosY = dm.dmPositionY;
                    }
                    else
                    {
                        Diag.Hit("hw.display.mode");
                    }
                    list.Add(di);
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("hw.displays", ex);
            }
            return list;
        }
    }
}
