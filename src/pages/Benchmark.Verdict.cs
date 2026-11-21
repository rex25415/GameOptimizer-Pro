// ============================================================================
//  Benchmark.Verdict.cs — 一句话结论（Benchmark 分片）
//
//  判定不是一个形容词，是一个可被推翻的陈述：
//   "本次 P99 较上次 −0.6%，落在噪声阈值 ±1.5% 内 → 视为持平"
//  阈值来自同机历史运行 P99 的相对标准差（样本标准差，n−1），
//  两次独立运行之差的置信尺度取 √2·σ。样本不足（<3 次）时不下结论，
//  而不是拿一个默认值假装有把握。
//
//  Rev86 把"判什么"和"怎么画"拆开：
//     Judge()      只产出一句结论 + 一个色调（纯数据，不碰 WPF）
//     PaintVerdict 只负责把结论贴上去，并在原有的四格证据里补上 σ_rel
//   旧版把两件事写在一个 164 行的函数里，于是每加一种状态就得再嵌一层 if。
//   固定不变的说明文字（METHOD / 口径）改在 Build 期建一次，不再每次判定重建。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Benchmark
    {
        private ContentControl _verdictHead;
        private TextBlock _evThis, _evLast, _evDelta, _evSigma, _evBand;
        private TextBlock _evKey0, _evKey1, _method1, _method2;

        // ---- 判定结果 -------------------------------------------------------
        // 一个 struct 而不是四个 out 参数：调用方只想拿到"结论 + 依据"，
        // 不想知道它内部是怎么算出来的。
        private struct Verdict
        {
            public uint Tint;
            public string Chip;
            public string Sentence;
            public string Sub;
        }

        private UIElement BuildVerdictPanel(Core c)
        {
            StackPanel body = new StackPanel();

            _verdictHead = new ContentControl();
            body.Children.Add(_verdictHead);

            body.Children.Add(Gap(13));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));

            TextBlock tag = Ui.Label("METHOD", T.Mute);
            tag.Margin = new Thickness(0, 0, 0, 5);
            body.Children.Add(tag);

            _method1 = Method("\u566A\u58F0\u9608\u503C = \u221A2 \u00B7 \u03C3_rel\uFF1B\u03C3_rel \u662F\u6700\u8FD1 N \u6B21\u540C\u53E3\u5F84\u8FD0\u884C\u4E3B\u6307\u6807\u7684\u76F8\u5BF9\u6807\u51C6\u5DEE\uFF08n\u22121\uFF09\uFF0C"
                + "\u6837\u672C\u5C11\u4E8E 3 \u6B21\u65F6\u4E0D\u4E0B\u7ED3\u8BBA\u3002");
            _method2 = Method("\u5524\u9192\u8D85\u8C03 = Thread.Sleep(1) \u5B9E\u9645\u5524\u9192\u65F6\u95F4 \u2212 1 ms\uFF0C\u53CD\u6620\u7CFB\u7EDF\u8C03\u5EA6\u4E0E\u5B9A\u65F6\u5668\u9897\u7C92\u5EA6\uFF1B"
                + "\u8BFB\u6570\u5168\u90E8\u6765\u81EA\u672C\u6B21\u6837\u672C\u3002");
            body.Children.Add(_method1);
            body.Children.Add(_method2);

            body.Children.Add(Gap(13));
            body.Children.Add(Ui.Cap(T.LineSoft, Ui.Wash(T.Accent, 0x44), 18));
            body.Children.Add(Gap(10));
            body.Children.Add(BuildEvidence());
            return Ui.Panel(body, T.PadPanel);
        }

        /// <summary>说明性段落：建一次就够，判定不重建它（判定只改数值格子）。</summary>
        private static TextBlock Method(string text)
        {
            TextBlock t = Ui.Tx(text, T.FsMicro, T.Mute, T.FCn);
            t.TextWrapping = TextWrapping.Wrap;
            t.LineHeight = 16;
            t.Margin = new Thickness(0, 4, 0, 0);
            return t;
        }

        // ---- 证据链：结论用到的每个数都摆出来，读者可以自己复核 -------------
        private UIElement BuildEvidence()
        {
            Grid ev = new Grid();
            for (int i = 0; i < 5; i++) ev.ColumnDefinitions.Add(new ColumnDefinition());
            _evThis = EvCell(ev, 0, "THIS P99 \u00B5s", out _evKey0);
            _evLast = EvCell(ev, 1, "BASE P99 \u00B5s", out _evKey1);
            TextBlock dummy;
            _evDelta = EvCell(ev, 2, "DELTA", out dummy);
            _evSigma = EvCell(ev, 3, "\u03C3 REL", out dummy);
            _evBand = EvCell(ev, 4, "NOISE BAND", out dummy);
            return ev;
        }

        private static TextBlock EvCell(Grid host, int col, string key, out TextBlock keyTb)
        {
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(col == 0 ? 0 : 14, 0, 0, 0);
            keyTb = Ui.Mono(key, T.FsNano, T.Mute);
            sp.Children.Add(keyTb);
            TextBlock v = Ui.Mono("\u2014", T.FsBody, T.Mute);
            v.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(v);
            Grid.SetColumn(sp, col);
            host.Children.Add(sp);
            return v;
        }

        private void UpdateVerdictModeChrome()
        {
            if (_method2 != null)
            {
                if (_mode == "mem")
                    _method2.Text = "\u987A\u5E8F\u62F7\u8D1D\u5E26\u5BBD (MB/s) \u8D8A\u9AD8\u8D8A\u597D\uFF0C\u968F\u673A\u6307\u9488\u8FFD\u9010\u5EF6\u8FDF (ns) \u8D8A\u4F4E\u8D8A\u597D\uFF1B\u4EC5\u4E0E\u540C\u6A21\u5F0F\u57FA\u7EBF\u6BD4\u8F83\u3002";
                else if (_mode == "pingpong")
                    _method2.Text = "\u7EBF\u7A0B\u8C03\u5EA6\u4E52\u4E53 = \u53CC\u7EBF\u7A0B\u4EA4\u66FF\u5524\u9192\u5F80\u8FD4 P99 \u5EF6\u8FDF (\u00B5s)\uFF0C\u8D8A\u4F4E\u8D8A\u7A33\uFF1B\u4EC5\u4E0E\u540C\u6A21\u5F0F\u57FA\u7EBF\u6BD4\u8F83\u3002";
                else
                    _method2.Text = "\u5524\u9192\u8D85\u8C03 = Thread.Sleep(1) \u5B9E\u9645\u5524\u9192\u65F6\u95F4 \u2212 1 ms\uFF0C\u53CD\u6620\u7CFB\u7EDF\u8C03\u5EA6\u4E0E\u5B9A\u65F6\u5668\u9897\u7C92\u5EA6\uFF1B\u8BFB\u6570\u5168\u90E8\u6765\u81EA\u672C\u6B21\u6837\u672C\u3002";
            }
            if (_evKey0 != null) _evKey0.Text = _mode == "mem" ? "THIS COPY MB/s" : "THIS P99 \u00B5s";
            if (_evKey1 != null) _evKey1.Text = _mode == "mem" ? "BASE COPY MB/s" : "BASE P99 \u00B5s";
        }

        // ---- 判定 ------------------------------------------------------------
        private void PaintVerdict()
        {
            if (_verdictHead == null) return;

            UpdateVerdictModeChrome();
            List<RunData> same = ModeRuns(_mode);
            RunData cur = _phase == Phase.Running ? null : ActiveRunForMode(same);
            RunData cmp = CompareBaseForMode(same, cur);

            double sig;
            int nz;
            NoiseRel(out sig, out nz);
            double thr = sig * 1.41421356;

            Verdict v = Judge(cur, cmp, thr, nz);
            string grade = null;
            uint gradeTint = T.Dim;
            string gradeTip = null;
            ComputeGrade(cur, out grade, out gradeTint, out gradeTip);
            _verdictHead.Content = BuildVerdictHead(v.Tint, v.Chip, v.Sentence, v.Sub, grade, gradeTint, gradeTip);
            if (_heroGradeBox != null && _heroGradeLetter != null)
            {
                if (!string.IsNullOrEmpty(grade))
                {
                    int sep = grade.IndexOf('\u00B7');
                    string lt = sep > 0 ? grade.Substring(0, sep).Trim() : grade;
                    string sb = sep > 0 && sep + 1 < grade.Length ? grade.Substring(sep + 1).Trim() : "";
                    _heroGradeLetter.Text = lt;
                    _heroGradeLetter.Foreground = T.S(gradeTint);
                    if (_heroGradeSub != null) { _heroGradeSub.Text = sb; _heroGradeSub.Foreground = T.S(T.Dim); }
                    _heroGradeBox.BorderBrush = T.S(Ui.Wash(gradeTint, 0x6E));
                    _heroGradeBox.Background = T.S(Ui.Wash(gradeTint, 0x1A));
                    if (!string.IsNullOrEmpty(gradeTip)) Tip.Attach(_heroGradeBox, gradeTip);
                }
                else
                {
                    _heroGradeLetter.Text = "\u2014";
                    _heroGradeLetter.Foreground = T.S(T.Mute);
                    if (_heroGradeSub != null) _heroGradeSub.Text = "";
                }
            }
            SetEvidence(cur, cmp, thr, sig, nz);
        }

        /// <summary>透明的综合档位评估：严格依据本次实测主指标落区间，阈值全部在悬停说明中公开。</summary>
        private void ComputeGrade(RunData cur, out string grade, out uint tint, out string tip)
        {
            grade = null;
            tint = T.Dim;
            tip = null;
            if (cur == null) return;
            double v = PrimaryValue(cur, _mode);
            if (!(v > 0)) return;
            if (_mode == "wake")
            {
                if (v <= 1000) { grade = "S \u7EA7 \u00B7 \u6781\u4F4E\u6296\u52A8"; tint = T.Ok; }
                else if (v <= 2500) { grade = "A \u7EA7 \u00B7 \u826F\u597D"; tint = T.Accent; }
                else if (v <= 8000) { grade = "B \u7EA7 \u00B7 \u4E2D\u7B49"; tint = T.Warn; }
                else { grade = "C \u7EA7 \u00B7 \u9ED8\u8BA4\u9897\u7C92\u5EA6"; tint = T.Dim; }
                tip = "\u5524\u9192\u8D85\u8C03 P99 \u5206\u6863\uFF1AS \u2264 1,000 \u00B5s \u00B7 A \u2264 2,500 \u00B5s \u00B7 B \u2264 8,000 \u00B5s \u00B7 C > 8,000 \u00B5s\uFF08\u672A\u5F00 0.5ms \u5B9A\u65F6\u5668\u65F6\u7CFB\u7EDF\u9ED8\u8BA4\u843D\u5728 C \u6863\u5C5E\u6B63\u5E38\uFF09\u3002";
            }
            else if (_mode == "pingpong")
            {
                if (v <= 40) { grade = "S \u7EA7 \u00B7 \u6781\u901F\u8C03\u5EA6"; tint = T.Ok; }
                else if (v <= 120) { grade = "A \u7EA7 \u00B7 \u826F\u597D"; tint = T.Accent; }
                else if (v <= 300) { grade = "B \u7EA7 \u00B7 \u4E00\u822C"; tint = T.Warn; }
                else { grade = "C \u7EA7 \u00B7 \u504F\u6162"; tint = T.Bad; }
                tip = "\u7EBF\u7A0B\u4E52\u4E53 P99 \u5206\u6863\uFF1AS \u2264 40 \u00B5s \u00B7 A \u2264 120 \u00B5s \u00B7 B \u2264 300 \u00B5s \u00B7 C > 300 \u00B5s\u3002";
            }
            else if (_mode == "mem")
            {
                if (v >= 20000) { grade = "S \u7EA7 \u00B7 \u9AD8\u5E26\u5BBD"; tint = T.Ok; }
                else if (v >= 12000) { grade = "A \u7EA7 \u00B7 \u826F\u597D"; tint = T.Accent; }
                else if (v >= 6000) { grade = "B \u7EA7 \u00B7 \u4E2D\u7B49"; tint = T.Warn; }
                else { grade = "C \u7EA7 \u00B7 \u504F\u4F4E"; tint = T.Dim; }
                tip = "\u5355\u7EBF\u7A0B\u987A\u5E8F\u62F7\u8D1D\u5E26\u5BBD\u5206\u6863\uFF1AS \u2265 20 GB/s \u00B7 A \u2265 12 GB/s \u00B7 B \u2265 6 GB/s\u3002";
            }
        }

        /// <summary>
        /// 判定语义：只回答"能不能下结论"，以及"是改善还是劣化"。
        /// 阈值边缘不硬判：|Δ| 只比噪声带宽一点点的时候，"改善/劣化"其实是噪声。
        /// </summary>
        private string ModeScopeText()
        {
            if (_mode == "mem") return "\u987A\u5E8F\u62F7\u8D1D\u5E26\u5BBD (MB/s)";
            if (_mode == "pingpong") return "\u53CC\u7EBF\u7A0B\u5F80\u8FD4 P99 (\u00B5s)";
            return "8.0 \u79D2\u7A97\u53E3\u00B7\u5524\u9192\u8D85\u8C03 P99 (\u00B5s)";
        }

        private Verdict Judge(RunData cur, RunData prev, double thr, int nz)
        {
            Verdict v = new Verdict();
            bool isWake = _mode == "wake";

            if (_phase == Phase.Running)
            {
                v.Tint = T.Accent;
                v.Chip = "\u8FDB\u884C\u4E2D";
                v.Sentence = isWake
                    ? "\u91C7\u6837\u8FDB\u884C\u4E2D \u00B7 \u7A97\u53E3\u8DD1\u6EE1\u540E\u624D\u4F1A\u4E0B\u7ED3\u8BBA\u3002"
                    : "\u5FAE\u57FA\u51C6\u8FD0\u884C\u4E2D \u00B7 \u540E\u53F0\u91C7\u96C6\u5B8C\u6210\u540E\u624D\u4E0B\u7ED3\u8BBA\u3002";
                v.Sub = isWake
                    ? "\u5224\u5B9A\u53EA\u7528\u5B8C\u6574\u7684 8.0 \u79D2\u7A97\u53E3\uFF0C\u4E2D\u9014\u4E0D\u9884\u6D4B\uFF1B\u5B9E\u65F6\u6837\u672C\u6570\u5728\u4E0A\u65B9\u8FD0\u884C\u949F\u91CC\u3002"
                    : "\u5F53\u524D\u6B63\u5728\u540E\u53F0\u8FD0\u884C\u771F\u5B9E\u5DE5\u4F5C\u8D1F\u8F7D\uFF0C\u5B8C\u6210\u540E\u4EC5\u4E0E\u540C\u6A21\u5F0F\u57FA\u7EBF\u5BF9\u6BD4\u3002";
                return v;
            }
            if (cur == null || PrimaryValue(cur, _mode) <= 0)
            {
                v.Tint = _phase == Phase.Aborted ? T.Warn : (_phase == Phase.Failed ? T.Warn : T.Dim);
                v.Chip = _phase == Phase.Aborted ? "\u5DF2\u4E2D\u6B62" : (_phase == Phase.Failed ? "\u4E0D\u53EF\u7528" : (isWake ? "\u5F85\u91C7\u6837" : "\u5F85\u6D4B\u91CF"));
                v.Sentence = _phase == Phase.Aborted ? "\u672C\u6B21\u8FD0\u884C\u88AB\u4E2D\u6B62 \u00B7 \u4E0D\u4E0B\u7ED3\u8BBA\u3002"
                    : (_phase == Phase.Failed ? "\u672C\u6B21\u6CA1\u6709\u53D6\u5230\u6709\u6548\u6837\u672C \u00B7 \u4E0D\u4E0B\u7ED3\u8BBA\u3002"
                                              : "\u5C1A\u672A\u8FD0\u884C \u00B7 \u4E0D\u4E0B\u7ED3\u8BBA\u3002");
                v.Sub = _phase == Phase.Aborted
                    ? "\u53EA\u6709\u5B8C\u6574\u8DD1\u5B8C\u7684\u6837\u672C\u624D\u8FDB\u5165\u5BF9\u6BD4\u4E0E\u5224\u5B9A\uFF1B\u4E2D\u65AD\u6B8B\u6B21\u4E0D\u5165\u8D26\u3002"
                    : (_phase == Phase.Failed
                        ? "\u8BF7\u68C0\u67E5\u7CFB\u7EDF\u8D1F\u8F7D\u6216\u91C7\u6837\u5668\u72B6\u6001\u540E\u91CD\u65B0\u8FD0\u884C\u3002"
                        : "\u70B9\u51FB\u53F3\u4E0A\u5F00\u59CB\u6D4B\u91CF\uFF1B\u7ED3\u8BBA\u81F3\u5C11\u9700\u8981\u4E24\u6B21\u540C\u53E3\u5F84\uFF08" + ModeScopeText() + "\uFF09\u8FD0\u884C\u3002");
                return v;
            }
            if (prev == null || PrimaryValue(prev, _mode) <= 0)
            {
                v.Tint = T.Dim;
                v.Chip = "\u9996\u6B21";
                v.Sentence = "\u5F53\u524D\u6A21\u5F0F\u4EC5\u4E00\u6761\u6709\u6548\u6837\u672C \u00B7 \u6CA1\u6709\u53EF\u6BD4\u8F83\u7684\u540C\u53E3\u5F84\u57FA\u7EBF\uFF0C\u4E0D\u4E0B\u7ED3\u8BBA\u3002";
                v.Sub = "\u5728\u540C\u6A21\u5F0F\u4E0B\u518D\u8DD1\u4E00\u6B21\u5373\u53EF\u5BF9\u6BD4 THIS vs BASE \u5E76\u7D2F\u79EF\u540C\u673A\u566A\u58F0\u6837\u672C\u3002";
                return v;
            }
            if (nz < 3)
            {
                v.Tint = T.Dim;
                v.Chip = "\u6837\u672C\u4E0D\u8DB3";
                v.Sentence = "\u540C\u6A21\u5F0F\u540C\u673A\u566A\u58F0\u6837\u672C\u4E0D\u8DB3\uFF08" + nz + " \u6B21\uFF0C\u9700 \u2265 3\uFF09\u00B7 \u4E0D\u4E0B\u7ED3\u8BBA\u3002";
                v.Sub = "\u566A\u58F0\u9608\u503C\u9700\u540C\u6A21\u5F0F\u591A\u6B21\u8FD0\u884C\u7684\u76F8\u5BF9\u6807\u51C6\u5DEE\u8BA1\u7B97\uFF1B\u4E0B\u65B9\u8BC1\u636E\u6761\u5DF2\u5217\u51FA THIS vs BASE \u539F\u59CB\u5DEE\u503C\u3002";
                return v;
            }
            return JudgeDelta(cur, prev, thr);
        }

        private Verdict JudgeDelta(RunData cur, RunData prev, double thr)
        {
            Verdict v = new Verdict();
            double vCur = PrimaryValue(cur, _mode);
            double vPrev = PrimaryValue(prev, _mode);
            double d = (vCur - vPrev) / vPrev * 100.0;
            bool hib = HigherIsBetter(_mode);
            bool improved = hib ? d > 0 : d < 0;
            double ratio = thr > 0 ? Math.Abs(d) / thr : 99.0;
            string pct = Math.Abs(d).ToString("0.00") + "%";
            string sign = d < 0 ? "\u2212" : "+";
            string thrS = "\u00B1" + thr.ToString("0.00") + "%";
            string unit = _mode == "mem" ? " MB/s" : " \u00B5s";
            string metricName = _mode == "mem" ? "\u987A\u5E8F\u5E26\u5BBD" : "P99";
            string baseLabel = (_baseRun != null && _baseRun == prev) ? "\u6240\u9009\u57FA\u7EBF" : "\u4E0A\u4E00\u6B21";

            v.Sub = metricName + " " + FormatPrimary(vPrev, _mode) + " \u2192 " + FormatPrimary(vCur, _mode)
                + unit + " \u00B7 \u540C\u53E3\u5F84\u5BF9\u6BD4\uFF08" + ModeScopeText() + "\uFF09\u3002";

            if (ratio <= 1.0)
            {
                v.Tint = T.Dim;
                v.Chip = "\u566A\u58F0\u5185";
                v.Sentence = "\u672C\u6B21\u8F83" + baseLabel + " " + sign + pct + "\uFF0C\u843D\u5728\u566A\u58F0\u9608\u503C " + thrS + " \u5185 \u2014\u2014 \u89C6\u4E3A\u6301\u5E73\u3002";
            }
            else if (ratio < 1.30)
            {
                v.Tint = T.Dim;
                v.Chip = "\u8FB9\u7F18";
                v.Sentence = "\u672C\u6B21\u8F83" + baseLabel + " " + sign + pct + "\uFF0C\u4EC5\u9AD8\u4E8E\u566A\u58F0\u9608\u503C " + thrS
                    + "\uFF08" + ratio.ToString("0.00") + " \u00D7\uFF09\u2014\u2014 \u5904\u4E8E\u8FB9\u7F18\uFF0C\u4E0D\u4E0B\u7ED3\u8BBA\u3002";
            }
            else if (improved)
            {
                v.Tint = T.Ok;
                v.Chip = "\u6539\u5584";
                v.Sentence = "\u672C\u6B21 " + metricName + " \u8F83" + baseLabel + (hib ? "\u63D0\u5347 " : "\u4E0B\u964D ") + pct + "\uFF0C\u8D85\u8FC7\u566A\u58F0\u9608\u503C " + thrS + " \u2014\u2014 \u53EF\u5224\u5B9A\u4E3A\u771F\u5B9E\u6539\u5584\u3002";
            }
            else
            {
                v.Tint = ratio > 3.0 ? T.Bad : T.Warn;
                v.Chip = "\u52A3\u5316";
                v.Sentence = "\u672C\u6B21 " + metricName + " \u8F83" + baseLabel + (hib ? "\u4E0B\u964D " : "\u4E0A\u5347 ") + pct + "\uFF0C\u8D85\u8FC7\u566A\u58F0\u9608\u503C " + thrS + " \u2014\u2014 \u53EF\u5224\u5B9A\u4E3A\u771F\u5B9E\u52A3\u5316\u3002";
            }
            return v;
        }

        private void SetEvidence(RunData cur, RunData prev, double thr, double sig, int nz)
        {
            double vCur = PrimaryValue(cur, _mode);
            double vPrev = PrimaryValue(prev, _mode);
            SetEv(_evThis, cur != null ? FormatPrimary(vCur, _mode) : "\u2014");
            SetEv(_evLast, prev != null ? FormatPrimary(vPrev, _mode) : "\u2014");

            string d = "\u2014";
            if (cur != null && prev != null && vCur > 0 && vPrev > 0)
            {
                double p = (vCur - vPrev) / vPrev * 100.0;
                d = (p < 0 ? "\u2212" : "+") + Math.Abs(p).ToString("0.00") + "%";
            }
            SetEv(_evDelta, d);
            SetEv(_evSigma, nz >= 3 ? sig.ToString("0.00") + "%" : "\u2014");
            SetEv(_evBand, nz >= 3 ? "\u00B1" + thr.ToString("0.00") + "%" : "\u2014");
        }

        private static void SetEv(TextBlock tb, string text)
        {
            if (tb == null) return;
            tb.Text = text;
            tb.Foreground = T.S(text == "\u2014" ? T.Mute : T.Hi);
        }

        private static UIElement BuildVerdictHead(uint tint, string chip, string sentence, string sub,
                                                  string grade, uint gradeTint, string gradeTip)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            Grid icon = Ui.IconTile(Glyph.Target, 34, tint, Wash(tint), 6);
            icon.Margin = new Thickness(0, 0, 14, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            g.Children.Add(icon);

            StackPanel mid = new StackPanel();

            StackPanel line = new StackPanel();
            line.Orientation = Orientation.Horizontal;
            TextBlock t = Ui.Tx("\u5224\u5B9A", T.FsBody, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            line.Children.Add(t);
            Border cb = Chip.Make(chip, tint, T.ChipH);
            cb.Margin = new Thickness(10, 0, 0, 0);
            line.Children.Add(cb);
            if (!string.IsNullOrEmpty(grade))
            {
                Border gb = Chip.Make(grade, gradeTint, T.ChipH);
                gb.Margin = new Thickness(8, 0, 0, 0);
                if (!string.IsNullOrEmpty(gradeTip)) Tip.Attach(gb, gradeTip);
                line.Children.Add(gb);
            }
            mid.Children.Add(line);

            TextBlock s1 = Ui.Tx(sentence, T.FsTitle, T.Hi, T.FCn);
            s1.Margin = new Thickness(0, 7, 0, 0);
            s1.TextWrapping = TextWrapping.Wrap;
            s1.LineHeight = 21;
            mid.Children.Add(s1);

            TextBlock s2 = Ui.Tx(sub, T.FsMicro, T.Dim, T.FCn);
            s2.Margin = new Thickness(0, 6, 0, 0);
            s2.TextWrapping = TextWrapping.Wrap;
            s2.LineHeight = 16;
            mid.Children.Add(s2);

            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);
            return g;
        }

        private static uint Wash(uint tint)
        {
            if (tint == T.Ok) return T.OkWash;
            if (tint == T.Warn) return T.WarnWash;
            if (tint == T.Bad) return T.BadWash;
            if (tint == T.Accent) return T.AccentWash;
            if (tint == T.Dim) return 0x0CFFFFFF;
            return 0x0FFFFFFF;
        }

        /// <summary>
        /// 同机噪声：当前模式下最近 NoiseWindow 次运行（不含当前）主指标的相对标准差（%），分母 n−1。
        /// n 是可用的运行次数；n &lt; 3 时 σ 不可信，调用方必须写"不下结论"。
        /// </summary>
        private void NoiseRel(out double sigma, out int n)
        {
            sigma = 0;
            n = 0;

            List<RunData> same = ModeRuns(_mode);
            RunData active = _phase == Phase.Running ? null : ActiveRunForMode(same);
            List<double> vals = new List<double>();
            for (int i = 0; i < same.Count; i++)
            {
                if (same[i] == active) continue;
                double pv = PrimaryValue(same[i], _mode);
                if (pv > 0) vals.Add(pv);
            }

            int total = vals.Count;
            if (total <= 0) return;

            int start = Math.Max(0, total - NoiseWindow);
            int k = total - start;
            n = k;
            if (k < 3) return;

            double sum = 0;
            for (int i = start; i < total; i++) sum += vals[i];
            double mean = sum / k;
            if (!(mean > 0)) { n = 0; return; }

            double ss = 0;
            for (int i = start; i < total; i++)
            {
                double d = (vals[i] - mean) / mean * 100.0;
                ss += d * d;
            }
            sigma = Math.Sqrt(ss / (k - 1));
        }
    }
}
