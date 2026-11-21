// ============================================================================
//  Benchmark.Modes.cs — 多模式微基准（内存带宽延迟 / 线程乒乓）与 A/B 基线对照
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Benchmark
    {
        private static int ModeIndex(string mode)
        {
            if (mode == "mem") return 1;
            if (mode == "pingpong") return 2;
            return 0;
        }

        private static bool HigherIsBetter(string mode)
        {
            return mode == "mem";
        }

        private static double PrimaryValue(RunData r, string mode)
        {
            if (r == null) return 0;
            return mode == "mem" ? r.BandwidthMBs : r.P99;
        }

        private static string FormatPrimary(double v, string mode)
        {
            if (v <= 0) return "\u2014";
            return mode == "pingpong" ? v.ToString("#,##0.0") : v.ToString("#,##0");
        }

        private List<RunData> ModeRuns(string mode)
        {
            List<RunData> list = new List<RunData>();
            for (int i = 0; i < _runs.Count; i++)
                if (string.Equals(_runs[i].Mode, mode, StringComparison.Ordinal)) list.Add(_runs[i]);
            return list;
        }

        private RunData ActiveRunForMode(List<RunData> same)
        {
            if (_curRun != null && string.Equals(_curRun.Mode, _mode, StringComparison.Ordinal)) return _curRun;
            return same.Count > 0 ? same[same.Count - 1] : null;
        }

        private RunData CompareBaseForMode(List<RunData> same, RunData cur)
        {
            if (_baseRun != null && _baseRun.Mode == _mode && _baseRun != cur) return _baseRun;
            if (cur == null) return null;
            for (int i = same.Count - 1; i >= 0; i--)
                if (same[i] == cur) return i > 0 ? same[i - 1] : null;
            return null;
        }

        private int IndexInMode(List<RunData> same, RunData target)
        {
            for (int i = 0; i < same.Count; i++)
                if (same[i] == target) return i + 1;
            return 0;
        }

        private void ResetPaintCaches()
        {
            _heroP99 = int.MinValue;
            _stP1Shown = int.MinValue;
            _stP50Shown = int.MinValue;
            _stSigShown = int.MinValue;
            _tagShown = -1;
            _footCount = int.MinValue;
            _footP95 = int.MinValue;
            _footMax = int.MinValue;
            _footFar = int.MinValue;
            _deltaKey = long.MinValue;
            _clockKey = int.MinValue;
        }

        private void SwitchMode(string next)
        {
            if (string.IsNullOrEmpty(next) || next == _mode) return;
            if (_phase == Phase.Running)
            {
                if (_modeTabs != null) _modeTabs.Select(ModeIndex(_mode), false);
                if (C != null && C.Shell != null) C.Shell.Toast("\u6D4B\u8BD5\u8FDB\u884C\u4E2D\uFF0C\u8BF7\u5148\u7B49\u5F85\u5B8C\u6210\u6216\u4E2D\u6B62", false);
                return;
            }
            Interlocked.Increment(ref _bgRunToken);
            _mode = next;
            _phase = Phase.Idle;
            _sel = -1;
            _baseRun = null;

            List<RunData> same = ModeRuns(_mode);
            _curRun = same.Count > 0 ? same[same.Count - 1] : null;
            _chartRun = _mode == "wake" ? LastWithSamples() : _curRun;
            ResetPaintCaches();

            UpdateModeChrome();
            UpdateRunButton();
            RefreshHistory();
            PaintAll();
        }

        private void UpdateModeChrome()
        {
            bool wake = _mode == "wake";
            bool mem = _mode == "mem";

            if (_histBed != null) _histBed.Visibility = wake ? Visibility.Visible : Visibility.Collapsed;
            if (_modeVizHost != null) _modeVizHost.Visibility = wake ? Visibility.Collapsed : Visibility.Visible;

            if (_heroUnitRun != null) _heroUnitRun.Text = mem ? " MB/s" : " \u00B5s";
            if (_hdrUnit != null) _hdrUnit.Text = mem ? " MB/s" : " \u00B5s";

            if (_chartTitle != null)
                _chartTitle.Text = wake ? "\u5524\u9192\u8D85\u8C03\u5206\u5E03" : (mem ? "\u5185\u5B58\u5E26\u5BBD\u4E0E\u5EF6\u8FDF\u5BF9\u7167" : "\u8C03\u5EA6\u4E52\u4E53\u5F80\u8FD4\u5BF9\u7167");
            if (_chartSub != null)
                _chartSub.Text = wake ? "WAKE OVERSHOOT / MICROSECONDS \u00B7 48 BINS"
                    : (mem ? "SEQUENTIAL COPY & POINTER CHASE / A-B TREND" : "CROSS-THREAD PING-PONG / MICROSECONDS \u00B7 A-B TREND");
            if (_footMeta != null)
                _footMeta.Text = wake
                    ? "\u5524\u9192\u8D85\u8C03 = Thread.Sleep(1) \u5B9E\u9645\u5524\u9192 \u2212 1 ms \u00B7 \u7A97\u53E3 8.0 S \u00B7 48 \u7BB1"
                    : (mem ? "\u987A\u5E8F\u62F7\u8D1D\u541E\u5410 (MB/s) \u4E0E\u8DE8\u6B65\u968F\u673A\u6307\u9488\u8FFD\u9010\u5EF6\u8FDF (ns)"
                           : "\u53CC\u7EBF\u7A0B AutoResetEvent \u5524\u9192\u5F80\u8FD4\u5EF6\u8FDF P50 / P99 / Max (\u00B5s)");

            if (_stLbl != null && _stLbl.Length == 3)
            {
                if (wake)
                {
                    SetStatMeta(0, "P1", "\u00B5s", "\u6700\u5FEB\u7684\u4E00\u6B21 \u00B7 \u672C\u6B21\u7A97\u53E3\u7684\u5730\u677F");
                    SetStatMeta(1, "P50", "\u00B5s", "\u4E2D\u4F4D \u00B7 \u5178\u578B\u4E00\u6B21\u5524\u9192");
                    SetStatMeta(2, "JITTER", "\u00B5s", "\u6807\u51C6\u5DEE \u03C3 \u00B7 \u8D8A\u5927\u8D8A\u4E0D\u7A33");
                }
                else if (mem)
                {
                    SetStatMeta(0, "LATENCY", "ns", "\u968F\u673A\u6307\u9488\u8FFD\u9010\u5EF6\u8FDF \u00B7 \u8D8A\u4F4E\u8D8A\u5FEB");
                    SetStatMeta(1, "COPY BUF", "MB", "\u987A\u5E8F\u62F7\u8D1D\u7F13\u51B2\u533A\u5BB9\u91CF");
                    SetStatMeta(2, "STEPS", "\u6B65", "\u6307\u9488\u8FFD\u9010\u91C7\u6837\u6B65\u6570");
                }
                else
                {
                    SetStatMeta(0, "P50", "\u00B5s", "\u5F80\u8FD4\u4E2D\u4F4D\u5EF6\u8FDF");
                    SetStatMeta(1, "ONE-WAY", "\u00B5s", "\u4F30\u7B97\u5355\u7A0B\u5524\u9192 (P50 / 2)");
                    SetStatMeta(2, "MAX", "\u00B5s", "\u672C\u8F6E\u6700\u574F\u5F80\u8FD4\u5C3E\u5EF6\u8FDF");
                }
            }

            UpdateVerdictModeChrome();
            UpdateHistoryModeChrome();
        }

        private void SetStatMeta(int i, string label, string unit, string note)
        {
            if (_stLbl[i] != null) _stLbl[i].Text = Ui.Spaced(label);
            if (_stUnit[i] != null) _stUnit[i].Text = unit;
            if (_stNote[i] != null) _stNote[i].Text = note;
        }

        // ---- 后台执行内存 / 乒乓微基准 ---------------------------------------
        private void StartModeBench(string targetMode)
        {
            if (C == null || _phase == Phase.Running) return;

            int token = Interlocked.Increment(ref _bgRunToken);
            _cur = null;
            _curRun = null;
            _chartRun = null;
            _sel = -1;
            _sw.Reset();
            _sw.Start();
            _phase = Phase.Running;
            ResetPaintCaches();

            UpdateRunButton();
            PaintAll();
            RefreshHistory();

            EnsurePoll();
            _poll.Start();

            Dispatcher ui = Dispatcher.CurrentDispatcher;
            Thread th = new Thread(delegate ()
            {
                MemoryBenchResult mr = null;
                PingPongResult pp = null;
                string err = "";
                try
                {
                    if (targetMode == "mem") mr = BenchSuite.RunMemoryBench();
                    else pp = BenchSuite.RunThreadPingPong();
                }
                catch (Exception ex)
                {
                    Diag.Fail("bench." + targetMode, ex);
                    err = ex.Message;
                }
                if (token != Thread.VolatileRead(ref _bgRunToken)) return;
                try
                {
                    if (!ui.HasShutdownStarted && !ui.HasShutdownFinished)
                        ui.BeginInvoke(new Action(delegate { OnModeBenchDone(token, targetMode, mr, pp, err); }));
                }
                catch (Exception ex) { Diag.Fail("bench.marshal", ex); }
            });
            th.IsBackground = true;
            th.Priority = ThreadPriority.Normal;
            th.Name = "go.bench." + targetMode;
            th.Start();
        }

        private void OnModeBenchDone(int token, string modeSnap, MemoryBenchResult mr, PingPongResult pp, string err)
        {
            if (token != Thread.VolatileRead(ref _bgRunToken) || _phase != Phase.Running || !string.Equals(_mode, modeSnap, StringComparison.Ordinal)) return;
            if (_poll != null) _poll.Stop();
            _sw.Stop();

            bool ok = string.IsNullOrEmpty(err) && (modeSnap == "mem"
                ? (mr != null && mr.SeqCopyMBs > 0)
                : (pp != null && pp.Count > 0 && pp.P99Us > 0));
            if (!ok)
            {
                _phase = Phase.Failed;
                _curRun = LastRunOfMode(modeSnap);
                _chartRun = _curRun;
                ResetPaintCaches();
                UpdateRunButton();
                PaintAll();
                RefreshHistory();
                if (C != null && C.Shell != null)
                    C.Shell.Toast("\u6D4B\u8BD5\u672A\u5F97\u5230\u6709\u6548\u6837\u672C" + (err.Length > 0 ? ("\uFF1A" + err) : ""), false);
                return;
            }

            BenchRecord rec = modeSnap == "mem" ? BenchSuite.ToBenchRecord(mr) : BenchSuite.ToBenchRecord(pp);
            rec.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            rec.TimerMs = C != null ? C.Perf.Current.TimerMs : 0;
            BenchHistory.Append(rec);

            RunData r = FromRecord(rec);
            r.MemRes = mr;
            r.PpRes = pp;
            _runs.Add(r);

            _curRun = r;
            _chartRun = r;
            _phase = Phase.Done;
            _sel = -1;
            ResetPaintCaches();

            UpdateRunButton();
            PaintAll();
            RefreshHistory();

            if (C != null && C.Shell != null)
            {
                string msg = modeSnap == "mem"
                    ? ("\u5185\u5B58\u6D4B\u901F\u5B8C\u6210 \u00B7 " + mr.SeqCopyMBs.ToString("#,##0") + " MB/s \u00B7 \u5EF6\u8FDF " + mr.MemLatNs.ToString("0.0") + " ns")
                    : ("\u7EBF\u7A0B\u4E52\u4E53\u5B8C\u6210 \u00B7 P99 " + pp.P99Us.ToString("0.0") + " \u00B5s \u00B7 " + pp.Count.ToString("#,##0") + " \u8F6E");
                C.Shell.Toast(msg, true);
            }
        }

        private RunData LastRunOfMode(string mode)
        {
            for (int i = _runs.Count - 1; i >= 0; i--)
                if (string.Equals(_runs[i].Mode, mode, StringComparison.Ordinal)) return _runs[i];
            return null;
        }

        private void LoadRuns()
        {
            List<RunData> prevRuns = new List<RunData>(_runs);
            RunData prevCur = _curRun;
            RunData prevChart = _chartRun;
            RunData prevBase = _baseRun;

            _runs.Clear();
            List<BenchRecord> l = BenchHistory.Load();
            bool[] used = new bool[prevRuns.Count];
            RunData newCur = null;
            RunData newChart = null;
            RunData newBase = null;

            for (int i = 0; i < l.Count; i++)
            {
                RunData r = FromRecord(l[i]);
                int match = FindMatchingPrevRun(r, i, prevRuns, used, prevCur, prevChart, prevBase);
                if (match >= 0)
                {
                    used[match] = true;
                    RunData old = prevRuns[match];
                    r.Sorted = old.Sorted;
                    r.St = old.St;
                    r.MemRes = old.MemRes;
                    r.PpRes = old.PpRes;
                    if (object.ReferenceEquals(old, prevCur)) newCur = r;
                    if (object.ReferenceEquals(old, prevChart)) newChart = r;
                    if (object.ReferenceEquals(old, prevBase)) newBase = r;
                }
                _runs.Add(r);
            }

            for (int j = 0; j < prevRuns.Count; j++)
            {
                if (used[j]) continue;
                RunData old = prevRuns[j];
                if (!HasSessionDetail(old)) continue;
                _runs.Add(old);
                if (object.ReferenceEquals(old, prevCur)) newCur = old;
                if (object.ReferenceEquals(old, prevChart)) newChart = old;
                if (object.ReferenceEquals(old, prevBase)) newBase = old;
            }

            _curRun = newCur;
            _chartRun = newChart;
            _baseRun = newBase;
            if (_mode == "wake" && _chartRun == null)
                _chartRun = (_curRun != null && _curRun.Mode == "wake") ? _curRun : LastWithSamples();
            if (_baseRun != null)
            {
                List<RunData> same = ModeRuns(_mode);
                _sel = IndexInMode(same, _baseRun) - 1;
            }
            else _sel = -1;
        }

        private static int FindMatchingPrevRun(RunData target, int indexHint, List<RunData> prevRuns, bool[] used,
            RunData prevCur, RunData prevChart, RunData prevBase)
        {
            if (indexHint >= 0 && indexHint < prevRuns.Count && !used[indexHint]
                && SameRunData(target, prevRuns[indexHint]))
                return indexHint;
            for (int j = 0; j < prevRuns.Count; j++)
            {
                if (used[j]) continue;
                RunData old = prevRuns[j];
                if (!SameRunData(target, old)) continue;
                if (HasSessionDetail(old) || object.ReferenceEquals(old, prevCur)
                    || object.ReferenceEquals(old, prevChart) || object.ReferenceEquals(old, prevBase))
                    return j;
            }
            for (int j = 0; j < prevRuns.Count; j++)
                if (!used[j] && SameRunData(target, prevRuns[j])) return j;
            return -1;
        }

        private static bool HasSessionDetail(RunData r)
        {
            return r != null && (r.Sorted != null || r.St != null || r.MemRes != null || r.PpRes != null);
        }

        private static bool SameRunData(RunData a, RunData b)
        {
            if (a == null || b == null) return false;
            if (!string.Equals(a.Mode, b.Mode, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Time, b.Time, StringComparison.Ordinal)) return false;
            if (a.Count != b.Count) return false;
            return Math.Abs(a.P99 - b.P99) < 0.01
                && Math.Abs(a.P50 - b.P50) < 0.01
                && Math.Abs(a.BandwidthMBs - b.BandwidthMBs) < 0.01;
        }

        private static RunData FromRecord(BenchRecord b)
        {
            RunData r = new RunData();
            r.Time = b.Time == null ? "" : b.Time;
            r.Mode = string.IsNullOrEmpty(b.Mode) ? "wake" : b.Mode;
            r.P50 = b.P50;
            r.P99 = b.P99;
            r.Max = b.Max;
            r.BandwidthMBs = b.BandwidthMBs;
            r.MemLatNs = b.MemLatNs;
            r.Count = b.Count;
            r.TimerMs = b.TimerMs;
            r.Note = b.Note == null ? "" : b.Note;
            return r;
        }

        // ---- Hero & AB 芯片绘制（mem / pingpong）----------------------------
        private void PaintModeHero()
        {
            List<RunData> same = ModeRuns(_mode);
            RunData src = _phase == Phase.Running ? null : ActiveRunForMode(same);
            bool archived = src != null && src != _curRun;
            bool mem = _mode == "mem";

            if (_heroTag != null)
            {
                string k = _phase == Phase.Running ? (mem ? "MEASURING / COPY" : "MEASURING / P99")
                    : (archived ? (mem ? "LAST RECORD / COPY" : "LAST RECORD / P99")
                                : (mem ? "THIS RUN / COPY" : "THIS RUN / P99"));
                _heroTag.Text = Ui.Spaced(k);
            }

            if (_phase == Phase.Running || src == null)
            {
                _heroValRun.Text = "\u2014";
                _heroValRun.Foreground = T.S(T.Mute);
                SetMetric(_stP1, "\u2014");
                SetMetric(_stP50, "\u2014");
                SetMetric(_stSigma, "\u2014");
                if (_heroDelta != null) { _heroDelta.Text = _phase == Phase.Running ? "\u8FD0\u884C\u4E2D" : "\u2014"; _heroDelta.Foreground = T.S(_phase == Phase.Running ? T.Accent : T.Dim); }
                if (_heroDeltaNote != null)
                {
                    _heroDeltaNote.Text = _phase == Phase.Running
                        ? (mem ? "\u6B63\u5728\u6D4B\u8BD5\u987A\u5E8F\u62F7\u8D1D\u4E0E\u968F\u673A\u6307\u9488\u8FFD\u9010\u2026" : "\u53CC\u7EBF\u7A0B\u8DE8\u6838\u5524\u9192\u5F80\u8FD4\u91C7\u6837\u4E2D\u2026")
                        : ("\u5C1A\u65E0\u8BB0\u5F55 \u00B7 \u70B9\u53F3\u4E0A\u300C" + (mem ? "\u5F00\u59CB\u6D4B\u901F" : "\u5F00\u59CB\u6D4B\u8BD5") + "\u300D");
                }
                if (_footRun != null) _footRun.Text = _phase == Phase.Running ? "\u540E\u53F0\u7EBF\u7A0B\u6267\u884C\u4E2D" : "\u5C1A\u65E0\u5B9E\u6D4B\u6570\u636E";
                PaintAbChip();
                return;
            }

            _heroValRun.Text = FormatPrimary(PrimaryValue(src, _mode), _mode);
            _heroValRun.Foreground = T.S(T.Hi);

            if (mem)
            {
                SetMetric(_stP1, src.MemLatNs > 0 ? src.MemLatNs.ToString("0.0") : "\u2014");
                SetMetric(_stP50, src.MemRes != null ? src.MemRes.SeqBufferMB.ToString() : "\u2014");
                SetMetric(_stSigma, src.Count > 0 ? (src.Count >= 1000000 ? (src.Count / 1000000.0).ToString("0.0") + "M" : src.Count.ToString("#,##0")) : "\u2014");
                if (_footRun != null)
                {
                    string f = (src.MemRes != null ? "\u8017\u65F6 " + src.MemRes.ElapsedMs + " ms \u00B7 " : "")
                        + (string.IsNullOrEmpty(src.Note) ? "\u5B9E\u6D4B\u541E\u5410\u4E0E\u5EF6\u8FDF" : src.Note);
                    _footRun.Text = f;
                }
            }
            else
            {
                SetMetric(_stP1, src.P50 > 0 ? src.P50.ToString("0.0") : "\u2014");
                SetMetric(_stP50, src.P50 > 0 ? (src.P50 * 0.5).ToString("0.0") : "\u2014");
                SetMetric(_stSigma, src.Max > 0 ? src.Max.ToString("0.0") : "\u2014");
                if (_footRun != null)
                {
                    _footRun.Text = "\u5F80\u8FD4\u6837\u672C " + src.Count.ToString("#,##0")
                        + (string.IsNullOrEmpty(src.Note) ? "" : " \u00B7 " + src.Note);
                }
            }

            PaintModeHeroDelta(same, src);
            PaintAbChip();
        }

        private void PaintModeHeroDelta(List<RunData> same, RunData src)
        {
            RunData best = null;
            bool hib = HigherIsBetter(_mode);
            double sv = PrimaryValue(src, _mode);
            for (int i = 0; i < same.Count; i++)
            {
                RunData r = same[i];
                if (r == src) continue;
                double v = PrimaryValue(r, _mode);
                if (v <= 0) continue;
                if (best == null || (hib ? v > PrimaryValue(best, _mode) : v < PrimaryValue(best, _mode))) best = r;
            }
            if (best == null)
            {
                if (_heroDelta != null) { _heroDelta.Text = "\u2014"; _heroDelta.Foreground = T.S(T.Dim); }
                if (_heroDeltaNote != null) _heroDeltaNote.Text = "\u672C\u6A21\u5F0F\u9996\u6761\u8BB0\u5F55 \u00B7 \u65E0\u5386\u53F2\u5BF9\u7167";
                return;
            }
            double bv = PrimaryValue(best, _mode);
            double d = (sv - bv) / bv * 100.0;
            bool better = hib ? d > 0 : d < 0;
            bool sameVal = Math.Abs(d) < 0.05;
            uint col = sameVal ? T.Dim : (better ? T.Ok : T.Warn);
            string unit = _mode == "mem" ? " MB/s" : " \u00B5s";
            if (_heroDelta != null)
            {
                _heroDelta.Text = sameVal ? "\u00B7 0.0%" : (d > 0 ? "\u2191 " : "\u2193 ") + Math.Abs(d).ToString("0.0") + "%";
                _heroDelta.Foreground = T.S(col);
            }
            if (_heroDeltaNote != null)
                _heroDeltaNote.Text = (sameVal ? "\u4E0E\u5386\u53F2\u6700\u4F73\u6301\u5E73" : (better ? "\u5237\u65B0\u5386\u53F2\u6700\u4F73" : "\u8DDD\u5386\u53F2\u6700\u4F73"))
                    + " \u00B7 " + FormatPrimary(bv, _mode) + unit;
        }

        private void PaintAbChip()
        {
            if (_abChip == null || _abNote == null) return;
            List<RunData> same = ModeRuns(_mode);
            RunData cur = ActiveRunForMode(same);
            RunData cmp = CompareBaseForMode(same, cur);
            bool hasManual = _baseRun != null && _baseRun.Mode == _mode && _baseRun != cur;
            if (_btnClearBase != null) _btnClearBase.Visibility = hasManual ? Visibility.Visible : Visibility.Collapsed;

            if (cur == null || cmp == null)
            {
                _abChip.Set(0, "", HigherIsBetter(_mode));
                _abNote.Text = same.Count <= 1 ? "\u81F3\u5C11 2 \u6B21\u540C\u53E3\u5F84\u8FD0\u884C\u53EF A/B \u5BF9\u7167" : "\u70B9\u9009\u5386\u53F2\u884C\u53EF\u8BBE\u5BF9\u7167\u57FA\u7EBF";
                return;
            }

            double vc = PrimaryValue(cur, _mode);
            double vb = PrimaryValue(cmp, _mode);
            double d = vb > 0 ? (vc - vb) / vb * 100.0 : 0;
            int dir = Math.Abs(d) < 0.05 ? 0 : (d > 0 ? 1 : -1);
            _abChip.Set(dir, Math.Abs(d).ToString("0.0") + "%", HigherIsBetter(_mode));
            string unit = _mode == "mem" ? " MB/s" : " \u00B5s";
            _abNote.Text = (hasManual ? "\u57FA\u7EBF#" : "\u4E0A\u6B21#") + IndexInMode(same, cmp)
                + " (" + FormatPrimary(vb, _mode) + unit + ")";
        }

        // ---- Mode 2 / Mode 3 图表面板可视化 ----------------------------------
        private void PaintModeViz()
        {
            if (_modeVizHost == null) return;
            _modeVizHost.Children.Clear();
            List<RunData> same = ModeRuns(_mode);
            RunData cur = _phase == Phase.Running ? null : ActiveRunForMode(same);
            RunData cmp = CompareBaseForMode(same, cur);

            if (_phase == Phase.Running)
            {
                _chartMeta.Text = "MEASURING...";
                _chartFoot.Text = "\u540E\u53F0\u6267\u884C\u4E2D \u00B7 \u5B8C\u6210\u540E\u81EA\u52A8\u5199\u5165\u5386\u53F2\u5E76\u5237\u65B0 A/B \u5BF9\u7167";
                _modeVizHost.Children.Add(Ui.Empty(Glyph.Benchmark, "\u6B63\u5728\u6267\u884C\u57FA\u51C6\u6D4B\u8BD5\u2026",
                    _mode == "mem" ? "\u6B63\u5728\u6D4B\u91CF\u987A\u5E8F\u5185\u5B58\u62F7\u8D1D\u541E\u5410\u4E0E\u968F\u673A\u6307\u9488\u8FFD\u9010\u5EF6\u8FDF" : "\u6B63\u5728\u6D4B\u91CF\u53CC\u7EBF\u7A0B\u8DE8\u6838\u5524\u9192\u5F80\u8FD4\u5EF6\u8FDF", T.Accent, null));
                return;
            }

            if (cur == null)
            {
                _chartMeta.Text = "NO DATA";
                _chartFoot.Text = "\u5F53\u524D\u6A21\u5F0F\u6682\u65E0\u5B9E\u6D4B\u8BB0\u5F55";
                _modeVizHost.Children.Add(Ui.Empty(Glyph.Benchmark,
                    _mode == "mem" ? "\u5C1A\u65E0\u5185\u5B58\u5E26\u5BBD\u4E0E\u5EF6\u8FDF\u8BB0\u5F55" : "\u5C1A\u65E0\u7EBF\u7A0B\u8C03\u5EA6\u4E52\u4E53\u8BB0\u5F55",
                    "\u70B9\u51FB\u53F3\u4E0A\u65B9\u6309\u94AE\u8FD0\u884C\u4E00\u6B21\u540E\uFF0C\u8FD9\u91CC\u5C06\u5C55\u793A\u5B9E\u6D4B\u5206\u6790\u6761\u4E0E A/B \u5386\u53F2\u66F2\u7EBF\u3002", T.Mute, StartBtn()));
                return;
            }

            _chartMeta.Text = (_mode == "mem"
                ? (cur.BandwidthMBs.ToString("#,##0") + " MB/s \u00B7 " + cur.MemLatNs.ToString("0.0") + " ns")
                : ("P99 " + cur.P99.ToString("0.0") + " \u00B5s \u00B7 P50 " + cur.P50.ToString("0.0") + " \u00B5s"))
                + " \u00B7 " + ShortTime(cur.Time);

            StackPanel card = new StackPanel();
            if (_mode == "mem")
            {
                double maxBw = Math.Max(cur.BandwidthMBs, cmp != null ? cmp.BandwidthMBs : 0) * 1.15;
                double maxLat = Math.Max(cur.MemLatNs, cmp != null ? cmp.MemLatNs : 0) * 1.15;
                card.Children.Add(CompareBarRow("\u987A\u5E8F\u62F7\u8D1D\u5E26\u5BBD", cur.BandwidthMBs, cmp != null ? cmp.BandwidthMBs : 0, maxBw, "MB/s", true, false));
                card.Children.Add(CompareBarRow("\u968F\u673A\u8BBF\u5B58\u5EF6\u8FDF", cur.MemLatNs, cmp != null ? cmp.MemLatNs : 0, maxLat, "ns", false, true));
            }
            else
            {
                double maxAll = Math.Max(Math.Max(cur.P99, cur.Max), cmp != null ? Math.Max(cmp.P99, cmp.Max) : 0) * 1.12;
                card.Children.Add(CompareBarRow("\u5F80\u8FD4 P50 \u5EF6\u8FDF", cur.P50, cmp != null ? cmp.P50 : 0, maxAll, "\u00B5s", false, true));
                card.Children.Add(CompareBarRow("\u5F80\u8FD4 P99 \u5EF6\u8FDF", cur.P99, cmp != null ? cmp.P99 : 0, maxAll, "\u00B5s", false, true));
                card.Children.Add(CompareBarRow("\u5F80\u8FD4 MAX \u5CF0\u503C", cur.Max, cmp != null ? cmp.Max : 0, maxAll, "\u00B5s", false, true));
            }

            if (same.Count >= 2)
            {
                int start = Math.Max(0, same.Count - 24);
                int cnt = same.Count - start;
                Waveform wf = new Waveform(Math.Max(8, cnt));
                wf.Height = 148;
                wf.Unit = _mode == "mem" ? "MB/s" : "\u00B5s";
                wf.ShowThreshold = false;
                wf.Tint = _mode == "mem" ? T.SeriesA : T.Accent;
                wf.OverlayTint = T.SeriesB;
                for (int i = start; i < same.Count; i++) wf.Push(PrimaryValue(same[i], _mode));
                if (cmp != null)
                {
                    double bv = PrimaryValue(cmp, _mode);
                    double[] ov = new double[cnt];
                    for (int i = 0; i < cnt; i++) ov[i] = bv;
                    wf.SetOverlay(ov, cnt);
                }

                StackPanel legend = new StackPanel();
                legend.Orientation = Orientation.Horizontal;
                legend.Margin = new Thickness(0, 12, 0, 6);
                legend.Children.Add(Ui.DotLabel(wf.Tint, _mode == "mem" ? "\u5386\u6B21\u5E26\u5BBD\u8D70\u52BF" : "\u5386\u6B21 P99 \u8D70\u52BF", T.Dim));
                if (cmp != null)
                {
                    StackPanel baseLbl = Ui.DotLabel(wf.OverlayTint, "BASE #" + IndexInMode(same, cmp) + " = " + FormatPrimary(PrimaryValue(cmp, _mode), _mode) + " " + wf.Unit, T.SeriesB);
                    baseLbl.Margin = new Thickness(16, 0, 0, 0);
                    legend.Children.Add(baseLbl);
                }
                card.Children.Add(legend);
                card.Children.Add(Ui.Inset(wf, 6));
            }

            _modeVizHost.Children.Add(card);
            _chartFoot.Text = same.Count + " \u6B21\u540C\u53E3\u5F84\u8FD0\u884C"
                + (cmp != null ? (" \u00B7 \u5F53\u524D\u5BF9\u7167\u57FA\u7EBF #" + IndexInMode(same, cmp)) : " \u00B7 \u70B9\u9009\u4E0B\u65B9\u5386\u53F2\u53EF\u5207\u6362\u5BF9\u7167\u57FA\u7EBF");
        }

        private static UIElement CompareBarRow(string title, double vCur, double vBase, double scale, string unit, bool hib, bool dec)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 0, 0, 10);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });

            TextBlock lbl = Ui.Tx(title, T.FsSmall, T.Text, T.FCn);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(lbl);

            StackPanel bars = new StackPanel();
            bars.VerticalAlignment = VerticalAlignment.Center;
            SegBar bCur = new SegBar(scale > 0 ? Math.Min(1.0, vCur / scale) : 0);
            bCur.Segments = 30;
            bCur.Height = 6;
            bCur.Tint = T.Accent;
            bars.Children.Add(bCur);
            if (vBase > 0)
            {
                SegBar bBase = new SegBar(scale > 0 ? Math.Min(1.0, vBase / scale) : 0);
                bBase.Segments = 30;
                bBase.Height = 4;
                bBase.Margin = new Thickness(0, 3, 0, 0);
                bBase.Tint = T.SeriesB;
                bars.Children.Add(bBase);
            }
            Grid.SetColumn(bars, 1);
            g.Children.Add(bars);

            StackPanel vals = new StackPanel();
            vals.Orientation = Orientation.Horizontal;
            vals.HorizontalAlignment = HorizontalAlignment.Right;
            vals.VerticalAlignment = VerticalAlignment.Center;
            string fmt = dec ? "#,##0.0" : "#,##0";
            TextBlock vt = Ui.Mono(vCur.ToString(fmt) + " " + unit, T.FsSmall, T.Hi);
            vals.Children.Add(vt);
            if (vBase > 0)
            {
                DeltaChip dc = new DeltaChip();
                dc.Margin = new Thickness(8, 0, 0, 0);
                double p = (vCur - vBase) / vBase * 100.0;
                int dir = Math.Abs(p) < 0.05 ? 0 : (p > 0 ? 1 : -1);
                dc.Set(dir, Math.Abs(p).ToString("0.0") + "%", hib);
                vals.Children.Add(dc);
            }
            Grid.SetColumn(vals, 2);
            g.Children.Add(vals);
            return g;
        }
    }
}
