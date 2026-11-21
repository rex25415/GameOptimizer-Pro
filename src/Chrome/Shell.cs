// ============================================================================
//  Shell.cs — 外壳主体：无边框窗口 / 视觉底噪 / 页面宿主 / 启动装配
//             命令面板 Ctrl+K / 页面转场 / 窗口状态记忆
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
    public class UiState
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
        public string Page { get; set; }
        public string Theme { get; set; }
    }

    public partial class Shell : Window
    {
        private Core _core;
        private Grid _root;
        private ContentControl _host;
        private TextBlock _crumb;
        private StackPanel _navList;
        private Dictionary<string, NavRow> _navs = new Dictionary<string, NavRow>();
        private Dictionary<string, ThinScroll> _wraps = new Dictionary<string, ThinScroll>();
        private Page _cur;
        private DispatcherTimer _beat;
        private DispatcherTimer _fgWatch;
        private int _selfPid;

        private TextBlock _clock, _statLine, _ticker, _sysCpu, _sysGpu, _sysVer;
        private Sparkline _pulse;
        private Border _telemetryIsland;
        private Border _telemetrySep;
        // 布局分片要用它去改第一列宽度。原本靠 _root.Children[3] 找 —— 那是
        // 一个隐含假设：任何人往 _root 前面插一个图层，紧凑导轨就再也不收起来了。
        // 我加四角压暗时正好踩了这一脚。持有引用，别再数下标。
        private Grid _bodyGrid;
        private readonly List<UIElement> _footTexts = new List<UIElement>();
        private StackPanel _brandWording;
        private Grid _brand;
        private Grid _railGrid;
        private Ellipse _statDot;
        private Border _rail;
        private Rectangle _glow;
        private double _railW = T.RailW;
        private bool _compactRail;
        private bool _forceLayout;
        private bool _parked;
        private TextBlock _daemonLine;
        private Ellipse _daemonDot;
        private Border _gmPill, _gmDot, _pwrChip, _pwrDot;
        private FrameworkElement[] _themeRings;
        private StackPanel _themeBar;
        private Grid _compactThemeOrb;
        private System.Windows.Shapes.Path _maxBtnPath;
        private TextBlock _gmText, _pwrText;
        private System.Windows.Shapes.Path _gmIcon;
        private bool _gmHover;
        private bool _closing;
        private bool _pwrKnown, _pwrAc, _pwrSaver, _pwrBatt;
        private int _pwrPct = -1;
        private Go.Daemon.DaemonHost _hookedDaemon;
        private Action<bool> _powerChangedCb;
        private Action<bool> _powerSaverCb;
        private int _busToken;
        private int _lastTrayState = -1;
        private string _lastTrayTip;
        private bool _lastTrayInstalled;

        private StackPanel _toastHost;
        private Grid _palette;
        private TextBox _paletteInput;
        private StackPanel _paletteResults;
        private List<PaletteItem> _pResults = new List<PaletteItem>();
        private List<PaletteItem> _pAll = new List<PaletteItem>();
        private int _pIndex;
        private string _pPendingConfirm;
        private bool _placeholderOn;
        private const string PaletteHint = "搜索页面、动作或命令…";

        /// <summary>占位提示直接写在输入框里：WPF 没有原生 placeholder，
        /// 叠一个 TextBlock 会被 TextBox 模板遮住，这里用"文本 + 焦点清除"的稳妥做法。</summary>
        private void SetPlaceholder()
        {
            _placeholderOn = true;
            _paletteInput.Text = PaletteHint;
            _paletteInput.Foreground = T.S(T.Mute);
        }
        private TextBlock _pEmpty;
        private Grid _histBack, _histFwd;

        private List<string> _history = new List<string>();
        private int _histPos = -1;
        private int _navDir = 1;

        private readonly string _statePath;

        /// <summary>离屏渲染 / 自检时置真：不读取也不继承上次的窗口状态。</summary>
        public static bool SkipStateRestore;

        public Shell(Core core)
        {
            _core = core;
            core.Shell = this;
            string dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameOptimizer");
            _statePath = IOPath.Combine(dir, "ui.json");

            Title = "GameOptimizer Pro";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Background = T.DeckBrush();
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            MinWidth = 960; MinHeight = 600;

            Rect wa = SystemParameters.WorkArea;
            Width = Math.Min(1560, wa.Width * 0.95);
            Height = Math.Min(1020, wa.Height * 0.97);
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;

            // 图标解码（多帧 ICO，含 256×256）挪到窗口显示之后：
            // 它只为任务栏/Alt+Tab 服务，晚一帧无感，但在冷启动关键路径上白占 5~20ms。
            Loaded += delegate { ApplyBrandIcon(); };

            WindowChrome wc = new WindowChrome();
            // 标题栏交给系统：拖动、双击最大化、边缘吸附、右键系统菜单全部按原厂语义。
            // 可点控件用 IsHitTestVisibleInChrome 单独挖出来（见 Shell.Chrome.cs）。
            wc.CaptionHeight = T.TitleH;
            wc.ResizeBorderThickness = new Thickness(6);
            wc.GlassFrameThickness = new Thickness(0);
            wc.CornerRadius = new CornerRadius(0);
            wc.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, wc);

            Boot.M("shell_start");
            RestoreState();          // 先恢复主题与几何，Build 才能用正确的强调色渲染首帧
            Boot.M("state_restored");
            Build();
            Boot.M("shell_built");

            _beat = new DispatcherTimer();
            _beat.Interval = TimeSpan.FromMilliseconds(1000);
            _beat.Tick += OnBeat;
            _beat.Start();

            // 每 400ms 记录一个"别人的"前台进程，避免点按钮时 GetForegroundWindow 返回自己
            _selfPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            _fgWatch = new DispatcherTimer();
            _fgWatch.Interval = TimeSpan.FromMilliseconds(400);
            _fgWatch.Tick += delegate { TrackForeground(); };
            _fgWatch.Start();

            PreviewKeyDown += OnKey;
            SizeChanged += delegate { ApplyShellLayout(); };
            StateChanged += delegate
            {
                // 最小化 = 只是最小化：任务栏按钮必须留下，采样暂停即可。
                // Rev130 修复：从最大化状态最小化再恢复（Minimized -> Maximized）同样必须 Resume()，
                // 否则全站 1Hz 采样、时钟与图表永久停摆；同时补偿无边框最大化 7px 溢出边距并切换图标。
                SyncMaximizeChrome();
                if (WindowState == WindowState.Minimized) Suspend();
                else Resume();
            };
        }

        // ====================================================================
        //  构建
        // ====================================================================
        private void Build()
        {
            // 主题切换会再次调用 Build：先清空随视觉树重建的缓存与状态指纹，避免导航/侧栏残留旧色或卡成 --:-- / —。
            _navs.Clear();
            _wraps.Clear();
            _footTexts.Clear();
            _driftShown = -1;
            _clockMin = -1;
            _pwrKnown = false;
            _palette = null;
            _paletteBuilt = false;
            _pAll.Clear();
            _pResults.Clear();
            _root = new Grid();

            // 整窗底：极缓的上下渐变，让窗口读成"嵌进机箱的台面"，而不是一块纯黑。
            Rectangle bg = new Rectangle();
            bg.Fill = T.DeckBrush();
            _root.Children.Add(bg);

            DrawingBrush tile = new DrawingBrush();
            tile.TileMode = TileMode.Tile;
            tile.Viewport = new Rect(0, 0, 16, 16);
            tile.ViewportUnits = BrushMappingMode.Absolute;
            tile.Stretch = Stretch.None;
            // 点阵从 5.5% 降到 3.1%：它该是"纸的纹理"，不是"网格"——
            // 太亮会跟内容抢对比度，读者第一眼看到的是噪点而不是数字。
            GeometryDrawing dot = new GeometryDrawing(T.S(0x08FFFFFF), null, T.G("M0,0 H1 V1 H0 Z"));
            tile.Drawing = dot;
            tile.Freeze();
            Rectangle grid = new Rectangle();
            grid.Fill = tile;
            grid.IsHitTestVisible = false;
            _root.Children.Add(grid);

            Rectangle glow = new Rectangle();
            RadialGradientBrush rgb = new RadialGradientBrush();
            rgb.Center = new Point(0.18, -0.06);
            rgb.GradientOrigin = new Point(0.18, -0.06);
            rgb.RadiusX = 0.78; rgb.RadiusY = 0.85;
            Color ac = T.C(T.Accent);
            Color ac2 = T.C(T.Accent2);
            rgb.GradientStops.Add(new GradientStop(Color.FromArgb(54, ac.R, ac.G, ac.B), 0));
            rgb.GradientStops.Add(new GradientStop(Color.FromArgb(20, ac.R, ac.G, ac.B), 0.42));
            rgb.GradientStops.Add(new GradientStop(Color.FromArgb(0, ac.R, ac.G, ac.B), 1));
            rgb.Freeze();
            glow.Fill = rgb;
            glow.IsHitTestVisible = false;
            _glow = glow;
            _root.Children.Add(glow);

            Rectangle counterGlow = new Rectangle();
            RadialGradientBrush crgb = new RadialGradientBrush();
            crgb.Center = new Point(0.92, 0.94);
            crgb.GradientOrigin = new Point(0.92, 0.94);
            crgb.RadiusX = 0.68; crgb.RadiusY = 0.68;
            crgb.GradientStops.Add(new GradientStop(Color.FromArgb(40, ac2.R, ac2.G, ac2.B), 0));
            crgb.GradientStops.Add(new GradientStop(Color.FromArgb(14, ac2.R, ac2.G, ac2.B), 0.46));
            crgb.GradientStops.Add(new GradientStop(Color.FromArgb(0, ac2.R, ac2.G, ac2.B), 1));
            crgb.Freeze();
            counterGlow.Fill = crgb;
            counterGlow.IsHitTestVisible = false;
            _root.Children.Add(counterGlow);

            Rectangle zenithGlow = new Rectangle();
            RadialGradientBrush zrgb = new RadialGradientBrush();
            zrgb.Center = new Point(0.62, 0.04);
            zrgb.GradientOrigin = new Point(0.62, 0.04);
            zrgb.RadiusX = 0.48; zrgb.RadiusY = 0.36;
            zrgb.GradientStops.Add(new GradientStop(Color.FromArgb(22, ac2.R, ac2.G, ac2.B), 0));
            zrgb.GradientStops.Add(new GradientStop(Color.FromArgb(0, ac2.R, ac2.G, ac2.B), 1));
            zrgb.Freeze();
            zenithGlow.Fill = zrgb;
            zenithGlow.IsHitTestVisible = false;
            _root.Children.Add(zenithGlow);

            Border frame = new Border();
            frame.BorderBrush = T.S(T.AccentA(0x34));
            frame.BorderThickness = new Thickness(1);
            frame.IsHitTestVisible = false;
            _root.Children.Add(frame);

            // 四角压暗：屏幕边缘是仪器的外壳，中间才是玻璃。没有这一层，
            // 大屏上整面窗口会均匀发亮，读起来"平"，也压不住内容的层次。
            Rectangle vig = new Rectangle();
            RadialGradientBrush vg = new RadialGradientBrush();
            vg.Center = new Point(0.5, 0.42);
            vg.GradientOrigin = new Point(0.5, 0.42);
            vg.RadiusX = 0.78;
            vg.RadiusY = 0.85;
            vg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45));
            vg.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 0, 0, 0), 1));
            vg.Freeze();
            vig.Fill = vg;
            vig.IsHitTestVisible = false;
            _root.Children.Add(vig);

            Grid body = new Grid();
            _bodyGrid = body;
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_railW) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            _root.Children.Add(body);

            _rail = BuildRail();
            Boot.M("rail");
            body.Children.Add(_rail);

            Grid right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(T.TitleH) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition());
            Grid.SetColumn(right, 1);
            body.Children.Add(right);

            right.Children.Add(BuildTitleBar());
            Boot.M("titlebar");

            UIElement banner = BuildBanner();
            Grid.SetRow(banner, 1);
            right.Children.Add(banner);

            // 页面宿主叠在一层"图纸"基底上：面板之间的缝隙与短页面的余白
            // 因此读作刻度区，而不是没渲染出来的黑。内容控件自己不画 Background，
            // 所以必须靠外面这层 Grid 承载画刷。
            Grid stage = new Grid();
            stage.Background = Ui.Blueprint();
            _host = new ContentControl();
            stage.Children.Add(_host);
            Grid.SetRow(stage, 2);
            right.Children.Add(stage);

            _toastHost = new StackPanel();
            _toastHost.VerticalAlignment = VerticalAlignment.Bottom;
            _toastHost.HorizontalAlignment = HorizontalAlignment.Right;
            _toastHost.Margin = new Thickness(0, 0, 26, 22);
            // 注意：设成 false 会让子元素也拿不到命中，点击消消息就成了死代码。
            // 这里保持命中，但 Host 尺寸只包住 Toast 本身，不会挡住整页。
            _toastHost.IsHitTestVisible = true;
            _root.Children.Add(_toastHost);

            // 命令面板推迟到第一次 Ctrl+K 再建：它在冷启动里占 97ms，
            // 而绝大多数会话根本不会打开它。Build 里不再付这笔钱。
            Boot.M("palette_deferred");

            Content = _root;
            ApplyShellLayout();
        }

    }
}
