// ============================================================================
//  DaemonTest.cs — 守护与托盘的无头自检
//  --daemon-test
//  断言链：进程表可读 → 档案转目标表 → 真实进程启动被感知 → 自动挂载 →
//          进程退出被感知 → 系统参数复原 → 托盘图标可装卸 → 状态总线可广播。
//
//  Rev86：Run() 从一个 315 行、嵌套 4 层的直筒子拆成 9 个阶段方法
//  （总线 / 定时器 / 进程表 / 守护 / 账本 / 托盘 / 嗅探 / 会话与拥有权 / 供电+战报+采样+生命周期）。
//  拆的理由不是"行数好看"：原来任何一处抛异常都会把后面所有断言一起吃掉，
//  日志里只留一条"未捕获异常"——看不出是哪个环节崩的。现在每阶段独立，
//  失败的阶段名会进日志；断言编号与失败明细不变（28 条基线一条不少）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Go.Tray;

namespace Go.Daemon
{
    public static partial class DaemonTest
    {
        private static StringBuilder _log = new StringBuilder();
        private static int _pass, _fail;

        private static void Check(bool ok, string name, string detail)
        {
            if (ok) _pass++; else _fail++;
            _log.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail == null ? "" : "  ::  " + detail));
        }

        /// <summary>阶段之间要共享的东西（探针进程/档案），关停清理用它。</summary>
        private sealed class TestState
        {
            public Core Core;
            public Process Probe;
            public string ProbeExe;
            public GameProfile Profile;
        }

        public static int Run()
        {
            _pass = 0; _fail = 0;
            _log.AppendLine("GameOptimizer Rev89 daemon/tray self-test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            _log.AppendLine("elevated=" + Native.IsElevated() + "  cpus=" + Environment.ProcessorCount);
            _log.AppendLine("");

            string prevLedger = Ledger.DirOverride;
            string prevProf = ProfileStore.DirOverride;
            string prevSess = SessionStore.DirOverride;
            string prevHist = SessionHistory.DirOverride;
            if (string.IsNullOrEmpty(prevLedger)) Ledger.UseSandboxDir();

            TestState st = new TestState();
            try
            {
                st.Core = new Core();
                st.Core.DetectHardware();

                RunStage("bus", delegate { StageBus(st); });
                RunStage("timer", delegate { StageTimer(st); });
                RunStage("scan", delegate { StageScan(st); });
                RunStage("watch", delegate { StageWatch(st); });
                RunStage("ledger", delegate { StageLedger(st); });
                RunStage("tray", delegate { StageTray(st); });
                RunStage("sniffer", delegate { StageSniffer(st); });
                RunStage("ownership", delegate { StageOwnership(st); });
                RunStage("power", delegate { StagePower(st); });
                RunStage("power.rev88", delegate { StagePowerRev88(st); });
                RunStage("scanner.rev88", delegate { StageScannerRev88(st); });
                RunStage("autopilot.rev88", delegate { StageAutoPilotRev88(st); });
                RunStage("sessionhistory.rev88", delegate { StageSessionHistoryRev88(st); });
                RunStage("rev89", delegate { StageRev89(st); });
                RunStage("report", delegate { StageReport(st); });
                RunStage("sampling", delegate { StageSampling(st); });
                RunStage("lifecycle", delegate { StageLifecycle(st); });
                RunStage("spin", delegate { StageSpin(st); });
                RunStage("pipeline", delegate { StagePipeline(st); });
            }
            catch (Exception ex)
            {
                _log.AppendLine("[FAIL] 未捕获异常: " + ex);
                _fail++;
            }
            finally
            {
                Cleanup(st);
                SessionHistory.DirOverride = prevHist;
                SessionStore.DirOverride = prevSess;
                ProfileStore.DirOverride = prevProf;
                Ledger.DirOverride = prevLedger;
            }

            _log.AppendLine("");
            _log.AppendLine("RESULT  pass=" + _pass + "  fail=" + _fail);
            string text = _log.ToString();
            try
            {
                string outFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "out", "daemon-test.json");
                File.WriteAllText(outFile, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 报告落不了盘：stdout 仍有全文，但读取方要的是文件。
                Diag.Fail("daemontest.output", ex);
            }
            Console.WriteLine(text);
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// 一个阶段崩掉不能吃掉后面的断言：记一条带阶段名的 FAIL，继续下一段。
        /// 旧实现是一个大 try，第一处异常之后的所有断言都不再执行（日志里只剩"未捕获异常"）。
        /// </summary>
        private static void RunStage(string name, Action body)
        {
            try { body(); }
            catch (Exception ex)
            {
                _fail++;
                _log.AppendLine("[FAIL] 阶段 " + name + " 未捕获异常: " + ex.GetType().Name + ": " + ex.Message);
                _log.AppendLine(ex.StackTrace);
                Diag.Fail("daemontest.stage." + name, ex);
            }
        }

        private static void Cleanup(TestState st)
        {
            try { if (st.Probe != null && !st.Probe.HasExited) st.Probe.Kill(); }
            catch (Exception ex)
            {
                // 探针可能已经自己退出（ping 到点）；收尾失败不能影响报告。
                Diag.Fail("daemontest.probe.finalize", ex);
                _log.AppendLine("[WARN] 探针进程收尾失败: " + ex.Message);
            }
            try { if (st.Probe != null) st.Probe.Dispose(); }
            catch (Exception ex) { Diag.Fail("daemontest.probe.finalize", ex); }
            try { if (st.ProbeExe != null && File.Exists(st.ProbeExe)) File.Delete(st.ProbeExe); }
            catch (Exception ex)
            {
                Diag.Fail("daemontest.probe.finalize", ex);
                _log.AppendLine("[WARN] 探针文件删除失败: " + st.ProbeExe);
            }
            try
            {
                if (st.Core != null && st.Profile != null) st.Core.Profiles.RemoveNoSave(st.Profile);
            }
            catch (Exception ex)
            {
                // 自检档案留在内存里只影响本次进程；但"清理失败"本身要能查。
                Diag.Fail("daemontest.profile.cleanup", ex);
            }
        }

        // ---- 1. 状态总线 ---------------------------------------------------
        private static void StageBus(TestState st)
        {
            Core core = st.Core;
            int hits = 0;
            Dirty seen = Dirty.None;
            Action<Dirty> sub = delegate (Dirty d) { hits++; seen |= d; };
            core.State.On(Dirty.Daemon | Dirty.Timer, sub);
            core.State.Raise(Dirty.Daemon);
            core.State.Raise(Dirty.Ledger);      // 未订阅，不应计数
            core.State.Raise(Dirty.Timer);
            Check(hits == 2 && (seen & Dirty.Daemon) != 0 && (seen & Dirty.Timer) != 0,
                  "总线：按域精确广播，未订阅域不触发", "hits=" + hits + " seen=" + seen);
            core.State.Off(sub);
            core.State.Raise(Dirty.Daemon);
            Check(hits == 2, "总线：可注销订阅", "hits=" + hits);
        }

        // ---- 2. 定时器租约引用计数（旧实现的崩溃点）------------------------
        private static void StageTimer(TestState st)
        {
            Core tc = new Core();
            tc.Timer.Request(TimerOwner.Overview, 0.5);
            bool first = tc.Timer.Active;
            tc.Timer.Request(TimerOwner.Scheduler, 0.5);
            tc.Timer.Release(TimerOwner.Overview);
            bool stillHeld = tc.Timer.Active;       // 调度页仍持有，必须仍为 true
            tc.Timer.Release(TimerOwner.Scheduler);
            bool released = !tc.Timer.Active;
            Check(first && stillHeld && released,
                  "定时器：多持有者引用计数，最后一个退场才释放",
                  "first=" + first + " stillHeld=" + stillHeld + " released=" + released);
        }

        // ---- 3. 进程表 -----------------------------------------------------
        private static void StageScan(TestState st)
        {
            ProcessScan scan = new ProcessScan();
            int seenProcs = 0;
            scan.ForEach(delegate (IntPtr b, int c, int pid, long t) { seenProcs++; return false; });
            Check(seenProcs > 20, "进程表：零分配遍历可读", seenProcs + " 个进程");
            scan.Dispose();
        }

        // ---- 4. 档案 → 目标表 → 真实进程感知 → 退出复原 --------------------
        private static void StageWatch(TestState st)
        {
            Core core = st.Core;
            string dir = Path.Combine(Path.GetTempPath(), "GameOptimizerTest");
            Directory.CreateDirectory(dir);
            st.ProbeExe = Path.Combine(dir, "GoDaemonProbe_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".exe");
            string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
            bool copied = false;
            string copyWhy = null;
            try { File.Copy(src, st.ProbeExe, true); copied = true; }
            catch (Exception ex)
            {
                // 复制失败会让后面所有"真实进程"断言失去意义：原因必须出现在 FAIL 行里。
                Diag.Fail("daemontest.copy", ex);
                copyWhy = ex.GetType().Name + ": " + ex.Message;
            }
            Check(copied, "探针：复制一个真实可执行文件用于模拟游戏",
                  copied ? st.ProbeExe : (st.ProbeExe + " :: " + copyWhy));
            if (!copied) return;

            GameProfile p = new GameProfile();
            p.Id = "daemontest-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            p.Name = "守护自检";
            p.Exe = st.ProbeExe;
            p.PinCcd0 = true;
            p.Timer = true;
            p.HighGpu = false;
            core.Profiles.AddNoSave(p);
            st.Profile = p;

            GameWatcher w = new GameWatcher(core);
            AutoPilot pilot = new AutoPilot(core);
            int started = 0, attached = 0, stopped = 0, detached = 0;
            string startedName = "";
            w.Started += delegate (GameProfile g, int pid)
            {
                startedName = g.Name;
                pilot.Attach(g, pid);
                Interlocked.Increment(ref started);
                Interlocked.Increment(ref attached);
            };
            w.Stopped += delegate (GameProfile g, int pid)
            {
                pilot.Detach(g, pid);
                Interlocked.Increment(ref stopped);
                Interlocked.Increment(ref detached);
            };
            w.Enabled = true;

            st.Probe = new Process();
            st.Probe.StartInfo.FileName = st.ProbeExe;
            st.Probe.StartInfo.Arguments = "-n 40 127.0.0.1";
            st.Probe.StartInfo.UseShellExecute = false;
            st.Probe.StartInfo.CreateNoWindow = true;
            st.Probe.StartInfo.RedirectStandardOutput = true;
            st.Probe.Start();
            int probePid = st.Probe.Id;

            bool detected = WaitFor(delegate { w.PollOnce(); return Interlocked.CompareExchange(ref attached, 0, 0) > 0; }, 6000);
            Check(detected && startedName == "守护自检",
                  "守护：真实进程启动被感知（≤250ms 轮询）", "started=" + started + " name=" + startedName + " pid=" + probePid);
            Check(w.Active != null && w.ActivePid == probePid,
                  "守护：命中 PID 与档案一致", "active=" + (w.Active == null ? "null" : w.Active.Name));
            Check(core.Timer.Active, "守护：自动挂载了 0.5ms 定时器租约", core.Timer.GrantedMs.ToString("0.0") + " ms");

            // ---- PID 复用：外部挂载的 PID 必须解析出创建时间 --------------
            // 托盘「档案切换」与离屏演示走 ForceAttach(p, pid)，旧实现把 _activeBorn 写成 0，
            // 于是这段挂载只按 PID 判活 —— 号码被回收后守护以为游戏还在。
            GameWatcher w2 = new GameWatcher(core);
            long wantBorn = st.Probe.StartTime.ToUniversalTime().ToFileTimeUtc();
            w2.ForceAttach(p, probePid);
            long gotBorn = w2.ActiveBorn;
            w2.Stop();
            Check(gotBorn != 0 && gotBorn == wantBorn,
                  "PID 复用：外部给定 PID 也会解析创建时间", "born=" + gotBorn + " want=" + wantBorn);

            try { st.Probe.Kill(); }
            catch (Exception ex)
            {
                // 进程可能已自行退出（ping -n 40 到点）；后面的 WaitFor 会给出真相。
                Diag.Hit("daemontest.probe.kill");
                _log.AppendLine("[WARN] 探针 kill 失败: " + ex.Message);
            }
            bool gone = WaitFor(delegate { w.PollOnce(); return Interlocked.CompareExchange(ref detached, 0, 0) > 0; }, 6000);
            w.Stop();
            pilot.Stop();
            Check(gone, "守护：进程退出被感知", "stopped=" + stopped);
            Check(!core.Timer.Active, "守护：退出后按租约复原定时器精度", "active=" + core.Timer.Active);
        }

        // ---- 5. 账本闭环（只碰自检自己的键，绝不触碰用户账本）--------------
        private static void StageLedger(TestState st)
        {
            Core core = st.Core;
            string why = null;
            bool ledgerOk = false;
            string ledgerName = @"Software\GameOptimizer\DaemonTest";
            try
            {
                Reg.Write("HKCU", ledgerName, "Probe", "BEFORE", "String");
                ActionDef ad = new ActionDef();
                ad.Id = "daemontest-ledger";
                ad.Name = "守护自检往返";
                ad.Group = "selftest";
                ad.Ops = new Op[] { new Op { Kind = "Reg", Hive = "HKCU", Path = ledgerName, Name = "Probe", Type = "String", Value = "AFTER" } };
                ApplyReport applied = core.Engine.Apply(ad);
                string mid = Reg.ToText(Reg.Read("HKCU", ledgerName, "Probe").Value);
                ApplyReport back = core.Engine.RevertAction("daemontest-ledger");
                string fin = Reg.ToText(Reg.Read("HKCU", ledgerName, "Probe").Value);
                ledgerOk = applied.Ok && back.Ok && mid == "AFTER" && fin == "BEFORE";
                why = "applied=" + Applied(applied) + " value=" + mid + " -> " + fin;
            }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                try { Reg.Delete("HKCU", ledgerName, "Probe"); }
                catch (Exception cx)
                {
                    Diag.Fail("daemontest.cleanup", cx);
                    _log.AppendLine("[WARN] 清理自检注册表值失败: " + cx.Message);
                }
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ledgerName, false); }
                catch (Exception cx)
                {
                    Diag.Fail("daemontest.cleanup", cx);
                    _log.AppendLine("[WARN] 清理自检注册表键失败: " + cx.Message);
                }
            }
            Check(ledgerOk, "账本：写前落账 → 事件复原到原值", why);
        }

        // ---- 6. 托盘：安装 / 更新 / 消息解包 / 移除 ------------------------
        private static void StageTray(TestState st)
        {
            TrayHost tray = new TrayHost();
            int leftHits = 0, rightHits = 0;
            tray.LeftUp += delegate { leftHits++; };
            tray.RightUp += delegate { rightHits++; };

            bool installed = tray.Install("GameOptimizer · 自检");
            Check(installed, "托盘：Shell_NotifyIcon 安装成功", tray.LastError);
            tray.UpdateTip("GameOptimizer · 自检更新");
            Check(tray.Installed, "托盘：tip 可更新", null);

            // ---- 6.1 消息解包：用外壳**真正会发**的版本 4 事件码 ----
            // 旧实现只认 WM_LBUTTONUP(0x0202)/WM_RBUTTONUP(0x0205)，
            // 而启用 NOTIFYICON_VERSION_4 之后外壳发的是 NIN_SELECT(0x0400) /
            // NIN_KEYSELECT(0x0401) / WM_CONTEXTMENU(0x007B)，
            // 于是点击全部落空——这正是"点托盘图标没反应"的原因。
            IntPtr sink = tray.Handle;
            uint anchorW = MakeAnchorW(1234, 567);

            PostMsg(sink, TrayMsg, anchorW, 0x0400);       // NIN_SELECT
            Pump(180);
            Check(leftHits == 1, "托盘：NIN_SELECT(0x0400) 映射为左键激活", "hits=" + leftHits);

            PostMsg(sink, TrayMsg, anchorW, 0x0401);       // NIN_KEYSELECT
            Pump(180);
            Check(leftHits == 2, "托盘：NIN_KEYSELECT 同样激活", "hits=" + leftHits);

            PostMsg(sink, TrayMsg, anchorW, 0x007B);       // WM_CONTEXTMENU
            Pump(180);
            Check(rightHits == 1 && tray.AnchorX == 1234 && tray.AnchorY == 567,
                  "托盘：WM_CONTEXTMENU 弹出菜单且锚点解析正确",
                  "right=" + rightHits + " anchor=" + tray.AnchorX + "," + tray.AnchorY);

            // 旧版兜底必须单独验证：v4 下外壳会把 WM_LBUTTONUP 和 NIN_SELECT 一起发，
            // 那属于"同一次点击的两条记录"，会被去重吞掉——这正是我们想要的。
            // 这里等过去重窗口（260ms）再单独发一条，验证兜底路径本身仍然有效。
            Pump(340);
            PostMsg(sink, TrayMsg, anchorW, 0x0202);       // 旧版 WM_LBUTTONUP 兜底
            Pump(180);
            Check(leftHits == 3, "托盘：旧版 WM_LBUTTONUP 仍可兜底", "hits=" + leftHits);

            bool balloonOk = tray.Balloon("自检", "停靠态通知走气泡", false);
            Check(balloonOk, "托盘：停靠态可发气泡通知", null);

            tray.Remove();
            Check(!tray.Installed, "托盘：可干净移除", null);

            // ---- 6.2 设备形态 ----
            _log.AppendLine("        输入形态：" + Touch.Describe() + "  热区系数 " + Touch.HitScale.ToString("0.00"));
        }

        // ---- 7. 游戏嗅探（Steam / Epic）------------------------------------
        private static void StageSniffer(TestState st)
        {
            ScanResult scanRes = GameScanner.Scan();
            Check(scanRes.SteamFound || scanRes.EpicFound,
                  "嗅探：识别到启动器清单",
                  "steam=" + scanRes.SteamFound + "(库 " + scanRes.SteamLibraries + ") epic=" + scanRes.EpicFound);
            bool hasAny = scanRes.SteamFound || scanRes.EpicFound;
            Check(!hasAny || scanRes.Count > 0,
                  "嗅探：从库目录解析出可执行文件",
                  scanRes.Count + " 个 · " + scanRes.ElapsedMs + " ms" + (scanRes.Error == null ? "" : " · " + scanRes.Error));
            if (scanRes.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                int show = Math.Min(5, scanRes.Count);
                for (int i = 0; i < show; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append(scanRes.Games[i].Name);
                }
                _log.AppendLine("        发现：" + sb.ToString() + (scanRes.Count > show ? " …" : ""));
                bool allExist = true;
                foreach (FoundGame fg in scanRes.Games) if (!File.Exists(fg.Exe)) { allExist = false; break; }
                Check(allExist, "嗅探：返回的每个路径都真实存在", scanRes.Count + " 条");
            }
        }

        // ---- 8. 会话落点 + 拥有权 ------------------------------------------
        private static void StageOwnership(TestState st)
        {
            GameProfile p = st.Profile;
            // ---- 8.5 崩溃自愈落点 ----
            SessionRecord planted = new SessionRecord();
            planted.ProfileId = p == null ? "x" : p.Id;
            planted.ProfileName = "崩溃残留";
            planted.AppPid = 999999;          // 不存在的进程 = 上次崩溃
            SessionStore.Save(planted);
            bool recExisted = SessionStore.Exists;
            SessionStore.Clear();
            Check(recExisted && !SessionStore.Exists, "会话落点：可写入并清除", SessionStore.FilePath);

            // ---- 8.6 判活必须带创建时间（PID 是号码不是身份）----
            int self = Process.GetCurrentProcess().Id;
            long born = SessionStore.SelfBorn();
            bool aliveSelf = SessionStore.Alive(self, born);
            bool reused = SessionStore.Alive(self, born - 12345);      // 同号不同生：不是同一进程
            Check(aliveSelf && !reused,
                  "判活：同一个 PID 不同创建时间不算同一进程",
                  "aliveSelf=" + aliveSelf + " reused=" + reused + " born=" + born);

            // ---- 8.7 会话租约：同一会话只允许一个 owner，退出后可被接管 ----
            // 用独立的 GO_INSTANCE_KEY 隔离，避免与正在运行的正式实例互相影响。
            string oldKey = Environment.GetEnvironmentVariable("GO_INSTANCE_KEY");
            string key = "daemontest-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Environment.SetEnvironmentVariable("GO_INSTANCE_KEY", key);
            try
            {
                SessionLease a = SessionLease.Acquire();
                // 第二个 owner 必须来自**另一条线程**：命名互斥体按线程计所有权，
                // 同一个线程再 Acqire 会成功（递归持有），那验不到跨进程互斥。
                SessionLease b = null;
                Thread other = new Thread(delegate () { b = SessionLease.Acquire(); });
                other.IsBackground = true;
                other.Start();
                other.Join(5000);
                Check(a.Owned && b != null && !b.Owned,
                      "会话租约：同一会话只允许一个 owner", "a=" + a.Owned + " b=" + (b == null ? "null" : b.Owned.ToString()) + " why=" + (b == null ? "-" : b.Why));
                if (b != null) b.Release();
                a.Release();
                SessionLease c = SessionLease.Acquire();
                Check(c.Owned, "会话租约：owner 退出后可被下一个接管（崩溃自愈的地基）", c.Why);
                c.Release();
            }
            finally
            {
                Environment.SetEnvironmentVariable("GO_INSTANCE_KEY", oldKey);
            }
        }

        // ---- 9. 供电 -------------------------------------------------------
        private static void StagePower(TestState st)
        {
            PowerMonitor pm = new PowerMonitor();
            int powerEvents = 0;
            bool lastAc = true;
            pm.Changed += delegate (bool ac) { powerEvents++; lastAc = ac; };
            pm.HandleStatus(false);
            bool wentBattery = !lastAc && powerEvents == 1;
            pm.HandleStatus(false);          // 重复同态不应再次广播
            bool idempotent = powerEvents == 1;
            pm.HandleStatus(true);
            Check(wentBattery && idempotent && lastAc && powerEvents == 2,
                  "供电：电池切换广播一次，重复状态不重复触发",
                  "events=" + powerEvents + " ac=" + lastAc);

            // 并发同态：四个线程同时切电池，只应广播一次。
            // Apply 的读-判-写不在锁内时，这里会漏发/重发（重发 = 租约反复 Request/Release）。
            PowerMonitor conc = new PowerMonitor();
            int concEvents = 0;
            conc.Changed += delegate (bool ac) { Interlocked.Increment(ref concEvents); };
            Thread[] ts = new Thread[4];
            for (int i = 0; i < ts.Length; i++)
            {
                ts[i] = new Thread(delegate () { conc.HandleStatus(false); });
                ts[i].IsBackground = true;
                ts[i].Start();
            }
            for (int i = 0; i < ts.Length; i++) ts[i].Join(2000);
            Check(concEvents == 1, "供电：并发同态只广播一次", "events=" + concEvents);

            // Stop→Start 不能留下两条轮询线程（旧实现只看 _run，旧线程醒来后继续跑）。
            PowerMonitor pm2 = new PowerMonitor();
            pm2.Start();
            Thread t1 = ThreadOf(pm2, "_th");
            pm2.Stop();
            pm2.Start();
            Thread t2 = ThreadOf(pm2, "_th");
            bool restarted = t1 != null && t2 != null && !object.ReferenceEquals(t1, t2) && !t1.IsAlive && t2.IsAlive;
            pm2.Stop();
            Check(restarted, "供电：Stop→Start 不叠加轮询线程",
                  "t1=" + (t1 == null ? "-" : t1.IsAlive.ToString()) + " t2=" + (t2 == null ? "-" : t2.IsAlive.ToString()));
        }

        // ---- 10. 战报 ------------------------------------------------------
        private static void StageReport(TestState st)
        {
            SessionRecorder rec = new SessionRecorder(st.Core);
            rec.Begin("战报自检");
            rec.InjectForTest(120, 3, 4200);
            bool activeBefore = rec.Active;
            SessionReport rep0 = rec.End();
            bool verdictOk = rep0 != null && rep0.Verdict.IndexOf("3 次", StringComparison.Ordinal) >= 0;
            Check(activeBefore && !rec.Active && verdictOk,
                  "战报：统计口径可复现（3 次抖动 / 峰值 4.2ms）",
                  rep0 == null ? "null" : rep0.Verdict);
        }

        // ---- 11. 唤醒开销 --------------------------------------------------
        private static void StageSampling(TestState st)
        {
            PerfSampler ps = new PerfSampler();
            ps.Start();
            Thread.Sleep(1200);
            bool awake = ps.Current.Tick > 0;
            ps.Dormant = true;
            long t0 = Environment.TickCount;
            Thread.Sleep(900);
            bool dormantQuiet = (Environment.TickCount - t0) >= 850;
            ps.Dormant = false;
            ps.Stop();
            Check(awake, "采样：正常态有节拍", null);
            Check(dormantQuiet, "采样：休眠态不再占用唤醒", null);
        }

        // ---- 12. 守护宿主：Start → Shutdown 后守护线程 2 秒内全停 -----------
        private static void StageLifecycle(TestState st)
        {
            Core core = st.Core;
            // 这不是"读代码觉得会停"：真的起一遍（不装托盘、Passive 不写系统），
            // 真的 Shutdown，然后等线程死。关窗路径不允许 Join UI 线程，
            // 所以退出速度只能靠 IsBackground + Stop() 能立刻叫醒轮询。
            DaemonHost host = new DaemonHost(core);
            host.Passive = true;
            host.TrayEnabled = false;
            host.Start();
            Thread hw = ThreadOf(host.Watcher, "_thread");
            Thread hp = ThreadOf(host.Power, "_th");
            bool hostUp = hw != null && hw.IsAlive && hp != null && hp.IsAlive;
            host.Shutdown();
            long hs = Environment.TickCount;
            bool hostDown = false;
            while (Environment.TickCount - hs < 2000)
            {
                if ((hw == null || !hw.IsAlive) && (hp == null || !hp.IsAlive)) { hostDown = true; break; }
                Thread.Sleep(40);
            }
            bool report = host.LastShutdown != null && host.LastShutdown.Steps.Count > 0;
            Check(hostUp && hostDown && report,
                  "生命周期：DaemonHost Start/Shutdown 后守护线程 2 秒内全停",
                  (Environment.TickCount - hs) + " ms · watcherAlive=" + (hw != null && hw.IsAlive)
                  + " powerAlive=" + (hp != null && hp.IsAlive)
                  + " steps=" + (host.LastShutdown == null ? -1 : host.LastShutdown.Steps.Count));

            // 真实退出顺序：托盘必须在前，注册表复原必须在删落点与放会话锁之前。
            // 这条断言是给"两条文件对同一列表理解相反"那个 bug 兜底的。
            ShutdownReport sr = host.LastShutdown;
            int iTray = StepIndex(sr, "tray.icon");
            int iRevert = StepIndex(sr, "pilot.revert");
            int iClear = StepIndex(sr, "session.clear");
            int iLease = StepIndex(sr, "session.lease");
            Check(iTray >= 0 && iRevert >= 0 && iClear >= 0 && iLease >= 0
                  && iTray < iRevert && iRevert < iClear && iClear < iLease,
                  "退出管线：托盘 → 复原 → 删落点 → 放锁（顺序不可反）",
                  "tray=" + iTray + " revert=" + iRevert + " clear=" + iClear + " lease=" + iLease);
        }

        private static int StepIndex(ShutdownReport r, string name)
        {
            if (r == null) return -2;
            for (int i = 0; i < r.Steps.Count; i++) if (r.Steps[i].Name == name) return i;
            return -1;
        }

        // ---- 12.5 守护线程不会退化成紧循环 -----------------------------------
        /// <summary>
        /// 旧实现的 _wake 是 ManualResetEvent：一次 Set（关闭开关 / Reload）之后
        /// WaitOne 永久立即返回，while(_run) 变成单核 100% 的紧循环。这里用两个
        /// 可观测事实把它钉死：扫描次数必须与 250ms 节拍同量级；关掉开关后的
        /// 一秒里进程 CPU 增量必须接近 0。
        /// </summary>
        private static void StageSpin(TestState st)
        {
            GameProfile gp = new GameProfile();
            gp.Id = "spin-probe";
            gp.Name = "唤醒自检";
            gp.Exe = @"C:\Windows\System32\GoSpinProbeNeverExists.exe";
            st.Core.Profiles.AddNoSave(gp);

            GameWatcher w = new GameWatcher(st.Core);
            w.IdleIntervalMs = 250;
            try
            {
                w.Enabled = true;       // 会启动巡检线程
                Thread.Sleep(700);
                int a = w.ScanCount;
                Thread.Sleep(1000);
                int idle = w.ScanCount - a;

                w.Reload();             // 旧实现从这里开始永久保持信号
                Thread.Sleep(150);
                int b = w.ScanCount;
                Thread.Sleep(1000);
                int afterReload = w.ScanCount - b;

                Check(idle >= 2 && idle <= 10 && afterReload >= 2 && afterReload <= 10,
                      "守护：250ms 节拍稳定（Reload 不会退化成紧循环）",
                      "idle=" + idle + "/s afterReload=" + afterReload + "/s");

                w.Enabled = false;
                int c = w.ScanCount;
                TimeSpan cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
                Thread.Sleep(1000);
                TimeSpan cpu1 = Process.GetCurrentProcess().TotalProcessorTime;
                int idleAfterOff = w.ScanCount - c;
                double cpuMs = (cpu1 - cpu0).TotalMilliseconds;
                Check(idleAfterOff == 0 && cpuMs < 400,
                      "守护：关闭开关后不再扫描，也不空转",
                      "scans=" + idleAfterOff + " cpu=" + cpuMs.ToString("0") + "ms/1s");
            }
            finally
            {
                w.Stop();
                st.Core.Profiles.RemoveNoSave(gp);
            }
        }

        // ---- 13. 退出管线本身（纯逻辑：注册顺序 / 超时继续 / 幂等）----------
        private static void StagePipeline(TestState st)
        {
            List<string> order = new List<string>();
            ShutdownPipeline p = new ShutdownPipeline();
            p.Add("a", 500, false, delegate { order.Add("a"); });
            p.Add("b", 500, false, delegate { order.Add("b"); });
            p.Add("c", 500, false, delegate { order.Add("c"); });
            ShutdownReport rep = p.RunOnce("test");
            ShutdownReport again = p.RunOnce("test-again");
            // Rev87：注册顺序 = 执行顺序。旧契约是逆序，而 DaemonHost 按执行顺序注册，
            // 结果托盘图标最后才摘、会话锁最先放 —— 两条文件对同一列表的理解相反。
            bool forward = order.Count == 3 && order[0] == "a" && order[1] == "b" && order[2] == "c";
            Check(forward && object.ReferenceEquals(rep, again) && rep.Steps.Count == 3,
                  "退出管线：按注册顺序执行，且只跑一次",
                  "order=" + string.Join(",", order.ToArray()) + " steps=" + rep.Steps.Count);

            List<string> ran = new List<string>();
            ShutdownPipeline p2 = new ShutdownPipeline();
            p2.Add("slow", 60, false, delegate { Thread.Sleep(400); ran.Add("slow"); });
            p2.Add("after", 500, false, delegate { ran.Add("after"); });
            p2.Add("first", 500, false, delegate { ran.Add("first"); });
            ShutdownReport r2 = p2.RunOnce("timeout");
            ShutdownStepResult slow = r2.Find("slow");
            bool continued = ran.Contains("first") && ran.Contains("after");
            Check(slow != null && slow.State == "timeout" && r2.Timeouts == 1 && continued,
                  "退出管线：超时的步骤被放弃，后续步骤照常执行",
                  "state=" + (slow == null ? "-" : slow.State) + " timeouts=" + r2.Timeouts + " ran=" + string.Join(",", ran.ToArray()));
        }

        private static string Applied(ApplyReport r)
        {
            return r.Ok ? "ok" : "fail(" + r.Failed + ")";
        }

        private const int TrayMsg = 0x8000 + 1;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void PostMsg(IntPtr hwnd, int msg, uint wParam, int lParam)
        {
            PostMessage(hwnd, msg, new IntPtr(wParam), new IntPtr(lParam));
        }

        /// <summary>版本 4 把锚点坐标打包在 wParam：LOWORD=x, HIWORD=y。</summary>
        private static uint MakeAnchorW(int x, int y)
        {
            return (uint)((y << 16) | (x & 0xFFFF));
        }

        /// <summary>推动一次调度器消息泵，让 HwndSource 的回调真的被执行。</summary>
        private static void Pump(int ms)
        {
            System.Windows.Threading.DispatcherFrame frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.DispatcherTimer t = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(ms), System.Windows.Threading.DispatcherPriority.Background,
                delegate { frame.Continue = false; }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            t.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            t.Stop();
        }

        /// <summary>读私有线程字段（只用于断言线程是否已退出，不改产品代码）。</summary>
        private static Thread ThreadOf(object o, string field)
        {
            if (o == null) return null;
            FieldInfo f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            return f == null ? null : f.GetValue(o) as Thread;
        }

        private static bool WaitFor(Func<bool> cond, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                Thread.Sleep(120);
            }
            return cond();
        }
    }
}
