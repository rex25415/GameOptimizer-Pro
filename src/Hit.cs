// ============================================================================
//  Hit.cs — 命中与输入原语：最近输入类型
//  点击手感的基础设施：
//    · Track   记录"最近一次输入是键盘还是鼠标"，焦点环据此决定是否显形。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Go
{
    public static class Hit
    {
        private static bool _hooked;
        private static bool _keyboard = true;

        /// <summary>最近一次输入是否来自键盘（用于"鼠标点击不显示焦点环"）。</summary>
        public static bool LastWasKeyboard
        {
            get { return _keyboard; }
        }

        /// <summary>安装全局输入探针；可重复调用。</summary>
        public static void Track()
        {
            if (_hooked) return;
            _hooked = true;
            try { InputManager.Current.PreProcessInput += OnPreInput; }
            catch { /* 挂不上全局输入探针：复位标记让下次 Track 还能重试，点击本身不受影响 */ _hooked = false; }
        }

        private static void OnPreInput(object sender, PreProcessInputEventArgs e)
        {
            if (e == null) return;
            InputEventArgs input = null;
            try
            {
                if (e.StagingItem != null) input = e.StagingItem.Input;
            }
            catch { /* 取不到输入项就无法判定类型：保持上一次的结论比乱猜更接近真相 */ }
            if (input == null) return;

            // MouseButtonEventArgs / MouseWheelEventArgs 均派生自 MouseEventArgs；
            // KeyEventArgs / KeyboardFocusChangedEventArgs 均派生自 KeyboardEventArgs。
            if (input is MouseEventArgs) _keyboard = false;
            else if (input is KeyboardEventArgs) _keyboard = true;
            else if (input is TextCompositionEventArgs) _keyboard = true;
        }
    }

    /// <summary>由主题令牌派生画刷：混合 / 指定透明度。所有结果冻结。</summary>
    internal static class CtlTint
    {
        public static SolidColorBrush Mix(uint a, uint b, double t)
        {
            SolidColorBrush br = new SolidColorBrush(T.Mix(T.C(a), T.C(b), t));
            br.Freeze();
            return br;
        }

        public static SolidColorBrush Wash(uint argb, byte alpha)
        {
            Color c = T.C(argb);
            SolidColorBrush br = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            br.Freeze();
            return br;
        }
    }
}
