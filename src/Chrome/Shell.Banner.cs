// ============================================================================
//  Shell.Banner.cs — 只读态横幅 + 焦点归还（Shell 分片）
//  两件"用户会骂人"的小事：
//    1) 未提权时，每个页面的写入都会失败，但失败点分散在十一个页面里。
//       与其在每处弹 Toast，不如在内容区顶部给一条**能一键解决**的横幅。
//    2) Ctrl+K 面板关掉后焦点掉到窗口根上，再按 Tab 会从第一个控件重新开始。
//       打开前记住焦点，关闭后还回去。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go
{
    public partial class Shell : Window
    {
        private Border _banner;
        private bool _bannerDismissed;

        private UIElement BuildBanner()
        {
            // Rev79：常驻提示不该是一块黄色横幅 —— 那是"警报"的语言。
            // 仪器不会每页都朝你喊一句"你现在不是管理员"；它只在状态行里标一个点。
            // 所以这里压成一条 26px 的低对比状态带：仍然可读、仍然可操作，
            // 但不再把每页的顶部三分之一都染成警告色。
            _banner = new Border();
            _banner.Background = T.S(0x08FFB13C);
            _banner.BorderBrush = T.S(0x1AFFB13C);
            _banner.BorderThickness = new Thickness(0, 0, 0, 1);
            _banner.Padding = new Thickness(T.PadX, 3, T.PadX - 8, 3);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse();
            dot.Width = 5; dot.Height = 5;
            dot.Fill = T.S(T.Warn);
            dot.VerticalAlignment = VerticalAlignment.Center;
            dot.Margin = new Thickness(0, 0, 9, 0);
            g.Children.Add(dot);

            TextBlock t = Ui.Tx("只读模式 · 诊断与读取可用，写入系统需要管理员权限", T.FsMicro, T.Dim, T.FCn);
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, 1);
            g.Children.Add(t);

            StackPanel rightSide = new StackPanel();
            rightSide.Orientation = Orientation.Horizontal;
            rightSide.VerticalAlignment = VerticalAlignment.Center;

            Btn go = new Btn("提权重启", BtnKind.Quiet);
            go.Height = T.BtnHXs;
            go.Margin = new Thickness(0, 0, 4, 0);
            go.Click += delegate { RelaunchElevated(); };
            rightSide.Children.Add(go);

            Grid close = new Grid();
            close.Width = 22; close.Height = 22;
            close.Background = Brushes.Transparent;
            close.Cursor = Cursors.Hand;
            System.Windows.Shapes.Path x = Ui.Icon("M7 7 L17 17 M17 7 L7 17", 11, T.Mute, 1.3);
            close.Children.Add(x);
            close.MouseEnter += delegate { x.Stroke = T.S(T.Text); };
            close.MouseLeave += delegate { x.Stroke = T.S(T.Mute); };
            Pressable.On(close, delegate { _bannerDismissed = true; ApplyElevation(); });
            Tip.Attach(close, "本次会话不再提示（重启后仍会提醒）");
            rightSide.Children.Add(close);

            Grid.SetColumn(rightSide, 2);
            g.Children.Add(rightSide);

            _banner.Child = g;
            ApplyElevation();
            return _banner;
        }

        /// <summary>根据提权状态显隐横幅。提权成功后横幅直接消失，不需要手动关闭。</summary>
        public void ApplyElevation()
        {
            if (_banner == null) return;
            bool show = _core != null && !_core.Elevated && !_bannerDismissed;
            _banner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>自检用：只读横幅当前是否可见。</summary>
        public bool BannerVisible
        {
            get { return _banner != null && _banner.Visibility == Visibility.Visible; }
        }

        // ---- 焦点归还 ------------------------------------------------------
        private IInputElement _focusBeforePalette;

        private void RememberFocus()
        {
            try
            {
                IInputElement fe = Keyboard.FocusedElement;
                if (fe != null && !object.ReferenceEquals(fe, _paletteInput)) _focusBeforePalette = fe;
            }
            catch { /* 记不住焦点只是让 Esc 之后焦点回到默认位置，不丢功能 */ }
        }

        private void RestoreFocus()
        {
            IInputElement target = _focusBeforePalette;
            _focusBeforePalette = null;
            if (target == null) return;
            // 面板是隐藏而不是销毁，直接同步 Focus 会被 Dispatcher 的输入队列吞掉
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    FrameworkElement fe = target as FrameworkElement;
                    if (fe != null && !fe.IsVisible) return;
                    Keyboard.Focus(target);
                }
                catch { /* 焦点恢复失败：用户再按一次 Tab 即可，不能因此中断收起流程 */ }
            }), DispatcherPriority.Input);
        }
    }
}
