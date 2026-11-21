// ============================================================================
//  Processes.Table.cs — 列网格 / 表头排序 / 行骨架 / 渲染 / 空态（进程页分片）
//
//  列宽在这里是唯一真源：Columnize() 同时喂给表头、每一行与 CPU 刻度尺。
//  以前行里自己算 x、表头另抄一份数字，窗口一变就错位——读者看到的是"标题和数字对不上"。
//
//  渲染只改 Text / Value / Visibility：2 s 一拍 × 16 行，任何 new 都会变成持续的 GC 压力。
//  字符串先比整数刻度（PID / 线程数 / 0.1% CPU / 1MB 内存），没变就不拼。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Processes : Page
    {

        // ---- 进程表 ----------------------------------------------------------
        private UIElement BuildTable(Core c)
        {
            StackPanel outer = new StackPanel();

            Grid bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition());

            Ui.SegTabItem[] tabs = new Ui.SegTabItem[]
            {
                new Ui.SegTabItem("all", "全部", "显示全部进程排行"),
                new Ui.SegTabItem("high", "高负载", "仅显示 CPU >= 2.0% 或内存 >= 300 MB 的活跃进程"),
                new Ui.SegTabItem("tuned", "已锁核或节能", "仅显示已自定义亲和锁核或启用 EcoQoS 能效模式的进程")
            };
            _viewTabs = new Ui.SegTabs(tabs, 0);
            _viewTabs.Changed += delegate
            {
                string next = _viewTabs.SelectedKey;
                if (string.IsNullOrEmpty(next) || next == _viewMode) return;
                _viewMode = next;
                _expandedPid = 0;
                Sample();
            };
            bar.Children.Add(_viewTabs);

            string topoInfo = "TOPOLOGY \u2014";
            if (c != null && c.Topo != null && c.Topo.Logical > 0)
            {
                topoInfo = c.Topo.Logical + " 核";
                if (c.Topo.Ccds > 1) topoInfo += " · " + c.Topo.Ccds + " CCD";
                if (c.Topo.Hybrid) topoInfo += " · 大小核";
                topoInfo += " · 点行或「调优」展开精确控制";
            }
            TextBlock rightHint = Ui.Mono(topoInfo, T.FsNano, T.Mute);
            rightHint.HorizontalAlignment = HorizontalAlignment.Right;
            rightHint.VerticalAlignment = VerticalAlignment.Center;
            rightHint.TextTrimming = TextTrimming.CharacterEllipsis;
            rightHint.Margin = new Thickness(14, 0, 0, 0);
            Grid.SetColumn(rightHint, 1);
            bar.Children.Add(rightHint);
            outer.Children.Add(bar);
            outer.Children.Add(Gap(10));

            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _rows = new StackPanel();
            for (int i = 0; i < MaxRows; i++)
            {
                _skel[i] = BuildRow(i);
                _rows.Children.Add(_skel[i].Root);
            }

            _emptyHost = new Border();
            _emptyHost.Height = 360;
            _emptyHost.Visibility = Visibility.Collapsed;
            _emptyShown = "";

            Grid list = new Grid();
            list.Children.Add(_rows);
            list.Children.Add(_emptyHost);
            Grid.SetRow(list, 1);
            body.Children.Add(list);

            // 表头后加 = 画在行之上；吸顶时它就是盖住滚过去的内容的那块板。
            _headBand = BuildHeadBand();
            Panel.SetZIndex(_headBand, 10);
            body.Children.Add(_headBand);

            outer.Children.Add(body);
            return Ui.Panel(outer, T.PadPanel);
        }

        /// <summary>表头与每一行共用这份列定义——同一页出现两套列宽就是失败。</summary>
        private static void Columnize(Grid g)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition());                                      // 0 进程名（弹性）
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColCpu) });     // 1 CPU%
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColMem) });     // 2 内存
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColPri) });     // 3 优先级徽标
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColOp) });      // 4 操作
        }

        private Border BuildHeadBand()
        {
            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            Columnize(g);

            HeadCell(g, "进程名", 0, HorizontalAlignment.Left, SortName);
            _cpuHead = HeadCell(g, "CPU %（0–2%）", 1, HorizontalAlignment.Right, SortCpu);
            HeadCell(g, "内存 MB", 2, HorizontalAlignment.Right, SortMem);
            HeadCell(g, "优先级", 3, HorizontalAlignment.Left, SortPri);
            HeadCell(g, "操作", 4, HorizontalAlignment.Right, -1);

            // CPU 列下的刻度尺：条只有在知道量程时才是读数，不是装饰。
            CpuRuler ruler = new CpuRuler();
            ruler.Height = 7;
            ruler.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(ruler, 1);
            Grid.SetRow(ruler, 1);
            g.Children.Add(ruler);
            Tip.Attach(ruler, "条长 = 该进程 CPU 占量程的比例。量程取本帧最大值向上对齐到固定档位"
                             + "（2/3/4/5/6/8/10…100%），最大值掉到量程 55% 以下才降档；百分比按整机逻辑核归一。");

            Border band = new Border();
            band.Child = g;
            band.Background = Brushes.Transparent;
            band.BorderBrush = T.S(T.Line);
            band.BorderThickness = new Thickness(0, 0, 0, 1);
            // 负边距让表头在吸顶时横跨面板内宽，而不是只盖住内容区。
            band.Margin = new Thickness(-17, 0, -17, 0);
            band.Padding = new Thickness(17, 2, 17, 7);
            band.SnapsToDevicePixels = true;
            _headShift = new TranslateTransform(0, 0);
            band.RenderTransform = _headShift;
            return band;
        }

        /// <summary>可排序的列标题：标题 + 方向箭头；点一次排序，再点反向。</summary>
        private TextBlock HeadCell(Grid g, string text, int col, HorizontalAlignment ha, int mode)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.HorizontalAlignment = ha;
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Background = Brushes.Transparent;

            TextBlock t = Ui.Label(text, mode >= 0 ? T.Text : T.Mute);
            if (mode >= 0)
            {
                TextBlock arrow = Ui.Mono("", T.FsNano, T.Accent);
                arrow.Margin = new Thickness(0, 0, 5, 0);
                sp.Children.Add(arrow);
                _sortArrows[mode] = arrow;
                sp.Cursor = Cursors.Hand;
                int m = mode;
                sp.MouseLeftButtonDown += delegate { ToggleSort(m); };
                Tip.Attach(sp, "点列标题排序；再点一次反向。当前顺序写在标题左侧，量程写在 CPU 标题里。");
            }
            sp.Children.Add(t);
            if (col == 1 || col == 2) t.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(sp, col);
            g.Children.Add(sp);
            return t;
        }

        private void ToggleSort(int mode)
        {
            if (_sort == mode) _sortDesc = !_sortDesc;
            else { _sort = mode; _sortDesc = mode != SortName; }   // 名称默认升序，数值默认降序
            SyncSort();
            Sample();
        }

        /// <summary>排序是状态，不是装饰：只有当前列带箭头，且箭头指向真实方向。</summary>
        private void SyncSort()
        {
            for (int i = 0; i < _sortArrows.Length; i++)
            {
                TextBlock a = _sortArrows[i];
                if (a == null) continue;
                string s = i == _sort ? (_sortDesc ? "▼" : "▲") : "";
                if (a.Text != s) a.Text = s;
            }
            if (_cpuHead != null)
            {
                string s = "CPU %（0–" + _scaleCpu.ToString("0") + "%）";
                if (_cpuHead.Text != s) _cpuHead.Text = s;
            }
        }

        private static TextBlock Cell(Grid row, int col, TextBlock t, HorizontalAlignment ha, double pad)
        {
            t.HorizontalAlignment = ha;
            if (pad != 0) t.Margin = new Thickness(0, 0, pad, 0);
            Grid.SetColumn(t, col);
            row.Children.Add(t);
            return t;
        }

        private Row BuildRow(int i)
        {
            Row r = new Row();
            Border root = new Border();
            root.Visibility = Visibility.Collapsed;
            root.BorderThickness = new Thickness(0, 0, 0, 1);
            root.BorderBrush = T.S(T.LineSoft);
            root.Background = Brushes.Transparent;
            root.SnapsToDevicePixels = true;
            r.Root = root;

            r.Host = new StackPanel();
            root.Child = r.Host;

            Grid row = new Grid();
            row.Height = RowH;
            row.Background = Brushes.Transparent;
            Columnize(row);
            r.Summary = row;
            r.Host.Children.Add(row);

            // 0 进程名 + EcoQoS / 锁核状态徽标 + 说明行（PID / 线程数）
            StackPanel nm = new StackPanel();
            nm.VerticalAlignment = VerticalAlignment.Center;
            nm.Margin = new Thickness(0, 0, 14, 0);

            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            r.Name = Ui.Tx("—", T.FsBody, T.Hi, T.FCn);
            r.Name.TextTrimming = TextTrimming.CharacterEllipsis;
            r.Name.TextWrapping = TextWrapping.NoWrap;
            r.Name.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(r.Name);

            r.EcoTag = MakeBadge("EcoQoS", T.Ok);
            r.EcoTag.Box.Margin = new Thickness(7, 0, 0, 0);
            r.EcoTag.Box.Visibility = Visibility.Collapsed;
            Grid.SetColumn(r.EcoTag.Box, 1);
            top.Children.Add(r.EcoTag.Box);

            r.AffTag = MakeBadge("CCD0", T.Accent);
            r.AffTag.Box.Margin = new Thickness(5, 0, 0, 0);
            r.AffTag.Box.Visibility = Visibility.Collapsed;
            Grid.SetColumn(r.AffTag.Box, 2);
            top.Children.Add(r.AffTag.Box);

            nm.Children.Add(top);

            r.Sub = Ui.Mono("", T.FsNano, T.Mute);
            r.Sub.Margin = new Thickness(0, 3, 0, 0);
            r.Sub.TextTrimming = TextTrimming.CharacterEllipsis;
            r.Sub.TextWrapping = TextWrapping.NoWrap;
            nm.Children.Add(r.Sub);
            Grid.SetColumn(nm, 0);
            row.Children.Add(nm);

            // 1 CPU%：数值 + 条（条是量，数字是读数）
            Grid cpu = new Grid();
            cpu.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cpu.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cpu.VerticalAlignment = VerticalAlignment.Center;
            cpu.Margin = new Thickness(0, 0, 12, 0);
            r.Cpu = Ui.Mono("—", T.FsSmall, T.Mute);
            r.Cpu.HorizontalAlignment = HorizontalAlignment.Right;
            cpu.Children.Add(r.Cpu);
            r.Bar = new CpuBar();
            r.Bar.Height = 3;
            r.Bar.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(r.Bar, 1);
            cpu.Children.Add(r.Bar);
            Grid.SetColumn(cpu, 1);
            row.Children.Add(cpu);

            // 2 内存
            r.Mem = Cell(row, 2, Ui.Mono("—", T.FsNano, T.Dim), HorizontalAlignment.Right, 12);

            // 3 优先级徽标（读不到时同一个位置换成"需管理员"，不占第二枚）
            r.Pri = MakeBadge("—", T.Dim);
            Grid.SetColumn(r.Pri.Box, 3);
            row.Children.Add(r.Pri.Box);
            Tip.Attach(r.Pri.Box, "点「优先级」列标题可全局排序；受保护进程无权读取时显示「需管理员」。");

            // 4 操作：快捷锁核 + 展开精确调优
            StackPanel ops = new StackPanel();
            ops.Orientation = Orientation.Horizontal;
            ops.HorizontalAlignment = HorizontalAlignment.Right;
            ops.VerticalAlignment = VerticalAlignment.Center;

            Row captured = r;
            r.Pin = new Btn("CCD0", BtnKind.Quiet);
            r.Pin.Height = T.BtnHSm;
            r.Pin.Compact = true;
            r.Pin.Margin = new Thickness(0, 0, 6, 0);
            r.Pin.Click += delegate { PinRow(captured); };
            ops.Children.Add(r.Pin);

            r.ExpandBtn = new Btn("调优", BtnKind.Quiet);
            r.ExpandBtn.Height = T.BtnHSm;
            r.ExpandBtn.Compact = true;
            r.ExpandBtn.Click += delegate { ToggleExpand(captured); };
            Tip.Attach(r.ExpandBtn, "展开该进程的精确控制区：优先级、亲和锁核、EcoQoS 能效开关与建档。");
            ops.Children.Add(r.ExpandBtn);

            Grid.SetColumn(ops, 4);
            row.Children.Add(ops);

            r.DetailBox = new Border();
            r.DetailBox.Visibility = Visibility.Collapsed;
            r.Host.Children.Add(r.DetailBox);

            row.MouseEnter += delegate { row.Background = T.S(T.LineFaint); };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            row.MouseLeftButtonDown += delegate (object sender, MouseButtonEventArgs e)
            {
                if (IsInsideControl(e.OriginalSource as DependencyObject, ops)) return;
                ToggleExpand(captured);
            };
            return r;
        }

        private static bool IsInsideControl(DependencyObject src, DependencyObject target)
        {
            while (src != null)
            {
                if (src == target) return true;
                src = VisualTreeHelper.GetParent(src);
            }
            return false;
        }

        /// <summary>
        /// CPU 条：绝对刻度 0..N 的一段实心条。OnRender 只画矩形与线——
        /// 无几何、无画笔分配、无字符串；渐变按条宽绝对定位后冻结共用。
        /// 没有读数时只留基线（"没测到"），有读数但为 0 时留下整条轨道（"测到了 0"）：两者可区分。
        /// </summary>
        private sealed class CpuBar : FrameworkElement
        {
            private double _v;
            private bool _known;
            private static LinearGradientBrush _fill;
            private static uint _fillAccent;

            public CpuBar()
            {
                IsHitTestVisible = false;
                SnapsToDevicePixels = true;
                EnsureFill();
            }

            public double Value
            {
                get { return _v; }
                set
                {
                    double v = value;
                    if (v < 0) v = 0;
                    if (v > 1) v = 1;
                    if (v == _v) return;
                    _v = v;
                    InvalidateVisual();
                }
            }

            public bool Known
            {
                set { if (_known == value) return; _known = value; InvalidateVisual(); }
            }

            private static void EnsureFill()
            {
                if (_fill != null && _fillAccent == T.Accent) return;
                LinearGradientBrush g = new LinearGradientBrush();
                g.StartPoint = new Point(0, 0);
                g.EndPoint = new Point(ColCpu - 12, 0);
                g.MappingMode = BrushMappingMode.Absolute;
                g.GradientStops.Add(new GradientStop(T.C(T.Accent), 0));
                g.GradientStops.Add(new GradientStop(T.C(T.AccentDim), 1));
                g.Freeze();
                _fill = g;
                _fillAccent = T.Accent;
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w < 8 || h < 2) return;

                if (!_known)
                {
                    dc.DrawRectangle(T.S(0x10FFFFFF), null, new Rect(0, h - 1, w, 1));
                    return;
                }
                dc.DrawRectangle(T.S(0x0CFFFFFF), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(T.S(0x16FFFFFF), null, new Rect(0, h - 1, w, 1));
                if (_v <= 0) return;
                EnsureFill();
                double fw = w * _v;
                if (fw < 1) fw = 1;
                dc.DrawRectangle(_fill, null, new Rect(0, 0, fw, h));
                if (fw < w - 1) dc.DrawRectangle(T.S(T.AccentA(0xE6)), null, new Rect(fw - 1, 0, 1, h));
            }
        }

        /// <summary>CPU 量程刻度：只有横线与 5 个短齿，画在表头下面与条对齐。</summary>
        private sealed class CpuRuler : FrameworkElement
        {
            public CpuRuler()
            {
                IsHitTestVisible = false;
                SnapsToDevicePixels = true;
                Margin = new Thickness(0, 0, 12, 0);
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w < 8 || h < 3) return;
                dc.DrawRectangle(T.S(0x18FFFFFF), null, new Rect(0, h - 1, w, 1));
                Pen tick = T.P(0x2AFFFFFF, 1);
                for (int i = 0; i <= 4; i++)
                {
                    double x = Math.Round(w * i / 4.0) + 0.5;
                    dc.DrawLine(tick, new Point(x, h - 1), new Point(x, h - 1 - ((i == 0 || i == 4) ? 5 : 3)));
                }
            }
        }

        // ---- 渲染（只改文本/颜色/可见性，不建元素、不触发布局）----------------
        private static void SetText(TextBlock t, string s)
        {
            if (t.Text != s) t.Text = s;
        }

        private void Render()
        {
            if (_rows == null) return;
            WriteCount();
            WriteScale();
            long ccd0 = Affinity.CcdMask(C.Topo, 0);
            long ccd1 = (C.Topo != null && C.Topo.Ccds >= 2) ? Affinity.CcdMask(C.Topo, 1) : 0;
            long phys = Affinity.PhysicalOnlyMask(C.Topo);
            long all = Affinity.AllMask();
            for (int i = 0; i < MaxRows; i++)
            {
                Row r = _skel[i];
                if (i >= _last.Count)
                {
                    if (r.Root.Visibility != Visibility.Collapsed) r.Root.Visibility = Visibility.Collapsed;
                    continue;
                }
                PaintRow(r, _last[i], ccd0, ccd1, phys, all);
                if (r.Root.Visibility != Visibility.Visible) r.Root.Visibility = Visibility.Visible;
            }
            EmptyState();
        }

        private void PaintRow(Row r, PStat s, long ccd0, long ccd1, long phys, long all)
        {
            r.CurPid = s.Pid;
            r.CurName = s.Name;
            r.CurImage = s.Image;
            r.CurMask = s.Mask;
            r.CurPriClass = s.PriClass;
            r.CurPriLabel = s.PriLabel;
            r.CurEco = s.EcoQoS;
            r.CurEcoKnown = s.EcoQoSKnown;
            r.Pinned = s.Mask > 0 && ccd0 > 0 && s.Mask == ccd0;
            SetText(r.Name, s.Name == null ? "—" : s.Name);

            // EcoQoS 视觉徽标
            Visibility ecoVis = s.EcoQoS ? Visibility.Visible : Visibility.Collapsed;
            if (r.EcoTag.Box.Visibility != ecoVis) r.EcoTag.Box.Visibility = ecoVis;
            if (s.EcoQoS) r.EcoTag.Paint("EcoQoS", T.Ok);

            // 亲和锁核徽标
            string affBadge = "";
            if (s.Mask > 0 && all > 0 && s.Mask != all)
            {
                if (ccd0 > 0 && s.Mask == ccd0) affBadge = "CCD0";
                else if (ccd1 > 0 && s.Mask == ccd1) affBadge = "CCD1";
                else if (phys > 0 && s.Mask == phys) affBadge = "物理核";
                else affBadge = "锁核";
            }
            Visibility affVis = affBadge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (r.AffTag.Box.Visibility != affVis) r.AffTag.Box.Visibility = affVis;
            if (affBadge.Length > 0) r.AffTag.Paint(affBadge, T.Accent);

            // 整数刻度先比：说明行里的 PID 与线程数一年也不变几次，不该每拍拼一次字符串。
            if (s.Pid != r.SubPid || s.Threads != r.SubThreads)
            {
                r.SubPid = s.Pid;
                r.SubThreads = s.Threads;
                r.Sub.Text = "PID " + s.Pid + (s.Threads >= 0 ? " · " + s.Threads + " 线程" : "");
            }

            PaintCpu(r, s);

            int mem = s.WsMb >= 0 ? (int)Math.Round(s.WsMb) : -1;
            if (mem != r.MemShown)
            {
                r.MemShown = mem;
                r.Mem.Text = mem < 0 ? "—" : mem.ToString();
            }

            PaintPrio(r, s);
            if (r.PinAll != r.Pinned)
            {
                r.PinAll = r.Pinned;
                r.Pin.Label = r.Pinned ? "全核" : "CCD0";
            }
            SyncExpandedRow(r);
        }

        /// <summary>CPU 格：绝对量程，条是量、数字是读数；没测到就只留轨道并写 —。</summary>
        private void PaintCpu(Row r, PStat s)
        {
            if (!s.Measured)
            {
                r.Bar.Known = false;
                r.Bar.Value = 0;
                if (r.CpuTier != 0)
                {
                    r.CpuTier = 0;
                    r.CpuShown = int.MinValue;
                    r.Cpu.Text = "—";
                    r.Cpu.Foreground = T.S(T.Mute);
                }
                return;
            }
            r.Bar.Known = true;
            r.Bar.Value = s.CpuPct / _scaleCpu;

            int shown = (int)Math.Round(s.CpuPct * 10);         // 0.1% 刻度
            int tier = s.CpuPct >= 25 ? 2 : 1;
            if (shown == r.CpuShown && tier == r.CpuTier) return;
            r.CpuShown = shown;
            if (tier != r.CpuTier)
            {
                r.CpuTier = tier;
                r.Cpu.Foreground = T.S(tier == 2 ? T.Warn : T.Text);
            }
            r.Cpu.Text = (shown / 10.0).ToString("0.0");
        }

        /// <summary>优先级徽标：显示 PriorityLabel（实时/高/高于/正常/低于/空闲），无权时显示需管理员。</summary>
        private static void PaintPrio(Row r, PStat s)
        {
            if (s.PriClass == 0 || string.IsNullOrEmpty(s.PriLabel) || s.PriLabel == "\u2014")
            {
                r.Pri.Paint("需管理员", T.Dim);
                return;
            }
            uint tint = s.PriRank >= 6 ? T.Warn : (s.PriRank >= 4 ? T.Accent : (s.PriRank <= 2 ? T.Mute : T.Dim));
            r.Pri.Paint(s.PriLabel, tint);
        }

        private void WriteCount()
        {
            string cnt;
            if (_filterKey.Length > 0 || _viewMode != "all") cnt = "命中 " + _matched + " / " + _total;
            else if (_total > MaxRows) cnt = "前 " + Math.Min(MaxRows, _total) + " / " + _total;
            else cnt = _total + " 个进程";
            if (cnt != _countShown)
            {
                _countShown = cnt;
                if (_count != null) _count.Text = cnt;
                if (_sumTotalVal != null) _sumTotalVal.Text = _total > 0 ? _total.ToString() : "\u2014";
                if (_sumHotVal != null) _sumHotVal.Text = _matched > 0 ? _matched.ToString() : "0";
            }
        }

        private void WriteScale()
        {
            // 量程：本帧最大值向上对齐到档位。没有读数时维持上一档，不突然缩到 2%。
            double max = 0;
            for (int i = 0; i < _last.Count; i++)
                if (_last[i].Measured && _last[i].CpuPct > max) max = _last[i].CpuPct;
            double next = PickScale(max);
            if (Math.Abs(next - _scaleCpu) < 0.001) return;
            _scaleCpu = next;
            SyncSort();      // CPU 标题里写着量程，换档必须同步
        }

        // ---- 空态：三种各给画面 ----------------------------------------------
        private void EmptyState()
        {
            if (_last.Count > 0) { ShowEmpty(false, "", "", "", "", 0, false); return; }
            if (!_sampled)
            {
                ShowEmpty(true, "boot", Glyph.Processes, "正在枚举进程…",
                    "第一个采样窗口只建立基线；2 秒后才有可比较的 CPU 差分。", T.Accent, false);
                return;
            }
            if (_filterKey.Length > 0)
            {
                ShowEmpty(true, "hit:" + _filterKey + ":" + _viewMode, Glyph.Search, "没有匹配「" + _filterKey + "」的进程",
                    "过滤在全部 " + _total + " 个进程里按名字与当前视图筛选；清空过滤即可看到排行。",
                    T.Accent, true);
                return;
            }
            if (_viewMode == "high")
            {
                ShowEmpty(true, "view:high", Glyph.Processes, "当前无高负载进程",
                    "「高负载」仅展示 CPU >= 2.0% 或内存 >= 300 MB 的活跃进程；切换到「全部」即可查看完整列表。",
                    T.Accent, true);
                return;
            }
            if (_viewMode == "tuned")
            {
                ShowEmpty(true, "view:tuned", Glyph.Processes, "未检测到已锁核或 EcoQoS 进程",
                    "「已锁核或节能」筛选已自定义 CPU 亲和掩码或启用 Win11 EcoQoS 能效模式的进程。",
                    T.Accent, true);
                return;
            }
            ShowEmpty(true, "none", Glyph.Warn, "未枚举到任何进程",
                "Process.GetProcesses 返回 0 项。常见原因：安全软件拦截了进程枚举，或当前会话没有可见性。",
                T.Warn, false);
        }

        private void ShowEmpty(bool show, string key, string glyph, string title, string reason, uint tint, bool withClear)
        {
            if (show && key != _emptyShown)
            {
                _emptyShown = key;
                _emptyHost.Child = Ui.Empty(glyph, title, reason, tint, withClear ? ClearFilterButton() : null);
            }
            Visibility v = show ? Visibility.Visible : Visibility.Collapsed;
            if (_emptyHost.Visibility != v) _emptyHost.Visibility = v;
        }

        private Btn ClearFilterButton()
        {
            Btn b = new Btn(_filterKey.Length > 0 ? "清空过滤" : "显示全部", BtnKind.Ghost);
            b.Height = T.BtnH;
            b.Click += delegate
            {
                if (_filter != null && _filter.Text.Length > 0) _filter.Text = "";
                if (_viewMode != "all" && _viewTabs != null) { _viewMode = "all"; _viewTabs.Select(0, false); Sample(); }
            };
            return b;
        }

        /// <summary>量程档位：只升不轻易降。降档会让同一进程的条在两次刷新之间突然变长。</summary>
        private double PickScale(double max)
        {
            double need = max * 1.06;
            double next = Ladder[Ladder.Length - 1];
            for (int i = 0; i < Ladder.Length; i++)
                if (Ladder[i] >= need) { next = Ladder[i]; break; }
            if (next < _scaleCpu && max > _scaleCpu * 0.55) next = _scaleCpu;
            return next;
        }

        // ---- 吸顶 ------------------------------------------------------------
        // 滚的是 Shell 的 ThinScroll，所以向上找祖先 ScrollViewer，按偏移抬表头
        //（只改 RenderTransform，不触发 measure/arrange）。
        private void HookScroll()
        {
            if (_sv != null || _pageRoot == null) return;
            DependencyObject d = _pageRoot;
            while (d != null)
            {
                ScrollViewer sv = d as ScrollViewer;
                if (sv != null)
                {
                    _sv = sv;
                    sv.ScrollChanged += delegate { PinHead(); };
                    PinHead();
                    return;
                }
                d = VisualTreeHelper.GetParent(d);
            }
        }

        private void PinHead()
        {
            if (_sv == null || _headBand == null || _rows == null || _headShift == null) return;
            FrameworkElement content = _sv.Content as FrameworkElement;
            if (content == null) return;
            double y;
            try { y = _headBand.TransformToAncestor(content).Transform(new Point(0, 0)).Y; }
            catch (Exception) { return; }      // 还没进可视树：下一帧再问
            double dy = _sv.VerticalOffset - (y - _headShift.Y);
            if (dy < 0) dy = 0;
            double max = _rows.ActualHeight;
            if (dy > max) dy = max;
            if (Math.Abs(_headShift.Y - dy) < 0.5) return;
            _headShift.Y = dy;
            // 抬起来时必须自己带底色，否则行会从字缝里穿过去。
            _headBand.Background = dy > 0.5 ? T.S(T.PlateLit) : Brushes.Transparent;
        }

        private static string ShortPrio(string p)
        {
            if (string.IsNullOrEmpty(p)) return "—";
            if (p == "Normal") return "NORM";
            if (p == "High") return "HIGH";
            if (p == "AboveNormal") return "ABOVE";
            if (p == "BelowNormal") return "BELOW";
            if (p == "Idle") return "IDLE";
            if (p == "RealTime") return "REALTIME";
            return p.ToUpperInvariant();
        }
    }
}
