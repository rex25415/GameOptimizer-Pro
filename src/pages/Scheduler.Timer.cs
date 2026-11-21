// ============================================================================
//  Scheduler.Timer.cs — 03 调度与定时器：定时器租约与前台量子（Scheduler 分片）
//  为什么切：一个 Switch 租约 + 两个量子按钮，背后是 TimerService 的引用计数与
//  Win32PrioritySeparation 的位域语义；两块合成一块板，代码也放一处。
//
//  Rev86：这一块从"横跨整幅的两栏"改成下排左栏（420）的上下两段。
//  横跨整幅时它抢走了矩阵的宽度，却只放了三行字；现在它挨着审计表，
//  两块一起回答"为什么是这个读数"。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Scheduler : Page
    {
        // ====================================================================
        //  定时器与量子（同一面板的上下两段，中间一条发丝线）
        // ====================================================================
        private FrameworkElement BuildTimerQuantum(Core c)
        {
            StackPanel body = new StackPanel();
            _leaseMeta = Ui.Mono("租约 —", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("定时器与量子", "TIMER LEASE / QUANTUM", _leaseMeta));
            body.Children.Add(Gap(7));
            body.Children.Add(BuildLeaseBlock(c));
            body.Children.Add(Rule());
            body.Children.Add(BuildQuantumBlock(c));
            return Ui.Panel(body, 16);
        }

        private UIElement BuildLeaseBlock(Core c)
        {
            StackPanel col = new StackPanel();

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel tl = new StackPanel();
            tl.Children.Add(Ui.Tx("本进程 0.5 ms 精度", T.FsBody, T.Hi, T.FCn));
            _tRange = Ui.Mono("—", T.FsNano, T.Mute);
            _tRange.Margin = new Thickness(0, 4, 0, 0);
            tl.Children.Add(_tRange);
            row.Children.Add(tl);

            _swTimer = new Switch(false);
            _swTimer.VerticalAlignment = VerticalAlignment.Center;
            _swTimer.Changed += delegate
            {
                // 租约归 TimerService 统一管理：这里只表达"我要 / 我不要"，
                // 真正的 NtSetTimerResolution 下发与引用计数由服务层完成。
                c.Timer.Set(TimerOwner.Scheduler, _swTimer.On, 0.5);
                bool on = _swTimer.On;
                bool active = c.Timer.Active;
                if (on && !active)
                {
                    _swTimer.On = false;
                    c.Shell.Toast("定时器精度请求失败（系统拒绝或服务不可用）", false);
                }
                else
                {
                    c.Shell.Toast(on
                        ? "定时器精度已提升至 " + c.Timer.GrantedMs.ToString("0.0") + " ms"
                        : (active ? "本页已放弃，其它模块仍持有精度请求" : "已恢复系统默认定时器精度"),
                        true);
                }
                RefreshFast();      // 立即读回实测值，不等下一拍
            };
            Tip.Attach(_swTimer, "向 TimerService 申请 0.5ms 定时器租约（引用计数）。\r\n只影响本进程的等待粒度；全局高精度会让整机功耗与温度上升。");
            Grid.SetColumn(_swTimer, 1);
            row.Children.Add(_swTimer);
            col.Children.Add(row);

            _tJitter = Ui.Mono("唤醒抖动 —", T.FsNano, T.Dim);
            _tJitter.Margin = new Thickness(0, 4, 0, 0);
            col.Children.Add(_tJitter);

            TextBlock note = Ui.Tx("只影响本进程等待粒度；全局 0.5 ms 会升高整机功耗与温度。",
                                   T.FsMicro, T.Mute, T.FCn);
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            note.Margin = new Thickness(0, 5, 0, 0);
            col.Children.Add(note);
            return col;
        }

        private UIElement BuildQuantumBlock(Core c)
        {
            StackPanel col = new StackPanel();

            StackPanel numRow = new StackPanel();
            numRow.Orientation = Orientation.Horizontal;

            Run prioRun;
            TextBlock pv = Number("—", "", T.FsNumL, T.Hi, out prioRun);
            _prioRun = prioRun;
            numRow.Children.Add(pv);

            _prioHex = Ui.Mono("—", T.FsMicro, T.Mute);
            _prioHex.VerticalAlignment = VerticalAlignment.Bottom;
            _prioHex.Margin = new Thickness(10, 0, 0, 7);
            numRow.Children.Add(_prioHex);
            col.Children.Add(numRow);

            _prioNote = Ui.Mono("—", T.FsNano, T.Dim);
            _prioNote.TextTrimming = TextTrimming.CharacterEllipsis;
            _prioNote.TextWrapping = TextWrapping.NoWrap;
            _prioNote.Margin = new Thickness(0, 4, 0, 0);
            col.Children.Add(_prioNote);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.Margin = new Thickness(0, 7, 0, 0);

            ActionDef def = c.Engine.Find("fix-prio");
            ActionDef boost = c.Engine.Find("prio-boost");

            Btn b1 = new Btn("恢复默认 (2)", BtnKind.Ghost);
            b1.Height = T.BtnH;
            b1.Compact = true;
            b1.Margin = new Thickness(0, 0, 8, 0);
            b1.Click += delegate
            {
                if (def == null) return;
                ApplyReport r = c.Engine.Apply(def);
                string why = !string.IsNullOrEmpty(r.Reason) ? r.Reason : (r.Lines.Count > 0 ? r.Lines[0] : "");
                c.Shell.Toast(r.Ok ? "已恢复默认量子策略" : (string.IsNullOrEmpty(why) ? "写入失败" : ("写入失败：" + why)), r.Ok);
                RefreshSlow();      // 动作后立即重读，不等 5s 慢拍
            };
            Tip.Attach(b1, "Win32PrioritySeparation = 2：可变长度量子 + 前台 3:1 加权。这是 Windows 客户端默认值。");
            btns.Children.Add(b1);

            Btn b2 = new Btn("短量子 (0x26)", BtnKind.Quiet);
            b2.Height = T.BtnH;
            b2.Compact = true;
            b2.Click += delegate
            {
                if (boost == null) return;
                ApplyReport r = c.Engine.Apply(boost);
                string why = !string.IsNullOrEmpty(r.Reason) ? r.Reason : (r.Lines.Count > 0 ? r.Lines[0] : "");
                c.Shell.Toast(r.Ok ? "已切换为短量子 + 3:1 加权" : (string.IsNullOrEmpty(why) ? "写入失败" : ("写入失败：" + why)), r.Ok);
                RefreshSlow();
            };
            Tip.Attach(b2, "Win32PrioritySeparation = 38（0x26）：短可变量子 + 前台 3 倍加成。比默认更激进，CPU 占用高时可能略增切换开销。");
            btns.Children.Add(b2);
            col.Children.Add(btns);

            TextBlock note = Ui.Tx("0x28(40) 会取消前台加权并固定时间片，属常见脚本负优化项。",
                                   T.FsMicro, T.Mute, T.FCn);
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            note.Margin = new Thickness(0, 6, 0, 0);
            col.Children.Add(note);

            col.Children.Add(Rule());
            col.Children.Add(BuildTargetSchedBlock(c));
            return col;
        }

        private UIElement BuildTargetSchedBlock(Core c)
        {
            StackPanel box = new StackPanel();

            Grid hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock title = Ui.Tx("前台目标直调", T.FsBody, T.Hi, T.FCn);
            title.FontWeight = FontWeights.SemiBold;
            hdr.Children.Add(title);

            _procSchedTarget = Ui.Mono("未锁外部进程", T.FsNano, T.Mute);
            _procSchedTarget.HorizontalAlignment = HorizontalAlignment.Right;
            _procSchedTarget.VerticalAlignment = VerticalAlignment.Center;
            _procSchedTarget.TextTrimming = TextTrimming.CharacterEllipsis;
            _procSchedTarget.MaxWidth = 220;
            Grid.SetColumn(_procSchedTarget, 1);
            hdr.Children.Add(_procSchedTarget);
            box.Children.Add(hdr);

            Grid prioRow = new Grid();
            prioRow.Margin = new Thickness(0, 7, 0, 0);
            prioRow.ColumnDefinitions.Add(new ColumnDefinition());
            prioRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel pLeft = new StackPanel();
            pLeft.Orientation = Orientation.Horizontal;
            pLeft.VerticalAlignment = VerticalAlignment.Center;
            TextBlock pLbl = Ui.Tx("优先级", T.FsSmall, T.Dim, T.FCn);
            pLeft.Children.Add(pLbl);
            _prioCurBadge = new Badge("\u2014", T.Mute);
            _prioCurBadge.Margin = new Thickness(8, 0, 0, 0);
            _prioCurBadge.VerticalAlignment = VerticalAlignment.Center;
            pLeft.Children.Add(_prioCurBadge);
            prioRow.Children.Add(pLeft);

            StackPanel pBtns = new StackPanel();
            pBtns.Orientation = Orientation.Horizontal;
            pBtns.VerticalAlignment = VerticalAlignment.Center;

            _btnPrioNormal = new Btn("正常", BtnKind.Ghost);
            _btnPrioNormal.Height = 26;
            _btnPrioNormal.Compact = true;
            _btnPrioNormal.Margin = new Thickness(0, 0, 5, 0);
            _btnPrioNormal.Click += delegate { ApplyTargetPriority(c, ProcessPower.PriorityNormal); };
            pBtns.Children.Add(_btnPrioNormal);

            _btnPrioAbove = new Btn("高于标准", BtnKind.Ghost);
            _btnPrioAbove.Height = 26;
            _btnPrioAbove.Compact = true;
            _btnPrioAbove.Margin = new Thickness(0, 0, 5, 0);
            _btnPrioAbove.Click += delegate { ApplyTargetPriority(c, ProcessPower.PriorityAboveNormal); };
            pBtns.Children.Add(_btnPrioAbove);

            _btnPrioHigh = new Btn("高", BtnKind.Ghost);
            _btnPrioHigh.Height = 26;
            _btnPrioHigh.Compact = true;
            _btnPrioHigh.Click += delegate { ApplyTargetPriority(c, ProcessPower.PriorityHigh); };
            pBtns.Children.Add(_btnPrioHigh);

            Grid.SetColumn(pBtns, 1);
            prioRow.Children.Add(pBtns);
            box.Children.Add(prioRow);

            Grid ecoRow = new Grid();
            ecoRow.Margin = new Thickness(0, 8, 0, 0);
            ecoRow.ColumnDefinitions.Add(new ColumnDefinition());
            ecoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel eLeft = new StackPanel();
            eLeft.VerticalAlignment = VerticalAlignment.Center;
            eLeft.Children.Add(Ui.Tx("能效模式 (EcoQoS)", T.FsSmall, T.Dim, T.FCn));
            _ecoStateText = Ui.Mono("\u2014", T.FsNano, T.Mute);
            _ecoStateText.Margin = new Thickness(0, 3, 0, 0);
            _ecoStateText.TextTrimming = TextTrimming.CharacterEllipsis;
            eLeft.Children.Add(_ecoStateText);
            ecoRow.Children.Add(eLeft);

            _swEco = new Switch(false);
            _swEco.VerticalAlignment = VerticalAlignment.Center;
            _swEco.Changed += delegate
            {
                if (_ecoSuppress) return;
                ApplyTargetEcoQoS(c, _swEco.On);
            };
            _swEco.Blocked += delegate
            {
                if (c != null && c.Shell != null)
                    c.Shell.Toast("暂无可写的外部前台进程（切到目标窗口后再操作）", false);
            };
            Tip.Attach(_swEco, "Win11 PowerThrottling EcoQoS 开关：关闭可避免游戏进程被调度至 E 核降频；开启可对高占用后台降功耗。");
            Grid.SetColumn(_swEco, 1);
            ecoRow.Children.Add(_swEco);
            box.Children.Add(ecoRow);

            return box;
        }

        /// <summary>两段之间的发丝线：12px 上下留白，和面板内的 12 节奏一致。</summary>
        private static Border Rule()
        {
            Border b = new Border();
            b.Height = 1;
            b.Background = T.S(T.LineSoft);
            b.Margin = new Thickness(0, 8, 0, 8);
            return b;
        }
    }
}
