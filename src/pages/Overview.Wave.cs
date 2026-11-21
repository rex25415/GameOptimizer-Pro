// ============================================================================
//  Overview.Wave.cs — 01 态势总览：唤醒抖动波形面板（Overview 分片）
//  来源：Overview.cs（862 行单体）按职责拆分，partial 共享字段，行为零变化。
//  面板与它的采样驱动（PrimeWave / 5Hz Pump）放在一起，"图上的数据从哪来"才看得见。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Go.Pages
{
    public partial class Overview : Page
    {
        // ---- 遥测波形 -------------------------------------------------------
        private UIElement BuildWavePanel(Core c)
        {
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel hl = new StackPanel();
            TextBlock t = Ui.Tx("唤醒抖动轨迹", T.FsTitle, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            hl.Children.Add(t);
            TextBlock s = Ui.Label("WAKE JITTER / \u00B5s \u00B7 5HZ", T.Mute);
            s.Margin = new Thickness(0, 3, 0, 0);
            hl.Children.Add(s);
            head.Children.Add(hl);

            StackPanel stats = new StackPanel();
            stats.Orientation = Orientation.Horizontal;
            stats.VerticalAlignment = VerticalAlignment.Center;
            _statAvg = StatPill("AVG", "0");
            _statP99 = StatPill("P99", "0");
            _statMax = StatPill("MAX", "0");
            stats.Children.Add(_statAvg);
            stats.Children.Add(_statP99);
            stats.Children.Add(_statMax);

            // 三个标签的前缀（字母 + 字距 + 三个空格）只在构建期拼一次；
            // Tick 里只有数字变了才把新数字接上去（WriteStatPills）。
            _statAvgTag = Ui.Spaced("AVG") + "   ";
            _statP99Tag = Ui.Spaced("P99") + "   ";
            _statMaxTag = Ui.Spaced("MAX") + "   ";
            _statAvg.Foreground = T.S(T.Dim);
            _statP99.Foreground = T.S(T.Dim);
            _statMax.Foreground = T.S(T.Dim);
            Grid.SetColumn(stats, 1);
            head.Children.Add(stats);

            _wave = new Waveform(150);
            _wave.Height = 104;
            _wave.Threshold = 1000;
            _wave.ShowThreshold = true;
            _wave.ShowMeanLine = true;
            _wave.SmoothCurve = true;
            _wave.AutoScale = true;
            _wave.Ceil = 6000;
            _wave.Floor = 0;

            Grid boxGrid = new Grid();
            boxGrid.Children.Add(_wave);

            Border box = Ui.Inset(boxGrid, 4);

            Grid axis = new Grid();
            axis.Margin = new Thickness(0, 5, 0, 0);
            for (int i = 0; i < 5; i++) axis.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 5; i++)
            {
                string lab = i == 0 ? "\u221230s" : i == 4 ? "NOW" : "\u2212" + (30 - i * 7).ToString() + "s";
                TextBlock ax = Ui.Mono(lab, T.FsNano, T.Mute);
                ax.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                Grid.SetColumn(ax, i);
                axis.Children.Add(ax);
            }

            StackPanel body = new StackPanel();
            body.Children.Add(head);
            Border g1 = new Border(); g1.Height = 9; body.Children.Add(g1);
            body.Children.Add(box);
            body.Children.Add(axis);

            // 用采样器已有历史填充，避免首次打开是空图
            PrimeWave();
            return Ui.Panel(body, 14);
        }

        private TextBlock StatPill(string key, string val)
        {
            TextBlock t = new TextBlock();
            t.Text = Ui.Spaced(key);
            t.FontSize = T.FsNano;
            t.FontFamily = T.FDisplayReg;
            t.Foreground = T.S(T.Mute);
            t.Margin = new Thickness(18, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        private void PrimeWave()
        {
            if (C == null || _wave == null) return;
            double[] hist = C.Perf.JitterSnapshot();
            int take = Math.Min(hist.Length, 150);
            for (int i = hist.Length - take; i < hist.Length; i++) _wave.Push(hist[i]);
            _jcursor = C.Perf.JitterCount;
            _wave.Refresh();
        }

        // 复用的取样缓冲：旧实现走 Drain()，每 200ms 新建两个数组（实测约 14 KB/s）。
        private readonly double[] _jb = new double[256];

        private void Pump()
        {
            if (_wave == null || C == null) return;
            int n = C.Perf.DrainInto(_jb, ref _jcursor);
            if (n <= 0) return;
            for (int i = 0; i < n; i++) _wave.Push(_jb[i]);
            _wave.Refresh();
        }
    }
}
