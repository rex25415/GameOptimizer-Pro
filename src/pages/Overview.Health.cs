// ============================================================================
//  Overview.Health.cs — Rev130 总览驾驶舱：四维健康条 + 瓶颈诊断 + 综合评级徽标
//  与 Kernel/Telemetry.Engine.cs 联动，零额外轮询、仅在刻度变化时更新 UI。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Overview : Page
    {
        private Border _gradeBadge;
        private TextBlock _gradeText;
        private TextBlock _bottleneckText;
        private SegBar _axSchedBar, _axCpuBar, _axMemBar, _axTuneBar;
        private TextBlock _axSchedVal, _axCpuVal, _axMemVal, _axTuneVal;
        private int _healthSig = int.MinValue;
        private string _lastBottleneck = "";

        private void ResetHealthUi()
        {
            _healthSig = int.MinValue;
            _lastBottleneck = "";
        }

        private UIElement BuildMissionHeader()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Margin = new Thickness(0, 0, 0, 2);

            StackPanel left = new StackPanel();
            TextBlock t = Ui.Label("MISSION READINESS", T.AccentDim);
            left.Children.Add(t);
            TextBlock sub = Ui.Mono("四维态势 · 实测驱动", T.FsNano, T.Mute);
            sub.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(sub);
            g.Children.Add(left);

            Border badge = new Border();
            badge.CornerRadius = new CornerRadius(T.RadiusMd);
            badge.BorderThickness = new Thickness(1);
            badge.BorderBrush = T.S(T.LineMid);
            badge.Background = T.S(T.Inset);
            badge.Padding = new Thickness(10, 3, 10, 3);
            badge.VerticalAlignment = VerticalAlignment.Center;
            _gradeBadge = badge;

            _gradeText = Ui.Mono("GRADE \u2014", T.FsNano, T.Dim);
            _gradeText.FontWeight = FontWeights.SemiBold;
            badge.Child = _gradeText;
            Grid.SetColumn(badge, 1);
            g.Children.Add(badge);
            Tip.Attach(badge, "综合评级 = 调度唤醒(34%) + 算力余裕(24%) + 内存余裕(22%) + 调优洁净度(20%)");
            return g;
        }

        private UIElement BuildBottleneckStrip()
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(T.RadiusSm);
            b.Background = T.S(0xAA070C17);
            b.BorderBrush = T.S(T.LineSoft);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(9, 6, 9, 6);
            b.Margin = new Thickness(0, 8, 0, 0);

            _bottleneckText = Ui.Tx("正在采集四维遥测基线…", T.FsNano, T.Dim, T.FCn);
            _bottleneckText.TextWrapping = TextWrapping.Wrap;
            _bottleneckText.LineHeight = 14.5;
            b.Child = _bottleneckText;
            return b;
        }

        private UIElement BuildFourAxisStrip()
        {
            Grid g = new Grid();
            for (int i = 0; i < 4; i++)
            {
                if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
            }

            AddAxisCell(g, 0, "调度唤醒", T.Accent, out _axSchedBar, out _axSchedVal);
            AddAxisCell(g, 2, "算力余裕", T.Accent2, out _axCpuBar, out _axCpuVal);
            AddAxisCell(g, 4, "内存余裕", T.Ok, out _axMemBar, out _axMemVal);
            AddAxisCell(g, 6, "调优覆盖", T.Violet, out _axTuneBar, out _axTuneVal);

            Border wrap = new Border();
            wrap.CornerRadius = new CornerRadius(T.RadiusMd);
            wrap.Background = T.S(0xAA080E1B);
            wrap.BorderBrush = T.S(T.LineSoft);
            wrap.BorderThickness = new Thickness(1);
            wrap.Padding = new Thickness(12, 8, 12, 8);
            wrap.Child = g;
            return wrap;
        }

        private static void AddAxisCell(Grid host, int col, string label, uint tint, out SegBar bar, out TextBlock val)
        {
            StackPanel sp = new StackPanel();
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock lbl = Ui.Tx(label, T.FsNano, T.Mute, T.FCn);
            head.Children.Add(lbl);

            val = Ui.Mono("\u2014", T.FsNano, T.Hi);
            val.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(val, 1);
            head.Children.Add(val);
            sp.Children.Add(head);

            bar = new SegBar(0);
            bar.Height = 3;
            bar.Tint = tint;
            bar.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(bar);

            Grid.SetColumn(sp, col);
            host.Children.Add(sp);
        }

        private void RenderSystemHealth(Snapshot s)
        {
            if (_gradeText == null || s == null) return;
            int app = _rdTuneApplied >= 0 ? _rdTuneApplied : 0;
            int tot = _rdTuneTotal > 0 ? _rdTuneTotal : 30;
            int rem = _rdTuneRem >= 0 ? _rdTuneRem : 0;

            SystemHealthScore h = TelemetryEngine.Evaluate(s, app, tot, rem, _cpuThrottled);
            int sig = h.SchedScore * 1000000 + h.CpuScore * 10000 + h.MemScore * 100 + h.TuneScore;
            if (sig != _healthSig)
            {
                _healthSig = sig;
                _gradeText.Text = "GRADE " + h.Grade + " · " + h.Composite;
                _gradeText.Foreground = T.S(h.GradeTint);
                if (_gradeBadge != null)
                {
                    _gradeBadge.BorderBrush = T.S(Ui.Wash(h.GradeTint, 0x66));
                    _gradeBadge.Background = T.S(Ui.Wash(h.GradeTint, 0x1A));
                }
                UpdateAxis(_axSchedBar, _axSchedVal, h.SchedScore);
                UpdateAxis(_axCpuBar, _axCpuVal, h.CpuScore);
                UpdateAxis(_axMemBar, _axMemVal, h.MemScore);
                UpdateAxis(_axTuneBar, _axTuneVal, h.TuneScore);
            }
            if (_bottleneckText != null && h.Bottleneck != _lastBottleneck)
            {
                _lastBottleneck = h.Bottleneck;
                _bottleneckText.Text = h.Bottleneck;
                _bottleneckText.Foreground = T.S(h.Composite >= 80 ? T.Text : T.Dim);
            }
        }

        private static void UpdateAxis(SegBar bar, TextBlock val, int score)
        {
            uint col = score >= 82 ? T.Ok : (score >= 60 ? T.Accent : T.Warn);
            if (bar != null) { bar.Tint = col; bar.Value = score / 100.0; }
            if (val != null) { val.Text = score.ToString(); val.Foreground = T.S(col); }
        }
    }
}
