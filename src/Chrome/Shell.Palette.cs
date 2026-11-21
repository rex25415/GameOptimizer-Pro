// ============================================================================
//  Shell.Palette.cs — 命令面板（Shell 分片）
//  Ctrl+K 模糊检索、分组排序、危险项二次确认
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
        //  命令面板
        // ====================================================================
        private class PaletteItem
        {
            public string Title;
            public string Hint;
            public string Ico;
            public string Type;
            public Action Run;
            public bool Dangerous;
            public string Lower; // 预计算小写检索串，避免每次击键重复拼接+ToLower
        }

        private void BuildPalette()
        {
            _palette = new Grid();
            _palette.Background = T.S(0xCC04060A);
            _palette.Visibility = Visibility.Collapsed;
            _palette.MouseLeftButtonDown += delegate { HidePalette(); };

            Border box = new Border();
            _paletteBox = box;
            box.Width = 636;
            box.MaxHeight = 472;
            box.VerticalAlignment = VerticalAlignment.Top;
            box.Margin = new Thickness(0, 112, 0, 0);
            box.CornerRadius = new CornerRadius(T.RadiusLg);
            box.Background = T.S(0xF60C1322);
            box.BorderBrush = T.S(T.AccentA(0x64));
            box.BorderThickness = new Thickness(1);
            box.RenderTransformOrigin = new Point(0.5, 0);
            _paletteScale = new ScaleTransform(1, 1);
            _paletteTy = new TranslateTransform(0, 0);
            TransformGroup tg = new TransformGroup();
            tg.Children.Add(_paletteScale);
            tg.Children.Add(_paletteTy);
            box.RenderTransform = tg;
            box.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };

            StackPanel sp = new StackPanel();

            Grid inputRow = new Grid();
            inputRow.Margin = new Thickness(16, 14, 16, 10);
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition());
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            System.Windows.Shapes.Path searchIco = Ui.Icon(Glyph.Search, 15, T.Dim, 1.4);
            searchIco.Margin = new Thickness(0, 0, 10, 0);
            inputRow.Children.Add(searchIco);

            Grid inputWrap = new Grid();
            Grid.SetColumn(inputWrap, 1);
            inputRow.Children.Add(inputWrap);

            _pCount = Ui.Mono("", T.FsNano, T.Mute);
            _pCount.VerticalAlignment = VerticalAlignment.Center;
            _pCount.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(_pCount, 2);
            inputRow.Children.Add(_pCount);

            _paletteInput = new TextBox();
            _paletteInput.FontSize = T.FsTitle;
            _paletteInput.FontFamily = T.FCn;
            _paletteInput.Foreground = T.S(T.Hi);
            _paletteInput.CaretBrush = T.S(T.Accent);
            _paletteInput.Background = Brushes.Transparent;
            _paletteInput.BorderThickness = new Thickness(0);
            _paletteInput.VerticalContentAlignment = VerticalAlignment.Center;
            _paletteInput.TextChanged += delegate { if (!_placeholderOn) RebuildResults(); };
            _paletteInput.PreviewMouseDown += delegate
            {
                if (!_placeholderOn) return;
                _placeholderOn = false;
                _paletteInput.Text = "";
                _paletteInput.Foreground = T.S(T.Hi);
            };
            _paletteInput.PreviewTextInput += delegate
            {
                if (!_placeholderOn) return;
                _placeholderOn = false;
                _paletteInput.Text = "";
                _paletteInput.Foreground = T.S(T.Hi);
            };
            _paletteInput.PreviewKeyDown += OnPaletteKey;
            _paletteInput.GotKeyboardFocus += delegate { searchIco.Stroke = T.S(T.Accent); };
            _paletteInput.LostKeyboardFocus += delegate { searchIco.Stroke = T.S(T.Dim); };
            inputWrap.Children.Add(_paletteInput);


            sp.Children.Add(inputRow);

            sp.Children.Add(Ui.Rule(T.LineSoft, 16));

            _paletteResults = new StackPanel();
            _paletteResults.Margin = new Thickness(8, 6, 8, 8);
            ScrollViewer sv = Ui.Scroll(_paletteResults);
            sv.MaxHeight = 326;
            sp.Children.Add(sv);

            _pEmpty = Ui.Tx("没有匹配的命令", T.FsSmall, T.Mute, T.FCn);
            _pEmpty.Margin = new Thickness(16, 6, 16, 14);
            _pEmpty.Visibility = Visibility.Collapsed;
            sp.Children.Add(_pEmpty);

            sp.Children.Add(Ui.Rule(T.LineSoft, 16));
            TextBlock legend = Ui.Mono("↑↓ 选择   Enter 执行   Esc 关闭   Ctrl+K 唤出", T.FsNano, T.Mute);
            legend.Margin = new Thickness(16, 8, 16, 12);
            sp.Children.Add(legend);

            Border topBevel = new Border();
            topBevel.Height = 2;
            topBevel.VerticalAlignment = VerticalAlignment.Top;
            topBevel.Margin = new Thickness(18, 0, 18, 0);
            topBevel.Background = T.AccentBevel();
            topBevel.IsHitTestVisible = false;
            Grid shellBox = new Grid();
            shellBox.Children.Add(sp);
            shellBox.Children.Add(topBevel);
            box.Child = shellBox;
            _palette.Children.Add(box);
            _palette.Visibility = Visibility.Collapsed;
        }

        private bool _paletteBuilt;
        private Border _paletteBox;
        private TextBlock _pCount;
        private ScaleTransform _paletteScale;
        private TranslateTransform _paletteTy;

        /// <summary>首次打开才建视觉树：冷启动不再为可能用不到的功能付费。</summary>
        private void EnsurePalette()
        {
            if (_paletteBuilt) return;
            _paletteBuilt = true;
            BuildPalette();
            _root.Children.Add(_palette);
        }

        public void ShowPalette()
        {
            EnsurePalette();
            RememberFocus();
            _pPendingConfirm = null;
            SetPlaceholder();
            // 面板盖住了标题栏：这时候标题栏不该再吃点击去拖窗口
            CaptionEnabled(false);
            if (_paletteBox != null) _paletteBox.BorderBrush = T.S(T.AccentA(0x52));
            _palette.Visibility = Visibility.Visible;
            Ui.Fade(_palette, 1, 120);
            if (Ui.Animate && !Rs.ReduceMotion && _paletteScale != null && _paletteTy != null)
            {
                CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                DoubleAnimation sx = new DoubleAnimation(0.97, 1.0, new Duration(TimeSpan.FromMilliseconds(150)));
                sx.EasingFunction = ease;
                DoubleAnimation ty = new DoubleAnimation(-10, 0, new Duration(TimeSpan.FromMilliseconds(150)));
                ty.EasingFunction = ease;
                _paletteScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
                _paletteScale.BeginAnimation(ScaleTransform.ScaleYProperty, sx);
                _paletteTy.BeginAnimation(TranslateTransform.YProperty, ty);
            }
            else if (_paletteScale != null && _paletteTy != null)
            {
                _paletteScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _paletteScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _paletteTy.BeginAnimation(TranslateTransform.YProperty, null);
                _paletteScale.ScaleX = 1; _paletteScale.ScaleY = 1; _paletteTy.Y = 0;
            }
            _pAll = CollectItems();   // 每次打开只探测一次，避免逐字敲击时反复读注册表
            _paletteInput.Focus();
            RebuildResults();
        }

        public void HidePalette()
        {
            if (_palette == null) return;
            _palette.Visibility = Visibility.Collapsed;
            _palette.Opacity = 1;
            CaptionEnabled(true);
            RestoreFocus();
        }

        /// <summary>标题栏的可拖动区域开关。面板 / 模态层出现时关掉它。</summary>
        private void CaptionEnabled(bool on)
        {
            try
            {
                WindowChrome wc = WindowChrome.GetWindowChrome(this);
                if (wc == null) return;
                wc.CaptionHeight = on ? T.TitleH : 0;
            }
            catch { /* 窗口还没建好时改不了标题栏高度：菜单仍可用，下次打开会重算 */ }
        }

        private void OnPaletteKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { HidePalette(); e.Handled = true; return; }
            if (e.Key == Key.Down) { MoveSel(1); e.Handled = true; return; }
            if (e.Key == Key.Up) { MoveSel(-1); e.Handled = true; return; }
            if (e.Key == Key.Enter) { ActivateSelected(); e.Handled = true; return; }
            // Tab 在面板里应该走结果列表，而不是把焦点甩到背后页面的第一个控件上
            if (e.Key == Key.Tab)
            {
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                MoveSel(shift ? -1 : 1);
                e.Handled = true;
                return;
            }
            // Rev130 修复：按 Ctrl+K 后直接敲退格/空格/输入键立即清除占位提示，无需先点鼠标
            if (_placeholderOn && (e.Key == Key.Back || e.Key == Key.Delete || e.Key == Key.Space))
            {
                _placeholderOn = false;
                _paletteInput.Text = "";
                _paletteInput.Foreground = T.S(T.Hi);
                if (e.Key == Key.Back || e.Key == Key.Delete) e.Handled = true;
                RebuildResults();
            }
        }

        private void MoveSel(int d)
        {
            if (_pResults.Count == 0) return;
            _pIndex = (_pIndex + d + _pResults.Count) % _pResults.Count;
            PaintSelection();
        }

        private void PaintSelection()
        {
            for (int i = 0; i < _paletteResults.Children.Count; i++)
            {
                Border b = _paletteResults.Children[i] as Border;
                if (b == null) continue;
                bool sel = i == _pIndex;
                b.Background = T.S(sel ? T.AccentWash : 0x00000000);
                b.BorderBrush = T.S(sel ? T.AccentGlow : 0x00000000);
                if (sel) b.BringIntoView();
            }
        }

        private void ActivateSelected()
        {
            if (_pResults.Count == 0 || _pIndex >= _pResults.Count) return;
            PaletteItem it = _pResults[_pIndex];
            if (it.Dangerous && _pPendingConfirm != it.Title)
            {
                _pPendingConfirm = it.Title;
                RebuildResults();
                return;
            }
            HidePalette();
            if (it.Run != null) it.Run();
        }

        private void RebuildResults()
        {
            _paletteResults.Children.Clear();
            _pResults.Clear();
            _pIndex = 0;

            string q = _placeholderOn || _paletteInput.Text == null ? "" : _paletteInput.Text.Trim();
            string ql = q.ToLowerInvariant();
            List<PaletteItem> items = _pAll;

            // 每条目每查询只评分一次；排序比较器只读预计算分数，不再重复 Score。
            List<PaletteItem> scored = new List<PaletteItem>();
            List<int> scores = new List<int>();
            foreach (PaletteItem it in items)
            {
                if (q.Length == 0) { scored.Add(it); scores.Add(0); continue; }
                int s = Score(it, ql);
                if (s > 0) { scored.Add(it); scores.Add(s); }
            }
            if (q.Length > 0)
            {
                for (int i = 0; i < scored.Count; i++)
                {
                    int best = i;
                    for (int j = i + 1; j < scored.Count; j++) if (scores[j] > scores[best]) best = j;
                    if (best != i)
                    {
                        PaletteItem ti = scored[i]; scored[i] = scored[best]; scored[best] = ti;
                        int ts = scores[i]; scores[i] = scores[best]; scores[best] = ts;
                    }
                }
            }

            int shown = 0;
            foreach (PaletteItem it in scored)
            {
                if (shown >= 12) break;
                _pResults.Add(it);
                _paletteResults.Children.Add(RowFor(it));
                shown++;
            }

            // 危险项二次确认时，重建列表必须把选中项落回同一个条目，
            // 否则第二次回车会执行到第 0 条（可能是另一个动作）。
            _pIndex = 0;
            if (_pPendingConfirm != null)
            {
                for (int i = 0; i < _pResults.Count; i++)
                {
                    if (_pResults[i].Title == _pPendingConfirm) { _pIndex = i; break; }
                }
            }

            _pEmpty.Visibility = _pResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_pCount != null) _pCount.Text = scored.Count + " \u9879";
            PaintSelection();
        }

        private static int Score(PaletteItem it, string ql)
        {
            string t = it.Lower;
            if (t == null)
            {
                t = (it.Title + " " + it.Hint + " " + it.Type).ToLowerInvariant();
                it.Lower = t;
            }
            if (t.StartsWith(ql)) return 100;
            if (t.IndexOf(ql) >= 0) return 50;
            // 模糊子序列匹配：连续命中加分，让 "tjdl" 这类缩写优先于散乱命中。
            int pos = 0, bonus = 0, prev = -2;
            for (int i = 0; i < ql.Length; i++)
            {
                int f = t.IndexOf(ql[i], pos);
                if (f < 0) return 0;
                if (f == prev + 1) bonus += 2;
                prev = f;
                pos = f + 1;
            }
            return 10 + bonus;
        }

        private Border RowFor(PaletteItem it)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(T.RadiusMd);
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = T.S(0x00000000);
            b.Background = T.S(0x00000000);
            b.Padding = new Thickness(10, 8, 10, 8);
            b.Margin = new Thickness(0, 1, 0, 1);
            b.Cursor = Cursors.Hand;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (!string.IsNullOrEmpty(it.Ico)) g.Children.Add(Ui.Icon(it.Ico, 14, T.Dim, 1.35));

            StackPanel mid = new StackPanel();
            Grid.SetColumn(mid, 1);
            TextBlock t = Ui.Tx(it.Title, T.FsBody, T.Hi, T.FCn);
            mid.Children.Add(t);
            if (!string.IsNullOrEmpty(it.Hint))
            {
                TextBlock h = Ui.Mono(it.Hint, T.FsNano, T.Mute);
                h.Margin = new Thickness(0, 3, 0, 0);
                h.TextTrimming = TextTrimming.CharacterEllipsis;
                mid.Children.Add(h);
            }
            g.Children.Add(mid);

            if (_pPendingConfirm == it.Title)
            {
                Border warn = Chip.Make("再次回车确认", T.Bad, T.ChipH);
                Grid.SetColumn(warn, 2);
                g.Children.Add(warn);
            }
            else
            {
                TextBlock ty = Ui.Label(it.Type, T.Mute);
                ty.FontSize = T.FsNano;
                Grid.SetColumn(ty, 2);
                g.Children.Add(ty);
            }

            b.Child = g;

            PaletteItem captured = it;
            b.MouseEnter += delegate { _pIndex = _pResults.IndexOf(captured); PaintSelection(); };
            Pressable.On(b, delegate
            {
                _pIndex = _pResults.IndexOf(captured);
                ActivateSelected();
            }, null, true);
            return b;
        }

        private List<PaletteItem> CollectItems()
        {
            List<PaletteItem> r = new List<PaletteItem>();

            foreach (Page p in _core.Pages)
            {
                PaletteItem it = new PaletteItem();
                it.Title = p.Title;
                it.Hint = p.En + "  ·  " + p.Idx;
                it.Ico = p.Ico;
                it.Type = "PAGE";
                string key = p.Key;
                it.Run = delegate { Go(key); };
                r.Add(it);
            }

            foreach (ActionDef a in _core.Engine.Actions)
            {
                PaletteItem it = new PaletteItem();
                bool applied = _core.Engine.Probe(a) == AState.Applied;
                it.Title = (applied ? "还原：" : "应用：") + a.Name;
                it.Hint = a.Group + "  ·  风险 " + a.Risk + (a.Restart ? "  ·  需重启" : "");
                it.Ico = Glyph.Tune;
                it.Type = "ACTION";
                ActionDef cap = a;
                bool wasApplied = applied;
                it.Run = delegate
                {
                    if (!_core.Engine.CanWrite(cap)) { Toast("需要管理员权限，请点左下角状态行提权重启", false); return; }
                    ApplyReport rep = wasApplied ? _core.Engine.RevertAction(cap.Id) : _core.Engine.Apply(cap);
                    if (wasApplied)
                    {
                        if (rep.Ok && rep.Applied == 0) Toast("该项并非通过本工具修改（账本无记录），未改动系统设置", false);
                        else Toast(rep.Ok ? cap.Name + " · 已还原" : "还原失败", rep.Ok);
                    }
                    else
                    {
                        Toast(rep.Ok ? cap.Name + " · 已应用" : "应用失败", rep.Ok);
                    }
                    RefreshAllPages();
                };
                r.Add(it);
            }

            PaletteItem rev = new PaletteItem();
            rev.Title = "全部还原账本";
            rev.Hint = _core.Engine.PendingCount + " 条记录待还原";
            rev.Ico = Glyph.Restore;
            rev.Type = "SAFE";
            rev.Dangerous = true;
            rev.Run = delegate
            {
                ApplyReport rep = _core.Engine.RevertAll();
                Toast(rep.Ok ? ("已还原 " + rep.Applied + " 项") : ("有 " + rep.Failed + " 项失败"), rep.Ok);
                Go("restore");
            };
            r.Add(rev);

            PaletteItem elev = new PaletteItem();
            elev.Title = _core.Elevated ? "已具备管理员权限" : "以管理员身份重启";
            elev.Hint = _core.Elevated ? "当前写入能力已解锁" : "解锁 HKLM / BCD / 电源策略写入";
            elev.Ico = Glyph.Shield;
            elev.Type = "SYSTEM";
            elev.Run = delegate { RelaunchElevated(); };
            r.Add(elev);

            PaletteItem bench = new PaletteItem();
            bench.Title = "打开基准体检并开始采样";
            bench.Hint = "8 秒唤醒延迟分布 · 前后对比";
            bench.Ico = Glyph.Benchmark;
            bench.Type = "GO";
            bench.Run = delegate { Go("bench"); };
            r.Add(bench);

            PaletteItem dir = new PaletteItem();
            dir.Title = "打开账本目录";
            dir.Hint = _core.Engine.Ledger.DirectoryPath;
            dir.Ico = Glyph.Log;
            dir.Type = "FILE";
            dir.Run = delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", _core.Engine.Ledger.DirectoryPath); }
                catch (Exception ex) { Diag.Fail("palette.openledger", ex); }
            };
            r.Add(dir);

            PaletteItem gm = new PaletteItem();
            gm.Title = _gameMode ? "退出游戏模式" : "一键游戏模式（开/关）";
            gm.Hint = "游戏模式+关DVR+关空闲维护+0.5ms · 逐项可逆";
            gm.Ico = Glyph.Bolt;
            gm.Type = "MODE";
            gm.Run = delegate { ToggleGameMode(); };
            r.Add(gm);

            for (int i = 0; i < T.Themes.Length; i++)
            {
                T.AccentTheme th = T.Themes[i];
                PaletteItem thm = new PaletteItem();
                thm.Title = "主题：" + th.Name;
                thm.Hint = th.Id == T.ThemeId ? (th.Id + " · 当前主题") : (th.Id + " · 切换强调色 · 立即生效");
                thm.Ico = Glyph.Theme;
                thm.Type = "THEME";
                string id = th.Id;
                thm.Run = delegate { ApplyThemeLive(id); };
                r.Add(thm);
            }

            AddShortcut(r, "图形与多显设置", "多显拓扑 / MPO / VRR / 着色器缓存", Glyph.Gpu, "GO", delegate { Go("graphics"); });
            AddShortcut(r, "网络双测与协议栈", "网卡参数 / 延迟路由双测 / TCP 调优", Glyph.Network, "GO", delegate { Go("network"); });
            AddShortcut(r, "进程优先级与 EcoQoS", "实时优先级 / CPU 亲和性 / 能效调节", Glyph.Processes, "GO", delegate { Go("processes"); });
            AddShortcut(r, "多源扫描已安装游戏", "Steam / WeGame / Epic / Xbox / 桌面快捷方式识别", Glyph.Profiles, "GO", delegate { Go("profiles"); });
            AddShortcut(r, "三模定时基准测试", "唤醒超调(8s) / 内存带宽延迟 / 线程调度乒乓 A/B 对照", Glyph.Benchmark, "GO", delegate { Go("bench"); });

            AddShortcut(r, "刷新系统 DNS 解析缓存", "原生 dnsapi.dll 即刻清理，零子进程", Glyph.Network, "ACT", delegate
            {
                bool ok = WinNative.FlushDnsCache();
                if (_core != null && _core.Engine != null) _core.Engine.Ledger.AppendAudit("NET flush-dns -> " + ok);
                Toast(ok ? "系统 DNS 解析缓存已刷新" : "刷新 DNS 缓存失败", ok);
            });
            AddShortcut(r, "清理待机内存列表（Standby）", "归还系统待机缓存页 · 需管理员", Glyph.Memory, "ACT", delegate
            {
                bool ok = Mem.PurgeStandby();
                if (_core != null && _core.Engine != null) _core.Engine.Ledger.AppendAudit("MEM purge-standby -> " + ok);
                Toast(ok ? "系统待机内存列表已清空" : "清理失败 · 需要管理员权限", ok);
            });

            PaletteItem exp = new PaletteItem();
            exp.Title = "账本导出（备份全部还原记录）";
            exp.Hint = "导出当前回滚账本 JSON 到本地数据目录";
            exp.Ico = Glyph.Download;
            exp.Type = "ACT";
            exp.Run = delegate { ExportLedgerFromPalette(); };
            r.Add(exp);

            return r;
        }

        private static void AddShortcut(List<PaletteItem> r, string title, string hint, string ico, string type, Action run)
        {
            PaletteItem it = new PaletteItem();
            it.Title = title;
            it.Hint = hint;
            it.Ico = ico;
            it.Type = type;
            it.Run = run;
            r.Add(it);
        }

        public void ExportLedgerSnapshot() { ExportLedgerFromPalette(); }

        private void ExportLedgerFromPalette()
        {
            try
            {
                Ledger ledger = (_core != null && _core.Engine != null) ? _core.Engine.Ledger : null;
                if (ledger == null) { Toast("账本尚未初始化", false); return; }
                string dir = ledger.DirectoryPath;
                if (string.IsNullOrEmpty(dir)) { Toast("账本目录不可用", false); return; }
                System.IO.Directory.CreateDirectory(dir);
                string fileName = "ledger-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
                string path = System.IO.Path.Combine(dir, fileName);

                System.Collections.Generic.IList<LedgerEntry> entries = ledger.Entries;
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                sb.Append("{\r\n  \"exportedAt\": \"").Append(DateTime.Now.ToString("o")).Append("\",\r\n");
                sb.Append("  \"rev\": ").Append(Brand.Rev).Append(",\r\n");
                sb.Append("  \"count\": ").Append(entries == null ? 0 : entries.Count).Append(",\r\n");
                sb.Append("  \"entries\": [");
                if (entries != null && entries.Count > 0)
                {
                    sb.Append("\r\n");
                    for (int i = 0; i < entries.Count; i++)
                    {
                        LedgerEntry e = entries[i];
                        sb.Append("    {\"id\":\"").Append(JsonEsc(e.Id))
                          .Append("\",\"time\":\"").Append(JsonEsc(e.Time))
                          .Append("\",\"actionId\":\"").Append(JsonEsc(e.ActionId))
                          .Append("\",\"actionName\":\"").Append(JsonEsc(e.ActionName))
                          .Append("\",\"kind\":\"").Append(JsonEsc(e.Kind))
                          .Append("\",\"oldValue\":\"").Append(JsonEsc(e.OldValue))
                          .Append("\",\"newValue\":\"").Append(JsonEsc(e.NewValue))
                          .Append("\"}");
                        if (i + 1 < entries.Count) sb.Append(",");
                        sb.Append("\r\n");
                    }
                    sb.Append("  ");
                }
                sb.Append("]\r\n}\r\n");
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                Toast("账本已导出 · " + fileName + " (" + (entries == null ? 0 : entries.Count) + " 条)", true);
            }
            catch (Exception ex)
            {
                Diag.Fail("palette.export_ledger", ex);
                Toast("账本导出失败 · 请检查权限", false);
            }
        }

        private static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
