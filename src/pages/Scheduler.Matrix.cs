// ============================================================================
//  Scheduler.Matrix.cs — 03 调度与定时器：亲和矩阵面板（Scheduler 分片）
//  为什么切：矩阵、它的面板头（掩码读数 + 图例 + 两个动作）与每簇走势
//  共用同一套版面尺度（CcdGap / 簇宽）；它们是一张图，改动必然同时落在三者上。
//
//  Rev86：矩阵头上那个 60 号的定时器 hero 搬进了顶部读数带（同一页不该有两个主指标）。
//  这里只剩一条等宽的"当前掩码 = 0x…"读数——矩阵要回答的是"允许谁"，
//  而这行字就是它的量纲；图例给出三态：选中 / 被占用 / 未选中。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Scheduler : Page
    {
        // ====================================================================
        //  亲和矩阵
        // ====================================================================
        private UIElement BuildMatrixPanel(Core c)
        {
            StackPanel body = new StackPanel();
            body.Children.Add(BuildMatrixHead(c));
            body.Children.Add(Gap(9));
            body.Children.Add(BuildMaskRow());
            body.Children.Add(Gap(7));

            _matrix = new SchedMatrix();
            _matrix.Ccds = Math.Max(1, c.Topo.Ccds);
            _matrix.CoresPerCcd = Math.Max(1, c.Topo.CoresPerCcd);
            _matrix.ThreadsPerCore = Math.Max(1, c.Topo.ThreadsPerCore);
            _matrix.ClusterGap = CcdGap;
            _matrix.Height = 148;
            _matrix.CoreClicked += delegate (int idx) { ToggleSingleCore(c, idx); };
            _matrix.ClusterClicked += delegate (int cc) { ToggleCluster(c, cc); };
            Tip.Attach(_matrix,
                "每个逻辑处理器一条竖槽：高度 = 该处理器 1 秒窗口内的忙时比例。\r\n" +
                "点击任意逻辑核竖槽可直接开关前台进程的该核亲和位；点击底部 CCD0/CCD1 括号可切换整簇。\r\n" +
                "簇之间的空白是物理间隙：双 CCD 的 32 核被 L3 分成两簇，跨簇访问延迟明显更高。");
            body.Children.Add(_matrix);

            body.Children.Add(Gap(4));
            body.Children.Add(BuildClusters(c));
            Grid stage = new Grid();
            stage.Children.Add(Ui.HeroPanel(body, T.PadPanel));
            stage.Children.Add(Ui.CornerTicks(T.AccentA(0x88), 10, 1.5));
            return stage;
        }

        /// <summary>面板头：左是统一区块头，右是掩码的两个动作（它们改的是前台进程，不是本页）。</summary>
        private UIElement BuildMatrixHead(Core c)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Ui.PanelHead("亲和矩阵", "FOREGROUND AFFINITY MASK", null));

            UIElement acts = BuildMaskActions(c);
            Grid.SetColumn(acts, 1);
            g.Children.Add(acts);
            return g;
        }

        /// <summary>掩码读数行：左边一句等宽事实，右边三态图例。两者同一基线。</summary>
        private UIElement BuildMaskRow()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _maskLine = Ui.Mono("当前掩码 = —", T.FsMicro, T.Text);
            _maskLine.TextTrimming = TextTrimming.CharacterEllipsis;
            _maskLine.TextWrapping = TextWrapping.NoWrap;
            _maskLine.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(_maskLine);

            // 结论徽标：把"这串掩码意味着什么"先说一句（已锁定 CCD0 / 全部可用 / 部分掩码）。
            _maskBadge = new Badge("未测量", T.Mute);
            _maskBadge.Margin = new Thickness(12, 0, 16, 0);
            Grid.SetColumn(_maskBadge, 1);
            g.Children.Add(_maskBadge);

            StackPanel legend = new StackPanel();
            legend.Orientation = Orientation.Horizontal;
            legend.HorizontalAlignment = HorizontalAlignment.Right;
            legend.VerticalAlignment = VerticalAlignment.Center;
            legend.Margin = new Thickness(16, 0, 0, 0);
            legend.Children.Add(Swatch(0));
            _allowText = Ui.Mono("选中 —", T.FsNano, T.Dim);
            legend.Children.Add(_allowText);
            legend.Children.Add(LegendDot());
            legend.Children.Add(Swatch(1));
            TextBlock busy = Ui.Mono("被占用", T.FsNano, T.Dim);
            legend.Children.Add(busy);
            legend.Children.Add(LegendDot());
            legend.Children.Add(Swatch(2));
            _denyText = Ui.Mono("未选中 —", T.FsNano, T.Dim);
            legend.Children.Add(_denyText);
            Grid.SetColumn(legend, 2);
            g.Children.Add(legend);

            Tip.Attach(g, "当前掩码 = 前台外部进程的 ProcessorAffinity 实测值；读不到时显示 —，矩阵也不画任何排除态（宁可空着，不画错）。");
            return g;
        }

        private static Border LegendDot()
        {
            Border b = new Border();
            b.Width = 14;
            return b;
        }

        private UIElement BuildMaskActions(Core c)
        {
            StackPanel acts = new StackPanel();
            acts.Orientation = Orientation.Horizontal;
            acts.VerticalAlignment = VerticalAlignment.Center;

            Btn phys = new Btn("仅物理核", BtnKind.Ghost);
            _btnPhys = phys;
            phys.Height = T.BtnH;
            phys.Compact = true;
            phys.Margin = new Thickness(0, 0, 6, 0);
            phys.Click += delegate { ApplyMaskPreset(c, Affinity.PhysicalOnlyMask(c.Topo), "仅物理核"); };
            Tip.Attach(phys, "仅保留每个物理核心的主线程（关闭 SMT/超线程同核干扰），按真实拓扑计算掩码。");
            acts.Children.Add(phys);

            Btn ccd0 = new Btn("CCD0", BtnKind.Ghost);
            _btnCcd0 = ccd0;
            ccd0.Height = T.BtnH;
            ccd0.Compact = true;
            ccd0.Margin = new Thickness(0, 0, 6, 0);
            ccd0.Click += delegate { ApplyMaskPreset(c, Affinity.CcdMask(c.Topo, 0), "CCD0"); };
            Tip.Attach(ccd0, "将前台进程亲和性限制到第 1 个 CCD（共享同一片 L3 缓存，避免跨 CCD 延迟抖动）。");
            acts.Children.Add(ccd0);

            if (c.Topo.Ccds > 1)
            {
                Btn ccd1 = new Btn("CCD1", BtnKind.Ghost);
                _btnCcd1 = ccd1;
                ccd1.Height = T.BtnH;
                ccd1.Compact = true;
                ccd1.Margin = new Thickness(0, 0, 6, 0);
                ccd1.Click += delegate { ApplyMaskPreset(c, Affinity.CcdMask(c.Topo, 1), "CCD1"); };
                Tip.Attach(ccd1, "将前台进程亲和性限制到第 2 个 CCD（可与 CCD0 对比压测或隔离后台负载）。");
                acts.Children.Add(ccd1);
            }
            else
            {
                _btnCcd1 = null;
            }

            Btn free = new Btn("全核恢复", BtnKind.Ghost);
            _btnFree = free;
            free.Height = T.BtnH;
            free.Compact = true;
            free.Click += delegate { RestoreAll(c); };
            Tip.Attach(free, "将前台进程亲和性恢复为全部逻辑处理器（不改动当前进程优先级）。");
            acts.Children.Add(free);
            return acts;
        }

        /// <summary>
        /// 每簇平均负载 + 60 秒迷你走势。列宽与画布的簇宽一致（同样的 CcdGap），
        /// 所以数字与走势正好落在对应簇的正下方，不需要额外标注去指认。
        /// </summary>
        private UIElement BuildClusters(Core c)
        {
            int n = Math.Max(1, c.Topo.Ccds);
            _ccdRun = new Run[n];
            _ccdSub = new TextBlock[n];
            _spark = new Sparkline[n];
            _sparkCount = new int[n];
            _sparkShown = new int[n];
            _ccdShown = new int[n];
            for (int i = 0; i < n; i++) { _sparkShown[i] = -1; _ccdShown[i] = int.MinValue; }

            Grid g = new Grid();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CcdGap) });
                g.ColumnDefinitions.Add(new ColumnDefinition());

                Grid cell = new Grid();
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                cell.ColumnDefinitions.Add(new ColumnDefinition());
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Run vr;
                TextBlock val = Number("—", " %", T.FsNumL, T.Hi, out vr);
                _ccdRun[i] = vr;
                val.VerticalAlignment = VerticalAlignment.Bottom;
                cell.Children.Add(val);

                _spark[i] = new Sparkline((int)(SparkSec * 2));
                _spark[i].Height = 18;

                // 走势放在一个浅凹槽里：没有历史时它仍然是一块"待测区域"，
                // 而不是数字与标签之间的一段莫名空白。
                Border well = new Border();
                well.Background = T.S(0x080C11);
                well.BorderBrush = T.S(T.LineSoft);
                well.BorderThickness = new Thickness(1);
                well.CornerRadius = new CornerRadius(T.RadiusSm);
                well.Padding = new Thickness(5, 2, 5, 2);
                well.VerticalAlignment = VerticalAlignment.Bottom;
                well.Margin = new Thickness(0, 0, 0, 1);
                well.Child = _spark[i];
                Grid.SetColumn(well, 2);
                cell.Children.Add(well);

                _ccdSub[i] = Ui.Mono("采样 0 s", T.FsNano, T.Mute);
                _ccdSub[i].VerticalAlignment = VerticalAlignment.Bottom;
                _ccdSub[i].Margin = new Thickness(8, 0, 0, 4);
                Grid.SetColumn(_ccdSub[i], 3);
                cell.Children.Add(_ccdSub[i]);

                Tip.Attach(cell, "本簇全部逻辑处理器负载的算术平均（1 秒窗口，500ms 刷新）；右侧是最近 60 秒的走势。\r\n走势的形状比数字有用：34% 是刚从 80% 掉下来，还是一直平稳，一眼就能分开。");

                Grid.SetColumn(cell, i * 2);
                g.Children.Add(cell);
            }
            return g;
        }
    }
}
