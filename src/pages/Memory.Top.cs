// ============================================================================
//  Memory.Top.cs — 04 内存与提交：吃内存前几名（行网格 + 后台扫描）（Memory 分片）
//  为什么切：这里是全页唯一有后台线程与扫描节流的地方（5 拍一次、BelowNormal）；
//  和 UI 装配混在一起时，没法一眼确认"500ms 热路径上没有全表扫描"。
//
//  Rev86：从"排名 + 名字 + 一根条 + MB"改成 DESIGN-86 §3 的行网格——
//  进程名 / 工作集(110) / 私有(92) / 操作(56)。原来只有工作集一列、没有量纲对照，
//  读者无法回答"这个进程是占得多还是本来就这么大"；现在工作集与私有并排，
//  差值就是共享部分。列宽与图形页适配器表共用同一组数，跨页右缘对齐。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Memory : Page
    {
        private static readonly int SelfPid = Process.GetCurrentProcess().Id;

        // 行网格列宽：预留 EcoQoS 快捷压制按钮 + 复制操作列，保证与表头右缘齐平。
        private const double ColWsW = 88;
        private const double ColPrivW = 80;
        private const double ColActW = 148;

        // 单行行高 42：行内上下各 6px，内容 30。
        private const double RowH = 30;

        // ====================================================================
        //  面板
        // ====================================================================
        private UIElement BuildTop(Core c)
        {
            StackPanel body = new StackPanel();
            _topMeta = Src("PROCESS");
            body.Children.Add(Ui.PanelHead("吃内存前几名", "TOP WORKING SET / PRIVATE / ECOQOS", _topMeta));
            body.Children.Add(Gap(12));

            body.Children.Add(BuildHeadRow());
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(0));

            StackPanel list = new StackPanel();
            for (int i = 0; i < ProcRows; i++)
            {
                _rows[i] = MakeRow(i, c);
                list.Children.Add(_rows[i].Root);
            }
            body.Children.Add(list);

            UIElement empty = Ui.Empty(Glyph.Warn, "没有可读的进程占用",
                "进程枚举被拒绝，或当前会话看不到任何进程。这张表每 5 秒自动重试一次。",
                T.Mute, null);
            _topEmpty = empty as FrameworkElement;
            if (_topEmpty != null) _topEmpty.Visibility = Visibility.Collapsed;
            body.Children.Add(empty);

            return Ui.Panel(body, T.PadPanel);
        }

        /// <summary>列头：与数据行共用同一个列模板，读者靠 x 坐标对齐，不靠猜。</summary>
        private Grid BuildHeadRow()
        {
            Grid g = NewRowGrid();
            g.Height = 22;

            TextBlock name = Ui.Label("进程", T.Mute);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(22, 0, 0, 0);   // 与行内进程名同左缘（排名列 22 宽）
            g.Children.Add(name);

            TextBlock ws = Ui.Label("工作集", T.Mute);
            ws.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(ws, 1);
            g.Children.Add(ws);

            TextBlock priv = Ui.Label("私有", T.Mute);
            priv.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(priv, 2);
            g.Children.Add(priv);

            TextBlock act = Ui.Label("操作", T.Mute);
            act.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(act, 3);
            g.Children.Add(act);
            return g;
        }

        private static Grid NewRowGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColWsW) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColPrivW) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColActW) });
            return g;
        }

        private ProcRow MakeRow(int i, Core c)
        {
            ProcRow r = new ProcRow();

            Grid row = NewRowGrid();
            row.Height = RowH;

            Grid title = new Grid();
            title.Margin = new Thickness(0, 0, 8, 0);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            title.ColumnDefinitions.Add(new ColumnDefinition());
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            r.Rank = Ui.Mono((i + 1).ToString("00"), T.FsNano, T.Mute);
            r.Rank.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(r.Rank);

            r.Name = Ui.Tx("\u2014", T.FsSmall, T.Text, T.FCn);
            r.Name.TextTrimming = TextTrimming.CharacterEllipsis;
            r.Name.TextWrapping = TextWrapping.NoWrap;
            r.Name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(r.Name, 1);
            title.Children.Add(r.Name);

            r.EcoBadge = Chip.Make("常规", T.Dim, 18);
            r.EcoBadge.Margin = new Thickness(6, 0, 0, 0);
            r.EcoBadge.VerticalAlignment = VerticalAlignment.Center;
            r.EcoBadgeText = r.EcoBadge.Child as TextBlock;
            Grid.SetColumn(r.EcoBadge, 2);
            title.Children.Add(r.EcoBadge);
            row.Children.Add(title);

            r.Ws = Ui.Mono("\u2014", T.FsSmall, T.Text);
            r.Ws.HorizontalAlignment = HorizontalAlignment.Right;
            r.Ws.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(r.Ws, 1);
            row.Children.Add(r.Ws);

            r.Priv = Ui.Mono("\u2014", T.FsSmall, T.Dim);
            r.Priv.HorizontalAlignment = HorizontalAlignment.Right;
            r.Priv.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(r.Priv, 2);
            row.Children.Add(r.Priv);

            StackPanel acts = new StackPanel();
            acts.Orientation = Orientation.Horizontal;
            acts.HorizontalAlignment = HorizontalAlignment.Right;
            acts.VerticalAlignment = VerticalAlignment.Center;

            ProcRow self = r;
            Core core = c;

            r.EcoBtn = new Btn("能效压制", BtnKind.Ghost);
            r.EcoBtn.Compact = true;
            r.EcoBtn.Height = T.BtnHSm;
            r.EcoBtn.MinWidth = 84;
            r.EcoBtn.Margin = new Thickness(0, 0, 6, 0);
            r.EcoBtn.Click += delegate { ToggleRowEco(core, self); };
            Tip.Attach(r.EcoBtn, "切换该高占用非系统后台进程的 Win11 EcoQoS 能效档位（调用 ProcessPower.SetEcoQoS 并回读刷新状态）。");
            acts.Children.Add(r.EcoBtn);

            Btn copy = new Btn("复制", BtnKind.Quiet);
            copy.Compact = true;
            copy.Height = T.BtnHSm;
            copy.Width = 48;
            copy.Click += delegate { CopyRow(core, self.Mem); };
            Tip.Attach(copy, "把这一行的「进程名 · 工作集 · 私有」复制到剪贴板。报内存问题时直接贴给对方。");
            acts.Children.Add(copy);

            Grid.SetColumn(acts, 3);
            row.Children.Add(acts);

            Border shell = Ui.RowShell(row, T.LineMid, 6);
            shell.Background = Brushes.Transparent;
            shell.MouseEnter += delegate { shell.Background = T.S(T.LineFaint); };
            shell.MouseLeave += delegate { shell.Background = Brushes.Transparent; };
            r.Root = shell;
            r.Root.Visibility = Visibility.Collapsed;
            return r;
        }

        private void ToggleRowEco(Core c, ProcRow r)
        {
            if (r == null || r.Mem == null) return;
            ProcMem m = r.Mem;
            if (m.Pid <= 4 || m.Pid == SelfPid || m.IsProtected)
            {
                if (c != null && c.Shell != null) c.Shell.Toast("系统关键进程或本工具自身不允许能效压制", false);
                return;
            }
            bool want = !m.EcoOn;
            string err;
            bool ok = ProcessPower.SetEcoQoSIfMatch(m.Pid, m.Born, want, false, out err);
            bool actual;
            bool readOk = ProcessPower.ReadEcoQoS(m.Pid, out actual);
            m.EcoKnown = readOk;
            if (readOk) m.EcoOn = actual;
            else if (ok) m.EcoOn = want;

            ApplyRowEcoUi(r);
            if (c != null && c.Engine != null && c.Engine.Ledger != null)
                c.Engine.Ledger.AppendAudit("MEM ECOQOS " + m.Name + " pid=" + m.Pid + " want=" + (want ? "ON" : "OFF") + " ok=" + ok + (ok ? "" : " err=" + err));
            if (c != null && c.Shell != null)
            {
                c.Shell.Toast(ok
                    ? ("已" + (want ? "开启" : "关闭") + " " + m.Name + " 能效压制 (EcoQoS)")
                    : ("设置失败（" + m.Name + "）：" + (string.IsNullOrEmpty(err) ? "访问被拒绝或系统不支持" : err)),
                    ok);
            }
        }

        private static void ApplyRowEcoUi(ProcRow r)
        {
            if (r == null || r.Mem == null) return;
            ProcMem m = r.Mem;
            if (r.EcoBadge != null && r.EcoBadgeText != null)
            {
                if (m.IsProtected)
                {
                    r.EcoBadgeText.Text = "系统";
                    Chip.Paint(r.EcoBadge, T.Mute, false);
                }
                else if (!m.EcoKnown)
                {
                    r.EcoBadgeText.Text = "未读";
                    Chip.Paint(r.EcoBadge, T.Mute, false);
                }
                else if (m.EcoOn)
                {
                    r.EcoBadgeText.Text = "EcoQoS";
                    Chip.Paint(r.EcoBadge, T.Ok, true);
                }
                else
                {
                    r.EcoBadgeText.Text = "常规";
                    Chip.Paint(r.EcoBadge, T.Dim, false);
                }
            }
            if (r.EcoBtn != null)
            {
                if (m.IsProtected)
                {
                    r.EcoBtn.Label = "系统保护";
                    r.EcoBtn.Kind = BtnKind.Ghost;
                    r.EcoBtn.Enabled = false;
                }
                else if (!m.EcoKnown)
                {
                    r.EcoBtn.Label = "权限受限";
                    r.EcoBtn.Kind = BtnKind.Ghost;
                    r.EcoBtn.Enabled = false;
                }
                else if (m.EcoOn)
                {
                    r.EcoBtn.Label = "解除压制";
                    r.EcoBtn.Kind = BtnKind.Accent;
                    r.EcoBtn.Enabled = true;
                }
                else
                {
                    r.EcoBtn.Label = "能效压制";
                    r.EcoBtn.Kind = BtnKind.Ghost;
                    r.EcoBtn.Enabled = true;
                }
            }
        }

        private void CopyRow(Core c, ProcMem m)
        {
            if (m == null) return;
            string text = m.Name + " · 工作集 " + Group(m.Ws / 1048576.0) + " MB · 私有 " +
                          Group(m.Priv / 1048576.0) + " MB";
            bool ok = false;
            try { Clipboard.SetText(text); ok = true; }
            catch (Exception ex)
            {
                // 剪贴板被其它进程占用时 SetText 会抛 COM 异常：可预期失败，记账但不改变页面状态。
                Diag.Fail("mem.top.copy", ex);
            }
            if (c != null) c.Shell.Toast(ok ? "已复制 " + m.Name + " 的占用" : "复制失败 · 剪贴板被占用", ok);
        }

        // ====================================================================
        //  扫描：5 秒一次，后台线程
        // ====================================================================
        private void LoadTop(bool sync)
        {
            if (_rows[0] == null) return;
            if (sync) { ApplyTop(Collect()); return; }
            if (_scanning) return;
            _scanning = true;
            int gen = _scanGen;
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                ScanResult res = Collect();
                ui.BeginInvoke(new Action(delegate ()
                {
                    if (gen != _scanGen) return;
                    _scanning = false;
                    ApplyTop(res);
                }));
            });
            t.IsBackground = true;
            t.Priority = System.Threading.ThreadPriority.BelowNormal;
            t.Name = "go.mem.top";
            t.Start();
        }

        /// <summary>
        /// 一次取数：工作集排名来自共享进程表（一次 NtQuery 遍历），
        /// 只有最终显示的 5 行才打开句柄读「私有」——旧实现每 5 秒
        /// Process.GetProcesses() 全量枚举一次（430 个对象、1300 个句柄），
        /// 其中 99% 的行只是为了排序，排完就丢。
        /// </summary>
        private ScanResult Collect()
        {
            ScanResult res = new ScanResult();
            res.SelfWs = SelfWorkingSet();

            ProcSnapshot snap = C == null ? null : C.Procs.Get(5000);
            if (snap == null || !snap.Ok) return res;

            res.Total = snap.Count;
            List<ProcMem> rows = new List<ProcMem>(snap.Count);
            for (int i = 0; i < snap.Count; i++)
            {
                ProcEntry e = snap.Items[i];
                if (e == null || e.WorkingSetBytes <= 0) continue;
                ProcMem m = new ProcMem();
                m.Pid = e.Pid;
                m.Born = e.Born;
                m.Name = e.Name;
                m.Image = e.Image;
                m.Ws = e.WorkingSetBytes;
                rows.Add(m);
            }
            rows.Sort(delegate (ProcMem a, ProcMem b) { return b.Ws.CompareTo(a.Ws); });

            int take = Math.Min(ProcRows, rows.Count);
            if (rows.Count > take) rows.RemoveRange(take, rows.Count - take);
            for (int i = 0; i < rows.Count; i++)
            {
                ProcMem m = rows[i];
                m.Priv = PrivateOf(m.Pid);
                m.IsProtected = IsProtectedSystemProc(m.Pid, m.Name, m.Image);
                if (!m.IsProtected)
                {
                    bool eco;
                    m.EcoKnown = ProcessPower.ReadEcoQoS(m.Pid, out eco);
                    m.EcoOn = m.EcoKnown && eco;
                }
            }
            res.Rows.AddRange(rows);
            return res;
        }

        private static bool IsProtectedSystemProc(int pid, string name, string image)
        {
            if (pid <= 4 || pid == SelfPid) return true;
            return ProcessPower.IsProtectedBackgroundName(name) || ProcessPower.IsProtectedBackgroundName(image);
        }

        /// <summary>私有提交（PrivateUsage）。改走原生 K32GetProcessMemoryInfo（~15 µs），消除 10 次全表扫描。</summary>
        private static long PrivateOf(int pid)
        {
            long v = ProcName.ReadPrivateBytes(pid);
            return v > 0 ? v : 0;
        }

        private static long SelfWorkingSet()
        {
            try
            {
                using (Process self = Process.GetCurrentProcess()) return self.WorkingSet64;
            }
            catch { return -1; }
        }

        private void ApplyTop(ScanResult res)
        {
            if (res == null || _rows[0] == null) return;

            int n = res.Rows.Count;
            long max = n > 0 ? res.Rows[0].Ws : 0;

            for (int i = 0; i < ProcRows; i++)
            {
                ProcRow r = _rows[i];
                if (r == null) continue;
                if (i >= n)
                {
                    if (r.Root.Visibility != Visibility.Collapsed) r.Root.Visibility = Visibility.Collapsed;
                    continue;
                }
                ProcMem m = res.Rows[i];
                r.Mem = m;
                Set(r.Name, m.Name);
                Set(r.Ws, Group(m.Ws / 1048576.0) + " MB");
                // 私有读不到时给 —，不拿 0 MB 冒充"这个进程不占私有内存"。
                Set(r.Priv, m.Priv > 0 ? Group(m.Priv / 1048576.0) + " MB" : "\u2014");
                ApplyRowEcoUi(r);
                if (r.Root.Visibility != Visibility.Visible) r.Root.Visibility = Visibility.Visible;
            }

            if (_topEmpty != null)
                _topEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_topMeta != null)
            {
                string s = "PROCESS · " + res.Total + " 个进程";
                if (max > 0) s += " · 满格 " + Group(max / 1048576.0) + " MB";
                Set(_topMeta, s);
            }

            if (_selfWs != null)
                Set(_selfWs, res.SelfWs > 0 ? "本进程 " + Group(res.SelfWs / 1048576.0) + " MB" : "本进程 —");
        }
    }
}
