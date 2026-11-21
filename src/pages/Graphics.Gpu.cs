// ============================================================================
//  Graphics.Gpu.cs — 图形栈 · GPU 实时占用（Graphics 分片）
//  读数来自 Kernel/GpuSampler（PDH GPU Engine / GPU Process Memory）。
//
//  Rev86：这里从"左边一个 60 号数字 + 右边一根尺"改成"尺面 + 事实"。
//  数值已经在上面的读数带里了，本面板只回答数值回答不了的问题：
//  0–100 的尺在哪、显存用掉总量的多少、引擎是哪一个。
//
//  三类"不可用"必须长得不一样（DESIGN-86 §1.5）：
//    · 预热中     → 尺面压暗 + 状态写"预热中"（还没到能下结论的时候）
//    · 驱动不支持 → 用 Ui.Empty 顶掉尺面，并写清"需要什么"（这是结论）
//    · 采样中断   → 压暗 + "正在重连"（曾经有过值，现在读不到）
//  只有 Available=true 才显示 0：真的 0 是一个测量结论，不能被"没测到"冒充。
//
//  判断依据不靠猜：新采样内核把每层失败写进 SampleHealth（gpu.open 打不开 =
//  计数器不存在），这里直接读它的 code / 重试倒计时；5 秒兜底只防"采样线程根本没起来"。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Graphics : Page
    {
        private const int GpuTrendN = 24;
        private const int GpuTrendBack = 9;

        private sealed class LuidCardRef
        {
            public string Luid = "";
            public TextBlock Peak;
            public TextBlock Val3D, ValCmp, ValCopy;
            public SegBar Bar3D, BarCmp, BarCopy;
            public TextBlock VramText;
            public SegBar VramBar;
            public int PeakKey = int.MinValue;
            public int Pct3DKey = int.MinValue;
            public int PctCmpKey = int.MinValue;
            public int PctCopyKey = int.MinValue;
            public long VramKey = long.MinValue;
        }

        private FrameworkElement _gpuLive, _gpuEmpty, _gpuFallback;
        private StackPanel _gpuCardsHost;
        private readonly List<LuidCardRef> _gpuCards = new List<LuidCardRef>();
        private string _gpuCardsSig = "";
        private Border _gpuStateDot;
        private TextBlock _gpuStateWord, _gpuStateDetail, _gpuMeta;
        private TextBlock _gpuEngine, _gpuMem, _gpuEngines;
        private TextBlock _gpuVramText;
        private SegBar _gpuBar, _gpuVramBar;

        /// <summary>呈现中适配器的显存总量（字节）；0 = 未知，此时不画比例尺。</summary>
        private long _vramTotal;

        private bool _gpuSeenLive;
        private int _gpuStateCode = -1;
        private int _gpuStateKey = int.MinValue;
        private int _gpuTier = -1;
        private int _gpuValKey = int.MinValue;
        private int _gpuEnginesKey = int.MinValue;
        private int _vramValKey = int.MinValue;
        private long _vramNoteKey = long.MinValue;
        private long _gpuMemKey = long.MinValue;

        private readonly double[] _hGpu = new double[GpuTrendN];
        private int _hGpuHead, _hGpuCount;
        private int _dGpu = int.MinValue;

        private static readonly string[] GpuWords = new string[]
            { "实时", "预热中", "驱动不支持", "采样中断" };
        private static readonly string[] GpuDetails = new string[]
        {
            "1 Hz · 整卡口径（最高引擎）",
            "首拍只建基线，约 1 秒",
            "需要 Win10 1709+ 与 WDDM2 驱动",
            "曾经有值，正在重连"
        };

        /// <summary>视觉树重建时清空全部 GPU 面板与读数缓存，防止新树命中旧键值而跳过绘制。</summary>
        private void ResetGpuState()
        {
            _gpuCards.Clear();
            _gpuCardsSig = "";
            _gpuSeenLive = false;
            _gpuStateCode = -1;
            _gpuStateKey = int.MinValue;
            _gpuTier = -1;
            _gpuValKey = int.MinValue;
            _gpuEnginesKey = int.MinValue;
            _vramValKey = int.MinValue;
            _vramNoteKey = long.MinValue;
            _gpuMemKey = long.MinValue;
            Array.Clear(_hGpu, 0, _hGpu.Length);
            _hGpuHead = 0;
            _hGpuCount = 0;
            _dGpu = int.MinValue;
        }

        // ====================================================================
        //  面板
        // ====================================================================
        private UIElement BuildGpuLive(Core c)
        {
            StackPanel body = new StackPanel();
            _gpuMeta = Ui.Mono("PDH \u2014", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("GPU 实时占用", "PDH PER-LUID ENGINE · 1HZ", _gpuMeta));
            body.Children.Add(Gap(12));

            body.Children.Add(BuildStateSpine());
            body.Children.Add(Gap(12));

            _gpuLive = BuildRulers();
            body.Children.Add(_gpuLive);

            UIElement empty = Ui.Empty(Glyph.Warn, "GPU 占用不可用",
                "PDH 的 GPU Engine / GPU Process Memory 计数器打不开或读不到。这套计数器需要 Windows 10 1709+ 与 WDDM2 驱动；" +
                "虚拟机与部分远程会话不提供。GPU 温度没有通用用户态接口，本工具不采集、也不显示假值。",
                T.Mute, null);
            _gpuEmpty = empty as FrameworkElement;
            if (_gpuEmpty != null) _gpuEmpty.Visibility = Visibility.Collapsed;
            body.Children.Add(empty);

            body.Children.Add(Gap(12));
            TextBlock note = Ui.Tx("按物理适配器 LUID 独立统计 3D / Compute / Copy 引擎负载与专有显存占用（双卡不再混算）；" +
                                   "各卡只统计归属自身 LUID 的真实 PDH 计数。",
                T.FsMicro, T.Mute, T.FCn);
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 17;
            body.Children.Add(note);

            return Ui.Panel(body, T.PadPanel);
        }

        /// <summary>状态行：一个点 + 一句状态 + 一句"为什么 / 什么时候再试"。</summary>
        private UIElement BuildStateSpine()
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;

            _gpuStateDot = new Border();
            _gpuStateDot.Width = 6;
            _gpuStateDot.Height = 6;
            _gpuStateDot.CornerRadius = new CornerRadius(3);
            _gpuStateDot.Background = T.S(T.Mute);
            _gpuStateDot.VerticalAlignment = VerticalAlignment.Center;
            _gpuStateDot.Margin = new Thickness(0, 0, 9, 0);
            sp.Children.Add(_gpuStateDot);

            _gpuStateWord = Ui.Tx("等待采样", T.FsSmall, T.Dim, T.FCn);
            _gpuStateWord.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(_gpuStateWord);

            _gpuStateDetail = Ui.Mono("\u2014", T.FsNano, T.Mute);
            _gpuStateDetail.Margin = new Thickness(12, 0, 0, 0);
            _gpuStateDetail.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(_gpuStateDetail);
            return sp;
        }

        /// <summary>尺面：按 LUID 独立渲染的逐卡实时遥测（未识别 LUID 时回退汇总尺）。</summary>
        private FrameworkElement BuildRulers()
        {
            StackPanel sp = new StackPanel();

            _gpuCardsHost = new StackPanel();
            _gpuCardsHost.Visibility = Visibility.Collapsed;
            sp.Children.Add(_gpuCardsHost);

            StackPanel fb = new StackPanel();
            _gpuFallback = fb;

            _gpuBar = new SegBar(0);
            _gpuBar.Height = 6;
            _gpuBar.Segments = 40;
            _gpuBar.Tint = T.Accent;
            fb.Children.Add(_gpuBar);

            Grid scale = new Grid();
            scale.Margin = new Thickness(0, 7, 0, 0);
            string[] marks = new string[] { "0", "25", "50", "75", "100%" };
            for (int i = 0; i < marks.Length; i++)
            {
                scale.ColumnDefinitions.Add(new ColumnDefinition());
                TextBlock m = Ui.Mono(marks[i], T.FsNano, T.Mute);
                m.Opacity = 0.8;
                if (i == 0) m.HorizontalAlignment = HorizontalAlignment.Left;
                else if (i == marks.Length - 1) m.HorizontalAlignment = HorizontalAlignment.Right;
                else m.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(m, i);
                scale.Children.Add(m);
            }
            fb.Children.Add(scale);

            _gpuVramText = Ui.Mono("显存 \u2014", T.FsNano, T.Mute);
            _gpuVramText.Margin = new Thickness(0, 16, 0, 0);
            fb.Children.Add(_gpuVramText);

            _gpuVramBar = new SegBar(0);
            _gpuVramBar.Height = 3;
            _gpuVramBar.Segments = 40;
            _gpuVramBar.Tint = T.Violet;
            _gpuVramBar.Margin = new Thickness(0, 6, 0, 0);
            _gpuVramBar.Visibility = Visibility.Collapsed;
            fb.Children.Add(_gpuVramBar);
            sp.Children.Add(fb);

            sp.Children.Add(Gap(14));
            sp.Children.Add(Ui.Rule(T.LineSoft, 0));
            sp.Children.Add(Gap(12));

            Grid facts = new Grid();
            for (int i = 0; i < 3; i++) facts.ColumnDefinitions.Add(new ColumnDefinition());
            facts.Children.Add(GpuFact("最高引擎", out _gpuEngine, 0));
            facts.Children.Add(GpuFact("主卡显存", out _gpuMem, 1));
            facts.Children.Add(GpuFact("引擎实例", out _gpuEngines, 2));
            sp.Children.Add(facts);
            return sp;
        }

        /// <summary>事实列：小标签在上、等宽读数在下。三列等宽，读者用竖线对齐去比较。</summary>
        private static StackPanel GpuFact(string label, out TextBlock val, int col)
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Ui.Label(label, T.Mute));
            val = Ui.Mono("\u2014", T.FsTitle, T.Text);
            val.Margin = new Thickness(0, 5, 0, 0);
            val.TextWrapping = TextWrapping.NoWrap;
            val.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(val);
            Grid.SetColumn(sp, col);
            return sp;
        }

        // ====================================================================
        //  刷新
        // ====================================================================
        private void RefreshGpu()
        {
            if (_gpuBar == null || C == null) return;
            GpuSnapshot s = C.Gpu.Current;
            int state = GpuState(s);
            ApplyGpuState(state);
            if (state == 2) { WriteGpuEmpty(); return; }

            WriteGpuBars(s);
            RenderAdapterCards(s);
            WriteGpuFacts(s);
            WriteGpuVram(s);
            WriteGpuCells(s, state);
        }

        /// <summary>
        /// 0 实时 / 1 预热 / 2 驱动不支持 / 3 采样中断。
        /// 先看健康账簿（gpu.open 打不开 = 计数器不存在），再用 5 秒兜底：
        /// 采样线程根本没起来时不会留下 Down，不能让界面永远停在"预热中"。
        /// </summary>
        private int GpuState(GpuSnapshot s)
        {
            if (s.Available) { _gpuSeenLive = true; return 0; }
            if (_gpuSeenLive) return 3;

            SampleHealth open = C != null ? C.Gpu.Health.At(0) : null;
            if (open != null && open.Down) return 2;
            if ((DateTime.UtcNow - _builtAt).TotalSeconds >= 5.0) return 2;
            return 1;
        }

        private void ApplyGpuState(int state)
        {
            SampleHealth open = C != null ? C.Gpu.Health.At(0) : null;
            bool down = open != null && open.Down;
            int retrySec = down ? (open.RetryInMs + 999) / 1000 : 0;
            int key = state * 1000000 + (down ? 100000 : 0) + retrySec;
            if (key == _gpuStateKey) return;

            bool changed = state != _gpuStateCode;
            _gpuStateKey = key;
            if (changed)
            {
                _gpuStateCode = state;
                uint tint = state == 0 ? T.Ok : (state == 3 ? T.Warn : T.Mute);
                _gpuStateDot.Background = T.S(tint);
                _gpuStateWord.Foreground = T.S(state == 0 ? T.Ok : (state == 3 ? T.Warn : T.Dim));
                Set(_gpuStateWord, GpuWords[state]);

                bool empty = state == 2;
                _gpuLive.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                if (_gpuEmpty != null) _gpuEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                _gpuLive.Opacity = (state == 1 || state == 3) ? 0.4 : 1.0;   // 压暗 = 这一版读数还不能用
                if (empty) Set(_gpuMeta, "PDH \u2014");
            }

            string detail = GpuDetails[state];
            if (down && state != 0)
                detail = "gpu.open 降级 · " + open.CodeText +
                         (retrySec > 0 ? " · " + retrySec + "s 后重试" : " · 可立即重试");
            Set(_gpuStateDetail, detail);
        }

        private void WriteGpuBars(GpuSnapshot s)
        {
            _gpuBar.Value = s.Available ? s.UtilPct / 100.0 : 0;
        }

        private void RenderAdapterCards(GpuSnapshot s)
        {
            if (_gpuCardsHost == null || _gpuFallback == null) return;
            bool hasCards = s.Adapters != null && s.AdapterCount > 0;
            _gpuCardsHost.Visibility = hasCards ? Visibility.Visible : Visibility.Collapsed;
            _gpuFallback.Visibility = hasCards ? Visibility.Collapsed : Visibility.Visible;
            if (!hasCards) return;

            string sig = "";
            for (int i = 0; i < s.AdapterCount; i++)
            {
                if (i > 0) sig += "|";
                GpuAdapterSample a = s.Adapters[i];
                sig += (a != null && !string.IsNullOrEmpty(a.Luid)) ? a.Luid : i.ToString();
            }
            if (sig != _gpuCardsSig)
            {
                _gpuCardsSig = sig;
                BuildAdapterCards(s);
            }
            for (int i = 0; i < s.AdapterCount && i < _gpuCards.Count; i++)
                UpdateAdapterCard(_gpuCards[i], s.Adapters[i]);
        }

        private void BuildAdapterCards(GpuSnapshot s)
        {
            _gpuCards.Clear();
            _gpuCardsHost.Children.Clear();
            for (int i = 0; i < s.AdapterCount; i++)
            {
                GpuAdapterSample a = s.Adapters[i];
                if (a == null) continue;
                LuidCardRef r = BuildSingleLuidCard(a);
                _gpuCards.Add(r);
                Border shell = r.Peak.Tag as Border;
                if (shell != null)
                {
                    if (i > 0) shell.Margin = new Thickness(0, 10, 0, 0);
                    _gpuCardsHost.Children.Add(shell);
                }
            }
        }

        private static LuidCardRef BuildSingleLuidCard(GpuAdapterSample a)
        {
            LuidCardRef r = new LuidCardRef();
            r.Luid = a.Luid ?? "";

            StackPanel inner = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;

            string title = !string.IsNullOrEmpty(a.ShortName) ? a.ShortName : a.DriverDesc;
            if (string.IsNullOrEmpty(title)) title = "GPU Adapter";
            TextBlock name = Ui.Tx(title, T.FsBody, T.Hi, T.FCn);
            name.FontWeight = FontWeights.SemiBold;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(name);

            Border kindTag = Ui.Tag(a.Discrete ? "独显" : "核显", a.Discrete ? T.Accent : T.SeriesA);
            kindTag.Margin = new Thickness(8, 0, 0, 0);
            kindTag.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(kindTag);

            if (!string.IsNullOrEmpty(a.Luid))
            {
                TextBlock luidTb = Ui.Mono("LUID " + a.Luid, T.FsNano, T.Mute);
                luidTb.Margin = new Thickness(10, 0, 0, 0);
                luidTb.VerticalAlignment = VerticalAlignment.Center;
                left.Children.Add(luidTb);
            }
            head.Children.Add(left);

            r.Peak = Ui.Mono("\u2014", T.FsSmall, T.Hi);
            r.Peak.FontWeight = FontWeights.SemiBold;
            r.Peak.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(r.Peak, 1);
            head.Children.Add(r.Peak);
            inner.Children.Add(head);

            Grid engines = new Grid();
            engines.Margin = new Thickness(0, 10, 0, 0);
            for (int c = 0; c < 3; c++) engines.ColumnDefinitions.Add(new ColumnDefinition());
            engines.Children.Add(EngineMeter(0, "3D", T.Accent, out r.Val3D, out r.Bar3D));
            engines.Children.Add(EngineMeter(1, "Compute", T.Ok, out r.ValCmp, out r.BarCmp));
            engines.Children.Add(EngineMeter(2, "Copy", T.Warn, out r.ValCopy, out r.BarCopy));
            inner.Children.Add(engines);

            r.VramText = Ui.Mono("专有显存 \u2014", T.FsNano, T.Dim);
            r.VramText.Margin = new Thickness(0, 10, 0, 0);
            inner.Children.Add(r.VramText);

            r.VramBar = new SegBar(0);
            r.VramBar.Height = 3;
            r.VramBar.Segments = 32;
            r.VramBar.Tint = T.Violet;
            r.VramBar.Margin = new Thickness(0, 5, 0, 0);
            r.VramBar.Visibility = Visibility.Collapsed;
            inner.Children.Add(r.VramBar);

            Border card = new Border();
            card.Background = T.S(T.Inset);
            card.BorderBrush = T.S(T.LineSoft);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(8, 9, 12, 9);
            card.CornerRadius = new CornerRadius(T.RadiusSm);
            Grid wrap = new Grid();
            wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            wrap.ColumnDefinitions.Add(new ColumnDefinition());
            Border accent = new Border();
            accent.Background = T.S(a.Discrete ? T.Accent : T.SeriesA);
            accent.CornerRadius = new CornerRadius(1);
            wrap.Children.Add(accent);
            inner.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(inner, 1);
            wrap.Children.Add(inner);
            card.Child = wrap;
            r.Peak.Tag = card;
            return r;
        }

        private static UIElement EngineMeter(int col, string label, uint tint, out TextBlock val, out SegBar bar)
        {
            StackPanel sp = new StackPanel();
            if (col < 2) sp.Margin = new Thickness(0, 0, 14, 0);

            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock nm = Ui.Mono(label, T.FsNano, T.Dim);
            top.Children.Add(nm);

            val = Ui.Mono("\u2014", T.FsNano, T.Hi);
            Grid.SetColumn(val, 1);
            top.Children.Add(val);
            sp.Children.Add(top);

            bar = new SegBar(0);
            bar.Height = 4;
            bar.Segments = 20;
            bar.Tint = tint;
            bar.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(bar);

            Grid.SetColumn(sp, col);
            return sp;
        }

        private static void UpdateAdapterCard(LuidCardRef r, GpuAdapterSample a)
        {
            if (r == null || a == null) return;
            if (!a.Available)
            {
                if (r.PeakKey != -1) { r.PeakKey = -1; r.Peak.Text = "\u2014"; r.Peak.Foreground = T.S(T.Mute); }
                SetEngineMeter(ref r.Pct3DKey, r.Val3D, r.Bar3D, false, 0);
                SetEngineMeter(ref r.PctCmpKey, r.ValCmp, r.BarCmp, false, 0);
                SetEngineMeter(ref r.PctCopyKey, r.ValCopy, r.BarCopy, false, 0);
            }
            else
            {
                int peak = (int)Math.Round(a.MaxUtilPct);
                if (peak != r.PeakKey)
                {
                    r.PeakKey = peak;
                    r.Peak.Text = "峰值 " + peak + "%";
                    r.Peak.Foreground = T.S(peak > 85 ? T.Warn : T.Hi);
                }
                SetEngineMeter(ref r.Pct3DKey, r.Val3D, r.Bar3D, true, a.Load3D);
                SetEngineMeter(ref r.PctCmpKey, r.ValCmp, r.BarCmp, true, a.LoadCompute);
                SetEngineMeter(ref r.PctCopyKey, r.ValCopy, r.BarCopy, true, a.LoadCopy);
            }

            if (!a.MemKnown)
            {
                if (r.VramKey != -1)
                {
                    r.VramKey = -1;
                    r.VramText.Text = "专有显存 · PDH 未提供";
                    r.VramBar.Visibility = Visibility.Collapsed;
                }
                return;
            }

            int usedMb = (int)Math.Round(a.VramUsedBytes / 1048576.0);
            if (a.VramTotalBytes <= 0)
            {
                long k = ((long)usedMb << 32) ^ -2L;
                if (k != r.VramKey)
                {
                    r.VramKey = k;
                    r.VramText.Text = "专有显存 " + usedMb + " MB · 总量未知";
                    r.VramBar.Visibility = Visibility.Collapsed;
                }
                return;
            }

            int totalMb = (int)Math.Round(a.VramTotalBytes / 1048576.0);
            double frac = a.VramUsedBytes / (double)a.VramTotalBytes;
            if (frac > 1.0) frac = 1.0;
            if (frac < 0.0) frac = 0.0;
            int pct = (int)Math.Round(frac * 100.0);
            long vkey = ((long)usedMb << 32) ^ (long)totalMb;
            if (vkey != r.VramKey)
            {
                r.VramKey = vkey;
                r.VramText.Text = "专有显存 " + usedMb + " / " + totalMb + " MB · " + pct + "%";
            }
            r.VramBar.Visibility = Visibility.Visible;
            r.VramBar.Value = frac;
        }

        private static void SetEngineMeter(ref int key, TextBlock val, SegBar bar, bool avail, double pctRaw)
        {
            if (!avail)
            {
                if (key != -1) { key = -1; val.Text = "\u2014"; }
                bar.Value = 0;
                bar.Opacity = 0.35;
                return;
            }
            int pct = (int)Math.Round(pctRaw);
            if (pct != key) { key = pct; val.Text = pct + "%"; }
            double frac = pctRaw / 100.0;
            if (frac > 1.0) frac = 1.0;
            if (frac < 0.0) frac = 0.0;
            bar.Opacity = 1.0;
            bar.Value = frac;
        }

        private static GpuAdapterSample PrimarySample(GpuSnapshot s)
        {
            if (s.Adapters == null || s.AdapterCount <= 0) return null;
            GpuAdapterSample best = null;
            for (int i = 0; i < s.AdapterCount; i++)
            {
                GpuAdapterSample a = s.Adapters[i];
                if (a == null) continue;
                if (a.Discrete && a.Available) return a;
                if (best == null) best = a;
            }
            for (int i = 0; i < s.AdapterCount; i++)
            {
                GpuAdapterSample a = s.Adapters[i];
                if (a != null && a.Available && (best == null || !best.Available || a.MaxUtilPct > best.MaxUtilPct))
                    best = a;
            }
            return best;
        }

        private void WriteGpuFacts(GpuSnapshot s)
        {
            Set(_gpuEngine, s.Available && s.TopEngine.Length > 0 ? s.TopEngine : "\u2014");

            int engines = s.Available ? s.Engines : -1;
            int gpus = s.AdapterCount;
            int metaKey = engines * 16 + gpus;
            if (metaKey != _gpuEnginesKey)
            {
                _gpuEnginesKey = metaKey;
                _gpuEngines.Text = engines < 0 ? "\u2014" : engines.ToString();
                if (engines > 0)
                    _gpuMeta.Text = gpus > 0 ? ("PDH " + gpus + " GPU · " + engines + " ENG") : ("PDH " + engines + " ENG");
            }
        }

        private void WriteGpuVram(GpuSnapshot s)
        {
            GpuAdapterSample p = PrimarySample(s);
            bool memKnown = p != null ? p.MemKnown : s.MemKnown;
            long usedBytes = p != null ? p.VramUsedBytes : s.LocalMemBytes;
            // 所选卡有独立样本时必须以该卡自身 VramTotalBytes 为准，绝不借用独显 _vramTotal 算假百分比
            long totalBytes = p != null ? p.VramTotalBytes : _vramTotal;

            if (!memKnown)
            {
                if (_gpuMemKey != -1)
                {
                    _gpuMemKey = -1;
                    _gpuMem.Text = "\u2014";
                    _gpuVramText.Text = "显存 · PDH 未提供";
                }
                _gpuVramBar.Visibility = Visibility.Collapsed;
                return;
            }

            double usedMb = usedBytes / 1048576.0;
            int usedKey = (int)Math.Round(usedMb);
            if (totalBytes <= 0)
            {
                long unknownTotalKey = ((long)usedKey << 32) ^ -2L;
                if (_gpuMemKey != unknownTotalKey)
                {
                    _gpuMemKey = unknownTotalKey;
                    _gpuMem.Text = usedKey.ToString() + " MB";
                    _gpuVramText.Text = "显存占用 · 总量未知";
                }
                _gpuVramBar.Visibility = Visibility.Collapsed;
                return;
            }

            double totalMb = totalBytes / 1048576.0;
            int totalKey = (int)Math.Round(totalMb);
            int pct = (int)Math.Round(Math.Min(1.0, usedBytes / (double)totalBytes) * 100);
            long key = ((long)usedKey << 32) ^ (long)totalKey;
            if (key != _gpuMemKey)
            {
                _gpuMemKey = key;
                _gpuMem.Text = usedKey.ToString() + " / " + totalKey.ToString() + " MB";
                _gpuVramText.Text = "显存占用 · 共 " + (totalMb / 1024.0).ToString("0.#") + " GB · " + pct + "%";
            }
            _gpuVramBar.Visibility = Visibility.Visible;
            _gpuVramBar.Value = Math.Min(1.0, usedBytes / (double)totalBytes);
        }

        /// <summary>读数带的两格：GPU 占用 / 显存。优先按主卡 LUID 统计，多卡不混算。</summary>
        private void WriteGpuCells(GpuSnapshot s, int state)
        {
            if (_cellGpu == null) return;
            GpuAdapterSample p = PrimarySample(s);
            WriteUtilCell(s, p);
            string tag = (p != null && !string.IsNullOrEmpty(p.ShortName)) ? p.ShortName + " · " : "";
            Set(_cellGpu.Note, tag + GpuCellNote(state, p != null));
            WriteVramCell(s, p);
        }

        private void WriteUtilCell(GpuSnapshot s, GpuAdapterSample p)
        {
            bool avail = p != null ? p.Available : s.Available;
            double pct = p != null ? p.MaxUtilPct : s.UtilPct;
            if (!avail)
            {
                if (_gpuValKey != -1) { _gpuValKey = -1; _cellGpu.Val.Text = "\u2014"; }
                _cellGpu.Bar.Value = 0;
                _cellGpu.Bar.Opacity = 0.35;
                _cellGpu.Delta.Set(0, "", false);
                if (_gpuTier != 0) { _gpuTier = 0; _cellGpu.Val.Foreground = T.S(T.Hi); }
                return;
            }

            int util = (int)Math.Round(pct);
            if (util != _gpuValKey)
            {
                _gpuValKey = util;
                _cellGpu.Val.Text = util.ToString();
            }
            _cellGpu.Bar.Value = Math.Min(1.0, pct / 100.0);
            _cellGpu.Bar.Opacity = 1.0;
            PushGpuDelta(util);

            int tier = pct > 85 ? 2 : 1;
            if (tier != _gpuTier)
            {
                _gpuTier = tier;
                _cellGpu.Val.Foreground = T.S(tier == 2 ? T.Warn : T.Hi);
            }
        }

        private void WriteVramCell(GpuSnapshot s, GpuAdapterSample p)
        {
            bool memKnown = p != null ? p.MemKnown : s.MemKnown;
            long usedBytes = p != null ? p.VramUsedBytes : s.LocalMemBytes;
            long totalBytes = p != null ? p.VramTotalBytes : _vramTotal;

            if (!memKnown) { WriteVramUnknown(); return; }

            int usedKey = (int)Math.Round(usedBytes / 1048576.0);
            if (usedKey != _vramValKey)
            {
                _vramValKey = usedKey;
                _cellVram.Val.Text = usedKey.ToString();
            }
            if (totalBytes <= 0) { WriteVramNoTotal(); return; }
            WriteVramRatio(usedBytes, totalBytes);
        }

        private void WriteVramUnknown()
        {
            if (_vramValKey != -1) { _vramValKey = -1; _cellVram.Val.Text = "\u2014"; }
            _cellVram.Bar.Value = 0;
            _cellVram.Bar.Opacity = 0.35;
            Set(_cellVram.Note, "PDH 未提供显存计数器");
            _vramNoteKey = long.MinValue;
        }

        private void WriteVramNoTotal()
        {
            _cellVram.Bar.Value = 0;
            _cellVram.Bar.Opacity = 0.35;
            Set(_cellVram.Note, "总量未知 · 只有用量");
            _vramNoteKey = long.MinValue;
        }

        private void WriteVramRatio(long usedBytes, long totalBytes)
        {
            double frac = usedBytes / (double)totalBytes;
            int pct = (int)Math.Round(Math.Min(1.0, frac) * 100);
            long key = ((long)pct << 32) ^ (long)Math.Round(totalBytes / 1073741824.0 * 10);
            if (key != _vramNoteKey)
            {
                _vramNoteKey = key;
                _cellVram.Note.Text = "共 " + (totalBytes / 1073741824.0).ToString("0.#") + " GB · " + pct + "%";
            }
            _cellVram.Bar.Value = frac > 1 ? 1 : frac;
            _cellVram.Bar.Opacity = 1.0;
        }

        private static string GpuCellNote(int state, bool perLuid)
        {
            if (state == 1) return "预热中 · 首拍建基线";
            if (state == 2) return "驱动不支持 · 需 WDDM2";
            if (state == 3) return "采样中断 · 重连中";
            return perLuid ? "1Hz · 按 LUID 分卡" : "实时 · 1Hz";
        }

        private void PushGpuDelta(int util)
        {
            int back = (_hGpuHead - GpuTrendBack + GpuTrendN) % GpuTrendN;
            bool has = _hGpuCount >= GpuTrendBack;
            int prev = has ? (int)Math.Round(_hGpu[back]) : util;

            _hGpu[_hGpuHead] = util;
            _hGpuHead = (_hGpuHead + 1) % GpuTrendN;
            if (_hGpuCount < GpuTrendN) _hGpuCount++;

            int tick = util - prev;
            if (tick == _dGpu) return;
            _dGpu = tick;
            if (!has) { _cellGpu.Delta.Set(0, "", false); return; }
            int dir = tick > 0 ? 1 : tick < 0 ? -1 : 0;
            _cellGpu.Delta.Set(dir, Math.Abs(tick) + "%", false);
        }

        private void WriteGpuEmpty()
        {
            if (_gpuValKey != -1) { _gpuValKey = -1; _cellGpu.Val.Text = "\u2014"; }
            Set(_cellGpu.Note, "驱动不支持 · 需 WDDM2");
            _cellGpu.Bar.Value = 0;
            _cellGpu.Bar.Opacity = 0.35;
            _cellGpu.Delta.Set(0, "", false);
            if (_gpuTier != 0) { _gpuTier = 0; _cellGpu.Val.Foreground = T.S(T.Hi); }

            if (_vramValKey != -1) { _vramValKey = -1; _cellVram.Val.Text = "\u2014"; }
            Set(_cellVram.Note, "不可用");
            _vramNoteKey = long.MinValue;
            _cellVram.Bar.Value = 0;
            _cellVram.Bar.Opacity = 0.35;

            Set(_gpuEngine, "\u2014");
            Set(_gpuMem, "\u2014");
            Set(_gpuEngines, "\u2014");
            _gpuEnginesKey = int.MinValue;
            _gpuMemKey = long.MinValue;
        }
    }
}
