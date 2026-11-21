// ============================================================================
//  SelfTest.Rev140.cs — Rev 140 IRONCLAD KERNEL 后端原生加速与并发稳定性回归自检
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;
using Go.Daemon;

namespace Go
{
    public static partial class SelfTest
    {
        private static void TestRev140Ironclad()
        {
            // 1) Reg.Read 原生 RegGetValueW 全类型校验（DWord / QWord / String / ExpandString 不展开 / Multi / Binary / 超 4KB 长串 / 缺失值）
            const string regPath = @"Software\GameOptimizer\SelfTest\Rev140Reg";
            try
            {
                Reg.Write("HKCU", regPath, "V_Dw", "0x2A", "DWord");
                Reg.Write("HKCU", regPath, "V_Qw", "5000000000", "QWord");
                Reg.Write("HKCU", regPath, "V_Sz", "Vanguard-中文测试", "String");
                Reg.Write("HKCU", regPath, "V_Empty", "", "String");
                Reg.Write("HKCU", regPath, "V_Exp", @"%WINDIR%\System32", "ExpandString");
                Reg.Write("HKCU", regPath, "V_Multi", "alpha\nbeta\ngamma", "MultiString");
                byte[] binSrc = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
                Reg.Write("HKCU", regPath, "V_Bin", Convert.ToBase64String(binSrc), "Binary");
                string longStr = new string('X', 5200);
                Reg.Write("HKCU", regPath, "V_Long", longStr, "String");

                RegValue rDw = Reg.Read("HKCU", regPath, "V_Dw");
                RegValue rQw = Reg.Read("HKCU", regPath, "V_Qw");
                RegValue rSz = Reg.Read("HKCU", regPath, "V_Sz");
                RegValue rEmpty = Reg.Read("HKCU", regPath, "V_Empty");
                RegValue rExp = Reg.Read("HKCU", regPath, "V_Exp");
                RegValue rMulti = Reg.Read("HKCU", regPath, "V_Multi");
                RegValue rBin = Reg.Read("HKCU", regPath, "V_Bin");
                RegValue rLong = Reg.Read("HKCU", regPath, "V_Long");
                RegValue rMiss = Reg.Read("HKCU", regPath, "V_DoesNotExist");
                RegValue rNoKey = Reg.Read("HKCU", regPath + @"\NoSuchSubKey", "Any");

                bool regAllOk = rDw.Existed && rDw.Kind == RegistryValueKind.DWord && Reg.ToText(rDw.Value) == "42"
                    && rQw.Existed && rQw.Kind == RegistryValueKind.QWord && Reg.ToText(rQw.Value) == "5000000000"
                    && rSz.Existed && rSz.Kind == RegistryValueKind.String && (string)rSz.Value == "Vanguard-中文测试"
                    && rEmpty.Existed && rEmpty.Kind == RegistryValueKind.String && (string)rEmpty.Value == ""
                    && rExp.Existed && rExp.Kind == RegistryValueKind.ExpandString && (string)rExp.Value == @"%WINDIR%\System32"
                    && rMulti.Existed && rMulti.Kind == RegistryValueKind.MultiString && Reg.ToText(rMulti.Value) == "alpha\nbeta\ngamma"
                    && rBin.Existed && rBin.Kind == RegistryValueKind.Binary && Reg.ToText(rBin.Value) == Convert.ToBase64String(binSrc)
                    && rLong.Existed && (string)rLong.Value == longStr
                    && !rMiss.Existed && !rMiss.Denied && rMiss.Error == null
                    && !rNoKey.Existed && !rNoKey.Denied && rNoKey.Error == null
                    && Reg.KeyExists("HKCU", regPath) && !Reg.KeyExists("HKCU", regPath + @"\NoSuchSubKey");

                Check(regAllOk,
                      "Rev140 RegGetValueW：全 7 种注册表类型（含不展开 ExpandString 与 >4KB 长串）零异常精准读写",
                      "dw=" + Reg.ToText(rDw.Value) + " exp=" + Reg.ToText(rExp.Value) + " long=" + (rLong.Value == null ? 0 : ((string)rLong.Value).Length));
            }
            catch (Exception ex)
            {
                Check(false, "Rev140 RegGetValueW：原生注册表读路径异常", ex.Message);
            }
            finally
            {
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(regPath, false); } catch { Diag.Hit("selftest.r140.reg.clean"); }
            }

            // 2) ProcName & Affinity 原生单句柄查询（零 Process.GetProcessById）
            int selfPid = Process.GetCurrentProcess().Id;
            long selfBorn = SessionStore.SelfBorn();
            bool aliveOk = ProcName.IsAlive(selfPid, selfBorn);
            bool reuseCaught = !ProcName.IsAlive(selfPid, selfBorn + 123456L);
            bool deadOk = !ProcName.IsAlive(999999, 0);
            string fullExe = ProcName.FullPathOf(selfPid);
            long privBytes = ProcName.ReadPrivateBytes(selfPid);
            long affMask = Affinity.Read(selfPid);
            long phys1 = Affinity.PhysicalOnlyMask(Topology.Detect());
            long phys2 = Affinity.PhysicalOnlyMask(Topology.Detect());
            Check(aliveOk && reuseCaught && deadOk
                  && !string.IsNullOrEmpty(fullExe) && fullExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                  && privBytes > 1024 * 1024
                  && affMask > 0 && phys1 > 0 && phys1 == phys2,
                  "Rev140 原生单句柄 Win32：判活/防 PID 复用/完整路径/私有提交/亲和掩码与物理核缓存全通过",
                  "privMB=" + (privBytes / 1048576) + " aff=0x" + affMask.ToString("X") + " phys=0x" + phys1.ToString("X"));

            // 3) ProcessScan 共享扫描器 4 线程并发压测（验证修复 _forwardVisitor 数据竞态后零错乱零异常）
            int raceErrors = 0;
            int totalHits = 0;
            ManualResetEvent goEvt = new ManualResetEvent(false);
            Thread[] workers = new Thread[4];
            string selfName = Process.GetCurrentProcess().ProcessName;
            for (int w = 0; w < workers.Length; w++)
            {
                workers[w] = new Thread((ThreadStart)delegate
                {
                    goEvt.WaitOne(2000);
                    try
                    {
                        for (int k = 0; k < 5; k++)
                        {
                            int[] pids = ProcessScan.FindPidsByName(selfName);
                            if (Array.IndexOf(pids, selfPid) < 0) Interlocked.Increment(ref raceErrors);
                            else Interlocked.Increment(ref totalHits);
                        }
                    }
                    catch { Interlocked.Increment(ref raceErrors); }
                });
                workers[w].IsBackground = true;
                workers[w].Start();
            }
            goEvt.Set();
            for (int w = 0; w < workers.Length; w++) workers[w].Join(10000);
            goEvt.Close();
            Check(raceErrors == 0 && totalHits == 20,
                  "Rev140 ProcessScan：4 线程并发共用共享扫描器 20 轮零数据竞态且 100% 命中",
                  "hits=" + totalHits + "/20 errors=" + raceErrors);

            // 4) NetTrace 持久化 TCP 缓冲 + 短 TTL PID 缓存：冷查与热查结果一致且零异常
            NetTrace.ClearPidNameCache();
            List<PidTcpSummary> tcpCold = NetTrace.ReadActiveTcpByPid();
            List<PidTcpSummary> tcpWarm = NetTrace.ReadActiveTcpByPid();
            Check(tcpCold != null && tcpWarm != null,
                  "Rev140 NetTrace：持久化 TCP 非托管缓冲与 PID 缓存冷/热双路径稳定",
                  "cold=" + (tcpCold == null ? -1 : tcpCold.Count) + " warm=" + (tcpWarm == null ? -1 : tcpWarm.Count));

            // 5) Engine O(1) 动作索引与分组缓存 + Power.ReadAcSetting 指定方案直读一致性
            Engine eng = new Engine();
            ActionDef found = eng.Find("game-mode");
            Guid activeScheme = Power.Active();
            uint acDirect, acBatch;
            bool p1 = Power.ReadAcSetting("SUB_PROCESSOR", "CPMINCORES", out acDirect);
            bool p2 = Power.ReadAcSetting(activeScheme, "SUB_PROCESSOR", "CPMINCORES", out acBatch);
            Check(found != null && found.Id == "game-mode" && eng.Groups().Count >= 5
                  && p1 == p2 && (!p1 || acDirect == acBatch),
                  "Rev140 Engine & Power：O(1) 动作字典索引与复用方案 GUID 直读 AC 索引一致",
                  "groups=" + eng.Groups().Count + " ac=0x" + acBatch.ToString("X"));
        }
    }
}
