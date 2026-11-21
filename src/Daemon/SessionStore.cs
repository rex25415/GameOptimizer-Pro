// ============================================================================
//  SessionStore.cs — 守护会话的崩溃落点
//  为什么要落盘：进程被杀 / 崩溃 / 断电时，内存里的"我正在挂载谁"会一起消失，
//  但账本里的写前记录还在磁盘上。没有这个文件，下次启动就没人知道
//  那些 daemon-gpu-* 条目是"该保留的策略"还是"上次没来得及复原的残局"。
//
//  约定：Attach 时写入，正常 Detach / 退出时删除。
//        启动时若发现它还在——上次一定没走正常路径。
//
//  Rev86：落点里新增 AppBorn（拥有者进程创建时间）。
//  为什么必须有它：只比 AppPid 会把"系统把同一个 PID 发给了另一个进程"当成
//  "上次那个实例还活着"，于是这次启动跳过复原，账本里的写前记录变成孤儿，
//  系统永远停在改过的状态（HANDOFF「判活只比 PID」那条坑的同一个根因）。
// ============================================================================
using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Go.Daemon
{
    public sealed class TrackedProcRecord
    {
        public int Pid { get; set; }
        public long Born { get; set; }
        public uint OrigPriority { get; set; }
        public bool EcoSet { get; set; }
    }

    public sealed class SessionRecord
    {
        public string ProfileId { get; set; }
        public string ProfileName { get; set; }
        public string Exe { get; set; }
        public int GamePid { get; set; }
        public int AppPid { get; set; }
        /// <summary>拥有者进程的创建时间（FILETIME 刻，0 = 旧版本写的/未知）。</summary>
        public long AppBorn { get; set; }
        public string AttachedAt { get; set; }
        /// <summary>挂起或压制过的进程原始快照（崩溃重启时据此逐一还原，防复用误伤）。</summary>
        public TrackedProcRecord[] TrackedProcs { get; set; }
    }

    public static class SessionStore
    {
        public static volatile string DirOverride;

        private static string Path_
        {
            get
            {
                string dir = DirOverride ?? Ledger.DirOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GameOptimizer");
                return Path.Combine(dir, "daemon-session.json");
            }
        }

        public static string FilePath { get { return Path_; } }

        /// <summary>最近一次 Save/Load/Clear 的失败原因（空 = 没失败）。界面据此显示"崩溃保护未生效"。</summary>
        public static string LastError { get; private set; }

        /// <summary>返回 false 表示崩溃落点没写下去——调用方必须把这个事实带进战报/挂载报告。</summary>
        public static bool Save(SessionRecord r)
        {
            if (r == null) return false;
            try
            {
                string f = Path_;
                string dir = Path.GetDirectoryName(f);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                JavaScriptSerializer js = new JavaScriptSerializer();
                // 临时名带 Guid：巡检线程（Attach）与 UI 线程（ApplyExtreme）会并发 Save，
                // 写死 ".tmp" 时两条路径撞同一个临时文件，File.Replace 抛异常 → 崩溃落点丢失。
                string tmp = f + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, js.Serialize(r), new UTF8Encoding(false));
                if (File.Exists(f)) File.Replace(tmp, f, null);
                else File.Move(tmp, f);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                // 写不下去 = 下次崩溃/被杀时没人知道"正在挂载谁"，账本里的写前记录会变成孤儿。
                // 这是用户可见的坏结果（系统停在改过的状态），不能静默。
                LastError = ex.GetType().Name + ": " + ex.Message;
                Diag.Fail("session.save", ex);
                return false;
            }
        }

        public static SessionRecord Load()
        {
            try
            {
                string f = Path_;
                if (!File.Exists(f)) return null;
                string t = File.ReadAllText(f, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(t)) return null;
                JavaScriptSerializer js = new JavaScriptSerializer();
                return js.Deserialize<SessionRecord>(t);
            }
            catch (Exception ex)
            {
                // 读不出来 = 本次启动不会做崩溃复原。计数并留原因，绝不假装"上次是干净的"。
                LastError = ex.GetType().Name + ": " + ex.Message;
                Diag.Fail("session.load", ex);
                return null;
            }
        }

        public static void Clear()
        {
            try { string f = Path_; if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex)
            {
                // 清不掉会让下一次启动误判"上次没正常收尾"并多跑一次复原。
                // 复原是幂等的，所以不致命——但必须计数。
                Diag.Fail("session.clear", ex);
            }
        }

        public static bool Exists { get { return File.Exists(Path_); } }

        public static int SelfPidSafe()
        {
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
                    return p.Id;
            }
            catch (Exception ex)
            {
                Diag.Fail("session.selfpid", ex);
                return 0;
            }
        }

        /// <summary>
        /// 当前进程的创建时间（FILETIME 刻：自 1601-01-01 UTC 起的 100ns 数）。
        /// 用这个刻度而不是 DateTime.Ticks：ProcessScan 的 createTime 来自
        /// SYSTEM_PROCESS_INFORMATION，同一个刻度，两边才能直接比。
        /// </summary>
        public static long SelfBorn()
        {
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    return p.StartTime.ToUniversalTime().ToFileTimeUtc();
                }
            }
            catch (Exception ex)
            {
                // 拿不到自己的创建时间：落点里的 AppBorn=0，下一次启动退回"只比 PID"（旧行为）。
                Diag.Hit("session.born.self");
                Diag.Fail("session.born.self.detail", ex);
                return 0;
            }
        }

        /// <summary>
        /// 进程是否还活着。给了 born 就一起比：PID 是号码不是身份，
        /// 系统会把它回收再发出去，只比号码会把无关的新进程当成"上次那个实例"。
        /// born=0（旧落点/取不到）时退回只比 PID —— 这时只能靠 SessionLease 的互斥体兜底。
        /// </summary>
        public static bool Alive(int pid, long born)
        {
            if (pid <= 0) return false;
            bool denied, reused;
            bool ok = ProcName.IsAlive(pid, born, out denied, out reused);
            if (ok) return true;
            if (reused) Diag.Hit("session.pid.reuse");
            else if (denied) Diag.Hit("session.alive.denied");
            return false;
        }

        public static bool Alive(int pid) { return Alive(pid, 0); }
    }
}
