// ============================================================================
//  SessionLease.cs — 谁拥有"当前守护会话"的唯一判据
//
//  旧实现：DaemonHost 自己读落点里的 AppPid，再用 Process.GetProcessById 判活。
//  这有两个漏洞，都会让两边以为自己是 ghost 或都以为对方活着：
//    · PID 是号码不是身份，系统会回收再发。上一个实例死掉、号码被一个无关进程
//      拿走，新实例就永远不敢复原账本（系统停在改过的状态）；
//    · 两份进程同时启动时，谁都还没写落点，于是两份都判定"没有 owner"，抢着复原。
//
//  Rev86 的选择：**句柄（命名互斥体）**，不是心跳文件。
//    内核对象的生命周期 == 进程生命周期：进程死（含被 TerminateProcess）→ 句柄关闭 →
//    互斥体自动释放，下一个启动 WaitOne(0) 立刻拿到，这是无法伪造的"owner 已死"。
//    心跳文件的替代方案更差：它是"最近一次活着的时间"，需要一个超时阈值，
//    超时短了会把卡住但还活着的实例判死（两边同时复原），长了退出后要等一轮；
//    而且每次心跳都是一次写盘，守护进程常驻却什么也不干时也在碰磁盘。
//
//  失效路径（写清楚，因为这里是"绝不重复"的地基）：
//    1. 跨完整性级别：提权实例建的互斥体，普通权限进程默认打不开（AccessDenied）。
//       本类建对象时给自己加 World/Modify ACL，所以正常情况下能打开、能 WaitOne(0) 判占用。
//    2. 真打不开（组策略/安全软件）：按"别人持有"处理 → 跳过复原，只记 Diag + LastNotice。
//       宁可这一次不复原，也不能把另一个活实例的落点删掉——复原会在下一次启动再做。
//    3. 内核对象建不出来（资源耗尽）：退回"落点里的 (AppPid, AppBorn) 二元组"判定，
//       这时才依赖 PID+创建时间；born=0 的老落点只能比 PID，并在 Diag 里记一笔。
//    4. 上一个 owner 崩溃：WaitOne(0) 抛 AbandonedMutexException = 明确告诉我们
//       "句柄还在、人没了"，正是该接管并复原的信号。
// ============================================================================
using System;
using System.Threading;

namespace Go.Daemon
{
    /// <summary>守护会话的拥有权租约。Owner=true 才允许读写/复原会话落点与账本条目。</summary>
    public sealed class SessionLease : IDisposable
    {
        private Mutex _m;
        private bool _disposed;
        // 命名互斥体的所有权按**线程**记：Acquire 在哪个线程，就必须在那个线程 ReleaseMutex。
        // 关机/退出路径可能从别的线程进来（例如工作线程收尾），跨线程 ReleaseMutex 会抛
        // ApplicationException。记下 owner，跨线程时只关句柄——进程退出时内核会释放所有权。
        private int _ownerThread;

        /// <summary>本进程是否拥有当前守护会话。</summary>
        public bool Owned { get; private set; }

        /// <summary>判定依据（成功/失败原因）。失败时会进 LastNotice 与 Diag，不能静默。</summary>
        public string Why { get; private set; }

        /// <summary>
        /// 名字带 GO_INSTANCE_KEY 后缀：并行自检/多开对比时各自独立，不互相挡。
        /// 注意命名互斥体按**线程**记所有权：同一进程内两条线程各自 Acquire 会双双成功。
        /// 这不影响本类的用途（跨进程归属），但断言/自检必须从另一条线程去抢，
        /// 否则会得到"同一个线程递归持有"的假阳性。
        /// </summary>
        public static string Name()
        {
            string suffix = Environment.GetEnvironmentVariable("GO_INSTANCE_KEY");
            return @"Global\GameOptimizerDaemonSession" + (string.IsNullOrEmpty(suffix) ? "" : ("_" + suffix));
        }

        public static SessionLease Acquire()
        {
            SessionLease lease = new SessionLease();
            string name = Name();
            try
            {
                // 宽松 ACL：允许较低完整性级别的进程打开并判定"被占用"，
                // 否则普通权限的第二实例只能拿到 AccessDenied，分不清"有人在跑"和"权限不够"。
                System.Security.AccessControl.MutexSecurity sec =
                    new System.Security.AccessControl.MutexSecurity();
                sec.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.WorldSid, null),
                    System.Security.AccessControl.MutexRights.Modify
                    | System.Security.AccessControl.MutexRights.Synchronize,
                    System.Security.AccessControl.AccessControlType.Allow));

                bool created;
                Mutex m = new Mutex(false, name, out created, sec);
                if (created)
                {
                    // 我们是创建者：立刻取所有权。取不到只可能是别的线程/进程抢先，
                    // 那就按"别人持有"处理（保守方向）。
                    if (m.WaitOne(0))
                    {
                        lease._m = m;
                        lease._ownerThread = Thread.CurrentThread.ManagedThreadId;
                        lease.Owned = true;
                        lease.Why = "本进程创建并持有会话互斥体";
                        return lease;
                    }
                    m.Dispose();
                    lease.Owned = false;
                    lease.Why = "会话互斥体创建后被他人持有";
                    return lease;
                }

                bool taken;
                try { taken = m.WaitOne(0); }
                catch (AbandonedMutexException)
                {
                    // 上一个 owner 崩溃/被杀：句柄还在但人没了 —— 这就是"该复原"的确切信号。
                    taken = true;
                    Diag.Hit("daemon.lease.abandoned");
                }
                if (taken)
                {
                    lease._m = m;
                    lease._ownerThread = Thread.CurrentThread.ManagedThreadId;
                    lease.Owned = true;
                    lease.Why = "接管已释放的会话互斥体（上一个实例已退出）";
                    return lease;
                }
                m.Dispose();
                lease.Owned = false;
                lease.Why = "另一个实例正持有守护会话";
                Diag.Hit("daemon.lease.held");
                return lease;
            }
            catch (UnauthorizedAccessException ex)
            {
                // 同样要区分两件事（与 InstanceArbiter 的同类处理一致）：
                //   · 对象存在但打不开 → 另一份更高权限实例持有，绝不动它的落点；
                //   · 对象根本不存在 → 标准用户在 Global\ 下没有创建权限。
                //     这时若直接判"别人持有"，崩溃复原对标准用户就永久失效，
                //     账本里的 daemon-gpu-* 会变成孤儿（系统停在改过的状态）。
                bool exists;
                try
                {
                    Mutex probe;
                    exists = Mutex.TryOpenExisting(name, out probe);
                    if (exists && probe != null) probe.Dispose();
                }
                catch (UnauthorizedAccessException) { exists = true; }
                catch (Exception pex) { Diag.Fail("daemon.lease.probe", pex); exists = true; }

                lease.Owned = !exists;
                lease.Why = exists
                    ? "会话互斥体不可打开（可能由更高权限实例持有）"
                    : "无权限在 Global 命名空间建对象，退回 PID+创建时间判定";
                Diag.Hit(exists ? "daemon.lease.denied" : "daemon.lease.noprivilege");
                Diag.Fail("daemon.lease.denied.detail", ex);
                return lease;
            }
            catch (Exception ex)
            {
                // 见失效路径 3：内核对象不可用时退回 (pid,born)，因此这里必须放行（Owned=true）。
                // 放行不是放弃保护——RecoverIfNeeded 仍会比 (AppPid, AppBorn)。
                lease.Owned = true;
                lease.Why = "会话互斥体不可用，退回 PID+创建时间判定";
                Diag.Fail("daemon.lease", ex);
                return lease;
            }
        }

        public void Release()
        {
            if (_disposed) return;
            _disposed = true;
            Mutex m = _m;
            _m = null;
            if (m == null) return;
            try
            {
                // 只有 owner 线程才能 ReleaseMutex；别的线程进来只关句柄（进程马上要退，
                // 内核会在线程结束时释放所有权），否则会抛 ApplicationException 并留下一条假失败。
                if (Owned && _ownerThread == Thread.CurrentThread.ManagedThreadId) m.ReleaseMutex();
                else if (Owned) Diag.Hit("daemon.lease.release.crossthread");
            }
            catch (Exception ex)
            {
                // 不是持有者/已被释放：退出路径上无所谓，进程退出内核会收走；但要能查。
                Diag.Hit("daemon.lease.release");
                Diag.Fail("daemon.lease.release.detail", ex);
            }
            try { m.Dispose(); }
            catch (Exception ex) { Diag.Fail("daemon.lease.dispose", ex); }
            Owned = false;
        }

        public void Dispose() { Release(); }
    }
}
