// ============================================================================
//  Ops.cs — 原子操作：探针 / 写前快照 / 应用 / 校验 / 还原
//  所有变更都必须先落账本，再写系统。
//
//  Rev86 重构（写入路径是本应用唯一会改用户机器的地方，所以这里的失败必须可读）：
//   · Probe 改成「值类型 → 读取器」表：新增一种动作只加一行映射，不动探测逻辑；
//   · Apply 改成显式步骤状态机：探测 → 记录原值 → 写入 → 校验，每一步失败都带着
//     「停在第几步 / 人类可读原因 / 技术细节」返回，不再用一个巨型 try 把八种失败
//     揉成一句「应用失败」；
//   · 失败补偿（把已写入的值退回原值）本身是一步，它失败也会被写进结果；
//   · 结局统一成 OpResult.Status（成功 / 跳过 / 被拒 / 失败）。
//
//  文件按职责拆开（HANDOFF「单文件不超过 800 行」）：
//   · Ops.cs        探测表 / 值比较 / 步骤骨架 / 进程执行 —— 与具体动作类型无关的部分；
//   · Ops.Apply.cs  步骤计划（每种 Kind 的探测→记账→写入→校验差异）与命令类；
//   · Ops.Revert.cs 还原路径（每种 Kind 写回原值并回读校验）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Go
{
    public class Op
    {
        public string Kind = "Reg";   // Reg / RegKv / Bcd / PowerAc / Command / Hibernate
        public string Hive;
        public string Path;
        public string Name;
        public string KvKey;          // RegKv 专用：分号串里的子键名
        public string Type = "DWord";
        public string Value;
        public bool Delete;           // 删除该值
        public string Expect;         // 期望读回（null = 用 Value 推断）

        // Bcd: Name/Value | PowerAc: Path=Sub, Name=Set, Value=索引 | Command: Path=exe, Value=args
        public string Sub { get { return Path; } }

        public string Truth
        {
            get
            {
                if (Expect != null) return Expect;
                if (Delete) return "(不存在)";
                return Value;
            }
        }
    }

    /// <summary>一次操作的结局。四种语义必须分开：页面据此决定说「已完成」「没动」还是「没权限」。</summary>
    public enum OpStatus
    {
        Ok = 0,        // 成功：写入并校验通过
        Skipped = 1,   // 跳过：目标状态本来就成立，这次没有写（结果仍算成功）
        Denied = 2,    // 被拒：权限不足 / 拒绝访问
        Failed = 3     // 失败：读不到、写不进、校验不一致、回滚失败
    }

    public class OpResult
    {
        /// <summary>结局。Ok 与 Skipped 都算成功——旧调用点只看 Ok，语义保持不变。</summary>
        public OpStatus Status = OpStatus.Failed;
        /// <summary>人类可读的结论/原因。页面直接显示这一句，不再显示「失败(1)」。</summary>
        public string Message;
        /// <summary>技术细节（异常类型、回读值、命令输出片段）：给诊断用，不直接给用户看。</summary>
        public string Detail;
        /// <summary>失败停在哪一步（探测 / 记录 / 写入 / 校验 / 执行 / 还原）。</summary>
        public string Step;
        /// <summary>已完成的步数：0 未开始，1 探测，2 记账，3 写入，4 校验。</summary>
        public int Committed;
        /// <summary>失败后是否已经把改动退回原值（false = 系统可能停在半改状态，需要人工处理）。</summary>
        public bool RolledBack;
        /// <summary>本次写入留下的写前记录。null = 没留下（跳过 / 记账失败 / 已回滚）。</summary>
        public LedgerEntry Entry;

        public bool Ok { get { return Status == OpStatus.Ok || Status == OpStatus.Skipped; } }

        internal static OpResult Make(OpStatus st, string msg, string step, int committed)
        {
            OpResult r = new OpResult();
            r.Status = st;
            r.Message = msg;
            r.Step = step;
            r.Committed = committed;
            return r;
        }

        internal static OpResult Good(string msg, int committed, LedgerEntry e)
        {
            OpResult r = Make(OpStatus.Ok, msg, null, committed);
            r.Entry = e;
            return r;
        }

        internal static OpResult Skip(string msg, int committed)
        {
            return Make(OpStatus.Skipped, msg, null, committed);
        }

        internal static OpResult Denied(string msg, string step, int committed)
        {
            return Make(OpStatus.Denied, msg, step, committed);
        }

        internal static OpResult Fail(string msg, string step, int committed)
        {
            return Make(OpStatus.Failed, msg, step, committed);
        }
    }

    public static class Mem
    {
        // SystemMemoryListInformation = 80; command: 1=EmptyWorkingSets 3=FlushModifiedList 4=PurgeStandbyList
        public static bool PurgeStandby()
        {
            return SendMemListCmd(4, "mem.purge");
        }

        public static bool FlushModified()
        {
            return SendMemListCmd(3, "mem.flush");
        }

        private static bool SendMemListCmd(int cmd, string diagTag)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(buf, cmd);
                int rc = Native.NtSetSystemInformation(80, buf, sizeof(int));
                return rc == 0;
            }
            catch (Exception ex)
            {
                Diag.Fail(diagTag, ex);
                return false;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }
    }

    public static partial class Ops
    {
        public const string Absent = "(不存在)";
        public const string DeniedText = "(拒绝访问)";
        public const string ReadFailText = "(读取失败)";
        public const string ErrorText = "(错误)";
        public const string UnsupportedText = "(不支持)";
        public const string PendingText = "(读取中)";

        private const int PowerAcCacheTtlMs = 15000;

        /// <summary>
        /// 控制台工具的输出编码：bcdedit / powercfg 走的是控制台 OEM 代码页，
        /// 中文系统上是 936(GBK)。旧实现按 UTF-8 解码，得到的是 U+FFFD 乱码 ——
        /// 于是 powercfg 的中文标签永远匹配不上，探针恒返回「不存在」，
        /// 应用报「未确认」、还原也报「未确认还原」（用户在中文系统上必然踩到）。
        /// </summary>
        private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

        private const string StepProbeName = "探测";
        private const string StepRecordName = "记录";
        private const string StepWriteName = "写入";
        private const string StepVerifyName = "校验";
        private const string StepRunName = "执行";
        private const string StepRevertName = "还原";

        private static Encoding _consoleEnc;
        private static Encoding ConsoleEnc
        {
            get
            {
                if (_consoleEnc != null) return _consoleEnc;
                try
                {
                    if (Encoding.Default.CodePage == 65001)
                        _consoleEnc = new UTF8Encoding(false);
                    else
                        _consoleEnc = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                }
                catch (Exception ex) { Diag.Fail("ops.console.enc", ex); _consoleEnc = Encoding.Default; }
                return _consoleEnc;
            }
        }

        // ---- 探针：值类型 → 读取器 ------------------------------------------
        // 旧实现是一条 70 行的 if 链：每加一种动作类型都要改 Probe 本体，改漏一处就是
        // 「新动作恒判未应用」。现在只改这张表；Probe 本体不认识任何具体 Kind。
        private static readonly Dictionary<string, Func<Op, string>> _readers = BuildReaders();

        private static Dictionary<string, Func<Op, string>> BuildReaders()
        {
            Dictionary<string, Func<Op, string>> m = new Dictionary<string, Func<Op, string>>(StringComparer.Ordinal);
            m["Reg"] = ReadRegValue;
            m["RegKv"] = ReadRegKvValue;
            m["Bcd"] = ReadBcdValue;
            m["PowerAc"] = ReadPowerAcValue;
            m["Hibernate"] = ReadHibernateValue;
            return m;
        }

        private sealed class PowerAcCacheEntry
        {
            public string Value;
            public int UpdatedTick;
        }

        private static readonly object _powerCacheLock = new object();
        private static readonly Dictionary<string, PowerAcCacheEntry> _powerCache =
            new Dictionary<string, PowerAcCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Op> _knownPowerOps =
            new Dictionary<string, Op>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Action> _powerWaiters = new List<Action>();
        private static int _powerCacheGen;
        private static bool _powerRefreshing;

        private static string PowerAcKey(Op op)
        {
            if (op == null) return "";
            return (op.Path ?? "").Trim() + "|" + (op.Name ?? "").Trim();
        }

        public static void RegisterPowerAcOp(Op op)
        {
            if (op == null || op.Kind != "PowerAc") return;
            string key = PowerAcKey(op);
            if (key.Length <= 1) return;
            lock (_powerCacheLock)
            {
                if (!_knownPowerOps.ContainsKey(key))
                    _knownPowerOps[key] = new Op { Kind = "PowerAc", Path = op.Path, Name = op.Name };
            }
        }

        public static void InvalidatePowerAcCache()
        {
            lock (_powerCacheLock)
            {
                _powerCacheGen++;
                _powerCache.Clear();
            }
        }

        /// <summary>供界面层非阻塞读取：PowerAc 走短期缓存并在冷/过期时触发后台刷新，不阻塞 UI 线程派生 powercfg.exe。</summary>
        public static string ProbeForUi(Op op)
        {
            return ProbeForUi(op, null);
        }

        public static string ProbeForUi(Op op, Action onUpdated)
        {
            if (op == null || string.IsNullOrEmpty(op.Kind)) return ErrorText;
            if (!string.Equals(op.Kind, "PowerAc", StringComparison.Ordinal)) return Probe(op);

            RegisterPowerAcOp(op);
            string key = PowerAcKey(op);
            PowerAcCacheEntry entry;
            bool needRefresh = false;
            lock (_powerCacheLock)
            {
                if (_powerCache.TryGetValue(key, out entry))
                {
                    int age = unchecked(Environment.TickCount - entry.UpdatedTick);
                    if (age < 0 || age > PowerAcCacheTtlMs) needRefresh = true;
                }
                else
                {
                    entry = null;
                    needRefresh = true;
                }
            }
            if (needRefresh) RefreshPowerAcCacheAsync(onUpdated);
            return entry != null ? entry.Value : PendingText;
        }

        public static void RefreshPowerAcCacheAsync(Action onUpdated)
        {
            List<Op> ops = null;
            Action[] immediate = null;
            lock (_powerCacheLock)
            {
                if (onUpdated != null) _powerWaiters.Add(onUpdated);
                if (_powerRefreshing) return;
                if (_knownPowerOps.Count == 0)
                {
                    if (_powerWaiters.Count > 0)
                    {
                        immediate = _powerWaiters.ToArray();
                        _powerWaiters.Clear();
                    }
                }
                else
                {
                    _powerRefreshing = true;
                    ops = new List<Op>(_knownPowerOps.Values);
                }
            }
            if (immediate != null)
            {
                InvokePowerWaiters(immediate);
                return;
            }
            if (ops == null) return;

            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        Guid scheme = Power.Active();
                        for (int i = 0; i < ops.Count; i++)
                        {
                            try { ReadPowerAcValueWithScheme(ops[i], scheme); }
                            catch (Exception ex) { Diag.Fail("ops.powercache.probe", ex); }
                        }
                    }
                    finally
                    {
                        Action[] cbs = null;
                        lock (_powerCacheLock)
                        {
                            _powerRefreshing = false;
                            if (_powerWaiters.Count > 0)
                            {
                                cbs = _powerWaiters.ToArray();
                                _powerWaiters.Clear();
                            }
                        }
                        if (cbs != null) InvokePowerWaiters(cbs);
                    }
                });
            }
            catch (Exception ex)
            {
                Diag.Fail("ops.powercache.queue", ex);
                Action[] cbs = null;
                lock (_powerCacheLock)
                {
                    _powerRefreshing = false;
                    if (_powerWaiters.Count > 0)
                    {
                        cbs = _powerWaiters.ToArray();
                        _powerWaiters.Clear();
                    }
                }
                if (cbs != null) InvokePowerWaiters(cbs);
            }
        }

        private static void InvokePowerWaiters(Action[] cbs)
        {
            for (int i = 0; i < cbs.Length; i++)
            {
                if (cbs[i] == null) continue;
                try { cbs[i](); }
                catch (Exception ex) { Diag.Fail("ops.powercache.cb", ex); }
            }
        }

        /// <summary>读当前值。返回的是表层字符串（含 (不存在)/(拒绝访问) 这类可分辨标记）。</summary>
        public static string Probe(Op op)
        {
            if (op == null || string.IsNullOrEmpty(op.Kind)) return ErrorText;
            Func<Op, string> read;
            if (!_readers.TryGetValue(op.Kind, out read)) return UnsupportedText;
            try { return read(op); }
            catch (Exception ex)
            {
                // 探针异常单独成一档：调用方看到 (错误) 会判为未应用，不会误判成 (不存在)。
                Diag.Fail("ops.probe", ex);
                return ErrorText;
            }
        }

        private static string ReadRegValue(Op op)
        {
            RegValue rv = Reg.Read(op.Hive, op.Path, op.Name);
            if (rv.Denied) return DeniedText;
            // 读异常 ≠ 值不存在：给调用方一个可分辨的字符串（页面会显示状态，Diag 有原因）。
            if (rv.Error != null) return ReadFailText;
            if (!rv.Existed) return Absent;
            return Reg.ToText(rv.Value);
        }

        private static readonly object _bcdCacheLock = new object();
        private static Dictionary<string, string> _bcdCache;
        private static int _bcdCacheTick;
        private const int BcdCacheTtlMs = 15000;

        public static void InvalidateBcdCache()
        {
            lock (_bcdCacheLock) { _bcdCache = null; }
        }

        private static string ReadBcdValue(Op op)
        {
            string key = (op == null || op.Name == null) ? "" : op.Name.Trim();
            if (key.Length == 0) return Absent;

            Dictionary<string, string> map = null;
            int now = Environment.TickCount;
            lock (_bcdCacheLock)
            {
                if (_bcdCache != null)
                {
                    int age = unchecked(now - _bcdCacheTick);
                    if (age >= 0 && age <= BcdCacheTtlMs) map = _bcdCache;
                }
            }
            if (map == null)
            {
                string outp = Run("bcdedit", "/enum {current}", 8000);
                map = ParseBcdMap(outp);
                lock (_bcdCacheLock)
                {
                    _bcdCache = map;
                    _bcdCacheTick = Environment.TickCount;
                }
            }
            string val;
            return map.TryGetValue(key, out val) ? val : Absent;
        }

        private static Dictionary<string, string> ParseBcdMap(string outp)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(16, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(outp)) return map;
            string[] lines = outp.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                int sp = t.IndexOf(' ');
                if (sp <= 0) continue;
                string k = t.Substring(0, sp);
                if (!map.ContainsKey(k)) map[k] = t.Substring(sp).Trim();
            }
            return map;
        }

        private static string ReadPowerAcValue(Op op)
        {
            return ReadPowerAcValueWithScheme(op, Guid.Empty);
        }

        private static string ReadPowerAcValueWithScheme(Op op, Guid schemeHint)
        {
            RegisterPowerAcOp(op);
            int gen;
            lock (_powerCacheLock) { gen = _powerCacheGen; }

            // 优先走原生 powrprof 读取 AC 索引（免派生 powercfg 进程与本地化解析；批量刷新复用同一份 Active()）。
            uint nativeVal;
            bool ok = schemeHint != Guid.Empty
                ? Power.ReadAcSetting(schemeHint, op.Path, op.Name, out nativeVal)
                : Power.ReadAcSetting(op.Path, op.Name, out nativeVal);
            if (ok)
            {
                string fast = nativeVal.ToString("X");
                StorePowerAcCache(op, fast, gen);
                return fast;
            }

            string outp = Run("powercfg", "/q scheme_current " + op.Path + " " + op.Name, 8000);
            string[] lines = outp.Split('\n');
            bool grab = false;
            string result = Absent;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (!grab)
                {
                    if (!IsPowerAcLabel(t)) continue;
                    // 中文 powercfg 把标签和值打在同一行："当前交流电源设置索引: 0x00000064"。
                    // 命中标签后直接 continue 会跳过这一行，后面再没有以 0x 开头的行 —— 恒判「不存在」。
                    int colon = t.IndexOf(':');
                    if (colon > 0)
                    {
                        string same = t.Substring(colon + 1).Trim();
                        if (same.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            result = HexValue(same);
                            break;
                        }
                    }
                    grab = true;
                    continue;
                }
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    result = HexValue(t);
                    break;
                }
            }
            StorePowerAcCache(op, result, gen);
            return result;
        }

        private static void StorePowerAcCache(Op op, string val, int expectedGen)
        {
            string key = PowerAcKey(op);
            if (key.Length <= 1) return;
            lock (_powerCacheLock)
            {
                if (_powerCacheGen != expectedGen) return;
                PowerAcCacheEntry e = new PowerAcCacheEntry();
                e.Value = val;
                e.UpdatedTick = Environment.TickCount;
                _powerCache[key] = e;
            }
        }

        private static bool IsPowerAcLabel(string t)
        {
            return t.Contains("当前交流电源设置索引")
                || t.IndexOf("AC Power Setting Index", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string HexValue(string s)
        {
            // 解析失败会抛：Probe 把它降级成 (错误)，不会被当成「不存在」。
            return Convert.ToInt64(s.Substring(2), 16).ToString("X");
        }

        private static string ReadHibernateValue(Op op)
        {
            string drive = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
            return File.Exists(System.IO.Path.Combine(drive, "hiberfil.sys")) ? "1" : "0";
        }

        public static bool Matches(Op op, string current)
        {
            string want = op.Truth;
            if (current == null) return false;
            if (current == DeniedText) return false;
            if (want == Absent) return current == Absent;

            if (op.Kind == "Reg")
            {
                if (op.Type.Equals("String", StringComparison.OrdinalIgnoreCase))
                    return string.Equals(current, want, StringComparison.OrdinalIgnoreCase);

                // 数值型不能按字符串比：DWord 的 -1 与 4294967295 是同一个值
                long a, b;
                if (TryNumber(current, op.Type, out a) && TryNumber(want, op.Type, out b)) return a == b;
            }
            return string.Equals(current.Trim(), want.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把注册表读回值与期望值统一成可比较的 64 位整数。</summary>
        public static bool TryNumber(string s, string type, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            long raw;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                ulong u;
                if (!ulong.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out u)) return false;
                raw = unchecked((long)u);
            }
            else if (!long.TryParse(s, out raw)) return false;

            if (type.Equals("DWord", StringComparison.OrdinalIgnoreCase))
            {
                value = (long)((ulong)raw & 0xFFFFFFFFUL);
                return true;
            }
            value = raw;
            return true;
        }

        /// <summary>写后类型校验：REG_SZ 的 "1" 与 DWORD 1 不是同一个东西。</summary>
        public static bool TypeMatches(Op op)
        {
            if (op.Kind != "Reg" || op.Delete) return true;
            RegValue rv = Reg.Read(op.Hive, op.Path, op.Name);
            if (!rv.Existed) return false;
            RegistryValueKind want = Reg.ParseKind(op.Type);
            if (rv.Kind == want) return true;
            // String 与 ExpandString 在读取侧等价（Reg.Read 用 DoNotExpandEnvironmentNames 读原文），
            // 不判成不一致，否则正常的 REG_EXPAND_SZ 值会被误报失败并回滚。
            if (want == RegistryValueKind.String && rv.Kind == RegistryValueKind.ExpandString) return true;
            if (want == RegistryValueKind.ExpandString && rv.Kind == RegistryValueKind.String) return true;
            return false;
        }

        private static string KindOf(Op op)
        {
            RegValue rv = Reg.Read(op.Hive, op.Path, op.Name);
            return rv.Existed ? rv.Kind.ToString() : "不存在";
        }

        /// <summary>当前注册表值是否仍等于账本里的原值（判断「是否需要回滚」）。</summary>
        private static bool ValueIsOriginal(LedgerEntry e)
        {
            RegValue now = Reg.Read(e.Hive, e.Path, e.Name);
            // 读不到（拒绝访问 / IO 错）时 Existed 恒为 false，而 e.Existed=false 的写入
            // 会在这里被判成「原值未改变」，写前记录被删掉 —— 那次改动从此无法还原。
            // 读不到就按「可能已经改了」处理：保留记录并走回滚。
            if (now.Denied || now.Error != null) return false;
            if (now.Existed != e.Existed) return false;
            if (!e.Existed) return true;
            return string.Equals(Reg.ToText(now.Value), e.OldValue ?? "", StringComparison.Ordinal);
        }

        // ---- 进程执行 --------------------------------------------------------
        private sealed class ProcessOutcome
        {
            public string Output = "";
            public bool TimedOut;
            public string StartError;
            public int ExitCode;
        }

        /// <summary>
        /// 执行外部命令。输出编码走 OEM 代码页（见 ConsoleEnc）；两个管道并发读取，
        /// 先读完 stdout 再读 stderr 在输出量大时会把对方管道写满，双方互等直接死锁。
        /// </summary>
        private static ProcessOutcome Exec(string exe, string args, int timeoutMs)
        {
            ProcessOutcome o = new ProcessOutcome();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = ConsoleEnc;
                psi.StandardErrorEncoding = ConsoleEnc;
                using (Process p = Process.Start(psi))
                {
                    System.Threading.Tasks.Task<string> so = p.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); }
                        catch (Exception kx)
                        {
                            // 降级：进程已自行退出时 Kill 会抛；无论如何本次都返回 (超时)。
                            Diag.Fail("ops.run.kill", kx);
                        }
                        o.TimedOut = true;
                        return o;
                    }
                    StringBuilder sb = new StringBuilder();
                    try { sb.Append(so.Result); }
                    catch (Exception ex) { /* stdout 丢掉会让提示不完整，但判定已由回读完成；记账以便查出管道泄漏 */ Diag.Fail("ops.run.stdout", ex); }
                    try { sb.Append(se.Result); }
                    catch (Exception ex) { /* 同上：stderr 读取失败不能把已有 stdout 一起丢掉 */ Diag.Fail("ops.run.stderr", ex); }
                    o.Output = sb.ToString();
                    o.ExitCode = p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                // 命令执行失败是「按钮点了没反应」的常见成因，必须能在 Diag 里看到次数。
                Diag.Fail("ops.run.start", ex);
                o.StartError = ex.Message;
            }
            return o;
        }

        public static string Run(string exe, string args, int timeoutMs)
        {
            ProcessOutcome o = Exec(exe, args, timeoutMs);
            if (o.TimedOut) return "(超时)";
            if (o.StartError != null) return "(失败: " + o.StartError + ")";
            return o.Output;
        }
        private static string Key(Op op)
        {
            if (op == null) return "?";
            if (op.Kind == "Reg") return op.Hive + "\\" + op.Path + "\\" + op.Name;
            if (op.Kind == "RegKv") return op.Hive + "\\" + op.Path + "\\" + op.Name + "[" + (op.KvKey ?? "") + "]";
            return op.Kind + " " + (op.Name ?? op.Path ?? "");
        }

        private static string Key(LedgerEntry e)
        {
            if (e == null) return "?";
            if (e.Kind == "Reg") return e.Hive + "\\" + e.Path + "\\" + e.Name;
            if (e.Kind == "RegKv") return e.Hive + "\\" + e.Path + "\\" + e.Name + "[" + ExtractRegKvKey(e.Extra) + "]";
            return e.Kind + " " + (e.Name ?? "");
        }

        private static string Shorten(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }
    }
}