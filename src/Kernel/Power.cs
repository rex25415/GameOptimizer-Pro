// ============================================================================
//  Power.cs — 电源计划（真实读写，不是装饰）
//
//  为什么要有这一层：
//  总览页右上角原来那个 "PROFILE 平衡 · BALANCED" 是个纯装饰——文案写死，
//  点击没有处理，用户以为它有多个方案可选，实际永远只有"平衡"。
//  这里直接用 powrprof 读活动方案、枚举全部方案、切换方案，
//  不 spawn powercfg（进程启动 30~80ms，且解析本地化输出很脆）。
//
//  权限：读取枚举普通用户即可；切换通常需要管理员。失败原样返回，由调用方提示。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Go
{
    public static partial class Power
    {
        private const int AccessScheme = 16;   // ACCESS_SCHEME
        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_MORE_DATA = 234;

        // 三个微软内置方案 + 卓越性能（需手动创建）
        public static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid HighPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static readonly Guid Saver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        public static readonly Guid Ultimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
        [DllImport("powrprof.dll")]
        private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, int flags, int index, IntPtr buffer, ref int size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr root, IntPtr scheme, IntPtr sub, IntPtr lang, IntPtr buffer, ref int size);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr h);

        // ====================================================================
        //  恢复点：优化软件绝不能把用户的电源方案留在改过的状态。
        //  正常退出 → ProcessExit 里恢复；被杀进程/蓝屏/断电 → 下次启动第一件事恢复。
        //  只有当"当前方案仍是我们设的那一个"才恢复：用户自己又改过就尊重用户。
        // ====================================================================
        private sealed class RestorePoint
        {
            public Guid Original;
            public Guid Applied;
        }

        private static readonly object _rpLock = new object();
        private static readonly object _nameCacheLock = new object();
        private static readonly Dictionary<Guid, string> _friendlyCache = new Dictionary<Guid, string>(16);
        private static bool _exitHooked;

        /// <summary>
        /// 只读模式（--render / 托盘预览）：绝不修改活动电源计划或消费恢复点。
        /// </summary>
        public static volatile bool ReadOnlyMode;

        /// <summary>
        /// 退出时是否主动把临时切换的方案切回原值。
        /// 默认 true（仅针对 trackTemporary=true 的会话临时方案；用户主动在下拉选定的方案会保留）。
        /// </summary>
        public static bool RestoreOnExit = true;

        static Power()
        {
            if (ReadOnlyMode) return;
            try
            {
                if (!_exitHooked)
                {
                    _exitHooked = true;
                    AppDomain.CurrentDomain.ProcessExit += delegate
                    {
                        if (RestoreOnExit && !ReadOnlyMode) RestorePending("process-exit");
                    };
                }
            }
            catch (Exception ex)
            {
                // 挂不上退出钩子只是少一条恢复路径；崩溃恢复仍在下次启动时的静态构造里生效。
                Audit("POWER 退出钩子注册失败: " + ex.Message);
            }
            try { if (!ReadOnlyMode) RestorePending("startup-recovery"); }
            catch (Exception ex)
            {
                // 静态构造函数里绝不能再抛：那会让整个 Power 类型永久不可用（TypeInitializationException）。
                Audit("POWER 启动恢复异常: " + ex.Message);
            }
        }

        /// <summary>零装箱从非托管 16 字节指针读取 Guid。</summary>
        private static Guid ReadGuid(IntPtr p)
        {
            int a = Marshal.ReadInt32(p, 0);
            short b = Marshal.ReadInt16(p, 4);
            short c = Marshal.ReadInt16(p, 6);
            return new Guid(a, b, c,
                Marshal.ReadByte(p, 8), Marshal.ReadByte(p, 9), Marshal.ReadByte(p, 10), Marshal.ReadByte(p, 11),
                Marshal.ReadByte(p, 12), Marshal.ReadByte(p, 13), Marshal.ReadByte(p, 14), Marshal.ReadByte(p, 15));
        }

        /// <summary>当前活动电源计划。失败返回 Guid.Empty。</summary>
        public static Guid Active()
        {
            IntPtr p;
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out p) != ERROR_SUCCESS) return Guid.Empty;
            }
            catch (Exception ex)
            {
                Diag.Fail("power.active", ex);
                return Guid.Empty;
            }
            if (p == IntPtr.Zero) return Guid.Empty;
            try { return ReadGuid(p); }
            catch (Exception ex)
            {
                Diag.Fail("power.active.parse", ex);
                return Guid.Empty;
            }
            finally { try { LocalFree(p); } catch { /* 可忽略：LocalFree 失败最多泄漏 16 字节，不能覆盖上面的返回值 */ } }
        }

        /// <summary>枚举本机所有电源计划（单块 16 字节复用缓冲，消除循环内 64 次 Alloc/Free）。</summary>
        public static List<Guid> Schemes()
        {
            List<Guid> list = new List<Guid>(8);
            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(16);
                for (int i = 0; i < 64; i++)
                {
                    int size = 16;
                    uint r = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buf, ref size);
                    if (r != ERROR_SUCCESS && r != ERROR_MORE_DATA) break;
                    Guid g = ReadGuid(buf);
                    if (g != Guid.Empty) list.Add(g);
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("power.schemes", ex);
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
            return list;
        }

        /// <summary>方案的本地化名称（带进程级有界缓存，避免周期采样反复分配非托管缓冲）。</summary>
        public static string FriendlyName(Guid scheme)
        {
            if (scheme == Guid.Empty) return null;
            lock (_nameCacheLock)
            {
                string cached;
                if (_friendlyCache.TryGetValue(scheme, out cached)) return cached;
            }
            IntPtr g = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            try
            {
                g = Marshal.AllocHGlobal(16);
                Marshal.StructureToPtr(scheme, g, false);
                int size = 0;
                PowerReadFriendlyName(IntPtr.Zero, g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
                if (size <= 1) return null;
                buf = Marshal.AllocHGlobal(size);
                if (PowerReadFriendlyName(IntPtr.Zero, g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != ERROR_SUCCESS) return null;
                string s = Marshal.PtrToStringUni(buf);
                if (s == null) return null;
                s = s.Trim();
                if (s.Length == 0) return null;
                lock (_nameCacheLock)
                {
                    if (_friendlyCache.Count < 64) _friendlyCache[scheme] = s;
                }
                return s;
            }
            catch (Exception ex)
            {
                Diag.Fail("power.friendly", ex);
                return null;
            }
            finally
            {
                try { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); } catch { /* 可忽略 */ }
                try { if (g != IntPtr.Zero) Marshal.FreeHGlobal(g); } catch { /* 可忽略 */ }
            }
        }

        /// <summary>切换活动方案（用户主动选择：退出后保持）。true 表示内核已接受。</summary>
        public static bool SetActive(Guid scheme)
        {
            return SetActive(scheme, false);
        }

        /// <summary>切换活动方案；trackTemporary=true 时记录恢复点，退出或下次启动自动还原。</summary>
        public static bool SetActive(Guid scheme, bool trackTemporary)
        {
            if (ReadOnlyMode) return false;
            Guid before;
            try { before = Active(); }
            catch (Exception ex)
            {
                // 读不到旧值 → 仍然允许切换，但无法承诺恢复；记一条，别把失败当成功。
                Audit("POWER 读取切换前方案失败: " + ex.Message);
                before = Guid.Empty;
            }

            bool ok;
            try
            {
                Guid g = scheme;
                ok = PowerSetActiveScheme(IntPtr.Zero, ref g) == ERROR_SUCCESS;
            }
            catch (Exception ex)
            {
                Audit("POWER 切换异常: " + ex.Message);
                ok = false;
            }
            if (!ok) return false;

            if (trackTemporary)
            {
                // 临时提档才写恢复点。记录失败不影响切换结果，但必须留痕。
                try { TrackChange(before, scheme); }
                catch (Exception ex) { Audit("POWER 写恢复点失败: " + ex.Message); }
            }
            else
            {
                // 用户手动选择了新的持久方案，清掉过期临时恢复点避免下次启动误切回。
                lock (_rpLock) { ClearRestorePoint(); }
            }
            return true;
        }

        /// <summary>是否有待恢复的电源方案改动（自检/界面只读查询）。</summary>
        public static bool PendingRestore
        {
            get
            {
                try { return ReadRestorePoint() != null; }
                catch (Exception ex) { Audit("POWER 查询恢复点失败: " + ex.Message); return false; }
            }
        }

        /// <summary>
        /// 按恢复点把方案切回原值。仅当"当前方案 == 我们设的那个"才动手。
        /// 幂等、可重入；进程退出、下次启动、以及任何调用方都可以直接调。
        /// </summary>
        public static bool RestorePending(string reason)
        {
            if (ReadOnlyMode) return false;
            lock (_rpLock)
            {
                RestorePoint p;
                try { p = ReadRestorePoint(); }
                catch (Exception ex) { Audit("POWER 恢复点读取失败: " + ex.Message); return false; }
                if (p == null) return false;

                Guid cur = Active();
                if (cur == Guid.Empty)
                {
                    // 当前方案读不到（权限/内核异常）：什么都别做，也绝不删恢复点——
                    // 删掉就等于永久丢失原始值。
                    return false;
                }
                if (cur != p.Applied)
                {
                    // 当前不是我们设的那个：用户自己改过，尊重用户；只清掉过期恢复点。
                    ClearRestorePoint();
                    Audit("POWER 放弃恢复（当前方案已被改动） cur=" + cur + " applied=" + p.Applied + " reason=" + reason);
                    return false;
                }

                bool ok;
                try
                {
                    Guid g = p.Original;
                    ok = PowerSetActiveScheme(IntPtr.Zero, ref g) == ERROR_SUCCESS;
                }
                catch (Exception ex)
                {
                    Audit("POWER 恢复异常: " + ex.Message);
                    ok = false;
                }

                if (ok && Active() == p.Original)
                {
                    ClearRestorePoint();
                    Audit("POWER 已恢复原方案 " + p.Original + " (撤销 " + p.Applied + ", reason=" + reason + ")");
                    return true;
                }
                Audit("POWER 恢复失败 original=" + p.Original + " applied=" + p.Applied + " reason=" + reason);
                return false;
            }
        }

        /// <summary>切换成功后维护恢复点：永远保留最早的原值，切回原值即视为已兑现。</summary>
        private static void TrackChange(Guid before, Guid applied)
        {
            if (applied == Guid.Empty || before == Guid.Empty) return;
            lock (_rpLock)
            {
                RestorePoint p = ReadRestorePoint();
                if (p != null && p.Original == applied)
                {
                    // 又切回了最初那一个 → 恢复点已兑现（自检、用户来回切都走这条）。
                    ClearRestorePoint();
                    return;
                }
                if (before == applied) return;      // 没有变化
                if (p == null) p = new RestorePoint();
                p.Original = p.Original == Guid.Empty ? before : p.Original;
                p.Applied = applied;
                WriteRestorePoint(p);
            }
        }

        private static string RestorePath()
        {
            string dir = Ledger.CurrentDir;
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, "power-restore.txt");
        }

        private static RestorePoint ReadRestorePoint()
        {
            string path = RestorePath();
            if (path == null || !File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            RestorePoint p = new RestorePoint();
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;              // 残行/空行跳过，不让一行坏数据废掉整个恢复点
                string k = t.Substring(0, eq);
                string v = t.Substring(eq + 1);
                Guid g;
                if (!Guid.TryParse(v, out g)) continue;
                if (k == "original") p.Original = g;
                else if (k == "applied") p.Applied = g;
            }
            if (p.Original == Guid.Empty || p.Applied == Guid.Empty) return null;
            return p;
        }

        private static void WriteRestorePoint(RestorePoint p)
        {
            string path = RestorePath();
            if (path == null) return;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            StringBuilder sb = new StringBuilder();
            sb.Append("original=").Append(p.Original.ToString("D")).Append("\n");
            sb.Append("applied=").Append(p.Applied.ToString("D")).Append("\n");
            sb.Append("time=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n");
            // 原子写：直接 WriteAllText 时断电会留下 0 字节文件，ReadRestorePoint 返 null，
            // 电源方案从此永不恢复——恰好击穿"断电恢复"的设计目标。tmp + Replace 同 BenchHistory。
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception cx) { Diag.Fail("power.restore.tmpclean", cx); }
                throw;
            }
        }

        private static void ClearRestorePoint()
        {
            try
            {
                string path = RestorePath();
                if (path != null && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                // 删不掉只意味着下次启动会再判定一次（当前方案已变时会自行放弃），不影响正确性。
                Audit("POWER 清理恢复点失败: " + ex.Message);
            }
        }

        private static void Audit(string line)
        {
            try { Ledger.Audit(line); }
            catch (Exception ex)
            {
                // 审计写不进去不能反过来让电源逻辑失败。先 Diag 记账（不碰 IO），
                // 再退到托盘 trace 留证；两条都失败也不能在这里抛。
                Diag.Fail("power.audit", ex);
                try { Go.Tray.TrayTrace.Write("power: " + ex.Message); } catch { /* 连 trace 都写不了：上面已经 Diag 记账，不在这里再抛 */ }
            }
        }

        /// <summary>内置方案给一个稳定的英文标识；自定义方案返回 null。</summary>
        public static string KnownEn(Guid scheme)
        {
            if (scheme == Balanced) return "BALANCED";
            if (scheme == HighPerf) return "HIGH PERFORMANCE";
            if (scheme == Saver) return "POWER SAVER";
            if (scheme == Ultimate) return "ULTIMATE";
            return null;
        }

        /// <summary>界面上用的一行摘要：中文名 + 英文标识。</summary>
        public static string Describe(Guid scheme)
        {
            if (scheme == Guid.Empty) return "未知 · UNKNOWN";
            string cn = FriendlyName(scheme);
            string en = KnownEn(scheme);
            if (string.IsNullOrEmpty(cn)) cn = "自定义方案";
            if (string.IsNullOrEmpty(en)) en = "CUSTOM";
            return cn + " · " + en;
        }

        /// <summary>把当前活动方案同步进 Core（供总览页与其它页面读取）。</summary>
        public static void SyncInto(Core c)
        {
            if (c == null) return;
            Guid g = Active();
            if (g == Guid.Empty) return;
            string cn = FriendlyName(g);
            string en = KnownEn(g);
            if (string.IsNullOrEmpty(cn)) cn = "自定义方案";
            if (string.IsNullOrEmpty(en)) en = "CUSTOM";
            if (c.Profile != cn) c.Profile = cn;
            if (c.ProfileEn != en) c.ProfileEn = en;
        }
    }
}
