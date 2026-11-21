// ============================================================================
//  Ops.RegKv.cs — 注册表分号键值对（RegKv）原子事务分片
//
//  用途：Windows 11 的 DirectXUserGlobalSettings 等复合字符串值在同一个 REG_SZ
//  里打包了多个用分号分隔的键值（例如 SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;）。
//  若按整串 Reg 写入/还原，两个独立动作会互相覆盖。
//  RegKv 实现「字段级探测 · 字段级合并写 · 字段级原子还原」：
//    · 写入前记录该字段的原值与是否存在（含原字符串键是否存在）
//    · 写入时只替换或追加目标字段，100% 保留用户已有的其他字段（如 AutoHDREnable）
//    · 还原时只复原目标字段；若原字符串原本不存在且移除后为空，再删除整条注册表值
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace Go
{
    public static partial class Ops
    {
        /// <summary>从分号分隔的 Key=Value; 串中读取指定键的值（不区分大小写）。</summary>
        public static bool TryParseSemicolonKv(string raw, string key, out string val)
        {
            val = null;
            if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(key)) return false;
            string[] parts = raw.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string seg = parts[i];
                if (string.IsNullOrWhiteSpace(seg)) continue;
                int eq = seg.IndexOf('=');
                if (eq <= 0) continue;
                string k = seg.Substring(0, eq).Trim();
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                val = seg.Substring(eq + 1).Trim();
                return true;
            }
            return false;
        }

        /// <summary>原地更新、追加或移除分号串中的指定键，严格保留其余键顺序与尾分号格式。</summary>
        public static string UpdateSemicolonKv(string raw, string key, string newVal, bool remove)
        {
            List<string> kept = new List<string>();
            bool replaced = false;
            if (!string.IsNullOrEmpty(raw))
            {
                string[] parts = raw.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string seg = parts[i].Trim();
                    if (seg.Length == 0) continue;
                    int eq = seg.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = seg.Substring(0, eq).Trim();
                        if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!remove && !replaced)
                            {
                                kept.Add(key + "=" + (newVal ?? ""));
                                replaced = true;
                            }
                            continue;
                        }
                    }
                    kept.Add(seg);
                }
            }
            if (!remove && !replaced) kept.Add(key + "=" + (newVal ?? ""));
            if (kept.Count == 0) return "";
            StringBuilder sb = new StringBuilder(kept.Count * 24);
            for (int i = 0; i < kept.Count; i++)
            {
                sb.Append(kept[i]).Append(';');
            }
            return sb.ToString();
        }

        private static string ReadRegKvValue(Op op)
        {
            if (op == null || string.IsNullOrEmpty(op.KvKey)) return ErrorText;
            RegValue rv = Reg.Read(op.Hive, op.Path, op.Name);
            if (rv.Denied) return DeniedText;
            if (rv.Error != null) return ReadFailText;
            if (!rv.Existed) return Absent;
            string raw = Reg.ToText(rv.Value);
            string found;
            return TryParseSemicolonKv(raw, op.KvKey, out found) ? found : Absent;
        }

        private static StepPlan BuildRegKvPlan()
        {
            StepPlan p = new StepPlan();
            p.Record = RegKvRecord;
            p.Write = RegKvWrite;
            p.Verify = RegKvVerify;
            p.SkipWhenMatched = true;
            p.Reversible = true;
            return p;
        }

        private static string ExtractRegKvKey(string extra)
        {
            if (string.IsNullOrEmpty(extra)) return "";
            int bar = extra.IndexOf('|');
            return bar > 0 ? extra.Substring(0, bar) : extra;
        }

        private static bool ExtractRegKvValExisted(string extra)
        {
            if (string.IsNullOrEmpty(extra)) return true;
            string[] parts = extra.Split('|');
            return parts.Length < 2 || parts[1] != "0";
        }

        private static bool ExtractRegKvNewKey(string extra)
        {
            if (string.IsNullOrEmpty(extra)) return false;
            string[] parts = extra.Split('|');
            return parts.Length >= 3 && parts[2] == "newkey";
        }

        private static LedgerEntry RegKvRecord(Txn t)
        {
            RegValue rv = Reg.Read(t.Op.Hive, t.Op.Path, t.Op.Name);
            bool keyExisted = Reg.KeyExists(t.Op.Hive, t.Op.Path);
            bool subExisted = t.Before != Absent;

            LedgerEntry e = NewEntry(t, "RegKv");
            e.Hive = t.Op.Hive;
            e.Path = t.Op.Path;
            e.Name = t.Op.Name;
            e.ValueType = "String";
            e.Existed = subExisted;
            e.OldValue = subExisted ? t.Before : null;
            e.NewValue = t.Op.Delete ? null : t.Op.Value;
            // Extra 编码：KvKey|原父值是否存在(1/0)|[newkey]
            e.Extra = (t.Op.KvKey ?? "") + "|" + (rv.Existed ? "1" : "0") + (keyExisted ? "" : "|newkey");
            return e;
        }

        private static string RegKvWrite(Txn t)
        {
            if (string.IsNullOrEmpty(t.Op.KvKey)) return "缺少分号子键名 KvKey";
            RegValue rv = Reg.Read(t.Op.Hive, t.Op.Path, t.Op.Name);
            if (rv.Denied) throw new UnauthorizedAccessException("注册表键拒绝访问");
            if (rv.Error != null) return "读取复合字符串失败：" + rv.Error;
            string raw = rv.Existed ? Reg.ToText(rv.Value) : "";
            string next = UpdateSemicolonKv(raw, t.Op.KvKey, t.Op.Value, t.Op.Delete);
            if (next.Length == 0 && t.Op.Delete) Reg.Delete(t.Op.Hive, t.Op.Path, t.Op.Name);
            else Reg.Write(t.Op.Hive, t.Op.Path, t.Op.Name, next, "String");
            return null;
        }

        private static string RegKvVerify(Txn t)
        {
            string now = Probe(t.Op);
            if (!Matches(t.Op, now)) return "写入后校验不一致：" + now;
            return null;
        }

        private static OpResult RevertRegKv(LedgerEntry e)
        {
            string kvKey = ExtractRegKvKey(e.Extra);
            if (string.IsNullOrEmpty(kvKey))
                return OpResult.Fail("复合注册表记录缺少子键名，无法还原", StepRevertName, 0);

            bool parentExisted = ExtractRegKvValExisted(e.Extra);
            bool newKey = ExtractRegKvNewKey(e.Extra);

            RegValue cur = Reg.Read(e.Hive, e.Path, e.Name);
            if (cur.Denied) return OpResult.Denied("还原前读取被拒绝（需要管理员）", StepRevertName, 0);
            if (cur.Error != null) return OpResult.Fail("还原前读取失败：" + cur.Error, StepRevertName, 0);

            string raw = cur.Existed ? Reg.ToText(cur.Value) : "";
            string next = UpdateSemicolonKv(raw, kvKey, e.OldValue, !e.Existed);
            if (next.Length == 0 && !parentExisted) Reg.Delete(e.Hive, e.Path, e.Name);
            else Reg.Write(e.Hive, e.Path, e.Name, next, "String");

            // 写后回读校验该子键是否精确复原
            RegValue back = Reg.Read(e.Hive, e.Path, e.Name);
            if (back.Denied) return OpResult.Denied("还原后回读被拒绝（需要管理员）", StepRevertName, 0);
            if (back.Error != null) return OpResult.Fail("还原后回读失败：" + back.Error, StepRevertName, 0);

            string backVal = null;
            bool hasSub = back.Existed && TryParseSemicolonKv(Reg.ToText(back.Value), kvKey, out backVal);
            bool ok = e.Existed ? (hasSub && string.Equals(backVal, e.OldValue ?? "", StringComparison.OrdinalIgnoreCase)) : !hasSub;
            if (!ok) return OpResult.Fail("还原后子键回读不一致：" + (hasSub ? backVal : Absent), StepRevertName, 0);

            if (newKey && !back.Existed) Reg.TryDeleteEmptyKey(e.Hive, e.Path);
            return OpResult.Good("已还原", 0, null);
        }
    }
}
