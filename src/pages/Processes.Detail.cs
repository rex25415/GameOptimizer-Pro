// ============================================================================
//  Processes.Detail.cs — 行内精确控制抽屉 & 建档操作（Processes 分片）
//  提供单进程优先级、亲和锁核（CCD0/CCD1/仅物理核/全核）、EcoQoS 能效开关、
//  以及一键为该进程建档（GameProfile）。全部写入经由 ProcessTable 真值落库。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Processes : Page
    {
        private sealed class DetailRefs
        {
            public TextBlock Meta;
            public Btn PriHigh, PriAbove, PriNorm, PriBelow;
            public Btn AffCcd0, AffCcd1, AffPhys, AffAll;
            public Btn EcoBtn, ProfileBtn;
        }

        private void ToggleExpand(Row r)
        {
            if (r == null || r.CurPid <= 0) return;
            _expandedPid = _expandedPid == r.CurPid ? 0 : r.CurPid;
            SyncExpandedRow(r);
            for (int i = 0; i < MaxRows; i++)
            {
                if (_skel[i] != r && _skel[i].DetailBox != null && _skel[i].DetailBox.Visibility == Visibility.Visible)
                {
                    _skel[i].DetailBox.Visibility = Visibility.Collapsed;
                    if (_skel[i].ExpandBtn != null) _skel[i].ExpandBtn.Label = "调优";
                }
            }
        }

        private void SyncExpandedRow(Row r)
        {
            if (r == null || r.DetailBox == null) return;
            bool open = r.CurPid > 0 && r.CurPid == _expandedPid;
            if (open)
            {
                EnsureDetail(r);
                PaintDetail(r);
            }
            Visibility vis = open ? Visibility.Visible : Visibility.Collapsed;
            if (r.DetailBox.Visibility != vis) r.DetailBox.Visibility = vis;
            if (r.ExpandBtn != null) r.ExpandBtn.Label = open ? "收起" : "调优";
        }

        private void EnsureDetail(Row r)
        {
            if (r.Detail != null) return;
            DetailRefs d = new DetailRefs();

            StackPanel body = new StackPanel();

            // 顶行：状态摘要 + EcoQoS 开关 + 建档按钮
            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            d.Meta = Ui.Mono("\u2014", T.FsNano, T.Dim);
            d.Meta.VerticalAlignment = VerticalAlignment.Center;
            d.Meta.TextTrimming = TextTrimming.CharacterEllipsis;
            d.Meta.TextWrapping = TextWrapping.NoWrap;
            d.Meta.Margin = new Thickness(0, 0, 12, 0);
            top.Children.Add(d.Meta);

            StackPanel rightAct = new StackPanel();
            rightAct.Orientation = Orientation.Horizontal;
            rightAct.VerticalAlignment = VerticalAlignment.Center;

            Row cap = r;
            d.EcoBtn = new Btn("开启 EcoQoS", BtnKind.Quiet);
            d.EcoBtn.Height = T.BtnHSm;
            d.EcoBtn.Compact = true;
            d.EcoBtn.Margin = new Thickness(0, 0, 8, 0);
            d.EcoBtn.Click += delegate { ToggleRowEcoQoS(cap); };
            Tip.Attach(d.EcoBtn, "切换 Win11 进程能效模式（ProcessPowerThrottling EcoQoS）；开启后将调度至能效核与较低电源档位。");
            rightAct.Children.Add(d.EcoBtn);

            d.ProfileBtn = new Btn("为该进程建档", BtnKind.Ghost);
            d.ProfileBtn.Height = T.BtnHSm;
            d.ProfileBtn.Compact = true;
            d.ProfileBtn.SetIcon(Glyph.Profiles, 11);
            d.ProfileBtn.Click += delegate { CreateProfileForRow(cap); };
            Tip.Attach(d.ProfileBtn, "识别该进程真实可执行文件路径并在「游戏档案」中建立优化配置。");
            rightAct.Children.Add(d.ProfileBtn);

            Grid.SetColumn(rightAct, 1);
            top.Children.Add(rightAct);
            body.Children.Add(top);

            body.Children.Add(Gap(8));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(8));

            // 第二行：优先级组 + 亲和锁核组
            Grid ctrls = new Grid();
            ctrls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctrls.ColumnDefinitions.Add(new ColumnDefinition());

            StackPanel priGroup = new StackPanel();
            priGroup.Orientation = Orientation.Horizontal;
            priGroup.VerticalAlignment = VerticalAlignment.Center;
            priGroup.Margin = new Thickness(0, 0, 18, 0);
            TextBlock priLbl = Ui.Label("优先级", T.Mute);
            priLbl.VerticalAlignment = VerticalAlignment.Center;
            priLbl.Margin = new Thickness(0, 0, 8, 0);
            priGroup.Children.Add(priLbl);

            d.PriHigh = SmallBtn("高", delegate { ApplyRowPriority(cap, ProcessPower.PriorityHigh, "高"); });
            d.PriAbove = SmallBtn("高于", delegate { ApplyRowPriority(cap, ProcessPower.PriorityAboveNormal, "高于正常"); });
            d.PriNorm = SmallBtn("正常", delegate { ApplyRowPriority(cap, ProcessPower.PriorityNormal, "正常"); });
            d.PriBelow = SmallBtn("低于", delegate { ApplyRowPriority(cap, ProcessPower.PriorityBelowNormal, "低于正常"); });
            priGroup.Children.Add(d.PriHigh);
            priGroup.Children.Add(d.PriAbove);
            priGroup.Children.Add(d.PriNorm);
            priGroup.Children.Add(d.PriBelow);
            ctrls.Children.Add(priGroup);

            StackPanel affGroup = new StackPanel();
            affGroup.Orientation = Orientation.Horizontal;
            affGroup.HorizontalAlignment = HorizontalAlignment.Right;
            affGroup.VerticalAlignment = VerticalAlignment.Center;
            TextBlock affLbl = Ui.Label("亲和锁核", T.Mute);
            affLbl.VerticalAlignment = VerticalAlignment.Center;
            affLbl.Margin = new Thickness(0, 0, 8, 0);
            affGroup.Children.Add(affLbl);

            d.AffCcd0 = SmallBtn("CCD0", delegate { ApplyRowAffinity(cap, Affinity.CcdMask(C.Topo, 0), "CCD0"); });
            d.AffCcd1 = SmallBtn("CCD1", delegate { ApplyRowAffinity(cap, Affinity.CcdMask(C.Topo, 1), "CCD1"); });
            d.AffPhys = SmallBtn("仅物理核", delegate { ApplyRowAffinity(cap, Affinity.PhysicalOnlyMask(C.Topo), "仅物理核"); });
            d.AffAll = SmallBtn("全核", delegate { ApplyRowAffinity(cap, Affinity.AllMask(), "全核"); });

            if (C == null || C.Topo == null || C.Topo.Ccds < 2)
            {
                d.AffCcd1.IsEnabled = false;
                Tip.Attach(d.AffCcd1, "当前 CPU 拓扑仅包含 1 个 CCD，CCD1 不可用");
            }

            affGroup.Children.Add(d.AffCcd0);
            affGroup.Children.Add(d.AffCcd1);
            affGroup.Children.Add(d.AffPhys);
            affGroup.Children.Add(d.AffAll);
            Grid.SetColumn(affGroup, 1);
            ctrls.Children.Add(affGroup);
            body.Children.Add(ctrls);

            Grid frame = new Grid();
            frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            frame.ColumnDefinitions.Add(new ColumnDefinition());
            Border bar = new Border();
            bar.Background = T.S(T.Accent);
            bar.CornerRadius = new CornerRadius(1);
            frame.Children.Add(bar);
            body.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(body, 1);
            frame.Children.Add(body);

            r.DetailBox.Background = T.S(T.Inset);
            r.DetailBox.BorderBrush = T.S(T.LineSoft);
            r.DetailBox.BorderThickness = new Thickness(1);
            r.DetailBox.CornerRadius = new CornerRadius(T.RadiusSm);
            r.DetailBox.Margin = new Thickness(0, 4, 0, 10);
            r.DetailBox.Padding = new Thickness(8, 9, 12, 9);
            r.DetailBox.Child = frame;
            r.Detail = d;
        }

        private static Btn SmallBtn(string text, EventHandler onClick)
        {
            Btn b = new Btn(text, BtnKind.Quiet);
            b.Height = T.BtnHSm;
            b.Compact = true;
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Click += onClick;
            return b;
        }

        private void PaintDetail(Row r)
        {
            DetailRefs d = r.Detail;
            if (d == null) return;

            string maskText = r.CurMask > 0 ? ("0x" + r.CurMask.ToString("X")) : "\u2014";
            string priText = string.IsNullOrEmpty(r.CurPriLabel) ? "\u2014" : r.CurPriLabel;
            string ecoText = !r.CurEcoKnown ? "未知" : (r.CurEco ? "已开启" : "关闭");
            string imgText = string.IsNullOrEmpty(r.CurImage) ? r.CurName : r.CurImage;
            d.Meta.Text = "PID " + r.CurPid + " · " + imgText + " · 优先级 " + priText +
                          " · 亲和掩码 " + maskText + " · EcoQoS " + ecoText;

            SetBtnActive(d.PriHigh, r.CurPriClass == ProcessPower.PriorityHigh);
            SetBtnActive(d.PriAbove, r.CurPriClass == ProcessPower.PriorityAboveNormal);
            SetBtnActive(d.PriNorm, r.CurPriClass == ProcessPower.PriorityNormal);
            SetBtnActive(d.PriBelow, r.CurPriClass == ProcessPower.PriorityBelowNormal);

            long ccd0 = Affinity.CcdMask(C.Topo, 0);
            long ccd1 = C.Topo != null && C.Topo.Ccds >= 2 ? Affinity.CcdMask(C.Topo, 1) : 0;
            long phys = Affinity.PhysicalOnlyMask(C.Topo);
            long all = Affinity.AllMask();

            SetBtnActive(d.AffCcd0, r.CurMask > 0 && ccd0 > 0 && r.CurMask == ccd0);
            SetBtnActive(d.AffCcd1, r.CurMask > 0 && ccd1 > 0 && r.CurMask == ccd1);
            SetBtnActive(d.AffPhys, r.CurMask > 0 && phys > 0 && r.CurMask == phys && phys != all);
            SetBtnActive(d.AffAll, r.CurMask > 0 && r.CurMask == all);

            d.EcoBtn.Label = r.CurEco ? "关闭 EcoQoS" : "开启 EcoQoS";
            SetBtnActive(d.EcoBtn, r.CurEco);
        }

        private static void SetBtnActive(Btn b, bool active)
        {
            if (b == null) return;
            BtnKind want = active ? BtnKind.Accent : BtnKind.Quiet;
            if (b.Kind != want) b.Kind = want;
        }

        private void ApplyRowPriority(Row r, uint priorityClass, string label)
        {
            if (r == null || r.CurPid <= 0 || C == null) return;
            string err;
            bool ok = C.Procs.SetRowPriority(r.CurPid, priorityClass, out err);
            string name = string.IsNullOrEmpty(r.CurName) ? ProcName.OrPid(r.CurPid) : r.CurName;
            if (ok)
            {
                lock (_touchLock) { _touchedPids.Add(r.CurPid); }
                C.Engine.Ledger.AppendAudit("PROC priority " + name + " (" + r.CurPid + ") -> " + label);
                if (C.Shell != null) C.Shell.Toast("已设置优先级「" + label + "」：" + name, true);
            }
            else
            {
                string why = string.IsNullOrEmpty(err) ? "访问被拒绝或需要管理员权限" : err;
                Diag.Fail("processes.priority", new InvalidOperationException(why));
                if (C.Shell != null) C.Shell.Toast("设置优先级失败：" + why + " · " + name, false);
            }
            Sample();
        }

        private void ApplyRowAffinity(Row r, long mask, string label)
        {
            if (r == null || r.CurPid <= 0 || C == null) return;
            if (mask == 0)
            {
                if (C.Shell != null) C.Shell.Toast("当前处理器拓扑不支持该掩码：" + label, false);
                return;
            }
            string err;
            bool ok = C.Procs.SetRowAffinity(r.CurPid, mask, out err);
            string name = string.IsNullOrEmpty(r.CurName) ? ProcName.OrPid(r.CurPid) : r.CurName;
            if (ok)
            {
                lock (_touchLock) { _touchedPids.Add(r.CurPid); }
                C.Engine.Ledger.AppendAudit("PROC affinity " + name + " (" + r.CurPid + ") -> " + label + " (0x" + mask.ToString("X") + ")");
                if (C.Shell != null) C.Shell.Toast("已设置亲和「" + label + "」：" + name, true);
            }
            else
            {
                string why = string.IsNullOrEmpty(err) ? "访问被拒绝或进程已退出" : err;
                Diag.Fail("processes.affinity", new InvalidOperationException(why));
                if (C.Shell != null) C.Shell.Toast("设置亲和性失败：" + why + " · " + name, false);
            }
            Sample();
        }

        private void ToggleRowEcoQoS(Row r)
        {
            if (r == null || r.CurPid <= 0 || C == null) return;
            bool target = !r.CurEco;
            string err;
            bool ok = C.Procs.SetRowEcoQoS(r.CurPid, target, out err);
            string name = string.IsNullOrEmpty(r.CurName) ? ProcName.OrPid(r.CurPid) : r.CurName;
            if (ok)
            {
                lock (_touchLock) { _touchedPids.Add(r.CurPid); }
                C.Engine.Ledger.AppendAudit("PROC ecoqos " + name + " (" + r.CurPid + ") -> " + (target ? "ON" : "OFF"));
                if (C.Shell != null) C.Shell.Toast((target ? "已启用 EcoQoS 能效模式：" : "已关闭 EcoQoS：") + name, true);
            }
            else
            {
                string why = string.IsNullOrEmpty(err) ? "访问被拒绝或系统版本不支持" : err;
                Diag.Fail("processes.ecoqos", new InvalidOperationException(why));
                if (C.Shell != null) C.Shell.Toast("EcoQoS 操作失败：" + why + " · " + name, false);
            }
            Sample();
        }

        private void CreateProfileForRow(Row r)
        {
            if (r == null || r.CurPid <= 0 || C == null || C.Profiles == null) return;
            string name = !string.IsNullOrEmpty(r.CurName) ? r.CurName : ProcName.OrPid(r.CurPid);
            string exe = ResolveProcessExe(r.CurPid, r.CurImage, name);
            if (string.IsNullOrEmpty(exe))
            {
                if (C.Shell != null) C.Shell.Toast("无法读取该进程的可执行路径", false);
                return;
            }

            GameProfile existing = C.Profiles.FindByExe(exe);
            if (existing != null)
            {
                if (C.Shell != null) C.Shell.Toast("档案已存在：" + existing.Name + " (" + System.IO.Path.GetFileName(existing.Exe) + ")", true);
                return;
            }

            GameProfile p = new GameProfile();
            p.Id = Guid.NewGuid().ToString("N");
            p.Name = name;
            p.Exe = exe;
            p.PinCcd0 = r.Pinned || (C.Topo != null && C.Topo.Ccds > 1);
            p.HighGpu = true;
            p.Timer = false;
            p.Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (!C.Profiles.Add(p))
            {
                if (C.Shell != null) C.Shell.Toast("建档失败：无法写入档案文件", false);
                return;
            }
            if (C.State != null) C.State.Post(Dirty.Daemon);
            C.Engine.Ledger.AppendAudit("PROFILE create " + p.Name + " <- " + p.Exe);
            if (C.Shell != null) C.Shell.Toast("已建档「" + p.Name + "」(" + System.IO.Path.GetFileName(p.Exe) + ")", true);
        }

        private static string ResolveProcessExe(int pid, string fallbackImage, string fallbackName)
        {
            string full = ProcName.FullPathOf(pid);
            if (!string.IsNullOrEmpty(full)) return full;
            if (!string.IsNullOrEmpty(fallbackImage)) return fallbackImage;
            return string.IsNullOrEmpty(fallbackName) ? "" : (fallbackName + ".exe");
        }
    }
}
