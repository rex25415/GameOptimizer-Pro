// ============================================================================
//  Memory.Readout.cs — 04 内存与提交：一个 Snapshot → 各面板读数（Memory 分片）
//  为什么切：哪些面板吃同一个量、缺测时各自显示什么，是这一页最容易出错的地方；
//  把"取值 → 分发"放在一起，才能逐条对照面板字段。
//
//  Rev86：原来的 Apply() 133 行、七个面板混在一段里。现在它只做三件事——
//  归一化输入（总量/可用/待机/已修改/提交）、决定"有没有测到"、按面板分发。
//  每个写入口自己不超过 25 行，缺测分支集中在 ApplyEmpty 一处。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;

namespace Go.Pages
{
    public partial class Memory : Page
    {
        // 面板读数与注脚的最后一次刻度：变了才重写文本（稳态 0 次字符串分配）。
        private int _poolPagedKey = int.MinValue, _poolNonpagedKey = int.MinValue, _poolMetaKey = int.MinValue;
        private long _commitKey = long.MinValue, _verdictKey = long.MinValue;

        // ====================================================================
        //  读数
        // ====================================================================
        private void Apply(Snapshot s)
        {
            if (_cellUsed == null) return;

            if (s == null || s.MemTotalMB <= 0)
            {
                ApplyEmpty();
                return;
            }

            double total = s.MemTotalMB;
            double avail = Math.Max(0, Math.Min(s.MemAvailMB, total));

            // NtQuerySystemInformation(80) 失败时待机与已修改都留在 0。一个正在跑的 Windows
            // 不可能两者同时为 0（待机列表至少装着缓存页），所以"两个都 0"= 这条查询没成，
            // 而不是"这台机器没有缓存"。缺测就显示 —，不拿 0 冒充测量结论。
            bool memListOk = s.MemStandbyMB > 0 || s.MemModifiedMB > 0;
            double standby = memListOk ? Math.Min(s.MemStandbyMB, avail) : 0;
            double modified = memListOk ? Math.Max(0, Math.Min(s.MemModifiedMB, Math.Max(0, total - avail))) : 0;
            double used = Math.Max(0, total - avail - modified);   // 已用不含已修改（页在写回前仍算已改）
            double free = Math.Max(0, avail - standby);

            bool cmtOk = s.CommitLimitMB > 0 && s.CommitTotalMB > 0;
            double ratio = cmtOk ? s.CommitTotalMB / s.CommitLimitMB : 0;
            double head = cmtOk ? Math.Max(0, s.CommitLimitMB - s.CommitTotalMB) : 0;

            ApplyBand(total, used, avail, standby, modified, memListOk);
            ApplyRibbon(total, used, standby, modified, free, memListOk);
            ApplyVerdict(cmtOk, ratio, head, s.CommitTotalMB, s.CommitLimitMB);
            ApplyKernel(s);
            ApplyCommit(cmtOk, ratio, head, s.CommitTotalMB, s.CommitLimitMB);
            SetPurgeChip(C, memListOk ? standby : -1);
            PushTrends(total, used, avail, standby, modified, memListOk);
        }

        // ---- 内核池 ---------------------------------------------------------
        private void ApplyKernel(Snapshot s)
        {
            bool ok = s.KernelPagedMB > 0 || s.KernelNonpagedMB > 0;
            double paged = s.KernelPagedMB;
            double nonpaged = s.KernelNonpagedMB;
            double scale = PoolScale(Math.Max(paged, nonpaged));

            int pk = ok ? (int)Math.Round(paged) : int.MinValue;
            if (pk != _poolPagedKey)
            {
                _poolPagedKey = pk;
                _poolPagedVal.Text = ok ? Mb(paged) : "\u2014";
            }
            int nk = ok ? (int)Math.Round(nonpaged) : int.MinValue;
            if (nk != _poolNonpagedKey)
            {
                _poolNonpagedKey = nk;
                _poolNonpagedVal.Text = ok ? Mb(nonpaged) : "\u2014";
            }
            SetBar(_poolPagedBar, ok, ok && scale > 0 ? paged / scale : 0);
            SetBar(_poolNonpagedBar, ok, ok && scale > 0 ? nonpaged / scale : 0);

            int mk = ok ? (int)Math.Round(scale) * 31 + (int)Math.Round(paged + nonpaged) : int.MinValue;
            if (mk != _poolMetaKey)
            {
                _poolMetaKey = mk;
                _poolMeta.Text = ok
                    ? ("合计 " + Mb(paged + nonpaged) + " · 刻度 0\u2013" + Group(scale) + " MB")
                    : "PERF INFO · 未测量";
            }
        }

        private static void ResetCell(MemCell c, string note)
        {
            c.ValKey = int.MinValue;
            c.NoteKey = long.MinValue;
            c.Val.Text = "\u2014";
            c.Note.Text = note;
        }

        private static void SetBar(MeterBar bar, bool known, double value)
        {
            if (bar == null) return;
            bar.Known = known;
            bar.Value = value;
        }

        // ---- 提交与页面文件 -------------------------------------------------
        private void ApplyCommit(bool cmtOk, double ratio, double head, double commitTotal, double commitLimit)
        {
            uint col = !cmtOk ? T.Mute : (ratio >= 0.90 ? T.Bad : (ratio >= 0.75 ? T.Warn : T.Hi));
            if (_ruler != null)
            {
                _ruler.Known = cmtOk;
                _ruler.Value = cmtOk ? ratio : 0;
                _ruler.Tint = !cmtOk ? T.Accent : (ratio >= 0.90 ? T.Bad : (ratio >= 0.75 ? T.Warn : T.Accent));
            }

            long key = cmtOk
                ? (((long)Math.Round(commitTotal / 102.4) << 24) ^ (long)Math.Round(commitLimit / 102.4))
                : -1L;
            if (key == _commitKey) return;
            _commitKey = key;
            int pct = cmtOk ? (int)Math.Round(ratio * 100.0) : 0;
            _cmtLine.Text = cmtOk ? (Gb(commitTotal) + " / " + Gb(commitLimit) + " GB (" + pct + "%)") : "\u2014 / \u2014 GB";
            _cmtLine.Foreground = T.S(col);
            _cmtHead.Text = cmtOk ? "距上限 " + Gb(head) + " GB" : "距上限 \u2014 GB";
        }

        // ---- 空态：一帧都没采到 ---------------------------------------------
        private void ApplyEmpty()
        {
            // 空态直接写 —，并把"上次显示刻度"归零：数据回来时即使刻度相同也必须重写，
            // 否则读者会一直看着一个 —。
            ResetCell(_cellUsed, "等待采样");
            ResetCell(_cellAvail, "等待采样");
            ResetCell(_cellStby, "等待采样");
            ResetCell(_cellMod, "等待采样");
            _cellUsed.Delta.Set(0, "", false);
            _cellAvail.Delta.Set(0, "", true);
            _cellStby.Delta.Set(0, "", true);
            _cellMod.Delta.Set(0, "", false);
            _cellUsed.Bar.Opacity = 0.35;
            _cellAvail.Bar.Opacity = 0.35;
            _cellStby.Bar.Opacity = 0.35;
            _cellMod.Bar.Opacity = 0.35;

            ClearRibbon("等待采样 · 采样线程尚未发布第一帧");

            _verdictKey = long.MinValue;
            ApplyVerdict(false, 0, 0, 0, 0);

            _poolPagedKey = int.MinValue;
            _poolNonpagedKey = int.MinValue;
            _poolMetaKey = int.MinValue;
            _poolPagedVal.Text = "\u2014";
            _poolNonpagedVal.Text = "\u2014";
            SetBar(_poolPagedBar, false, 0);
            SetBar(_poolNonpagedBar, false, 0);
            _poolMeta.Text = "PERF INFO · 未测量";

            _commitKey = long.MinValue;
            _cmtLine.Text = "\u2014 / \u2014 GB";
            _cmtHead.Text = "距上限 \u2014 GB";
            if (_ruler != null) { _ruler.Known = false; _ruler.Value = 0; }
            SetPurgeChip(C, -1);
        }
    }
}
