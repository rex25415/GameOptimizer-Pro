// ============================================================================
//  Tip.cs — 工具提示
//  自定义 ToolTip 模板 + 防溢出定位
//  来源：Ctl.cs（1440 行单体）按职责拆分，代码逐行迁移，行为零变化。
// ============================================================================
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Go
{
    public static class Tip
    {
        private const double MaxTipWidth = 360;
        private const double ContentWidth = 338; // 360 - 2*1 描边 - 2*10 内边距

        private static readonly ControlTemplate TipTemplate = BuildTemplate();

        // 文案在 Attach 时固定，尺寸只需量一次；弱引用表随 ToolTip 回收，不泄漏。
        private sealed class SizeBox { public Size Value; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolTip, SizeBox> _sizes =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ToolTip, SizeBox>();

        public static void Attach(FrameworkElement el, string text)
        {
            if (el == null || string.IsNullOrEmpty(text)) return;

            // 附加属性是轻量写入，留在 Attach；ToolTip 视觉树（模板 + TextBlock + 首次文本测量）
            // 推迟到第一次悬浮/聚焦再建。导轨 14 个 + 各页数十个 ToolTip 因此移出 BuildRail /
            // 页面首建的关键路径——这是启动里最大的一块可延后成本。
            ToolTipService.SetInitialShowDelay(el, 320);
            ToolTipService.SetShowDuration(el, 20000);
            ToolTipService.SetBetweenShowDelay(el, 100);
            ToolTipService.SetPlacement(el, PlacementMode.Relative);
            ToolTipService.SetPlacementTarget(el, el);
            ToolTipService.SetHorizontalOffset(el, 0);
            ToolTipService.SetVerticalOffset(el, 6);
            ToolTipService.SetShowsToolTipOnKeyboardFocus(el, true);

            string captured = text;
            MouseEventHandler onMouse = null;
            KeyboardFocusChangedEventHandler onFocus = null;
            Action buildOnce = delegate
            {
                el.MouseEnter -= onMouse;
                el.GotKeyboardFocus -= onFocus;
                if (el.ToolTip != null) return;
                ToolTip tip = BuildTip(el, captured);
                el.ToolTip = tip;
                Place(el, tip);
            };
            onMouse = delegate { buildOnce(); };
            onFocus = delegate { buildOnce(); };
            el.MouseEnter += onMouse;
            el.GotKeyboardFocus += onFocus;
        }

        private static ToolTip BuildTip(FrameworkElement el, string text)
        {
            ToolTip tip = new ToolTip();
            tip.Background = T.S(T.Panel);
            tip.Foreground = T.S(T.Text);
            tip.BorderBrush = T.S(T.LineHi);
            tip.BorderThickness = new Thickness(1);
            tip.Padding = new Thickness(8);
            tip.FontFamily = T.FCn;
            tip.FontSize = T.FsBody;
            tip.MaxWidth = MaxTipWidth;
            tip.HasDropShadow = true;
            tip.Placement = PlacementMode.Relative;
            tip.PlacementTarget = el;
            tip.HorizontalOffset = 0;
            tip.VerticalOffset = 6;
            tip.Template = TipTemplate;
            tip.Content = BuildContent(text);
            return tip;
        }

        private static FrameworkElement BuildContent(string text)
        {
            int nl = text.IndexOf('\n');
            if (nl > 0 && nl < 48 && nl + 1 < text.Length)
            {
                string head = text.Substring(0, nl).TrimEnd('\r');
                string body = text.Substring(nl + 1).TrimStart('\r', '\n');
                if (!string.IsNullOrEmpty(body))
                {
                    StackPanel sp = new StackPanel();
                    sp.MaxWidth = ContentWidth;

                    TextBlock h = new TextBlock();
                    h.Text = head;
                    h.FontFamily = T.FDisplayReg;
                    h.FontSize = T.FsSmall;
                    h.FontWeight = FontWeights.SemiBold;
                    h.Foreground = T.S(T.Hi);
                    h.TextWrapping = TextWrapping.Wrap;
                    h.LineHeight = 16;
                    TextOptions.SetTextFormattingMode(h, TextFormattingMode.Display);
                    sp.Children.Add(h);

                    Border rule = new Border();
                    rule.Height = 1;
                    rule.Background = T.S(T.Line);
                    rule.Margin = new Thickness(0, 5, 0, 5);
                    sp.Children.Add(rule);

                    TextBlock b = new TextBlock();
                    b.Text = body;
                    b.TextWrapping = TextWrapping.Wrap;
                    b.FontFamily = T.FCn;
                    b.FontSize = T.FsSmall;
                    b.Foreground = T.S(T.Text);
                    b.LineHeight = 18;
                    b.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                    TextOptions.SetTextFormattingMode(b, TextFormattingMode.Display);
                    sp.Children.Add(b);
                    return sp;
                }
            }

            TextBlock t = new TextBlock();
            t.Text = text;
            t.TextWrapping = TextWrapping.Wrap;
            t.FontFamily = T.FCn;
            t.FontSize = T.FsSmall;
            t.Foreground = T.S(T.Text);
            t.LineHeight = 18.5;
            t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            t.MaxWidth = ContentWidth;
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
            return t;
        }

        private static ControlTemplate BuildTemplate()
        {
            ControlTemplate ct = new ControlTemplate(typeof(ToolTip));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, T.S(T.Panel));
            border.SetValue(Border.BorderBrushProperty, T.S(T.LineHi));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(T.RadiusSm));
            border.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 8));

            FrameworkElementFactory grid = new FrameworkElementFactory(typeof(Grid));
            FrameworkElementFactory bevel = new FrameworkElementFactory(typeof(Border));
            bevel.SetValue(Border.HeightProperty, 1.0);
            bevel.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Top);
            bevel.SetValue(Border.MarginProperty, new Thickness(-8, -7, -8, 0));
            bevel.SetValue(Border.BackgroundProperty, T.Bevel());
            bevel.SetValue(UIElement.IsHitTestVisibleProperty, false);

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            grid.AppendChild(bevel);
            grid.AppendChild(content);
            border.AppendChild(grid);
            ct.VisualTree = border;
            return ct;
        }

        /// <summary>相对目标定位：优先下方，底部空间不足翻到上方；右侧越界则左移。</summary>
        private static void Place(FrameworkElement el, ToolTip tip)
        {
            Window w = Window.GetWindow(el);
            double ox = 0;
            double oy = 6;

            Size size = Measure(tip);
            if (w != null && w.ActualWidth > 0)
            {
                Point p = el.TranslatePoint(new Point(0, 0), w);
                bool canBelow = (w.ActualHeight - (p.Y + el.ActualHeight)) >= size.Height + 14;
                bool canAbove = p.Y >= size.Height + 14;
                if (!canBelow && canAbove) oy = -(size.Height + 6);
                else oy = el.ActualHeight + 6;

                double right = p.X + size.Width;
                if (right > w.ActualWidth - 8)
                    ox = Math.Min(0, (w.ActualWidth - 8 - size.Width) - p.X);
                if (p.X + ox < 8) ox = 8 - p.X;
            }
            else if (!double.IsNaN(el.ActualHeight))
            {
                oy = el.ActualHeight + 6;
            }

            ToolTipService.SetVerticalOffset(el, oy);
            ToolTipService.SetHorizontalOffset(el, ox);
            tip.VerticalOffset = oy;
            tip.HorizontalOffset = ox;
        }

        private static Size Measure(ToolTip tip)
        {
            SizeBox box = _sizes.GetValue(tip, delegate(ToolTip t)
            {
                SizeBox b = new SizeBox();
                b.Value = ComputeSize(t);
                return b;
            });
            return box.Value;
        }

        private static Size ComputeSize(ToolTip tip)
        {
            FrameworkElement c = tip.Content as FrameworkElement;
            if (c == null) return new Size(0, 0);
            c.Measure(new Size(ContentWidth, double.PositiveInfinity));
            Size d = c.DesiredSize;
            return new Size(Math.Min(MaxTipWidth, Math.Ceiling(d.Width) + 22),
                            Math.Ceiling(d.Height) + 17);
        }
    }
}
