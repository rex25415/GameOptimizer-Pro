// ============================================================================
//  Graphics.Prefs.cs — GPU 首选项读写（Graphics 分片）
//
//  Rev86：从"一张平铺的 8 行表 + 每行一枚模式胶囊"改成"按影响分组的行网格"。
//  同一张表里 2/1/0 三种模式混排时，读者要逐行读胶囊才知道这台机器把哪些程序钉在独显上；
//  分组之后，"强制高性能"这一组本身就是答案，行里那枚胶囊就是重复信息，删掉。
//  每组一个 PanelHead（组名 + GpuPreference 值 + 条数）和一张行网格：程序名 / 首选项原文 / 操作。
//
//  诚实性：键不存在是"合法的空"（Windows 首次写入才建键），不算读失败；
//  HKCU 被策略挡住时才显示 Ui.Empty，并把被挡的路径写出来。
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
        private const double ColRawW = 110;
        private const double ColCopyW = 56;

        private UIElement BuildPrefs(Core c)
        {
            StackPanel body = new StackPanel();
            _prefMeta = Ui.Mono("\u2014", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("GPU 首选项", "USER GPUPREFERENCES · HKCU", _prefMeta));
            body.Children.Add(Gap(12));

            _prefHost = new StackPanel();
            body.Children.Add(_prefHost);

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));

            TextBlock note = Ui.Tx("GpuPreference=2 表示强制高性能 GPU，对混合显卡笔记本最直接有效；" +
                                   "改这一项走 Windows 设置（显示 → 图形），本页只读、不写 HKCU。",
                T.FsMicro, T.Mute, T.FCn);
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 16;
            body.Children.Add(note);

            return Ui.Panel(body, T.PadPanel);
        }

        // ====================================================================
        //  刷新：读 → 分成三组 → 行网格
        // ====================================================================
        private void RefreshPrefs()
        {
            if (_prefHost == null) return;
            int total = 0;
            List<PrefInfo> list = ReadPrefs(out total);

            _prefHost.Children.Clear();
            if (total < 0)
            {
                _prefHost.Children.Add(Ui.Empty(Glyph.Warn, "GPU 首选项不可读",
                    "HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences 拒绝访问：可能是组策略限制，或当前账户没有读权限。",
                    T.Mute, null));
            }
            else if (list.Count == 0)
            {
                _prefHost.Children.Add(EmptyHint());
            }
            else
            {
                BuildGroups(list);
            }

            if (_prefMeta != null)
            {
                _prefMeta.Text = total < 0
                    ? "READ ONLY · ACCESS DENIED"
                    : total == 0
                        ? "READ ONLY · NO ENTRY"
                        : "READ ONLY · " + total + (total == 1 ? " ENTRY" : " ENTRIES") + (total > 8 ? " · TOP 8" : "");
            }
        }

        /// <summary>真的没有条目：给一句"在哪里加"，不是一句"没有数据"。</summary>
        private UIElement EmptyHint()
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;

            sp.Children.Add(Ui.Icon(Glyph.Target, 14, T.Dim, 1.35));

            StackPanel tx = new StackPanel();
            tx.Margin = new Thickness(9, 0, 0, 0);
            tx.Children.Add(Ui.Tx("尚无应用级 GPU 首选项", T.FsBody, T.Dim, T.FCn));
            TextBlock hint = Ui.Mono("设置 → 系统 → 显示 → 图形 → 为某个应用指定「高性能」", T.FsNano, T.Mute);
            hint.Margin = new Thickness(0, 5, 0, 0);
            tx.Children.Add(hint);
            sp.Children.Add(tx);
            return sp;
        }

        private void BuildGroups(List<PrefInfo> list)
        {
            List<PrefInfo> high = new List<PrefInfo>();
            List<PrefInfo> save = new List<PrefInfo>();
            List<PrefInfo> auto = new List<PrefInfo>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Mode == 2) high.Add(list[i]);
                else if (list[i].Mode == 1) save.Add(list[i]);
                else auto.Add(list[i]);
            }

            int budget = 8;
            budget = AddGroup(high, "强制高性能", "GPUPREFERENCE = 2 · 钉在独显上", budget);
            budget = AddGroup(save, "强制省电", "GPUPREFERENCE = 1 · 钉在核显 / 省电", budget);
            AddGroup(auto, "由系统决定", "GPUPREFERENCE = 0 或未指定", budget);
        }

        /// <summary>一组：PanelHead + 行网格。budget 是全局"最多显示 8 行"的余额。</summary>
        private int AddGroup(List<PrefInfo> items, string title, string label, int budget)
        {
            if (items.Count == 0 || budget <= 0) return budget;

            if (_prefHost.Children.Count > 0) _prefHost.Children.Add(Gap(14));
            TextBlock count = Ui.Mono(items.Count + (items.Count == 1 ? " ENTRY" : " ENTRIES"), T.FsNano, T.Mute);
            _prefHost.Children.Add(Ui.PanelHead(title, label, count));

            StackPanel rows = new StackPanel();
            rows.Margin = new Thickness(0, 6, 0, 0);
            int shown = Math.Min(budget, items.Count);
            for (int i = 0; i < shown; i++) rows.Children.Add(PrefRow(items[i], C));
            _prefHost.Children.Add(rows);

            if (shown < items.Count)
            {
                TextBlock more = Ui.Mono("还有 " + (items.Count - shown) + " 条未显示 · 本页最多列 8 行", T.FsNano, T.Mute);
                more.Margin = new Thickness(0, 8, 0, 0);
                _prefHost.Children.Add(more);
            }
            return budget - shown;
        }

        private UIElement PrefRow(PrefInfo p, Core c)
        {
            Grid row = NewPrefGrid();
            row.Height = 36;

            StackPanel left = new StackPanel();
            left.VerticalAlignment = VerticalAlignment.Center;

            string exe = System.IO.Path.GetFileName(p.ExePath);
            if (string.IsNullOrEmpty(exe)) exe = p.ExePath;

            TextBlock exeT = Ui.Tx(exe, T.FsSmall, T.Text, T.FCn);
            exeT.TextTrimming = TextTrimming.CharacterEllipsis;
            exeT.TextWrapping = TextWrapping.NoWrap;
            left.Children.Add(exeT);

            TextBlock full = Ui.Mono(p.ExePath, T.FsNano, T.Mute);
            full.Margin = new Thickness(0, 3, 12, 0);
            full.TextTrimming = TextTrimming.CharacterEllipsis;
            full.TextWrapping = TextWrapping.NoWrap;
            left.Children.Add(full);
            row.Children.Add(left);

            TextBlock raw = Ui.Mono(string.IsNullOrEmpty(p.Raw) ? "\u2014" : p.Raw, T.FsNano, T.Dim);
            raw.HorizontalAlignment = HorizontalAlignment.Right;
            raw.VerticalAlignment = VerticalAlignment.Center;
            raw.TextTrimming = TextTrimming.CharacterEllipsis;
            raw.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(raw, 1);
            row.Children.Add(raw);

            Btn copy = new Btn("复制", BtnKind.Quiet);
            copy.Compact = true;
            copy.Height = T.BtnHSm;
            copy.Width = ColCopyW;
            copy.HorizontalAlignment = HorizontalAlignment.Right;
            copy.VerticalAlignment = VerticalAlignment.Center;
            Core core = c;
            string path = p.ExePath;
            copy.Click += delegate { CopyPath(core, path); };
            Tip.Attach(copy, "复制这个程序的完整路径。");
            Grid.SetColumn(copy, 2);
            row.Children.Add(copy);

            Border shell = Ui.RowShell(row, T.LineMid, 9);
            shell.Background = Brushes.Transparent;
            shell.MouseEnter += delegate { shell.Background = T.S(T.LineFaint); };
            shell.MouseLeave += delegate { shell.Background = Brushes.Transparent; };
            Tip.Attach(shell, p.ExePath + "\n" + (string.IsNullOrEmpty(p.Raw) ? "注册表值为空" : "注册表值：" + p.Raw));
            return shell;
        }

        private static Grid NewPrefGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColRawW) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColCopyW) });
            return g;
        }

        private void CopyPath(Core c, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            bool ok = false;
            try { Clipboard.SetText(path); ok = true; }
            catch (Exception ex)
            {
                // 剪贴板被其它进程占用：可预期失败，记账即可。
                Diag.Fail("gfx.pref.copy", ex);
            }
            if (c != null) c.Shell.Toast(ok ? "已复制程序路径" : "复制失败 · 剪贴板被占用", ok);
        }

        /// <summary>
        /// 读取用户级 GPU 首选项。total 约定：条目数；-1 表示读取被拒绝。
        /// 键不存在属于"合法的空"（Windows 首次写入时才建键），不算错误。
        /// </summary>
        private static List<PrefInfo> ReadPrefs(out int total)
        {
            List<PrefInfo> list = new List<PrefInfo>();
            bool denied = false;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Reg.Base("HKCU").OpenSubKey(
                           @"Software\Microsoft\DirectX\UserGpuPreferences", false))
                {
                    if (k != null)
                    {
                        string[] names = k.GetValueNames();
                        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < names.Length; i++)
                        {
                            if (names[i].Length > 0) list.Add(ReadPref(k, names[i]));
                        }
                    }
                }
            }
            catch (System.Security.SecurityException) { denied = true; }
            catch (UnauthorizedAccessException) { denied = true; }
            catch (Exception ex) { Diag.Fail("gfx.prefs.open", ex); denied = true; }
            total = denied ? -1 : list.Count;
            return list;
        }

        private static PrefInfo ReadPref(Microsoft.Win32.RegistryKey k, string name)
        {
            PrefInfo p = new PrefInfo();
            p.ExePath = name;
            try
            {
                object v = k.GetValue(name, null);
                if (v != null) p.Raw = Convert.ToString(v);
            }
            catch (Exception ex) { Diag.Fail("gfx.pref.value", ex); }
            p.Mode = GpuPreference(p.Raw);
            return p;
        }

        private static int GpuPreference(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return -1;
            string[] parts = raw.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim();
                if (t.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(t.Substring(14), out v)) return v;
                }
            }
            return -1;
        }

        // ---- 生命周期 ------------------------------------------------------
        public override void OnEnter()
        {
            _left = false;
            if (_poll == null)
            {
                _poll = new DispatcherTimer();
                _poll.Interval = TimeSpan.FromMilliseconds(1000);
                // 分离两种节拍：
                //   · GPU 占用来自内存里的 GpuSnapshot（PDH 线程每秒发布），读它零成本，1s 一刷；
                //   · 呈现管线是三个注册表键，属于 IO。旧实现跟 GPU 同频，2 秒读 3 次，
                //     用户不动任何设置时它也在读——纯浪费。现在 5 秒一次。
                //     动作区切换后会立即复判，所以不会出现"改了要等 5 秒"。
                _poll.Tick += delegate
                {
                    RefreshGpu();
                    _ioBeat++;
                    if (_ioBeat % 5 == 0) RefreshPipeline();
                };
            }

            if (_firstEnter)
            {
                _firstEnter = false;
                // 首次进页也在后台刷新一次显示器/适配器拓扑，避免一直沿用启动时旧快照
                RefreshAdapters(true, true);
            }
            else
            {
                RefreshAdapters(true, true);
                RefreshPipeline();
                RefreshActions(true);
                RefreshPrefs();
            }
            RefreshGpu();
            _poll.Start();
        }

        public override void OnLeave()
        {
            _left = true;
            _scanGen++;
            _scanning = false;
            if (_poll != null) _poll.Stop();
        }
    }
}
