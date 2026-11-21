// ============================================================================
//  GameScanner.cs — 已安装游戏嗅探（Steam / Epic，零配置）
//  目标：不再让用户去 C:\Program Files 里翻 exe。
//  做法：只读两个启动器自己的清单文件，不递归扫盘、不调用 WMI。
//    · Steam  libraryfolders.vdf  → 每个库根 → steamapps\common\<游戏>\*.exe
//    · Epic   LauncherInstalled.dat → InstallationList[].InstallLocation
//  识别 exe 用打分：目录名匹配 + 体积 + 排除安装器/运行时/反作弊组件。
//
//  Rev86：扫描从"跑到跑完为止"改成**有预算、可取消、可异步**。
//  触发原因不是洁癖：Profiles 页在 UI 线程上直接调 Scan()（历史遗留），
//  而 Directory.GetDirectories 在冷盘/网络盘/被安全软件扫描的目录上单次就能停 1~3 秒，
//  多次叠加就是"点开战术档案，界面先白 10 秒"。这里给三层保险：
//    · 预算：总耗时上限 + 访问目录数上限 + 收录游戏数上限（超了就停在已拿到的结果上）；
//    · 取消：CancellationToken 任意一步都能立刻停（窗口收进托盘、用户换页都不必等）；
//    · 异步：ScanAsync 跑在线程池，UI 线程一次文件系统调用都不做。
//  被截断时 Canceled=true 且 Error 里写明原因——"少列几个游戏"必须能解释，
//  不能伪装成"这台机器上就这么多"。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Go.Daemon
{
    public sealed class FoundGame
    {
        public string Name;
        public string Exe;
        public string Source;
        public long Bytes;
        public string Dir;

        public string SizeText
        {
            get
            {
                if (Bytes <= 0) return "";
                double mb = Bytes / 1048576.0;
                return mb >= 1024 ? (mb / 1024).ToString("0.0") + " GB" : mb.ToString("0") + " MB";
            }
        }
    }

    /// <summary>扫描预算。默认值按"一台装了 100 个游戏的机器"留足余量，同时保证 UI 有上界。</summary>
    public sealed class ScanLimits
    {
        /// <summary>总耗时上限（毫秒）。超了停在已拿到的结果上。</summary>
        public int MaxMs = 4000;
        /// <summary>最多收录多少个游戏。</summary>
        public int MaxGames = 300;
        /// <summary>最多访问多少个目录（跨所有库累计）。</summary>
        public int MaxDirsVisited = 8000;
        /// <summary>单个游戏目录里最多收集多少个 exe（打分用）。</summary>
        public int MaxExesPerGame = 400;

        public static ScanLimits Default() { return new ScanLimits(); }
    }

    public sealed class ScanResult
    {
        public List<FoundGame> Games = new List<FoundGame>();
        public int SteamLibraries;
        public int EpicLocations;
        public int WeGameLocations;
        public int XboxLocations;
        public int ShortcutLocations;
        public bool SteamFound;
        public bool EpicFound;
        public bool WeGameFound;
        public bool XboxFound;
        public bool ShortcutsFound;
        public long ElapsedMs;
        public string Error;
        /// <summary>true = 被预算/取消截断，结果可能不完整（原因在 Error 里）。</summary>
        public bool Canceled;
        /// <summary>实际访问的目录数（诊断用：能看出预算是被"目录太多"还是"太慢"吃掉的）。</summary>
        public int DirsVisited;

        internal ScanLimits Lim = ScanLimits.Default();
        internal CancellationToken Cancel;
        internal System.Diagnostics.Stopwatch Clock;

        public int Count { get { return Games.Count; } }

        /// <summary>是否该停下来。调用方在每一层循环里问一次，不依赖异常中断。</summary>
        internal bool ShouldStop()
        {
            if (Canceled) return true;
            if (Cancel.IsCancellationRequested)
            {
                Truncate("已取消");
                return true;
            }
            if (Clock != null && Clock.ElapsedMilliseconds > Lim.MaxMs)
            {
                Diag.Hit("scan.budget.time");
                Truncate("超出 " + Lim.MaxMs + "ms 预算");
                return true;
            }
            return false;
        }

        /// <summary>记账一次目录访问；超过上限返回 false（调用方应停止继续深入）。</summary>
        internal bool TakeDir()
        {
            DirsVisited++;
            if (DirsVisited > Lim.MaxDirsVisited)
            {
                Diag.Hit("scan.budget.dirs");
                Truncate("访问目录数超过 " + Lim.MaxDirsVisited);
                return false;
            }
            return true;
        }

        internal bool Full()
        {
            if (Games.Count < Lim.MaxGames) return false;
            Diag.Hit("scan.budget.games");
            Truncate("收录游戏数达到 " + Lim.MaxGames);
            return true;
        }

        internal void Truncate(string why)
        {
            Canceled = true;
            // 不覆盖已有的具体错误：先发生的那条（读清单失败/库跳过）更有价值。
            if (Error == null) Error = "扫描未跑完：" + why;
        }
    }

    public static partial class GameScanner
    {
        // 明显不是"游戏主程序"的文件名。分三档匹配，而不是一律 Contains：
        // 旧实现用裸子串，"Reach.exe" 里有 "eac"、"beach.exe" 里也有 —— 正常主程序被整条丢掉，
        // 用户看到的是"少了一个游戏"，界面上没有任何提示。
        //
        //   ① 短词：只认整词（前后是边界或非字母数字）—— eac / setup / config …
        //   ② 前缀：安装器与再分发包惯用前缀 —— unins000 / dxsetup / vcredist_x64
        //   ③ 长词：足够有辨识度，才允许子串命中 —— unitycrashhandler / easyanticheat …
        private static readonly string[] JunkWhole =
        {
            "eac", "setup", "install", "config", "settings", "report", "helper",
            "updater", "patcher", "patch", "touchup", "beservice", "vanguard",
            "anticheat", "crashpad", "crashhandler", "dotnet", "launcher"
        };
        private static readonly string[] JunkPrefix =
        {
            "unins", "dxsetup", "dxwebsetup", "vcredist", "dotnet", "setup", "install"
        };
        private static readonly string[] JunkStrong =
        {
            "unitycrashhandler", "crashreportclient", "crashreporter", "easyanticheat",
            "epiconlineservices", "launcher_install", "battleye"
        };

        /// <summary>同步扫描（保留旧签名：自检与既有调用方不用改）。带默认预算。</summary>
        public static ScanResult Scan() { return Scan(ScanLimits.Default(), CancellationToken.None); }

        /// <summary>同步扫描，指定预算。</summary>
        public static ScanResult Scan(ScanLimits limits) { return Scan(limits, CancellationToken.None); }

        /// <summary>
        /// 同步扫描。会在调用方线程上做文件系统 IO —— UI 线程请用 ScanAsync，
        /// 否则就算列表是对的，界面也会先白一段时间（这段空白是用户唯一会记住的东西）。
        /// </summary>
        public static ScanResult Scan(ScanLimits limits, CancellationToken cancel)
        {
            ScanResult r = new ScanResult();
            r.Lim = limits == null ? ScanLimits.Default() : limits;
            r.Cancel = cancel;
            r.Clock = System.Diagnostics.Stopwatch.StartNew();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ScanSteam(r, seen);
                if (!r.ShouldStop()) ScanEpic(r, seen);
                if (!r.ShouldStop()) ScanWeGame(r, seen);
                if (!r.ShouldStop()) ScanXbox(r, seen);
                if (!r.ShouldStop()) ScanShortcuts(r, seen);
            }
            catch (Exception ex)
            {
                // 扫描整体失败时调用方看到 0 个游戏；Error 会显示在自检/页面详情里。
                Diag.Fail("scan", ex);
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }
            r.Clock.Stop();
            r.ElapsedMs = r.Clock.ElapsedMilliseconds;
            if (r.Canceled) r.Error = r.Error ?? "扫描未跑完（预算/取消）";
            return r;
        }

        /// <summary>
        /// 线程池扫描：调用方（UI 线程）立刻返回，结果在 done 里送回来。
        /// 这里必须是"一条文件系统调用都不在调用方线程上"——不是把同步调用包一层 Task 就算异步。
        /// done 为 null 时结果只进诊断（调用方不关心）。
        /// </summary>
        public static void ScanAsync(CancellationToken cancel, Action<ScanResult> done)
        {
            ScanAsync(ScanLimits.Default(), cancel, done);
        }

        public static void ScanAsync(ScanLimits limits, CancellationToken cancel, Action<ScanResult> done)
        {
            ScanLimits lim = limits == null ? ScanLimits.Default() : limits;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate (object state)
                {
                    ScanResult r;
                    try { r = Scan(lim, cancel); }
                    catch (Exception ex)
                    {
                        // 线程池上的异常没有任何人接：失败必须变成结果对象送回去。
                        Diag.Fail("scan.async", ex);
                        r = new ScanResult();
                        r.Error = ex.GetType().Name + ": " + ex.Message;
                        r.Canceled = true;
                    }
                    if (done == null) return;
                    try { done(r); }
                    catch (Exception ex)
                    {
                        // 回调（一般是 UI 投递）失败：不能再抛回线程池。
                        Diag.Fail("scan.async.done", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                // 线程池排不进（进程正在关停）：明确告诉调用方这次没跑，而不是永远没有结果。
                Diag.Fail("scan.async.queue", ex);
                if (done != null)
                {
                    try { done(new ScanResult { Error = "扫描未能排队：" + ex.Message, Canceled = true }); }
                    catch (Exception dex) { Diag.Fail("scan.async.done", dex); }
                }
            }
        }

        // ---- Steam ---------------------------------------------------------
        private static void ScanSteam(ScanResult r, HashSet<string> seen)
        {
            string root = SteamRoot();
            if (root == null) return;
            r.SteamFound = true;

            List<string> libs = new List<string>();
            libs.Add(root);

            string vdf = null;
            string a = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            string b = Path.Combine(root, "config", "libraryfolders.vdf");
            if (File.Exists(a)) vdf = a; else if (File.Exists(b)) vdf = b;

            if (vdf != null)
            {
                try
                {
                    string text = File.ReadAllText(vdf);
                    MatchCollection ms = Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"");
                    foreach (Match m in ms)
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (p.Length > 2 && !libs.Exists(delegate (string x) { return string.Equals(x, p, StringComparison.OrdinalIgnoreCase); }))
                            libs.Add(p);
                    }
                }
                catch (Exception ex)
                {
                    // 库清单读不出来 → 只剩主库。游戏少几个但界面看不出来，必须计数。
                    Diag.Fail("scan.steam.vdf", ex);
                }
            }

            r.SteamLibraries = libs.Count;
            for (int li = 0; li < libs.Count; li++)
            {
                if (r.ShouldStop()) return;
                string lib = libs[li];
                string common = Path.Combine(lib, "steamapps", "common");
                if (!Directory.Exists(common)) continue;

                // appmanifest_*.acf 才是"这个游戏装好了"的权威记录。
                // 不起眼的 common 目录里常躺着卸载后没删干净的残留（几十 MB 的空壳），
                // 不按清单过滤就会把它们当成游戏推荐给用户。
                HashSet<string> installed = InstalledDirs(Path.Combine(lib, "steamapps"), r);
                bool filter = installed.Count > 0;

                string[] dirs;
                try { dirs = Directory.GetDirectories(common); }
                catch (Exception ex)
                {
                    // 降级：整库不可读（权限/盘符离线）就跳过它，其余库继续。
                    Diag.Hit("scan.library.skip");
                    r.Error = r.Error ?? ("库跳过：" + ex.Message);
                    continue;
                }
                for (int di = 0; di < dirs.Length; di++)
                {
                    if (r.ShouldStop() || r.Full()) return;
                    if (!r.TakeDir()) return;
                    string dir = dirs[di];
                    try
                    {
                        string leaf = Path.GetFileName(dir);
                        if (filter && !installed.Contains(leaf)) continue;
                        FoundGame g = BestExe(r, dir, leaf, "Steam");
                        if (g == null) continue;
                        if (!seen.Add(g.Exe)) continue;
                        r.Games.Add(g);
                    }
                    catch (Exception ex)
                    {
                        // 单个游戏目录读失败（权限/损坏链接）：跳过它，不能整库放弃。
                        Diag.Hit("scan.game.skip");
                        if (r.Error == null) r.Error = "目录跳过：" + ex.Message;
                    }
                }
            }
        }

        /// <summary>读取 steamapps 下的安装清单：只要 StateFlags 带"已完整安装"位。</summary>
        private static HashSet<string> InstalledDirs(string steamapps, ScanResult r)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] acf;
            try { acf = Directory.GetFiles(steamapps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                // 降级：读不到清单就不做"已安装"过滤，可能多列几个卸载残留。
                Diag.Hit("scan.manifest.list");
                if (r.Error == null) r.Error = "清单不可读：" + ex.Message;
                return set;
            }
            for (int i = 0; i < acf.Length; i++)
            {
                if (r.ShouldStop()) return set;
                try
                {
                    string text = File.ReadAllText(acf[i]);
                    Match st = Regex.Match(text, "\"StateFlags\"\\s*\"(\\d+)\"");
                    if (st.Success)
                    {
                        long flags;
                        if (long.TryParse(st.Groups[1].Value, out flags) && (flags & 4) == 0) continue;
                    }
                    Match dir = Regex.Match(text, "\"installdir\"\\s*\"([^\"]+)\"");
                    if (dir.Success) set.Add(dir.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch (Exception ex)
                {
                    // 单个 acf 解析失败：少了这个游戏的"已安装"凭证，残余目录可能被当游戏推荐。
                    Diag.Hit("scan.manifest.parse");
                    if (r.Error == null) r.Error = "清单解析失败：" + ex.Message;
                }
            }
            return set;
        }

        private static string SteamRoot()
        {
            try
            {
                RegValue v = Reg.Read("HKCU", @"Software\Valve\Steam", "SteamPath");
                if (v.Existed && v.Value != null)
                {
                    string p = v.Value.ToString().Replace('/', '\\');
                    if (Directory.Exists(p)) return p;
                }
            }
            catch (Exception ex)
            {
                // 注册表读不到 Steam 路径 → 走下面的猜测列表。猜测可能全错，必须计数。
                Diag.Fail("scan.steam.root", ex);
            }
            string[] guesses = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
                @"D:\steam", @"D:\Steam", @"E:\Steam"
            };
            foreach (string g in guesses) if (Directory.Exists(g) && Directory.Exists(Path.Combine(g, "steamapps"))) return g;
            return null;
        }

        // ---- Epic ----------------------------------------------------------
        private static void ScanEpic(ScanResult r, HashSet<string> seen)
        {
            string dat = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                      "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
            if (!File.Exists(dat)) return;
            r.EpicFound = true;
            try
            {
                string text = File.ReadAllText(dat);
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(text);
                if (root == null) return;
                object listObj;
                if (!root.TryGetValue("InstallationList", out listObj)) return;
                object[] list = listObj as object[];
                if (list == null) return;
                foreach (object o in list)
                {
                    if (r.ShouldStop() || r.Full()) return;
                    Dictionary<string, object> entry = o as Dictionary<string, object>;
                    if (entry == null) continue;
                    object loc, app;
                    if (!entry.TryGetValue("InstallLocation", out loc) || loc == null) continue;
                    entry.TryGetValue("AppName", out app);
                    string dir = loc.ToString();
                    if (!Directory.Exists(dir)) continue;
                    string name = app == null ? Path.GetFileName(dir) : app.ToString();
                    FoundGame g = BestExe(r, dir, name, "Epic");
                    if (AddUniqueGame(r, seen, g)) r.EpicLocations++;
                }
            }
            catch (Exception ex)
            {
                // Epic 清单解析失败：Epic 库整个消失（界面只显示"没找到游戏"）。
                Diag.Fail("scan.epic", ex);
                if (r.Error == null) r.Error = "Epic 清单：" + ex.Message;
            }
        }

        // ---- exe 打分 ------------------------------------------------------
        /// <summary>
        /// 在游戏目录里挑一个最像"主程序"的 exe。
        /// 有界广度优先（深度 ≤ 3，最多访问 400 个目录项/游戏，另受总预算约束）；
        /// 跳过素材/着色器/运行时目录；UE 的 <Game>\Binaries\Win64\*-Shipping.exe、
        /// Unity 的 <Game>.exe 都能命中。
        /// </summary>
        private static FoundGame BestExe(ScanResult r, string dir, string gameName, string source)
        {
            List<string> files = new List<string>(64);
            CollectExes(r, dir, files, 0, 3);
            if (files.Count == 0) return null;

            string norm = Normalize(gameName);
            string[] tokens = Tokens(gameName);
            string best = null;
            double bestScore = -1;
            int pool = 0;

            for (int i = 0; i < files.Count; i++)
            {
                if (r.ShouldStop()) break;
                string f = files[i];
                string fn = Path.GetFileNameWithoutExtension(f);
                string low = fn.ToLowerInvariant();
                if (IsJunk(low)) continue;

                long len = 0;
                try { len = new FileInfo(f).Length; }
                catch (Exception ex)
                {
                    // 降级：读不到体积就按 0 计分，该 exe 排名下降但扫描继续。
                    Diag.Hit("scan.size");
                    Diag.Fail("scan.size.detail", ex);
                }
                double mb = len / 1048576.0;
                if (mb > 4) pool++;

                double score = Math.Min(30, mb / 2.0);          // 体积是"这是游戏本体"的强证据
                string nfn = Normalize(fn);
                if (nfn == norm) score += 60;
                else if (nfn.Length > 2 && norm.Length > 2 && (nfn.StartsWith(norm) || norm.StartsWith(nfn))) score += 35;
                else if (nfn.Length > 3 && norm.Length > 3 && (nfn.IndexOf(norm) >= 0 || norm.IndexOf(nfn) >= 0)) score += 22;
                else if (HitsToken(low, tokens)) score += 18;

                if (low.Contains("shipping")) score += 10;      // UE 打包产物
                if (low.Contains("win64")) score += 4;
                if (low.IndexOf("launcher") >= 0 || low.IndexOf("updater") >= 0) score -= 20;
                if (score > bestScore) { bestScore = score; best = f; }
            }
            // 目录里只有一个像样的可执行文件时，它几乎一定是本体
            if (best == null) return null;
            if (bestScore < 25 && !(pool == 1 && bestScore >= 10)) return null;

            FoundGame g = new FoundGame();
            g.Name = gameName;
            g.Exe = best;
            g.Source = source;
            g.Dir = dir;
            try { g.Bytes = new FileInfo(best).Length; }
            catch (Exception ex)
            {
                // 体积只是展示用的副标题，读不到就留空（SizeText 会显示空串）。
                Diag.Hit("scan.size.final");
                Diag.Fail("scan.size.final.detail", ex);
            }
            return g;
        }

        private static bool AddUniqueGame(ScanResult r, HashSet<string> seen, FoundGame g)
        {
            if (g == null || string.IsNullOrEmpty(g.Exe)) return false;
            if (!seen.Add(g.Exe)) return false;
            r.Games.Add(g);
            return true;
        }

        private static readonly string[] SkipDirs =
        {
            "content", "pak", "paks", "data", "movies", "movie", "redist", "_commonredist",
            "redistributables", "directx", "vcredist", "dotnet", "shaders", "cache",
            "localization", "locales", "audio", "sound", "music", "videos", "plugins",
            "easyanticheat", "battleye", "anticheat", "crashreportclient", "engine"
        };

        private static bool SkipDir(string name)
        {
            string low = name.ToLowerInvariant();
            for (int i = 0; i < SkipDirs.Length; i++) if (low == SkipDirs[i]) return true;
            return false;
        }

        private static void CollectExes(ScanResult r, string dir, List<string> into, int depth, int maxDepth)
        {
            if (r.ShouldStop()) return;
            if (into.Count >= r.Lim.MaxExesPerGame || depth > maxDepth) return;
            if (!r.TakeDir()) return;
            try
            {
                string[] exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < exes.Length; i++)
                {
                    if (into.Count >= r.Lim.MaxExesPerGame) return;
                    into.Add(exes[i]);
                }
            }
            catch (Exception ex)
            {
                // 降级：单个目录不可读（权限/链接）就跳过，不能整次扫描失败。
                Diag.Hit("scan.walk.files");
                Diag.Fail("scan.walk.files.detail", ex);
                return;
            }

            if (depth == maxDepth) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception ex)
            {
                // 降级：同上，子目录不可读就到此为止。
                Diag.Hit("scan.walk.dirs");
                Diag.Fail("scan.walk.dirs.detail", ex);
                return;
            }
            for (int i = 0; i < subs.Length; i++)
            {
                if (r.ShouldStop()) return;
                string name = Path.GetFileName(subs[i]);
                if (SkipDir(name)) continue;
                CollectExes(r, subs[i], into, depth + 1, maxDepth);
                if (into.Count >= r.Lim.MaxExesPerGame) return;
            }
        }

        private static string[] Tokens(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            string[] raw = s.Split(new char[] { ' ', '-', '_', '.', ':', '|' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> keep = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string t = raw[i].ToLowerInvariant();
                if (t.Length < 3) continue;
                if (t == "the" || t == "and" || t == "of" || t == "edition" || t == "game") continue;
                keep.Add(t);
            }
            return keep.ToArray();
        }

        private static bool HitsToken(string low, string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++) if (low.IndexOf(tokens[i]) >= 0) return true;
            return false;
        }

        private static bool IsJunk(string low)
        {
            for (int i = 0; i < JunkStrong.Length; i++)
                if (low.IndexOf(JunkStrong[i], StringComparison.Ordinal) >= 0) return true;
            for (int i = 0; i < JunkPrefix.Length; i++)
                if (low.StartsWith(JunkPrefix[i], StringComparison.Ordinal)) return true;
            for (int i = 0; i < JunkWhole.Length; i++)
                if (IsWholeWord(low, JunkWhole[i])) return true;
            return false;
        }

        /// <summary>token 出现在 low 里，且两侧都不是字母/数字（"reach" 里的 "eac" 不算命中）。</summary>
        private static bool IsWholeWord(string low, string token)
        {
            int at = 0;
            while (at <= low.Length - token.Length)
            {
                int hit = low.IndexOf(token, at, StringComparison.Ordinal);
                if (hit < 0) return false;
                bool left = hit == 0 || !char.IsLetterOrDigit(low[hit - 1]);
                int end = hit + token.Length;
                bool right = end >= low.Length || !char.IsLetterOrDigit(low[end]);
                if (left && right) return true;
                at = hit + 1;
            }
            return false;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = char.ToLowerInvariant(s[i]);
                if (c >= 'a' && c <= 'z') sb.Append(c);
                else if (c >= '0' && c <= '9') sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
