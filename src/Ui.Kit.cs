// ============================================================================
//  Ui.Kit.cs — Rev78 APEX 视觉套件
//  蓝图基底 / 自适应页根 / 读数原子 / 空态语言 / 端帽分隔线
//  这一层的存在理由：把"每个页面各写一遍"的排版决定，收敛成可复用的形状。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Go
{
    public static partial class Ui
    {
        // ====================================================================
        //  蓝图基底
        // ====================================================================
        private static DrawingBrush _blueprint;

        /// <summary>
        /// 仪器面板的"图纸"底：16 细格 + 80 粗格，极低透明度。
        /// 它解决的是一个真实的设计问题——页面内容短于视口时，底部会剩下一整片纯黑，
        /// 看起来像"这页没做完"。把空白变成有刻度的基底之后，空白本身就成了仪表的一部分：
        /// 读者看到的是"待测区域"，而不是"渲染失败"。
        /// </summary>
        public static DrawingBrush Blueprint()
        {
            if (_blueprint != null) return _blueprint;

            // Rev120 HYPERION：告别生硬满屏细网格，改用精密星点矩阵 + 64px 光学十字准星，通透且充满纵深感。
            DrawingGroup dg = new DrawingGroup();

            GeometryGroup dots = new GeometryGroup();
            for (int y = 16; y < 64; y += 16)
                for (int x = 16; x < 64; x += 16)
                    dots.Children.Add(new RectangleGeometry(new Rect(x, y, 1, 1)));
            dg.Children.Add(new GeometryDrawing(T.S(0x0ECFE6FF), null, dots));

            GeometryGroup cross = new GeometryGroup();
            cross.Children.Add(new RectangleGeometry(new Rect(0, 0, 5, 1)));
            cross.Children.Add(new RectangleGeometry(new Rect(0, 0, 1, 5)));
            dg.Children.Add(new GeometryDrawing(T.S(0x1ACFE6FF), null, cross));

            dg.Freeze();

            DrawingBrush b = new DrawingBrush(dg);
            b.TileMode = TileMode.Tile;
            b.Viewport = new Rect(0, 0, 64, 64);
            b.ViewportUnits = BrushMappingMode.Absolute;
            b.Viewbox = new Rect(0, 0, 64, 64);
            b.ViewboxUnits = BrushMappingMode.Absolute;
            b.Stretch = Stretch.None;
            b.Freeze();
            _blueprint = b;
            return b;
        }

        // ====================================================================
        //  启动预热（后台线程，只碰冻结几何）
        // ====================================================================

        private static bool _prewarmed;

        /// <summary>
        /// 把 Glyph 表里所有路径在若干常用尺寸下全部解析、缩放、冻结一遍。
        /// 实测：这一步在后台跑完之后，BuildRail 里的图标段从 235~252ms 降到 102~163ms。
        /// 注意只预热几何——字体做同样的事没有收益（文本首次测量是线程级缓存）。
        /// </summary>
        public static void PrewarmIcons()
        {
            if (_prewarmed) return;
            _prewarmed = true;
            try
            {
                System.Reflection.FieldInfo[] fs = typeof(Glyph).GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                double[] sizes = new double[] { 11, 12, 13, 14, 15, 16, 18, 20, 22, 26, 30 };
                for (int i = 0; i < fs.Length; i++)
                {
                    if (fs[i].FieldType != typeof(string)) continue;
                    string data = fs[i].GetValue(null) as string;
                    if (string.IsNullOrEmpty(data)) continue;
                    for (int s = 0; s < sizes.Length; s++)
                    {
                        try { IconGeometry(data, sizes[s]); }
                        catch { Diag.Hit("icon.prewarm"); }
                    }
                }
                // 品牌标不是 Glyph 字段，且导轨用 26 / 托盘菜单用 15：必须按 IconGeometry 预热
                // （只 T.G 缓存的是未缩放基几何，BuildRail 仍会在 UI 线程首次缩放）。
                try { IconGeometry(Brand.MarkPath, 26); IconGeometry(Brand.MarkPath, 15); } catch { Diag.Hit("icon.brand"); }
                try { IconGeometry(Brand.MarkInnerPath, 26); } catch { Diag.Hit("icon.brand.inner"); }
                // 横幅关闭按钮的内联 X 路径（BuildBanner 期建），同样提前缩放冻结。
                try { IconGeometry("M7 7 L17 17 M17 7 L7 17", 11); } catch { Diag.Hit("icon.close"); }
            }
            catch (Exception ex) { Diag.Fail("icons.prewarm", ex); }
        }

        // ====================================================================
        //  自适应页根
        // ====================================================================

        /// <summary>
        /// 页面根：页头 / 18px 间隔 / 可伸展主体。
        /// 主体落在 * 行上——配合 ThinScroll 的最小高度贴合，短页面会把剩余高度
        /// 交给主体去分配，而不是全部堆在底部变成空白。
        /// </summary>
        public static Grid PageRoot(UIElement header, UIElement body)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(T.PadX, 16, T.PadX, 26);
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            if (header != null) g.Children.Add(header);
            if (body != null)
            {
                Grid.SetRow(body, 2);
                g.Children.Add(body);
            }
            return g;
        }

        /// <summary>
        /// 上下两段，中间用 * 撑开：把次要说明压到容器底部。
        /// 短内容面板因此不再"上重下空"，而是有一根有意为之的地脚。
        /// </summary>
        public static Grid Spread(UIElement top, UIElement bottom)
        {
            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (top != null) g.Children.Add(top);
            if (bottom != null)
            {
                Grid.SetRow(bottom, 2);
                g.Children.Add(bottom);
            }
            return g;
        }

        // ====================================================================
        //  读数原子
        // ====================================================================

        /// <summary>
        /// 数值 + 单位：同一条基线，单位缩到 34% 并转为等宽弱色。
        /// 旧写法把数字和单位放成两个 TextBlock 再各自 VerticalAlignment=Center，
        /// 结果单位永远黏在数字的垂直中线上——看上去像浮在数字旁边，而不是它的量纲。
        /// </summary>
        public static TextBlock UnitNum(string value, string unit, double size, uint col)
        {
            TextBlock t = new TextBlock();
            t.FontFamily = T.FDisplay;
            t.FontWeight = FontWeights.SemiBold;
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Ideal);
            t.VerticalAlignment = VerticalAlignment.Center;

            Run v = new Run(value);
            v.FontSize = size;
            v.Foreground = T.S(col);
            t.Inlines.Add(v);

            if (!string.IsNullOrEmpty(unit))
            {
                Run u = new Run(" " + unit);
                u.FontSize = Math.Round(size * 0.34);
                u.FontFamily = T.FMono;
                u.Foreground = T.S(T.Mute);
                t.Inlines.Add(u);
            }
            return t;
        }

        /// <summary>字段读数：左标签右值，行内基线对齐（表格与参数面板通用）。</summary>
        public static Grid Kv(string key, TextBlock value, double keyWidth)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = keyWidth > 0 ? new GridLength(keyWidth) : GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock k = Tx(key, T.FsSmall, T.Text, T.FCn);
            k.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(k);
            if (value != null)
            {
                value.VerticalAlignment = VerticalAlignment.Center;
                value.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(value, 1);
                g.Children.Add(value);
            }
            return g;
        }

        // ====================================================================
        //  空态语言
        // ====================================================================

        /// <summary>把强调色压成低透明底色（0x12 之类的洗涤层）。</summary>
        public static uint Wash(uint tint, byte a)
        {
            return ((uint)a << 24) | (tint & 0x00FFFFFFu);
        }

        /// <summary>
        /// 空态：图标 + 标题 + 原因 + 可选动作，垂直居中。
        /// 关键在"原因"这一行——空白之所以让人焦虑，是因为它不说为什么空。
        /// </summary>
        public static UIElement Empty(string glyph, string title, string reason, uint tint, UIElement action)
        {
            StackPanel sp = new StackPanel();
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.MaxWidth = 460;

            Grid tile = IconTile(glyph, 46, tint, Wash(tint, 0x12), 9);
            tile.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(tile);

            TextBlock t = Tx(title, T.FsBody, T.Hi, T.FCn);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 16, 0, 0);
            sp.Children.Add(t);

            TextBlock r = Tx(reason, T.FsSmall, T.Mute, T.FCn);
            r.HorizontalAlignment = HorizontalAlignment.Center;
            r.TextAlignment = TextAlignment.Center;
            r.TextWrapping = TextWrapping.Wrap;
            r.LineHeight = 19;
            r.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(r);

            if (action != null)
            {
                FrameworkElement fe = action as FrameworkElement;
                if (fe != null)
                {
                    fe.HorizontalAlignment = HorizontalAlignment.Center;
                    fe.Margin = new Thickness(0, 18, 0, 0);
                }
                sp.Children.Add(action);
            }
            return sp;
        }

        // ====================================================================
        //  分隔与刻度
        // ====================================================================

        /// <summary>带强调色端帽的分隔线：本设计语言的签名细节。</summary>
        public static Grid Cap(uint line, uint cap, double capW)
        {
            Grid g = new Grid();
            g.Height = 1;
            g.SnapsToDevicePixels = true;
            Border b = new Border();
            b.Background = T.S(line);
            g.Children.Add(b);
            Border c = new Border();
            c.Background = T.S(cap);
            c.Width = capW;
            c.HorizontalAlignment = HorizontalAlignment.Left;
            g.Children.Add(c);
            return g;
        }

        /// <summary>数据地板：等分的横向量度线，给空图表一个"待测"的底，而不是一块黑。</summary>
        public static Grid Floor(uint col, int rows)
        {
            Grid g = new Grid();
            g.IsHitTestVisible = false;
            for (int i = 0; i < rows; i++) g.RowDefinitions.Add(new RowDefinition());
            for (int i = 1; i < rows; i++)
            {
                Border b = new Border();
                b.Height = 1;
                b.Background = T.S(col);
                b.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(b, i);
                g.Children.Add(b);
            }
            return g;
        }

        // ====================================================================
        //  分段切换条（Ui.SegTabs）
        // ====================================================================

        /// <summary>分段标签项：Key 用于逻辑标识，Label 用于显示，Tip 为可选悬停说明。</summary>
        public sealed class SegTabItem
        {
            public string Key;
            public string Label;
            public string Tip;

            public SegTabItem() { }
            public SegTabItem(string key, string label, string tip)
            {
                Key = key;
                Label = label;
                Tip = tip;
            }
        }

        /// <summary>
        /// 精密仪表分段切换条：沉底槽体 + 选中态抬升底板 + 底部强调色导轨。
        /// 供基准模式切换、调度过滤与视图切换直接复用。
        /// </summary>
        public sealed class SegTabs : Border
        {
            private sealed class TabSlot
            {
                public SegTabItem Item;
                public Grid Root;
                public Border Plate;
                public Border Edge;
                public TextBlock Text;
                public bool Enabled;
                public bool Hover;
            }

            private readonly Grid _host;
            private readonly Border _slider;
            private readonly Border _sliderEdge;
            private readonly TranslateTransform _sliderTx;
            private readonly StackPanel _row;
            private readonly TabSlot[] _slots;
            private int _selected;
            private bool _themeHooked;
            private bool _sliderActive;

            public event EventHandler Changed;
            public event Action<int, string> Selected;

            public SegTabs(string[] labels) : this(ToItems(labels), 0) { }
            public SegTabs(string[] labels, int selected) : this(ToItems(labels), selected) { }
            public SegTabs(SegTabItem[] items, int selected)
            {
                SegTabItem[] list = items ?? new SegTabItem[0];
                _slots = new TabSlot[list.Length];

                CornerRadius = new CornerRadius(T.RadiusMd + 2);
                BorderThickness = new Thickness(1);
                BorderBrush = T.S(T.Line);
                Background = T.S(T.Inset);
                Padding = new Thickness(3);
                MinHeight = 32;
                HorizontalAlignment = HorizontalAlignment.Left;
                VerticalAlignment = VerticalAlignment.Center;
                SnapsToDevicePixels = true;
                UseLayoutRounding = true;

                _host = new Grid();
                _sliderTx = new TranslateTransform(0, 0);
                _slider = new Border();
                _slider.CornerRadius = new CornerRadius(T.RadiusSm + 1);
                _slider.BorderThickness = new Thickness(1);
                _slider.HorizontalAlignment = HorizontalAlignment.Left;
                _slider.VerticalAlignment = VerticalAlignment.Stretch;
                _slider.IsHitTestVisible = false;
                _slider.Visibility = Visibility.Collapsed;
                _slider.RenderTransform = _sliderTx;

                _sliderEdge = new Border();
                _sliderEdge.Height = 2;
                _sliderEdge.CornerRadius = new CornerRadius(1);
                _sliderEdge.Margin = new Thickness(10, 0, 10, 1);
                _sliderEdge.VerticalAlignment = VerticalAlignment.Bottom;
                _sliderEdge.IsHitTestVisible = false;
                _slider.Child = _sliderEdge;
                _host.Children.Add(_slider);

                _row = new StackPanel();
                _row.Orientation = Orientation.Horizontal;
                _row.SizeChanged += delegate { SyncSlider(false); };
                _host.Children.Add(_row);
                Child = _host;

                for (int i = 0; i < list.Length; i++)
                {
                    SegTabItem item = list[i] ?? new SegTabItem("", "", null);
                    TabSlot slot = BuildSlot(i, item);
                    _slots[i] = slot;
                    _row.Children.Add(slot.Root);
                }

                if (_slots.Length > 0)
                {
                    if (selected < 0) selected = 0;
                    if (selected >= _slots.Length) selected = _slots.Length - 1;
                    _selected = selected;
                }
                else
                {
                    _selected = -1;
                }

                RefreshVisuals();

                Loaded += OnLoaded;
                Unloaded += OnUnloaded;
            }

            public int Count { get { return _slots.Length; } }

            public int SelectedIndex
            {
                get { return _selected; }
                set { Select(value, false); }
            }

            public string SelectedKey
            {
                get
                {
                    if (_selected < 0 || _selected >= _slots.Length) return null;
                    return _slots[_selected].Item.Key;
                }
            }

            public string SelectedLabel
            {
                get
                {
                    if (_selected < 0 || _selected >= _slots.Length) return null;
                    return _slots[_selected].Item.Label;
                }
            }

            public void Select(int index, bool raise)
            {
                if (index < 0 || index >= _slots.Length) return;
                if (!_slots[index].Enabled) return;
                bool changed = _selected != index;
                _selected = index;
                RefreshVisuals(raise && changed);
                if (raise && changed) RaiseChanged();
            }

            public bool SelectKey(string key, bool raise)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (string.Equals(_slots[i].Item.Key, key, StringComparison.Ordinal))
                    {
                        Select(i, raise);
                        return true;
                    }
                }
                return false;
            }

            public void SetEnabled(int index, bool enabled)
            {
                if (index < 0 || index >= _slots.Length) return;
                _slots[index].Enabled = enabled;
                _slots[index].Root.IsHitTestVisible = enabled;
                _slots[index].Root.Focusable = enabled;
                _slots[index].Root.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
                RefreshVisuals();
            }

            private static SegTabItem[] ToItems(string[] labels)
            {
                if (labels == null) return new SegTabItem[0];
                SegTabItem[] arr = new SegTabItem[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    string lbl = labels[i] ?? "";
                    arr[i] = new SegTabItem(lbl, lbl, null);
                }
                return arr;
            }

            private TabSlot BuildSlot(int index, SegTabItem item)
            {
                TabSlot slot = new TabSlot();
                slot.Item = item;
                slot.Enabled = true;

                Grid g = new Grid();
                g.MinWidth = 54;
                g.Height = 24;
                g.Margin = index > 0 ? new Thickness(2, 0, 0, 0) : new Thickness(0);
                g.Background = Brushes.Transparent;
                g.Cursor = Cursors.Hand;
                g.Focusable = true;
                g.FocusVisualStyle = null;
                g.SnapsToDevicePixels = true;
                KeyboardNavigation.SetIsTabStop(g, true);
                Hit.Track();

                Border plate = new Border();
                plate.CornerRadius = new CornerRadius(T.RadiusSm);
                plate.BorderThickness = new Thickness(1);
                plate.IsHitTestVisible = false;
                g.Children.Add(plate);

                Border edge = new Border();
                edge.Height = 2;
                edge.CornerRadius = new CornerRadius(1);
                edge.Margin = new Thickness(8, 0, 8, 1);
                edge.VerticalAlignment = VerticalAlignment.Bottom;
                edge.IsHitTestVisible = false;
                g.Children.Add(edge);

                TextBlock tx = Tx(Spaced(item.Label ?? ""), T.FsNano, T.Dim, T.FDisplayReg);
                tx.Margin = new Thickness(11, 0, 11, 1);
                tx.VerticalAlignment = VerticalAlignment.Center;
                tx.HorizontalAlignment = HorizontalAlignment.Center;
                tx.IsHitTestVisible = false;
                g.Children.Add(tx);

                if (!string.IsNullOrEmpty(item.Tip)) Tip.Attach(g, item.Tip);

                slot.Root = g;
                slot.Plate = plate;
                slot.Edge = edge;
                slot.Text = tx;

                int idx = index;
                g.MouseEnter += delegate { slot.Hover = true; PaintSlot(slot, idx == _selected); };
                g.MouseLeave += delegate { slot.Hover = false; PaintSlot(slot, idx == _selected); };
                Pressable.On(g, delegate { Select(idx, true); });
                g.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.Key == Key.Left) { MoveStep(-1); e.Handled = true; }
                    else if (e.Key == Key.Right) { MoveStep(1); e.Handled = true; }
                    else if (e.Key == Key.Return || e.Key == Key.Space) { Select(idx, true); e.Handled = true; }
                };

                return slot;
            }

            private void MoveStep(int delta)
            {
                if (_slots.Length == 0) return;
                int cur = _selected < 0 ? 0 : _selected;
                for (int step = 1; step <= _slots.Length; step++)
                {
                    int next = (cur + delta * step + _slots.Length * 4) % _slots.Length;
                    if (_slots[next].Enabled)
                    {
                        Select(next, true);
                        _slots[next].Root.Focus();
                        break;
                    }
                }
            }

            private void RefreshVisuals() { RefreshVisuals(false); }

            private void RefreshVisuals(bool animate)
            {
                BorderBrush = T.S(T.Line);
                Background = T.S(T.Inset);
                _slider.Background = T.Grad(0xFF21314E, 0xFF152036, 90);
                _slider.BorderBrush = T.S(Wash(T.Accent, 0x72));
                _sliderEdge.Background = T.AccentGrad();
                SyncSlider(animate);
                for (int i = 0; i < _slots.Length; i++)
                    PaintSlot(_slots[i], i == _selected);
            }

            private void SyncSlider(bool animate)
            {
                if (_selected < 0 || _selected >= _slots.Length || !_slots[_selected].Enabled)
                {
                    _slider.Visibility = Visibility.Collapsed;
                    _sliderActive = false;
                    return;
                }
                TabSlot active = _slots[_selected];
                if (!IsLoaded || active.Root.ActualWidth <= 0)
                {
                    _slider.Visibility = Visibility.Collapsed;
                    _sliderActive = false;
                    return;
                }

                double targetX = active.Root.TranslatePoint(new Point(0, 0), _row).X;
                double targetW = active.Root.ActualWidth;
                bool wasActive = _sliderActive;
                _sliderActive = true;
                _slider.Visibility = Visibility.Visible;

                if (wasActive && animate && Ui.Animate && !Rs.ReduceMotion)
                {
                    QuinticEase ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
                    Duration dur = new Duration(TimeSpan.FromMilliseconds(150));
                    DoubleAnimation ax = new DoubleAnimation(targetX, dur);
                    ax.EasingFunction = ease;
                    DoubleAnimation aw = new DoubleAnimation(targetW, dur);
                    aw.EasingFunction = ease;
                    _sliderTx.BeginAnimation(TranslateTransform.XProperty, ax);
                    _slider.BeginAnimation(FrameworkElement.WidthProperty, aw);
                }
                else
                {
                    _sliderTx.BeginAnimation(TranslateTransform.XProperty, null);
                    _slider.BeginAnimation(FrameworkElement.WidthProperty, null);
                    _sliderTx.X = targetX;
                    _slider.Width = targetW;
                }

                if (!wasActive)
                {
                    for (int i = 0; i < _slots.Length; i++)
                        PaintSlot(_slots[i], i == _selected);
                }
            }

            private void PaintSlot(TabSlot slot, bool active)
            {
                if (!slot.Enabled)
                {
                    slot.Plate.Background = Brushes.Transparent;
                    slot.Plate.BorderBrush = Brushes.Transparent;
                    slot.Edge.Visibility = Visibility.Collapsed;
                    slot.Text.Foreground = T.S(T.Mute);
                    slot.Root.Opacity = T.DisabledOpacity;
                    return;
                }
                slot.Root.Opacity = 1.0;

                if (active)
                {
                    if (_sliderActive)
                    {
                        slot.Plate.Background = Brushes.Transparent;
                        slot.Plate.BorderBrush = Brushes.Transparent;
                        slot.Edge.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        slot.Plate.Background = T.Grad(0xFF21314E, 0xFF152036, 90);
                        slot.Plate.BorderBrush = T.S(Wash(T.Accent, 0x72));
                        slot.Edge.Background = T.AccentGrad();
                        slot.Edge.Visibility = Visibility.Visible;
                    }
                    slot.Text.Foreground = T.S(T.Hi);
                }
                else
                {
                    slot.Plate.Background = slot.Hover ? T.S(T.LineFaint) : Brushes.Transparent;
                    slot.Plate.BorderBrush = slot.Hover ? T.S(T.LineSoft) : Brushes.Transparent;
                    slot.Edge.Visibility = Visibility.Collapsed;
                    slot.Text.Foreground = T.S(slot.Hover ? T.Text : T.Dim);
                }
            }

            private void RaiseChanged()
            {
                if (Changed != null) Changed(this, EventArgs.Empty);
                if (Selected != null) Selected(_selected, SelectedKey);
            }

            private void OnLoaded(object sender, RoutedEventArgs e)
            {
                if (!_themeHooked)
                {
                    _themeHooked = true;
                    T.ThemeChanged += RefreshVisuals;
                }
                RefreshVisuals();
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                if (_themeHooked)
                {
                    _themeHooked = false;
                    T.ThemeChanged -= RefreshVisuals;
                }
            }
        }
    }
}
