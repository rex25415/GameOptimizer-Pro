// ============================================================================
//  BenchHistory.cs — 基准体检历史持久化（追加式、原子写、上限 50 次）
//  让一次性体检变成可积累、可回看、可导出的资产。读数全部来自真实采样。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using IOPath = System.IO.Path;

namespace Go
{
    public sealed class BenchRecord
    {
        private string _mode;

        public string Time = "";
        public double P50;
        public double P99;
        public double Max;
        public int Count;
        public double TimerMs;

        // Rev88 多模式微基准字段（兼容旧 bench-history.json）
        public string Mode
        {
            get { return string.IsNullOrEmpty(_mode) ? "wake" : _mode; }
            set { _mode = value; }
        }
        public double BandwidthMBs;
        public double MemLatNs;
        public string Note = "";
    }

    public static class BenchHistory
    {
        public const int Cap = 50;
        public static volatile string DirOverride;
        /// <summary>只读模式：--render 离屏渲染禁止 Quarantine 或覆盖写用户真实 bench-history.json。</summary>
        public static volatile bool ReadOnlyMode;
        private static readonly object _ioLock = new object();

        public static string FilePath()
        {
            string dir = DirOverride ?? Ledger.DirOverride ?? IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameOptimizer");
            return IOPath.Combine(dir, "bench-history.json");
        }

        public static void ResetDirOverride()
        {
            DirOverride = null;
        }

        public static List<BenchRecord> Load()
        {
            lock (_ioLock)
            {
                return LoadCore();
            }
        }

        private static List<BenchRecord> LoadCore()
        {
            try
            {
                string p = FilePath();
                if (!File.Exists(p)) return new List<BenchRecord>();
                JavaScriptSerializer js = new JavaScriptSerializer();
                List<BenchRecord> l = js.Deserialize<List<BenchRecord>>(File.ReadAllText(p, Encoding.UTF8));
                return l ?? new List<BenchRecord>();
            }
            catch (Exception ex)
            {
                if (ReadOnlyMode)
                {
                    Diag.Hit("bench.readonly.corrupt");
                    return new List<BenchRecord>();
                }
                // 读坏了一次就返回空表——而 Append 紧接着 Save 会把空表写回去，
                // 于是"文件损坏"变成"历史被静默清空"。这里先把坏文件挪走留证：
                // 之后即使覆盖，用户/支持人员手里仍有原始数据。
                try
                {
                    string p = FilePath();
                    if (File.Exists(p))
                    {
                        string stem = p + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        string dst = stem;
                        for (int i = 2; i < 100 && File.Exists(dst); i++) dst = stem + "-" + i;
                        File.Move(p, dst);
                    }
                }
                catch (Exception mx)
                {
                    Diag.Fail("bench.quarantine", mx);
                    try { Go.Tray.TrayTrace.Write("bench history quarantine: " + mx.Message + " / " + ex.Message); }
                    catch (Exception tx) { Diag.Fail("bench.quarantine.trace", tx); }
                }
                return new List<BenchRecord>();
            }
        }

        public static void Append(BenchRecord r)
        {
            if (r == null) return;
            if (ReadOnlyMode)
            {
                Diag.Hit("bench.readonly.skip");
                return;
            }
            try
            {
                lock (_ioLock)
                {
                    if (ReadOnlyMode) return;
                    List<BenchRecord> l = LoadCore();
                    l.Add(r);
                    while (l.Count > Cap) l.RemoveAt(0);
                    Save(l);
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("bench.append", ex);
                try { Go.Tray.TrayTrace.Write("bench history append: " + ex.Message); }
                catch (Exception tx) { Diag.Fail("bench.append.trace", tx); }
            }
        }

        private static void Save(List<BenchRecord> l)
        {
            if (ReadOnlyMode) return;
            string p = FilePath();
            string dir = IOPath.GetDirectoryName(p);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            JavaScriptSerializer js = new JavaScriptSerializer();
            string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, js.Serialize(l), new UTF8Encoding(false));
                if (File.Exists(p)) File.Replace(tmp, p, null);
                else File.Move(tmp, p);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception cx) { Diag.Fail("bench.tmpclean", cx); }
                throw;
            }
        }

        public static string ToCsv(List<BenchRecord> l)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("time,p50_us,p99_us,max_us,samples,timer_ms,mode,bandwidth_mbs,mem_lat_ns,note");
            if (l == null) return sb.ToString();
            for (int i = 0; i < l.Count; i++)
            {
                BenchRecord r = l[i];
                string note = (r.Note ?? "").Replace(',', ' ').Replace('\r', ' ').Replace('\n', ' ');
                sb.Append(r.Time).Append(',')
                  .Append(r.P50.ToString("0.##")).Append(',')
                  .Append(r.P99.ToString("0.##")).Append(',')
                  .Append(r.Max.ToString("0.##")).Append(',')
                  .Append(r.Count).Append(',')
                  .Append(r.TimerMs.ToString("0.##")).Append(',')
                  .Append(r.Mode).Append(',')
                  .Append(r.BandwidthMBs.ToString("0.##")).Append(',')
                  .Append(r.MemLatNs.ToString("0.##")).Append(',')
                  .Append(note).AppendLine();
            }
            return sb.ToString();
        }
    }
}