// ============================================================================
//  SelfTest.cs — 无界面自检：账本往返 / 注册表原子性 / 采样器合理性 / 目录完整性
//  --selftest <outFile> 运行；全部通过返回 0。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Go
{
    public static partial class SelfTest
    {
        private static StringBuilder _log = new StringBuilder();
        private static int _pass;
        private static int _fail;

        private static void Check(bool ok, string name, string detail)
        {
            if (ok) _pass++; else _fail++;
            _log.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail == null ? "" : "  ::  " + detail));
        }

        public static int Run(string outFile)
        {
            _pass = 0; _fail = 0;
            _log.AppendLine("GameOptimizer Rev" + Brand.Rev + " self-test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            _log.AppendLine("elevated=" + Native.IsElevated() + "  cpus=" + Environment.ProcessorCount);
            _log.AppendLine("");

            try
            {
                TestRegistryAtomic();
                TestLedgerRoundTrip();
                TestLedgerSafety();
                TestBatchAtomic();
                TestCatalogIntegrity();
                TestTopology();
                TestPower();
                TestMotion();
                TestSampler();
                TestTimerResolution();
                TestProcessTable();
                TestHardwareAndGpuRev88();
                TestAffinityAndProcessPowerRev88();
                TestNetTraceRev88();
                TestBenchSuiteAndHistoryRev88();
                TestActionsRev88();
                TestRev89Regression();
                TestRev110Aether();
                TestRev130Vanguard();
                TestRev140Ironclad();
                TestRev141DesktopInstall();
                // 这两条必须最后跑：容量断言会故意把 Diag 表灌满，之后的 tag 都会并入 __overflow__。
                TestLedgerSandbox();
                TestDiagCapacity();
            }
            catch (Exception ex)
            {
                _log.AppendLine("[FAIL] 未捕获异常: " + ex);
                _fail++;
            }

            _log.AppendLine("");
            _log.AppendLine("RESULT  pass=" + _pass + "  fail=" + _fail);
            string text = _log.ToString();
            try { File.WriteAllText(outFile, text, new UTF8Encoding(false)); }
            catch (Exception ex)
            {
                // 报告落不了盘：stdout 仍有全文，但调用方（CI/agent）读的是文件。
                // 只记 Diag 不改退出码的话，调用方拿到"成功"却读到上一轮的残留报告。
                Diag.Fail("selftest.output", ex);
                Console.WriteLine("[FAIL] 报告无法写入 " + outFile + "：" + ex.Message);
                _fail++;
            }
            Console.WriteLine(text);
            return _fail == 0 ? 0 : 1;
        }

        // ---- 1. 注册表原子读改写 --------------------------------------------
        private static void TestRegistryAtomic()
        {
            const string path = @"Software\GameOptimizer\SelfTest";
            const string name = "ProbeValue";
            try
            {
                Reg.Delete("HKCU", path, name);
                RegValue before = Reg.Read("HKCU", path, name);
                // 断言必须同时看 Error：只判 Existed==false 时，读失败（Error 非空）也算“不存在”，
                // 于是 Reg.Read 把「值不存在」误判成「读取失败」两版都能过 —— 这条断言形同虚设。
                Check(!before.Existed && before.Error == null, "注册表：删除后读到「不存在」", before.Error);

                Reg.Write("HKCU", path, name, "42", "DWord");
                RegValue after = Reg.Read("HKCU", path, name);
                Check(after.Existed && Reg.ToText(after.Value) == "42", "注册表：写入 DWord 并读回", Reg.ToText(after.Value));

                Reg.Write("HKCU", path, name, "hello", "String");
                RegValue str = Reg.Read("HKCU", path, name);
                Check(str.Existed && Reg.ToText(str.Value) == "hello", "注册表：类型可切换为 String", Reg.ToText(str.Value));

                Reg.Delete("HKCU", path, name);
                RegValue gone = Reg.Read("HKCU", path, name);
                Check(!gone.Existed && gone.Error == null, "注册表：删除后恢复「不存在」", gone.Error);

                // 删除型动作（如恢复 MPO 要删 Dwm\OverlayTestMode）完全依赖这条语义：
                // 值不存在必须返回「不存在」而不是「读取失败」，否则删成功也会被判失败并回滚。
                RegValue deep = Reg.Read("HKCU", @"Software\GameOptimizer\SelfTest\NoSuchSubKey", "NoSuchValue");
                Check(!deep.Existed && !deep.Denied && deep.Error == null, "注册表：键不存在读作「不存在」", deep.Error);

                // 清理测试键
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\GameOptimizer\SelfTest", false); }
                catch (Exception cx) { /* 自检清理失败：会留下一个空测试键，不影响断言 */ Diag.Fail("selftest.cleanup", cx); }
            }
            catch (Exception ex) { Check(false, "注册表：原子读改写", ex.Message); }
        }

        // ---- 2. 账本往返（写前落账 → 还原到原值）-----------------------------
        private static void TestLedgerRoundTrip()
        {
            const string path = @"Software\GameOptimizer\SelfTest";
            const string name = "RoundTrip";
            Engine eng = new Engine();
            try
            {
                Reg.Write("HKCU", path, name, "ORIGINAL", "String");
                int ledgerBefore = eng.Ledger.Count;

                ActionDef a = new ActionDef();
                a.Id = "selftest-roundtrip";
                a.Name = "自检往返";
                a.Group = "selftest";
                a.Ops = new Op[] { new Op { Kind = "Reg", Hive = "HKCU", Path = path, Name = name, Type = "String", Value = "CHANGED" } };

                ApplyReport rep = eng.Apply(a);
                Check(rep.Ok, "账本：应用动作成功", rep.Lines.Count > 0 ? rep.Lines[0] : "");
                Check(Reg.ToText(Reg.Read("HKCU", path, name).Value) == "CHANGED", "账本：新值已落盘", null);
                Check(eng.Ledger.Count == ledgerBefore + 1, "账本：新增 1 条写前记录",
                      "before=" + ledgerBefore + " after=" + eng.Ledger.Count);

                ApplyReport rv = eng.RevertAction("selftest-roundtrip");
                Check(rv.Ok, "账本：还原成功", null);
                Check(Reg.ToText(Reg.Read("HKCU", path, name).Value) == "ORIGINAL", "账本：读回原始值 ORIGINAL",
                      Reg.ToText(Reg.Read("HKCU", path, name).Value));
                Check(eng.Ledger.Count == ledgerBefore, "账本：还原后记录被移除",
                      "now=" + eng.Ledger.Count);

                bool audited = false;
                try
                {
                    string audit = Path.Combine(eng.Ledger.DirectoryPath, "audit-" + DateTime.Now.ToString("yyyyMM") + ".log");
                    if (File.Exists(audit)) audited = File.ReadAllText(audit).Contains("自检往返");
                }
                catch (Exception ax)
                {
                    // 读不到审计日志 → 下面的断言会 FAIL；把读失败的原因也记下来。
                    Diag.Fail("selftest.audit.read", ax);
                }
                Check(audited, "账本：审计日志已追加", null);
            }
            catch (Exception ex) { Check(false, "账本：往返", ex.Message); }
            finally
            {
                try { Reg.Delete("HKCU", path, name); }
                catch (Exception cx) { Diag.Fail("selftest.cleanup", cx); }
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\GameOptimizer\SelfTest", false); }
                catch (Exception cx) { Diag.Fail("selftest.cleanup", cx); }
            }
        }

        // ---- 2.5 账本安全边界（只读 / 污染粘性 / 外部改写）--------------------
        private static void TestLedgerSafety()
        {
            string oldDir = Ledger.DirOverride;
            bool oldRo = Ledger.ReadOnlyMode;
            string root = Path.Combine(Path.GetTempPath(), "GameOptimizer", "selftest-ledger-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);

                // ① 只读模式：损坏的账本只能抢救到内存，绝不能迁移/覆盖磁盘原件。
                string roDir = Path.Combine(root, "ro");
                Directory.CreateDirectory(roDir);
                string roFile = Path.Combine(roDir, "ledger.json");
                string corrupt = "{\"Ver\":2,\"Entries\":[{\"Id\":\"x\"";
                File.WriteAllText(roFile, corrupt, new UTF8Encoding(false));
                Ledger.DirOverride = roDir;
                Ledger.ReadOnlyMode = true;
                Ledger ro = new Ledger();
                bool quarantined = Directory.GetFiles(roDir, "*.corrupt-*").Length > 0;
                string after = File.ReadAllText(roFile, Encoding.UTF8);
                bool saveThrew = false;
                try { ro.Save(); }
                catch (InvalidOperationException) { saveThrew = true; }
                Check(!quarantined && after == corrupt && saveThrew,
                      "账本：只读模式不迁移、不覆盖、拒绝写盘",
                      "quarantine=" + quarantined + " intact=" + (after == corrupt) + " saveThrew=" + saveThrew);

                // ② 污染粘性：一次读失败后，即使文件被外部删掉也必须继续拒绝覆盖。
                string badDir = Path.Combine(root, "bad");
                Directory.CreateDirectory(badDir);
                Directory.CreateDirectory(Path.Combine(badDir, "ledger.json"));   // 目录冒充文件 → 读必失败
                Ledger.DirOverride = badDir;
                Ledger.ReadOnlyMode = false;
                Ledger bad = new Ledger();
                bool poisoned = bad.Poisoned;
                Directory.Delete(Path.Combine(badDir, "ledger.json"));
                bool stillThrew = false;
                try { bad.Save(); }
                catch (InvalidOperationException) { stillThrew = true; }
                Check(poisoned && stillThrew, "账本：读失败后本会话拒绝覆盖写（文件消失也不解除）",
                      "poisoned=" + poisoned + " threwAfterDelete=" + stillThrew);

                // ③ 外部改写：磁盘指纹变了就不能整份覆盖（会抹掉别人刚写的记录）。
                string exDir = Path.Combine(root, "ext");
                Directory.CreateDirectory(exDir);
                Ledger.DirOverride = exDir;
                Ledger ex = new Ledger();
                ex.Add(NewTestEntry("selftest-ext-1"));
                ex.Save();
                string exFile = Path.Combine(exDir, "ledger.json");
                string external = "{\"Ver\":2,\"Entries\":[]}";
                File.WriteAllText(exFile, external, new UTF8Encoding(false));
                ex.Add(NewTestEntry("selftest-ext-2"));
                bool blocked = false;
                try { ex.Save(); }
                catch (InvalidOperationException) { blocked = true; }
                string survived = File.ReadAllText(exFile, Encoding.UTF8);
                Check(blocked && survived == external, "账本：检测到外部改写后拒绝覆盖",
                      "blocked=" + blocked + " intact=" + (survived == external));
            }
            catch (Exception ex)
            {
                Check(false, "账本：安全边界", ex.Message);
            }
            finally
            {
                Ledger.DirOverride = oldDir;
                Ledger.ReadOnlyMode = oldRo;
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception cx) { Diag.Fail("selftest.ledger.cleanup", cx); }
            }
        }

        private static LedgerEntry NewTestEntry(string actionId)
        {
            LedgerEntry e = new LedgerEntry();
            e.Id = Guid.NewGuid().ToString("N");
            e.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            e.ActionId = actionId;
            e.ActionName = "自检记录";
            e.Kind = "Reg";
            e.Hive = "HKCU";
            e.Path = @"Software\GameOptimizer\SelfTest";
            e.Name = actionId;
            e.ValueType = "String";
            e.Existed = false;
            e.OldValue = null;
            e.NewValue = "x";
            return e;
        }

        // ---- 2.6 批量事务：一个 op 失败，整批不落 ---------------------------------
        private static void TestBatchAtomic()
        {
            const string path = @"Software\GameOptimizer\SelfTest";
            Engine eng = new Engine();
            try
            {
                Reg.Write("HKCU", path, "BatchA", "ORIGINAL", "String");
                Reg.Delete("HKCU", path, "BatchB");
                int before = eng.Ledger.Count;

                ActionDef a = new ActionDef();
                a.Id = "selftest-batch";
                a.Name = "自检批量";
                a.Group = "selftest";
                a.Ops = new Op[]
                {
                    new Op { Kind = "Reg", Hive = "HKCU", Path = path, Name = "BatchA", Type = "String", Value = "CHANGED" },
                    // 第二个 op 的值不是合法 DWord：写入步会抛，整批必须回滚
                    new Op { Kind = "Reg", Hive = "HKCU", Path = path, Name = "BatchB", Type = "DWord", Value = "not-a-number" }
                };

                ApplyReport rep = eng.Apply(a);
                string gotA = Reg.ToText(Reg.Read("HKCU", path, "BatchA").Value);
                RegValue b = Reg.Read("HKCU", path, "BatchB");
                Check(!rep.Ok && gotA == "ORIGINAL" && !b.Existed && b.Error == null,
                      "批量：第二个 op 失败时第一个 op 被回滚（系统不停在半改状态）",
                      "ok=" + rep.Ok + " A=" + gotA + " B=" + (b.Existed ? Reg.ToText(b.Value) : "absent"));
                Check(eng.Ledger.Count == before, "批量：失败后不留下写前记录",
                      "before=" + before + " after=" + eng.Ledger.Count);
                Check(rep.RolledBack >= 1, "批量：报告里写明回滚了几项", "rolled=" + rep.RolledBack);
            }
            catch (Exception ex)
            {
                Check(false, "批量：事务原子性", ex.Message);
            }
            finally
            {
                try { Reg.Delete("HKCU", path, "BatchA"); Reg.Delete("HKCU", path, "BatchB"); }
                catch (Exception cx) { Diag.Fail("selftest.cleanup", cx); }
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\GameOptimizer\SelfTest", false); }
                catch (Exception cx) { Diag.Fail("selftest.cleanup", cx); }
            }
        }

        // ---- 3. 动作目录完整性 -----------------------------------------------
        private static void TestCatalogIntegrity()
        {
            List<ActionDef> all = Catalog.Build();
            Check(all.Count >= 20, "目录：动作数量", all.Count + " 项");

            HashSet<string> ids = new HashSet<string>();
            bool dup = false, empty = false, badRisk = false;
            foreach (ActionDef a in all)
            {
                if (!ids.Add(a.Id)) dup = true;
                if (a.Ops == null || a.Ops.Length == 0) { empty = true; continue; }
                if (a.Risk != "A" && a.Risk != "B" && a.Risk != "C") badRisk = true;
                foreach (Op o in a.Ops)
                {
                    if (o.Kind == "Reg" && (string.IsNullOrEmpty(o.Hive) || string.IsNullOrEmpty(o.Path)))
                        empty = true;
                }
            }
            Check(!dup, "目录：动作 Id 唯一", null);
            Check(!empty, "目录：每项都有操作与路径", null);
            Check(!badRisk, "目录：风险等级合法", null);

            int remediation = 0;
            foreach (ActionDef a in all) if (a.Remediation) remediation++;
            Check(remediation >= 5, "目录：修复残留项齐备", remediation + " 项");
        }

        // ---- 3.5 CPU 拓扑 ------------------------------------------------------
        private static void TestTopology()
        {
            Topology t = Topology.Detect();
            Check(t.Logical == Environment.ProcessorCount, "拓扑：逻辑处理器数与系统一致", t.Logical.ToString());
            Check(t.Cores > 0 && t.Cores <= t.Logical, "拓扑：物理核心数合理", t.Cores + " 核 / " + t.Logical + " 线程");
            Check(t.Ccds >= 1 && t.Ccds <= t.Cores, "拓扑：簇数落在 1..核心数", t.Ccds + " 簇 × " + t.CoresPerCcd + " 核");
            Check(t.CoreProbed, "拓扑：核心关系来自内核查询（非估算回退）", t.Cores + " 核");
            Check(t.Ccd0Mask > 0, "拓扑：CCD0 掩码非零", "0x" + t.Ccd0Mask.ToString("X"));
            long limit = t.Logical >= 63 ? long.MaxValue : ((1L << t.Logical) - 1);
            Check((t.Ccd0Mask & ~limit) == 0, "拓扑：掩码不超出逻辑处理器范围", "0x" + t.Ccd0Mask.ToString("X"));
            _log.AppendLine("        实测拓扑：" + t.Describe());
        }

        // ---- 3.6 电源计划：读得到、切得动（切换只做只读校验）--------------------
        private static void TestPower()
        {
            Guid active = Power.Active();
            Check(active != Guid.Empty, "电源：能读到活动方案", active.ToString());

            List<Guid> list = Power.Schemes();
            Check(list.Count >= 1, "电源：能枚举本机方案", list.Count + " 个");

            bool contains = false;
            for (int i = 0; i < list.Count; i++) if (list[i] == active) { contains = true; break; }
            Check(contains, "电源：活动方案出现在枚举结果里", null);

            string name = Power.FriendlyName(active);
            Check(!string.IsNullOrEmpty(name), "电源：方案名可读（本地化）", name);

            Check(Power.KnownEn(Power.Balanced) == "BALANCED"
                  && Power.KnownEn(Power.HighPerf) == "HIGH PERFORMANCE"
                  && Power.KnownEn(Power.Saver) == "POWER SAVER",
                  "电源：内置方案英文标识稳定", null);

            string desc = Power.Describe(active);
            Check(desc != null && desc.IndexOf("·") > 0, "电源：界面摘要格式正确", desc);
        }

        // ---- 3.7 动效引擎 ------------------------------------------------------
        private static void TestMotion()
        {
            Check(Math.Abs(Motion.EaseOutCubic(0)) < 1e-9 && Math.Abs(Motion.EaseOutCubic(1) - 1) < 1e-9,
                  "动效：缓出曲线端点精确", null);

            bool mono = true;
            double prev = -1;
            for (int i = 0; i <= 20; i++)
            {
                double v = Motion.EaseOutCubic(i / 20.0);
                if (v < prev - 1e-9) mono = false;
                prev = v;
            }
            Check(mono, "动效：缓出曲线单调不减", null);

            bool animateWas = Ui.Animate;
            Ui.Animate = false;
            double jumped = -1;
            Motion.Tween(0, 1, 200, delegate (double v) { jumped = v; });
            Check(Math.Abs(jumped - 1) < 1e-9, "动效：关闭动画时立即落到终点", jumped.ToString("0.00"));

            Ui.Animate = true;
            double reached = 0;
            Motion.Tween(0, 10, 100, delegate (double v) { reached = v; });
            Motion.FlushForTest(800);
            Check(Math.Abs(reached - 10) < 1e-6, "动效：补间在时限内到达终点", reached.ToString("0.000"));
            Check(!Motion.Busy, "动效：结束后驱动自动停机", "live=" + Motion.LiveCount);

            // ---- 交互：按钮状态机（纯逻辑，可逐条断言）----
            // 桌面程序最容易被察觉的业余感，全在这五条里。
            ClickGate gate = new ClickGate();
            gate.Press(1000);
            bool hit = gate.Release(true, 1050);
            Check(hit, "交互：按下后在控件内抬起 = 一次点击", null);

            gate.Press(1000);
            bool missOut = gate.Release(false, 1050);
            Check(!missOut, "交互：按下后拖到控件外抬起 = 取消（不误触）", null);

            gate.Cancel();
            bool noPress = gate.Release(true, 1100);
            Check(!noPress, "交互：没有按下就不会有点击（抬起事件不凭空触发）", null);

            gate.Press(2000);
            gate.Cancel();
            bool afterCancel = gate.Release(true, 2050);
            Check(!afterCancel, "交互：取消后再抬起不再触发", null);

            gate.Press(3000);
            bool backwards = gate.Release(true, 2999);
            Check(!backwards, "交互：时钟回退时宁可不动手", null);

            Ui.Animate = animateWas;
        }

        // ---- 4. 采样器合理性 --------------------------------------------------
        private static void TestSampler()
        {
            PerfSampler p = new PerfSampler();
            p.Start();
            Thread.Sleep(2600);
            Snapshot s = p.Current;
            p.Stop();

            Check(s.CpuTotal >= 0 && s.CpuTotal <= 1.0001, "采样：CPU 在 0..1", s.CpuTotal.ToString("0.000"));
            Check(s.Cores.Length == Environment.ProcessorCount, "采样：核心数与系统一致",
                  s.Cores.Length + " vs " + Environment.ProcessorCount);
            Check(s.MemTotalMB > 1024, "采样：物理内存总量合理", s.MemTotalMB.ToString("0") + " MB");
            Check(s.MemAvailMB > 0 && s.MemAvailMB <= s.MemTotalMB, "采样：可用内存合理", s.MemAvailMB.ToString("0") + " MB");
            Check(s.MemStandbyMB >= 0, "采样：待机列表可读", s.MemStandbyMB.ToString("0") + " MB");
            Check(s.KernelPagedMB >= 0 && s.KernelPagedMB < s.MemTotalMB, "采样：内核分页池合理", s.KernelPagedMB.ToString("0") + " MB");
            Check(s.KernelNonpagedMB > 0 && s.KernelNonpagedMB < s.MemTotalMB, "采样：内核非分页池合理", s.KernelNonpagedMB.ToString("0") + " MB");
            // P99 只保证落在 [0, MAX] 内；"≥ AVG" 不是数学定律（长尾分布下 AVG 可以超过 P99），
            // 旧断言在重尾机器上会误报。真正要守住的是三者的自洽性。
            Check(s.JitterP99Us >= 0 && s.JitterP99Us <= s.JitterMaxUs && s.JitterAvgUs <= s.JitterMaxUs,
                  "采样：P99 / AVG / MAX 自洽", s.JitterP99Us.ToString("0") + " / " + s.JitterAvgUs.ToString("0") + " / " + s.JitterMaxUs.ToString("0"));
            Check(s.TimerMs > 0 && s.TimerMs <= 16, "采样：定时器精度在合理区间", s.TimerMs.ToString("0.000") + " ms");
            // 不能写死 >20：干净虚拟机/精简系统上本来就没那么多进程，那会让断言在别人的机器上误红。
            // 要守的是"真的读到了"，所以与系统 API 的枚举结果对齐。
            int realProcs = 0;
            try { realProcs = System.Diagnostics.Process.GetProcesses().Length; }
            catch (Exception ex) { Diag.Fail("selftest.proccount", ex); }
            Check(s.ProcessCount > 0 && (realProcs <= 0 || Math.Abs(s.ProcessCount - realProcs) <= Math.Max(8, realProcs / 5)),
                  "采样：进程数可读且与系统枚举一致", s.ProcessCount + " vs " + realProcs);
        }

        // ---- 5.5 账本沙箱：自检绝不能写进用户真实账本 --------------------------
        //  这条断言是给下一轮的人兜底的：任何新的自检/压测都必须先 UseSandboxDir()，
        //  否则测试数据会混进用户看到的审计账本，把"每一条都真实发生过"这句话毁掉。
        private static void TestLedgerSandbox()
        {
            string cur = Ledger.CurrentDir;
            string tmp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "GameOptimizer", "ledger");
            string curFull = Path.GetFullPath(cur).TrimEnd('\\');
            string realFull = Path.GetFullPath(real).TrimEnd('\\');

            Check(curFull.StartsWith(tmp, StringComparison.OrdinalIgnoreCase),
                  "账本沙箱：CurrentDir 落在 TEMP 下", cur);
            Check(!string.Equals(curFull, realFull, StringComparison.OrdinalIgnoreCase),
                  "账本沙箱：CurrentDir 不是真实账本目录", real);

            // 真实目录的指纹：文件集合 + 长度 + 最后写入时间。空目录也要能被区分。
            string stampBefore = LedgerStamp(real);
            Ledger probe = new Ledger();
            LedgerEntry e = new LedgerEntry();
            e.Id = "selftest-sandbox-" + Guid.NewGuid().ToString("N");
            e.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            e.ActionId = "selftest-sandbox";
            e.ActionName = "沙箱自检";
            e.Kind = "Reg";
            e.Hive = "HKCU";
            e.Path = @"Software\GameOptimizer\SelfTest";
            e.Name = "Sandbox";
            bool wrote = false;
            string why = null;
            try
            {
                probe.Add(e);
                probe.Save();
                wrote = File.Exists(Path.Combine(cur, "ledger.json"));
            }
            catch (Exception ex)
            {
                why = ex.Message;
                Diag.Fail("selftest.sandbox.write", ex);
            }
            probe.Remove(e.Id);
            try { probe.Save(); }
            catch (Exception ex) { why = why ?? ex.Message; Diag.Fail("selftest.sandbox.cleanup", ex); }

            string stampAfter = LedgerStamp(real);
            Check(wrote, "账本沙箱：写入落进沙箱目录（ledger.json 存在）", why);
            Check(stampBefore == stampAfter, "账本沙箱：真实账本目录未被写入",
                  "before=" + stampBefore + "  after=" + stampAfter);
        }

        private static string LedgerStamp(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return "(absent)";
                string f = Path.Combine(dir, "ledger.json");
                StringBuilder sb = new StringBuilder("exists;");
                if (File.Exists(f))
                {
                    FileInfo fi = new FileInfo(f);
                    sb.Append(fi.Length).Append('@').Append(fi.LastWriteTimeUtc.Ticks);
                }
                sb.Append(";tmp=").Append(File.Exists(f + ".tmp") ? "1" : "0");
                sb.Append(";corrupt=").Append(Directory.GetFiles(dir, "ledger.json.corrupt-*").Length);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                // 连指纹都读不到：返回一个不可能与正常值相同的串，断言会失败并说明原因。
                Diag.Fail("selftest.sandbox.stamp", ex);
                return "(unreadable:" + ex.GetType().Name + ")";
            }
        }

        // ---- 5.6 Diag 容量与淘汰 ----------------------------------------------
        //  Diag 的契约是"总格子 ≤200，超出的新 tag 一律并入 __overflow__"。
        //  这条断言不依赖任何业务路径：它只验证容量机制本身，给下一轮改 Diag 的人兜底。
        private static void TestDiagCapacity()
        {
            int tagsBefore = Diag.Tags;
            int totalBefore = Diag.Total;

            for (int i = 0; i < 300; i++) Diag.Hit("selftest.cap." + i);
            int tags = Diag.Tags;
            Check(tags <= 200, "Diag：300 个不同 tag 后总格子仍 ≤ 200",
                  "before=" + tagsBefore + "  after=" + tags);
            Check(Diag.Count("__overflow__") > 0, "Diag：容量耗尽后新 tag 归入 __overflow__",
                  "overflow=" + Diag.Count("__overflow__"));

            // 稳态零增长：同一个 tag 再报 1000 次，格子数不能变（否则内存随次数增长）。
            for (int i = 0; i < 1000; i++) Diag.Hit("selftest.cap.0");
            Check(Diag.Tags == tags, "Diag：重复上报 1000 次不新增格子", "tags=" + Diag.Tags + "  was=" + tags);
            Check(Diag.Total == totalBefore + 1300, "Diag：重复上报全部计入 Total",
                  "before=" + totalBefore + "  after=" + Diag.Total);
            _log.AppendLine("        Diag 容量实测：tags=" + Diag.Tags + "  total=" + Diag.Total
                            + "  （此后本轮自检的 tag 均在 __overflow__ 里计数）");
        }

        // ---- 进程表（共享快照服务）-------------------------------------------
        /// <summary>
        /// 断言对准系统契约：同一时刻扫出来的行必须自洽；PID/创建时间/工作集/线程数
        /// 不能是"结构体错位后看起来合理"的垃圾值。只判 Count>0 抓不到偏移错误。
        /// </summary>
        private static void TestProcessTable()
        {
            ProcessTable t = null;
            try
            {
                t = new ProcessTable();
                ProcSnapshot s = t.Refresh();
                Check(s.Ok && s.Count > 10, "进程表：NtQuery 快照可读",
                      "ok=" + s.Ok + " count=" + s.Count + " err=" + s.Error + " us=" + s.ScanUs);

                int selfPid = 0;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess()) selfPid = p.Id;
                ProcEntry self = null;
                int badPid = 0, badName = 0, badWs = 0;
                for (int i = 0; i < s.Count; i++)
                {
                    ProcEntry e = s.Items[i];
                    if (e == null || e.Pid <= 0) { badPid++; continue; }
                    if (string.IsNullOrEmpty(e.Name)) badName++;
                    if (e.WorkingSetBytes < 0) badWs++;
                    if (e.Pid == selfPid) self = e;
                }
                Check(badPid == 0 && badName <= 1 && badWs == 0, "进程表：行字段自洽（PID/映像名/工作集）",
                      "badPid=" + badPid + " noName=" + badName + " negWs=" + badWs);
                Check(self != null && self.Born > 0 && self.WorkingSetBytes > 0 && self.Threads > 0,
                      "进程表：自身进程可读（创建时间/工作集/线程数）",
                      self == null ? "self missing"
                                   : ("born=" + self.Born + " ws=" + self.WorkingSetBytes + " th=" + self.Threads));

                // 跨拍复用名字：不是"看起来相等"，而是引用相等 —— 每拍重新 PtrToStringUni
                // 会让"稳态零分配"名存实亡。双缓冲各带一份名字缓存，所以第三拍
                // （回到第一块缓冲）才应该命中复用。
                string before = self == null ? null : self.Name;
                t.Refresh();
                ProcSnapshot s3 = t.Refresh();
                ProcEntry self3 = null;
                for (int i = 0; i < s3.Count; i++)
                    if (s3.Items[i] != null && s3.Items[i].Pid == selfPid) { self3 = s3.Items[i]; break; }
                Check(self3 != null && object.ReferenceEquals(self3.Name, before),
                      "进程表：跨拍复用映像名字符串（稳态不重复分配）",
                      self3 == null ? "missing" : "same=" + object.ReferenceEquals(self3.Name, before));
            }
            catch (Exception ex)
            {
                Check(false, "进程表：快照", ex.Message);
            }
            finally
            {
                if (t != null) t.Dispose();
            }
        }

        // ---- 5. 定时器请求与释放 ----------------------------------------------
        private static void TestTimerResolution()
        {
            PerfSampler p = new PerfSampler();
            // 强求 0.5ms 是不对的：Windows 会按当时的全局状态给一个"它愿意给的值"，
            // 本机实测常见 1.0ms（忙时甚至原样退回 15.6）。所以这里断言的不再是
            // "我拿到 0.5"，而是**"我报出去的数就是系统此刻的真值"** ——
            // 这才是这一层唯一必须守住的契约。
            double got = p.RequestTimerResolution(0.5);
            uint mn, mx, cur;
            Native.NtQueryTimerResolution(out mn, out mx, out cur);
            double sysMs = cur / 10000.0;
            Check(got > 0 && Math.Abs(got - sysMs) < 0.05,
                  "定时器：请求后报告值与系统实际一致",
                  got.ToString("0.000") + " ms vs 系统 " + sysMs.ToString("0.000") + " ms");
            p.ReleaseTimerResolution();
            Check(p.RequestedTimerMs == 0, "定时器：可释放请求", null);
        }
    }
}
