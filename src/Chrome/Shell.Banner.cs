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
        private Border _installBanner;
        private bool _installSessionDismissed;
        private AppInstall.InstallStatus _installStatus;

        private AppInstall.InstallStatus GetInstallStatus()
        {
            if (_installStatus == null) _installStatus = AppInstall.Probe();
            return _installStatus;
        }

        private UIElement BuildBanner()
        {
            StackPanel stack = new StackPanel();

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
            stack.Children.Add(_banner);

            // Rev141：普通用户从下载/临时目录打开单文件 exe 时，温和提示一键安装并建立桌面/开始菜单快捷方式；
            // 源码开发仓（含 build.ps1）或已从安装目录启动时不打扰，保持便携也不再反复弹出。
            _installBanner = new Border();
            _installBanner.Background = T.S(T.AccentA(0x10));
            _installBanner.BorderBrush = T.S(T.AccentA(0x32));
            _installBanner.BorderThickness = new Thickness(0, 0, 0, 1);
            _installBanner.Padding = new Thickness(T.PadX, 3, T.PadX - 8, 3);

            Grid ig = new Grid();
            ig.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ig.ColumnDefinitions.Add(new ColumnDefinition());
            ig.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            System.Windows.Shapes.Ellipse idot = new System.Windows.Shapes.Ellipse();
            idot.Width = 5; idot.Height = 5;
            idot.Fill = T.S(T.Accent);
            idot.VerticalAlignment = VerticalAlignment.Center;
            idot.Margin = new Thickness(0, 0, 9, 0);
            ig.Children.Add(idot);

            TextBlock it = Ui.Tx("便携运行中 · 可按需安装到本机并自选桌面/开始菜单快捷方式，下次打开不再难找", T.FsMicro, T.Text, T.FCn);
            it.VerticalAlignment = VerticalAlignment.Center;
            it.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(it, 1);
            ig.Children.Add(it);

            StackPanel iright = new StackPanel();
            iright.Orientation = Orientation.Horizontal;
            iright.VerticalAlignment = VerticalAlignment.Center;

            Btn instBtn = new Btn("安装 / 自选快捷方式", BtnKind.Primary);
            instBtn.Height = T.BtnHXs;
            instBtn.Margin = new Thickness(0, 0, 6, 0);
            instBtn.Click += delegate { OpenSetupDialog(false); };
            iright.Children.Add(instBtn);

            Btn keepBtn = new Btn("保持便携", BtnKind.Quiet);
            keepBtn.Height = T.BtnHXs;
            keepBtn.Margin = new Thickness(0, 0, 4, 0);
            keepBtn.Click += delegate { DismissInstallPrompt(true); };
            iright.Children.Add(keepBtn);

            Grid iclose = new Grid();
            iclose.Width = 22; iclose.Height = 22;
            iclose.Background = Brushes.Transparent;
            iclose.Cursor = Cursors.Hand;
            System.Windows.Shapes.Path ix = Ui.Icon("M7 7 L17 17 M17 7 L7 17", 11, T.Mute, 1.3);
            iclose.Children.Add(ix);
            iclose.MouseEnter += delegate { ix.Stroke = T.S(T.Text); };
            iclose.MouseLeave += delegate { ix.Stroke = T.S(T.Mute); };
            Pressable.On(iclose, delegate { DismissInstallPrompt(false); });
            Tip.Attach(iclose, "本次会话收起提示");
            iright.Children.Add(iclose);

            Grid.SetColumn(iright, 2);
            ig.Children.Add(iright);

            _installBanner.Child = ig;
            stack.Children.Add(_installBanner);

            ApplyElevation();
            ApplyInstallBanner();
            return stack;
        }

        /// <summary>根据提权状态显隐横幅。提权成功后横幅直接消失，不需要手动关闭。</summary>
        public void ApplyElevation()
        {
            if (_banner == null) return;
            bool show = _core != null && !_core.Elevated && !_bannerDismissed;
            _banner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>根据安装与运行位置显隐桌面安装引导带（离屏渲染与源码开发仓自动静音）。</summary>
        public void ApplyInstallBanner()
        {
            if (_installBanner == null) return;
            bool headless = _core != null && _core.HeadlessRender;
            AppInstall.InstallStatus st = GetInstallStatus();
            bool show = !headless
                && !_installPromptDismissed
                && !_installSessionDismissed
                && st != null
                && st.NeedsInstallPrompt;
            _installBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        public void DismissInstallPrompt(bool persist)
        {
            if (persist)
            {
                _installPromptDismissed = true;
                SaveState();
                Toast("已保持便携模式 · 随时可在左下角、还原中心或 Ctrl+K 管理安装与卸载", true);
            }
            else
            {
                _installSessionDismissed = true;
            }
            ApplyInstallBanner();
        }

        public void OpenSetupDialog(bool uninstallMode)
        {
            try
            {
                _installStatus = AppInstall.Probe();
                SetupDialog dlg = new SetupDialog(_core, this, uninstallMode, _installStatus);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                Diag.Fail("shell.setup_dialog", ex);
                Toast("无法打开应用管理窗口：" + ex.Message, false);
            }
        }

        public void OnInstallStateChanged(AppInstall.InstallResult res)
        {
            _installStatus = AppInstall.Probe();
            ApplyInstallBanner();
            RefreshInstallUi();
            if (res == null || !res.Ok) return;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(res.HotSwapped ? "已热更新安装目录" : "已保存到本机");
            if (res.DesktopShortcutActive && res.StartMenuShortcutActive) sb.Append(" · 已建桌面与开始菜单快捷方式");
            else if (res.DesktopShortcutActive) sb.Append(" · 已建桌面快捷方式");
            else if (res.StartMenuShortcutActive) sb.Append(" · 已建开始菜单入口");
            else sb.Append(" · 已按勾选清理快捷方式");
            Toast(sb.ToString(), true);
        }

        public void PrepareForUninstall()
        {
            _uninstalling = true;
            try { Program.ReleaseInstanceLock(); }
            catch { }
            try { if (_core != null && _core.Daemon != null) _core.Daemon.RemoveTrayIconNow(); }
            catch { }
        }

        public void FinishUninstallExit()
        {
            _uninstalling = true;
            _relaunching = true;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try { Application.Current.Shutdown(); }
                catch (Exception ex) { Diag.Fail("shell.uninstall.shutdown", ex); }
            }), DispatcherPriority.Background);
        }

        private void RefreshInstallChipColor()
        {
            if (_installChip == null) return;
            AppInstall.InstallStatus st = GetInstallStatus();
            bool ok = st != null && st.IsInstalled;
            _installChip.Foreground = T.S(ok ? T.Mute : T.AccentDim);
        }

        private void RefreshInstallUi()
        {
            if (_installChip == null) return;
            AppInstall.InstallStatus st = GetInstallStatus();
            bool ok = st != null && st.IsInstalled;
            _installChip.Text = ok ? "安装/卸载" : "安装到本机";
            RefreshInstallChipColor();
            Tip.Attach(_installChip, ok
                ? ("已安装于本机 · 点击可自选开关桌面/开始菜单快捷方式，或直接卸载本软件\n目录：" + st.InstallDir)
                : "点击打开应用管理：自由勾选桌面/开始菜单快捷方式、开机自启，或彻底卸载本软件");
        }

        /// <summary>自检用：只读横幅当前是否可见。</summary>
        public bool BannerVisible
        {
            get { return _banner != null && _banner.Visibility == Visibility.Visible; }
        }

        /// <summary>自检用：桌面安装引导带当前是否可见。</summary>
        public bool InstallBannerVisible
        {
            get { return _installBanner != null && _installBanner.Visibility == Visibility.Visible; }
        }

        /// <summary>自检用：注入虚拟安装探测状态以验证横幅显隐规则（不触碰真实桌面/注册表）。</summary>
        public void TestSetInstallStatus(AppInstall.InstallStatus st, bool resetDismiss)
        {
            _installStatus = st;
            if (resetDismiss)
            {
                _installPromptDismissed = false;
                _installSessionDismissed = false;
            }
            ApplyInstallBanner();
            RefreshInstallUi();
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
