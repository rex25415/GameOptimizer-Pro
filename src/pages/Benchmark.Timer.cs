// ============================================================================
//  Benchmark.Timer.cs — 定时器精度面板（Benchmark 分片）
//
//  它为噪声提供物理解释：唤醒超调的绝对值主要由系统的定时器颗粒度决定
//  （默认约 15.6 ms，请求 0.5 ms 后降到 0.5 ms 量级）。所以这一块不是装饰，
//  它是"为什么 P50 会是 14 ms"的答案。请求/释放只作用于本页租约。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Benchmark : Page
    {
        private Switch _swTimer;
        private TextBlock _sysVal, _reqVal;

        // ---- 定时器精度 -----------------------------------------------------
        private UIElement BuildTimerPanel(Core c)
        {
            StackPanel body = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel hl = new StackPanel();
            StackPanel trow = new StackPanel();
            trow.Orientation = Orientation.Horizontal;

            Border tick = new Border();
            tick.Width = 2;
            tick.Height = 13;
            tick.Background = T.S(T.Accent);
            tick.Margin = new Thickness(0, 0, 9, 1);
            tick.VerticalAlignment = VerticalAlignment.Bottom;
            trow.Children.Add(tick);

            TextBlock t = Ui.Tx("\u672C\u8FDB\u7A0B 0.5 ms \u7CBE\u5EA6", T.FsTitle, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            trow.Children.Add(t);
            hl.Children.Add(trow);

            TextBlock hs = Ui.Label("NT SET TIMER RESOLUTION \u00B7 PROCESS SCOPE", T.Mute);
            hs.Margin = new Thickness(11, 4, 0, 0);
            hs.Opacity = 0.8;
            hl.Children.Add(hs);
            head.Children.Add(hl);

            _swTimer = new Switch(c.Timer.Active);
            _swTimer.VerticalAlignment = VerticalAlignment.Center;
            _swTimer.Changed += delegate { OnTimerToggle(); };
            Grid.SetColumn(_swTimer, 1);
            head.Children.Add(_swTimer);
            body.Children.Add(head);

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));

            TextBlock ex = Ui.Tx("Windows \u9ED8\u8BA4\u5B9A\u65F6\u5668\u9897\u7C92\u5EA6\u7EA6 1\u201315.6 ms\uFF1B"
                + "\u8BF7\u6C42 0.5 ms \u53EF\u7F29\u5C0F Sleep / NtDelayExecution \u7684\u5524\u9192\u8D85\u8C03\uFF0C"
                + "\u4EE3\u4EF7\u662F\u7A7A\u95F2\u5524\u9192\u6B21\u6570\u4E0E\u529F\u8017\u4E0A\u5347\u3002"
                + "\u5F00\u542F\u540E\u91CD\u65B0\u91C7\u6837\uFF0C\u5C31\u80FD\u5728\u4E0A\u65B9\u76F4\u65B9\u56FE\u91CC\u770B\u5230\u5206\u5E03\u6574\u4F53\u5DE6\u79FB\u3002",
                T.FsSmall, T.Dim, T.FCn);
            ex.TextWrapping = TextWrapping.Wrap;
            ex.LineHeight = 18;
            body.Children.Add(ex);

            body.Children.Add(Gap(10));

            StackPanel line = new StackPanel();
            line.Orientation = Orientation.Horizontal;

            line.Children.Add(Ui.Tx("\u7CFB\u7EDF\u9897\u7C92\u5EA6", T.FsMicro, T.Mute, T.FCn));
            _sysVal = Ui.Mono("\u2014", T.FsMicro, T.Mute);
            _sysVal.Width = 58;
            _sysVal.Margin = new Thickness(8, 0, 0, 0);
            line.Children.Add(_sysVal);

            TextBlock dot1 = Ui.Mono("\u00B7", T.FsMicro, T.Mute);
            dot1.Margin = new Thickness(10, 0, 10, 0);
            line.Children.Add(dot1);

            line.Children.Add(Ui.Tx("\u672C\u8FDB\u7A0B", T.FsMicro, T.Mute, T.FCn));
            _reqVal = Ui.Mono("\u2014", T.FsMicro, T.Mute);
            _reqVal.Width = 58;
            _reqVal.Margin = new Thickness(8, 0, 0, 0);
            line.Children.Add(_reqVal);

            TextBlock hint = Ui.Tx("\u518D\u6B21\u91C7\u6837\u5BF9\u6BD4\u53EF\u89C1\u5DEE\u5F02", T.FsMicro, T.Mute, T.FCn);
            hint.Margin = new Thickness(16, 0, 0, 0);
            line.Children.Add(hint);

            body.Children.Add(line);
            return Ui.Panel(body, T.PadPanel);
        }

        private void OnTimerToggle()
        {
            if (C == null || _swTimer == null) return;
            if (_swTimer.On)
            {
                C.Timer.Request(TimerOwner.Benchmark, 0.5);
                if (!C.Timer.Active)
                {
                    _swTimer.On = false;
                    if (C.Shell != null) C.Shell.Toast("\u672C\u8FDB\u7A0B\u5B9A\u65F6\u5668\u7CBE\u5EA6\u8BF7\u6C42\u88AB\u7CFB\u7EDF\u62D2\u7EDD", false);
                }
                else if (C.Shell != null)
                {
                    C.Shell.Toast("\u5DF2\u8BF7\u6C42 " + C.Timer.GrantedMs.ToString("0.00") + " ms \u7CBE\u5EA6 \u00B7 \u518D\u6B21\u91C7\u6837\u5BF9\u6BD4\u53EF\u89C1\u5DEE\u5F02", true);
                }
            }
            else
            {
                // 只撤销本页的租约：总览/调度页可能还持有同一个系统级精度。
                C.Timer.Release(TimerOwner.Benchmark);
                if (C.Shell != null) C.Shell.Toast(C.Timer.Active
                    ? "\u672C\u9875\u5DF2\u91CA\u653E \u00B7 \u5176\u5B83\u6A21\u5757\u4ECD\u6301\u6709\u7CBE\u5EA6\u8BF7\u6C42"
                    : "\u5DF2\u91CA\u653E\u5B9A\u65F6\u5668\u7CBE\u5EA6\u8BF7\u6C42 \u00B7 \u6062\u590D\u7CFB\u7EDF\u9ED8\u8BA4", true);
            }
            PaintTimerState(C.Perf.Current.TimerMs);
        }

        private void PaintTimerState(double sysMs)
        {
            if (_sysVal == null) return;
            double req = (C == null || !C.Timer.Active) ? 0 : C.Timer.GrantedMs;
            _sysVal.Text = sysMs > 0 ? sysMs.ToString("0.00") + " ms" : "\u2014";
            _sysVal.Foreground = T.S(sysMs > 0 ? T.Dim : T.Mute);
            _reqVal.Text = req > 0 ? req.ToString("0.00") + " ms" : "\u2014";
            _reqVal.Foreground = T.S(req > 0 ? T.Ok : T.Mute);
        }
    }
}
