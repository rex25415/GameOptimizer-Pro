// ============================================================================
//  Memory.Actions.cs — 04 内存与提交：动作区（清理待机 / 剪裁 / 打开监视器）
//  为什么切：这一区是唯一写系统的地方（NtSetSystemInformation / EmptyWorkingSet），
//  每个动作都必须带账本记录与副作用说明；集中一处才审得动。
// ============================================================================
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Memory : Page
    {
        // ====================================================================
        //  动作
        // ====================================================================
        private UIElement BuildActions(Core c)
        {
            StackPanel body = new StackPanel();
            _actMeta = Src("未执行");
            body.Children.Add(Ui.PanelHead("动作", "ACTIONS", _actMeta));
            body.Children.Add(Gap(12));

            HoldBtn purge = new HoldBtn("长按清理", 900, T.AccentA(0x30));
            purge.Height = T.BtnHXs;
            purge.Confirmed += delegate { DoPurge(c); };

            _purgeChipHost = new Grid();
            _purgeChipHost.VerticalAlignment = VerticalAlignment.Center;

            body.Children.Add(ActionRow("清理待机列表",
                "把缓存页交还空闲页：只是把缓存交还，不会变快多少；热缓存下次要重读盘。",
                _purgeChipHost, purge,
                "待机列表里的页已经不属于任何进程，内容却还留在物理内存里。这个动作（NtSetSystemInformation 80/4）"
                + "把它们直接丢弃，物理内存立刻变空闲。副作用：被清掉的热缓存下次访问要重新从磁盘读，短时间内反而略慢。"));

            body.Children.Add(Gap(4));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(4));

            _selfWs = Ui.Mono("本进程 —", T.FsNano, T.Dim);
            _selfWs.VerticalAlignment = VerticalAlignment.Center;
            _selfWs.HorizontalAlignment = HorizontalAlignment.Right;
            _selfWs.TextTrimming = TextTrimming.CharacterEllipsis;
            _selfWs.TextWrapping = TextWrapping.NoWrap;

            Btn trim = new Btn("剪裁", BtnKind.Ghost);
            trim.Height = T.BtnHXs;
            trim.MinWidth = 78;
            trim.HorizontalAlignment = HorizontalAlignment.Right;
            trim.Click += delegate { DoTrim(c); };

            body.Children.Add(ActionRow("剪裁本进程工作集",
                "把本工具自己驻留的页交还系统：只影响本进程，剪裁前会跑一次完整 GC。",
                _selfWs, trim,
                "EmptyWorkingSet(GetCurrentProcess)：本工具自己的页被换出到页面文件，物理内存立刻少占几十 MB。"
                + "只作用于这个进程，不碰任何其它程序。剪裁前先做一次完整 GC，界面可能停一拍。"));

            body.Children.Add(Gap(4));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(4));

            TextBlock tag = Ui.Mono("资源监视器", T.FsNano, T.Dim);
            tag.VerticalAlignment = VerticalAlignment.Center;
            tag.HorizontalAlignment = HorizontalAlignment.Right;

            Btn open = new Btn("打开", BtnKind.Ghost);
            open.Height = T.BtnHXs;
            open.MinWidth = 78;
            open.HorizontalAlignment = HorizontalAlignment.Right;
            open.Click += delegate { DoOpenPanel(c); };

            body.Children.Add(ActionRow("打开系统内存面板",
                "打开 Windows 资源监视器的内存页签：那里的物理内存分解与这里同源。",
                tag, open,
                "启动 resmon.exe（资源监视器）。它读的是同一套内核计数器，可以用来交叉核对本页的读数。"));

            return Ui.Panel(body, T.PadPanel);
        }

        /// <summary>一行动作：标题 + 一行说明 + 右侧按钮；副作用写进悬停提示，不写成魔法。</summary>
        private UIElement ActionRow(string title, string note, UIElement state, UIElement button, string tip)
        {
            Grid g = new Grid();
            g.Background = Brushes.Transparent;
            g.MinHeight = 50;   // 与列表行同一节奏：动作行也是行，读者不该在这里重新学一种行高
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });

            TextBlock t = Ui.Tx(title, T.FsSmall, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.TextWrapping = TextWrapping.NoWrap;
            g.Children.Add(t);

            if (state != null)
            {
                FrameworkElement se = state as FrameworkElement;
                if (se != null)
                {
                    se.VerticalAlignment = VerticalAlignment.Center;
                    se.Margin = new Thickness(10, 0, 10, 0);
                }
                Grid.SetColumn(state, 1);
                g.Children.Add(state);
            }

            if (button != null)
            {
                FrameworkElement be = button as FrameworkElement;
                if (be != null)
                {
                    be.VerticalAlignment = VerticalAlignment.Center;
                    be.Margin = new Thickness(8, 0, 0, 0);
                }
                Grid.SetColumn(button, 2);
                g.Children.Add(button);
            }

            TextBlock n = Ui.Tx(note, T.FsMicro, T.Mute, T.FCn);
            n.TextTrimming = TextTrimming.CharacterEllipsis;
            n.TextWrapping = TextWrapping.NoWrap;
            n.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(n, 1);
            Grid.SetColumnSpan(n, 3);
            g.Children.Add(n);

            Tip.Attach(g, tip);
            return g;
        }

        private void DoPurge(Core c)
        {
            if (c == null) return;
            if (!c.Elevated)
            {
                Set(_actMeta, "未执行 · 需要管理员");
                c.Shell.Toast("清理待机列表需要管理员权限：点右上角「提权重启」后再试", false);
                c.Engine.Ledger.AppendAudit("MEM purge-standby -> denied (not elevated)");
                return;
            }
            double standbyBefore = (c.Perf != null && c.Perf.Current != null) ? c.Perf.Current.MemStandbyMB : 0;
            bool ok = Mem.PurgeStandby();
            c.Engine.Ledger.AppendAudit("MEM purge-standby -> " + ok);
            Set(_actMeta, ok ? "已请求清空待机列表" : "被拒绝（需要管理员）");
            c.Shell.Toast(ok ? "已请求清空待机列表" : "操作被拒绝（需管理员）", ok);

            // 系统重建待机列表需要一点时间：600ms 后一次性重读并回显实际释放量，不用定时器轮询。
            if (ok)
            {
                if (_purgeTimer != null) _purgeTimer.Stop();
                _purgeTimer = new DispatcherTimer();
                _purgeTimer.Interval = TimeSpan.FromMilliseconds(600);
                _purgeTimer.Tick += delegate
                {
                    _purgeTimer.Stop();
                    if (C != null && C.Perf != null && C.Perf.Current != null && standbyBefore > 0)
                    {
                        double freed = standbyBefore - C.Perf.Current.MemStandbyMB;
                        if (freed >= 64)
                            Set(_actMeta, "已释放待机页 " + (freed >= 1024 ? ((freed / 1024.0).ToString("0.1") + " GB") : (freed.ToString("0") + " MB")));
                    }
                    if (C != null) Apply(C.Perf.Current);
                    LoadTop(false);
                };
                _purgeTimer.Start();
            }
        }

        private void DoTrim(Core c)
        {
            long before = SelfWs();
            try { Native.TrimWorkingSet(); }
            catch (Exception ex) { Diag.Fail("mem.trim", ex); }
            long after = SelfWs();

            string s = (before > 0 ? Group(before / 1048576.0) : "—") + " → " + (after > 0 ? Group(after / 1048576.0) : "—") + " MB";
            Set(_selfWs, "本进程 " + (after > 0 ? Group(after / 1048576.0) + " MB" : "—"));
            Set(_actMeta, "已剪裁 " + s);
            if (c != null)
            {
                c.Engine.Ledger.AppendAudit("MEM trim-workset -> " + before + " -> " + after);
                c.Shell.Toast("本进程工作集 " + s, true);
            }
        }

        private static long SelfWs()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                {
                    return p.WorkingSet64;
                }
            }
            catch { return -1; }
        }

        private void DoOpenPanel(Core c)
        {
            bool ok = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("resmon.exe");
                psi.UseShellExecute = true;
                Process.Start(psi);
                ok = true;
            }
            catch
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskmgr.exe");
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    ok = true;
                }
                catch { ok = false; }
            }
            Set(_actMeta, ok ? "已打开资源监视器" : "无法启动（系统组件缺失）");
            if (c != null)
            {
                c.Shell.Toast(ok ? "已打开资源监视器 · 内存页签" : "无法启动资源监视器", ok);
                c.Engine.Ledger.AppendAudit("MEM open-resmon -> " + ok);
            }
        }

        // ---- 待机可回收量胶囊（只在数值或提权状态变化时重建）----
        private void SetPurgeChip(Core c, double standbyMb)
        {
            if (_purgeChipHost == null) return;
            bool elevated = c != null ? c.Elevated : false;
            // 待机量每秒都在变：按数值当 key 会让这个胶囊每秒销毁重建一次
            // （新 Border + 新 ToolTip 模板 + 三组委托）。只在"提权状态"变化时重建，
            // 其余时候只改文本 —— 这是这一页唯一一处每秒都会走到的地方。
            string key = (elevated ? "e" : "n") + (standbyMb >= 0 ? "1" : "0");
            if (key != _purgeChipKey)
            {
                _purgeChipKey = key;
                _purgeChipHost.Children.Clear();
                string initTxt = elevated
                    ? (standbyMb >= 0 ? "可回收 " + Gb(standbyMb) + " GB" : "可回收 —")
                    : (standbyMb >= 0 ? "待机 " + Gb(standbyMb) + " GB · 需管理员" : "需管理员");
                Border chip = Chip.Make(initTxt, elevated ? T.Accent : T.Warn, T.ChipHSm);
                chip.HorizontalAlignment = HorizontalAlignment.Right;
                _purgeChipHost.Children.Add(chip);
                _purgeChipText = chip.Child as TextBlock;
                _purgeChipTenth = int.MinValue;      // 新胶囊：下一次一定重写首行文本
                Tip.Attach(chip, elevated
                    ? "待机列表当前占用的物理内存。清理后这部分页变成空闲页；被清掉的缓存下次访问要重新读盘。"
                    : "清理待机列表需要管理员权限（NtSetSystemInformation）。当前为只读模式，可在左下角提权重启后操作。");
                return;
            }
            if (_purgeChipText != null && standbyMb >= 0)
            {
                // 文本按 0.1 GB 刻度比较：待机量一秒里常常只有 ±0.02 GB，
                // 那点变化不值得一次字符串分配 + 一次文本重排。
                int tenth = (int)Math.Round(standbyMb / 102.4);
                if (tenth == _purgeChipTenth) return;
                _purgeChipTenth = tenth;
                _purgeChipText.Text = elevated
                    ? ("可回收 " + Gb(standbyMb) + " GB")
                    : ("待机 " + Gb(standbyMb) + " GB · 需管理员");
            }
        }

    }
}
