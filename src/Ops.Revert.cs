// ============================================================================
//  Ops.Revert.cs — 还原路径（Ops 的 partial 分片）
//
//  还原是与应用对称的一半：每种 Kind 把写前记录里的原值写回去，并且必须回读校验。
//  「没有」与「读不到」在这里必须分开判——读失败时 Existed 也是 false，判成成功
//  就等于丢掉一条本该重试的恢复记录。
// ============================================================================
using System;
using System.Collections.Generic;

namespace Go
{
    public static partial class Ops
    {
        // ---- 还原 ------------------------------------------------------------
        private static readonly Dictionary<string, Func<LedgerEntry, OpResult>> _reverters = BuildReverters();

        private static Dictionary<string, Func<LedgerEntry, OpResult>> BuildReverters()
        {
            Dictionary<string, Func<LedgerEntry, OpResult>> m = new Dictionary<string, Func<LedgerEntry, OpResult>>(StringComparer.Ordinal);
            m["Reg"] = RevertReg;
            m["RegKv"] = RevertRegKv;
            m["Bcd"] = RevertBcd;
            m["PowerAc"] = RevertPowerAc;
            m["Hibernate"] = RevertHibernate;
            return m;
        }

        public static OpResult Revert(LedgerEntry e)
        {
            if (e == null) return OpResult.Fail("没有可还原的记录", StepRevertName, 0);
            Func<LedgerEntry, OpResult> fn;
            if (e.Kind == null || !_reverters.TryGetValue(e.Kind, out fn))
                return OpResult.Fail("该记录类型不支持自动还原：" + (e.Kind ?? "(空)"), StepRevertName, 0);
            try { return fn(e); }
            catch (UnauthorizedAccessException ex)
            {
                return RevertCrash(e, ex, OpStatus.Denied);
            }
            catch (System.Security.SecurityException ex)
            {
                return RevertCrash(e, ex, OpStatus.Denied);
            }
            catch (Exception ex)
            {
                return RevertCrash(e, ex, OpStatus.Failed);
            }
        }

        private static OpResult RevertCrash(LedgerEntry e, Exception ex, OpStatus status)
        {
            Diag.Fail("ops.revert", ex);
            OpResult r = OpResult.Make(status, ex.Message, StepRevertName, 0);
            r.Detail = ex.GetType().Name + ": " + ex.Message + " @ " + Key(e);
            return r;
        }

        private static OpResult RevertReg(LedgerEntry e)
        {
            if (e.Existed)
            {
                string type = e.ValueType;
                if (string.IsNullOrEmpty(type)) type = "String";
                Reg.Write(e.Hive, e.Path, e.Name, e.OldValue, type);
            }
            else
            {
                Reg.Delete(e.Hive, e.Path, e.Name);
            }

            // 还原同样要回读校验：写失败时不能对用户报「已还原」。而且「没有」与
            // 「读不到」必须分开——读失败时 Existed 也是 false，判成成功就等于丢掉了
            // 一条本该重试的恢复记录（HANDOFF 里那个吞了两版的 bug 就是这一条）。
            RegValue back = Reg.Read(e.Hive, e.Path, e.Name);
            if (back.Denied) return OpResult.Denied("还原后回读被拒绝（需要管理员）", StepRevertName, 0);
            if (back.Error != null)
            {
                OpResult bad = OpResult.Fail("还原后回读失败：" + back.Error, StepRevertName, 0);
                bad.Detail = back.Error;
                return bad;
            }

            bool ok;
            if (e.Existed) ok = back.Existed && Reg.ToText(back.Value) == (e.OldValue ?? "");
            else ok = !back.Existed;
            if (!ok)
                return OpResult.Fail("还原后回读不一致：" + (back.Existed ? Reg.ToText(back.Value) : Absent), StepRevertName, 0);

            // 本工具建出来的键：值已经删掉了，再把这个空键一起清掉，才算真的回到原状。
            if (e.Extra == "newkey") Reg.TryDeleteEmptyKey(e.Hive, e.Path);
            WinNative.SyncControlPanelIfNeeded(e.Hive, e.Path);
            return OpResult.Good("已还原", 0, null);
        }

        private static OpResult RevertBcd(LedgerEntry e)
        {
            string outp;
            if (e.Existed) outp = Run("bcdedit", "/set " + e.Name + " " + e.OldValue, 10000);
            else outp = Run("bcdedit", "/deletevalue " + e.Name, 10000);
            InvalidateBcdCache();

            // 旧代码无条件 r.Ok = true：没权限、值名写错、bcdedit 根本不存在时，
            // 用户看到的都是「已还原」，而系统还停在改过的状态。这里必须回读。
            Op probe = new Op();
            probe.Kind = "Bcd";
            probe.Name = e.Name;
            string now = Probe(probe);
            bool ok;
            if (e.Existed) ok = string.Equals((now ?? "").Trim(), (e.OldValue ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            else ok = now == Absent;
            if (ok) return OpResult.Good("已还原", 0, null);

            OpResult r = OpResult.Fail("bcdedit 未确认还原：当前=" + now + " · " + Shorten(outp), StepRevertName, 0);
            r.Detail = Shorten(outp);
            return r;
        }

        private static OpResult RevertPowerAc(LedgerEntry e)
        {
            string sub, set;
            string err = ParsePowerExtra(e.Extra, out sub, out set);
            if (err != null) return OpResult.Fail(err, StepRevertName, 0);

            long idx;
            err = ParsePowerIndex(e.OldValue, out idx);
            if (err != null) return OpResult.Fail(err, StepRevertName, 0);

            if (idx < 0 || idx > uint.MaxValue || !Power.WriteAcSetting(sub, set, (uint)idx))
            {
                Run("powercfg", "/setacvalueindex scheme_current " + sub + " " + set + " " + idx, 8000);
                Run("powercfg", "/setactive scheme_current", 8000);
            }
            InvalidatePowerAcCache();

            return VerifyPowerAc(sub, set, idx);
        }

        private static string ParsePowerExtra(string extra, out string sub, out string set)
        {
            sub = null;
            set = null;
            string[] parts = (extra ?? "|").Split('|');
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                return "电源项记录缺少 subgroup/setting，无法还原";
            sub = parts[0];
            set = parts[1];
            return null;
        }

        private static string ParsePowerIndex(string oldValue, out long idx)
        {
            idx = 0;
            if (oldValue == null || oldValue == Absent) return "电源项没有记录原值，无法还原";
            try { idx = Convert.ToInt64(oldValue, 16); }
            catch (FormatException ex)
            {
                return "电源项原值无法解析：" + oldValue + " (" + ex.Message + ")";
            }
            catch (OverflowException ex)
            {
                return "电源项原值超出范围：" + oldValue + " (" + ex.Message + ")";
            }
            return null;
        }

        private static OpResult VerifyPowerAc(string sub, string set, long idx)
        {
            Op probe = new Op();
            probe.Kind = "PowerAc";
            probe.Path = sub;
            probe.Name = set;
            string now = Probe(probe);
            if (!string.IsNullOrEmpty(now) && now != Absent && now != DeniedText && now != ReadFailText && now != ErrorText)
            {
                try
                {
                    if (Convert.ToInt64(now, 16) == idx) return OpResult.Good("已还原", 0, null);
                }
                catch (FormatException ex)
                {
                    // Probe 的输出格式变了（本地化/版本差异）：不能当成还原成功。
                    OpResult r = OpResult.Fail("电源项回读无法解析：" + now + " (" + ex.Message + ")", StepRevertName, 0);
                    r.Detail = now;
                    return r;
                }
                catch (OverflowException ex)
                {
                    OpResult r = OpResult.Fail("电源项回读无法解析：" + now + " (" + ex.Message + ")", StepRevertName, 0);
                    r.Detail = now;
                    return r;
                }
            }
            return OpResult.Fail("电源项未确认还原：当前=" + now, StepRevertName, 0);
        }

        private static OpResult RevertHibernate(LedgerEntry e)
        {
            Run("powercfg", e.OldValue == "1" ? "/h on" : "/h off", 15000);

            // 快速启动开关不属于 powercfg /h 的语义，必须单独写回（见 HibernateRecord）。
            // 它写不回去也是「没回干净」，不能只记 Diag 就报成功。
            string hbWhy = null;
            if (!string.IsNullOrEmpty(e.Extra))
            {
                try { Reg.Write("HKLM", PowerKey, "HiberbootEnabled", e.Extra, "DWord"); }
                catch (Exception hx)
                {
                    Diag.Fail("ops.hibernate.hb", hx);
                    hbWhy = "快速启动开关写回失败：" + hx.Message;
                }
                if (hbWhy == null)
                {
                    RegValue hb = Reg.Read("HKLM", PowerKey, "HiberbootEnabled");
                    if (!hb.Denied && hb.Error == null && Reg.ToText(hb.Value) != e.Extra)
                        hbWhy = "快速启动开关回读不一致：当前=" + (hb.Existed ? Reg.ToText(hb.Value) : Absent);
                }
            }

            Op probe = new Op();
            probe.Kind = "Hibernate";
            probe.Name = "hibernate";
            string now = Probe(probe);
            bool ok = e.OldValue == "1" ? now == "1" : now == "0";
            if (ok && hbWhy == null) return OpResult.Good("已还原", 0, null);

            string msg = ok ? ("休眠已还原，但" + hbWhy) : ("休眠策略未确认还原：当前=" + now);
            if (!ok && hbWhy != null) msg += "；" + hbWhy;
            OpResult r = OpResult.Fail(msg, StepRevertName, 0);
            r.Detail = hbWhy;
            return r;
        }
    }
}