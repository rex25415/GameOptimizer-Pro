// ============================================================================
//  SelfTest.Rev110.cs — Rev 110 AETHER 后端核心回归与新能力自检
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Go.Daemon;

namespace Go
{
    public static partial class SelfTest
    {
        private static void TestRev110Aether()
        {
            // 1) 目录扩展至 30 项，包含全部 5 项新增微软官方硬核调优
            List<ActionDef> all = Catalog.Build();
            bool hasWinOpt = false, hasVrr = false, hasUsbAc = false, hasMinState = false, hasPwrThrot = false;
            for (int i = 0; i < all.Count; i++)
            {
                string id = all[i].Id;
                if (id == "win-opt-dx") hasWinOpt = true;
                else if (id == "vrr-opt-dx") hasVrr = true;
                else if (id == "usb-power-ac") hasUsbAc = true;
                else if (id == "cpu-min-state") hasMinState = true;
                else if (id == "power-throttle-off") hasPwrThrot = true;
            }
            Check(all.Count >= 30 && hasWinOpt && hasVrr && hasUsbAc && hasMinState && hasPwrThrot,
                  "Rev110 目录：30 项调优与 5 项新增硬核动作齐备",
                  "count=" + all.Count + " dx=" + hasWinOpt + "/" + hasVrr + " usb=" + hasUsbAc);

            // 2) RegKv 字段级原子合并写 + 独立还原（互不覆盖其他键值对）
            const string kvPath = @"Software\GameOptimizer\SelfTest\RegKv";
            const string kvName = "DirectXUserGlobalSettings";
            string sb = Path.Combine(Path.GetTempPath(), "GameOptimizer", "selftest-regkv-" + Guid.NewGuid().ToString("N"));
            string prevDir = Ledger.DirOverride;
            try
            {
                Directory.CreateDirectory(sb);
                Ledger.DirOverride = sb;
                Reg.Delete("HKCU", kvPath, kvName);
                Reg.Write("HKCU", kvPath, kvName, "AutoHDREnable=1;", "String");
                Ledger led = new Ledger();

                Op opA = new Op { Kind = "RegKv", Hive = "HKCU", Path = kvPath, Name = kvName, KvKey = "SwapEffectUpgradeEnable", Type = "String", Value = "1" };
                Op opB = new Op { Kind = "RegKv", Hive = "HKCU", Path = kvPath, Name = kvName, KvKey = "VRROptimizeEnable", Type = "String", Value = "1" };

                OpResult rA = Ops.Apply(opA, "kv-a", "测试A", led);
                OpResult rB = Ops.Apply(opB, "kv-b", "测试B", led);
                string midRaw = Reg.ToText(Reg.Read("HKCU", kvPath, kvName).Value);
                string vHdr, vSwap, vVrr;
                bool midOk = rA.Ok && rB.Ok
                    && Ops.TryParseSemicolonKv(midRaw, "AutoHDREnable", out vHdr) && vHdr == "1"
                    && Ops.TryParseSemicolonKv(midRaw, "SwapEffectUpgradeEnable", out vSwap) && vSwap == "1"
                    && Ops.TryParseSemicolonKv(midRaw, "VRROptimizeEnable", out vVrr) && vVrr == "1";

                // 单独还原 A：必须只移除 SwapEffectUpgradeEnable，保留 AutoHDREnable 和 VRROptimizeEnable
                OpResult revA = rA.Entry != null ? Ops.Revert(rA.Entry) : OpResult.Fail("no entry", "还原", 0);
                string afterARaw = Reg.ToText(Reg.Read("HKCU", kvPath, kvName).Value);
                string aHdr, aSwap, aVrr;
                bool afterAOk = revA.Ok
                    && Ops.TryParseSemicolonKv(afterARaw, "AutoHDREnable", out aHdr) && aHdr == "1"
                    && !Ops.TryParseSemicolonKv(afterARaw, "SwapEffectUpgradeEnable", out aSwap)
                    && Ops.TryParseSemicolonKv(afterARaw, "VRROptimizeEnable", out aVrr) && aVrr == "1";

                OpResult revB = rB.Entry != null ? Ops.Revert(rB.Entry) : OpResult.Fail("no entry", "还原", 0);
                string finalRaw = Reg.ToText(Reg.Read("HKCU", kvPath, kvName).Value);
                bool finalOk = revB.Ok && finalRaw == "AutoHDREnable=1;";

                Check(midOk && afterAOk && finalOk,
                      "Rev110 RegKv：双动作字段级并发合并与独立还原互不踩踏",
                      "mid=" + midRaw + " afterA=" + afterARaw + " final=" + finalRaw);
            }
            catch (Exception ex)
            {
                Check(false, "Rev110 RegKv：字段级事务异常", ex.Message);
            }
            finally
            {
                Ledger.DirOverride = prevDir;
                try { Reg.Delete("HKCU", kvPath, kvName); } catch { Diag.Hit("selftest.regkv.clean"); }
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\GameOptimizer\SelfTest\RegKv", false); } catch { Diag.Hit("selftest.regkv.clean"); }
                try { if (Directory.Exists(sb)) Directory.Delete(sb, true); } catch { Diag.Hit("selftest.regkv.clean"); }
            }

            // 3) Hardware.ReadCpuPowerSummary 复用非托管缓冲连续调用稳定有效
            Hardware.CpuPowerSummary p1 = Hardware.ReadCpuPowerSummary();
            Hardware.CpuPowerSummary p2 = Hardware.ReadCpuPowerSummary();
            Check(p1.Valid && p2.Valid && p1.CurrentMhz > 0 && p2.MaxMhz > 0,
                  "Rev110 Hardware：复用缓冲读取 CPU 当前/最大/限频摘要",
                  "cur=" + p2.CurrentMhz + " max=" + p2.MaxMhz + " lim=" + p2.LimitMhz);

            // 4) ProcessScan.FindPidsByName 共享扫描器连续查找零扩容且精确命中自身
            string selfName = Process.GetCurrentProcess().ProcessName;
            int selfPid = Process.GetCurrentProcess().Id;
            int[] hits1 = ProcessScan.FindPidsByName(selfName);
            int[] hits2 = ProcessScan.FindPidsByName(selfName + ".exe");
            bool found1 = Array.IndexOf(hits1, selfPid) >= 0;
            bool found2 = Array.IndexOf(hits2, selfPid) >= 0;
            Check(found1 && found2,
                  "Rev110 ProcessScan：共享缓冲按名查 PID 连续命中",
                  "pid=" + selfPid + " hits=" + hits1.Length + "/" + hits2.Length);

            // 5) Power.Ac 新增原生别名（SUB_USB/USBSELECTSUSPEND 与 PROCTHROTTLEMIN）
            uint usbVal, minVal;
            bool usbOk = Power.ReadAcSetting("SUB_USB", "USBSELECTSUSPEND", out usbVal);
            bool minOk = Power.ReadAcSetting("SUB_PROCESSOR", "PROCTHROTTLEMIN", out minVal);
            Check(usbOk && minOk,
                  "Rev110 Power.Ac：新增 SUB_USB/USBSELECTSUSPEND 与 PROCTHROTTLEMIN 原生读取",
                  "usb=" + usbVal + " min=" + minVal);

            // 6) WinNative.FlushDnsCache 在只读模式下严格短路（防离屏渲染副作用）
            bool prevRo = Power.ReadOnlyMode;
            try
            {
                Power.ReadOnlyMode = true;
                bool blocked = !WinNative.FlushDnsCache();
                Check(blocked, "Rev110 WinNative：只读模式下 FlushDnsCache 严格短路", null);
            }
            finally { Power.ReadOnlyMode = prevRo; }
        }
    }
}
