// ============================================================================
//  Graphics.Displays.cs — 图形栈 · 显示输出拓扑（Graphics 分片）
//  数据直接来自 Hardware.ReadDisplays()（EnumDisplayDevices + EnumDisplaySettingsEx），
//  零 WMI 调用、零阻塞；渲染分辨率、刷新率、色深与主屏标识。
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
        private StackPanel _displayHost;
        private TextBlock _displayMeta;
        private DisplayStageCanvas _displayCanvas;

        // ====================================================================
        //  Rev130 显示输出拓扑虚拟桌面画布 + 端口明细
        // ====================================================================
        private UIElement BuildDisplays(Core c)
        {
            StackPanel body = new StackPanel();
            _displayMeta = Ui.Mono("\u2014", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("显示拓扑画布", "VIRTUAL DISPLAY TOPOLOGY · NATIVE GDI", _displayMeta));
            body.Children.Add(Gap(10));

            _displayCanvas = new DisplayStageCanvas();
            body.Children.Add(_displayCanvas);
            body.Children.Add(Gap(10));

            body.Children.Add(BuildDisplayHead());
            body.Children.Add(Ui.Rule(T.LineSoft, 0));

            _displayHost = new StackPanel();
            body.Children.Add(_displayHost);

            Grid hero = new Grid();
            hero.Children.Add(Ui.HeroPanel(body, T.PadPanel));
            hero.Children.Add(Ui.CornerTicks(T.AccentA(0x80), 10, 1.5));
            return hero;
        }

        private static Grid BuildDisplayHead()
        {
            Grid g = NewDisplayGrid();
            g.Height = 22;

            TextBlock name = Ui.Label("显示器 / 端口", T.Mute);
            name.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(name);

            TextBlock res = Ui.Label("原生分辨率", T.Mute);
            res.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(res, 1);
            g.Children.Add(res);

            TextBlock hz = Ui.Label("刷新率", T.Mute);
            hz.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(hz, 2);
            g.Children.Add(hz);

            TextBlock bpp = Ui.Label("色深", T.Mute);
            bpp.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(bpp, 3);
            g.Children.Add(bpp);
            return g;
        }

        private static Grid NewDisplayGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            return g;
        }

        private void ApplyDisplays(List<Hardware.DisplayInfo> list)
        {
            if (_displayCanvas != null) _displayCanvas.SetDisplays(list);
            if (_displayHost == null) return;
            _displayHost.Children.Clear();

            if (list == null || list.Count == 0)
            {
                if (_displayMeta != null) _displayMeta.Text = "0 DISPLAYS";
                _displayHost.Children.Add(Ui.Empty(Glyph.Graphics, "未检测到活动显示输出",
                    "EnumDisplayDevices 未发现处于桌面拓扑中的活动显示器。", T.Mute, null));
                return;
            }

            Hardware.DisplayInfo mainDisp = null;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Main) { mainDisp = list[i]; break; }
            }
            if (mainDisp == null) mainDisp = list[0];

            string meta = list.Count + (list.Count == 1 ? " DISPLAY" : " DISPLAYS");
            if (mainDisp.Width > 0 && mainDisp.Height > 0)
            {
                meta += " · " + mainDisp.Width + "\u00D7" + mainDisp.Height;
                if (mainDisp.RefreshHz > 0) meta += "@" + mainDisp.RefreshHz + "Hz";
            }
            if (_displayMeta != null) _displayMeta.Text = meta;

            for (int i = 0; i < list.Count; i++)
                _displayHost.Children.Add(DisplayRow(list[i]));
        }

        private static UIElement DisplayRow(Hardware.DisplayInfo d)
        {
            Grid row = NewDisplayGrid();
            row.MinHeight = 62;

            Grid titleWrap = new Grid();
            titleWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleWrap.ColumnDefinitions.Add(new ColumnDefinition());
            titleWrap.VerticalAlignment = VerticalAlignment.Center;
            titleWrap.Margin = new Thickness(0, 0, 12, 0);

            UIElement monitorViz = BuildMonitorThumb(d);
            titleWrap.Children.Add(monitorViz);

            StackPanel title = new StackPanel();
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(14, 0, 0, 0);
            Grid.SetColumn(title, 1);

            StackPanel top = new StackPanel();
            top.Orientation = Orientation.Horizontal;

            string label = string.IsNullOrEmpty(d.Name) ? d.DeviceName : d.Name;
            TextBlock name = Ui.Tx(label, T.FsBody, T.Hi, T.FCn);
            name.FontWeight = FontWeights.SemiBold;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            name.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(name);

            if (d.Main)
            {
                Border badge = Ui.Tag("主屏", T.Accent);
                badge.Margin = new Thickness(8, 0, 0, 0);
                badge.VerticalAlignment = VerticalAlignment.Center;
                top.Children.Add(badge);
            }
            title.Children.Add(top);

            string subText = string.IsNullOrEmpty(d.DeviceName) ? "\u2014" : d.DeviceName;
            if (!string.IsNullOrEmpty(d.AdapterName)) subText += " · " + d.AdapterName;
            subText += " · 坐标 (" + d.PosX + ", " + d.PosY + ")";
            TextBlock sub = Ui.Mono(subText, T.FsNano, T.Mute);
            sub.Margin = new Thickness(0, 4, 0, 0);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sub.TextWrapping = TextWrapping.NoWrap;
            title.Children.Add(sub);
            titleWrap.Children.Add(title);
            row.Children.Add(titleWrap);

            string resText = d.Width > 0 && d.Height > 0 ? (d.Width + "\u00D7" + d.Height) : "\u2014";
            TextBlock res = Ui.Mono(resText, T.FsBody, T.Hi);
            res.FontWeight = FontWeights.SemiBold;
            res.HorizontalAlignment = HorizontalAlignment.Right;
            res.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(res, 1);
            row.Children.Add(res);

            string hzText = d.RefreshHz > 0 ? (d.RefreshHz + " Hz") : "\u2014";
            uint hzCol = d.RefreshHz >= 120 ? T.Ok : T.Text;
            TextBlock hz = Ui.Mono(hzText, T.FsSmall, hzCol);
            hz.HorizontalAlignment = HorizontalAlignment.Right;
            hz.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hz, 2);
            row.Children.Add(hz);

            string bppText = d.BitsPerPel > 0 ? (d.BitsPerPel + " bit") : "\u2014";
            TextBlock bpp = Ui.Mono(bppText, T.FsSmall, T.Dim);
            bpp.HorizontalAlignment = HorizontalAlignment.Right;
            bpp.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(bpp, 3);
            row.Children.Add(bpp);

            Border shell = Ui.RowShell(row, d.Main ? T.Accent : T.LineMid, 9);
            shell.Background = Brushes.Transparent;
            shell.MouseEnter += delegate { shell.Background = T.S(T.LineFaint); };
            shell.MouseLeave += delegate { shell.Background = Brushes.Transparent; };
            Tip.Attach(shell, "设备名：" + d.DeviceName +
                              "\n适配器：" + (string.IsNullOrEmpty(d.AdapterName) ? "\u2014" : d.AdapterName) +
                              "\n分辨率：" + resText + " · 刷新率：" + hzText + " · 色深：" + bppText);
            return shell;
        }

        /// <summary>按实际屏幕纵横比实时绘制的微型玻璃显示器轮廓（含刷新率微标与支架）。</summary>
        private static UIElement BuildMonitorThumb(Hardware.DisplayInfo d)
        {
            double ar = (d.Width > 0 && d.Height > 0) ? ((double)d.Width / d.Height) : 1.6;
            if (ar < 1.0) ar = 1.0; else if (ar > 2.4) ar = 2.4;
            double sh = 34;
            double sw = Math.Round(sh * ar);
            if (sw > 68) sw = 68;
            uint edgeTint = d.Main ? T.Accent : T.Dim;

            StackPanel wrap = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

            Border screen = new Border();
            screen.Width = sw;
            screen.Height = sh;
            screen.CornerRadius = new CornerRadius(4);
            screen.Background = d.Main ? (Brush)T.Grad(T.AccentA(0x2A), T.Accent2A(0x12), 135) : T.S(T.Inset);
            screen.BorderBrush = T.S(d.Main ? T.AccentA(0x88) : T.LineMid);
            screen.BorderThickness = new Thickness(1.2);
            screen.SnapsToDevicePixels = true;

            TextBlock hz = Ui.Mono(d.RefreshHz > 0 ? (d.RefreshHz + "Hz") : "DISP", 8.5, d.RefreshHz >= 120 ? T.Ok : edgeTint);
            hz.FontWeight = FontWeights.SemiBold;
            hz.HorizontalAlignment = HorizontalAlignment.Center;
            hz.VerticalAlignment = VerticalAlignment.Center;
            screen.Child = hz;
            wrap.Children.Add(screen);

            Border neck = new Border { Width = 6, Height = 3, Background = T.S(T.LineMid), HorizontalAlignment = HorizontalAlignment.Center };
            wrap.Children.Add(neck);
            Border foot = new Border { Width = Math.Round(sw * 0.45), Height = 2, CornerRadius = new CornerRadius(1), Background = T.S(d.Main ? T.AccentA(0x70) : T.LineMid), HorizontalAlignment = HorizontalAlignment.Center };
            wrap.Children.Add(foot);
            return wrap;
        }
    }
}
