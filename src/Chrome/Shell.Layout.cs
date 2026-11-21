// ============================================================================
//  Shell.Layout.cs — 自适应外壳（Shell 分片）
//  折叠断点 / 品牌图标绑定 / 重新布局
//  来源：Shell.cs（1324 行单体）按职责拆分，partial 共享字段，行为零变化。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IOPath = System.IO.Path;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Go
{
    public partial class Shell : Window
    {
        // ====================================================================
        //  自适应外壳
        // ====================================================================
        private void ApplyShellLayout()
        {
            // 用 ActualWidth 而不是 Width：最大化之后 Width 仍是还原时的尺寸，
            // 于是宽屏上仍按窄态排版（导轨收成图标、内容只剩一半宽度）。
            double w = ActualWidth > 0 ? ActualWidth : Width;
            bool compact = w < 1180;

            // 旧写法是 if (_forceLayout) { _compactRail = !compact; ... }，
            // 用一个"故意写错的字段值"来强制重跑一次。代价是：字段从此与真实状态相反，
            // 之后任何按 _compactRail 判断的逻辑都会得到反答案（离屏渲染时
            // 1300 宽的窗口会被当成折叠态）。这里改成"强制标志只影响是否重跑，
            // 不伪造状态"。
            if (compact != _compactRail || _forceLayout)
            {
                _forceLayout = false;
                _compactRail = compact;
                double target = compact ? 68 : T.RailW;
                _railW = target;
                _rail.Width = target;
                if (_bodyGrid != null)
                    _bodyGrid.ColumnDefinitions[0].Width = new GridLength(target);
                foreach (KeyValuePair<string, NavRow> kv in _navs) kv.Value.SetCompact(compact);
                foreach (object child in _navList.Children)
                {
                    FrameworkElement fe = child as FrameworkElement;
                    if (fe != null && fe.Tag as string == "group")
                        fe.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                }
                foreach (UIElement fe in _footTexts)
                    fe.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                _statLine.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                _clock.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                if (_compactThemeOrb != null) _compactThemeOrb.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                if (_brandWording != null) _brandWording.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                footMarginAdjust(compact);
                AdjustRailFit();
            }
            SyncMaximizeChrome();
            bool showTicker = w >= 1560;
            bool showPulse = w >= 1040;
            if (_ticker != null)
                _ticker.Visibility = showTicker ? Visibility.Visible : Visibility.Collapsed;
            if (_pulse != null)
                _pulse.Visibility = showPulse ? Visibility.Visible : Visibility.Collapsed;
            if (_telemetrySep != null)
                _telemetrySep.Visibility = (showTicker && showPulse) ? Visibility.Visible : Visibility.Collapsed;
            if (_telemetryIsland != null)
                _telemetryIsland.Visibility = (showTicker || showPulse) ? Visibility.Visible : Visibility.Collapsed;
        }

        public string GameModeButtonText { get { return _gmText == null ? "" : _gmText.Text; } }
        public string PowerChipText { get { return _pwrText == null ? "" : _pwrText.Text; } }
        public global::Go.Tray.TrayVisualState LastTrayState { get { return _lastTrayState < 0 ? global::Go.Tray.TrayVisualState.Idle : (global::Go.Tray.TrayVisualState)_lastTrayState; } }
        public string LastTrayTipSuffix { get { return _lastTrayTip ?? ""; } }
        public int ThemeSwatchCount { get { return _themeRings == null ? 0 : _themeRings.Length; } }
        public bool ThemeBarVisible { get { return _themeBar != null && _themeBar.Visibility == Visibility.Visible; } }
        public bool TickerVisible { get { return _ticker != null && _ticker.Visibility == Visibility.Visible; } }
        public bool PulseVisible { get { return _pulse != null && _pulse.Visibility == Visibility.Visible; } }
        public bool GameModeButtonHitReady
        {
            get
            {
                return _gmPill != null
                    && _gmPill.Visibility == Visibility.Visible
                    && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(_gmPill);
            }
        }
        public void TestClickGameMode() { ToggleGameMode(); }
        public void TestClickThemeSwatch(string id) { if (!string.IsNullOrEmpty(id)) ApplyThemeLive(id); }

        /// <summary>检测标题栏左侧面包屑与右侧控件区是否发生水平重叠。</summary>
        public bool HasTitleBarOverlap()
        {
            if (_crumb == null || _pwrChip == null || _gmPill == null) return true;
            try
            {
                Point pCrumb = _crumb.TranslatePoint(new Point(_crumb.ActualWidth, 0), _root);
                FrameworkElement rightAnchor = (_pulse != null && _pulse.Visibility == Visibility.Visible)
                    ? (FrameworkElement)_pulse
                    : ((_ticker != null && _ticker.Visibility == Visibility.Visible) ? (FrameworkElement)_ticker : _pwrChip);
                Point pRight = rightAnchor.TranslatePoint(new Point(0, 0), _root);
                return pCrumb.X > pRight.X + 0.5;
            }
            catch (Exception ex) { Diag.Fail("layout.overlap", ex); return false; }
        }

        // ---- 自检用只读钩子（不参与正常交互）------------------------------
        public string CurrentKey { get { return _cur == null ? "" : _cur.Key; } }
        public bool RailCompact { get { return _compactRail; } }
        public int PaletteResultCount { get { return _pResults.Count; } }
        public string PaletteFirstTitle { get { return _pResults.Count > 0 ? _pResults[0].Title : ""; } }
        public int HistoryCount { get { return _history.Count; } }
        public string BrandDebug
        {
            get
            {
                if (_brand == null) return "brand=null";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("brand=").Append(_brand.ActualWidth.ToString("0")).Append("x").Append(_brand.ActualHeight.ToString("0"));
                sb.Append(" vis=").Append(_brand.Visibility).Append(" op=").Append(_brand.Opacity);
                sb.Append(" kids=").Append(_brand.Children.Count).Append(" row=").Append(Grid.GetRow(_brand));
                sb.Append(" wordingVis=").Append(_brandWording == null ? "null" : _brandWording.Visibility.ToString());
                if (_railGrid != null)
                {
                    sb.Append(" | rail=").Append(_railGrid.ActualWidth.ToString("0")).Append("x").Append(_railGrid.ActualHeight.ToString("0"));
                    sb.Append(" rows=").Append(_railGrid.RowDefinitions.Count);
                    for (int i = 0; i < _railGrid.RowDefinitions.Count; i++)
                        sb.Append("[").Append(i).Append(":").Append(_railGrid.RowDefinitions[i].ActualHeight.ToString("0")).Append("]");
                    sb.Append(" | railY=").Append(_railGrid.TranslatePoint(new Point(0, 0), this).Y.ToString("0"));
                }
                sb.Append(" | brandY=").Append(_brand.TranslatePoint(new Point(0, 0), this).Y.ToString("0"));
                return sb.ToString();
            }
        }

        public string PaletteDebug
        {
            get
            {
                if (_paletteInput == null) return "input=null";
                return "input=" + _paletteInput.ActualWidth.ToString("0") + "x" + _paletteInput.ActualHeight.ToString("0")
                    + " text='" + _paletteInput.Text + "' vis=" + _paletteInput.Visibility
                    + " fg=" + _paletteInput.Foreground + " palette=" + _palette.Visibility
                    + " results=" + _pResults.Count;
            }
        }

        public void TestPalette(string query)
        {
            ShowPalette();
            _placeholderOn = false;
            _paletteInput.Foreground = T.S(T.Hi);
            _paletteInput.Text = query == null ? "" : query;
            RebuildResults();
        }

        public void TestPaletteActivate() { ActivateSelected(); }
        public void TestBack() { Navigate(-1); }
        public void TestForward() { Navigate(1); }

        /// <summary>
        /// 把 exe 内嵌的多尺寸 .ico 绑定到 Window.Icon：
        /// 任务栏 / Alt+Tab 会拿到 256 帧而不是让系统对 32px 位图做双三次放大。
        /// </summary>
        private void ApplyBrandIcon()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (!name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) continue;
                    using (System.IO.Stream st = asm.GetManifestResourceStream(name))
                    {
                        if (st == null) continue;
                        IconBitmapDecoder dec = new IconBitmapDecoder(st, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        BitmapFrame best = null;
                        foreach (BitmapFrame f in dec.Frames)
                            if (best == null || f.PixelWidth > best.PixelWidth) best = f;
                        if (best != null) { Icon = best; return; }
                    }
                }
            }
            catch { Diag.Hit("icon.embedded"); }

            // 兜底：安装目录旁的独立 .ico
            try
            {
                string side = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameOptimizer.ico");
                if (System.IO.File.Exists(side))
                {
                    using (System.IO.Stream st = System.IO.File.OpenRead(side))
                    {
                        IconBitmapDecoder dec = new IconBitmapDecoder(st, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        BitmapFrame best = null;
                        foreach (BitmapFrame f in dec.Frames)
                            if (best == null || f.PixelWidth > best.PixelWidth) best = f;
                        if (best != null) Icon = best;
                    }
                }
            }
            catch { Diag.Hit("icon.sidecar"); }
        }

        public void Relayout()
        {
            _forceLayout = true;
            ApplyShellLayout();
        }

        private StackPanel _footPanel;

        private void footMarginAdjust(bool compact)
        {
            if (_footPanel != null)
                _footPanel.Margin = compact ? new Thickness(26, 0, 26, 14) : new Thickness(20, 0, 20, 14);
        }
    }
}
