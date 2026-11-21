// ============================================================================
//  Power.Ac.cs — 原生 powrprof 交流电源子项读写（零子进程派生）
//
//  旧实现每次探测 / 写入 / 还原 PowerAc 都要派生 powercfg.exe 并按本地化中文标签
//  解析标准输出，冷调用 30~80ms 且受 OEM 代码页与语言包影响。
//  本分片直接走 PowerReadACValueIndex / PowerWriteACValueIndex：单次 < 0.1ms，
//  返回值与十六进制索引口径完全一致；仅在未知别名时才回退命令路径。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Go
{
    public static partial class Power
    {
        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            out uint AcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            uint AcValueIndex);

        private static readonly Dictionary<string, Guid> _subAliases = BuildSubAliases();
        private static readonly Dictionary<string, Guid> _setAliases = BuildSetAliases();

        private static Dictionary<string, Guid> BuildSubAliases()
        {
            Dictionary<string, Guid> d = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            d["SUB_PROCESSOR"] = new Guid("54533251-82be-4824-96c1-47b60b740d00");
            d["SUB_PCIEXPRESS"] = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
            d["SUB_DISK"] = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
            d["SUB_SLEEP"] = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
            d["SUB_VIDEO"] = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
            d["SUB_USB"] = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
            return d;
        }

        private static Dictionary<string, Guid> BuildSetAliases()
        {
            Dictionary<string, Guid> d = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            d["CPMINCORES"] = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
            d["CPMAXCORES"] = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028");
            d["PROCTHROTTLEMIN"] = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
            d["PROCTHROTTLEMAX"] = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
            d["PERFBOOSTMODE"] = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
            d["ASPM"] = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
            d["DISKIDLE"] = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");
            d["USBSELECTSUSPEND"] = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
            return d;
        }

        private static bool ResolvePowerGuid(string text, Dictionary<string, Guid> aliases, out Guid g)
        {
            g = Guid.Empty;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string k = text.Trim();
            // 热路径优先查静态别名表（O(1)），避免对 "SUB_PROCESSOR" 等别名先做必然失败的 Guid.TryParse。
            if (aliases.TryGetValue(k, out g)) return true;
            return Guid.TryParse(k, out g) && g != Guid.Empty;
        }

        /// <summary>直接经由 powrprof.dll 读取当前活动方案的 AC 设置索引（不派生 powercfg.exe）。</summary>
        public static bool ReadAcSetting(string subGroup, string setting, out uint value)
        {
            Guid scheme = Active();
            return ReadAcSetting(scheme, subGroup, setting, out value);
        }

        /// <summary>按已取得的活动方案 GUID 直读 AC 设置索引（批量探测复用一次 Active()）。</summary>
        public static bool ReadAcSetting(Guid scheme, string subGroup, string setting, out uint value)
        {
            value = 0;
            if (scheme == Guid.Empty) return false;
            Guid sub, set;
            if (!ResolvePowerGuid(subGroup, _subAliases, out sub)) return false;
            if (!ResolvePowerGuid(setting, _setAliases, out set)) return false;
            try
            {
                return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out value) == ERROR_SUCCESS;
            }
            catch (Exception ex)
            {
                Diag.Fail("power.read.ac", ex);
                return false;
            }
        }

        /// <summary>直接经由 powrprof.dll 写入当前活动方案的 AC 设置索引并立即激活生效。</summary>
        public static bool WriteAcSetting(string subGroup, string setting, uint value)
        {
            if (ReadOnlyMode) return false;
            Guid sub, set;
            if (!ResolvePowerGuid(subGroup, _subAliases, out sub)) return false;
            if (!ResolvePowerGuid(setting, _setAliases, out set)) return false;

            Guid scheme = Active();
            if (scheme == Guid.Empty) return false;
            try
            {
                if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, value) != ERROR_SUCCESS)
                    return false;
                return PowerSetActiveScheme(IntPtr.Zero, ref scheme) == ERROR_SUCCESS;
            }
            catch (Exception ex)
            {
                Diag.Fail("power.write.ac", ex);
                return false;
            }
        }
    }
}
