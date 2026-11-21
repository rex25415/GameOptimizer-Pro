// ============================================================================
//  Graphics.Actions.cs — 本页动作执行与结果回显（Graphics 分片）
//  行本身由 Ui.ActionRow 统一构建与驱动：判读 / 写入账本 / 失败回滚 / 状态语言
//  只有那一份实现。本页只保留图形栈特有的东西：「重启生效」标记，
//  以及动作提交后呈现管线的即时复判（RefreshPipeline）。
//  来源：Graphics.cs（1204 行单体）按职责拆分，行为零变化。
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
        // 共享行的句柄：本页只读它判完的状态，不碰开关与读数。
        private readonly List<Ui.ActionRow> _actRows = new List<Ui.ActionRow>();

        // ---- 本页动作 ------------------------------------------------------
        private UIElement BuildActions(Core c)
        {
            // 同 Tuning：Page 实例复用，重建时列表必须归零，否则行与探针次数都会翻倍累积。
            _actRows.Clear();
            List<ActionDef> items = c.Engine.GroupOf("图形栈");
            StackPanel body = new StackPanel();
            _actionMeta = Ui.Mono("—", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("本页动作", "GRAPHICS ACTIONS · REVERSIBLE", _actionMeta));
            body.Children.Add(Gap(12));

            if (items.Count == 0)
            {
                body.Children.Add(Ui.Tx("当前没有可用的图形栈动作。", T.FsSmall, T.Mute, T.FCn));
            }
            else
            {
                bool first = true;
                for (int i = 0; i < items.Count; i++)
                {
                    if (!first) body.Children.Add(Ui.Rule(T.LineSoft, 0));
                    body.Children.Add(BuildRow(c, items[i]));
                    first = false;
                }
            }

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));

            TextBlock foot = Ui.Tx("切换即写入并落账；随后按注册表真实值复判开关，失败时开关会退回原状态。", T.FsMicro, T.Mute, T.FCn);
            foot.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(foot);

            return Ui.Panel(body, 18);
        }

        private UIElement BuildRow(Core c, ActionDef a)
        {
            Ui.ActionRowSpec spec = new Ui.ActionRowSpec();
            spec.Core = c;
            spec.Def = a;
            spec.Kind = Ui.ActionRowKind.Stacked;
            spec.Chip = a.Restart ? "重启生效" : null;
            spec.WantTint = T.Ok;
            // 动作改变了 HwSchMode / OverlayTestMode，呈现管线的结论必须同帧重判。
            spec.Committed = delegate { RefreshActions(false); RefreshPipeline(); };
            Ui.ActionRow row = Ui.Row(spec);
            _actRows.Add(row);
            return row.Root;
        }

        private void RefreshActions() { RefreshActions(false); }

        private void RefreshActions(bool refreshPowerAsync)
        {
            if (C == null) return;
            int applied = 0, total = 0;
            bool needPowerRefresh = false;

            for (int i = 0; i < _actRows.Count; i++)
            {
                Ui.ActionRow r = _actRows[i];
                total++;
                string cur0;
                // 使用非阻塞 ProbeForUi：PowerAc 走后台缓存快照，不在 UI 线程派生 powercfg.exe
                AState st = C.Engine.ProbeForUi(r.Def, out cur0);
                bool can = C.Engine.CanWrite(r.Def);
                r.Paint(st, can, cur0);
                if (st == AState.Applied) applied++;
                if (HasPowerAc(r.Def) && (refreshPowerAsync || cur0 == Ops.PendingText))
                    needPowerRefresh = true;
            }

            if (_actionMeta != null)
            {
                _actionMeta.Text = total == 0
                    ? "NO ACTIONS"
                    : applied + "/" + total + " APPLIED · LEDGER " + C.Engine.PendingCount;
            }

            if (needPowerRefresh)
            {
                int bgen = _buildGen;
                Dispatcher ui = Dispatcher.CurrentDispatcher;
                Core coreRef = C;
                coreRef.Engine.RefreshPowerAcCacheAsync(delegate
                {
                    if (bgen != _buildGen || _left || ui.HasShutdownStarted || ui.HasShutdownFinished) return;
                    try
                    {
                        ui.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                        {
                            if (bgen != _buildGen || _left || C == null || _actRows.Count == 0) return;
                            RefreshActions(false);
                        });
                    }
                    catch (Exception ex) { Diag.Fail("gfx.actions.powercb", ex); }
                });
            }
        }

        private static bool HasPowerAc(ActionDef a)
        {
            if (a == null || a.Ops == null) return false;
            for (int i = 0; i < a.Ops.Length; i++)
            {
                if (a.Ops[i] != null && string.Equals(a.Ops[i].Kind, "PowerAc", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ---- 自适应 --------------------------------------------------------
        /// <summary>断点回调：只切换可见性，不碰读数与动作。</summary>
        private void ApplyBp(Bp bp)
        {
            bool compact = bp == Bp.Compact;

            // 页头 SCAN 时间属次要读数，Compact 下让位给标题与「重新探测」。
            if (_headMeta != null)
                _headMeta.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

            // 动作行：Compact 只显示目标值。
            for (int i = 0; i < _actRows.Count; i++) _actRows[i].SetCompact(compact);
        }
    }
}
