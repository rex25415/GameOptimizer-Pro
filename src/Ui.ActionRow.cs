// ============================================================================
//  Ui.ActionRow.cs — 共享动作行：一个动作 = 名称 + 风险 + 说明 + 现值→目标 + 状态 + 开关
//
//  为什么要有它：
//  「读当前值 / 判是否已应用 / 点了写账本 / 失败回滚 UI」这一套，调优矩阵、
//  图形栈、网络协议栈各写过一遍（合计约 1150 行，逻辑八成重复）。三份拷贝各修各的，
//  于是同一种失败在三个页面上的反馈不一致：有的弹 toast、有的静默、有的把开关
//  留在用户的点击态上——界面显示"开"，系统其实是"关"。
//
//  这一层只做四件事，且只做一遍：
//   1) 单一判读：Engine.Probe 决定状态文字与开关位置，开关本身不是真源；
//   2) 单一写入点：开关 → Engine.Apply / Engine.RevertEntry，没有第二条写路径；
//   3) 单一回滚：提交后一律重新探针，失败时开关退回真实状态（不是点击态）；
//   4) 单一失败反馈：Shell.Toast + Diag.Fail，任何一条失败都不能静默。
//
//  页面只传数据（ActionDef + 可选标记），不再各写一遍行。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Go
{
    public static partial class Ui
    {
        /// <summary>动作行版式。Stacked = 图形/网络（说明 + 读数 + 备注纵向堆叠）；Columns = 调优矩阵（列对齐 + 读数胶囊）。</summary>
        public enum ActionRowKind
        {
            Stacked = 0,
            Columns = 1
        }

        /// <summary>
        /// 动作行的差异全部走参数：页面不再为"重启生效 / 撤销路径 / 批量标记"各包一层。
        /// 未设置的字段 = 该页不需要这项信息。
        /// </summary>
        public sealed class ActionRowSpec
        {
            public Core Core;
            public ActionDef Def;
            public ActionRowKind Kind;
            /// <summary>标题行附加胶囊，例如「重启生效」/「RESTART」。</summary>
            public string Chip;
            /// <summary>标题行附加批量胶囊，例如「×3」：该动作一次写 3 个键。</summary>
            public string BatchLabel;
            /// <summary>读数下面追加一行等宽说明，例如撤销路径。</summary>
            public string Note;
            /// <summary>开关自己的工具提示。</summary>
            public string SwitchTip;
            /// <summary>整行可点（开关之外点行也算切换）。</summary>
            public bool RowClick;
            /// <summary>目标值颜色，0 = T.Ok。</summary>
            public uint WantTint;
            /// <summary>应用回调：null = Engine.Apply（默认，也是唯一写入点）。</summary>
            public Func<ActionDef, ApplyReport> ApplyFn;
            /// <summary>还原回调：null = 逐条 Engine.RevertEntry（默认）。</summary>
            public Func<ActionDef, ApplyReport> RevertFn;
            /// <summary>提交完成（无论成败）后的回调：页面刷新自己的汇总读数。</summary>
            public EventHandler Committed;
        }

        /// <summary>一行动作的构建结果：Root 挂进页面，Refresh 让页面按需重绘。</summary>
        public sealed class ActionRow
        {
            public readonly UIElement Root;
            public readonly ActionDef Def;
            public readonly Action Refresh;

            private readonly ActionRowSpec _spec;
            private readonly Core _core;
            private readonly bool _columns;
            private readonly uint _wantTint;
            private readonly string _opName;
            private readonly string _more;
            private readonly double _chipW;

            private Grid _grid;
            private Border _bar;
            private Border _appliedRail;
            private Border _pad;
            private StackPanel _chips;
            private Border _adminChip;
            private ColumnDefinition _readCol;
            private ColumnDefinition _airCol;
            private ColumnDefinition _zoneCol;
            private TextBlock _name;
            private TextBlock _desc;
            private TextBlock _read;
            private TextBlock _state;
            private Switch _sw;
            private readonly string _risk;

            private bool _compact;
            private bool _painted;
            private bool _hasPaint;
            private AState _lastState;
            private bool _lastCan;
            private string _curShown;
            private string _wantShown;
            private int _toneShown = -1;
            private int _padTone = -1;
            private string _lastFeedback;
            private bool _lastFeedbackOk;

            public ActionRow(ActionRowSpec spec)
            {
                _spec = spec;
                _core = spec == null ? null : spec.Core;
                Def = spec == null ? null : spec.Def;
                _columns = spec != null && spec.Kind == ActionRowKind.Columns;
                _wantTint = (spec == null || spec.WantTint == 0) ? T.Ok : spec.WantTint;

                ActionDef a = Def;
                string risk = (a == null || string.IsNullOrEmpty(a.Risk)) ? "A" : a.Risk;
                _risk = risk;
                Op[] ops = a == null ? null : a.Ops;
                Op first = (ops != null && ops.Length > 0) ? ops[0] : null;
                _opName = first == null ? "" : (ReadableOpName(first) + "  ");
                _more = (ops != null && ops.Length > 1) ? ("  +" + (ops.Length - 1)) : null;

                _grid = new Grid();
                _grid.Background = Brushes.Transparent;

                // ---- 左：风险色条（A 绿 / B 琥珀 / C 红）+ Applied 高亮轨 ---------
                BuildBar(risk);

                // ---- 中：名称 + 风险胶囊 + 可选标记 + 说明 ----------------------
                StackPanel mid = new StackPanel();
                mid.MinWidth = 0;
                mid.VerticalAlignment = VerticalAlignment.Center;

                StackPanel titleRow = new StackPanel();
                titleRow.Orientation = Orientation.Horizontal;

                _name = Tx(a == null || a.Name == null ? "—" : a.Name, T.FsBody, T.Hi, T.FCn);
                _name.FontWeight = FontWeights.SemiBold;
                _name.TextTrimming = TextTrimming.CharacterEllipsis;
                _name.TextWrapping = TextWrapping.NoWrap;
                _name.VerticalAlignment = VerticalAlignment.Center;
                titleRow.Children.Add(_name);

                _chips = new StackPanel();
                _chips.Orientation = Orientation.Horizontal;
                _chips.VerticalAlignment = VerticalAlignment.Center;

                if (spec != null && !string.IsNullOrEmpty(spec.Chip))
                {
                    Border extra = Chip.Make(spec.Chip, T.Dim, T.ChipHSm);
                    extra.Margin = new Thickness(5, 0, 0, 0);
                    _chips.Children.Add(extra);
                }
                if (spec != null && !string.IsNullOrEmpty(spec.BatchLabel))
                {
                    Border batch = Chip.Make(spec.BatchLabel, T.AccentDim, T.ChipHSm);
                    batch.Margin = new Thickness(5, 0, 0, 0);
                    if (ops != null && ops.Length > 1)
                        Tip.Attach(batch, "该动作一次写 " + ops.Length + " 个键；行内读数只展示第一个，其余以 +N 标注。");
                    _chips.Children.Add(batch);
                }

                // 需管理员：不是"开关坏了"，是"这里没权限"。胶囊 + 悬停说明，开关同步只读。
                _adminChip = Chip.Make("需管理员", T.Warn, T.ChipHSm);
                _adminChip.Margin = new Thickness(6, 0, 0, 0);
                _adminChip.Visibility = Visibility.Collapsed;
                Tip.Attach(_adminChip, "该动作写入 HKLM / BCD / 电源方案，需要以管理员身份运行；点左下角状态行提权重启后可写。");
                _chips.Children.Add(_adminChip);

                titleRow.Children.Add(_chips);
                mid.Children.Add(titleRow);

                _desc = Tx(a == null || a.Desc == null ? "" : a.Desc, T.FsSmall, T.Dim, T.FCn);
                _desc.TextTrimming = TextTrimming.CharacterEllipsis;
                _desc.TextWrapping = TextWrapping.Wrap;
                _desc.LineHeight = 16.5;
                _desc.MaxHeight = 35;
                mid.Children.Add(_desc);

                // ---- 中：现值 → 目标值（等宽，只显示第一个 Op，其余 +N）---------
                _read = Mono("", T.FsNano, T.Text);
                _read.TextTrimming = TextTrimming.CharacterEllipsis;
                _read.TextWrapping = TextWrapping.NoWrap;
                _read.VerticalAlignment = VerticalAlignment.Center;

                // ---- 右：状态 + 开关 -------------------------------------------
                _state = Tx("", T.FsNano, T.Dim, T.FCn);
                _state.HorizontalAlignment = HorizontalAlignment.Right;
                _state.VerticalAlignment = VerticalAlignment.Center;
                _state.TextTrimming = TextTrimming.CharacterEllipsis;
                _state.TextWrapping = TextWrapping.NoWrap;
                _state.MaxWidth = 76;
                _state.Margin = new Thickness(0, 0, 10, 0);
                _state.IsHitTestVisible = false;

                _sw = new Switch(false);
                _sw.VerticalAlignment = VerticalAlignment.Center;
                _sw.Changed += OnToggle;
                _sw.Blocked += OnBlocked;
                if (spec != null && !string.IsNullOrEmpty(spec.SwitchTip)) Tip.Attach(_sw, spec.SwitchTip);

                // 风险不再占一枚字母胶囊：左缘 3px 色条已经说了同一件事，
                // 而字母要求读者先记住 A/B/C 的表才能翻译。信息重复 = 噪声。
                _chipW = 0;
                if (spec != null && !string.IsNullOrEmpty(spec.Chip)) _chipW += 54;
                if (spec != null && !string.IsNullOrEmpty(spec.BatchLabel)) _chipW += 46;
                if (a != null && a.NeedsAdmin) _chipW += 68;

                if (_columns) BuildColumns(mid, _desc, risk, spec);
                else BuildStacked(mid, _desc, risk, spec);

                Root = _grid;
                Refresh = new Action(RefreshNow);

                if (_columns)
                {
                    _grid.Margin = new Thickness(-10, 0, -10, 0);
                    _grid.Cursor = Cursors.Hand;
                    _grid.SizeChanged += delegate { Fit(_grid.ActualWidth); };
                }

                Tip.Attach(_grid, a == null ? null : a.Mech);

                if (spec != null && spec.RowClick)
                {
                    _grid.Cursor = Cursors.Hand;
                    _grid.MouseEnter += delegate { _grid.Background = T.S(T.LineFaint); };
                    _grid.MouseLeave += delegate { _grid.Background = Brushes.Transparent; };
                    Pressable.On(_grid, new Action(ClickRow));
                }
            }

            /// <summary>最近一次判读到的状态与可写性（页面清点用，避免重复探针）。</summary>
            public AState State { get { return _lastState; } }
            public bool CanWrite { get { return _lastCan; } }
            public bool SwitchOn { get { return _sw != null && _sw.On; } }
            public string LastFeedback { get { return _lastFeedback ?? ""; } }
            public bool LastFeedbackOk { get { return _lastFeedbackOk; } }
            public void TestUserToggle(bool want)
            {
                if (_sw != null) _sw.On = want;
                Commit(want);
            }

            // ---- 构建 -------------------------------------------------------

            private void BuildBar(string risk)
            {
                Grid railHost = new Grid();
                railHost.Width = _columns ? 3 : 2.5;
                railHost.HorizontalAlignment = _columns ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
                railHost.Margin = _columns ? new Thickness(0, 8, 0, 8) : new Thickness(0, 3, 0, 3);
                railHost.SnapsToDevicePixels = true;

                _bar = new Border();
                _bar.CornerRadius = new CornerRadius(_columns ? 1.5 : 1);
                _bar.Background = risk == "C" ? CtlTint.Wash(T.Bad, 0xCC)
                                : risk == "B" ? CtlTint.Wash(T.Warn, 0xBB)
                                : CtlTint.Wash(T.Ok, 0x36);
                railHost.Children.Add(_bar);

                // Applied 状态细高亮轨：风险 A 整条点亮为高对比 Ok 轨；风险 B/C 下段点亮而顶部保留 6px 风险提示段。
                _appliedRail = new Border();
                _appliedRail.CornerRadius = new CornerRadius(_columns ? 1.5 : 1);
                _appliedRail.Background = T.S(T.Ok);
                _appliedRail.Margin = (risk == "B" || risk == "C") ? new Thickness(0, 7, 0, 0) : new Thickness(0);
                _appliedRail.Visibility = Visibility.Collapsed;
                _appliedRail.IsHitTestVisible = false;
                railHost.Children.Add(_appliedRail);

                // 颜色是前置注意，但不能是唯一通道：悬停给出这个词的字面意思。
                Tip.Attach(railHost, risk == "C" ? "风险 C：可能影响系统稳定性，回滚路径已落账，但建议逐项确认"
                            : risk == "B" ? "风险 B：需要重新登录或重启才会完全生效"
                            : "风险 A：可逆，随时可在还原中心撤回");
                _grid.Children.Add(railHost);
            }

            private void BuildColumns(StackPanel mid, TextBlock desc, string risk, ActionRowSpec spec)
            {
                mid.Margin = new Thickness(13, 9, 14, 9);
                desc.Margin = new Thickness(0, 4, 0, 0);

                // 列宽：3 风险条 / 身分区（星号）/ 读数 / 呼吸位 / 状态 132。
                // 读数与呼吸位由 Fit 按行宽分档收放，保证窄屏下不挤掉胶囊或开关。
                _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
                _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _readCol = new ColumnDefinition { Width = new GridLength(300) };
                _grid.ColumnDefinitions.Add(_readCol);
                _airCol = new ColumnDefinition { Width = new GridLength(12) };
                _grid.ColumnDefinitions.Add(_airCol);
                _zoneCol = new ColumnDefinition { Width = new GridLength(132) };
                _grid.ColumnDefinitions.Add(_zoneCol);

                Grid.SetColumn(mid, 1);
                _grid.Children.Add(mid);

                _pad = new Border();
                _pad.Height = 24;
                _pad.CornerRadius = new CornerRadius(12);
                _pad.BorderThickness = new Thickness(1);
                _pad.Padding = new Thickness(10, 0, 10, 0);
                _pad.MaxWidth = 292;
                _pad.MinWidth = 140;
                // 右对齐：读数胶囊贴着自己那一列的右边界，紧靠开关方便核对。
                _pad.HorizontalAlignment = HorizontalAlignment.Right;
                _pad.VerticalAlignment = VerticalAlignment.Center;
                _pad.IsHitTestVisible = false;
                _pad.Child = _read;
                Grid.SetColumn(_pad, 2);
                _grid.Children.Add(_pad);

                Grid zone = new Grid();
                zone.HorizontalAlignment = HorizontalAlignment.Stretch;
                zone.VerticalAlignment = VerticalAlignment.Center;
                zone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                zone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                zone.Children.Add(_state);
                Grid.SetColumn(_sw, 1);
                zone.Children.Add(_sw);
                Grid.SetColumn(zone, 4);
                _grid.Children.Add(zone);
            }

            private void BuildStacked(StackPanel mid, TextBlock desc, string risk, ActionRowSpec spec)
            {
                mid.Margin = new Thickness(12, 0, 12, 0);
                desc.Margin = new Thickness(0, 4, 0, 0);

                _read.Margin = new Thickness(0, 6, 0, 0);
                mid.Children.Add(_read);

                if (spec != null && !string.IsNullOrEmpty(spec.Note))
                {
                    // 撤销路径：不写"安全 / 可逆"，写清楚按哪里退回去。
                    TextBlock note = Mono(spec.Note, T.FsNano, risk == "A" ? T.Mute : T.Warn);
                    note.Margin = new Thickness(0, 5, 0, 0);
                    note.TextTrimming = TextTrimming.CharacterEllipsis;
                    note.TextWrapping = TextWrapping.NoWrap;
                    mid.Children.Add(note);
                }

                _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                _grid.ColumnDefinitions.Add(new ColumnDefinition());
                _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _grid.Margin = new Thickness(0, 11, 0, 11);

                Grid.SetColumn(mid, 1);
                _grid.Children.Add(mid);

                _state.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(_state, 2);
                _grid.Children.Add(_state);

                Grid.SetColumn(_sw, 3);
                _grid.Children.Add(_sw);
            }

            // ---- 判读 -------------------------------------------------------

            /// <summary>自己探针并重绘（单行用）。页面批量刷新时请用 Paint 传入已读到的状态。</summary>
            private void RefreshNow()
            {
                if (_core == null || Def == null) return;
                string cur0;
                AState st = _core.Engine.Probe(Def, out cur0);
                Paint(st, _core.Engine.CanWrite(Def), cur0);
            }

            /// <summary>用页面已经读到的状态重绘：开关位置与状态文字都来自真实读数。</summary>
            public void Paint(AState st, bool can) { Paint(st, can, null); }

            /// <summary>cur0 非 null 时复用探针已读到的首 Op 当前值，避免 PaintDelta 再开一次键。</summary>
            public void Paint(AState st, bool can, string cur0)
            {
                _hasPaint = true;
                _lastState = st;
                _lastCan = can;

                if (_sw != null)
                {
                    _sw.On = st == AState.Applied;
                    _sw.SetReadOnly(!can);
                    _sw.Opacity = can ? 1.0 : (st == AState.Applied ? 0.68 : T.DisabledOpacity);
                }

                // 只有"看不出原因"的状态才需要文字：
                //   已应用 → 开关就是答案；可写且未应用 → 开关关着就是答案。
                // 旧实现每行都写"已应用"，22 行就是 22 个重复词，把真正的异常（需管理员/拒绝访问）淹了。
                string text = QuietState(st, can);
                if (_state != null && _state.Text != text) _state.Text = text;
                if (_state != null) _state.Foreground = T.S(StateTint(st, can));

                if (_adminChip != null)
                {
                    Visibility v = can ? Visibility.Collapsed : Visibility.Visible;
                    if (_adminChip.Visibility != v)
                    {
                        _adminChip.Visibility = v;
                        if (_columns && _grid.ActualWidth > 0) Fit(_grid.ActualWidth);
                    }
                }

                if (_appliedRail != null)
                {
                    Visibility rv = st == AState.Applied ? Visibility.Visible : Visibility.Collapsed;
                    if (_appliedRail.Visibility != rv) _appliedRail.Visibility = rv;
                    if (st == AState.Applied && _bar != null && _risk == "A")
                        _bar.Background = CtlTint.Wash(T.Ok, 0x88);
                    else if (_bar != null && _risk == "A")
                        _bar.Background = CtlTint.Wash(T.Ok, 0x36);
                }

                PaintDelta(st, can, cur0);
            }

            /// <summary>Compact 收起「当前值」，只留「目标值」——窄窗时读数与说明不互相挤压。</summary>
            public void SetCompact(bool compact)
            {
                if (_compact == compact) return;
                _compact = compact;
                _painted = false;
                if (_hasPaint) Paint(_lastState, _lastCan);
            }

            private void PaintDelta(AState st, bool can, string cur0)
            {
                if (Def == null || Def.Ops == null || Def.Ops.Length == 0) return;
                Op op = Def.Ops[0];
                string cur = cur0 ?? Ops.Probe(op);
                string want = op.Truth;
                bool ok = st == AState.Applied;
                bool hit = Ops.Matches(op, cur);
                uint tone = StateTint(st, can);

                if (_pad != null && _padTone != (int)tone)
                {
                    _padTone = (int)tone;
                    _pad.BorderBrush = CtlTint.Wash(tone, ok ? (byte)0x56 : (byte)0x84);
                    _pad.Background = CtlTint.Wash(tone, ok ? (byte)0x16 : (byte)0x14);
                }

                // 稳态不动视觉树：读数没变就不重建 Inlines（每次刷新重建会产生一串字符串与 Run）。
                if (_painted && _toneShown == (int)tone && _curShown == cur && _wantShown == want) return;
                _painted = true;
                _toneShown = (int)tone;
                _curShown = cur;
                _wantShown = want;

                uint curTint = st == AState.Denied ? T.Bad : (hit ? (ok ? T.Ok : T.Dim) : T.Warn);

                _read.Inlines.Clear();
                if (st == AState.Applied) AddRun(_read, "\u2713 ", T.Ok);
                AddRun(_read, _opName, T.Mute);
                if (st == AState.Applied)
                {
                    if (!_compact) AddRun(_read, "已符合 · ", T.Ok);
                    AddRun(_read, want, _wantTint);
                }
                else
                {
                    if (!_compact) AddRun(_read, cur, curTint);
                    AddRun(_read, "  \u2192  ", T.Mute);
                    AddRun(_read, want, _wantTint);
                }
                if (_more != null) AddRun(_read, _more, T.Mute);
            }

            private static void AddRun(TextBlock t, string s, uint col)
            {
                Run run = new Run(s);
                run.Foreground = T.S(col);
                t.Inlines.Add(run);
            }

            // ---- 单一写入点 --------------------------------------------------

            private void OnToggle(object sender, EventArgs e)
            {
                if (_sw == null) return;
                Commit(_sw.On);
            }

            private void OnBlocked(object sender, EventArgs e)
            {
                Blocked();
            }

            /// <summary>整行可点：开关之外的位置点一下等于切一次，但同样只走 Commit。</summary>
            private void ClickRow()
            {
                if (_core == null || Def == null) return;
                if (!_core.Engine.CanWrite(Def)) { Blocked(); return; }
                bool want = !_sw.On;
                _sw.On = want;
                Commit(want);
            }

            private void Blocked()
            {
                Say("需要管理员权限：点左下角状态行提权重启", false);
                Diag.Hit("ui.actionrow.blocked");
            }

            /// <summary>
            /// 唯一的写入路径。成功后重新探针；失败同样重新探针——开关回到的是系统的
            /// 真实状态，不是用户的点击态。失败必留 toast + Diag，不静默。
            /// </summary>
            private void Commit(bool want)
            {
                if (_core == null || Def == null) return;
                ActionDef a = Def;

                if (!_core.Engine.CanWrite(a))
                {
                    Blocked();
                    RefreshNow();
                    return;
                }

                ApplyReport rep;
                try
                {
                    rep = want ? ApplyNow(a) : RevertNow(a);
                }
                catch (Exception ex)
                {
                    Diag.Fail("ui.actionrow.commit", ex);
                    Say(a.Name + (want ? " · 应用失败：" : " · 还原失败：") + ex.Message, false);
                    RefreshNow();
                    FireCommitted();
                    return;
                }

                if (rep.Ok)
                {
                    if (!want && rep.Applied == 0)
                        Say("该项并非通过本工具修改（账本无记录），未改动系统设置", false);
                    else
                        Say(a.Name + (want ? " · 已应用" : " · 已还原"), true);
                }
                else
                {
                    string line = FirstFail(rep);
                    Say(a.Name + (want ? " · 应用失败：" : " · 还原失败：") + line, false);
                    Diag.Fail("ui.actionrow.commit", new InvalidOperationException(line));
                }

                RefreshNow();
                FireCommitted();
            }

            private ApplyReport ApplyNow(ActionDef a)
            {
                if (_spec != null && _spec.ApplyFn != null) return _spec.ApplyFn(a);
                return _core.Engine.Apply(a);
            }

            private ApplyReport RevertNow(ActionDef a)
            {
                if (_spec != null && _spec.RevertFn != null) return _spec.RevertFn(a);
                return Revert(_core, a);
            }

            private void FireCommitted()
            {
                if (_spec != null && _spec.Committed != null) _spec.Committed(this, EventArgs.Empty);
            }

            /// <summary>还原本动作在账本里的全部记录：逆序（后写的先还原），逐条走 Engine.RevertEntry。</summary>
            private static ApplyReport Revert(Core c, ActionDef a)
            {
                ApplyReport total = new ApplyReport();
                total.Ok = true;
                IList<LedgerEntry> entries = c.Engine.Ledger.Entries;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    LedgerEntry e = entries[i];
                    if (e.ActionId != a.Id) continue;
                    ApplyReport r = c.Engine.RevertEntry(e.Id);
                    total.Applied += r.Applied;
                    total.Failed += r.Failed;
                    if (r.Ok) continue;
                    total.Ok = false;
                    for (int k = 0; k < r.Lines.Count; k++) total.Lines.Add(r.Lines[k]);
                }
                return total;
            }

            /// <summary>
            /// 失败要说三件事：为什么、系统现在是什么状态、要不要人管。
            /// 只回一句"失败"等于让用户自己去还原中心核对——那正是这轮重构要消灭的体验。
            /// </summary>
            private static string FirstFail(ApplyReport r)
            {
                if (r == null) return "失败";
                string head = string.IsNullOrEmpty(r.Reason) ? null : r.Reason;
                if (head == null)
                {
                    for (int i = 0; i < r.Lines.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(r.Lines[i])) { head = r.Lines[i]; break; }
                    }
                }
                if (head == null) head = "失败";

                if (r.RollbackFailed > 0) return head + " · " + r.RollbackFailed + " 项回滚失败，需手动还原";
                if (r.RolledBack > 0) return head + " · 已回滚 " + r.RolledBack + " 项";
                if (r.Committed > 0) return head + " · 仍有 " + r.Committed + " 项留在系统里";
                return head;
            }

            private void Say(string msg, bool ok)
            {
                _lastFeedback = msg;
                _lastFeedbackOk = ok;
                if (_core != null && _core.Shell != null) _core.Shell.Toast(msg, ok);
            }

            // ---- 状态语言（三个页面同一套说法）--------------------------------

            /// <summary>安静版状态文字：没有额外信息就返回空串。</summary>
            private static string QuietState(AState st, bool can)
            {
                if (st == AState.Applied) return "";
                if (st == AState.Denied) return "拒绝访问";
                if (st == AState.Partial) return "部分应用";
                if (!can) return "需管理员";
                return "";
            }

            private static string StateText(AState st, bool can)
            {
                // 顺序有意义：已经达成的状态比"能不能写"更该被读到——不然后台是一排
                // "需管理员"，读者再也看不出哪些已经生效。权限只在"还没生效"时才是答案。
                if (st == AState.Applied) return "已应用";
                if (st == AState.Denied) return "拒绝访问";
                if (st == AState.Partial) return "部分应用";
                if (!can) return "需管理员";
                return "未应用";
            }

            private static uint StateTint(AState st, bool can)
            {
                if (st == AState.Applied) return T.Ok;
                if (st == AState.Denied) return T.Bad;
                if (st == AState.Partial) return T.Warn;
                if (!can) return T.Warn;
                return T.Dim;
            }

            // ---- 列宽分档自适应 --------------------------------------------

            private void Fit(double w)
            {
                if (!_columns || _pad == null || w <= 0) return;
                double read, air, zoneW;
                if (w >= 1120) { read = 300; air = 12; zoneW = 132; }
                else if (w >= 960) { read = 264; air = 10; zoneW = 128; }
                else if (w >= 820) { read = 228; air = 6; zoneW = 124; }
                else { read = 188; air = 4; zoneW = 118; }

                if (_readCol.Width.Value != read) _readCol.Width = new GridLength(read);
                if (_airCol.Width.Value != air) _airCol.Width = new GridLength(air);
                if (_zoneCol != null && _zoneCol.Width.Value != zoneW) _zoneCol.Width = new GridLength(zoneW);

                double max = read - 6;
                if (_pad.MaxWidth != max) _pad.MaxWidth = max;
                double min = Math.Max(80, read - 130);
                if (_pad.MinWidth != min) _pad.MinWidth = min;

                // 左侧 StackPanel 是横向的，按真实胶囊占用宽度计算名称上限，
                // 避免"需管理员"/批量徽标与右侧 Cur -> Expect 互相挤压。
                double chipW = (_chips != null && _chips.ActualWidth > 0) ? (_chips.ActualWidth + 6) : _chipW;
                if ((_chips == null || _chips.ActualWidth <= 0) && _adminChip != null && _adminChip.Visibility != Visibility.Visible && Def != null && Def.NeedsAdmin)
                    chipW = Math.Max(0, chipW - 68);

                double midW = w - 3 - read - air - zoneW - 28;
                double nameW = midW - chipW;
                if (nameW < 72) nameW = 72;
                if (_name.MaxWidth != nameW) _name.MaxWidth = nameW;

                if (_desc != null)
                {
                    double descW = Math.Max(120, midW);
                    if (_desc.MaxWidth != descW) _desc.MaxWidth = descW;
                }
            }

            private static string ReadableOpName(Op op)
            {
                if (op == null) return "";
                if (op.Kind == "RegKv" && !string.IsNullOrEmpty(op.KvKey)) return op.KvKey;
                string n = op.Name ?? "";
                if (op.Kind == "PowerAc")
                {
                    if (string.Equals(n, "0cc5b647-c1df-4637-891a-dec35c318583", StringComparison.OrdinalIgnoreCase)) return "CPMINCORES";
                    if (string.Equals(n, "893dee8e-2bef-41e0-89c6-b55d0929964c", StringComparison.OrdinalIgnoreCase)) return "PROCTHROTTLEMIN";
                    if (string.Equals(n, "ee12f906-d277-404b-b6da-e5fa1a576df5", StringComparison.OrdinalIgnoreCase)) return "PCIE·ASPM";
                    if (string.Equals(n, "6738e2c4-e8a5-4a42-b16a-e040e769756e", StringComparison.OrdinalIgnoreCase)) return "DISKIDLE";
                    if (string.Equals(n, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", StringComparison.OrdinalIgnoreCase)) return "USB·SUSPEND";
                }
                return n;
            }
        }

        /// <summary>构建一个动作行。页面只负责把它挂进布局，并保留返回的 Refresh。</summary>
        public static ActionRow Row(ActionRowSpec spec)
        {
            return new ActionRow(spec);
        }
    }
}