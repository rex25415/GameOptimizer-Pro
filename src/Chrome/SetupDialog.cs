// ============================================================================
//  SetupDialog.cs — 一体化「应用安装、快捷方式自选与原生卸载」深色弹窗（Rev 142）
//
//  统一入口：
//    · 首次下载便携态顶部引导条 → 打开（安装模式）
//    · 侧栏左下角 / 10 还原中心 / Ctrl+K → 打开（安装/管理模式）
//    · Windows「设置 → 已安装的应用 → 卸载」(GameOptimizer.exe --uninstall) → 打开（卸载模式）
//
//  设计原则：
//    · 桌面快捷方式、开始菜单入口、开机托盘自启全部由用户勾选决定（勾即建、取消即删）；
//    · 卸载前主动核查写前账本未还原项，支持先一键全部还原或保留账本，二次点击防误触；
//    · 全纯代码 WPF，零外置脚本，零 PowerShell 黑框，跟随当前主题配色。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Go
{
    public sealed class SetupDialog : Window
    {
        private readonly Core _core;
        private readonly Shell _ownerShell;
        private readonly bool _uninstallMode;
        private AppInstall.InstallStatus _status;

        private bool _chkDesktop;
        private bool _chkStartMenu;
        private bool _chkAutostart;
        private bool _chkPurgeData;
        private bool _uninstallArmed;

        private TextBlock _statusBadge;
        private TextBlock _ledgerNotice;
        private Btn _revertBtn;
        private Btn _uninstallBtn;
        private TextBlock _feedbackText;

        public bool ChangedInstall { get; private set; }
        public bool DidUninstall { get; private set; }

        public bool DesktopChecked { get { return _chkDesktop; } }
        public bool StartMenuChecked { get { return _chkStartMenu; } }
        public bool AutostartChecked { get { return _chkAutostart; } }
        public bool PurgeDataChecked { get { return _chkPurgeData; } }

        public SetupDialog(Core core, Shell ownerShell, bool uninstallMode)
            : this(core, ownerShell, uninstallMode, null)
        {
        }

        public SetupDialog(Core core, Shell ownerShell, bool uninstallMode, AppInstall.InstallStatus presetStatus)
        {
            _core = core;
            _ownerShell = ownerShell;
            _uninstallMode = uninstallMode;
            _status = presetStatus ?? AppInstall.Probe();

            AppInstall.InstallOptions def = AppInstall.InstallOptions.DefaultFor(_status);
            _chkDesktop = def.CreateDesktopShortcut;
            _chkStartMenu = def.CreateStartMenuShortcut;
            _chkAutostart = def.EnableAutostart;
            _chkPurgeData = false;

            Title = uninstallMode ? "卸载 GameOptimizer Pro" : "GameOptimizer Pro · 应用安装与管理";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Width = 528;
            SizeToContent = SizeToContent.Height;
            Background = T.DeckBrush();
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            ShowInTaskbar = ownerShell == null;
            if (ownerShell != null)
            {
                Owner = ownerShell;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };

            Content = BuildRoot();
        }

        private UIElement BuildRoot()
        {
            Border outer = new Border();
            outer.Background = T.DeckBrush();
            outer.BorderBrush = T.S(_uninstallMode ? 0x70FF5C63 : T.AccentA(0x64));
            outer.BorderThickness = new Thickness(1);

            Grid shell = new Grid();

            Border topBevel = new Border();
            topBevel.Height = 2;
            topBevel.VerticalAlignment = VerticalAlignment.Top;
            topBevel.Background = _uninstallMode ? (Brush)T.S(T.Bad) : (Brush)T.AccentBevel();
            topBevel.IsHitTestVisible = false;
            shell.Children.Add(topBevel);

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(22, 18, 22, 20);

            body.Children.Add(BuildHeader());
            body.Children.Add(Gap(14));
            body.Children.Add(BuildPathCard());
            body.Children.Add(Gap(14));

            if (!_uninstallMode)
            {
                body.Children.Add(BuildInstallSection());
                body.Children.Add(Gap(14));
                body.Children.Add(BuildUninstallSection(false));
            }
            else
            {
                body.Children.Add(BuildUninstallSection(true));
            }

            _feedbackText = Ui.Tx("", T.FsMicro, T.Ok, T.FCn);
            _feedbackText.Margin = new Thickness(0, 10, 0, 0);
            _feedbackText.TextWrapping = TextWrapping.Wrap;
            _feedbackText.Visibility = Visibility.Collapsed;
            body.Children.Add(_feedbackText);

            shell.Children.Add(body);
            outer.Child = shell;
            return outer;
        }

        private UIElement BuildHeader()
        {
            Grid hdr = new Grid();
            hdr.Background = Brushes.Transparent;
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.ColumnDefinitions.Add(new ColumnDefinition());
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    try { DragMove(); }
                    catch { }
                }
            };

            Border markFrame = new Border();
            markFrame.Width = 32; markFrame.Height = 32;
            markFrame.CornerRadius = new CornerRadius(8);
            markFrame.Background = T.Grad(T.AccentA(0x2E), T.Accent2A(0x14), 135);
            markFrame.BorderBrush = T.S(T.AccentA(0x64));
            markFrame.BorderThickness = new Thickness(1);
            markFrame.VerticalAlignment = VerticalAlignment.Center;
            Grid mark = new Grid { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            mark.Children.Add(Ui.Icon(Brand.MarkPath, 18, T.Accent, 1.3));
            mark.Children.Add(Ui.Icon(Brand.MarkInnerPath, 18, T.Hi, 1.0));
            markFrame.Child = mark;
            hdr.Children.Add(markFrame);

            StackPanel titles = new StackPanel();
            titles.Margin = new Thickness(12, 0, 0, 0);
            titles.VerticalAlignment = VerticalAlignment.Center;
            TextBlock h1 = Ui.Tx(_uninstallMode ? "卸载 GameOptimizer Pro" : "应用安装与管理", T.FsTitle, T.Hi, T.FCn);
            h1.FontWeight = FontWeights.SemiBold;
            titles.Children.Add(h1);
            TextBlock sub = Ui.Mono("REV " + Brand.Rev + " · SINGLE-FILE SELF-CONTAINED APP", T.FsNano, T.Mute);
            sub.Margin = new Thickness(0, 2, 0, 0);
            titles.Children.Add(sub);
            Grid.SetColumn(titles, 1);
            hdr.Children.Add(titles);

            Border badge = new Border();
            badge.CornerRadius = new CornerRadius(10);
            badge.Padding = new Thickness(9, 3, 9, 3);
            badge.Margin = new Thickness(8, 0, 10, 0);
            badge.VerticalAlignment = VerticalAlignment.Center;
            bool inst = _status != null && _status.IsInstalled;
            badge.Background = T.S(inst ? 0x1A3FE0A4 : T.AccentA(0x1C));
            badge.BorderBrush = T.S(inst ? 0x4C3FE0A4 : T.AccentA(0x4C));
            badge.BorderThickness = new Thickness(1);
            _statusBadge = Ui.Tx(inst ? "已安装于本机" : "便携运行中", T.FsNano, inst ? T.Ok : T.Accent, T.FCn);
            badge.Child = _statusBadge;
            Grid.SetColumn(badge, 2);
            hdr.Children.Add(badge);

            Grid close = new Grid();
            close.Width = 26; close.Height = 26;
            close.Background = Brushes.Transparent;
            close.Cursor = Cursors.Hand;
            close.VerticalAlignment = VerticalAlignment.Center;
            Path x = Ui.Icon("M7 7 L17 17 M17 7 L7 17", 12, T.Mute, 1.4);
            close.Children.Add(x);
            close.MouseEnter += delegate { x.Stroke = T.S(T.Hi); };
            close.MouseLeave += delegate { x.Stroke = T.S(T.Mute); };
            Pressable.On(close, delegate { Close(); });
            Grid.SetColumn(close, 3);
            hdr.Children.Add(close);

            return hdr;
        }

        private UIElement BuildPathCard()
        {
            Border card = new Border();
            card.CornerRadius = new CornerRadius(T.RadiusMd);
            card.Background = T.SubCardBg();
            card.BorderBrush = T.S(T.LineSoft);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(12, 9, 12, 9);

            StackPanel sp = new StackPanel();
            TextBlock lbl = Ui.Label("INSTALL LOCATION", T.AccentDim);
            lbl.FontSize = 9.5;
            sp.Children.Add(lbl);
            string p = (_status != null && !string.IsNullOrEmpty(_status.InstalledExe))
                ? _status.InstalledExe
                : System.IO.Path.Combine(AppInstall.DefaultInstallDir(), "GameOptimizer.exe");
            TextBlock pathTx = Ui.Mono(p, T.FsNano, T.Dim);
            pathTx.Margin = new Thickness(0, 4, 0, 0);
            pathTx.TextTrimming = TextTrimming.CharacterEllipsis;
            pathTx.ToolTip = p;
            sp.Children.Add(pathTx);
            card.Child = sp;
            return card;
        }

        private UIElement BuildInstallSection()
        {
            Border card = new Border();
            card.CornerRadius = new CornerRadius(T.RadiusMd);
            card.Background = T.SubCardBg();
            card.BorderBrush = T.S(T.Line);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(14, 12, 14, 14);

            StackPanel sp = new StackPanel();
            TextBlock sec = Ui.Tx("快捷方式与启动入口（勾上即创建，取消即移除）", T.FsSmall, T.Hi, T.FCn);
            sec.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(sec);
            sp.Children.Add(Gap(10));

            sp.Children.Add(BuildCheckRow(
                "桌面快捷方式",
                "在桌面创建 GameOptimizer Pro 快捷方式（即使清理下载文件夹也不会失效）",
                _chkDesktop,
                delegate(bool v) { _chkDesktop = v; }));
            sp.Children.Add(Gap(6));

            sp.Children.Add(BuildCheckRow(
                "开始菜单入口",
                "添加到 Windows 开始菜单，按 Win 键可直接搜索启动（完全由你自选）",
                _chkStartMenu,
                delegate(bool v) { _chkStartMenu = v; }));
            sp.Children.Add(Gap(6));

            sp.Children.Add(BuildCheckRow(
                "开机静默启动至托盘",
                "登录 Windows 时在托盘自动守护已建档游戏（--tray）",
                _chkAutostart,
                delegate(bool v) { _chkAutostart = v; }));
            sp.Children.Add(Gap(8));

            TextBlock taskbarHint = Ui.Tx(
                "提示：Windows 10/11 禁止程序代码强制固定下方任务栏；如需固定，请在运行时右键任务栏图标选择「固定到任务栏」。",
                T.FsNano, T.Mute, T.FCn);
            taskbarHint.TextWrapping = TextWrapping.Wrap;
            taskbarHint.LineHeight = 16;
            sp.Children.Add(taskbarHint);
            sp.Children.Add(Gap(12));

            Grid actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bool inst = _status != null && _status.IsInstalled;
            Btn apply = new Btn(inst ? "保存快捷方式设置" : "安装到本机并应用勾选", BtnKind.Primary);
            apply.Height = 32;
            apply.Click += delegate { RunApplyInstall(); };
            Grid.SetColumn(apply, 1);
            actions.Children.Add(apply);

            sp.Children.Add(actions);
            card.Child = sp;
            return card;
        }

        private UIElement BuildUninstallSection(bool prominent)
        {
            Border card = new Border();
            card.CornerRadius = new CornerRadius(T.RadiusMd);
            card.Background = T.S(prominent ? 0x16FF5C63 : 0x0CFF5C63);
            card.BorderBrush = T.S(prominent ? 0x56FF5C63 : 0x30FF5C63);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(14, 12, 14, 14);

            StackPanel sp = new StackPanel();
            TextBlock sec = Ui.Tx("卸载本软件", T.FsSmall, T.Hi, T.FCn);
            sec.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(sec);
            sp.Children.Add(Gap(8));

            // 写前账本安全检查行
            Grid ledgerRow = new Grid();
            ledgerRow.ColumnDefinitions.Add(new ColumnDefinition());
            ledgerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _ledgerNotice = Ui.Tx("", T.FsMicro, T.Dim, T.FCn);
            _ledgerNotice.TextWrapping = TextWrapping.Wrap;
            _ledgerNotice.VerticalAlignment = VerticalAlignment.Center;
            ledgerRow.Children.Add(_ledgerNotice);

            _revertBtn = new Btn("先全部还原", BtnKind.Quiet);
            _revertBtn.Height = 26;
            _revertBtn.Margin = new Thickness(10, 0, 0, 0);
            _revertBtn.VerticalAlignment = VerticalAlignment.Center;
            _revertBtn.Click += delegate { RunRevertBeforeUninstall(); };
            Grid.SetColumn(_revertBtn, 1);
            ledgerRow.Children.Add(_revertBtn);

            sp.Children.Add(ledgerRow);
            RefreshLedgerNotice();
            sp.Children.Add(Gap(10));

            sp.Children.Add(BuildCheckRow(
                "同时删除本地配置与写前账本",
                "清空 %LOCALAPPDATA%\\GameOptimizer（默认不勾选，保留账本以便日后查证或恢复）",
                _chkPurgeData,
                delegate(bool v) { _chkPurgeData = v; }));
            sp.Children.Add(Gap(12));

            Grid foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock note = Ui.Tx("清理全部快捷方式、开机自启、系统卸载项与安装目录", T.FsNano, T.Mute, T.FCn);
            note.VerticalAlignment = VerticalAlignment.Center;
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            foot.Children.Add(note);

            if (prominent)
            {
                Btn cancel = new Btn("取消", BtnKind.Quiet);
                cancel.Height = 30;
                cancel.Margin = new Thickness(10, 0, 8, 0);
                cancel.Click += delegate { Close(); };
                Grid.SetColumn(cancel, 1);
                foot.Children.Add(cancel);
            }

            _uninstallBtn = new Btn("卸载本软件", BtnKind.Danger);
            _uninstallBtn.Height = 30;
            _uninstallBtn.Margin = new Thickness(prominent ? 0 : 10, 0, 0, 0);
            _uninstallBtn.Click += delegate { RunUninstallClick(); };
            Grid.SetColumn(_uninstallBtn, 2);
            foot.Children.Add(_uninstallBtn);

            sp.Children.Add(foot);
            card.Child = sp;
            return card;
        }

        private UIElement BuildCheckRow(string title, string desc, bool initial, Action<bool> onChanged)
        {
            bool state = initial;

            Border row = new Border();
            row.CornerRadius = new CornerRadius(T.RadiusSm);
            row.Background = T.S(0x0AFFFFFF);
            row.BorderBrush = T.S(state ? T.AccentA(0x4C) : T.LineSoft);
            row.BorderThickness = new Thickness(1);
            row.Padding = new Thickness(10, 8, 10, 8);
            row.Cursor = Cursors.Hand;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            Border box = new Border();
            box.Width = 16; box.Height = 16;
            box.CornerRadius = new CornerRadius(4);
            box.BorderThickness = new Thickness(1.2);
            box.VerticalAlignment = VerticalAlignment.Top;
            box.Margin = new Thickness(0, 1, 10, 0);

            Path chk = Ui.Icon("M4 12.5 L9.5 17.5 L20 6.5", 11, 0xFF06101B, 2.2);
            chk.HorizontalAlignment = HorizontalAlignment.Center;
            chk.VerticalAlignment = VerticalAlignment.Center;
            box.Child = chk;
            g.Children.Add(box);

            StackPanel texts = new StackPanel();
            TextBlock t1 = Ui.Tx(title, T.FsSmall, T.Hi, T.FCn);
            t1.FontWeight = FontWeights.Medium;
            texts.Children.Add(t1);
            TextBlock t2 = Ui.Tx(desc, T.FsNano, T.Mute, T.FCn);
            t2.Margin = new Thickness(0, 2, 0, 0);
            t2.TextWrapping = TextWrapping.Wrap;
            texts.Children.Add(t2);
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            Action paint = delegate
            {
                box.Background = state ? T.S(T.Accent) : Brushes.Transparent;
                box.BorderBrush = T.S(state ? T.Accent : T.LineHi);
                chk.Visibility = state ? Visibility.Visible : Visibility.Collapsed;
                row.BorderBrush = T.S(state ? T.AccentA(0x4C) : T.LineSoft);
            };
            paint();

            row.MouseEnter += delegate { row.Background = T.S(0x12FFFFFF); };
            row.MouseLeave += delegate { row.Background = T.S(0x0AFFFFFF); };
            Pressable.On(row, delegate
            {
                state = !state;
                paint();
                if (onChanged != null) onChanged(state);
            });

            row.Child = g;
            return row;
        }

        private void RefreshLedgerNotice()
        {
            if (_ledgerNotice == null || _revertBtn == null) return;
            int pending = (_core != null && _core.Engine != null) ? _core.Engine.PendingCount : 0;
            if (pending > 0)
            {
                _ledgerNotice.Text = "写前账本检测到当前仍有 " + pending + " 项系统调优未还原，建议先还原或保留账本再卸载。";
                _ledgerNotice.Foreground = T.S(T.Warn);
                _revertBtn.Visibility = Visibility.Visible;
            }
            else
            {
                _ledgerNotice.Text = "系统调优账本已归零（无待还原项），可放心卸载。";
                _ledgerNotice.Foreground = T.S(T.Ok);
                _revertBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void RunRevertBeforeUninstall()
        {
            if (_core == null || _core.Engine == null) return;
            ApplyReport rep = _core.Engine.RevertAll();
            RefreshLedgerNotice();
            if (rep.Ok)
            {
                ShowFeedback("已还原 " + rep.Applied + " 项系统设置，系统已恢复接管前状态。", true);
                if (_ownerShell != null) _ownerShell.RefreshAllPages();
            }
            else
            {
                ShowFeedback("有 " + rep.Failed + " 项需管理员权限才能还原；可提权后再还原，或保持「不删除账本」安全卸载。", false);
            }
        }

        private void RunApplyInstall()
        {
            AppInstall.InstallOptions opt = new AppInstall.InstallOptions();
            opt.CreateDesktopShortcut = _chkDesktop;
            opt.CreateStartMenuShortcut = _chkStartMenu;
            opt.EnableAutostart = _chkAutostart;

            Ledger ledger = (_core != null && _core.Engine != null) ? _core.Engine.Ledger : null;
            AppInstall.InstallResult res = AppInstall.ApplyInstall(opt, ledger);
            if (!res.Ok)
            {
                ShowFeedback("保存失败：" + (string.IsNullOrEmpty(res.Error) ? "请检查目录权限" : res.Error), false);
                return;
            }

            ChangedInstall = true;
            _status = AppInstall.Probe();
            if (_ownerShell != null) _ownerShell.OnInstallStateChanged(res);
            Close();
        }

        private void RunUninstallClick()
        {
            if (!_uninstallArmed)
            {
                _uninstallArmed = true;
                if (_uninstallBtn != null) _uninstallBtn.Label = "再次点击确认卸载";
                ShowFeedback("请再次点击「再次点击确认卸载」开始清理；关闭此窗口可取消。", false);
                return;
            }

            Ledger ledger = (_core != null && _core.Engine != null) ? _core.Engine.Ledger : null;
            if (_ownerShell != null) _ownerShell.PrepareForUninstall();

            AppInstall.UninstallResult res = AppInstall.UninstallSelf(_chkPurgeData, true, ledger);
            if (!res.Ok)
            {
                _uninstallArmed = false;
                if (_uninstallBtn != null) _uninstallBtn.Label = "卸载本软件";
                ShowFeedback("卸载失败：" + (string.IsNullOrEmpty(res.Error) ? "请检查权限" : res.Error), false);
                return;
            }

            DidUninstall = true;
            if (_ownerShell != null)
            {
                Close();
                _ownerShell.FinishUninstallExit();
                return;
            }

            ShowFeedback("卸载已完成（快捷方式与注册项已清理，安装目录将在退出后自动清空）。", true);
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(900);
            t.Tick += delegate { t.Stop(); Close(); };
            t.Start();
        }

        private void ShowFeedback(string text, bool ok)
        {
            if (_feedbackText == null) return;
            _feedbackText.Text = text;
            _feedbackText.Foreground = T.S(ok ? T.Ok : T.Warn);
            _feedbackText.Visibility = Visibility.Visible;
        }

        private static Border Gap(double h) { return new Border { Height = h }; }
    }
}
