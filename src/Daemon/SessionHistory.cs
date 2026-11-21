// ============================================================================
//  SessionHistory.cs — 最近 30 局对局复盘战报持久化（原子写 + 防损坏隔离）
//
//  存储路径：%LOCALAPPDATA%\GameOptimizer\sessions.json（自动遵循 DirOverride /
//  Ledger.DirOverride 测试沙箱隔离，测试不污染宿主真实数据）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Go.Daemon
{
    public sealed class SessionHistoryItem
    {
        public string ProfileId = "";
        public string ProfileName = "";
        public string Exe = "";
        public string StartedAt = "";
        public string StartedTime = "";
        public double DurationSec;
        public string DurationText = "";
        public int Samples;
        public int JitterSamples;
        public int StutterWindows;
        public double AvgP99Us;
        public double MaxJitterUs;
        public string Verdict = "";
        public string TopCulprit = "";
        public double TopCulpritPct;
        public int EcoThrottledCount;
    }

    public static class SessionHistory
    {
        public const int Cap = 30;
        private static readonly object _gate = new object();
        public static volatile string DirOverride;
        public static bool LastLoadCorrupt { get; private set; }

        public static string CurrentDir
        {
            get
            {
                return DirOverride ?? Ledger.DirOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GameOptimizer");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(CurrentDir, "sessions.json"); }
        }

        public static string UseSandboxDir()
        {
            int pid = 0;
            try { pid = Process.GetCurrentProcess().Id; }
            catch (Exception ex) { Diag.Fail("sessionhistory.sandbox.pid", ex); }
            string dir = Path.Combine(Path.GetTempPath(), "GameOptimizer", "sessions-sandbox", pid.ToString());
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { Diag.Fail("sessionhistory.sandbox.clean", ex); }
            Directory.CreateDirectory(dir);
            DirOverride = dir;
            return dir;
        }

        public static void ResetDirOverride()
        {
            DirOverride = null;
        }

        public static List<SessionHistoryItem> Load()
        {
            lock (_gate)
            {
                LastLoadCorrupt = false;
                string p = FilePath;
                if (!File.Exists(p)) return new List<SessionHistoryItem>();
                try
                {
                    string txt = File.ReadAllText(p, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(txt)) return new List<SessionHistoryItem>();
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    List<SessionHistoryItem> items = js.Deserialize<List<SessionHistoryItem>>(txt);
                    return items ?? new List<SessionHistoryItem>();
                }
                catch (Exception ex)
                {
                    LastLoadCorrupt = true;
                    Diag.Fail("sessionhistory.parse", ex);
                    Quarantine(p);
                    return new List<SessionHistoryItem>();
                }
            }
        }

        public static List<SessionHistoryItem> LoadNewestFirst()
        {
            List<SessionHistoryItem> list = Load();
            list.Reverse();
            return list;
        }

        public static SessionHistoryItem FromReport(SessionReport r, string profileId, string exe, int ecoCount)
        {
            SessionHistoryItem item = new SessionHistoryItem();
            if (r == null) return item;
            item.ProfileId = profileId ?? "";
            item.ProfileName = r.ProfileName ?? "";
            item.Exe = exe ?? "";
            item.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            item.StartedTime = r.Started ?? "";
            item.DurationSec = r.DurationSec;
            item.DurationText = r.DurationText ?? "";
            item.Samples = r.Samples;
            item.JitterSamples = r.JitterSamples;
            item.StutterWindows = r.StutterWindows;
            item.AvgP99Us = r.AvgP99Us;
            item.MaxJitterUs = r.MaxJitterUs;
            item.Verdict = r.Verdict ?? "";
            item.EcoThrottledCount = ecoCount;
            if (r.Top != null && r.Top.Count > 0 && r.Top[0] != null)
            {
                item.TopCulprit = r.Top[0].Name ?? "";
                item.TopCulpritPct = r.Top[0].CpuPercent;
            }
            return item;
        }

        public static bool Append(SessionReport r, string profileId, string exe, int ecoCount)
        {
            if (r == null) return false;
            return Append(FromReport(r, profileId, exe, ecoCount));
        }

        public static bool Append(SessionHistoryItem item)
        {
            if (item == null) return false;
            lock (_gate)
            {
                try
                {
                    List<SessionHistoryItem> list = Load();
                    list.Add(item);
                    while (list.Count > Cap) list.RemoveAt(0);
                    return SaveInternal(list);
                }
                catch (Exception ex)
                {
                    Diag.Fail("sessionhistory.append", ex);
                    return false;
                }
            }
        }

        public static bool Clear()
        {
            lock (_gate)
            {
                try
                {
                    string p = FilePath;
                    if (File.Exists(p)) File.Delete(p);
                    return true;
                }
                catch (Exception ex)
                {
                    Diag.Fail("sessionhistory.clear", ex);
                    return false;
                }
            }
        }

        private static bool SaveInternal(List<SessionHistoryItem> list)
        {
            string p = FilePath;
            string dir = Path.GetDirectoryName(p);
            string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                JavaScriptSerializer js = new JavaScriptSerializer();
                string json = js.Serialize(list);
                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                // 连续高频写盘（或杀软/索引器瞬时扫描句柄）下 File.Replace 可能偶发共享冲突；
                // 带短退避重试 5 次，防止静默吞错丢掉当次战报条目。
                Exception lastErr = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(p)) File.Replace(tmp, p, null);
                        else File.Move(tmp, p);
                        return true;
                    }
                    catch (IOException ex) { lastErr = ex; }
                    catch (UnauthorizedAccessException ex) { lastErr = ex; }
                    System.Threading.Thread.Sleep(12 * (attempt + 1));
                }
                if (lastErr != null) throw lastErr;
                return false;
            }
            catch (Exception ex)
            {
                Diag.Fail("sessionhistory.save", ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception cx) { Diag.Fail("sessionhistory.tmpclean", cx); }
                return false;
            }
        }

        private static void Quarantine(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string stem = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string dst = stem;
                for (int i = 2; i < 100 && File.Exists(dst); i++) dst = stem + "-" + i;
                File.Move(path, dst);
            }
            catch (Exception ex)
            {
                Diag.Fail("sessionhistory.quarantine", ex);
            }
        }
    }
}
