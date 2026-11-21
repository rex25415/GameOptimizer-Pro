// ============================================================================
//  Ops.Apply.cs — 应用步骤计划（Ops 的 partial 分片）
//
//  每种动作类型的差异只在这里表达：怎么组织写前记录、怎么写、写完怎么校验。
//  状态机（探测 → 记账 → 写入 → 校验 → 失败补偿）在 Ops.cs 里，不认识任何具体类型；
//  新增一种动作类型只需在 BuildPlans 里加一行映射。
// ============================================================================
using System;
using System.Collections.Generic;

namespace Go
{
    public static partial class Ops
    {
        /// <summary>这次写入到底改了系统没有：Reg 比账本原值，其它类型比写入前的探针读数。</summary>
        private static bool Unchanged(Txn t)
        {
            if (t.Entry != null && t.Entry.Kind == "Reg") return ValueIsOriginal(t.Entry);
            // 命令类没有探针（不记账，走不到这里）；其余三类都能回读同一个键。
            string now = Probe(t.Op);
            return string.Equals(now, t.Before, StringComparison.Ordinal);
        }

        // ---- 应用：显式步骤状态机 --------------------------------------------
        //  一步一份计划：Kind 之间的差异全部收在 _plans 里，状态机本身不认识任何具体类型。
        //  这样新增动作类型不会碰到「探测 → 记账 → 写入 → 校验」的骨架。
        private sealed class Txn
        {
            public Op Op;
            public string ActionId;
            public string ActionName;
            public Ledger Ledger;
            public string Before;    // 步骤 1 的读数（表层字符串）
            public RegValue Raw;     // Reg 专用：需要 Kind/Existed 才能组织写前记录
            public LedgerEntry Entry;// 步骤 2 落下的写前记录
            public string Output;    // 外部命令的原始输出（校验失败时进 Detail）
        }

        private sealed class StepPlan
        {
            public Func<Op, RegValue> RawRead;          // Reg 专用（其余走 Probe 表）
            public Func<Txn, LedgerEntry> Record;       // 用写入前的读数组织账本记录
            public Func<Txn, string> Write;             // 返回 null = 成功，否则是失败原因
            public Func<Txn, string> Verify;            // 写后校验，返回 null = 通过
            public bool SkipWhenMatched;                // 目标状态已成立就不写（Reg）
            public bool Reversible;                     // false = Command：不可逆、不记账
        }

        private static readonly Dictionary<string, StepPlan> _plans = BuildPlans();

        private static Dictionary<string, StepPlan> BuildPlans()
        {
            Dictionary<string, StepPlan> m = new Dictionary<string, StepPlan>(StringComparer.Ordinal);

            StepPlan reg = new StepPlan();
            reg.RawRead = RegRawRead;
            reg.Record = RegRecord;
            reg.Write = RegWrite;
            reg.Verify = RegVerify;
            reg.SkipWhenMatched = true;
            reg.Reversible = true;
            m["Reg"] = reg;

            m["RegKv"] = BuildRegKvPlan();

            StepPlan bcd = new StepPlan();
            bcd.Record = BcdRecord;
            bcd.Write = BcdWrite;
            bcd.Verify = BcdVerify;
            bcd.Reversible = true;
            m["Bcd"] = bcd;

            StepPlan power = new StepPlan();
            power.Record = PowerAcRecord;
            power.Write = PowerAcWrite;
            power.Verify = PowerAcVerify;
            power.SkipWhenMatched = true;
            power.Reversible = true;
            m["PowerAc"] = power;

            StepPlan hib = new StepPlan();
            hib.Record = HibernateRecord;
            hib.Write = HibernateWrite;
            hib.Verify = HibernateVerify;
            hib.Reversible = true;
            m["Hibernate"] = hib;

            StepPlan cmd = new StepPlan();
            cmd.Reversible = false;
            m["Command"] = cmd;

            return m;
        }

        /// <summary>
        /// 一次批量应用里的一个 op：带上它所属的动作身份（账本记录要按动作归组）。
        /// </summary>
        public sealed class OpJob
        {
            public Op Op;
            public string ActionId;
            public string ActionName;
            public int ActionIndex;

            public OpJob(Op op, string actionId, string actionName, int actionIndex)
            {
                Op = op;
                ActionId = actionId;
                ActionName = actionName;
                ActionIndex = actionIndex;
            }
        }

        /// <summary>
        /// 一次批量应用的完整结局。批量 = 一个事务：任一 op 失败，本次调用写下的
        /// 全部改动都会被回滚（逆序），系统不会停在半改状态。
        /// </summary>
        public sealed class BatchOutcome
        {
            public bool Ok;
            /// <summary>成功单位数：生效 + 已成立（skip）。</summary>
            public int Applied;
            public int FailedIndex = -1;
            public string Reason;
            public string Detail;
            /// <summary>失败停在哪一步（探测/记录/写入/校验/执行）。</summary>
            public string Step;
            /// <summary>失败时已完成的步数（0..4）。</summary>
            public int Committed;
            public int RolledBack;
            public int RollbackFailed;
            /// <summary>失败后的补偿落盘是否也失败了（记录可能只在内存里）。</summary>
            public bool SaveFailed;
            /// <summary>给用户看的行（保持旧 ApplyReport 的措辞）。</summary>
            public readonly List<string> Lines = new List<string>();
            /// <summary>成功 = 本次保留的写前记录；失败 = 回滚失败、仍需人工处理的记录。</summary>
            public readonly List<LedgerEntry> Touched = new List<LedgerEntry>();
            /// <summary>与 jobs 下标一一对应；未执行到的 op 为 null。</summary>
            public readonly List<OpResult> Results = new List<OpResult>();
        }

        /// <summary>单 op 应用（AutoPilot 等旧调用点）：内部走同一条批量事务。</summary>
        public static OpResult Apply(Op op, string actionId, string actionName, Ledger ledger)
        {
            List<OpJob> jobs = new List<OpJob>(1);
            jobs.Add(new OpJob(op, actionId, actionName, 0));
            BatchOutcome b = ApplyBatch(jobs, ledger);
            if (b.Results.Count > 0 && b.Results[0] != null) return b.Results[0];
            OpResult r = OpResult.Fail(b.Reason == null ? "应用失败" : b.Reason, b.Step, b.Committed);
            r.Detail = b.Detail;
            return r;
        }

        /// <summary>
        /// 批量应用：探测全部 → 一次性记账并落盘 → 逐个写入+校验 → 任一失败逆序回滚。
        ///
        /// 为什么不是"每个 op 各存一次盘"：一次 Save 是整份 JSON + FlushFileBuffers + Replace，
        /// 机械盘上 20~40ms；一个 8 动作的预设（13 个 op）就是 13 次 fsync 压在 UI 线程上。
        /// 而"先落盘、再写系统"的顺序没有被削弱——所有写前记录在**任何一次系统写入之前**
        /// 就已经整体落盘；中途失败时，未执行到的 op 的记录会被删掉，已执行的走补偿。
        /// </summary>
        public static BatchOutcome ApplyBatch(IList<OpJob> jobs, Ledger ledger)
        {
            BatchOutcome o = new BatchOutcome();
            if (jobs == null || jobs.Count == 0)
            {
                o.Ok = true;
                o.Lines.Add("没有需要执行的原子操作");
                return o;
            }
            if (ledger == null)
            {
                o.Ok = false;
                o.Reason = "账本不可用，拒绝写入（无法保证可还原）";
                o.Step = StepRecordName;
                o.Lines.Add("FAIL " + o.Reason);
                return o;
            }

            int n = jobs.Count;
            Txn[] tx = new Txn[n];
            StepPlan[] plan = new StepPlan[n];
            bool[] needWrite = new bool[n];
            bool[] written = new bool[n];
            List<LedgerEntry> added = new List<LedgerEntry>();
            for (int i = 0; i < n; i++) o.Results.Add(null);

            // ---- 阶段 1：探测全部（只读，不碰账本、不碰系统）------------------
            for (int i = 0; i < n; i++)
            {
                OpJob job = jobs[i];
                Op op = job == null ? null : job.Op;
                if (op == null) return FailProbe(o, i, "没有要执行的原子操作", StepProbeName, 0, null);
                StepPlan p;
                if (op.Kind == null || !_plans.TryGetValue(op.Kind, out p))
                    return FailProbe(o, i, "不支持的操作类型：" + (op.Kind ?? "(空)"), StepProbeName, 0, null);

                Txn t = new Txn();
                t.Op = op;
                t.ActionId = job.ActionId;
                t.ActionName = job.ActionName;
                t.Ledger = ledger;
                tx[i] = t;
                plan[i] = p;

                if (!p.Reversible)
                {
                    // Command 没有可探测的状态、也不记账：它是事务里唯一不可回滚的一步。
                    needWrite[i] = true;
                    continue;
                }

                string detail;
                OpResult stop = StepProbe(t, p, out detail);
                if (stop != null)
                {
                    stop.Detail = detail;
                    o.Results[i] = stop;
                    return FailProbe(o, i, stop.Message, stop.Step, stop.Committed, detail);
                }
                if (p.SkipWhenMatched && Matches(op, t.Before))
                {
                    o.Results[i] = OpResult.Skip("已是目标状态（未写入）", 1);
                    o.Applied++;
                    continue;
                }
                needWrite[i] = true;
            }

            // ---- 阶段 2：组织全部写前记录，一次落盘 --------------------------
            // 这一段必须整体在任何系统写入之前完成：账本没落盘就写系统 = 改了但还原不回来。
            for (int i = 0; i < n; i++)
            {
                if (!needWrite[i] || !plan[i].Reversible) continue;
                LedgerEntry e;
                try { e = plan[i].Record(tx[i]); }
                catch (Exception ex)
                {
                    Diag.Fail("ops.record", ex);
                    RemoveAll(ledger, added);
                    o.Detail = ex.GetType().Name + ": " + ex.Message;
                    o.Results[i] = OpResult.Fail("组织写前记录失败，已中止：" + ex.Message, StepRecordName, 1);
                    return FailProbe(o, i, "组织写前记录失败，已中止：" + ex.Message, StepRecordName, 1, o.Detail);
                }
                if (e == null) continue;

                // Add 会按「动作 + 键」去重：拿回账本里真正生效的那条，
                // 否则回滚时 Remove(新 Id) 会打空，留下一条幽灵记录。
                LedgerEntry canonical = ledger.Add(e);
                tx[i].Entry = canonical;
                if (canonical == null || ContainsId(added, canonical.Id)) continue;
                added.Add(canonical);
            }

            if (added.Count > 0)
            {
                try { ledger.Save(); }
                catch (Exception ex)
                {
                    // 落盘失败 = 这次写入没有「可还原的凭证」。撤掉内存记录并中止，
                    // 系统此时一个字节都没被改过。
                    RemoveAll(ledger, added);
                    Diag.Fail("ops.record.save", ex);
                    ledger.AppendAudit("SAVE-FAIL " + jobs[0].ActionName + " :: " + ex.Message);
                    int at = FirstNeedWrite(needWrite, plan);
                    o.SaveFailed = true;
                    o.Detail = ex.GetType().Name + ": " + ex.Message;
                    if (at >= 0) o.Results[at] = OpResult.Fail("账本落盘失败，已中止（系统未被修改）：" + ex.Message, StepRecordName, 1);
                    o.Lines.Add("FAIL 账本落盘失败，已中止（系统未被修改）：" + ex.Message);
                    return FailProbe(o, at, "账本落盘失败，已中止（系统未被修改）：" + ex.Message, StepRecordName, 1, o.Detail);
                }
            }

            // ---- 阶段 3/4：逐个写入并回读校验，失败逆序回滚 ------------------
            for (int i = 0; i < n; i++)
            {
                if (!needWrite[i]) continue;

                if (!plan[i].Reversible)
                {
                    OpResult cmd = RunCommand(tx[i]);
                    o.Results[i] = cmd;
                    if (!cmd.Ok) return AbortBatch(o, tx, plan, needWrite, written, ledger, added, i, cmd.Step, cmd.Message, cmd.Detail, cmd.Status == OpStatus.Denied);
                    o.Applied++;
                    continue;
                }

                string why, detail;
                bool denied;
                why = RunStep(plan[i].Write, tx[i], StepWriteName, out detail, out denied);
                if (why != null) return AbortBatch(o, tx, plan, needWrite, written, ledger, added, i, StepWriteName, why, detail, denied);

                written[i] = true;
                why = RunStep(plan[i].Verify, tx[i], StepVerifyName, out detail, out denied);
                if (why != null) return AbortBatch(o, tx, plan, needWrite, written, ledger, added, i, StepVerifyName, why, detail, denied);

                o.Applied++;
                o.Committed++;
                o.Results[i] = OpResult.Good("已生效", 4, tx[i].Entry);
                ledger.AppendAudit("OK    " + tx[i].ActionName + " :: " + Key(tx[i].Op)
                                   + " -> " + (tx[i].Op.Delete ? "[删除]" : tx[i].Op.Value));
            }

            o.Ok = true;
            for (int i = 0; i < added.Count; i++) o.Touched.Add(added[i]);
            return o;
        }

        /// <summary>探测阶段失败：系统一个字节都没动，只需把结果组织成报告。</summary>
        private static BatchOutcome FailProbe(BatchOutcome o, int index, string msg, string step, int committed, string detail)
        {
            o.Ok = false;
            o.FailedIndex = index;
            o.Reason = msg;
            o.Step = step;
            o.Committed = committed;
            if (detail != null) o.Detail = detail;
            o.Lines.Add("FAIL " + msg + StepSuffix(step, committed));
            return o;
        }

        /// <summary>
        /// 写入/校验失败：把这一步的改动退回原值，再把本次已经写下的 op 逆序回滚，
        /// 最后只落一次盘。回滚本身失败会留在账本里并在报告里说清楚。
        /// </summary>
        private static BatchOutcome AbortBatch(BatchOutcome o, Txn[] tx, StepPlan[] plan, bool[] needWrite,
                                               bool[] written, Ledger ledger, List<LedgerEntry> added,
                                               int failed, string step, string why, string detail, bool denied)
        {
            o.Ok = false;
            o.FailedIndex = failed;
            o.Reason = why;
            o.Step = step;
            o.Committed = step == StepVerifyName ? 3 : 2;
            if (detail != null) o.Detail = detail;
            o.Lines.Add("FAIL " + why + StepSuffix(step, o.Committed));
            if (failed >= 0 && failed < o.Results.Count)
            {
                OpResult bad = OpResult.Make(denied ? OpStatus.Denied : OpStatus.Failed, why, step, o.Committed);
                bad.Detail = detail;
                o.Results[failed] = bad;
            }
            // 事务语义：本次写下的改动全部回滚，所以"成功单位数"归零。
            // 回滚失败的条目仍留在账本里，用 RollbackFailed 单独表达。
            o.Applied = 0;

            bool ledgerChanged = false;

            // ① 当前这一步：写入可能已经部分生效，必须按"原值是否改变"决定要不要回写。
            if (tx[failed].Entry != null)
            {
                if (Unchanged(tx[failed]))
                {
                    ledger.Remove(tx[failed].Entry.Id);
                    tx[failed].Entry = null;
                    o.RolledBack++;
                    ledgerChanged = true;
                    o.Lines.Add("回滚 OK  原值未改变，已撤销写前记录");
                }
                else
                {
                    OpResult rb = Revert(tx[failed].Entry);
                    if (rb.Ok)
                    {
                        ledger.Remove(tx[failed].Entry.Id);
                        tx[failed].Entry = null;
                        o.RolledBack++;
                        ledgerChanged = true;
                        o.Lines.Add("回滚 OK  " + Key(tx[failed].Op));
                    }
                    else
                    {
                        o.RollbackFailed++;
                        o.Touched.Add(tx[failed].Entry);
                        o.Lines.Add("回滚 FAIL " + Key(tx[failed].Op) + " · " + rb.Message + "（记录已保留，可在还原中心重试）");
                    }
                }
            }

            // ② 之前已经写入的 op：逆序还原。
            for (int j = failed - 1; j >= 0; j--)
            {
                if (!needWrite[j] || !written[j] || tx[j].Entry == null) continue;
                OpResult rb = Revert(tx[j].Entry);
                if (rb.Ok)
                {
                    ledger.Remove(tx[j].Entry.Id);
                    tx[j].Entry = null;
                    o.RolledBack++;
                    ledgerChanged = true;
                    o.Lines.Add("回滚 OK  " + Key(tx[j].Op));
                }
                else
                {
                    o.RollbackFailed++;
                    o.Touched.Add(tx[j].Entry);
                    o.Lines.Add("回滚 FAIL " + Key(tx[j].Op) + " · " + rb.Message + "（记录已保留，可在还原中心重试）");
                }
            }

            // ③ 还没执行到的 op：写前记录只是"计划"，系统没动过，直接撤掉。
            for (int j = failed + 1; j < tx.Length; j++)
            {
                if (!needWrite[j] || tx[j].Entry == null) continue;
                ledger.Remove(tx[j].Entry.Id);
                tx[j].Entry = null;
                ledgerChanged = true;
            }

            if (ledgerChanged)
            {
                try { ledger.Save(); }
                catch (Exception ex)
                {
                    Diag.Fail("ops.abort.save", ex);
                    o.SaveFailed = true;
                    o.Lines.Add("WARN 回滚后的账本落盘失败：" + ex.Message + " —— 下次启动无法据此还原");
                }
            }

            if (o.RolledBack > 0 && o.RollbackFailed == 0)
                o.Reason = why + "，已回滚本次全部改动";
            else if (o.RollbackFailed > 0)
                o.Reason = why + "，回滚失败 " + o.RollbackFailed + " 项（记录已保留）";
            o.Lines.AddRange(CollectApplied(o));
            return o;
        }

        /// <summary>失败报告里补上"哪些 op 本来已经生效"（旧实现把 okLines 也列出来）。</summary>
        private static List<string> CollectApplied(BatchOutcome o)
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < o.Results.Count; i++)
            {
                if (o.Results[i] != null && o.Results[i].Ok) lines.Add("OK  " + o.Results[i].Message);
            }
            return lines;
        }

        private static int FirstNeedWrite(bool[] needWrite, StepPlan[] plan)
        {
            for (int i = 0; i < needWrite.Length; i++) if (needWrite[i]) return i;
            return needWrite.Length > 0 ? 0 : -1;
        }

        private static void RemoveAll(Ledger ledger, List<LedgerEntry> added)
        {
            for (int i = 0; i < added.Count; i++)
            {
                if (added[i] == null) continue;
                ledger.Remove(added[i].Id);
            }
            added.Clear();
        }

        private static bool ContainsId(List<LedgerEntry> list, string id)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].Id == id) return true;
            return false;
        }

        private static string StepSuffix(string step, int committed)
        {
            if (step == null) return "";
            return "  [停在「" + step + "」步 · 已完成 " + committed + "/4]";
        }

        private static OpResult StepProbe(Txn t, StepPlan plan, out string detail)
        {
            detail = null;
            if (plan.RawRead != null)
            {
                RegValue rv;
                try { rv = plan.RawRead(t.Op); }
                catch (Exception ex)
                {
                    Diag.Fail("ops.probe.raw", ex);
                    detail = ex.GetType().Name + ": " + ex.Message;
                    return OpResult.Fail("读取原值失败，已中止以免覆盖：" + ex.Message, StepProbeName, 0);
                }
                t.Raw = rv;
                if (rv.Denied) return OpResult.Denied("拒绝访问（需要管理员）", StepProbeName, 0);
                if (rv.Error != null)
                {
                    detail = rv.Error;
                    return OpResult.Fail("读取原值失败，已中止以免覆盖：" + rv.Error, StepProbeName, 0);
                }
                t.Before = rv.Existed ? Reg.ToText(rv.Value) : Absent;
                return null;
            }

            string cur = Probe(t.Op);
            if (cur == DeniedText) return OpResult.Denied("拒绝访问（需要管理员）", StepProbeName, 0);
            if (cur == ReadFailText || cur == ErrorText || cur == UnsupportedText)
            {
                detail = cur;
                return OpResult.Fail("读取原值失败，已中止以免覆盖：" + cur, StepProbeName, 0);
            }
            t.Before = cur;
            return null;
        }

        /// <summary>执行一步：把异常翻译成「这一步失败」+ 技术细节，不让它冒泡成「应用失败」。</summary>
        private static string RunStep(Func<Txn, string> fn, Txn t, string step, out string detail, out bool denied)
        {
            detail = null;
            denied = false;
            try { return fn(t); }
            catch (UnauthorizedAccessException ex)
            {
                Diag.Fail("ops.step." + step, ex);
                detail = ex.GetType().Name + ": " + ex.Message;
                denied = true;
                return step + "被拒绝：" + ex.Message;
            }
            catch (System.Security.SecurityException ex)
            {
                Diag.Fail("ops.step." + step, ex);
                detail = ex.GetType().Name + ": " + ex.Message;
                denied = true;
                return step + "被拒绝：" + ex.Message;
            }
            catch (Exception ex)
            {
                Diag.Fail("ops.step." + step, ex);
                detail = ex.GetType().Name + ": " + ex.Message;
                return step + "失败：" + ex.Message;
            }
        }

        // ---- 各类型的具体计划 ------------------------------------------------
        private static RegValue RegRawRead(Op op)
        {
            return Reg.Read(op.Hive, op.Path, op.Name);
        }

        private static LedgerEntry RegRecord(Txn t)
        {
            RegValue old = t.Raw;
            LedgerEntry e = NewEntry(t, "Reg");
            e.Hive = t.Op.Hive;
            e.Path = t.Op.Path;
            e.Name = t.Op.Name;
            e.ValueType = old.Existed ? old.Kind.ToString() : t.Op.Type;
            e.Existed = old.Existed;
            e.OldValue = Reg.ToText(old.Value);
            e.NewValue = t.Op.Delete ? null : t.Op.Value;
            // 记下「这条写入顺手建了一个键」，还原时才可能把空键一起清掉
            // （旧实现只删值不删键，回滚后系统里留下本工具建出来的空键）。
            e.Extra = Reg.KeyExists(t.Op.Hive, t.Op.Path) ? null : "newkey";
            return e;
        }

        private static string RegWrite(Txn t)
        {
            if (t.Op.Delete) Reg.Delete(t.Op.Hive, t.Op.Path, t.Op.Name);
            else Reg.Write(t.Op.Hive, t.Op.Path, t.Op.Name, t.Op.Value, t.Op.Type);
            WinNative.SyncControlPanelIfNeeded(t.Op.Hive, t.Op.Path);
            return null;
        }

        private static string RegVerify(Txn t)
        {
            string now = Probe(t.Op);
            if (!Matches(t.Op, now)) return "写入后校验不一致：" + now;
            if (!TypeMatches(t.Op)) return "写入后类型不一致：期望 " + t.Op.Type + "，实际 " + KindOf(t.Op);
            return null;
        }

        private static LedgerEntry BcdRecord(Txn t)
        {
            LedgerEntry e = NewEntry(t, "Bcd");
            e.Name = t.Op.Name;
            e.Existed = t.Before != Absent;
            e.OldValue = e.Existed ? t.Before : null;
            e.NewValue = t.Op.Value;
            return e;
        }

        private static string BcdWrite(Txn t)
        {
            string args = t.Op.Delete ? "/deletevalue " + t.Op.Name : "/set " + t.Op.Name + " " + t.Op.Value;
            t.Output = Run("bcdedit", args, 10000);
            InvalidateBcdCache();
            return null;
        }

        private static string BcdVerify(Txn t)
        {
            if (Matches(t.Op, Probe(t.Op))) return null;
            return "bcdedit 未确认：" + Shorten(t.Output);
        }

        private static LedgerEntry PowerAcRecord(Txn t)
        {
            LedgerEntry e = NewEntry(t, "PowerAc");
            e.Name = t.Op.Name;
            e.Extra = t.Op.Path + "|" + t.Op.Name;
            e.Existed = t.Before != Absent;
            e.OldValue = t.Before;
            e.NewValue = t.Op.Value;
            return e;
        }

        private static string PowerAcWrite(Txn t)
        {
            uint val;
            if (uint.TryParse((t.Op.Value ?? "").Trim(), out val) && Power.WriteAcSetting(t.Op.Path, t.Op.Name, val))
            {
                t.Output = "powrprof native write: " + val;
                InvalidatePowerAcCache();
                return null;
            }
            t.Output = Run("powercfg", "/setacvalueindex scheme_current " + t.Op.Path + " " + t.Op.Name + " " + t.Op.Value, 8000);
            Run("powercfg", "/setactive scheme_current", 8000);
            InvalidatePowerAcCache();
            return null;
        }

        private static string PowerAcVerify(Txn t)
        {
            if (Matches(t.Op, Probe(t.Op))) return null;
            return "电源项未确认";
        }

        private static LedgerEntry HibernateRecord(Txn t)
        {
            LedgerEntry e = NewEntry(t, "Hibernate");
            e.Name = "hibernate";
            e.Existed = true;
            e.OldValue = t.Before == "1" ? "1" : "0";
            e.NewValue = t.Op.Value;
            // powercfg /h off 会连带关掉「快速启动」（HiberbootEnabled=0），
            // 只记 hiberfil.sys 是否存在的话，/h on 之后快速启动仍然是关的 ——
            // 而动作说明写的是「可完整还原」。把 Hib 开关一起记进 Extra。
            RegValue hb = Reg.Read("HKLM", PowerKey, "HiberbootEnabled");
            e.Extra = hb.Existed ? Reg.ToText(hb.Value) : null;
            return e;
        }

        private static string HibernateWrite(Txn t)
        {
            t.Output = Run("powercfg", t.Op.Value == "1" ? "/h on" : "/h off", 15000);
            return null;
        }

        private static string HibernateVerify(Txn t)
        {
            if (Matches(t.Op, Probe(t.Op))) return null;
            return "电源策略未确认";
        }

        private static LedgerEntry NewEntry(Txn t, string kind)
        {
            LedgerEntry e = new LedgerEntry();
            e.Id = Guid.NewGuid().ToString("N");
            e.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            e.ActionId = t.ActionId;
            e.ActionName = t.ActionName;
            e.Kind = kind;
            return e;
        }

        /// <summary>命令类动作：不可逆，不落账，只执行并留审计。失败要说出来，不能一律「已执行」。</summary>
        private static OpResult RunCommand(Txn t)
        {
            ProcessOutcome o = Exec(t.Op.Path, t.Op.Value, 20000);
            t.Ledger.AppendAudit("CMD " + t.ActionName + " :: " + t.Op.Path + " " + t.Op.Value + " => " + Shorten(o.Output));
            OpResult r;
            if (o.TimedOut) r = OpResult.Fail("命令执行超时", StepRunName, 0);
            else if (o.StartError != null) r = OpResult.Fail("命令无法启动：" + o.StartError, StepRunName, 0);
            else if (o.ExitCode != 0) r = OpResult.Fail("命令以退出码 " + o.ExitCode + " 结束", StepRunName, 0);
            else return OpResult.Good("已执行", 0, null);
            r.Detail = Shorten(o.Output);
            return r;
        }
    }
}