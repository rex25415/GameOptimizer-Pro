// ============================================================================
//  Memory.Composition.cs — 04 内存与提交：一条 MemRibbon 缎带 + 同序图例（Memory 分片）
//  为什么切：四段比例（已用 / 待机 / 已修改 / 空闲）的切分与配色自成一件事，
//  和"够不够"是两个问题；分开后一次只读一个问题的全部代码。
//
//  Rev86：旧版这四段是用 ColumnDefinition.Star 手拼的 Grid，每段里塞一个标签，
//  再在下面对着同一组数字画一遍图例——两套"构成"讲同一件事。现在只有一条
//  MemRibbon（Widgets 里已有的绘制组件，零分配、按比例切分），图例只负责
//  颜色 → 名字 → 数值的对应，不再重复画第二根条。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Memory : Page
    {
        // 缎带高度：26px 立体玻璃能量条，作为内存页顶部主视觉焦点。
        private const double RibbonH = 26;

        private UIElement BuildComposition(Core c)
        {
            StackPanel body = new StackPanel();

            _compMeta = Src("MEMRIBBON · 4 SEGMENTS");
            body.Children.Add(Ui.PanelHead("物理内存构成", "PHYSICAL COMPOSITION", _compMeta));
            body.Children.Add(Gap(12));

            _ribTint = new uint[4];
            _ribTint[0] = T.Accent;
            _ribTint[1] = T.SeriesA;
            _ribTint[2] = T.SeriesB;
            _ribTint[3] = FreeTint;

            _ribParts = new double[4];

            _ribbon = new MemRibbon();
            _ribbon.Height = RibbonH;
            _ribbon.Gap = 3;
            _ribbon.Radius = 5;
            _ribbon.Parts = _ribParts;             // 复用同一个数组：Tick 里只改数值，不分配
            _ribbon.Colors = _ribTint;
            _ribbon.Labels = new string[] { "已用", "待机", "已修改", "空闲" };
            _ribbon.Unit = "MB";

            // 缺测时留在底下的轨道：它读作"待测区域"，不是一根 0 长的条。
            _ribTrack = new Border();
            _ribTrack.Background = T.S(0x0CFFFFFF);
            _ribTrack.BorderBrush = T.S(T.LineSoft);
            _ribTrack.BorderThickness = new Thickness(1);
            _ribTrack.CornerRadius = new CornerRadius(2);

            _ribWait = Ui.Mono("等待采样 · 采样线程尚未发布第一帧", T.FsNano, T.Mute);
            _ribWait.HorizontalAlignment = HorizontalAlignment.Center;
            _ribWait.VerticalAlignment = VerticalAlignment.Center;

            Grid host = new Grid();
            host.Height = RibbonH;
            host.Children.Add(_ribTrack);
            host.Children.Add(_ribbon);
            host.Children.Add(_ribWait);
            Tip.Attach(host,
                "已用 = 总量 − 可用 − 已修改；可用 = 空闲 + 待机。\n" +
                "待机：已从进程收回、内容还留在内存里的缓存页（清理待机列表就是把它们丢掉）。\n" +
                "已修改：已改动、还没写回磁盘的页，写回后变成待机。");
            body.Children.Add(host);

            body.Children.Add(Gap(10));
            body.Children.Add(BuildLegend());
            body.Children.Add(Gap(10));

            TextBlock note = Ui.Tx("「可用」= 空闲 + 待机。待机页不归任何程序，但也没有空出来；" +
                                   "清理待机列表只是把它们从「随时可复活」变成「真要重读盘」。",
                T.FsMicro, T.Mute, T.FCn);
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 15;
            body.Children.Add(note);

            return Ui.HeroPanel(body, T.PadPanel);
        }

        /// <summary>
        /// 图例：四枚 22×6 色块，与缎带同色、同序。
        /// 为什么不按段宽画色块：0.2% 的段在 1000px 宽上只有 2px，图例就退化成两行字，
        /// 读者反而读不到"已修改 37 MB"这条真实读数。等宽色块保住可读性，
        /// 顺序与颜色负责把它和缎带对上。
        /// </summary>
        private UIElement BuildLegend()
        {
            string[] cn = new string[] { "已用", "待机", "已修改", "空闲" };
            string[] en = new string[] { "IN USE", "STANDBY", "MODIFIED", "FREE" };
            _legVal = new TextBlock[4];

            Grid g = new Grid();
            for (int i = 0; i < 4; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                StackPanel sp = new StackPanel();
                if (i > 0) sp.Margin = new Thickness(16, 0, 0, 0);

                StackPanel row = new StackPanel();
                row.Orientation = Orientation.Horizontal;

                Border chip = new Border();
                chip.Width = 22;
                chip.Height = 6;
                chip.CornerRadius = new CornerRadius(2);
                chip.Background = T.S(_ribTint[i]);
                chip.VerticalAlignment = VerticalAlignment.Center;
                chip.Margin = new Thickness(0, 0, 8, 0);
                chip.IsHitTestVisible = false;
                row.Children.Add(chip);

                row.Children.Add(Ui.Tx(cn[i], T.FsMicro, T.Text, T.FCn));

                TextBlock e = Ui.Label(en[i], T.Mute);
                e.Opacity = 0.7;
                e.Margin = new Thickness(7, 0, 0, 0);
                row.Children.Add(e);
                sp.Children.Add(row);

                TextBlock v = Ui.Mono("\u2014", T.FsNano, T.Dim);
                v.Margin = new Thickness(30, 5, 0, 0);   // 与名字左缘对齐（色块 22 + 8）
                _legKey[i] = int.MinValue;
                v.TextTrimming = TextTrimming.CharacterEllipsis;
                v.TextWrapping = TextWrapping.NoWrap;
                _legVal[i] = v;
                sp.Children.Add(v);

                Grid.SetColumn(sp, i);
                g.Children.Add(sp);
            }
            return g;
        }

        // ====================================================================
        //  写入：缎带 + 图例
        // ====================================================================
        private void ApplyRibbon(double total, double used, double standby, double modified, double free, bool memListOk)
        {
            if (_ribbon == null) return;

            _ribParts[0] = used;
            _ribParts[1] = memListOk ? standby : 0;
            _ribParts[2] = memListOk ? modified : 0;
            _ribParts[3] = memListOk ? free : 0;

            // 只在"MB 刻度"变了才重画：1Hz 下重绘一条 18px 缎带不贵，但没必要白刷。
            long sig = (long)Math.Round(used) * 100000000L + (long)Math.Round(standby) * 10000L + (long)Math.Round(modified);
            if (sig != _ribSig) { _ribSig = sig; _ribbon.InvalidateVisual(); }

            // 待机 / 已修改 不可用时不能只画"已用 + 空闲"：那会把两根合起来的条读成 100%，
            // 而真实构成里还有没测到的两段。这种时候只留轨道和一句原因。
            bool draw = memListOk;
            _ribbon.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;
            _ribTrack.Visibility = draw ? Visibility.Collapsed : Visibility.Visible;
            _ribWait.Visibility = draw ? Visibility.Collapsed : Visibility.Visible;
            Set(_ribWait, "待机 / 已修改 未测量 · 内存列表查询不可用");

            SetLegend(0, used, null);
            SetLegend(1, standby, memListOk ? null : "\u2014 · 未测量");
            SetLegend(2, modified, memListOk ? null : "\u2014 · 未测量");
            SetLegend(3, free, memListOk ? null : "\u2014 · 待机未测量");

            int mk = (int)Math.Round(total / 102.4) * 2 + (memListOk ? 1 : 0);
            if (mk != _compMetaKey)
            {
                _compMetaKey = mk;
                _compMeta.Text = "MEMRIBBON · " + Gb(total) + " GB" + (memListOk ? "" : " · PARTIAL");
            }
        }

        /// <summary>图例一格：MB 刻度变化才重写；unknown 非空表示这一格没有测量值。</summary>
        private void SetLegend(int i, double mb, string unknown)
        {
            int key = unknown == null ? (int)Math.Round(mb) : int.MinValue;
            if (key == _legKey[i]) return;
            _legKey[i] = key;
            _legVal[i].Text = unknown == null ? SizeText(mb) : unknown;
        }

        /// <summary>
        /// 一个量给一种量纲：不足 1 GB 只写 MB。写成「51 MB · 0.0 GB」会让读者以为
        /// 后半截是个测量结果，而它只是四舍五入的零。
        /// </summary>
        private static string SizeText(double mb)
        {
            if (mb >= 1024) return Mb(mb) + " · " + Gb(mb) + " GB";
            return Mb(mb);
        }

        private void ClearRibbon(string why)
        {
            if (_ribbon == null) return;
            for (int i = 0; i < 4; i++) _ribParts[i] = 0;
            _ribSig = int.MinValue;
            _ribbon.Visibility = Visibility.Collapsed;
            _ribTrack.Visibility = Visibility.Visible;
            _ribWait.Visibility = Visibility.Visible;
            Set(_ribWait, why);
            for (int i = 0; i < 4; i++)
            {
                _legKey[i] = int.MinValue;
                _legVal[i].Text = "\u2014";
            }
            _compMetaKey = int.MinValue;
            _compMeta.Text = "MEMRIBBON · \u2014";
        }
    }
}
