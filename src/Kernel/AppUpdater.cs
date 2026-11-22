// ============================================================================
//  AppUpdater.cs — 手动检查更新、SHA-256 校验热更新与脱敏环境摘要（Rev 143）
//
//  安全与克制底线：
//    1) 默认零联网：绝不在启动或后台偷跑任何网络请求，仅在用户主动点击「检查更新」时访问一次 GitHub API；
//    2) 严格域名白名单：仅允许从本仓库官方 GitHub Release / GitHub CDN 域名下载；
//    3) 强校验防篡改：先读 SHA256SUMS.txt，再核对 PE 头 (MZ)、体积范围与完整 SHA-256 哈希，不匹配立即中止删除；
//    4) 无感热替换：沿用 Windows FILE_SHARE_DELETE 原子重命名热替换 + --wait-pid 交接重启，零残留。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Go
{
    public static class AppUpdater
    {
        public const string RepoOwner = "rex25415";
        public const string RepoName = "GameOptimizer-Pro";
        public const string RepoHomeUrl = "https://github.com/rex25415/GameOptimizer-Pro";
        public const string IssuesNewUrl = "https://github.com/rex25415/GameOptimizer-Pro/issues/new/choose";
        public const string ReleasesLatestApi = "https://api.github.com/repos/rex25415/GameOptimizer-Pro/releases/latest";
        public const string PreferredAssetName = "GameOptimizer-Pro-win-x64.exe";

        private const long MinExeBytes = 128L * 1024L;
        private const long MaxExeBytes = 32L * 1024L * 1024L;

        public sealed class UpdateCheckResult
        {
            public bool Ok;
            public int CurrentRev;
            public int RemoteRev;
            public bool HasUpdate;
            public string ReleaseTitle;
            public string TagName;
            public string AssetName;
            public string DownloadUrl;
            public string Sha256Url;
            public string ReleasePageUrl;
            public string PublishedAt;
            public string Error;
        }

        public sealed class UpdateApplyResult
        {
            public bool Ok;
            public string TargetExe;
            public string VerifiedSha256;
            public bool ShaVerified;
            public string Error;
        }

        private static void EnsureTls12()
        {
            try
            {
                // .NET Framework 4.8 上显式确保 TLS 1.2 (3072) 与 TLS 1.3 (12288) 可用
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch { }
        }

        public static bool IsTrustedUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (!string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
            string host = u.Host == null ? "" : u.Host.ToLowerInvariant();
            if (host == "github.com")
                return u.AbsolutePath.StartsWith("/" + RepoOwner + "/" + RepoName + "/", StringComparison.OrdinalIgnoreCase);
            if (host == "api.github.com")
                return u.AbsolutePath.StartsWith("/repos/" + RepoOwner + "/" + RepoName + "/", StringComparison.OrdinalIgnoreCase);
            if (host == "objects.githubusercontent.com" || host == "release-assets.githubusercontent.com")
                return true;
            return false;
        }

        /// <summary>从 Release 标题 / 标签 / 正文中提取修订号（如 "Rev 143" -> 143，"v1.43.0" -> 143）。</summary>
        public static int ParseRevNumber(string title, string tag, string body)
        {
            int rev = ExtractRevToken(title);
            if (rev > 0) return rev;
            rev = ExtractRevToken(tag);
            if (rev > 0) return rev;
            rev = ExtractRevToken(body);
            if (rev > 0) return rev;
            rev = ExtractSemverRev(tag);
            if (rev > 0) return rev;
            return ExtractSemverRev(title);
        }

        private static int ExtractRevToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            string low = s.ToLowerInvariant();
            int pos = 0;
            while (pos < low.Length)
            {
                int idx = low.IndexOf("rev", pos, StringComparison.Ordinal);
                if (idx < 0) break;
                int p = idx + 3;
                while (p < low.Length && (low[p] == ' ' || low[p] == '.' || low[p] == '-' || low[p] == ':')) p++;
                int start = p;
                while (p < low.Length && low[p] >= '0' && low[p] <= '9') p++;
                if (p > start)
                {
                    int val;
                    if (int.TryParse(low.Substring(start, p - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out val) && val >= 60 && val < 10000)
                        return val;
                }
                pos = idx + 3;
            }
            return 0;
        }

        private static int ExtractSemverRev(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != 'v' && s[i] != 'V') continue;
                int p = i + 1;
                int major = ReadDigits(s, ref p);
                if (major != 1 || p >= s.Length || s[p] != '.') continue;
                p++;
                int minor = ReadDigits(s, ref p);
                if (minor >= 10 && minor < 1000) return 100 + minor;
            }
            return 0;
        }

        private static int ReadDigits(string s, ref int p)
        {
            int start = p;
            while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++;
            if (p == start) return -1;
            int v;
            return int.TryParse(s.Substring(start, p - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        /// <summary>解析 SHA256SUMS.txt 中指定文件（或首个 .exe）对应的 64 位小写 SHA-256。</summary>
        public static string ParseExpectedSha256(string sumsText, string assetName)
        {
            if (string.IsNullOrEmpty(sumsText)) return "";
            string firstExe = "";
            string[] lines = sumsText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length < 66) continue;
                string hash = line.Substring(0, 64).ToLowerInvariant();
                if (!IsHex64(hash)) continue;
                string file = line.Substring(64).Trim().TrimStart('*').Trim();
                if (!string.IsNullOrEmpty(assetName) && string.Equals(file, assetName, StringComparison.OrdinalIgnoreCase))
                    return hash;
                if (firstExe.Length == 0 && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    firstExe = hash;
            }
            return firstExe;
        }

        private static bool IsHex64(string s)
        {
            if (s == null || s.Length != 64) return false;
            for (int i = 0; i < 64; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>纯内存解析 GitHub Release JSON（可离线单测）。</summary>
        public static UpdateCheckResult ParseReleaseJson(string json, int currentRev)
        {
            UpdateCheckResult res = new UpdateCheckResult();
            res.CurrentRev = currentRev;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> obj = js.Deserialize<Dictionary<string, object>>(json);
                if (obj == null) { res.Error = "远端返回格式无效"; return res; }

                res.TagName = Str(obj, "tag_name");
                res.ReleaseTitle = Str(obj, "name");
                res.ReleasePageUrl = Str(obj, "html_url");
                res.PublishedAt = Str(obj, "published_at");
                string body = Str(obj, "body");
                res.RemoteRev = ParseRevNumber(res.ReleaseTitle, res.TagName, body);

                object assetsObj;
                if (obj.TryGetValue("assets", out assetsObj))
                {
                    System.Collections.IList arr = assetsObj as System.Collections.IList;
                    if (arr != null)
                    {
                        string fallbackName = "", fallbackUrl = "";
                        for (int i = 0; i < arr.Count; i++)
                        {
                            Dictionary<string, object> a = arr[i] as Dictionary<string, object>;
                            if (a == null) continue;
                            string nm = Str(a, "name");
                            string dl = Str(a, "browser_download_url");
                            if (string.Equals(nm, PreferredAssetName, StringComparison.OrdinalIgnoreCase))
                            {
                                res.AssetName = nm;
                                res.DownloadUrl = dl;
                            }
                            else if (nm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && fallbackUrl.Length == 0)
                            {
                                fallbackName = nm;
                                fallbackUrl = dl;
                            }
                            else if (string.Equals(nm, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                            {
                                res.Sha256Url = dl;
                            }
                        }
                        if (string.IsNullOrEmpty(res.DownloadUrl) && fallbackUrl.Length > 0)
                        {
                            res.AssetName = fallbackName;
                            res.DownloadUrl = fallbackUrl;
                        }
                    }
                }

                if (res.RemoteRev <= 0)
                {
                    res.Error = "未能识别远端版本号";
                    return res;
                }
                if (!string.IsNullOrEmpty(res.DownloadUrl) && !IsTrustedUrl(res.DownloadUrl))
                {
                    res.Error = "远端下载地址未通过官方域名校验";
                    return res;
                }

                res.HasUpdate = res.RemoteRev > currentRev && !string.IsNullOrEmpty(res.DownloadUrl);
                res.Ok = true;
                return res;
            }
            catch (Exception ex)
            {
                Diag.Fail("updater.parse", ex);
                res.Error = ex.Message;
                return res;
            }
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v) && v != null) ? v.ToString() : "";
        }

        /// <summary>用户主动点击「检查更新」时触发一次 GitHub Releases API 查询。</summary>
        public static UpdateCheckResult CheckForUpdate()
        {
            EnsureTls12();
            try
            {
                string json = HttpGetText(ReleasesLatestApi, 10000);
                return ParseReleaseJson(json, Brand.Rev);
            }
            catch (WebException wex)
            {
                UpdateCheckResult r = new UpdateCheckResult { CurrentRev = Brand.Rev };
                HttpWebResponse resp = wex.Response as HttpWebResponse;
                if (resp != null && (int)resp.StatusCode == 403)
                    r.Error = "GitHub API 访问频率受限，请稍后再试或直接打开 Releases 页面";
                else
                    r.Error = "连接 GitHub 失败：" + wex.Message;
                return r;
            }
            catch (Exception ex)
            {
                Diag.Fail("updater.check", ex);
                return new UpdateCheckResult { CurrentRev = Brand.Rev, Error = "检查更新失败：" + ex.Message };
            }
        }

        /// <summary>下载新版 exe → 校验 MZ 头与 SHA-256 → 原子重命名热替换目标 exe。</summary>
        public static UpdateApplyResult DownloadAndApply(UpdateCheckResult info, string targetExe, Action<int> onPercent)
        {
            UpdateApplyResult res = new UpdateApplyResult { TargetExe = targetExe };
            if (info == null || !info.Ok || string.IsNullOrEmpty(info.DownloadUrl))
            {
                res.Error = "没有可用的更新下载地址";
                return res;
            }
            if (!IsTrustedUrl(info.DownloadUrl))
            {
                res.Error = "下载地址域名不受信任";
                return res;
            }
            if (string.IsNullOrEmpty(targetExe))
            {
                res.Error = "找不到当前可执行文件路径";
                return res;
            }

            EnsureTls12();
            string tmpPath = Path.Combine(Path.GetTempPath(), "GameOptimizer-update-r" + info.RemoteRev + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                string expectedSha = "";
                if (!string.IsNullOrEmpty(info.Sha256Url) && IsTrustedUrl(info.Sha256Url))
                {
                    try
                    {
                        string sums = HttpGetText(info.Sha256Url, 10000);
                        expectedSha = ParseExpectedSha256(sums, info.AssetName);
                    }
                    catch { /* 若校验清单拉取超时，仍继续核对长度与 PE 头 */ }
                }

                HttpDownloadFile(info.DownloadUrl, tmpPath, 30000, onPercent);
                return ApplyStagedFile(tmpPath, targetExe, expectedSha);
            }
            catch (Exception ex)
            {
                Diag.Fail("updater.download", ex);
                res.Error = "下载更新失败：" + ex.Message;
                return res;
            }
            finally
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                catch { }
            }
        }

        /// <summary>校验已落盘临时文件的 PE 头、体积与 SHA-256，并以原子热替换写入目标 exe（可离线单测）。</summary>
        public static UpdateApplyResult ApplyStagedFile(string stagedFile, string targetExe, string expectedSha256)
        {
            UpdateApplyResult res = new UpdateApplyResult { TargetExe = targetExe };
            try
            {
                if (string.IsNullOrEmpty(stagedFile) || !File.Exists(stagedFile))
                {
                    res.Error = "临时更新文件不存在";
                    return res;
                }
                FileInfo fi = new FileInfo(stagedFile);
                if (fi.Length < MinExeBytes || fi.Length > MaxExeBytes)
                {
                    res.Error = "下载文件大小异常（" + fi.Length + " B），已中止替换";
                    return res;
                }
                byte[] magic = new byte[2];
                using (FileStream fs = File.OpenRead(stagedFile))
                {
                    if (fs.Read(magic, 0, 2) < 2 || magic[0] != (byte)'M' || magic[1] != (byte)'Z')
                    {
                        res.Error = "下载文件不是有效的 Windows 可执行文件（缺少 MZ 头），已中止替换";
                        return res;
                    }
                }

                string actualSha = ComputeSha256(stagedFile);
                res.VerifiedSha256 = actualSha;
                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    if (!string.Equals(actualSha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        res.Error = "SHA-256 哈希校验不匹配，已拦截替换以保障安全";
                        return res;
                    }
                    res.ShaVerified = true;
                }

                string dir = Path.GetDirectoryName(targetExe);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(targetExe))
                {
                    string prev = targetExe + ".prev." + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Move(targetExe, prev);
                    try
                    {
                        File.Copy(stagedFile, targetExe, true);
                    }
                    catch
                    {
                        // 写入失败时立刻把旧程序移回原位，确保程序不损坏
                        try { if (!File.Exists(targetExe) && File.Exists(prev)) File.Move(prev, targetExe); }
                        catch { }
                        throw;
                    }
                }
                else
                {
                    File.Copy(stagedFile, targetExe, true);
                }

                res.Ok = true;
                return res;
            }
            catch (Exception ex)
            {
                Diag.Fail("updater.apply", ex);
                res.Error = ex.Message;
                return res;
            }
        }

        public static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static string HttpGetText(string url, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "GameOptimizer-Pro/" + Brand.Rev;
            req.Accept = "application/vnd.github+json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream st = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static void HttpDownloadFile(string url, string dstPath, int timeoutMs, Action<int> onPercent)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "GameOptimizer-Pro/" + Brand.Rev;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                if (!IsTrustedUrl(resp.ResponseUri.AbsoluteUri))
                    throw new InvalidOperationException("重定向目标不在受信任域名列表中");
                long total = resp.ContentLength;
                if (total > MaxExeBytes)
                    throw new InvalidOperationException("远端文件超过安全体积上限");
                using (Stream src = resp.GetResponseStream())
                using (FileStream dst = File.Create(dstPath))
                {
                    byte[] buf = new byte[32768];
                    long readTotal = 0;
                    int lastPct = -1;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        readTotal += n;
                        if (readTotal > MaxExeBytes)
                            throw new InvalidOperationException("下载数据超过安全体积上限");
                        dst.Write(buf, 0, n);
                        if (onPercent != null && total > 0)
                        {
                            int pct = (int)((readTotal * 100L) / total);
                            if (pct != lastPct) { lastPct = pct; onPercent(pct); }
                        }
                    }
                }
            }
        }

        // ---- 脱敏环境摘要（用于提交 GitHub Issue，绝不含用户名/路径/IP/进程） ----

        public static string BuildSanitizedSummary(Core core)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.AppendLine("App: GameOptimizer Pro Rev " + Brand.Rev + " (" + Brand.RevStamp + ", " + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")");
            sb.AppendLine("OS: " + ReadWindowsEdition());
            if (core != null)
            {
                sb.AppendLine("Privilege: " + (core.Elevated ? "Administrator" : "Standard User"));
                Topology t = core.Topo;
                sb.AppendLine("CPU: " + SafeStr(core.CpuName) + " (" + t.Cores + "C/" + t.Logical + "T, " + SafeStr(t.CcdLabel) + ")");
                sb.AppendLine("GPU: " + SafeStr(core.GpuName));
                if (core.Displays != null && core.Displays.Count > 0)
                {
                    StringBuilder ds = new StringBuilder();
                    for (int i = 0; i < core.Displays.Count; i++)
                    {
                        Hardware.DisplayInfo d = core.Displays[i];
                        if (i > 0) ds.Append(" + ");
                        ds.Append(d.Width).Append('x').Append(d.Height);
                        if (d.RefreshHz > 0) ds.Append('@').Append(d.RefreshHz).Append("Hz");
                        if (d.Main) ds.Append("(Main)");
                    }
                    sb.AppendLine("Displays: " + ds.ToString());
                }
                if (core.Engine != null)
                    sb.AppendLine("Ledger Pending: " + core.Engine.PendingCount);
            }
            return sb.ToString().TrimEnd();
        }

        private static string ReadWindowsEdition()
        {
            try
            {
                const string p = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                RegValue prod = Reg.Read("HKLM", p, "ProductName");
                RegValue disp = Reg.Read("HKLM", p, "DisplayVersion");
                RegValue bld = Reg.Read("HKLM", p, "CurrentBuildNumber");
                RegValue ubr = Reg.Read("HKLM", p, "UBR");

                string name = prod.Existed && prod.Value != null ? prod.Value.ToString() : "Windows";
                int buildNum = 0;
                if (bld.Existed && bld.Value != null)
                    int.TryParse(bld.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out buildNum);
                if (buildNum >= 22000 && name.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0)
                    name = name.Replace("Windows 10", "Windows 11");

                StringBuilder sb = new StringBuilder(name);
                if (disp.Existed && disp.Value != null && disp.Value.ToString().Length > 0)
                    sb.Append(' ').Append(disp.Value.ToString());
                if (buildNum > 0)
                {
                    sb.Append(" (Build ").Append(buildNum);
                    if (ubr.Existed && ubr.Value != null) sb.Append('.').Append(ubr.Value.ToString());
                    sb.Append(')');
                }
                return sb.ToString();
            }
            catch
            {
                return Environment.OSVersion.VersionString;
            }
        }

        private static string SafeStr(string s) { return string.IsNullOrEmpty(s) ? "-" : s.Trim(); }

        public static void OpenUrl(string url)
        {
            if (!IsTrustedUrl(url)) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Diag.Fail("updater.openurl", ex); }
        }
    }
}
