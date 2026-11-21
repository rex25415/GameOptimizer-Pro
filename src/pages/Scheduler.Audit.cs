// ============================================================================
//  Scheduler.Audit.cs — 03 调度与定时器：内核调度参数审计（Scheduler 分片）
//
//  为什么切：六行注册表期望值 + 「当前值」列完全是数据驱动的，改表只该动这一处。
//
//  Rev86：原来每一行是"一颗灰点 + 四个灰列"，读者要逐列对齐才知道这一行算不算合格。
//  现在每行是一个 RowShell（左缘 2px 结论色）+ 一个结论徽标（符合 / 偏离 / 不存在 / 读不到）：
//  结论写在行首和行尾，中间才是论据。符合的行用半透明绿——六个"通过"不该抢走注意力；
//  偏离和读不到用实色，它们才是需要有人看一眼的那两行。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Go.Pages
{
    public partial class Scheduler : Page
    {
        // 结论码：不是"成没成功"，而是"这一行能下什么结论"。
        // 不存在（配置事实）与读不到（本次读取失败）必须分开，否则"拒绝访问"会被读成"值被改坏了"。
        private const int AuditOk = 0;
        private const int AuditDeviate = 1;
        private const int AuditMissing = 2;
        private const int AuditUnknown = 3;

        // 行网格：读数 110 / 期望 92 / 结论 56。与本页其它行、与 08 档案页同一套数（DESIGN-86 §3）。
        private const double ColVal = 110;
        private const double ColExp = 92;
        private const double ColTag = 56;

        private sealed class AuditRow
        {
            public string Path;
            public string ValueName;
            public string Expect;
            public string Note;
            public TextBlock Cur;
            public Badge Tag;
            public Rectangle Bar;
        }

        private FrameworkElement BuildAudit(Core c)
        {
            StackPanel body = new StackPanel();
            _auditMeta = Ui.Mono("—", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("内核调度参数审计", "KERNEL SCHEDULER AUDIT", _auditMeta));
            body.Children.Add(Gap(10));
            body.Children.Add(BuildAuditHead());

            _rows = new AuditRow[]
            {
                MakeRow(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "2", "前台量子加权"),
                MakeRow(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "20", "MMCSS 预留比例（<10 会被夹回 20）"),
                MakeRow(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", "2", "游戏任务线程优先级"),
                MakeRow(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "Medium", "调度档位"),
                MakeRow(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", "0", "全局定时器常开 = 功耗上升"),
                MakeRow(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache", "0", "文件缓存挤占工作集")
            };

            for (int i = 0; i < _rows.Length; i++)
                body.Children.Add(BuildAuditRow(_rows[i]));

            return Ui.Panel(body, T.PadPanel);
        }

        /// <summary>列头：与行内容共用同一套列宽，行外壳左侧的 16px（2 色条 + 14 内缩）必须先让出来。</summary>
        private static Grid BuildAuditHead()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(16, 0, 0, 4);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColVal) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColExp) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColTag) });

            g.Children.Add(Ui.Label("参数 / KEY", T.Mute));
            HeadCell(g, "当前值", 1);
            HeadCell(g, "期望值", 2);
            HeadCell(g, "结论", 3);
            return g;
        }

        private static void HeadCell(Grid g, string s, int col)
        {
            TextBlock t = Ui.Label(s, T.Mute);
            t.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }

        private static AuditRow MakeRow(string path, string vname, string expect, string note)
        {
            AuditRow r = new AuditRow();
            r.Path = path;
            r.ValueName = vname;
            r.Expect = expect;
            r.Note = note;
            return r;
        }

        private UIElement BuildAuditRow(AuditRow r)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColVal) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColExp) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColTag) });
            g.Background = Brushes.Transparent;

            // 列 1：参数名 + 一句人话（单行截断，页高不随窗口宽度跳）。
            StackPanel mid = new StackPanel();
            TextBlock nm = Ui.Mono(r.ValueName, T.FsMicro, T.Text);
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            nm.TextWrapping = TextWrapping.NoWrap;
            mid.Children.Add(nm);
            TextBlock note = Ui.Tx(r.Note, T.FsNano, T.Mute, T.FCn);
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            note.TextWrapping = TextWrapping.NoWrap;
            note.Margin = new Thickness(0, 2, 0, 0);
            mid.Children.Add(note);
            g.Children.Add(mid);

            // 列 2：当前值（右对齐等宽；颜色只由结论决定）。
            r.Cur = Ui.Mono("—", T.FsMicro, T.Mute);
            r.Cur.HorizontalAlignment = HorizontalAlignment.Right;
            r.Cur.VerticalAlignment = VerticalAlignment.Center;
            r.Cur.TextTrimming = TextTrimming.CharacterEllipsis;
            r.Cur.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(r.Cur, 1);
            g.Children.Add(r.Cur);

            // 列 3：期望值。它是参照物，不承担注意力。
            TextBlock exp = Ui.Mono(r.Expect, T.FsMicro, T.Mute);
            exp.HorizontalAlignment = HorizontalAlignment.Right;
            exp.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(exp, 2);
            g.Children.Add(exp);

            // 列 4：结论徽标（行尾）——与行首色条一起下同一个结论。
            r.Tag = new Badge("—", T.Mute);
            r.Tag.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(r.Tag, 3);
            g.Children.Add(r.Tag);

            Border shell = Ui.RowShell(g, T.Mute, 5);
            shell.BorderThickness = new Thickness(0, 0, 0, 1);
            shell.BorderBrush = T.S(T.LineSoft);
            shell.Background = Brushes.Transparent;
            r.Bar = BarOf(shell);

            shell.MouseEnter += delegate { shell.Background = T.S(T.LineFaint); };
            shell.MouseLeave += delegate { shell.Background = Brushes.Transparent; };
            Tip.Attach(shell, "HKLM\\" + r.Path + "\\" + r.ValueName + "\r\n期望值 " + r.Expect
                + " 来自 Windows 客户端默认口径；偏离不代表系统有问题，但值得知道是谁改的。");
            return shell;
        }

        /// <summary>
        /// Ui.RowShell 的结论色条是内层 Grid 的第一个子元素。
        /// 取它的引用，之后换结论只改 Fill；取不到就保留构建时的颜色——宁可色条晚一拍，
        /// 也不为了改一支画笔把整行拆了重建。
        /// </summary>
        private static Rectangle BarOf(Border shell)
        {
            Grid g = shell.Child as Grid;
            if (g == null || g.Children.Count == 0) return null;
            return g.Children[0] as Rectangle;
        }

        /// <summary>一行落色：色条、当前值、徽标三处说同一个结论。</summary>
        private void ApplyAuditRow(AuditRow r, int st, string cur)
        {
            uint tint = st == AuditOk ? T.Ok : (st == AuditUnknown ? T.Bad : T.Warn);
            string text = st == AuditOk ? "符合"
                        : st == AuditDeviate ? "偏离"
                        : st == AuditMissing ? "不存在" : "读不到";

            Set(r.Cur, cur);
            // 符合时"当前值"仍是一等读者（T.Text）；"期望值"那一列才是参照物（T.Mute）。
            r.Cur.Foreground = T.S(st == AuditOk ? T.Text : tint);
            // 六行全绿 = 六个同等权重的"注意这里"，等于没有优先级。
            // "符合"只在行首色条上留一道低亮度绿，徽标退回灰阶；偏离与读不到才是实色。
            if (r.Tag != null) r.Tag.Set(text, st == AuditOk ? T.Dim : tint);
            if (r.Bar != null) r.Bar.Fill = T.S(st == AuditOk ? Ui.Wash(T.Ok, 0x66) : tint);
        }
    }
}
