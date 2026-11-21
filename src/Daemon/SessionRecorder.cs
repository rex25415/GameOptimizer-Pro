// ============================================================================
//  SessionRecorder.cs — 一局游戏结束后的"体检战报"
//  只回答用户真正关心的三件事：
//    1) 这局里有没有可感知的卡顿？几次？最坏多少？
//    2) 谁在后台抢 CPU？（按整局累计 CPU 时间排名）
//    3) 我们的挂载到底有没有生效？
//  采样成本：每秒一次进程表遍历（纯非托管读）+ 一次二级字典更新，不构造字符串。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Go.Daemon
{
    public sealed class Culprit
    {
        public string Name;
        public double CpuPercent;     // 占单核百分比
        public double CpuSeconds;
        public int Pid;
    }

    public sealed class SessionReport
    {
        public string ProfileName;
        public string Started;
        public double DurationSec;
        public int Samples;
        public int JitterSamples;    // 真正采到抖动的拍数（窗口收起探针停采时为 0）
        public int StutterWindows;    // P99 超过阈值的秒数
        public double MaxJitterUs;
        public double AvgP99Us;
        public List<Culprit> Top = new List<Culprit>();

        public string DurationText
        {
            get
            {
                int s = (int)Math.Round(DurationSec);
                if (s < 60) return s + " 秒";
                int m = s / 60;
                return m + " 分 " + (s % 60) + " 秒";
            }
        }

        /// <summary>一句话结论，给 Toast 和卡片用。</summary>
        public string Verdict
        {
            get
            {
                if (Samples < 3) return "采样过短，无结论";
                // 探针整局没热跑过（窗口一直收起）：不能把"没测"说成"平稳"。
                if (JitterSamples == 0) return "后台占用已记录 · 抖动未采样（窗口收起时探针停采）";
                if (StutterWindows == 0) return "全程平稳，未捕获可感知卡顿";
                if (StutterWindows <= 2) return "捕获 " + StutterWindows + " 次轻微抖动";
                return "捕获 " + StutterWindows + " 次抖动，最坏 " + (MaxJitterUs / 1000).ToString("0.0") + " ms";
            }
        }
    }

    public sealed class SessionRecorder
    {
        private readonly Core _core;
        private readonly ProcessScan _scan = new ProcessScan();
        // _prev/_cur 每秒互换引用（真交换，不再逐条拷贝）；_accum 累计整局占用。
        private Dictionary<int, long> _prev = new Dictionary<int, long>(512);
        private Dictionary<int, long> _cur = new Dictionary<int, long>(512);
        private readonly Dictionary<int, long> _accum = new Dictionary<int, long>(512);
        // 每个 PID 的创建时间：用来识别"PID 被复用"，见 SampleCpu。
        private Dictionary<int, long> _bornPrev = new Dictionary<int, long>(512);
        private Dictionary<int, long> _bornCur = new Dictionary<int, long>(512);

        private bool _active;
        private long _startTicks;
        private long _lastSampleTicks;
        private int _samples;
        private int _jitterSamples;   // 其中真正采到抖动的拍数（探针冷时为 0）
        private int _stutters;
        private double _maxJitter;
        private double _sumP99;
        private string _profile;
        private string _profileId;
        private string _profileExe;
        private readonly List<int> _hotPids = new List<int>(16);
        private readonly List<long> _hotTicks = new List<long>(16);

        public SessionRecorder(Core core)
        {
            _core = core;
            PersistHistory = true;
        }

        public bool PersistHistory { get; set; }
        public bool Active { get { return _active; } }
        public SessionReport Last { get; private set; }
        public event Action<SessionReport> Finished;

        public void Begin(string profileName)
        {
            Begin(profileName, null, null);
        }

        public void Begin(GameProfile profile)
        {
            if (profile == null) { Begin("", null, null); return; }
            Begin(profile.Name, profile.Id, profile.Exe);
        }

        public void Begin(string profileName, string profileId, string exe)
        {
            _profile = profileName;
            _profileId = profileId;
            _profileExe = exe;
            _active = true;
            _startTicks = DateTime.Now.Ticks;
            _lastSampleTicks = Stopwatch.GetTimestamp();
            _samples = 0; _jitterSamples = 0; _stutters = 0; _maxJitter = 0; _sumP99 = 0;
            _prev.Clear(); _cur.Clear(); _accum.Clear();
            _bornPrev.Clear(); _bornCur.Clear();
            _hotPids.Clear(); _hotTicks.Clear();
            SampleCpu();                 // 建立基线
        }

        /// <summary>每秒调用一次（由守护巡检驱动）。</summary>
        public void Tick()
        {
            if (!_active) return;

            Snapshot s = _core.Perf.Current;
            // 抖动探针只在"有页面在看"时热跑；游戏全屏时窗口通常收起，探针是冷的，
            // JitterP99Us 是上一会话冻住的旧值。旧实现不看 JitterLive，若该值 >1500
            // 就每秒 _stutters++，整局战报变成"捕获 N 次抖动"（N=挂机秒数）——那是伪造读数。
            // 这里只统计真正采到的拍；CPU 占用采样不受影响，照常进行。
            if (s.JitterLive)
            {
                _jitterSamples++;
                double p99 = s.JitterP99Us;
                _sumP99 += p99;
                if (s.JitterMaxUs > _maxJitter) _maxJitter = s.JitterMaxUs;
                if (p99 > 1500) _stutters++;
            }

            if (SampleCpu()) _samples++;
        }

        private bool SampleCpu()
        {
            _cur.Clear();
            // born 字典和 CPU 字典必须同拍清空：旧实现只清 _cur，_bornCur 会一直累积
            // 每个曾经出现过的 PID，长局里两张字典只增不减，还会让"PID 复用"判定读到
            // 上一局的创建时间。
            _bornCur.Clear();
            _hotPids.Clear();
            _hotTicks.Clear();
            if (!_scan.SnapshotCpu(_cur, _bornCur)) return false;   // 扫描失败：跳过本拍差分，不误判全进程退出
            long nowTs = Stopwatch.GetTimestamp();
            double elapsedSec = Math.Max(0.25, (double)(nowTs - _lastSampleTicks) / Stopwatch.Frequency);
            _lastSampleTicks = nowTs;
            long singleCoreWindow100Ns = (long)(elapsedSec * 10000000.0);

            foreach (KeyValuePair<int, long> kv in _cur)
            {
                // PID 复用：同一个号但创建时间变了 = 换了进程。旧累计值属于上一个进程，
                // 必须清零，否则新进程会继承它的账，战报把无关进程列成头号占用。
                long born, bornOld;
                if (_bornCur.TryGetValue(kv.Key, out born) && _bornPrev.TryGetValue(kv.Key, out bornOld)
                    && born != bornOld)
                {
                    _accum.Remove(kv.Key);
                    Diag.Hit("recorder.pid.reuse");
                }

                long before;
                if (!_prev.TryGetValue(kv.Key, out before)) continue;
                long delta = kv.Value - before;
                if (delta <= 0) continue;
                long acc;
                _accum.TryGetValue(kv.Key, out acc);
                _accum[kv.Key] = acc + delta;

                if (singleCoreWindow100Ns > 0 && delta * 100L >= singleCoreWindow100Ns * 3L)
                {
                    _hotPids.Add(kv.Key);
                    _hotTicks.Add(delta * 100L / singleCoreWindow100Ns);
                }
            }
            // 清掉本拍已不存在的 PID：长局不再累积死进程，也避免 PID 复用把新进程算进旧账。
            if (_accum.Count > 0)
            {
                List<int> dead = null;
                foreach (KeyValuePair<int, long> kv in _accum)
                    if (!_cur.ContainsKey(kv.Key)) { if (dead == null) dead = new List<int>(); dead.Add(kv.Key); }
                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++) { _accum.Remove(dead[i]); _bornPrev.Remove(dead[i]); }
                }
            }
            // 真交换缓冲：互换引用即可，零拷贝零分配（旧实现 Clear+逐条拷贝约 500 次插入/秒）。
            Dictionary<int, long> tmp = _prev;
            _prev = _cur;
            _cur = tmp;
            Dictionary<int, long> tmpBorn = _bornPrev;
            _bornPrev = _bornCur;
            _bornCur = tmpBorn;
            return true;
        }

        public SessionReport End()
        {
            if (!_active) return Last;
            _active = false;

            SessionReport r = new SessionReport();
            r.ProfileName = _profile;
            r.Started = new DateTime(_startTicks).ToString("HH:mm");
            r.DurationSec = (DateTime.Now.Ticks - _startTicks) / 10000000.0;
            r.Samples = _samples;
            r.JitterSamples = _jitterSamples;
            r.StutterWindows = _stutters;
            r.MaxJitterUs = _maxJitter;
            r.AvgP99Us = _jitterSamples > 0 ? _sumP99 / _jitterSamples : 0;

            // 只对前 8 名解析进程名（其余不值得一次 Process 打开）
            List<KeyValuePair<int, long>> ranked = new List<KeyValuePair<int, long>>(_accum);
            ranked.Sort(delegate (KeyValuePair<int, long> a, KeyValuePair<int, long> b)
            {
                return b.Value.CompareTo(a.Value);
            });
            double secs = Math.Max(0.5, r.DurationSec);
            int show = Math.Min(8, ranked.Count);
            int self = SafePid();
            for (int i = 0; i < show; i++)
            {
                int pid = ranked[i].Key;
                if (pid == self) continue;
                Culprit c = new Culprit();
                c.Pid = pid;
                c.CpuSeconds = ranked[i].Value / 10000000.0;
                c.CpuPercent = c.CpuSeconds / secs * 100.0;
                c.Name = NameOf(pid);
                if (c.Name == null) continue;
                r.Top.Add(c);
            }

            Last = r;
            _core.Engine.Ledger.AppendAudit("SESSION " + r.ProfileName + " " + r.DurationText
                + " stutter=" + r.StutterWindows + " max=" + (int)r.MaxJitterUs + "us"
                + " top=" + (r.Top.Count > 0 ? r.Top[0].Name + " " + r.Top[0].CpuPercent.ToString("0.0") + "%" : "-"));

            if (PersistHistory)
            {
                if (_core.Profiles != null && (!string.IsNullOrEmpty(_profileId) || !string.IsNullOrEmpty(_profileExe)))
                {
                    bool saveProfiles = !string.IsNullOrEmpty(ProfileStore.DirOverride) || !string.IsNullOrEmpty(Ledger.DirOverride)
                        || _core.Profiles.FindById(_profileId) != null || _core.Profiles.FindByExe(_profileExe) != null;
                    if (saveProfiles) _core.Profiles.RecordSession(_profileId, _exeOrNull(_profileExe), r, true);
                }
                if (!string.IsNullOrEmpty(SessionHistory.DirOverride) || !string.IsNullOrEmpty(Ledger.DirOverride)
                    || (_core.Profiles != null && (_core.Profiles.FindById(_profileId) != null || _core.Profiles.FindByExe(_profileExe) != null)))
                {
                    int ecoCount = (_core.Daemon != null && _core.Daemon.Pilot != null) ? _core.Daemon.Pilot.EcoThrottledCount : 0;
                    SessionHistory.Append(r, _profileId, _profileExe, ecoCount);
                }
            }

            Action<SessionReport> h = Finished;
            // 战报订阅者失败不能让 End() 半途而废（账本已写）；但战报会因此不显示。
            if (h != null) { try { h(r); } catch (Exception ex) { Diag.Fail("session.finished", ex); } }
            return r;
        }

        private static string _exeOrNull(string exe) { return string.IsNullOrEmpty(exe) ? null : exe; }

        public List<int> CollectRecentHighCpuPids(double minSingleCorePct)
        {
            List<int> list = new List<int>();
            long threshold = (long)Math.Max(1, Math.Round(minSingleCorePct));
            for (int i = 0; i < _hotPids.Count; i++)
            {
                if (_hotTicks[i] >= threshold) list.Add(_hotPids[i]);
            }
            return list;
        }

        private static int SafePid()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch (Exception ex) { Diag.Fail("sessionrecorder.selfpid", ex); return 0; }
        }

        private static string NameOf(int pid)
        {
            try
            {
                string n = ProcName.Of(pid);
                if (n.Length > 0) return n;
                string scanned = ProcessScan.LookupNameByPid(pid);
                return string.IsNullOrEmpty(scanned) ? null : scanned;
            }
            catch (Exception ex) { Diag.Fail("sessionrecorder.nameof", ex); return null; }
        }

        /// <summary>测试钩子：直接注入一次采样统计。</summary>
        public void InjectForTest(int samples, int stutters, double maxJitter)
        {
            _samples = samples; _jitterSamples = samples; _stutters = stutters; _maxJitter = maxJitter;
        }

        /// <summary>释放进程表缓冲（守护宿主关停时调用）。</summary>
        public void Dispose()
        {
            _scan.Dispose();
        }
    }
}
