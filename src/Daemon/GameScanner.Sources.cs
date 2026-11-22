// ============================================================================
//  GameScanner.Sources.cs — WeGame / Xbox (MS Store) / 桌面与开始菜单快捷方式扫描
//
//  硬约束：
//   · 全程遵守 ScanResult 预算（MaxMs / MaxGames / MaxDirsVisited）与 CancellationToken。
//   · .lnk 解析使用纯托管 [MS-SHLLINK] 二进制解析，绝不依赖 COM / WSH 避免 STA 卡死。
//   · 桌面与开始菜单快捷方式严格过滤常见办公/开发/浏览器/工具/启动器本体。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace Go.Daemon
{
    public static partial class GameScanner
    {
        private static readonly string[] WeGameJunk =
        {
            "wegame", "tgp_daemon", "rail", "teniodl", "qbclient", "bugreport", "tcls", "cross"
        };

        private static readonly string[] BrowserExes =
        {
            "chrome", "chrome_proxy", "msedge", "msedge_proxy", "msedgewebview2",
            "firefox", "opera", "brave", "vivaldi", "arc", "chromium",
            "360se", "360chrome", "qqbrowser", "liebao", "sogouexplorer",
            "quark", "ucbrowser", "yandex", "iexplore"
        };

        private static readonly string[] NonGameKeywords =
        {
            "chrome", "chrome_proxy", "msedge", "msedge_proxy", "firefox", "opera", "brave", "360se", "qqbrowser", "browser",
            "wechat", "weixin", "qq", "tim", "dingtalk", "feishu", "lark", "wps", "winword",
            "excel", "powerpnt", "onenote", "outlook", "notion", "obsidian", "typora", "foxmail",
            "code", "devenv", "rider", "idea64", "pycharm", "clion", "goland", "webstorm",
            "cursor", "windsurf", "trae", "zed", "codex", "claude", "gemini", "antigravity",
            "qoder", "workbuddy", "zcode", "github", "legion", "multisim", "mumu", "deepseek",
            "lex", "gmail", "chatgpt", "ollama", "docker", "vmware", "virtualbox", "postman",
            "apifox", "navicat", "dbeaver", "xshell", "mobaxterm", "finalshell", "putty",
            "winscp", "wireshark", "fiddler", "zotero", "anki", "kicad", "matlab", "autocad",
            "solidworks", "blender",
            "git", "powershell", "pwsh", "cmd", "wt", "conhost", "python", "node", "notepad++",
            "sublime_text", "7zfm", "winrar", "bandizip", "everything", "snipaste", "sharex",
            "obs64", "obs32", "potplayer", "vlc", "cloudmusic", "qqmusic", "kugou", "spotify",
            "thunder", "baidunetdisk", "aliyundrive", "clash", "mihomo", "v2rayn", "sing-box",
            "gameoptimizer", "steam", "epicgameslauncher", "wegame", "xbox",
            "battle.net", "upc", "ubisoftconnect", "eadesktop", "origin", "galaxyclient",
            "discord", "teamspeak", "yy", "nvidia", "geforce", "nvcontainer", "amd", "radeon",
            "adrenalin", "msiafterburner", "rtss", "armoury", "icue", "lghub", "razer",
            "synapse", "logioptions", "cpuz", "gpuz", "hwinfo", "aida64", "3dmark",
            "cinebench", "furmark", "regedit", "taskmgr", "explorer", "mmc", "control",
            "uninstall", "uninst", "update", "updater", "installer", "setup", "helper"
        };

        private static readonly string[] NonGameSubstrings =
        {
            "gameoptimizer", "cc switch", "cc-switch", "ccswitch", "cherry studio", "cherry-studio", "cherrystudio",
            "google docs", "google drive", "google sheets", "google slides", "google meet",
            "google calendar", "antigravity", "workbuddy", "deepseek", "multisim", "mumu",
            "legionzone", "legion toolkit", "github desktop", "vs code", "vscode",
            "jetbrains", "visual studio", "national instruments", "circuit design suite",
            "chrome_proxy", "msedge_proxy", "msedgewebview", "lm studio", "lmstudio"
        };

        private static readonly string[] NonGameToolPathHints =
        {
            "\\google\\chrome\\", "\\microsoft\\edge\\", "\\mozilla firefox\\", "\\brave-browser\\",
            "\\microsoft vs code\\", "\\vscode\\", "\\cursor\\", "\\windsurf\\", "\\trae\\",
            "\\jetbrains\\", "\\android studio\\", "\\microsoft visual studio\\",
            "\\notepad++\\", "\\sublime text\\", "\\git\\", "\\github\\",
            "\\python\\", "\\nodejs\\", "\\anaconda\\", "\\miniconda\\",
            "\\docker\\", "\\vmware\\", "\\virtualbox\\", "\\national instruments\\",
            "\\autodesk\\", "\\adobe\\", "\\kingsoft\\", "\\wps office\\",
            "\\microsoft office\\", "\\cherry studio\\", "\\cherrystudio\\",
            "\\cc switch\\", "\\cc-switch\\", "\\antigravity\\", "\\qoder\\",
            "\\workbuddy\\", "\\zcode\\", "\\lenovo\\legion", "\\legionzone\\",
            "\\mumu", "\\obsidian\\", "\\notion\\", "\\typora\\",
            "\\douyin\\", "\\qishui\\", "\\chaoxing\\"
        };

        private static readonly string[] NonGameTitleHints =
        {
            "微信", "企业微信", "钉钉", "飞书", "腾讯会议", "金山", "有道", "百度网盘", "阿里云盘",
            "迅雷", "网易云音乐", "qq音乐", "酷狗", "剪映", "控制面板", "任务管理器", "命令提示符",
            "终端", "记事本", "计算器", "画图", "卸载", "说明", "文档", "手册", "帮助",
            "千问", "学习通", "汽水音乐", "抖音", "模型", "图纸", "资料", "模拟器", "浏览器", "网盘"
        };

        // ---- 1. WeGame -----------------------------------------------------
        private static void ScanWeGame(ScanResult r, HashSet<string> seen)
        {
            List<string> roots = DiscoverWeGameAppsDirs();
            if (roots.Count == 0) return;

            for (int i = 0; i < roots.Count; i++)
            {
                if (r.ShouldStop() || r.Full()) break;
                ScanWeGameFolder(r, seen, roots[i]);
            }
        }

        internal static void ScanWeGameFolder(ScanResult r, HashSet<string> seen, string appsDir)
        {
            if (string.IsNullOrEmpty(appsDir) || !Directory.Exists(appsDir)) return;
            r.WeGameFound = true;
            if (!r.TakeDir()) return;

            string[] gameDirs;
            try { gameDirs = Directory.GetDirectories(appsDir); }
            catch (Exception ex)
            {
                Diag.Fail("scan.wegame.dirs", ex);
                return;
            }

            for (int i = 0; i < gameDirs.Length; i++)
            {
                if (r.ShouldStop() || r.Full()) break;
                string gdir = gameDirs[i];
                string gname = Path.GetFileName(gdir);
                if (string.IsNullOrEmpty(gname)) continue;
                string lowName = gname.ToLowerInvariant();
                if (lowName == "rail_files" || lowName == "wegame" || lowName == "tgp") continue;

                FoundGame g = BestExe(r, gdir, gname, "WeGame");
                if (g == null) continue;
                string exeLow = Path.GetFileNameWithoutExtension(g.Exe).ToLowerInvariant();
                if (ContainsAnyToken(exeLow, WeGameJunk)) continue;

                if (AddUniqueGame(r, seen, g)) r.WeGameLocations++;
            }
        }

        private static List<string> DiscoverWeGameAppsDirs()
        {
            List<string> list = new List<string>();
            HashSet<string> seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            TryAddWeGameRegDir("HKLM", @"SOFTWARE\Tencent\WeGame", list, seenDirs);
            TryAddWeGameRegDir("HKLM", @"SOFTWARE\WOW6432Node\Tencent\WeGame", list, seenDirs);
            TryAddWeGameRegDir("HKCU", @"SOFTWARE\Tencent\WeGame", list, seenDirs);
            TryAddWeGameRegDir("HKCU", @"SOFTWARE\Tencent\rail", list, seenDirs);

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    DriveInfo d = drives[i];
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string root = d.RootDirectory.FullName;
                    AddExistingDir(Path.Combine(root, "WeGameApps"), list, seenDirs);
                    AddExistingDir(Path.Combine(root, "Games", "WeGameApps"), list, seenDirs);
                    AddExistingDir(Path.Combine(root, "Program Files (x86)", "WeGameApps"), list, seenDirs);
                    AddExistingDir(Path.Combine(root, "Program Files", "WeGameApps"), list, seenDirs);
                }
            }
            catch (Exception ex) { Diag.Fail("scan.wegame.drives", ex); }
            return list;
        }

        private static void TryAddWeGameRegDir(string hive, string key, List<string> list, HashSet<string> seenDirs)
        {
            string[] valNames = { "setuppath", "InstallPath", "LauncherPath", "GamePath" };
            for (int i = 0; i < valNames.Length; i++)
            {
                try
                {
                    RegValue rv = Reg.Read(hive, key, valNames[i]);
                    if (!rv.Existed || rv.Value == null) continue;
                    string raw = rv.Value.ToString().Trim().Trim('"');
                    if (raw.Length == 0) continue;
                    string baseDir = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetDirectoryName(raw)
                        : raw;
                    if (string.IsNullOrEmpty(baseDir)) continue;
                    AddExistingDir(Path.Combine(baseDir, "WeGameApps"), list, seenDirs);
                    string parent = Path.GetDirectoryName(baseDir);
                    if (!string.IsNullOrEmpty(parent))
                        AddExistingDir(Path.Combine(parent, "WeGameApps"), list, seenDirs);
                    if (baseDir.EndsWith("WeGameApps", StringComparison.OrdinalIgnoreCase))
                        AddExistingDir(baseDir, list, seenDirs);
                }
                catch (Exception ex) { Diag.Fail("scan.wegame.reg", ex); }
            }
        }

        // ---- 2. Xbox / MS Store -------------------------------------------
        private static void ScanXbox(ScanResult r, HashSet<string> seen)
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) { Diag.Fail("scan.xbox.drives", ex); return; }

            for (int i = 0; i < drives.Length; i++)
            {
                if (r.ShouldStop() || r.Full()) break;
                DriveInfo d = drives[i];
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                ScanXboxDriveRoot(r, seen, d.RootDirectory.FullName);
            }
        }

        internal static void ScanXboxDriveRoot(ScanResult r, HashSet<string> seen, string driveRoot)
        {
            if (string.IsNullOrEmpty(driveRoot) || !Directory.Exists(driveRoot)) return;
            HashSet<string> xboxFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string gamingRootFile = Path.Combine(driveRoot, ".GamingRoot");
            if (File.Exists(gamingRootFile))
            {
                r.XboxFound = true;
                string rel = ReadGamingRootFolder(gamingRootFile);
                if (!string.IsNullOrEmpty(rel))
                {
                    string full = Path.Combine(driveRoot, rel);
                    if (Directory.Exists(full)) xboxFolders.Add(full);
                }
            }

            string defXbox = Path.Combine(driveRoot, "XboxGames");
            if (Directory.Exists(defXbox))
            {
                r.XboxFound = true;
                xboxFolders.Add(defXbox);
            }

            foreach (string folder in xboxFolders)
            {
                if (r.ShouldStop() || r.Full()) break;
                ScanXboxFolder(r, seen, folder);
            }
        }

        private static string ReadGamingRootFolder(string path)
        {
            try
            {
                byte[] buf = File.ReadAllBytes(path);
                if (buf.Length < 10) return null;
                // .GamingRoot 以 "RGBX" + 版本头（共 8 字节）起头，随后为 UTF-16LE 相对路径（\0 结尾）
                int offset = 8;
                if (buf.Length <= offset) return null;
                string s = Encoding.Unicode.GetString(buf, offset, buf.Length - offset);
                int nul = s.IndexOf('\0');
                if (nul >= 0) s = s.Substring(0, nul);
                s = s.Trim().TrimStart('\\', '/');
                return s.Length == 0 ? null : s;
            }
            catch (Exception ex)
            {
                Diag.Fail("scan.xbox.gamingroot", ex);
                return null;
            }
        }

        private static void ScanXboxFolder(ScanResult r, HashSet<string> seen, string root)
        {
            if (!r.TakeDir()) return;
            string[] gameDirs;
            try { gameDirs = Directory.GetDirectories(root); }
            catch (Exception ex) { Diag.Fail("scan.xbox.dirs", ex); return; }

            for (int i = 0; i < gameDirs.Length; i++)
            {
                if (r.ShouldStop() || r.Full()) break;
                string gdir = gameDirs[i];
                string folderName = Path.GetFileName(gdir);
                string contentDir = Path.Combine(gdir, "Content");
                string baseDir = Directory.Exists(contentDir) ? contentDir : gdir;
                string cfg = Path.Combine(baseDir, "MicrosoftGame.Config");
                FoundGame g = null;

                if (File.Exists(cfg))
                {
                    string displayName, exeRel;
                    ParseMicrosoftGameConfig(cfg, out displayName, out exeRel);
                    string title = string.IsNullOrEmpty(displayName) ? folderName : displayName;
                    if (!string.IsNullOrEmpty(exeRel))
                    {
                        string candidate = Path.Combine(baseDir, exeRel);
                        if (File.Exists(candidate))
                        {
                            g = new FoundGame();
                            g.Name = title;
                            g.Exe = candidate;
                            g.Source = "Xbox";
                            g.Dir = baseDir;
                            try { g.Bytes = new FileInfo(candidate).Length; }
                            catch (Exception ex) { Diag.Fail("scan.xbox.size", ex); }
                        }
                    }
                    if (g == null) g = BestExe(r, baseDir, title, "Xbox");
                }
                else
                {
                    g = BestExe(r, baseDir, folderName, "Xbox");
                }

                if (AddUniqueGame(r, seen, g)) r.XboxLocations++;
            }
        }

        internal static void ParseMicrosoftGameConfig(string cfgPath, out string displayName, out string exeRel)
        {
            displayName = "";
            exeRel = "";
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings();
                settings.DtdProcessing = DtdProcessing.Prohibit;
                settings.XmlResolver = null;
                XmlDocument doc = new XmlDocument();
                doc.XmlResolver = null;
                using (XmlReader xr = XmlReader.Create(cfgPath, settings))
                {
                    doc.Load(xr);
                }

                XmlNodeList visuals = doc.GetElementsByTagName("ShellVisuals");
                if (visuals.Count > 0 && visuals[0].Attributes != null)
                {
                    XmlAttribute attr = visuals[0].Attributes["DefaultDisplayName"];
                    if (attr != null)
                    {
                        string v = attr.Value == null ? "" : attr.Value.Trim();
                        if (v.Length > 0 && !v.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                            displayName = v;
                    }
                }

                XmlNodeList exes = doc.GetElementsByTagName("Executable");
                string fallback = "";
                for (int i = 0; i < exes.Count; i++)
                {
                    XmlAttribute attr = exes[i].Attributes == null ? null : exes[i].Attributes["Name"];
                    if (attr == null) continue;
                    string name = attr.Value == null ? "" : attr.Value.Trim();
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.IndexOf("gamelaunchhelper", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (fallback.Length == 0) fallback = name;
                        continue;
                    }
                    exeRel = name;
                    break;
                }
                if (exeRel.Length == 0) exeRel = fallback;
            }
            catch (Exception ex)
            {
                Diag.Fail("scan.xbox.config", ex);
            }
        }

        // ---- 3. 桌面与开始菜单快捷方式 ------------------------------------
        private static void ScanShortcuts(ScanResult r, HashSet<string> seen)
        {
            List<string> dirs = new List<string>();
            HashSet<string> dirSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddSpecialDir(Environment.SpecialFolder.DesktopDirectory, null, dirs, dirSeen);
            AddSpecialDir(Environment.SpecialFolder.CommonDesktopDirectory, null, dirs, dirSeen);
            AddSpecialDir(Environment.SpecialFolder.StartMenu, "Programs", dirs, dirSeen);
            AddSpecialDir(Environment.SpecialFolder.CommonStartMenu, "Programs", dirs, dirSeen);

            for (int i = 0; i < dirs.Count; i++)
            {
                if (r.ShouldStop() || r.Full()) break;
                int maxDepth = i < 2 ? 1 : 2;
                ScanShortcutsDir(r, seen, dirs[i], 0, maxDepth);
            }
        }

        internal static void ScanShortcutsDir(ScanResult r, HashSet<string> seen, string dir, int depth, int maxDepth)
        {
            if (r.ShouldStop() || r.Full() || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            if (!r.TakeDir()) return;
            r.ShortcutsFound = true;

            try
            {
                string[] files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    if (r.ShouldStop() || r.Full()) break;
                    string f = files[i];
                    bool isLnk = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
                    bool isUrl = !isLnk && f.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
                    if (!isLnk && !isUrl) continue;

                    string title = Path.GetFileNameWithoutExtension(f);
                    string target = isLnk ? ResolveLnkTarget(f) : ResolveUrlTarget(f);
                    if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                    if (!IsLikelyGameShortcut(title, target)) continue;

                    FoundGame g = new FoundGame();
                    g.Name = title;
                    g.Exe = target;
                    g.Source = "桌面";
                    g.Dir = Path.GetDirectoryName(target) ?? "";
                    try { g.Bytes = new FileInfo(target).Length; }
                    catch (Exception ex) { Diag.Fail("scan.shortcut.size", ex); }
                    if (g.Bytes > 0 && g.Bytes < 256 * 1024) continue;

                    if (AddUniqueGame(r, seen, g)) r.ShortcutLocations++;
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("scan.shortcut.files", ex);
                return;
            }

            if (depth >= maxDepth) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception ex) { Diag.Fail("scan.shortcut.dirs", ex); return; }

            for (int i = 0; i < subs.Length; i++)
            {
                if (r.ShouldStop() || r.Full()) break;
                string subName = Path.GetFileName(subs[i]);
                if (string.IsNullOrEmpty(subName)) continue;
                string low = subName.ToLowerInvariant();
                if (low.IndexOf("windows") >= 0 || low.IndexOf("microsoft") >= 0 ||
                    low.IndexOf("administrative") >= 0 || low.IndexOf("accessibility") >= 0)
                    continue;
                ScanShortcutsDir(r, seen, subs[i], depth + 1, maxDepth);
            }
        }

        internal static bool IsLikelyGameShortcut(string title, string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return false;

            string lowPath = exePath.ToLowerInvariant().Replace('/', '\\');
            if (lowPath.IndexOf("\\windows\\", StringComparison.Ordinal) >= 0 ||
                lowPath.IndexOf("\\windowsapps\\", StringComparison.Ordinal) >= 0 ||
                lowPath.IndexOf("\\system32\\", StringComparison.Ordinal) >= 0 ||
                lowPath.IndexOf("\\syswow64\\", StringComparison.Ordinal) >= 0)
                return false;

            string exeName = Path.GetFileNameWithoutExtension(lowPath);
            for (int i = 0; i < BrowserExes.Length; i++)
            {
                if (string.Equals(exeName, BrowserExes[i], StringComparison.Ordinal)) return false;
            }
            if (ContainsAnySubstring(lowPath, NonGameToolPathHints)) return false;
            if (IsJunk(exeName) || ContainsAnyToken(exeName, NonGameKeywords) || ContainsAnySubstring(exeName, NonGameSubstrings))
                return false;

            string lowTitle = (title ?? "").ToLowerInvariant();
            if (!IsLikelyGameTitle(lowTitle)) return false;
            if (ContainsAnySubstring(exeName, NonGameTitleHints)) return false;

            // AppData\Local\Programs / Program Files 下的工具目录与折叠名再做一层严格兜底
            if (lowPath.IndexOf("\\appdata\\local\\programs\\", StringComparison.Ordinal) >= 0 ||
                lowPath.IndexOf("\\program files\\", StringComparison.Ordinal) >= 0 ||
                lowPath.IndexOf("\\program files (x86)\\", StringComparison.Ordinal) >= 0)
            {
                if (ContainsAnySubstring(lowPath, NonGameSubstrings) || ContainsAnySubstring(lowPath, NonGameTitleHints))
                    return false;
            }
            return true;
        }

        private static bool IsLikelyGameTitle(string lowTitle)
        {
            if (string.IsNullOrEmpty(lowTitle)) return false;
            if (ContainsAnySubstring(lowTitle, NonGameTitleHints)) return false;
            if (ContainsAnySubstring(lowTitle, NonGameSubstrings)) return false;
            if (ContainsAnyToken(lowTitle, NonGameKeywords)) return false;
            string collapsed = Normalize(lowTitle);
            if (collapsed.Length > 0 && ContainsAnySubstring(collapsed, NonGameSubstrings)) return false;
            return true;
        }

        internal static string ResolveUrlTarget(string urlPath)
        {
            try
            {
                string[] lines = File.ReadAllLines(urlPath);
                string iconFile = null;
                string url = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        string p = line.Substring(9).Trim().Trim('"');
                        int comma = p.LastIndexOf(',');
                        if (comma > 0 && p.Substring(0, comma).EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            p = p.Substring(0, comma).Trim();
                        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                            iconFile = p;
                    }
                    else if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        url = line.Substring(4).Trim();
                    }
                }
                if (string.IsNullOrEmpty(url)) return null;

                if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                {
                    string fp = Uri.UnescapeDataString(url.Substring(8)).Replace('/', '\\');
                    if (fp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(fp))
                        return fp;
                    return null;
                }

                if (!IsGameProtocolUrl(url)) return null;
                string title = Path.GetFileNameWithoutExtension(urlPath) ?? "";
                if (!IsLikelyGameTitle(title.ToLowerInvariant())) return null;
                if (!string.IsNullOrEmpty(iconFile) && IsLikelyGameShortcut(title, iconFile))
                    return iconFile;
                return null;
            }
            catch (Exception ex)
            {
                Diag.Fail("scan.url.read", ex);
                return null;
            }
        }

        private static bool IsGameProtocolUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("steam://run/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("uplay://launch/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("uplay://run/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("com.epicgames.launcher://apps/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("origin://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("origin2://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("eadm://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("battlenet://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("riotclient://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("wegame://", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsAnySubstring(string low, string[] needles)
        {
            if (string.IsNullOrEmpty(low)) return false;
            for (int i = 0; i < needles.Length; i++)
            {
                if (low.IndexOf(needles[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>纯托管解析 Windows .lnk ([MS-SHLLINK]) 目标路径，零 COM/WSH 依赖。</summary>
        internal static string ResolveLnkTarget(string lnkPath)
        {
            try
            {
                byte[] b = File.ReadAllBytes(lnkPath);
                if (b.Length < 76) return null;
                uint hdrSize = BitConverter.ToUInt32(b, 0);
                if (hdrSize != 0x4C) return null;

                uint flags = BitConverter.ToUInt32(b, 20);
                int pos = 76;

                if ((flags & 0x01) != 0) // HasLinkTargetIDList
                {
                    if (pos + 2 > b.Length) return null;
                    ushort idListSize = BitConverter.ToUInt16(b, pos);
                    pos += 2 + idListSize;
                }

                string target = null;
                if ((flags & 0x02) != 0) // HasLinkInfo
                {
                    if (pos + 28 > b.Length) return null;
                    int infoStart = pos;
                    uint infoSize = BitConverter.ToUInt32(b, infoStart);
                    uint infoHdr = BitConverter.ToUInt32(b, infoStart + 4);
                    uint infoFlags = BitConverter.ToUInt32(b, infoStart + 8);
                    if ((infoFlags & 0x01) != 0) // VolumeIDAndLocalBasePath
                    {
                        uint localAnsiOff = BitConverter.ToUInt32(b, infoStart + 16);
                        uint commonAnsiOff = BitConverter.ToUInt32(b, infoStart + 24);
                        string baseP = null;
                        string suffixP = null;
                        if (infoHdr >= 0x24 && infoStart + 36 <= b.Length)
                        {
                            uint localUniOff = BitConverter.ToUInt32(b, infoStart + 28);
                            uint commonUniOff = BitConverter.ToUInt32(b, infoStart + 32);
                            if (localUniOff > 0) baseP = ReadNullUni(b, infoStart + (int)localUniOff);
                            if (commonUniOff > 0) suffixP = ReadNullUni(b, infoStart + (int)commonUniOff);
                        }
                        if (string.IsNullOrEmpty(baseP) && localAnsiOff > 0)
                            baseP = ReadNullAnsi(b, infoStart + (int)localAnsiOff);
                        if (string.IsNullOrEmpty(suffixP) && commonAnsiOff > 0)
                            suffixP = ReadNullAnsi(b, infoStart + (int)commonAnsiOff);
                        if (!string.IsNullOrEmpty(baseP))
                            target = string.IsNullOrEmpty(suffixP) ? baseP : (baseP + suffixP);
                    }
                    pos = infoStart + (int)infoSize;
                }

                if (!string.IsNullOrEmpty(target) && File.Exists(target)) return target;

                // 若 LinkInfo 缺失或不可用，则继续按 StringData 读 RelativePath
                bool unicode = (flags & 0x80) != 0;
                if ((flags & 0x04) != 0) SkipStringData(b, ref pos, unicode); // Name
                if ((flags & 0x08) != 0)
                {
                    string rel = ReadStringData(b, ref pos, unicode);
                    if (!string.IsNullOrEmpty(rel))
                    {
                        try
                        {
                            string baseDir = Path.GetDirectoryName(lnkPath) ?? "";
                            string full = Path.GetFullPath(Path.Combine(baseDir, rel));
                            if (File.Exists(full)) return full;
                        }
                        catch (Exception ex) { Diag.Fail("scan.lnk.rel", ex); }
                    }
                }
                return target;
            }
            catch (Exception ex)
            {
                Diag.Fail("scan.lnk.read", ex);
                return null;
            }
        }

        private static string ReadNullAnsi(byte[] b, int off)
        {
            if (off < 0 || off >= b.Length) return null;
            int end = off;
            while (end < b.Length && b[end] != 0) end++;
            return Encoding.Default.GetString(b, off, end - off);
        }

        private static string ReadNullUni(byte[] b, int off)
        {
            if (off < 0 || off + 1 >= b.Length) return null;
            int end = off;
            while (end + 1 < b.Length && (b[end] != 0 || b[end + 1] != 0)) end += 2;
            return Encoding.Unicode.GetString(b, off, end - off);
        }

        private static void SkipStringData(byte[] b, ref int pos, bool unicode)
        {
            ReadStringData(b, ref pos, unicode);
        }

        private static string ReadStringData(byte[] b, ref int pos, bool unicode)
        {
            if (pos + 2 > b.Length) { pos = b.Length; return null; }
            ushort count = BitConverter.ToUInt16(b, pos);
            pos += 2;
            int byteLen = count * (unicode ? 2 : 1);
            if (byteLen <= 0 || pos + byteLen > b.Length) { pos = Math.Min(b.Length, pos + Math.Max(0, byteLen)); return null; }
            string s = unicode ? Encoding.Unicode.GetString(b, pos, byteLen) : Encoding.Default.GetString(b, pos, byteLen);
            pos += byteLen;
            return s;
        }

        private static bool ContainsAnyToken(string low, string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                if (IsWholeWord(low, tokens[i])) return true;
            }
            return false;
        }

        private static void AddExistingDir(string dir, List<string> list, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                if (Directory.Exists(dir) && seen.Add(dir)) list.Add(dir);
            }
            catch (Exception ex) { Diag.Fail("scan.dir.exists", ex); }
        }

        private static void AddSpecialDir(Environment.SpecialFolder sf, string sub, List<string> list, HashSet<string> seen)
        {
            try
            {
                string p = Environment.GetFolderPath(sf);
                if (string.IsNullOrEmpty(p)) return;
                if (!string.IsNullOrEmpty(sub)) p = Path.Combine(p, sub);
                AddExistingDir(p, list, seen);
            }
            catch (Exception ex) { Diag.Fail("scan.specialdir", ex); }
        }
    }
}
