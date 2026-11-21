// ============================================================================
//  Benchmark.Run.cs — 采样引擎与刷新（Benchmark 分片）
//
//  状态机：Idle → Running → Done / Aborted / Failed。
//  中止是真的中止：停 DispatcherTimer、停秒表、丢弃本次窗口的样本、不写历史、
//  不参与判定 —— 一个 2 秒的残窗和 8 秒的窗口不是同一个量，混在一起就是假数据。
//
//  每 150ms 一拍：DrainInto 取新增样本 → 一次排序算全部统计 → 刷新读数与直方图。
//  排序缓冲与统计对象全部复用，稳态零分配（旧实现每拍排两次序、ToArray 一份）。
//
//  Rev86 把"状态到画面"的映射拆成四段，各管一段，互不猜对方：
//    PaintHero   主指标 + 三格读数（值 / 与历史最好的差 / 注脚）
//    PaintSteps  四步状态机（准备 / 运行 / 结果 / 存档）
//    PaintClock  已用 / 预计剩余 / 进度（只在整数刻度变化时拼字符串）
//    PaintChart  直方图 + 图表脚注
//  旧版把这些全塞进一个 PaintProgress，加上 hero 里那段 102 行的绘制，
//  于是"运行到一半"和"跑完了"这两条路径各写一遍同样的东西，改一处漏一处。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Benchmark
    {
        // ====================================================================
        //  引擎
        // ====================================================================
        private void StartSample()
        {
            if (C == null || _phase == Phase.Running) return;
            if (_mode != "wake") { StartModeBench(_mode); return; }

            System.Threading.Interlocked.Increment(ref _bgRunToken);
            _samples.Clear();
            _sortN = 0;
            _live.Reset();
            _cur = null;
            _curRun = null;
            _chartRun = null;
            _sel = -1;

            _cursor = C.Perf.JitterCount;   // 只算本窗口内产生的样本
            _sw.Reset();
            _sw.Start();
            _phase = Phase.Running;

            UpdateRunButton();
            PaintAll();
            RefreshHistory();

            EnsurePoll();
            _poll.Start();
        }

        /// <summary>中止：窗口没跑满就不产出一条记录 —— 残窗与满窗不可比。</summary>
        private void AbortRun()
        {
            if (_phase != Phase.Running) return;

            System.Threading.Interlocked.Increment(ref _bgRunToken);
            if (_poll != null) _poll.Stop();
            _sw.Stop();
            _samples.Clear();
            _sortN = 0;
            _live.Reset();

            _phase = Phase.Aborted;
            _chartRun = _mode == "wake" ? LastWithSamples() : LastRunOfMode(_mode);
            _sel = -1;
            ResetPaintCaches();

            UpdateRunButton();
            PaintAll();
            RefreshHistory();

            if (C != null && C.Shell != null)
                C.Shell.Toast("\u672C\u6B21\u91C7\u6837\u5DF2\u4E2D\u6B62 \u00B7 \u672A\u5199\u5165\u5386\u53F2\uFF0C\u4E5F\u4E0D\u53C2\u4E0E\u5224\u5B9A", true);
        }

        private RunData LastWithSamples()
        {
            for (int i = _runs.Count - 1; i >= 0; i--)
                if (_runs[i].Mode == "wake" && _runs[i].Sorted != null) return _runs[i];
            return null;
        }

        private void EnsurePoll()
        {
            if (_poll != null) return;
            _poll = new DispatcherTimer();
            _poll.Interval = TimeSpan.FromMilliseconds(150);
            _poll.Tick += delegate { OnPoll(); };
        }

        private void OnPoll()
        {
            if (_phase != Phase.Running)
            {
                if (_poll != null) _poll.Stop();
                return;
            }
            if (_mode != "wake") { PaintClock(); return; }
            Pump();
            PaintHero();
            PaintSteps();
            PaintClock();
            PaintChart();
            if (Elapsed() >= WindowSec) Finish();
        }

        private readonly double[] _jb = new double[512];

        private void Pump()
        {
            if (C == null) return;
            int n = C.Perf.DrainInto(_jb, ref _cursor);
            if (n <= 0) return;
            for (int i = 0; i < n; i++) _samples.Add(_jb[i]);
            ComputeStats(_samples, _live);
        }

        // ====================================================================
        //  统计：一次排序，全部读数由它派生
        // ====================================================================
        private void ComputeStats(List<double> src, Stats dst)
        {
            int n = src == null ? 0 : src.Count;
            dst.Reset();
            dst.Count = n;
            _sortN = 0;
            if (n == 0) return;

            if (_sortBuf.Length < n) _sortBuf = new double[Math.Max(n, _sortBuf.Length * 2)];
            for (int i = 0; i < n; i++) _sortBuf[i] = src[i];
            Array.Sort(_sortBuf, 0, n);
            _sortN = n;

            double sum = 0;
            for (int i = 0; i < n; i++) sum += _sortBuf[i];
            dst.Mean = sum / n;

            double ss = 0;
            for (int i = 0; i < n; i++)
            {
                double d = _sortBuf[i] - dst.Mean;
                ss += d * d;
            }
            dst.Sigma = Math.Sqrt(ss / n);

            dst.Min = _sortBuf[0];
            dst.Max = _sortBuf[n - 1];
            dst.P1 = Q(n, 0.01);
            dst.P25 = Q(n, 0.25);
            dst.P50 = Q(n, 0.50);
            dst.P75 = Q(n, 0.75);
            dst.P95 = Q(n, 0.95);
            dst.P99 = Q(n, 0.99);

            // far-out 围栏（Tukey）：只用于"数出来"，不用于剔除 —— 异常值照样进分箱。
            double fence = dst.P75 + 3.0 * (dst.P75 - dst.P25);
            int far = 0;
            for (int i = 0; i < n; i++) if (_sortBuf[i] > fence) far++;
            dst.FarOut = far;
        }

        /// <summary>最近秩分位数（nearest-rank），对 n 的取样位置四舍五入，不插值。</summary>
        private double Q(int n, double q)
        {
            int k = (int)((n - 1) * q + 0.5);
            if (k < 0) k = 0; else if (k > n - 1) k = n - 1;
            return _sortBuf[k];
        }

        private void Finish()
        {
            Pump();
            if (_poll != null) _poll.Stop();
            _sw.Stop();

            if (_samples.Count < MinSamples)
            {
                _phase = Phase.Failed;
                _samples.Clear();
                _sortN = 0;
                _live.Reset();
                _chartRun = LastWithSamples();
                UpdateRunButton();
                PaintAll();
                RefreshHistory();
                if (C != null && C.Shell != null)
                    C.Shell.Toast("\u672A\u53D6\u5230\u8DB3\u591F\u7684\u6296\u52A8\u6837\u672C \u00B7 \u672C\u6B21\u4E0D\u53EF\u7528", false);
                return;
            }

            Stats st = new Stats();
            st.CopyFrom(_live);

            RunData r = new RunData();
            r.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            r.P50 = st.P50;
            r.P99 = st.P99;
            r.Max = st.Max;
            r.Count = st.Count;
            r.TimerMs = C != null ? C.Perf.Current.TimerMs : 0;
            r.Sorted = new double[_sortN];
            Array.Copy(_sortBuf, r.Sorted, _sortN);
            r.St = st;

            _cur = st;
            _curRun = r;
            _chartRun = r;
            _runs.Add(r);
            _phase = Phase.Done;
            _sel = -1;

            BenchRecord rec = new BenchRecord();
            rec.Time = r.Time;
            rec.P50 = r.P50;
            rec.P99 = r.P99;
            rec.Max = r.Max;
            rec.Count = r.Count;
            rec.TimerMs = r.TimerMs;
            BenchHistory.Append(rec);

            UpdateRunButton();
            PaintAll();
            RefreshHistory();

            if (C != null && C.Shell != null)
                C.Shell.Toast("\u91C7\u6837\u5B8C\u6210 \u00B7 P99 " + st.P99.ToString("#,##0") + " \u00B5s \u00B7 " + st.Count.ToString("#,##0") + " \u6837\u672C", true);
        }

        private double Elapsed()
        {
            double e = _sw.Elapsed.TotalSeconds;
            if (e < 0) e = 0;
            if (e > WindowSec) e = WindowSec;
            return e;
        }

        private void UpdateRunButton()
        {
            if (_btnSample == null) return;
            if (_mode == "wake")
            {
                _btnSample.Label = _phase == Phase.Running ? "\u91C7\u6837\u4E2D"
                    : (_phase == Phase.Idle ? "\u5F00\u59CB\u91C7\u6837" : "\u91CD\u65B0\u91C7\u6837");
            }
            else if (_mode == "mem")
            {
                _btnSample.Label = _phase == Phase.Running ? "\u6D4B\u901F\u4E2D"
                    : (_phase == Phase.Idle ? "\u5F00\u59CB\u6D4B\u901F" : "\u91CD\u65B0\u6D4B\u901F");
            }
            else
            {
                _btnSample.Label = _phase == Phase.Running ? "\u6D4B\u8BD5\u4E2D"
                    : (_phase == Phase.Idle ? "\u5F00\u59CB\u6D4B\u8BD5" : "\u91CD\u65B0\u6D4B\u8BD5");
            }
            _btnSample.Enabled = _phase != Phase.Running;
        }

        // ====================================================================
        //  历史装载（保留会话内明细与指针逻辑见 Benchmark.Modes.cs）
        // ====================================================================

        // ====================================================================
        //  刷新
        // ====================================================================
        private void PaintAll()
        {
            PaintHeader();
            PaintHero();
            PaintSteps();
            PaintClock();
            PaintChart();
            PaintVerdict();
        }

        private void PaintHeader()
        {
            if (_hdrVal == null) return;

            _hdrKick.Text = Ui.Spaced(_mode == "mem" ? "HISTORY / COPY BEST" : "HISTORY / P99 BEST");
            List<RunData> same = ModeRuns(_mode);
            if (same.Count == 0)
            {
                _hdrVal.Text = "\u2014";
                _hdrVal.Foreground = T.S(T.Mute);
                _hdrDelta.Text = "";
                return;
            }

            bool hib = HigherIsBetter(_mode);
            RunData last = same[same.Count - 1];
            double best = PrimaryValue(same[0], _mode);
            for (int i = 1; i < same.Count; i++)
            {
                double v = PrimaryValue(same[i], _mode);
                if (v <= 0) continue;
                if (best <= 0 || (hib ? v > best : v < best)) best = v;
            }

            _hdrVal.Text = FormatPrimary(best, _mode);
            _hdrVal.Foreground = T.S(T.Hi);

            if (same.Count > 1)
            {
                double vLast = PrimaryValue(last, _mode);
                double diffPct = best > 0 ? (vLast - best) / best * 100.0 : 0;
                _hdrDelta.Text = same.Count + "\u6B21 \u00B7 \u8DDD\u6700\u4F73 " + Math.Abs(diffPct).ToString("0.0") + "%";
                _hdrDelta.Foreground = T.S(Math.Abs(diffPct) < 0.05 ? T.Ok : T.Dim);
            }
            else
            {
                _hdrDelta.Text = "\u00B7 1\u6B21\u8BB0\u5F55";
                _hdrDelta.Foreground = T.S(T.Dim);
            }
        }

        private static string Arrow(double d)
        {
            if (Math.Abs(d) < 0.05) return "\u00B7 ";
            return d < 0 ? "\u2193 " : "\u2191 ";
        }

        // ---- 主指标 ---------------------------------------------------------
        /// <summary>
        /// 主读数：运行中读实时样本，其它状态读本次结果（没有结果就退到最近一条档案）。
        /// 旧版运行中显示的是"上一条历史记录"，而右上角却写着 LIVE —— 同一页两个数打架。
        /// </summary>
        // 主读数的整数刻度缓存：这一组函数 150ms 跑一次，值没变就一个字符都不重拼。
        private int _heroP99 = int.MinValue;
        private int _stP1Shown = int.MinValue, _stP50Shown = int.MinValue, _stSigShown = int.MinValue;
        private int _tagShown = -1;
        private int _footCount = int.MinValue, _footP95 = int.MinValue, _footMax = int.MinValue, _footFar = int.MinValue;
        private long _deltaKey = long.MinValue;

        private void PaintHero()
        {
            if (_heroValRun == null) return;
            if (_mode != "wake") { PaintModeHero(); return; }

            if (_phase == Phase.Running) { PaintHeroLive(); PaintAbChip(); return; }

            RunData src = _curRun != null && _curRun.Mode == "wake" ? _curRun : null;
            bool archived = false;
            if (src == null)
            {
                src = LastRunOfMode("wake");
                archived = src != null;
            }

            if (src == null) { PaintHeroIdle(); PaintAbChip(); return; }

            SetHero(src.P99);
            SetTag(archived ? 1 : 0);
            PaintMetrics(src.St, src.P99, src.P50, src.St != null);
            PaintDelta(src.P99, src);
            PaintFoot(src.St, src.Count, src.Max);
            PaintAbChip();
        }

        /// <summary>运行中：样本 ≥ 2 才给读数（1 个样本的分位数是把同一个数抄四遍）。</summary>
        private void PaintHeroLive()
        {
            bool ok = _live.Count >= 2;
            SetHero(ok ? _live.P99 : -1);
            SetTag(2);
            PaintMetrics(_live, -1, _live.P50, ok);
            if (ok) PaintDelta(_live.P99, null);
            else SetDelta(0, 0, 0, "", T.Dim, "\u8BA1\u65F6\u4E2D \u00B7 \u5C1A\u672A\u53D6\u5230\u53EF\u7528\u6837\u672C");
            PaintFoot(_live, _live.Count, ok ? _live.Max : 0);
        }

        /// <summary>主指标写入：v &lt; 0 表示"还没有可用样本"，显示 "—"。只在整数变化时写。</summary>
        private void SetHero(double v)
        {
            int iv = v < 0 ? int.MinValue : (int)Math.Round(v);
            if (iv == _heroP99) return;
            _heroP99 = iv;
            _heroValRun.Text = iv == int.MinValue ? "\u2014" : iv.ToString("#,##0");
            _heroValRun.Foreground = T.S(iv == int.MinValue ? T.Mute : T.Hi);
        }

        /// <summary>主指标标签：0 本次 / 1 最近记录 / 2 实时 / 3 空闲。</summary>
        private void SetTag(int mode)
        {
            if (mode == _tagShown) return;
            _tagShown = mode;
            _heroTag.Text = Ui.Spaced(mode == 1 ? "LAST RECORD / P99" : (mode == 2 ? "LIVE / P99" : "THIS RUN / P99"));
        }

        private void PaintHeroIdle()
        {
            SetHero(-1);
            SetTag(3);
            SetDelta(0, 0, 0, "", T.Dim, "\u5C1A\u65E0\u8FD0\u884C \u00B7 \u70B9\u53F3\u4E0A\u300C\u5F00\u59CB\u91C7\u6837\u300D\u8DD1\u6EE1 8.0 \u79D2\u7A97\u53E3");
            PaintMetrics(null, -1, 0, false);
            PaintFoot(null, 0, 0);
        }

        /// <summary>
        /// 三格读数。full = 这一条有完整样本（P1 / σ 可算）。
        /// 历史档案只存了聚合读数：P1 / σ 必须显示 "—"，绝不拿 0 冒充。
        /// 三个格子各有自己的整数刻度缓存：150ms 一拍，值没变就不写。
        /// </summary>
        private void PaintMetrics(Stats st, double p1, double p50, bool full)
        {
            SetNum(_stP1, full && st != null ? st.P1 : -1, ref _stP1Shown);
            SetNum(_stP50, p50 > 0 ? p50 : -1, ref _stP50Shown);
            SetNum(_stSigma, full && st != null ? st.Sigma : -1, ref _stSigShown);
        }

        /// <summary>读数的整数刻度写入：负值 = 无数据（显示 "—"），其余四舍五入后比较。</summary>
        private static void SetNum(TextBlock tb, double v, ref int shown)
        {
            int iv = v < 0 ? int.MinValue : (int)Math.Round(v);
            if (iv == shown) return;
            shown = iv;
            tb.Text = iv == int.MinValue ? "\u2014" : iv.ToString("#,##0");
            tb.Foreground = T.S(iv == int.MinValue ? T.Mute : T.Hi);
        }

        private void PaintFoot(Stats st, int count, double max)
        {
            if (_footRun == null) return;
            int p95 = st != null && st.Count >= 2 ? (int)Math.Round(st.P95) : int.MinValue;
            int mx = max > 0 ? (int)Math.Round(max) : int.MinValue;
            int far = st != null ? st.FarOut : 0;
            if (count == _footCount && p95 == _footP95 && mx == _footMax && far == _footFar) return;
            _footCount = count;
            _footP95 = p95;
            _footMax = mx;
            _footFar = far;

            string s = "\u6837\u672C " + count.ToString("#,##0");
            s += p95 == int.MinValue ? " \u00B7 P95 \u2014" : " \u00B7 P95 " + p95.ToString("#,##0");
            s += mx == int.MinValue ? " \u00B7 MAX \u2014" : " \u00B7 MAX " + mx.ToString("#,##0") + " \u00B5s";
            if (far > 0) s += " \u00B7 FAR-OUT " + far.ToString("#,##0");
            _footRun.Text = s;
        }

        /// <summary>
        /// 与历史最好的差值。用 Warn 而不是 Bad：它说明"还有更好的那次"，不是故障。
        /// 差值只到 0.1%，缓存键 = (分支, P99 整数, 历史最好整数) 拼成一个 long：键没变就一个字符都不重拼。
        /// </summary>
        private void PaintDelta(double p99, RunData self)
        {
            RunData exclude = _curRun != null ? _curRun : self;
            double best = 0;
            bool hasBest = false;
            for (int i = 0; i < _runs.Count; i++)
            {
                RunData r = _runs[i];
                if (r.Mode != "wake" || r == exclude) continue;
                if (!(r.P99 > 0)) continue;
                if (!hasBest || r.P99 < best) { best = r.P99; hasBest = true; }
            }

            if (!hasBest)
            {
                SetDelta(0, 0, 0, "\u2014", T.Dim, "\u672C\u673A\u9996\u6761\u8BB0\u5F55 \u00B7 \u65E0\u5386\u53F2\u5BF9\u7167");
                return;
            }

            double d = (p99 - best) / best * 100.0;
            int p99i = (int)Math.Round(p99, MidpointRounding.AwayFromZero);
            int besti = (int)Math.Round(best, MidpointRounding.AwayFromZero);

            if (Math.Abs(d) < 0.05)
            {
                SetDelta(1, p99i, besti, "\u00B7 0.0%", T.Dim, "\u4E0E\u5386\u53F2\u6700\u597D\u6301\u5E73 \u00B7 " + besti.ToString("#,##0") + " \u00B5s");
                return;
            }
            if (d < 0)
            {
                SetDelta(2, p99i, besti, "\u2193 " + Math.Abs(d).ToString("0.0") + "%", T.Ok,
                    "\u5237\u65B0\u5386\u53F2\u6700\u597D \u00B7 \u539F " + besti.ToString("#,##0") + " \u00B5s");
                return;
            }
            SetDelta(3, p99i, besti, "\u2191 " + Math.Abs(d).ToString("0.0") + "%", T.Warn, WorseNote(besti));
        }

        /// <summary>高于历史最好时的注脚：样本够就把噪声带宽一起写出来，读者自己判断。</summary>
        private string WorseNote(int besti)
        {
            double sig;
            int nz;
            NoiseRel(out sig, out nz);
            string note = "\u9AD8\u4E8E\u5386\u53F2\u6700\u597D " + besti.ToString("#,##0") + " \u00B5s";
            if (nz >= 3) note += "\uFF08\u566A\u58F0 \u00B1" + (sig * 1.41421356).ToString("0.0") + "%\uFF09";
            return note;
        }

        /// <summary>差值的唯一写入点。branch: 0 无对照 / 1 持平 / 2 更好 / 3 更差。</summary>
        private void SetDelta(int branch, int p99i, int besti, string text, uint tint, string note)
        {
            long key = ((long)branch << 60) ^ ((long)p99i << 24) ^ (uint)besti;
            if (key == _deltaKey) return;
            _deltaKey = key;
            if (_heroDelta != null)
            {
                _heroDelta.Text = text;
                _heroDelta.Foreground = T.S(tint);
            }
            if (_heroDeltaNote != null)
            {
                _heroDeltaNote.Text = note;
                _heroDeltaNote.Foreground = T.S(T.Mute);
            }
        }

        private static void SetMetric(TextBlock tb, string text)
        {
            if (tb == null) return;
            tb.Text = text;
            tb.Foreground = T.S(text == "\u2014" ? T.Mute : T.Hi);
        }

        // ---- 四步状态机 ------------------------------------------------------
        //  Phase 到四步的映射（true = 这一步已经走完）：
        //    Idle            → 停在"准备"
        //    Running         → 准备✔ 运行◀
        //    Done            → 准备✔ 运行✔ 结果◀ 存档✔（跑完就已经落盘，所以存档也是✔）
        //    Aborted/Failed  → 准备✔ 运行✔ 结果◀ 存档空（残窗不写历史）
        private void PaintSteps()
        {
            if (_stepBox == null) return;

            int a, b, c, d;
            switch (_phase)
            {
                case Phase.Running: a = StepDone; b = StepCurrent; c = StepFuture; d = StepFuture; break;
                case Phase.Done: a = StepDone; b = StepDone; c = StepCurrent; d = StepDone; break;
                case Phase.Aborted: a = StepDone; b = StepDone; c = StepCurrent; d = StepFuture; break;
                case Phase.Failed: a = StepDone; b = StepDone; c = StepCurrent; d = StepFuture; break;
                default: a = StepCurrent; b = StepFuture; c = StepFuture; d = StepFuture; break;
            }
            _stepNext[0] = a;
            _stepNext[1] = b;
            _stepNext[2] = c;
            _stepNext[3] = d;

            // 中止/失败时"当前步"用 Warn：此刻需要看的是"这一轮没成"，不是"你在这里"。
            uint tint = (_phase == Phase.Aborted || _phase == Phase.Failed) ? T.Warn : T.Accent;

            for (int i = 0; i < StepCount; i++)
            {
                if (_stepNext[i] == _stepShown[i] && tint == _stepTint) continue;
                _stepShown[i] = _stepNext[i];
                PaintStep(i, _stepNext[i], tint);
            }
            _stepTint = tint;
        }

        private void PaintStep(int i, int state, uint tint)
        {
            Border box = _stepBox[i];
            TextBlock glyph = _stepGlyph[i];
            TextBlock name = _stepName[i];

            if (state == StepCurrent)
            {
                box.BorderBrush = T.S(tint);
                box.Background = T.S(Ui.Wash(tint, 0x1A));
                glyph.Text = (i + 1).ToString();
                glyph.Foreground = T.S(tint);
                name.Foreground = T.S(T.Hi);
                name.FontWeight = FontWeights.SemiBold;
                return;
            }

            name.FontWeight = FontWeights.Normal;

            if (state == StepDone)
            {
                box.BorderBrush = T.S(T.LineMid);
                box.Background = T.S(0x0AFFFFFF);
                glyph.Text = "\u2713";
                glyph.Foreground = T.S(T.Dim);
                name.Foreground = T.S(T.Dim);
                return;
            }

            box.BorderBrush = T.S(T.LineSoft);
            box.Background = Brushes.Transparent;
            glyph.Text = (i + 1).ToString();
            glyph.Foreground = T.S(T.Mute);
            name.Foreground = T.S(T.Mute);
        }

        // ---- 运行钟 ----------------------------------------------------------
        /// <summary>
        /// 已用 / 预计剩余 / 进度。这个函数 150ms 跑一次，所以字符串只在整数刻度
        /// 变化时才拼（tenth 是 0.1 秒刻度，count 是样本数）——稳态零分配、零重排。
        /// </summary>
        private void PaintClock()
        {
            if (_clockUse == null) return;

            bool wake = _mode == "wake";
            bool running = _phase == Phase.Running;
            if (_btnAbort != null)
            {
                Visibility bv = running ? Visibility.Visible : Visibility.Collapsed;
                if (_btnAbort.Visibility != bv) _btnAbort.Visibility = bv;
            }
            if (_stepBar != null)
            {
                Visibility sv = running && wake ? Visibility.Visible : Visibility.Hidden;
                if (_stepBar.Visibility != sv) _stepBar.Visibility = sv;
                if (running && wake) _stepBar.Value = Elapsed() / WindowSec;
            }

            double elSec = wake ? Elapsed() : _sw.Elapsed.TotalSeconds;
            int tenth = (int)Math.Round(elSec * 10);
            int n = _live.Count > 999 ? 999 : _live.Count;
            int key = (int)_phase * 10000000 + tenth * 10000 + n + (wake ? 0 : 100000000);
            if (key == _clockKey) return;
            _clockKey = key;

            string use;
            string eta;
            uint tint = T.Mute;
            if (!wake)
            {
                if (_phase == Phase.Running) { use = "\u5DF2\u7528 " + (tenth / 10.0).ToString("0.0") + " S"; eta = "\u540E\u53F0\u7EBF\u7A0B\u5B9E\u6D4B\u4E2D\u2026"; tint = T.Hi; }
                else if (_phase == Phase.Done) { use = "\u5DF2\u7528 " + (tenth / 10.0).ToString("0.0") + " S \u00B7 \u5B8C\u6210"; eta = "\u5DF2\u5199\u5165\u540C\u53E3\u5F84\u5386\u53F2"; }
                else if (_phase == Phase.Aborted) { use = "\u5DF2\u4E2D\u6B62"; eta = "\u672A\u5199\u5165\u5386\u53F2"; tint = T.Warn; }
                else if (_phase == Phase.Failed) { use = "\u6D4B\u8BD5\u5931\u8D25"; eta = "\u672A\u53D6\u5230\u6709\u6548\u6837\u672C"; tint = T.Warn; }
                else { use = "\u5F85\u6D4B\u8BD5"; eta = _mode == "mem" ? "\u987A\u5E8F\u62F7\u8D1D + \u968F\u673A\u6307\u9488\u8FFD\u9010" : "\u53CC\u7EBF\u7A0B\u8DE8\u6838\u5524\u9192\u5F80\u8FD4"; }
                _clockUse.Text = use;
                _clockUse.Foreground = T.S(tint);
                _clockEta.Text = eta;
                return;
            }

            switch (_phase)
            {
                case Phase.Running:
                    use = "\u5DF2\u7528 " + (tenth / 10.0).ToString("0.0") + " S";
                    // 没有样本时窗口照样在走，但没有可估的产出：写"计时中"，不画假剩余。
                    eta = _live.Count == 0
                        ? "\u8BA1\u65F6\u4E2D \u00B7 \u5C1A\u672A\u53D6\u5230\u6837\u672C"
                        : "\u9884\u8BA1\u5269\u4F59 " + (WindowSec - tenth / 10.0).ToString("0.0") + " S \u00B7 \u5DF2\u91C7 "
                          + _live.Count.ToString("#,##0") + " \u6837\u672C";
                    tint = T.Hi;
                    break;
                case Phase.Done:
                    use = "\u5DF2\u7528 8.0 S \u00B7 \u5B8C\u6210";
                    eta = "\u6837\u672C " + (_cur != null ? _cur.Count.ToString("#,##0") : "\u2014") + " \u00B7 \u5DF2\u5199\u5165\u5386\u53F2";
                    break;
                case Phase.Aborted:
                    use = "\u5DF2\u4E2D\u6B62 " + (tenth / 10.0).ToString("0.0") + " S";
                    eta = "\u672A\u5199\u5165\u5386\u53F2 \u00B7 \u4E0D\u53C2\u4E0E\u5224\u5B9A";
                    tint = T.Warn;
                    break;
                case Phase.Failed:
                    use = "\u7A97\u53E3\u8DD1\u6EE1 \u00B7 \u4E0D\u53EF\u7528";
                    eta = "\u6837\u672C\u4E0D\u8DB3 " + MinSamples + " \u00B7 \u672C\u6B21\u4E0D\u5199\u5165\u5386\u53F2";
                    tint = T.Warn;
                    break;
                default:
                    use = "\u5F85\u91C7\u6837";
                    eta = "\u7A97\u53E3 8.0 S \u00B7 \u81F3\u5C11 " + MinSamples + " \u6837\u672C";
                    break;
            }

            _clockUse.Text = use;
            _clockUse.Foreground = T.S(tint);
            _clockEta.Text = eta;
        }

        // ====================================================================
        //  图表
        // ====================================================================
        private void PaintChart()
        {
            if (_mode != "wake") { PaintModeViz(); return; }
            if (_hist == null) return;

            if (_phase == Phase.Running)
            {
                if (_live.Count >= 2 && _live.Sigma > 0)
                {
                    _hist.SetData(_sortBuf, _sortN, _live.P50, _live.P95, _live.P99, _live.Mean);
                    _chartMeta.Text = "LIVE \u00B7 N " + _live.Count.ToString("#,##0") + " \u00B7 P99 " + _live.P99.ToString("#,##0");
                    _chartFoot.Text = FootText(_live, true);
                }
                else
                {
                    _hist.SetEmpty("\u6B63\u5728\u5EFA\u7ACB\u6837\u672C\u96C6 \u00B7 \u81F3\u5C11\u9700\u8981 2 \u4E2A\u4E0D\u540C\u6837\u672C");
                    _chartMeta.Text = "LIVE \u00B7 N " + _live.Count.ToString("#,##0");
                    _chartFoot.Text = "\u91C7\u96C6\u4E2D \u00B7 \u5206\u5E03\u5728\u6837\u672C\u4E0D\u8DB3\u65F6\u4E0D\u753B";
                }
                return;
            }

            RunData show = _chartRun;

            if (show == null)
            {
                _hist.SetEmpty("\u5B8C\u6210\u4E00\u6B21 8.0 \u79D2\u91C7\u6837\u540E\uFF0C\u8FD9\u91CC\u6309\u5B9E\u6D4B\u6837\u672C\u5206\u7BB1");
                _chartMeta.Text = "NO DATA";
                _chartFoot.Text = "\u6837\u672C \u2014 \u00B7 \u5206\u7BB1 48 \u00B7 \u57DF \u2014 \u00B7 OUT OF RANGE \u2014 \u00B7 FAR-OUT \u2014";
                return;
            }

            if (show.Sorted == null)
            {
                _hist.SetEmpty("\u8BE5\u5386\u53F2\u8BB0\u5F55\u53EA\u4FDD\u5B58\u4E86\u805A\u5408\u8BFB\u6570\uFF0C\u6CA1\u6709\u6837\u672C\u660E\u7EC6");
                _chartMeta.Text = MetaText(show);
                _chartFoot.Text = "P50 " + show.P50.ToString("#,##0") + " \u00B7 P99 " + show.P99.ToString("#,##0")
                    + " \u00B7 MAX " + show.Max.ToString("#,##0") + " \u00B5s \u00B7 "
                    + show.Count.ToString("#,##0") + " \u6837\u672C \u00B7 \u5386\u53F2\u6863\u6848\u4E0D\u4FDD\u5B58\u6837\u672C\u660E\u7EC6";
                return;
            }

            Stats st = show.St;
            _hist.SetData(show.Sorted, show.Count, st.P50, st.P95, st.P99, st.Mean);
            _chartMeta.Text = MetaText(show);
            _chartFoot.Text = FootText(st, false);
        }

        private static string MetaText(RunData r)
        {
            return "N " + r.Count.ToString("#,##0") + " \u00B7 P99 " + r.P99.ToString("#,##0") + " \u00B7 " + ShortTime(r.Time);
        }

        private static string ShortTime(string t)
        {
            if (t == null || t.Length < 16) return t == null ? "" : t;
            return t.Substring(5, 11);
        }

        /// <summary>图表脚注：先说轴，再说分箱与域，最后说计数——读者可以逐项复核。</summary>
        private string FootText(Stats st, bool live)
        {
            if (st == null || st.Count < 2) return "";
            string head = (live ? "\u5B9E\u65F6 \u00B7 " : "") + "X \u5524\u9192\u8D85\u8C03 \u00B5s \u00B7 Y \u6837\u672C\u6570 \u00B7 \u6837\u672C " + st.Count.ToString("#,##0");
            if (_hist == null || !_hist.Ready)
                return head + " \u00B7 \u96F6\u65B9\u5DEE\uFF08\u5168\u90E8\u6837\u672C\u76F8\u540C\uFF09\u00B7 \u4E0D\u5206\u7BB1 \u00B7 FAR-OUT " + st.FarOut.ToString("#,##0")
                    + " \u00B7 \u5747\u503C " + st.Mean.ToString("#,##0") + " \u00B5s";
            return head
                + " \u00B7 \u5206\u7BB1 48 \u00D7 " + _hist.BinWidth.ToString("0.#") + " \u00B5s"
                + " \u00B7 \u57DF " + _hist.Lo.ToString("#,##0") + " \u2013 " + _hist.Hi.ToString("#,##0") + " \u00B5s"
                + " \u00B7 OUT OF RANGE " + _hist.Clipped.ToString("#,##0")
                + " \u00B7 FAR-OUT " + st.FarOut.ToString("#,##0")
                + (_hist.ZoomActive
                    ? " \u00B7 \u5BC6\u96C6\u533A\u653E\u5927 " + _hist.ZoomLo.ToString("#,##0") + " \u2013 " + _hist.ZoomHi.ToString("#,##0") + " \u00B5s"
                    : "");
        }

        private DispatcherTimer _poll;
    }
}