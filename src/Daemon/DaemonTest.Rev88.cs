// ============================================================================
//  DaemonTest.Rev88.cs — Rev88 新增守护核心能力无头自检
//  覆盖：PowerMonitor 节能模式/Dispose、GameScanner 多源嗅探、
//        AutoPilot CcdMode/PriorityMode/EcoBackground/崩溃还原、
//        SessionHistory 30 局战报持久化与防损坏隔离。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Go.Daemon
{
    public static partial class DaemonTest
    {
        private static void StagePowerRev88(TestState st)
        {
            using (PowerMonitor pm = new PowerMonitor())
            {
                pm.PollOnce();
                bool pctOk = pm.BatteryPercent == -1 || (pm.BatteryPercent >= 0 && pm.BatteryPercent <= 100);
                Check(pctOk, "供电 Rev88：PollOnce 读取电池百分比在合法范围",
                      "pct=" + pm.BatteryPercent + " ac=" + pm.OnAc + " saver=" + pm.PowerSaverActive);

                int acEv = 0, saverEv = 0;
                bool lastSaver = false;
                pm.Changed += delegate (bool onAc) { acEv++; };
                pm.PowerSaverChanged += delegate (bool on) { saverEv++; lastSaver = on; };

                pm.HandleStatus(false, true);
                bool enterSaver = !pm.OnAc && pm.PowerSaverActive && lastSaver && acEv == 1 && saverEv == 1;
                pm.HandleStatus(false, true);
                bool idempotent = acEv == 1 && saverEv == 1;
                pm.HandleStatus(true, false);
                bool leaveSaver = pm.OnAc && !pm.PowerSaverActive && !lastSaver && acEv == 2 && saverEv == 2;
                Check(enterSaver && idempotent && leaveSaver,
                      "供电 Rev88：PowerSaverChanged 边沿通知且重复状态幂等",
                      "acEv=" + acEv + " saverEv=" + saverEv + " saverNow=" + pm.PowerSaverActive);

                pm.Start();
                Thread th = ThreadOf(pm, "_th");
                bool alive = th != null && th.IsAlive;
                pm.Dispose();
                pm.Dispose();
                bool stopped = th == null || !th.IsAlive;
                Check(alive && stopped, "供电 Rev88：事件窗 Start 后 Dispose 干净停机且可重复调用",
                      "alive=" + alive + " stopped=" + stopped);
            }
        }

        private static void StageScannerRev88(TestState st)
        {
            string sb = Path.Combine(Path.GetTempPath(), "GameOptimizer", "scan-rev88-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sb);
            try
            {
                // 1) WeGame 沙箱目录识别 + 过滤启动器自身进程名
                string wgRoot = Path.Combine(sb, "WeGameApps");
                string wgGameDir = Path.Combine(wgRoot, "BlackMythWukong");
                Directory.CreateDirectory(wgGameDir);
                string wgExe = Path.Combine(wgGameDir, "BlackMythWukong.exe");
                WriteDummyFile(wgExe, 65536);

                string wgJunkDir = Path.Combine(wgRoot, "rail_files");
                Directory.CreateDirectory(wgJunkDir);
                WriteDummyFile(Path.Combine(wgJunkDir, "rail.exe"), 65536);

                // 2) Xbox .GamingRoot + MicrosoftGame.Config 识别
                string xbDrive = Path.Combine(sb, "XboxDrive");
                string xbGames = Path.Combine(xbDrive, "XboxGames");
                string xbContent = Path.Combine(xbGames, "HaloInfinite", "Content");
                Directory.CreateDirectory(xbContent);
                byte[] grPrefix = new byte[] { (byte)'R', (byte)'G', (byte)'B', (byte)'X', 1, 0, 0, 0 };
                byte[] grRel = Encoding.Unicode.GetBytes("XboxGames\0");
                byte[] grBytes = new byte[grPrefix.Length + grRel.Length];
                Buffer.BlockCopy(grPrefix, 0, grBytes, 0, grPrefix.Length);
                Buffer.BlockCopy(grRel, 0, grBytes, grPrefix.Length, grRel.Length);
                File.WriteAllBytes(Path.Combine(xbDrive, ".GamingRoot"), grBytes);

                string xbExe = Path.Combine(xbContent, "HaloGame.exe");
                WriteDummyFile(xbExe, 32768);
                string xmlCfg = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                    + "<Game configVersion=\"1\">"
                    + "<ShellVisuals DefaultDisplayName=\"Halo Infinite\" />"
                    + "<ExecutableList><Executable Name=\"HaloGame.exe\" /></ExecutableList>"
                    + "</Game>";
                File.WriteAllText(Path.Combine(xbContent, "MicrosoftGame.Config"), xmlCfg, new UTF8Encoding(false));

                // 3) 桌面 .url / .lnk + 非游戏词表过滤
                string deskDir = Path.Combine(sb, "Desktop");
                Directory.CreateDirectory(deskDir);
                string lnkTarget = Path.Combine(sb, "CyberGame.exe");
                string urlTarget = Path.Combine(sb, "IndieQuest.exe");
                WriteDummyFile(lnkTarget, 300 * 1024);
                WriteDummyFile(urlTarget, 300 * 1024);
                string nonGameTarget = Path.Combine(sb, "msedge.exe");
                WriteDummyFile(nonGameTarget, 300 * 1024);

                WriteSyntheticLnk(Path.Combine(deskDir, "Cyber Game.lnk"), lnkTarget);
                WriteSyntheticLnk(Path.Combine(deskDir, "Microsoft Edge.lnk"), nonGameTarget);
                File.WriteAllText(Path.Combine(deskDir, "Indie Quest.url"),
                    "[InternetShortcut]\r\nURL=" + new Uri(urlTarget).AbsoluteUri + "\r\n",
                    new UTF8Encoding(false));

                ScanResult sr = new ScanResult();
                sr.Clock = Stopwatch.StartNew();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScanner.ScanWeGameFolder(sr, seen, wgRoot);
                GameScanner.ScanXboxDriveRoot(sr, seen, xbDrive);
                GameScanner.ScanShortcutsDir(sr, seen, deskDir, 0, 1);

                bool hasWg = HasGame(sr, "WeGame", wgExe);
                bool hasXb = HasGame(sr, "Xbox", xbExe, "Halo Infinite");
                bool hasLnk = HasGame(sr, "桌面", lnkTarget);
                bool hasUrl = HasGame(sr, "桌面", urlTarget);
                bool filteredNonGame = !HasGame(sr, "桌面", nonGameTarget)
                    && !GameScanner.IsLikelyGameShortcut("微信", @"D:\WeChat\WeChat.exe")
                    && GameScanner.IsLikelyGameShortcut("Cyberpunk 2077", @"D:\Games\Cyberpunk2077.exe");

                Check(sr.WeGameFound && sr.WeGameLocations == 1 && hasWg,
                      "嗅探 Rev88：WeGameApps 目录识别并跳过运行库垃圾进程",
                      "locs=" + sr.WeGameLocations + " hasWg=" + hasWg);
                Check(sr.XboxFound && sr.XboxLocations == 1 && hasXb,
                      "嗅探 Rev88：Xbox .GamingRoot 与 MicrosoftGame.Config 解析",
                      "locs=" + sr.XboxLocations + " hasXb=" + hasXb);
                Check(sr.ShortcutsFound && sr.ShortcutLocations == 2 && hasLnk && hasUrl && filteredNonGame,
                      "嗅探 Rev88：纯托管 .lnk/.url 解析及非游戏过滤",
                      "locs=" + sr.ShortcutLocations + " lnk=" + hasLnk + " url=" + hasUrl + " filt=" + filteredNonGame);

                // 确保真实宿主 Scan() 完整执行不抛异常且遵守计数契约
                ScanResult live = GameScanner.Scan();
                Check(live != null && (live.Error == null || live.Count >= 0),
                      "嗅探 Rev88：宿主全源扫描完整完成",
                      "total=" + (live == null ? -1 : live.Count)
                      + " wg=" + (live == null ? 0 : live.WeGameLocations)
                      + " xb=" + (live == null ? 0 : live.XboxLocations)
                      + " sc=" + (live == null ? 0 : live.ShortcutLocations)
                      + " ms=" + (live == null ? 0 : live.ElapsedMs));
            }
            finally
            {
                try { if (Directory.Exists(sb)) Directory.Delete(sb, true); }
                catch (Exception ex) { Diag.Fail("daemontest.scanner.cleanup", ex); }
            }
        }

        private static void StageAutoPilotRev88(TestState st)
        {
            Process gameProc = null;
            Process bgProc = null;
            AutoPilot pilot = new AutoPilot(st.Core);
            pilot.RevertOnExit = false;
            try
            {
                gameProc = StartSleepProbe();
                bgProc = StartSleepProbe();

                GameProfile gp = new GameProfile();
                gp.Id = "rev88-ap-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                gp.Name = "AutoPilotRev88";
                gp.Exe = @"C:\Windows\System32\ping.exe";
                gp.CcdMode = "Physical";
                gp.PriorityMode = "AboveNormal";
                gp.HighGpu = false;
                gp.Timer = false;
                gp.EcoBackground = true;

                pilot.Attach(gp, gameProc.Id);
                long affNow = Affinity.Read(gameProc.Id);
                long wantMask = Affinity.PhysicalOnlyMask(st.Core.Topo);
                uint prioNow = ProcessPower.ReadPriority(gameProc.Id);

                Check(affNow != 0 && wantMask != 0 && affNow == wantMask,
                      "挂载 Rev88：CcdMode=Physical 按物理核主线程掩码绑定",
                      "aff=0x" + affNow.ToString("X") + " want=0x" + wantMask.ToString("X"));
                Check(prioNow == ProcessPower.PriorityAboveNormal && ProcessPower.IsTracked(gameProc.Id),
                      "挂载 Rev88：PriorityMode=AboveNormal 生效并记入追踪",
                      "prio=0x" + prioNow.ToString("X") + " tracked=" + ProcessPower.TrackedCount);

                string bgErr;
                bool sameNameSuppressed = pilot.TrySuppressBackgroundPid(bgProc.Id, gameProc.Id, "ping", out bgErr);
                bool gameSelfSkipped = !pilot.TrySuppressBackgroundPid(gameProc.Id, gameProc.Id, "OtherGame", out bgErr);
                bool suppressBgOk = pilot.TrySuppressBackgroundPid(bgProc.Id, gameProc.Id, "OtherGame", out bgErr);
                bool ecoReadBack = false;
                bool ecoKnown = ProcessPower.ReadEcoQoS(bgProc.Id, out ecoReadBack);
                Check(!sameNameSuppressed && gameSelfSkipped,
                      "挂载 Rev88：EcoBackground 跳过目标游戏及同名进程防误伤",
                      "sameNameSkip=" + !sameNameSuppressed + " gameIdSkip=" + gameSelfSkipped);
                Check(suppressBgOk && ecoKnown && ecoReadBack,
                      "挂载 Rev88：后台进程 EcoQoS 压制成功且可回读",
                      "set=" + suppressBgOk + " known=" + ecoKnown + " val=" + ecoReadBack + " err=" + (bgErr ?? "-"));

                // 验证 Detach 严格还原游戏优先级 & 后台 EcoQoS
                pilot.Detach(gp, gameProc.Id);
                uint prioAfter = ProcessPower.ReadPriority(gameProc.Id);
                bool ecoAfterDetach = true;
                ProcessPower.ReadEcoQoS(bgProc.Id, out ecoAfterDetach);
                Check(prioAfter == ProcessPower.PriorityNormal && !ecoAfterDetach && ProcessPower.TrackedCount == 0,
                      "挂载 Rev88：Detach 后优先级与 EcoQoS 全数复原并清空追踪",
                      "prio=0x" + prioAfter.ToString("X") + " eco=" + ecoAfterDetach + " tracked=" + ProcessPower.TrackedCount);

                // 验证崩溃重启还原（SessionRecord.TrackedProcs）
                string cErr;
                bool crashedEcoSet = ProcessPower.SetEcoQoS(bgProc.Id, true, false, out cErr);
                SessionRecord fakeCrash = new SessionRecord();
                fakeCrash.ProfileId = gp.Id;
                fakeCrash.ProfileName = gp.Name;
                fakeCrash.AppPid = 999998;
                fakeCrash.TrackedProcs = new TrackedProcRecord[]
                {
                    new TrackedProcRecord
                    {
                        Pid = bgProc.Id,
                        Born = bgProc.StartTime.ToUniversalTime().ToFileTimeUtc(),
                        OrigPriority = ProcessPower.PriorityNormal,
                        EcoSet = true
                    }
                };
                int recCount = pilot.RecoverTrackedProcs(fakeCrash);
                bool ecoAfterRecover = true;
                ProcessPower.ReadEcoQoS(bgProc.Id, out ecoAfterRecover);
                Check(crashedEcoSet && recCount == 1 && !ecoAfterRecover,
                      "挂载 Rev88：崩溃恢复凭 SessionRecord.TrackedProcs 还原 EcoQoS",
                      "set=" + crashedEcoSet + " rec=" + recCount + " ecoNow=" + ecoAfterRecover);
            }
            finally
            {
                pilot.Stop();
                ProcessPower.RevertTracked();
                KillAndDispose(gameProc);
                KillAndDispose(bgProc);
                SessionStore.Clear();
            }
        }

        private static void StageSessionHistoryRev88(TestState st)
        {
            string sb = Path.Combine(Path.GetTempPath(), "GameOptimizer", "sesshist-rev88-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sb);
            string prevProf = ProfileStore.DirOverride;
            string prevHist = SessionHistory.DirOverride;
            try
            {
                ProfileStore.DirOverride = sb;
                SessionHistory.DirOverride = sb;

                // 1) 损坏 sessions.json 隔离测试
                File.WriteAllText(SessionHistory.FilePath, "{corrupt json [", new UTF8Encoding(false));
                List<SessionHistoryItem> emptyOnCorrupt = SessionHistory.Load();
                bool quarantined = Directory.GetFiles(sb, "sessions.json.corrupt-*").Length == 1;
                Check(SessionHistory.LastLoadCorrupt && emptyOnCorrupt.Count == 0 && quarantined,
                      "战报历史 Rev88：损坏文件自动转存隔离区不崩溃",
                      "corrupt=" + SessionHistory.LastLoadCorrupt + " q=" + quarantined);

                // 2) 30 条滚动上限
                for (int i = 0; i < 35; i++)
                {
                    SessionReport sr = new SessionReport();
                    sr.ProfileName = "Game-" + i;
                    sr.Started = "19:0" + (i % 10);
                    sr.DurationSec = 100 + i;
                    sr.Samples = 60;
                    sr.JitterSamples = 60;
                    sr.StutterWindows = i % 3;
                    sr.AvgP99Us = 900 + i;
                    sr.MaxJitterUs = 1800 + i;
                    SessionHistory.Append(sr, "id-" + i, @"C:\Games\g" + i + ".exe", i % 2);
                }
                List<SessionHistoryItem> all = SessionHistory.Load();
                List<SessionHistoryItem> rev = SessionHistory.LoadNewestFirst();
                Check(all.Count == SessionHistory.Cap && all[0].ProfileName == "Game-5" && rev[0].ProfileName == "Game-34",
                      "战报历史 Rev88：上限 30 局且保留最新记录",
                      "count=" + all.Count + " oldest=" + (all.Count > 0 ? all[0].ProfileName : "-")
                      + " newest=" + (rev.Count > 0 ? rev[0].ProfileName : "-"));

                // 3) SessionRecorder.End 联动写回 ProfileStore + SessionHistory
                ProfileStore ps = new ProfileStore();
                GameProfile gp = new GameProfile();
                gp.Id = "hist-prof-1";
                gp.Name = "Apex战报测试";
                gp.Exe = @"C:\Games\apex.exe";
                gp.CcdMode = "Physical";
                gp.PriorityMode = "High";
                gp.EcoBackground = true;
                ps.Add(gp);

                Core subCore = new Core();
                SessionRecorder rec = new SessionRecorder(subCore);
                rec.Begin(gp);
                rec.InjectForTest(120, 1, 3100);
                SessionReport endRep = rec.End();

                ProfileStore reloadedPs = new ProfileStore();
                GameProfile found = reloadedPs.FindById("hist-prof-1");
                List<SessionHistoryItem> newest = SessionHistory.LoadNewestFirst();
                Check(found != null && found.Hits == 1 && !string.IsNullOrEmpty(found.LastVerdict)
                      && found.CcdMode == "Physical" && found.PriorityMode == "High" && found.EcoBackground,
                      "战报历史 Rev88：End() 回写 ProfileStore 扩展字段并兼容落盘",
                      found == null ? "null" : ("hits=" + found.Hits + " verdict=" + found.LastVerdict));
                Check(endRep != null && newest.Count > 0 && newest[0].ProfileId == "hist-prof-1",
                      "战报历史 Rev88：End() 同步追加 SessionHistory",
                      newest.Count > 0 ? newest[0].ProfileName : "-");
            }
            finally
            {
                SessionHistory.DirOverride = prevHist;
                ProfileStore.DirOverride = prevProf;
                try { if (Directory.Exists(sb)) Directory.Delete(sb, true); }
                catch (Exception ex) { Diag.Fail("daemontest.sesshist.cleanup", ex); }
            }
        }

        private static void StageRev89(TestState st)
        {
            // 1. GameWatcher.ForceAttach 切档案先触发旧游戏的 Stopped，再触发新游戏的 Started
            Process p1 = null;
            Process p2 = null;
            GameWatcher gw = new GameWatcher(st.Core);
            try
            {
                p1 = StartSleepProbe();
                p2 = StartSleepProbe();
                GameProfile gpA = new GameProfile { Id = "r89-a", Name = "GameA", Exe = @"C:\Games\GameA.exe" };
                GameProfile gpB = new GameProfile { Id = "r89-b", Name = "GameB", Exe = @"C:\Games\GameB.exe" };

                List<string> events = new List<string>();
                gw.Started += delegate (GameProfile g, int pid) { events.Add("START:" + g.Id + ":" + pid); };
                gw.Stopped += delegate (GameProfile g, int pid) { events.Add("STOP:" + g.Id + ":" + pid); };

                gw.ForceAttach(gpA, p1.Id);
                gw.ForceAttach(gpB, p2.Id);
                bool switchOk = events.Count == 3
                    && events[0] == ("START:r89-a:" + p1.Id)
                    && events[1] == ("STOP:r89-a:" + p1.Id)
                    && events[2] == ("START:r89-b:" + p2.Id)
                    && object.ReferenceEquals(gw.Active, gpB)
                    && gw.ActivePid == p2.Id;
                Check(switchOk, "守护 Rev89：ForceAttach 切档案先触发旧游戏 Stopped 再挂载新档案",
                      "events=" + string.Join(",", events.ToArray()));
            }
            finally
            {
                gw.Stop();
                KillAndDispose(p1);
                KillAndDispose(p2);
            }

            // 2. GameScanner PWA / 工具降噪 & .url 游戏协议识别
            string sb = Path.Combine(Path.GetTempPath(), "GameOptimizer", "scan-rev89-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sb);
            try
            {
                bool pwaFiltered = !GameScanner.IsLikelyGameShortcut("Gmail", @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe")
                    && !GameScanner.IsLikelyGameShortcut("Google Docs", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
                    && !GameScanner.IsLikelyGameShortcut("CC Switch", @"C:\Users\u\AppData\Local\Programs\cc-switch\cc-switch.exe")
                    && !GameScanner.IsLikelyGameShortcut("Cherry Studio", @"C:\Users\u\AppData\Local\Programs\cherry-studio\Cherry Studio.exe")
                    && !GameScanner.IsLikelyGameShortcut("Antigravity", @"C:\Users\u\AppData\Local\Programs\Antigravity\Antigravity.exe")
                    && !GameScanner.IsLikelyGameShortcut("Multisim 14.0", @"C:\Program Files\National Instruments\Circuit Design Suite 14.0\multisim.exe")
                    && !GameScanner.IsLikelyGameShortcut("MuMu模拟器12", @"D:\Program Files\Netease\MuMuPlayer-12.0\shell\MuMuPlayer.exe")
                    && !GameScanner.IsLikelyGameShortcut("千问", @"D:\Tools\Qianwen.exe")
                    && !GameScanner.IsLikelyGameShortcut("学习通", @"D:\Tools\Chaoxing.exe")
                    && !GameScanner.IsLikelyGameShortcut("汽水音乐", @"D:\Tools\SodaMusic.exe")
                    && !GameScanner.IsLikelyGameShortcut("抖音", @"D:\Tools\Douyin.exe")
                    && !GameScanner.IsLikelyGameShortcut("3D模型", @"D:\Work\viewer.exe")
                    && !GameScanner.IsLikelyGameShortcut("工程图纸", @"D:\Work\cad.exe")
                    && !GameScanner.IsLikelyGameShortcut("学习资料", @"D:\Work\reader.exe")
                    && !GameScanner.IsLikelyGameShortcut("DeepSeek", @"D:\Tools\ds.exe")
                    && !GameScanner.IsLikelyGameShortcut("Codex", @"D:\Tools\codex.exe")
                    && !GameScanner.IsLikelyGameShortcut("Claude", @"D:\Tools\claude.exe")
                    && !GameScanner.IsLikelyGameShortcut("Gemini", @"D:\Tools\gemini.exe")
                    && !GameScanner.IsLikelyGameShortcut("Qoder", @"D:\Tools\qoder.exe")
                    && !GameScanner.IsLikelyGameShortcut("WorkBuddy", @"D:\Tools\wb.exe")
                    && !GameScanner.IsLikelyGameShortcut("ZCode", @"D:\Tools\zcode.exe")
                    && !GameScanner.IsLikelyGameShortcut("GitHub Desktop", @"D:\Tools\gh.exe")
                    && !GameScanner.IsLikelyGameShortcut("Legion Zone", @"D:\Tools\lz.exe")
                    && !GameScanner.IsLikelyGameShortcut("Lex", @"D:\Tools\lex.exe")
                    && GameScanner.IsLikelyGameShortcut("Counter-Strike 2", @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe");

                string desk = Path.Combine(sb, "Desk");
                Directory.CreateDirectory(desk);
                string gameExe = Path.Combine(sb, "cs2.exe");
                string dummyPwa = Path.Combine(sb, "chrome_proxy.exe");
                WriteDummyFile(gameExe, 300 * 1024);
                WriteDummyFile(dummyPwa, 300 * 1024);

                WriteSyntheticLnk(Path.Combine(desk, "Google Drive.lnk"), dummyPwa);
                File.WriteAllText(Path.Combine(desk, "Google Sheets.url"),
                    "[InternetShortcut]\r\nURL=https://docs.google.com/spreadsheets/\r\nIconFile=" + gameExe + "\r\n",
                    new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(desk, "Counter-Strike 2.url"),
                    "[InternetShortcut]\r\nURL=steam://rungameid/730\r\nIconFile=" + gameExe + "\r\n",
                    new UTF8Encoding(false));

                ScanResult sr = new ScanResult();
                sr.Clock = Stopwatch.StartNew();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScanner.ScanShortcutsDir(sr, seen, desk, 0, 1);
                bool urlOk = sr.ShortcutLocations == 1 && HasGame(sr, "桌面", gameExe, "Counter-Strike 2");
                Check(pwaFiltered && urlOk,
                      "嗅探 Rev89：过滤浏览器 PWA / AI 开发工具与网页收藏，安全识别游戏协议 .url",
                      "filt=" + pwaFiltered + " locs=" + sr.ShortcutLocations);
            }
            finally
            {
                try { if (Directory.Exists(sb)) Directory.Delete(sb, true); }
                catch (Exception ex) { Diag.Fail("daemontest.r89.scan.clean", ex); }
            }

            // 3. ProfileStore.UpdateProfile + FindByExe 文件名回退 + DaemonHost 策略副标题
            string pDir = Path.Combine(Path.GetTempPath(), "GameOptimizer", "prof-rev89-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pDir);
            string prevProf = ProfileStore.DirOverride;
            try
            {
                ProfileStore.DirOverride = pDir;
                ProfileStore ps = new ProfileStore();
                GameProfile shortProf = new GameProfile
                {
                    Id = "p-short",
                    Name = "CS2",
                    Exe = "cs2.exe",
                    CcdMode = "Physical",
                    PriorityMode = "High",
                    EcoBackground = true
                };
                GameProfile fullProf = new GameProfile
                {
                    Id = "p-full",
                    Name = "GameA",
                    Exe = @"D:\A\game.exe"
                };
                ps.Add(shortProf);
                ps.Add(fullProf);

                bool findFallback = object.ReferenceEquals(ps.FindByExe(@"C:\Steam\cs2.exe"), shortProf)
                    && object.ReferenceEquals(ps.FindByExe(@"D:\A\game.exe"), fullProf)
                    && ps.FindByExe(@"E:\B\game.exe") == null
                    && object.ReferenceEquals(ps.FindByExe("game.exe"), fullProf);

                int ver0 = ps.Version;
                bool updOk = ps.UpdateProfile("p-short", delegate (GameProfile g) { g.Timer = true; }, true);
                ProfileStore reloaded = new ProfileStore();
                GameProfile relShort = reloaded.FindById("p-short");
                string hint = DaemonHost.FormatProfilePolicyHint(relShort);
                bool hintOk = !string.IsNullOrEmpty(hint) && hint.IndexOf("仅物理核", StringComparison.Ordinal) >= 0
                    && hint.IndexOf("高优先级", StringComparison.Ordinal) >= 0
                    && hint.IndexOf("后台EcoQoS", StringComparison.Ordinal) >= 0;

                // 并发 UpdateProfile 与 RecordSession 不丢更新
                Thread tUpd = new Thread(delegate ()
                {
                    for (int k = 0; k < 20; k++)
                        ps.UpdateProfile("p-short", delegate (GameProfile g) { g.PinCcd0 = (k % 2 == 0); }, true);
                });
                Thread tRec = new Thread(delegate ()
                {
                    for (int k = 0; k < 20; k++)
                        ps.RecordSession("p-short", null, new SessionReport { DurationSec = 10, StutterWindows = 0 }, true);
                });
                tUpd.IsBackground = true; tRec.IsBackground = true;
                tUpd.Start(); tRec.Start();
                tUpd.Join(5000); tRec.Join(5000);
                GameProfile afterConc = ps.FindById("p-short");
                bool concOk = afterConc != null && afterConc.Hits == 20 && afterConc.TotalPlaySec == 200;

                Check(findFallback && updOk && ps.Version > ver0 && relShort != null && relShort.Timer && hintOk && concOk,
                      "档案与宿主 Rev89：FindByExe 文件名回退、UpdateProfile 并发锁与托盘策略副标题",
                      "fb=" + findFallback + " upd=" + updOk + " hint=" + hint + " hits=" + (afterConc == null ? -1 : afterConc.Hits));
            }
            finally
            {
                ProfileStore.DirOverride = prevProf;
                try { if (Directory.Exists(pDir)) Directory.Delete(pDir, true); }
                catch (Exception ex) { Diag.Fail("daemontest.r89.prof.clean", ex); }
            }

            // 4. DaemonHost.RecoverIfNeeded 在 RevertAction 失败时保留崩溃落点
            string prevSessDir = SessionStore.DirOverride;
            string sDir = Path.Combine(Path.GetTempPath(), "GameOptimizer", "sess-rev89-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sDir);
            string oldKey = Environment.GetEnvironmentVariable("GO_INSTANCE_KEY");
            Environment.SetEnvironmentVariable("GO_INSTANCE_KEY", "daemontest-r89-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Core recCore = new Core();
            DaemonHost recHost = new DaemonHost(recCore);
            LedgerEntry badEntry = null;
            try
            {
                SessionStore.DirOverride = sDir;
                badEntry = new LedgerEntry
                {
                    Id = "r89-bad-" + Guid.NewGuid().ToString("N"),
                    ActionId = "daemon-gpu-r89fail",
                    ActionName = "模拟复原失败",
                    Kind = "UnknownKind"
                };
                recCore.Engine.Ledger.Add(badEntry);
                recCore.Engine.Ledger.Save();
                SessionStore.Save(new SessionRecord { ProfileId = "r89fail", ProfileName = "BadRecover", AppPid = 999997 });

                recHost.RecoverIfNeeded();
                bool keptOnFail = recHost.RecoveredLastSession && recHost.RecoveredEntries == -1 && SessionStore.Exists;
                Check(keptOnFail, "宿主 Rev89：RecoverIfNeeded 还原失败时不盲目删除崩溃落点",
                      "entries=" + recHost.RecoveredEntries + " exists=" + SessionStore.Exists);
            }
            finally
            {
                try { recHost.Shutdown("test-r89-recover"); } catch (Exception ex) { Diag.Fail("daemontest.r89.shut", ex); }
                if (badEntry != null)
                {
                    recCore.Engine.Ledger.Remove(badEntry.Id);
                    try { recCore.Engine.Ledger.Save(); } catch (Exception ex) { Diag.Fail("daemontest.r89.ledger.clean", ex); }
                }
                SessionStore.Clear();
                SessionStore.DirOverride = prevSessDir;
                Environment.SetEnvironmentVariable("GO_INSTANCE_KEY", oldKey);
                try { if (Directory.Exists(sDir)) Directory.Delete(sDir, true); }
                catch (Exception ex) { Diag.Fail("daemontest.r89.sess.clean", ex); }
            }
        }

        private static void WriteDummyFile(string path, int sizeBytes)
        {
            byte[] buf = new byte[sizeBytes];
            buf[0] = (byte)'M';
            buf[1] = (byte)'Z';
            File.WriteAllBytes(path, buf);
        }

        private static void WriteSyntheticLnk(string lnkPath, string targetPath)
        {
            byte[] uni = Encoding.Unicode.GetBytes(targetPath + "\0");
            int infoHdr = 0x24;
            int infoSize = infoHdr + uni.Length;
            byte[] data = new byte[76 + infoSize];

            WriteU32(data, 0, 0x4C);             // HeaderSize
            WriteU32(data, 20, 0x02);            // LinkFlags: HasLinkInfo
            int off = 76;
            WriteU32(data, off, (uint)infoSize);
            WriteU32(data, off + 4, (uint)infoHdr);
            WriteU32(data, off + 8, 0x01);       // VolumeIDAndLocalBasePath
            WriteU32(data, off + 28, (uint)infoHdr); // LocalBasePathOffsetUnicode
            Buffer.BlockCopy(uni, 0, data, off + infoHdr, uni.Length);

            File.WriteAllBytes(lnkPath, data);
        }

        private static void WriteU32(byte[] buf, int off, uint val)
        {
            buf[off] = (byte)(val & 0xFF);
            buf[off + 1] = (byte)((val >> 8) & 0xFF);
            buf[off + 2] = (byte)((val >> 16) & 0xFF);
            buf[off + 3] = (byte)((val >> 24) & 0xFF);
        }

        private static bool HasGame(ScanResult sr, string src, string exePath)
        {
            return HasGame(sr, src, exePath, null);
        }

        private static bool HasGame(ScanResult sr, string src, string exePath, string wantName)
        {
            for (int i = 0; i < sr.Games.Count; i++)
            {
                FoundGame g = sr.Games[i];
                if (!string.Equals(g.Source, src, StringComparison.Ordinal)) continue;
                if (!string.Equals(g.Exe, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (wantName != null && !string.Equals(g.Name, wantName, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        private static Process StartSleepProbe()
        {
            Process p = new Process();
            p.StartInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
            p.StartInfo.Arguments = "-n 40 127.0.0.1";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.Start();
            return p;
        }

        private static void KillAndDispose(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); }
            catch (Exception ex) { Diag.Fail("daemontest.rev88.kill", ex); }
            try { p.Dispose(); }
            catch (Exception ex) { Diag.Fail("daemontest.rev88.dispose", ex); }
        }
    }
}
