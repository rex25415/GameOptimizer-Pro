// ============================================================================
//  Tuning.Inspector.cs — Rev130 调优矩阵右侧「战术检视台」(Master-Detail Inspector)
//  悬停或点选左侧任意调优项，右侧实时展示：风险/重启/权限徽标、原值→目标值双栏对比、
//  底层注册表/电源/BCD 原子操作清单、工程原理解释与账本可逆状态，支持直达应用/还原。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Tuning : Page
    {
        private Grid _workbench;
        private FrameworkElement _inspectorHost;
        private TranslateTransform _inspStickyTx;
        private ScrollViewer _stickyScroll;
        private ScrollChangedEventHandler _stickyHandler;
        private ActionDef _inspected;
        private TextBlock _inspGroup, _inspName, _inspDesc, _inspMech;
        private Border _inspRiskChip, _inspStateChip, _inspRestartChip;
        private TextBlock _inspRiskText, _inspStateText;
        private TextBlock _inspCurVal, _inspTargetVal, _inspLedgerText, _inspStepText;
        private StackPanel _inspOpsList;
        private Btn _inspActionBtn;
        private ColumnDefinition _inspGapCol, _inspCol;
        private bool _inspHidden;

        private UIElement BuildInspector(Core c)
        {
            StackPanel sp = new StackPanel();

            // 顶标 + 状态芯片
            Grid topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition());
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel topLeft = new StackPanel();
            topLeft.Children.Add(Ui.Label("TACTICAL INSPECTOR", T.AccentDim));
            _inspGroup = Ui.Mono("SELECT ACTION", T.FsNano, T.Mute);
            _inspGroup.Margin = new Thickness(0, 2, 0, 0);
            topLeft.Children.Add(_inspGroup);
            topRow.Children.Add(topLeft);

            _inspStateChip = new Border();
            _inspStateChip.CornerRadius = new CornerRadius(T.RadiusMd);
            _inspStateChip.BorderThickness = new Thickness(1);
            _inspStateChip.BorderBrush = T.S(T.LineMid);
            _inspStateChip.Background = T.S(T.Inset);
            _inspStateChip.Padding = new Thickness(8, 3, 8, 3);
            _inspStateChip.VerticalAlignment = VerticalAlignment.Center;
            _inspStateText = Ui.Mono("—", T.FsNano, T.Dim);
            _inspStateChip.Child = _inspStateText;
            Grid.SetColumn(_inspStateChip, 1);
            topRow.Children.Add(_inspStateChip);
            sp.Children.Add(topRow);

            sp.Children.Add(Gap(10));
            sp.Children.Add(Ui.Rule(T.LineSoft, 0));
            sp.Children.Add(Gap(10));

            // 动作标题与摘要
            _inspName = Ui.Tx("悬停或选择左侧调优项", T.FsTitle, T.Hi, T.FCn);
            _inspName.FontWeight = FontWeights.SemiBold;
            _inspName.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(_inspName);

            _inspDesc = Ui.Tx("检视台将实时展示系统当前探测值、目标写入值、底层注册表/电源操作路径与写前账本状态。", T.FsSmall, T.Dim, T.FCn);
            _inspDesc.TextWrapping = TextWrapping.Wrap;
            _inspDesc.LineHeight = 18;
            _inspDesc.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(_inspDesc);

            // 风险 + 重启徽标行
            StackPanel badges = new StackPanel();
            badges.Orientation = Orientation.Horizontal;
            badges.Margin = new Thickness(0, 10, 0, 0);

            _inspRiskChip = new Border();
            _inspRiskChip.CornerRadius = new CornerRadius(T.RadiusSm);
            _inspRiskChip.BorderThickness = new Thickness(1);
            _inspRiskChip.BorderBrush = T.S(T.LineMid);
            _inspRiskChip.Background = T.S(T.Inset);
            _inspRiskChip.Padding = new Thickness(7, 2, 7, 2);
            _inspRiskText = Ui.Mono("RISK A", T.FsNano, T.Ok);
            _inspRiskChip.Child = _inspRiskText;
            badges.Children.Add(_inspRiskChip);

            _inspRestartChip = Chip.Make("重启生效", T.Warn, T.ChipH);
            _inspRestartChip.Margin = new Thickness(8, 0, 0, 0);
            _inspRestartChip.Visibility = Visibility.Collapsed;
            badges.Children.Add(_inspRestartChip);
            sp.Children.Add(badges);

            sp.Children.Add(Gap(12));

            // 双栏 Diff 对比卡：当前实测 → 目标值
            Grid diff = new Grid();
            diff.ColumnDefinitions.Add(new ColumnDefinition());
            diff.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            diff.ColumnDefinitions.Add(new ColumnDefinition());

            _inspCurVal = Ui.Mono("—", T.FsMicro, T.Text);
            _inspCurVal.TextTrimming = TextTrimming.CharacterEllipsis;
            Border curBox = MakeDiffCard("当前实测", _inspCurVal, T.LineMid);
            diff.Children.Add(curBox);

            TextBlock arr = Ui.Mono("\u2192", T.FsBody, T.Accent);
            arr.HorizontalAlignment = HorizontalAlignment.Center;
            arr.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(arr, 1);
            diff.Children.Add(arr);

            _inspTargetVal = Ui.Mono("—", T.FsMicro, T.Ok);
            _inspTargetVal.TextTrimming = TextTrimming.CharacterEllipsis;
            Border tgtBox = MakeDiffCard("目标写入", _inspTargetVal, T.AccentA(0x5A));
            Grid.SetColumn(tgtBox, 2);
            diff.Children.Add(tgtBox);
            sp.Children.Add(diff);

            sp.Children.Add(Gap(12));

            // 底层原子操作路径
            TextBlock opsLbl = Ui.Label("ATOMIC OPERATIONS", T.Mute);
            sp.Children.Add(opsLbl);
            _inspOpsList = new StackPanel();
            _inspOpsList.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(_inspOpsList);

            sp.Children.Add(Gap(12));

            // 工程机制说明
            TextBlock mechLbl = Ui.Label("MECHANISM & BOUNDARY", T.Mute);
            sp.Children.Add(mechLbl);
            Border mechBox = new Border();
            mechBox.CornerRadius = new CornerRadius(T.RadiusMd);
            mechBox.Background = T.S(0xAA070C17);
            mechBox.BorderBrush = T.S(T.LineSoft);
            mechBox.BorderThickness = new Thickness(1);
            mechBox.Padding = new Thickness(10, 8, 10, 8);
            mechBox.Margin = new Thickness(0, 6, 0, 0);
            _inspMech = Ui.Tx("—", T.FsMicro, T.Text, T.FCn);
            _inspMech.TextWrapping = TextWrapping.Wrap;
            _inspMech.LineHeight = 18;
            mechBox.Child = _inspMech;
            sp.Children.Add(mechBox);

            sp.Children.Add(Gap(12));
            sp.Children.Add(Ui.Rule(T.LineSoft, 0));
            sp.Children.Add(Gap(10));

            // 底部账本状态 + 直接操作按钮
            Grid foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _inspLedgerText = Ui.Mono("账本待检", T.FsNano, T.Mute);
            _inspLedgerText.VerticalAlignment = VerticalAlignment.Center;
            _inspLedgerText.TextTrimming = TextTrimming.CharacterEllipsis;
            foot.Children.Add(_inspLedgerText);

            _inspActionBtn = new Btn("应用此项", BtnKind.Primary);
            _inspActionBtn.Height = T.BtnHSm;
            _inspActionBtn.Compact = true;
            _inspActionBtn.Click += delegate { RunInspectedAction(c); };
            Grid.SetColumn(_inspActionBtn, 1);
            foot.Children.Add(_inspActionBtn);
            sp.Children.Add(foot);

            // Rev130 随滚吸顶底栏：当前序号 + 上/下一项快速步进
            sp.Children.Add(Gap(10));
            sp.Children.Add(Ui.Rule(T.LineSoft, 0));
            sp.Children.Add(Gap(8));

            Grid navBar = new Grid();
            navBar.ColumnDefinitions.Add(new ColumnDefinition());
            navBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _inspStepText = Ui.Mono("01 / 30 \u00B7 \u968F\u6EDA\u5438\u9876", T.FsNano, T.Mute);
            _inspStepText.VerticalAlignment = VerticalAlignment.Center;
            navBar.Children.Add(_inspStepText);

            StackPanel stepBtns = new StackPanel();
            stepBtns.Orientation = Orientation.Horizontal;
            stepBtns.VerticalAlignment = VerticalAlignment.Center;
            stepBtns.Children.Add(MakeStepPill("\u2039", "检视上一项", delegate { StepInspect(-1); }));
            Border sg = new Border(); sg.Width = 6; stepBtns.Children.Add(sg);
            stepBtns.Children.Add(MakeStepPill("\u203A", "检视下一项", delegate { StepInspect(1); }));
            Grid.SetColumn(stepBtns, 1);
            navBar.Children.Add(stepBtns);
            sp.Children.Add(navBar);

            Border panel = Ui.HeroPanel(sp, 16);
            panel.VerticalAlignment = VerticalAlignment.Top;
            _inspStickyTx = new TranslateTransform(0, 0);
            panel.RenderTransform = _inspStickyTx;
            Panel.SetZIndex(panel, 2);
            panel.Loaded += delegate { AttachStickyScroll(); UpdateInspectorSticky(); };
            panel.Unloaded += delegate { DetachStickyScroll(); };
            panel.SizeChanged += delegate { UpdateInspectorSticky(); };
            _inspectorHost = panel;
            return panel;
        }

        private static Border MakeStepPill(string glyph, string tip, Action onClick)
        {
            Border b = new Border();
            b.Width = 24;
            b.Height = 20;
            b.CornerRadius = new CornerRadius(T.RadiusSm);
            b.Background = T.S(T.Inset);
            b.BorderBrush = T.S(T.LineMid);
            b.BorderThickness = new Thickness(1);
            b.Cursor = Cursors.Hand;
            TextBlock tx = Ui.Mono(glyph, T.FsBody, T.Dim);
            tx.HorizontalAlignment = HorizontalAlignment.Center;
            tx.VerticalAlignment = VerticalAlignment.Center;
            tx.Margin = new Thickness(0, -1, 0, 0);
            b.Child = tx;
            b.MouseEnter += delegate
            {
                b.Background = T.S(T.AccentWash);
                b.BorderBrush = T.S(T.AccentA(0x88));
                tx.Foreground = T.S(T.Hi);
            };
            b.MouseLeave += delegate
            {
                b.Background = T.S(T.Inset);
                b.BorderBrush = T.S(T.LineMid);
                tx.Foreground = T.S(T.Dim);
            };
            Pressable.On(b, onClick);
            Tip.Attach(b, tip);
            return b;
        }

        private static Border MakeDiffCard(string title, TextBlock val, uint borderCol)
        {
            StackPanel inner = new StackPanel();
            inner.Children.Add(Ui.Tx(title, T.FsNano, T.Mute, T.FCn));
            val.Margin = new Thickness(0, 4, 0, 0);
            inner.Children.Add(val);

            Border b = new Border();
            b.CornerRadius = new CornerRadius(T.RadiusMd);
            b.Background = T.S(0xC0070C17);
            b.BorderBrush = T.S(borderCol);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(9, 7, 9, 7);
            b.Child = inner;
            return b;
        }

        private void InspectAction(ActionDef a)
        {
            if (a == null || _inspName == null || C == null) return;
            _inspected = a;

            _inspGroup.Text = (a.Group ?? "SYSTEM") + " · " + (a.Id ?? "");
            _inspName.Text = a.Name ?? "";
            _inspDesc.Text = a.Desc ?? "";
            _inspMech.Text = !string.IsNullOrEmpty(a.Mech) ? a.Mech : (a.Desc ?? "—");

            uint rCol = a.Risk == "C" ? T.Bad : (a.Risk == "B" ? T.Warn : T.Ok);
            string rTxt = a.Risk == "C" ? "RISK C · 高风险" : (a.Risk == "B" ? "RISK B · 系统/服务" : "RISK A · 低风险可逆");
            _inspRiskText.Text = rTxt;
            _inspRiskText.Foreground = T.S(rCol);
            _inspRiskChip.BorderBrush = T.S(Ui.Wash(rCol, 0x55));
            _inspRiskChip.Background = T.S(Ui.Wash(rCol, 0x16));
            _inspRestartChip.Visibility = a.Restart ? Visibility.Visible : Visibility.Collapsed;

            string cur0;
            AState st = C.Engine.ProbeForUi(a, out cur0);
            bool applied = st == AState.Applied;
            uint sCol = applied ? T.Ok : (st == AState.Partial ? T.Warn : (st == AState.Denied ? T.Bad : T.Accent));
            _inspStateText.Text = applied ? "已应用 ✓" : (st == AState.Partial ? "部分生效" : (st == AState.Denied ? "需管理员" : "未应用"));
            _inspStateText.Foreground = T.S(sCol);
            _inspStateChip.BorderBrush = T.S(Ui.Wash(sCol, 0x60));
            _inspStateChip.Background = T.S(Ui.Wash(sCol, 0x18));

            _inspCurVal.Text = string.IsNullOrEmpty(cur0) ? "(未设置)" : cur0;
            _inspCurVal.Foreground = T.S(applied ? T.Ok : T.Text);

            string tgt = "—";
            if (a.Ops != null && a.Ops.Length > 0)
            {
                Op o0 = a.Ops[0];
                tgt = o0.Delete ? "[删除键值]" : (string.IsNullOrEmpty(o0.Value) ? "—" : o0.Value);
                if (a.Ops.Length > 1) tgt += " (+" + (a.Ops.Length - 1) + ")";
            }
            _inspTargetVal.Text = tgt;
            _inspTargetVal.Foreground = T.S(T.Accent);

            _inspOpsList.Children.Clear();
            if (a.Ops != null)
            {
                int show = Math.Min(a.Ops.Length, 4);
                for (int i = 0; i < show; i++)
                {
                    Op op = a.Ops[i];
                    Border ob = new Border();
                    ob.CornerRadius = new CornerRadius(T.RadiusSm);
                    ob.Background = T.S(0xAA080E1B);
                    ob.BorderBrush = T.S(T.LineSoft);
                    ob.BorderThickness = new Thickness(1);
                    ob.Padding = new Thickness(8, 5, 8, 5);
                    if (i > 0) ob.Margin = new Thickness(0, 4, 0, 0);

                    StackPanel osp = new StackPanel();
                    string keyLabel = !string.IsNullOrEmpty(op.KvKey) ? op.KvKey : (!string.IsNullOrEmpty(op.Name) ? op.Name : (op.Path ?? ""));
                    string head = (op.Kind ?? "OP").ToUpperInvariant() + " · " + keyLabel;
                    TextBlock ot1 = Ui.Mono(head, T.FsNano, T.Accent);
                    ot1.TextTrimming = TextTrimming.CharacterEllipsis;
                    osp.Children.Add(ot1);

                    string loc = !string.IsNullOrEmpty(op.Hive)
                        ? (op.Hive + "\\" + op.Path)
                        : (!string.IsNullOrEmpty(op.Path) ? op.Path : "Windows Native");
                    TextBlock ot2 = Ui.Mono(loc, T.FsNano, T.Mute);
                    ot2.TextTrimming = TextTrimming.CharacterEllipsis;
                    ot2.Margin = new Thickness(0, 2, 0, 0);
                    osp.Children.Add(ot2);

                    ob.Child = osp;
                    _inspOpsList.Children.Add(ob);
                }
            }

            int ldgHits = 0;
            IList<LedgerEntry> entries = C.Engine.Ledger.Entries;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].ActionId == a.Id) ldgHits++;

            if (ldgHits > 0)
            {
                _inspLedgerText.Text = "账本已存 " + ldgHits + " 条原值 · 可随时还原";
                _inspLedgerText.Foreground = T.S(T.Ok);
            }
            else
            {
                _inspLedgerText.Text = applied ? "系统原生已符合 · 无需入账" : "写入前自动原子落账";
                _inspLedgerText.Foreground = T.S(T.Mute);
            }

            if (_inspActionBtn != null)
            {
                if (applied && ldgHits > 0) _inspActionBtn.Label = "按账本还原";
                else if (applied) _inspActionBtn.Label = "已符合 ✓";
                else _inspActionBtn.Label = "应用此项";
            }
            UpdateInspectIndex();
        }

        private void UpdateInspectIndex()
        {
            if (_inspStepText == null) return;
            int visCount = 0;
            int hitIdx = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Root.Visibility != Visibility.Visible) continue;
                visCount++;
                if (_inspected != null && _rows[i].Def != null && _rows[i].Def.Id == _inspected.Id)
                    hitIdx = visCount;
            }
            if (visCount <= 0)
            {
                _inspStepText.Text = "0 / 0 \u00B7 \u968F\u6EDA\u5438\u9876";
                return;
            }
            if (hitIdx < 0) hitIdx = 1;
            _inspStepText.Text = hitIdx.ToString("00") + " / " + visCount.ToString("00") + " \u00B7 \u968F\u6EDA\u5438\u9876";
        }

        private void StepInspect(int delta)
        {
            List<Ui.ActionRow> vis = new List<Ui.ActionRow>();
            int cur = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Root.Visibility != Visibility.Visible) continue;
                if (_inspected != null && _rows[i].Def != null && _rows[i].Def.Id == _inspected.Id)
                    cur = vis.Count;
                vis.Add(_rows[i]);
            }
            if (vis.Count == 0) return;
            int next = cur < 0 ? 0 : (cur + delta + vis.Count) % vis.Count;
            Ui.ActionRow target = vis[next];
            InspectAction(target.Def);
            FrameworkElement fe = target.Root as FrameworkElement;
            if (fe != null) fe.BringIntoView();
        }

        private void SyncInspectedToVisible()
        {
            Ui.ActionRow firstVis = null;
            bool curVisible = false;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Root.Visibility != Visibility.Visible) continue;
                if (firstVis == null) firstVis = _rows[i];
                if (_inspected != null && _rows[i].Def != null && _rows[i].Def.Id == _inspected.Id)
                    curVisible = true;
            }
            if (!curVisible && firstVis != null) InspectAction(firstVis.Def);
            else UpdateInspectIndex();
            UpdateInspectorSticky();
        }

        private void AttachStickyScroll()
        {
            if (_workbench == null) return;
            ScrollViewer found = FindAncestorScrollViewer(_workbench);
            if (object.ReferenceEquals(found, _stickyScroll)) return;
            DetachStickyScroll();
            if (found == null) return;
            _stickyScroll = found;
            if (_stickyHandler == null)
                _stickyHandler = delegate { UpdateInspectorSticky(); };
            _stickyScroll.ScrollChanged += _stickyHandler;
        }

        private void DetachStickyScroll()
        {
            if (_stickyScroll != null && _stickyHandler != null)
                _stickyScroll.ScrollChanged -= _stickyHandler;
            _stickyScroll = null;
        }

        private static ScrollViewer FindAncestorScrollViewer(DependencyObject start)
        {
            DependencyObject cur = start;
            while (cur != null)
            {
                ScrollViewer sv = cur as ScrollViewer;
                if (sv != null) return sv;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return null;
        }

        /// <summary>随外层页面滚动将右侧战术检视台吸顶定位在视口内，零重排零分配。</summary>
        private void UpdateInspectorSticky()
        {
            if (_inspStickyTx == null) return;
            if (_inspHidden || _workbench == null || _inspectorHost == null)
            {
                _inspStickyTx.Y = 0;
                return;
            }
            if (_stickyScroll == null) AttachStickyScroll();
            ScrollViewer sv = _stickyScroll;
            if (sv == null || !sv.IsAncestorOf(_workbench))
            {
                _inspStickyTx.Y = 0;
                return;
            }
            double wbH = _workbench.ActualHeight;
            double inspH = _inspectorHost.ActualHeight;
            if (wbH <= 0 || inspH <= 0)
            {
                _inspStickyTx.Y = 0;
                return;
            }
            double maxTravel = wbH - inspH - 18;
            if (maxTravel <= 0)
            {
                _inspStickyTx.Y = 0;
                return;
            }
            double wbYInViewport = 0;
            try
            {
                wbYInViewport = _workbench.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
            }
            catch (Exception ex)
            {
                Diag.Fail("tuning.sticky", ex);
                _inspStickyTx.Y = 0;
                return;
            }
            const double stickyPad = 14.0;
            double want = stickyPad - wbYInViewport;
            if (want < 0) want = 0;
            if (want > maxTravel) want = maxTravel;
            double rounded = Math.Round(want);
            if (Math.Abs(_inspStickyTx.Y - rounded) >= 0.5)
                _inspStickyTx.Y = rounded;
        }

        private void RunInspectedAction(Core c)
        {
            if (_inspected == null || c == null) return;
            ActionDef a = _inspected;
            AState st = c.Engine.ProbeForUi(a);
            if (!c.Engine.CanWrite(a))
            {
                c.Shell.Toast("需要管理员权限：点左下角状态行提权重启", false);
                return;
            }
            if (st == AState.Applied)
            {
                ApplyReport rr = c.Engine.RevertAction(a.Id);
                c.State.Post(Dirty.Ledger);
                c.Shell.Toast(rr.Ok ? ("已还原：" + a.Name) : (rr.Reason ?? "账本无记录，未改动系统"), rr.Ok);
            }
            else
            {
                ApplyReport ar = c.Engine.Apply(a);
                c.State.Post(Dirty.Ledger);
                c.Shell.Toast(ar.Ok ? ("已应用：" + a.Name) : (ar.Reason ?? "应用失败"), ar.Ok);
            }
            Refresh();
            InspectAction(a);
        }

        private void FitInspector(double width)
        {
            bool hide = width < 960;
            if (hide == _inspHidden) return;
            _inspHidden = hide;
            if (_inspectorHost != null) _inspectorHost.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            if (_inspGapCol != null) _inspGapCol.Width = new GridLength(hide ? 0 : 12);
            if (_inspCol != null) _inspCol.Width = new GridLength(hide ? 0 : 296);
            UpdateInspectorSticky();
        }
    }
}
