// ============================================================================
//  Engine.cs — 动作执行引擎：探针 / 应用 / 还原 / 统计
//
//  Rev86：引擎从「把每个 Op 跑一遍、数成功数」改成带事务边界的编排：
//   · 单个动作内任一 Op 失败 → 立刻停在最后一个已提交步，并把本动作已经写下去的
//     部分真正回滚（旧实现会把剩下的 Op 继续跑完，系统停在半改状态）；
//   · 批量应用同一事务：任一动作失败 → 回滚本次调用已经提交的全部改动；
//   · 回滚本身失败会被单独统计并写进报告（而不是被一句「部分失败」盖住）；
//   · ApplyReport 多出 Reason / Detail / Committed / RolledBack / RollbackFailed，
//     页面可以直接显示原因，不再只剩一个「失败(1)」。
// ============================================================================
using System;
using System.Collections.Generic;

namespace Go
{
    public class ApplyReport
    {
        public bool Ok;
        /// <summary>成功单位数：应用 = 生效且保留的 Op 数；还原 = 成功还原的记录数。</summary>
        public int Applied;
        public int Failed;
        public List<string> Lines = new List<string>();
        /// <summary>首要原因（人类可读）。失败时页面直接显示这一句。</summary>
        public string Reason;
        /// <summary>首要原因的技术细节（异常类型、回读值、命令输出）。</summary>
        public string Detail;
        /// <summary>本次调用结束后仍留在系统里、可回滚的改动数。</summary>
        public int Committed;
        /// <summary>因事务边界被回滚掉的改动数。</summary>
        public int RolledBack;
        /// <summary>回滚失败、仍需人工处理的改动数（记录会留在账本里）。</summary>
        public int RollbackFailed;
        /// <summary>失败停在哪一步（探测 / 记录 / 写入 / 校验 / 还原）。</summary>
        public string StoppedAt;
        /// <summary>失败时已完成的步数（0..4）——「停在最后一个已提交步」的那个数字。</summary>
        public int StoppedAfter;
        /// <summary>本次调用写下的账本记录（内部用：批量事务回滚要精确知道「哪些是我写的」）。</summary>
        internal List<LedgerEntry> Touched = new List<LedgerEntry>();
    }

    public class Engine
    {
        public readonly Ledger Ledger = new Ledger();
        public readonly List<ActionDef> Actions = Catalog.Build();
        private readonly Dictionary<string, ActionDef> _byId;
        private readonly List<string> _groupNames;
        private readonly Dictionary<string, List<ActionDef>> _byGroup;
        public bool Elevated;
        /// <summary>
        /// 由 Core 注入。账本是唯一真源，但它自己不知道界面在听——
        /// 于是「应用了一个动作」这件事必须从这里广播出去，
        /// 否则页头的偏离度、还原中心的计数都只能等下一次 1Hz 拍才追上。
        /// </summary>
        public AppState State;

        public Engine()
        {
            _byId = new Dictionary<string, ActionDef>(Actions.Count, StringComparer.Ordinal);
            _groupNames = new List<string>(8);
            _byGroup = new Dictionary<string, List<ActionDef>>(8, StringComparer.Ordinal);
            for (int i = 0; i < Actions.Count; i++)
            {
                ActionDef a = Actions[i];
                if (a == null) continue;
                if (!string.IsNullOrEmpty(a.Id) && !_byId.ContainsKey(a.Id)) _byId[a.Id] = a;
                string g = a.Group ?? "";
                List<ActionDef> bucket;
                if (!_byGroup.TryGetValue(g, out bucket))
                {
                    bucket = new List<ActionDef>(8);
                    _byGroup[g] = bucket;
                    _groupNames.Add(g);
                }
                bucket.Add(a);
            }
            RegisterPowerAcOps();
        }

        private void RegisterPowerAcOps()
        {
            for (int i = 0; i < Actions.Count; i++)
            {
                Op[] ops = Actions[i].Ops;
                if (ops == null) continue;
                for (int k = 0; k < ops.Length; k++)
                {
                    if (ops[k] != null && ops[k].Kind == "PowerAc")
                        Ops.RegisterPowerAcOp(ops[k]);
                }
            }
        }

        public void RefreshPowerAcCacheAsync(Action onUpdated)
        {
            RegisterPowerAcOps();
            Ops.RefreshPowerAcCacheAsync(onUpdated != null ? onUpdated : NotifyPowerCacheUpdated);
        }

        private void NotifyPowerCacheUpdated()
        {
            AppState s = State;
            if (s != null) s.Post(Dirty.Power);
        }

        public ActionDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            ActionDef a;
            return _byId.TryGetValue(id, out a) ? a : null;
        }

        public List<string> Groups()
        {
            return new List<string>(_groupNames);
        }

        public List<ActionDef> GroupOf(string group)
        {
            List<ActionDef> bucket;
            return _byGroup.TryGetValue(group ?? "", out bucket)
                ? new List<ActionDef>(bucket)
                : new List<ActionDef>();
        }

        // ---- 状态探针 ------------------------------------------------------
        public AState Probe(ActionDef a)
        {
            string firstCurrent;
            return Probe(a, out firstCurrent);
        }

        /// <summary>探针并顺带输出首个 Op 的当前读数：动作行画「当前值→目标值」时复用，
        /// 不必为同一键再开一次注册表（旧实现 Probe 读一遍、PaintDelta 又读一遍）。</summary>
        public AState Probe(ActionDef a, out string firstCurrent)
        {
            firstCurrent = null;
            if (a == null || a.Ops == null || a.Ops.Length == 0) return AState.NotApplied;
            int total = a.Ops.Length;
            int hit = 0;
            for (int i = 0; i < total; i++)
            {
                Op op = a.Ops[i];
                string cur = Ops.Probe(op);
                if (i == 0) firstCurrent = cur;
                if (cur == Ops.DeniedText) return AState.Denied;
                if (Ops.Matches(op, cur)) hit++;
            }
            if (hit == total) return AState.Applied;
            if (hit == 0) return AState.NotApplied;
            return AState.Partial;
        }

        /// <summary>非阻塞 UI 探针：PowerAc 只读已缓存快照，未就绪时触发一次后台刷新而不卡 UI 线程。</summary>
        public AState ProbeForUi(ActionDef a)
        {
            string firstCurrent;
            return ProbeForUi(a, out firstCurrent);
        }

        public AState ProbeForUi(ActionDef a, out string firstCurrent)
        {
            firstCurrent = null;
            if (a == null || a.Ops == null || a.Ops.Length == 0) return AState.NotApplied;
            int total = a.Ops.Length;
            int hit = 0;
            for (int i = 0; i < total; i++)
            {
                Op op = a.Ops[i];
                string cur = Ops.ProbeForUi(op, NotifyPowerCacheUpdated);
                if (i == 0) firstCurrent = cur;
                if (cur == Ops.DeniedText) return AState.Denied;
                if (cur != Ops.PendingText && Ops.Matches(op, cur)) hit++;
            }
            if (hit == total) return AState.Applied;
            if (hit == 0) return AState.NotApplied;
            return AState.Partial;
        }

        public bool CanWrite(ActionDef a)
        {
            if (a == null || !a.NeedsAdmin) return true;
            return Elevated;
        }

        // ---- 应用 ----------------------------------------------------------
        /// <summary>应用一个动作。批内任一 Op 失败即停，并把本动作已提交的部分回滚。</summary>
        public ApplyReport Apply(ActionDef a)
        {
            ApplyReport rep = new ApplyReport();
            if (a == null || a.Ops == null || a.Ops.Length == 0)
            {
                rep.Ok = true;
                rep.Lines.Add("没有需要执行的原子操作");
                return rep;
            }
            if (!CanWrite(a))
            {
                rep.Ok = false;
                rep.Failed = a.Ops.Length;
                rep.Reason = "需要管理员权限";
                rep.Lines.Add("需要管理员权限");
                return rep;
            }
            List<Ops.OpJob> jobs = new List<Ops.OpJob>(a.Ops.Length);
            for (int i = 0; i < a.Ops.Length; i++) jobs.Add(new Ops.OpJob(a.Ops[i], a.Id, a.Name, 0));
            return MapBatch(Ops.ApplyBatch(jobs, Ledger), rep);
        }

        /// <summary>
        /// 批量应用 = **一个事务**：把全部动作的 op 拍平后一次探测、一次记账落盘、
        /// 逐个写入校验；任一失败就把本次调用已经提交的改动全部逆序回滚。
        ///
        /// 旧实现是"每个 op 一次 fsync"：8 动作预设（13 个 op）在 UI 线程上要付
        /// 13 次 FlushFileBuffers + 13 次 File.Replace + 13 次整份 JSON 重序列化。
        /// 现在整批只付一次 —— 而"先落盘再写系统"的约束没有被放宽（见 Ops.ApplyBatch）。
        /// </summary>
        public ApplyReport ApplyMany(List<ActionDef> list)
        {
            ApplyReport total = new ApplyReport();
            total.Ok = true;
            if (list == null || list.Count == 0)
            {
                total.Lines.Add("没有需要应用的动作");
                return total;
            }

            List<ActionDef> acts = new List<ActionDef>();
            List<Ops.OpJob> jobs = new List<Ops.OpJob>();
            for (int i = 0; i < list.Count; i++)
            {
                ActionDef a = list[i];
                if (a == null || a.Ops == null || a.Ops.Length == 0) continue;
                if (!CanWrite(a))
                {
                    // 批量 = 一个事务：有一条动作没权限就整批不写。
                    // 旧实现会在"前一条失败后"把已经写下的回滚；这里在探测阶段就中止，
                    // 系统一个字节都不会被改——比"先写一半再回滚"更干净。
                    total.Ok = false;
                    total.Failed = a.Ops.Length;
                    total.Reason = Prefix(a, "需要管理员权限");
                    total.Lines.Add("FAIL " + total.Reason);
                    return total;
                }
                int idx = acts.Count;
                acts.Add(a);
                for (int k = 0; k < a.Ops.Length; k++) jobs.Add(new Ops.OpJob(a.Ops[k], a.Id, a.Name, idx));
            }
            if (jobs.Count == 0) return total;

            Ops.BatchOutcome b = Ops.ApplyBatch(jobs, Ledger);
            MapBatch(b, total);
            if (!b.Ok && acts.Count > 0)
            {
                int jobIndex = b.FailedIndex;
                int actionIndex = jobIndex >= 0 && jobIndex < jobs.Count ? jobs[jobIndex].ActionIndex : 0;
                if (actionIndex >= 0 && actionIndex < acts.Count)
                {
                    total.Reason = Prefix(acts[actionIndex], b.Reason == null ? "应用失败" : b.Reason);
                    total.Lines.Insert(0, "FAIL " + total.Reason);
                }
                Ledger.AppendAudit("BATCH-STOP " + total.Reason
                                   + " :: rolled=" + b.RolledBack + " rollfail=" + b.RollbackFailed);
            }
            total.Ok = b.Ok && total.Failed == 0;
            return total;
        }

        /// <summary>把批量结局翻译成给页面/托盘看的 ApplyReport。措辞保持 Rev86 的形状。</summary>
        private ApplyReport MapBatch(Ops.BatchOutcome b, ApplyReport rep)
        {
            rep.Ok = b.Ok;
            rep.Applied = b.Applied;
            rep.Committed = b.Ok ? b.Touched.Count : 0;
            rep.RolledBack = b.RolledBack;
            rep.RollbackFailed = b.RollbackFailed;
            if (!b.Ok)
            {
                rep.Failed = b.FailedIndex >= 0 ? 1 : 0;
                rep.Reason = b.Reason;
                rep.Detail = b.Detail;
                rep.StoppedAt = b.Step;
                rep.StoppedAfter = b.Committed;
            }
            for (int i = 0; i < b.Lines.Count; i++) rep.Lines.Add(b.Lines[i]);
            for (int i = 0; i < b.Results.Count; i++)
            {
                OpResult r = b.Results[i];
                if (r == null || !r.Ok) continue;
                rep.Lines.Add("OK  " + r.Message);
            }
            if (b.Ok && b.Applied > 0 && State != null) State.Post(Dirty.Ledger);
            return rep;
        }

        // ---- 还原 ----------------------------------------------------------
        public ApplyReport RevertAll()
        {
            ApplyReport rep = new ApplyReport();
            if (Ledger.Unreadable)
            {
                // 磁盘上的账本读不出来 ≠ 没有需要还原的项目。这条分支必须报失败并说清原因，
                // 否则用户会以为系统是干净的，实际一条写前记录都没读到。
                rep.Ok = false;
                rep.Reason = "账本读取失败：无法确认需要还原的项目，请检查 " + Ledger.FilePath;
                rep.Lines.Add("FAIL " + rep.Reason);
                return rep;
            }
            IList<LedgerEntry> entries = Ledger.Entries;
            if (entries.Count == 0)
            {
                rep.Ok = true;
                rep.Lines.Add("账本为空，没有需要还原的项目");
                return rep;
            }
            RevertList(new List<LedgerEntry>(entries), rep);
            bool saved = SaveLedger(rep, "REVERT ALL");
            rep.Ok = saved && rep.Failed == 0;
            rep.Committed = 0;
            if (rep.Applied > 0 && State != null) State.Post(Dirty.Ledger);
            Ledger.AppendAudit("REVERT ALL -> ok=" + rep.Applied + " fail=" + rep.Failed);
            return rep;
        }

        public ApplyReport RevertAction(string actionId)
        {
            ApplyReport rep = new ApplyReport();
            rep.Ok = true;
            List<LedgerEntry> list = new List<LedgerEntry>();
            IList<LedgerEntry> entries = Ledger.Entries;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].ActionId == actionId) list.Add(entries[i]);

            if (list.Count == 0)
            {
                rep.Lines.Add("没有与该动作对应的账本记录");
                return rep;
            }
            RevertList(list, rep);
            if (rep.Failed > 0) rep.Ok = false;
            if (!SaveLedger(rep, "REVERT " + actionId)) rep.Ok = false;
            if (rep.Applied > 0 && State != null) State.Post(Dirty.Ledger);
            return rep;
        }

        public ApplyReport RevertEntry(string id)
        {
            ApplyReport rep = new ApplyReport();
            rep.Ok = true;
            IList<LedgerEntry> entries = Ledger.Entries;
            LedgerEntry found = null;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Id == id) { found = entries[i]; break; }
            if (found == null)
            {
                rep.Lines.Add("该记录已不存在（可能已被还原）");
                return rep;
            }

            OpResult r = Ops.Revert(found);
            if (r.Ok)
            {
                rep.Applied = 1;
                Ledger.Remove(found.Id);
                if (!SaveLedger(rep, "REVERT ENTRY " + id)) rep.Ok = false;
            }
            else
            {
                rep.Failed = 1;
                rep.Ok = false;
                rep.Reason = r.Message;
                rep.Detail = r.Detail;
                rep.Lines.Add("FAIL " + EntryKey(found) + " · " + r.Message);
            }
            if (rep.Applied > 0 && State != null) State.Post(Dirty.Ledger);
            return rep;
        }

        // ---- 确认保留当前值（归档待还原记录，不回滚系统）-----------------
        /// <summary>用户确认保留当前系统值，将该条写前记录从待还原账本移除并记入审计日志。</summary>
        public ApplyReport AcceptEntry(string id)
        {
            ApplyReport rep = new ApplyReport();
            rep.Ok = true;
            if (Ledger.Unreadable)
            {
                rep.Ok = false;
                rep.Reason = "账本读取失败，拒绝修改账本";
                rep.Lines.Add("FAIL " + rep.Reason);
                return rep;
            }
            IList<LedgerEntry> entries = Ledger.Entries;
            LedgerEntry found = null;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Id == id) { found = entries[i]; break; }
            if (found == null)
            {
                rep.Lines.Add("该记录已不存在");
                return rep;
            }
            Ledger.Remove(found.Id);
            if (!SaveLedger(rep, "ACCEPT ENTRY " + id))
            {
                Ledger.Add(found);
                rep.Ok = false;
                return rep;
            }
            rep.Applied = 1;
            Ledger.AppendAudit("LEDGER ACCEPT " + found.ActionName + " :: " + EntryKey(found) + " (保留当前值 " + found.NewValue + ")");
            if (State != null) State.Post(Dirty.Ledger);
            return rep;
        }

        /// <summary>确认保留全部当前系统值并清空待还原账本。</summary>
        public ApplyReport AcceptAll()
        {
            ApplyReport rep = new ApplyReport();
            if (Ledger.Unreadable)
            {
                rep.Ok = false;
                rep.Reason = "账本读取失败，拒绝清空账本";
                rep.Lines.Add("FAIL " + rep.Reason);
                return rep;
            }
            IList<LedgerEntry> entries = Ledger.Entries;
            if (entries.Count == 0)
            {
                rep.Ok = true;
                rep.Lines.Add("账本为空，无需归档");
                return rep;
            }
            List<LedgerEntry> backup = new List<LedgerEntry>(entries);
            Ledger.Clear();
            if (!SaveLedger(rep, "ACCEPT ALL"))
            {
                for (int i = 0; i < backup.Count; i++) Ledger.Add(backup[i]);
                rep.Ok = false;
                return rep;
            }
            rep.Ok = true;
            rep.Applied = backup.Count;
            Ledger.AppendAudit("LEDGER ACCEPT ALL :: 已确认保留 " + backup.Count + " 项当前系统状态并清空待还原账本");
            if (State != null) State.Post(Dirty.Ledger);
            return rep;
        }

        /// <summary>逆序还原：后写的先还原。成功才移除记录，失败原样留在账本里。</summary>
        private void RevertList(List<LedgerEntry> list, ApplyReport rep)
        {
            int ok = 0;
            List<string> failLines = new List<string>();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                LedgerEntry e = list[i];
                OpResult r = Ops.Revert(e);
                if (r.Ok)
                {
                    ok++;
                    Ledger.Remove(e.Id);
                    continue;
                }
                rep.Failed++;
                if (rep.Reason == null) rep.Reason = r.Message;
                if (rep.Detail == null) rep.Detail = r.Detail;
                failLines.Add("FAIL " + EntryKey(e) + " · " + r.Message);
            }
            rep.Applied += ok;
            rep.Lines.AddRange(failLines);
        }

        /// <summary>把「停在哪一步、完成了几步」写进给用户看的那一行：失败不能只剩一句「应用失败」。</summary>
        private static string StepSuffix(OpResult r)
        {
            if (r.Step == null) return "";
            return "  [停在「" + r.Step + "」步 · 已完成 " + r.Committed + "/4]";
        }

        private static string Prefix(ActionDef a, string line)
        {
            return (a == null ? "?" : a.Name) + " · " + line;
        }

        private static string EntryKey(LedgerEntry e)
        {
            if (e == null) return "?";
            if (e.Kind == "Reg") return e.Hive + "\\" + e.Path + "\\" + e.Name;
            if (e.Kind == "RegKv")
            {
                string ex = e.Extra ?? "";
                int bar = ex.IndexOf('|');
                string sub = bar > 0 ? ex.Substring(0, bar) : ex;
                return e.Hive + "\\" + e.Path + "\\" + e.Name + "[" + sub + "]";
            }
            return e.Kind + " " + (e.Name ?? "");
        }

        /// <summary>
        /// 落盘账本。失败是**真实失败**：还原动作已经改了系统，但「已经改过」这件事没进磁盘，
        /// 进程一旦意外结束就再也无法证明该还原什么。所以：上报 Diag + 在报告里写一行 WARN，
        /// 调用方（页面/托盘）会把它显示出来，而不是看到一句「已还原」。
        /// </summary>
        private bool SaveLedger(ApplyReport rep, string what)
        {
            try { Ledger.Save(); return true; }
            catch (Exception ex)
            {
                Diag.Fail("engine.ledger.save", ex);
                if (rep != null)
                    rep.Lines.Add("WARN 账本落盘失败（" + what + "）：" + ex.Message + " —— 下次启动无法据此还原");
                if (rep != null && rep.Reason == null) rep.Reason = "账本落盘失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>检测到第三方脚本留下的非默认配置（仅限「修复残留」组）。</summary>
        public List<ActionDef> RemediationIssues()
        {
            List<ActionDef> r = new List<ActionDef>();
            foreach (ActionDef a in Actions)
            {
                if (!a.Remediation) continue;
                if (Probe(a) != AState.Applied) r.Add(a);
            }
            return r;
        }

        public int PendingCount { get { return Ledger.Count; } }
    }
}