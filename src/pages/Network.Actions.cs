// ============================================================================
//  Network.Actions.cs — 本页动作行、开关与撤销路径（Network 分片）
//  行本身由 Ui.ActionRow 统一构建与驱动：判读 / 写入账本 / 失败回滚 / 状态语言
//  只有那一份实现。本页只保留网络页特有的两样东西：
//    · 撤销路径（Note）：不写"安全 / 可逆"，写清楚按哪里退回去；
//    · 提交后重读 TCP 审计表与页头读数（RefreshTable / UpdateHead）。
//  开关状态由探针实时回读，不靠开关自己的视觉记忆。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Network : Page
    {
        private const string NetGroup = "网络";

        private readonly List<Ui.ActionRow> _actRows = new List<Ui.ActionRow>();
        private TextBlock _actionSummary;

        private UIElement BuildActions(Core c)
        {
            // 同 Tuning：Page 实例复用，重建时列表必须归零。
            _actRows.Clear();
            List<ActionDef> items = c.Engine.GroupOf(NetGroup);

            StackPanel body = new StackPanel();
            _actionSummary = Ui.Mono("—", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("网络动作", "NETWORK ACTIONS · EVERY WRITE IS LEDGERED", _actionSummary));
            body.Children.Add(Gap(12));

            if (items.Count == 0)
            {
                body.Children.Add(Ui.Tx("动作目录中未找到「网络」分组。", T.FsSmall, T.Mute, T.FCn));
            }

            bool first = true;
            for (int i = 0; i < items.Count; i++)
            {
                if (!first) body.Children.Add(Ui.Rule(T.LineSoft, 0));
                body.Children.Add(BuildRow(c, items[i]));
                first = false;
            }

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));

            TextBlock foot = Ui.Tx("切换即写入并立即回读校验；失败时开关退回原状态，账本里仍留着这次尝试的原值。协议栈参数表在适配器行的展开里。",
                T.FsMicro, T.Mute, T.FCn);
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
            spec.Chip = a.Restart ? "RESTART" : null;
            spec.Note = UndoPath(a);
            spec.SwitchTip = "切换即应用 / 还原；状态每 5 s 由注册表回读复判一次，不以开关外观为准。";
            // 写入会改变协议栈参数：提交后立刻重读（展开着的那张表），不等下一个 5 s。
            spec.Committed = delegate { RefreshTable(); RefreshActions(); UpdateHead(); };
            Ui.ActionRow row = Ui.Row(spec);
            _actRows.Add(row);
            return row.Root;
        }

        /// <summary>撤销路径写死在行里：A 档切回开关；B/C 档同时给出重启与还原中心两条路。</summary>
        private static string UndoPath(ActionDef a)
        {
            if (a.Risk == "A" && !a.Restart)
                return "撤销 · 关闭开关即回写原值（原值已在写入前落账，还原中心可逐条回放）";
            return "撤销 · 还原中心 → 本动作；标记 RESTART 的项要重启才完全复原";
        }

        private void RefreshActions()
        {
            if (C == null) return;
            int applied = 0;
            int total = 0;
            for (int i = 0; i < _actRows.Count; i++)
            {
                Ui.ActionRow r = _actRows[i];
                total++;
                string cur0;
                AState st = C.Engine.ProbeForUi(r.Def, out cur0);
                bool can = C.Engine.CanWrite(r.Def);
                r.Paint(st, can, cur0);
                if (st == AState.Applied) applied++;
            }
            if (_actionSummary != null)
                _actionSummary.Text = "已应用 " + applied + "/" + total + " · 账本 " + C.Engine.PendingCount;
        }

        /// <summary>Compact 收起行内"当前值"，只留目标值——与图形页同一处理。</summary>
        private void ApplyActionBp(bool compact)
        {
            for (int i = 0; i < _actRows.Count; i++) _actRows[i].SetCompact(compact);
        }
    }
}