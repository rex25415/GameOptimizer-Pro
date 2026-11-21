// ============================================================================
//  Chip.cs — 胶囊标签
//  风险等级 / 状态标记的极简胶囊
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
    public static class Chip
    {
        public static Border Make(string text, uint tint, double h)
        {
            return Make(text, tint, h, false);
        }

        public static Border Make(string text, uint tint) { return Make(text, tint, T.ChipH, false); }

        /// <summary>支持选中态微辉光的胶囊标签，保持 Border.Child 为 TextBlock 以兼容旧调用。</summary>
        public static Border Make(string text, uint tint, double h, bool selected)
        {
            Border b = new Border();
            b.Height = h;
            b.CornerRadius = new CornerRadius(T.RadiusSm);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(T.GapIcon, 0, T.GapIcon, 0);
            TextBlock t = new TextBlock();
            t.Text = Ui.Spaced(text);
            t.FontSize = T.FsNano;
            t.FontFamily = T.FDisplayReg;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.SnapsToDevicePixels = true;
            Paint(b, tint, selected);
            return b;
        }

        /// <summary>切换或重绘已有胶囊的着色与选中态微辉光。</summary>
        public static void Paint(Border chip, uint tint, bool selected)
        {
            if (chip == null) return;
            TextBlock t = chip.Child as TextBlock;
            if (selected)
            {
                chip.BorderBrush = CtlTint.Wash(tint, 0xAA);
                LinearGradientBrush glowBg = new LinearGradientBrush();
                glowBg.StartPoint = new Point(0.5, 0);
                glowBg.EndPoint = new Point(0.5, 1);
                glowBg.GradientStops.Add(new GradientStop(T.Alpha(tint, 0x38), 0));
                glowBg.GradientStops.Add(new GradientStop(T.Alpha(tint, 0x1A), 1));
                glowBg.Freeze();
                chip.Background = glowBg;
                if (t != null)
                {
                    t.Foreground = CtlTint.Mix(tint, 0xFFFFFFFF, 0.28);
                    t.FontWeight = FontWeights.SemiBold;
                }
            }
            else
            {
                chip.BorderBrush = CtlTint.Wash(tint, 0x55);
                chip.Background = CtlTint.Wash(tint, 0x16);
                if (t != null)
                {
                    t.Foreground = T.S(tint);
                    t.FontWeight = FontWeights.Normal;
                }
            }
        }

        /// <summary>跟随当前强调色主题自动刷新的胶囊。</summary>
        public static Border MakeAccent(string text, double h, bool selected)
        {
            Border b = Make(text, T.Accent, h, selected);
            bool sel = selected;
            Action refresh = delegate { Paint(b, T.Accent, sel); };
            bool hooked = false;
            b.Loaded += delegate
            {
                if (!hooked) { hooked = true; T.ThemeChanged += refresh; }
                refresh();
            };
            b.Unloaded += delegate
            {
                if (hooked) { hooked = false; T.ThemeChanged -= refresh; }
            };
            return b;
        }

        public static Border MakeAccent(string text, bool selected) { return MakeAccent(text, T.ChipH, selected); }
    }
}
