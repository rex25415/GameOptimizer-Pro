// ============================================================================
//  Graphics.Adapters.cs — 适配器枚举（纯数据）与适配器行网格（渲染）
//
//  Rev86 拆法：ReadAdapters 原来 108 行、4 层嵌套，WMI 查询和"卡片长什么样"混在
//  同一段里 —— 想知道"读了几处注册表"要跳过一半的 StackPanel。现在上半段只产数据
//  （键 → 类键条目 → WMI 条目 → 合并），下半段只吃数据（行网格 / 空态），
//  中间没有共享状态，每一段都能单独对表。
//
//  行网格列：名称(*) / 驱动版本(110) / 类型徽标(92) / 操作(56)，与内存页 Top 表同宽。
//  驱动版本是等宽列：同一台机器上两张卡的版本号要能按数字位对齐着比。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Graphics : Page
    {
        // 124 而不是 DESIGN-86 §3 的 110：字号提档后 FsSmall(12) 等宽数字每字符约 7.2px，
        // WDDM 版本号最长 16 字符（32.0.21045.16010）= 115px，110 会截成 "32.0.21045.160…"。
        // 取 4 的倍数，跨页仍是同一套栅格。
        private const double ColDrvW = 124;
        private const double ColTagW = Ui.ColTag;
        private const double ColActW = 56;
        private const double RowH = 36;      // 两行内容（名称 + 输出来源），行高 36 + 上下 9 = 54

        private List<GpuInfo> _adapterList;
        private bool _adapterScanned;
        private volatile bool _scanning;

        // ====================================================================
        //  面板
        // ====================================================================
        private UIElement BuildAdapters(Core c)
        {
            StackPanel body = new StackPanel();
            _adapterMeta = Ui.Mono("\u2014", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("GPU 适配器", "ADAPTERS · DXGI LUID / CLASS KEY", _adapterMeta));
            body.Children.Add(Gap(12));

            body.Children.Add(BuildAdapterHead());
            body.Children.Add(Ui.Rule(T.LineSoft, 0));

            _adapterHost = new StackPanel();
            body.Children.Add(_adapterHost);

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));

            TextBlock foot = Ui.Tx("通过 DXGI 枚举物理适配器 LUID，与显卡类键（DriverDesc / DriverVersion / qwMemorySize）精确对齐；" +
                                   "显示输出状态关联自原生 EnumDisplayDevices，零 WMI 阻塞。",
                T.FsMicro, T.Mute, T.FCn);
            foot.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(foot);

            return Ui.Panel(body, T.PadPanel);
        }

        private Grid BuildAdapterHead()
        {
            Grid g = NewAdapterGrid();
            g.Height = 22;

            TextBlock name = Ui.Label("适配器", T.Mute);
            name.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(name);

            TextBlock drv = Ui.Label("驱动版本", T.Mute);
            drv.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(drv, 1);
            g.Children.Add(drv);

            TextBlock kind = Ui.Label("类型", T.Mute);
            kind.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(kind, 2);
            g.Children.Add(kind);

            TextBlock act = Ui.Label("操作", T.Mute);
            act.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(act, 3);
            g.Children.Add(act);
            return g;
        }

        private static Grid NewAdapterGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColDrvW) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColTagW) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColActW) });
            return g;
        }

        // ====================================================================
        //  行
        // ====================================================================
        private UIElement AdapterRow(GpuInfo g, Core c)
        {
            Grid row = NewAdapterGrid();
            row.Height = RowH;

            StackPanel title = new StackPanel();
            title.VerticalAlignment = VerticalAlignment.Center;
            TextBlock name = Ui.Tx(g.Name, T.FsBody, T.Hi, T.FCn);
            name.FontWeight = FontWeights.SemiBold;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            title.Children.Add(name);

            TextBlock note = Ui.Tx(AdapterNote(g), T.FsNano, T.Mute, T.FCn);
            note.Margin = new Thickness(0, 3, 12, 0);
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            note.TextWrapping = TextWrapping.NoWrap;
            title.Children.Add(note);
            row.Children.Add(title);

            // 等宽列：版本号右对齐，两张卡的版本位能直接上下对读。
            TextBlock drv = Ui.Mono(string.IsNullOrEmpty(g.Driver) ? "\u2014" : g.Driver, T.FsSmall, T.Dim);
            drv.HorizontalAlignment = HorizontalAlignment.Right;
            drv.VerticalAlignment = VerticalAlignment.Center;
            drv.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(drv, 1);
            row.Children.Add(drv);

            int kind = AdapterKind(g);
            Border tag = Ui.Tag(TypeWord(kind), TypeTint(kind));
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(tag, 2);
            row.Children.Add(tag);

            Btn copy = new Btn("复制", BtnKind.Quiet);
            copy.Compact = true;
            copy.Height = T.BtnHSm;
            copy.Width = ColActW;
            copy.HorizontalAlignment = HorizontalAlignment.Right;
            copy.VerticalAlignment = VerticalAlignment.Center;
            GpuInfo captured = g;
            Core core = c;
            copy.Click += delegate { CopyAdapter(core, captured); };
            Tip.Attach(copy, "复制「名称 · 驱动版本 · 显存」到剪贴板。报驱动兼容问题时直接贴给对方。");
            Grid.SetColumn(copy, 3);
            row.Children.Add(copy);

            // 左缘 2px：正在驱动显示输出的那张卡用强调色，其余用结构灰。
            Border shell = Ui.RowShell(row, g.Present ? T.Accent : T.LineMid, 9);
            shell.Background = Brushes.Transparent;
            shell.MouseEnter += delegate { shell.Background = T.S(T.LineFaint); };
            shell.MouseLeave += delegate { shell.Background = Brushes.Transparent; };
            Tip.Attach(shell, "来源：DXGI LUID + HKLM\\" + AdapterClass + "\n" +
                              "LUID：" + (string.IsNullOrEmpty(g.Luid) ? "\u2014" : g.Luid) +
                              " · 显存：" + Vram(g.VramBytes) + "\n" +
                              "「正在驱动」由显示输出拓扑（EnumDisplayDevices）关联得出，零 WMI 查询。");
            return shell;
        }

        /// <summary>说明行：这张卡此刻在做什么，带上物理 LUID 与显存容量。</summary>
        private static string AdapterNote(GpuInfo g)
        {
            string s;
            if (!g.PresentKnown) s = "输出拓扑未匹配";
            else if (g.Present && g.ResW > 0)
            {
                s = "正在驱动 " + g.ResW + "\u00D7" + g.ResH;
                if (g.Hz > 0) s += " · " + g.Hz + " Hz";
            }
            else if (g.Present) s = "正在驱动当前显示输出";
            else s = "未直接连接显示输出";

            if (g.VramBytes > 0) s += " · 显存 " + Vram(g.VramBytes);
            if (!string.IsNullOrEmpty(g.Luid)) s += " · LUID " + g.Luid;
            if (!string.IsNullOrEmpty(g.AuxLuid)) s += " · MUX 副节点 " + g.AuxLuid;
            return s;
        }

        /// <summary>0 = 核显 / 1 = 独显 / 2 = 虚拟 / -1 = 判不出。DXGI/类键已知时直接采用，否则按特征判。</summary>
        private static int AdapterKind(GpuInfo g)
        {
            if (g == null) return -1;
            if (g.DiscreteKnown == 1 || g.DiscreteKnown == 0 || g.DiscreteKnown == 2) return g.DiscreteKnown;
            if (Hardware.IsVirtualGpu(g.Name)) return 2;
            string n = g.Name.ToLowerInvariant();
            if (n.IndexOf("arc", StringComparison.Ordinal) >= 0) return 1;          // Intel Arc 是独显，先于 intel 判
            if (n.IndexOf("intel", StringComparison.Ordinal) >= 0) return 0;
            if (n.IndexOf("uhd graphics", StringComparison.Ordinal) >= 0) return 0;
            if (n.IndexOf("hd graphics", StringComparison.Ordinal) >= 0) return 0;
            if (n.IndexOf("iris", StringComparison.Ordinal) >= 0) return 0;
            if (n.IndexOf("vega", StringComparison.Ordinal) >= 0 && n.IndexOf("rx", StringComparison.Ordinal) < 0) return 0;
            if (g.VramBytes >= 1073741824L) return 1;
            if (g.VramBytes > 0) return 0;                                          // 专用显存 < 1 GB：共享/集成
            return -1;
        }

        private static string TypeWord(int kind)
        {
            if (kind == 1) return "独显";
            if (kind == 0) return "核显";
            if (kind == 2) return "虚拟";
            return "类型未知";
        }

        private static uint TypeTint(int kind)
        {
            if (kind == 1) return T.Text;
            if (kind == 0) return T.Dim;
            return T.Mute;
        }

        private void CopyAdapter(Core c, GpuInfo g)
        {
            if (g == null) return;
            string text = g.Name + " · 驱动 " + (string.IsNullOrEmpty(g.Driver) ? "\u2014" : g.Driver) +
                          " · 显存 " + Vram(g.VramBytes);
            bool ok = false;
            try { Clipboard.SetText(text); ok = true; }
            catch (Exception ex)
            {
                // 剪贴板被其它进程占用时 SetText 会抛 COM 异常：可预期失败，记账即可。
                Diag.Fail("gfx.adapter.copy", ex);
            }
            if (c != null) c.Shell.Toast(ok ? "已复制适配器信息" : "复制失败 · 剪贴板被占用", ok);
        }

        private static string Vram(long bytes)
        {
            if (bytes <= 0) return "\u2014";
            double gb = bytes / 1073741824.0;
            if (gb >= 0.995) return gb.ToString("0.#") + " GB";
            return (bytes / 1048576.0).ToString("0") + " MB";
        }

        // ====================================================================
        //  扫描：首建直读 Core.Adapters / Core.Displays（零 WMI）；进页/重探走后台
        // ====================================================================
        private void RefreshAdapters() { RefreshAdapters(_adapterScanned, false); }

        private void RefreshAdapters(bool forceRescan) { RefreshAdapters(forceRescan, false); }

        private void RefreshAdapters(bool forceRescan, bool silent)
        {
            if (_adapterHost == null) return;

            if (!forceRescan && C != null && C.Adapters != null && C.Adapters.Count > 0)
            {
                List<Hardware.DisplayInfo> cachedDisplays = C.Displays;
                List<GpuInfo> fast = BuildAdapterList(C.Adapters, cachedDisplays);
                ApplyAdapters(fast, cachedDisplays, false, true);
                return;
            }

            bool keepExisting = silent && _adapterScanned && _adapterList != null && _adapterList.Count > 0;
            if (!keepExisting)
            {
                _adapterHost.Children.Clear();
                _adapterHost.Children.Add(ScanningRow());
                if (_adapterMeta != null) _adapterMeta.Text = "SCANNING\u2026";
                WriteAdapterCells();
            }

            if (_scanning) return;
            _scanning = true;

            int gen = _scanGen;
            int bgen = _buildGen;
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            Core coreRef = C;
            System.Threading.Thread th = new System.Threading.Thread(delegate ()
            {
                List<Hardware.GpuAdapterInfo> hw = null;
                List<Hardware.DisplayInfo> disp = null;
                List<GpuInfo> list = null;
                bool scanOk = true;
                try
                {
                    hw = Hardware.ReadAdapters();
                    disp = Hardware.ReadDisplays();
                    list = BuildAdapterList(hw, disp);
                }
                catch (Exception ex)
                {
                    scanOk = false;
                    Diag.Fail("gfx.adapters.scan", ex);
                    list = new List<GpuInfo>();
                }
                if (gen != _scanGen || bgen != _buildGen || _left) return;
                try
                {
                    ui.BeginInvoke(new Action(delegate
                    {
                        if (gen != _scanGen || bgen != _buildGen || _left || _adapterHost == null) return;
                        _scanning = false;
                        if (coreRef != null)
                        {
                            if (hw != null && hw.Count > 0) coreRef.Adapters = hw;
                            if (disp != null) coreRef.Displays = disp;
                        }
                        ApplyAdapters(list, disp, false, scanOk);
                    }));
                }
                catch (Exception ex)
                {
                    if (gen == _scanGen && bgen == _buildGen) _scanning = false;
                    Diag.Fail("gfx.adapters.post", ex);
                }
            });
            th.IsBackground = true;
            th.Priority = System.Threading.ThreadPriority.BelowNormal;
            th.Name = "go.gpuscan";
            th.Start();
        }

        /// <summary>"还没采到"的形态：压暗一行说明，不画空卡片，也不显示 0。</summary>
        private UIElement ScanningRow()
        {
            StackPanel wait = new StackPanel();
            wait.Orientation = Orientation.Horizontal;
            wait.Margin = new Thickness(2, 10, 0, 10);
            wait.Opacity = 0.7;
            wait.Children.Add(Ui.Icon(Glyph.Graphics, 14, T.Dim, 1.3));
            TextBlock wt = Ui.Mono("正在枚举 DXGI LUID 与原生显示输出\u2026", T.FsNano, T.Mute);
            wt.Margin = new Thickness(9, 0, 0, 0);
            wait.Children.Add(wt);
            return wait;
        }

        private void ApplyAdapters(List<GpuInfo> list, List<Hardware.DisplayInfo> displays, bool wmiUsed, bool wmiOk)
        {
            if (_adapterHost == null) return;
            if (list == null) list = new List<GpuInfo>();
            _adapterList = list;
            _adapterScanned = true;

            _adapterHost.Children.Clear();
            if (list.Count == 0) _adapterHost.Children.Add(AdapterEmpty(wmiOk));
            else
            {
                for (int i = 0; i < list.Count; i++)
                    _adapterHost.Children.Add(AdapterRow(list[i], C));
            }

            // 显存总量优先取独显或正在呈现的那张卡（仅作为回退，各卡优先用自己的 VramTotalBytes）。
            _vramTotal = 0;
            for (int i = 0; i < list.Count; i++)
                if (AdapterKind(list[i]) == 1 && list[i].VramBytes > 0) { _vramTotal = list[i].VramBytes; break; }
            if (_vramTotal == 0)
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Present && list[i].VramBytes > 0) { _vramTotal = list[i].VramBytes; break; }
            if (_vramTotal == 0)
                for (int i = 0; i < list.Count; i++)
                    if (list[i].VramBytes > 0) { _vramTotal = list[i].VramBytes; break; }

            string meta = list.Count == 0 ? "NO ADAPTER" : (list.Count + (list.Count == 1 ? " ADAPTER" : " ADAPTERS · DXGI"));
            if (_adapterMeta != null) _adapterMeta.Text = meta;
            if (_headMeta != null) _headMeta.Text = "SCAN " + DateTime.Now.ToString("HH:mm:ss");

            ApplyDisplays(displays);
            WriteAdapterCells();
            RefreshGpu();
        }

        /// <summary>空态：Ui.Empty 会把"为什么空"和"下一步做什么"一起给出来。</summary>
        private UIElement AdapterEmpty(bool scanOk)
        {
            Btn again = new Btn("重新探测", BtnKind.Ghost);
            again.Height = T.BtnHSm;
            again.SetIcon(Glyph.Refresh, 11);
            again.Click += delegate { RefreshAdapters(true, false); };

            string why = scanOk
                ? "DXGI 与显卡类键均未返回显示适配器。常见于远程会话 / 无驱虚拟环境，或显卡驱动未就绪。"
                : "显卡适配器探测异常，请检查显示驱动状态。";
            return Ui.Empty(Glyph.Graphics, "未检测到显示适配器", why, T.Mute, again);
        }

        // ====================================================================
        //  读数带：适配器两格
        // ====================================================================
        private void WriteAdapterCells()
        {
            if (_cellAdapters == null) return;
            if (!_adapterScanned)
            {
                Set(_cellAdapters.Val, "\u2014");
                Set(_cellAdapters.Note, "正在枚举 DXGI LUID\u2026");
                Set(_cellDriver.Val, "\u2014");
                Set(_cellDriver.Note, "等待适配器结果\u2026");
                return;
            }

            List<GpuInfo> list = _adapterList == null ? new List<GpuInfo>() : _adapterList;
            Set(_cellAdapters.Val, list.Count.ToString());

            int dgpu = 0, igpu = 0, vgpu = 0, unknown = 0;
            for (int i = 0; i < list.Count; i++)
            {
                int k = AdapterKind(list[i]);
                if (k == 1) dgpu++;
                else if (k == 0) igpu++;
                else if (k == 2) vgpu++;
                else unknown++;
            }
            string note = "";
            if (dgpu > 0) note = "独显 " + dgpu;
            if (igpu > 0) note += (note.Length > 0 ? " · " : "") + "核显 " + igpu;
            if (vgpu > 0) note += (note.Length > 0 ? " · " : "") + "虚拟 " + vgpu;
            if (unknown > 0) note += (note.Length > 0 ? " · " : "") + "未知 " + unknown;
            Set(_cellAdapters.Note, list.Count == 0 ? "未检测到适配器" : note);

            GpuInfo main = Primary();
            Set(_cellDriver.Val, main == null || string.IsNullOrEmpty(main.Driver) ? "\u2014" : main.Driver);
            Set(_cellDriver.Note, main == null ? "没有可读的适配器条目" : main.Name);
        }

        /// <summary>读数带取哪张卡：优先取独显，其次正在驱动显示的卡，否则显存最大的卡。</summary>
        private GpuInfo Primary()
        {
            if (_adapterList == null || _adapterList.Count == 0) return null;
            for (int i = 0; i < _adapterList.Count; i++)
                if (AdapterKind(_adapterList[i]) == 1) return _adapterList[i];
            for (int i = 0; i < _adapterList.Count; i++)
                if (_adapterList[i].Present) return _adapterList[i];
            GpuInfo best = _adapterList[0];
            for (int i = 1; i < _adapterList.Count; i++)
                if (_adapterList[i].VramBytes > best.VramBytes) best = _adapterList[i];
            return best;
        }

        // ====================================================================
        //  数据：DXGI LUID + 显卡类键 + 原生显示输出匹配（零 WMI，纯数据）
        // ====================================================================
        private static List<GpuInfo> BuildAdapterList(List<Hardware.GpuAdapterInfo> hw, List<Hardware.DisplayInfo> displays)
        {
            List<GpuInfo> list = new List<GpuInfo>();
            if (hw != null)
            {
                for (int i = 0; i < hw.Count; i++)
                {
                    Hardware.GpuAdapterInfo a = hw[i];
                    if (a == null || a.Software) continue;
                    GpuInfo g = new GpuInfo();
                    g.Name = !string.IsNullOrEmpty(a.DriverDesc) ? a.DriverDesc : a.ShortName;
                    if (string.IsNullOrEmpty(g.Name)) continue;
                    g.ShortName = a.ShortName;
                    g.Driver = a.DriverVersion ?? "";
                    g.Luid = a.Luid ?? "";
                    g.AuxLuid = a.AuxLuid ?? "";
                    g.VramBytes = a.VramBytes > 0 ? a.VramBytes : 0;
                    g.DiscreteKnown = a.Virtual ? 2 : (a.Discrete ? 1 : 0);
                    list.Add(g);
                }
            }
            if (list.Count == 0)
            {
                List<string> keys = AdapterKeys();
                list = ReadClassAdapters(keys);
            }
            MatchDisplays(list, displays);
            return list;
        }

        private static void MatchDisplays(List<GpuInfo> list, List<Hardware.DisplayInfo> displays)
        {
            if (list == null || list.Count == 0) return;
            bool anyDisp = displays != null && displays.Count > 0;
            for (int i = 0; i < list.Count; i++) list[i].PresentKnown = anyDisp;
            if (!anyDisp) return;

            for (int d = 0; d < displays.Count; d++)
            {
                Hardware.DisplayInfo di = displays[d];
                if (di == null) continue;
                int bestIdx = -1;
                int bestScore = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    int score = ScoreDisplayMatch(list[i], di);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0) continue;

                GpuInfo g = list[bestIdx];
                g.Present = true;
                if (di.Main || g.ResW <= 0)
                {
                    g.ResW = di.Width;
                    g.ResH = di.Height;
                    g.Hz = di.RefreshHz;
                }
            }
        }

        private static int ScoreDisplayMatch(GpuInfo g, Hardware.DisplayInfo di)
        {
            if (g == null || di == null) return 0;
            if (!string.IsNullOrEmpty(g.DeviceName) && !string.IsNullOrEmpty(di.DeviceName) &&
                string.Equals(g.DeviceName.Trim(), di.DeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
                return 2000;

            string adap = (di.AdapterName ?? "").Trim();
            if (adap.Length == 0) return 0;

            if (!string.IsNullOrEmpty(g.Luid) && adap.IndexOf(g.Luid, StringComparison.OrdinalIgnoreCase) >= 0)
                return 1500;

            string gName = (g.Name ?? "").Trim();
            string gShort = (g.ShortName ?? "").Trim();
            if (gName.Length > 0 && string.Equals(gName, adap, StringComparison.OrdinalIgnoreCase))
                return 1000;
            if (gShort.Length > 0 && string.Equals(gShort, adap, StringComparison.OrdinalIgnoreCase))
                return 900;

            string nAdap = NormalizeGpuName(adap);
            string nName = NormalizeGpuName(gName);
            string nShort = NormalizeGpuName(gShort);
            if (nName.Length > 0 && nName == nAdap) return 800;
            if (nShort.Length > 0 && nShort == nAdap) return 700;

            int s1 = SubstringScore(nName, nAdap);
            int s2 = SubstringScore(nShort, nAdap);
            return s1 > s2 ? s1 : s2;
        }

        private static int SubstringScore(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            if (a.IndexOf(b, StringComparison.Ordinal) < 0 && b.IndexOf(a, StringComparison.Ordinal) < 0)
                return 0;
            int minLen = Math.Min(a.Length, b.Length);
            int diff = Math.Abs(a.Length - b.Length);
            int score = 400 + minLen * 2 - diff;
            if (score < 100) score = 100;
            if (score > 650) score = 650;
            return score;
        }

        private static string NormalizeGpuName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim().ToLowerInvariant();
            t = t.Replace("(tm)", "").Replace("(r)", "").Replace("(c)", "");
            while (t.IndexOf("  ", StringComparison.Ordinal) >= 0)
                t = t.Replace("  ", " ");
            return t.Trim();
        }

        /// <summary>显卡类键下的数字子键（0000 / 0001 …）。</summary>
        private static List<string> AdapterKeys()
        {
            List<string> subs = new List<string>();
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(AdapterClass, false))
                {
                    if (k == null) return subs;
                    string[] names = k.GetSubKeyNames();
                    Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (names[i].Length == 0) continue;
                        if (names[i][0] < '0' || names[i][0] > '9') continue;
                        subs.Add(names[i]);
                    }
                }
            }
            catch (Exception ex) { Diag.Fail("gfx.adapters.enum", ex); }
            return subs;
        }

        private static List<GpuInfo> ReadClassAdapters(List<string> keys)
        {
            List<GpuInfo> list = new List<GpuInfo>();
            for (int i = 0; i < keys.Count && list.Count < 4; i++)
            {
                GpuInfo g = ReadClassAdapter(AdapterClass + "\\" + keys[i]);
                if (g == null) continue;
                if (IsDuplicate(list, g.Name)) continue;
                list.Add(g);
            }
            return list;
        }

        private static GpuInfo ReadClassAdapter(string sub)
        {
            RegValue desc = Reg.Read("HKLM", sub, "DriverDesc");
            if (!desc.Existed || desc.Denied || desc.Error != null || desc.Value == null) return null;

            string nm = Convert.ToString(desc.Value).Trim();
            if (nm.Length == 0) return null;

            GpuInfo g = new GpuInfo();
            g.Name = nm;

            RegValue dv = Reg.Read("HKLM", sub, "DriverVersion");
            if (dv.Existed && dv.Value != null) g.Driver = Convert.ToString(dv.Value).Trim();

            RegValue vr = Reg.Read("HKLM", sub, "HardWareInformation.qwMemorySize");
            if (vr.Existed && vr.Value != null) g.VramBytes = LongOf(vr.Value);
            if (g.VramBytes < 0) g.VramBytes = 0;
            return g;
        }

        private static bool IsDuplicate(List<GpuInfo> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static long LongOf(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt64(v); }
            catch (Exception ex) { Diag.Fail("gfx.adapters.long", ex); return 0; }
        }
    }
}