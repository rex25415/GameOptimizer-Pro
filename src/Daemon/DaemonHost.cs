// ============================================================================
//  DaemonHost.cs — 守护编排：进程感知 → 自动挂载 → 托盘生命周期
//  这里是唯一把 GameWatcher / AutoPilot / TrayHost 接起来的地方，
//  页面只读 Core.Daemon 的状态，不直接持有任何守护对象。
//
//  Rev86 三件事：
//   · 会话拥有权显式化：SessionLease（命名互斥体）说"谁可以动落点与账本条目"，
//     不再靠"落点里那个 PID 看起来还活着"猜（见 SessionLease 文件头）。
//   · 退出顺序收敛：Shutdown() 只剩一条实现 —— ShutdownPipeline，
//     关窗 / Application.Exit / 托盘菜单 / 系统注销 / --diag 到期全部汇到这里。
//   · 托盘菜单弹出失败会重试一次，并把失败写进 LastNotice，而不是静默没有菜单。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Go.Tray;

namespace Go.Daemon
{
    public sealed class DaemonHost
    {
        private readonly Core _core;
        private readonly Dispatcher _ui;
        private TrayMenu _menu;

        public readonly GameWatcher Watcher;
        public readonly AutoPilot Pilot;
        public readonly SessionRecorder Recorder;
        public readonly PowerMonitor Power = new PowerMonitor();
        public TrayHost Tray;

        public bool TrayEnabled = true;
        /// <summary>关窗即隐入托盘（而不是退出）。</summary>
        public bool CloseToTray = true;
        /// <summary>启动后直接进托盘（--tray）。</summary>
        public bool StartHidden;
        public bool Autostart;

        public string LastNotice { get; private set; }

        /// <summary>关停已开始：此后任何后台回调都不再排进调度器（窗口可能已销毁）。</summary>
        private volatile bool _shuttingDown;

        /// <summary>被动模式：只广播状态，不真正写系统（离屏渲染演示用）。</summary>
        public bool Passive;

        // ---- 会话拥有权 ----------------------------------------------------
        private SessionLease _lease;
        private readonly object _shutdownGate = new object();
        private ShutdownReport _lastShutdown;

        /// <summary>本进程是否拥有当前守护会话（自检/界面可读；false = 只读地旁观）。</summary>
        public bool OwnsSession { get { return _lease != null && _lease.Owned; } }

        /// <summary>最近一次退出的完整账（每步耗时/超时/失败）。没退出过 = null。</summary>
        public ShutdownReport LastShutdown { get { return _lastShutdown; } }

        public DaemonHost(Core core)
        {
            _core = core;
            _ui = Dispatcher.CurrentDispatcher;
            Watcher = new GameWatcher(core);
            Pilot = new AutoPilot(core);
            Recorder = new SessionRecorder(core);
            Tray = new TrayHost();

            Watcher.Started += OnGameStarted;
            Watcher.Stopped += OnGameStopped;
            Watcher.Pulse += delegate
            {
                Recorder.Tick();
                if (!Passive) Pilot.OnPulse(Watcher.Active, Watcher.ActivePid, Recorder);
            };
            Power.Changed += OnPowerChanged;
            Power.PowerSaverChanged += OnPowerSaverChanged;
            // 挂载时按当前供电与节能模式决定要不要提升精度：电池或节能模式下不再强求 0.5ms。
            Pilot.PowerOk = delegate { return Power.OnAc && !Power.PowerSaverActive; };
        }

        // ---- 崩溃自愈 ------------------------------------------------------
        /// <summary>上次是否异常收场；本次启动复原了几条系统写入。</summary>
        public bool RecoveredLastSession { get; private set; }
        public int RecoveredEntries { get; private set; }

        /// <summary>
        /// 启动时检查崩溃落点。存在 = 上一个进程没走正常收尾。
        /// 处理方式：按账本把 daemon-gpu-* 的写前记录还原，然后删掉落点。
        /// 如果游戏还开着，监视器会在 250ms 内重新命中并再次挂载——先还原、再挂上，
        /// 比"保留不明状态"安全得多。
        ///
        /// 前置条件：本进程持有 SessionLease。没有拥有权就什么都不做——
        /// 复原会删掉另一个活实例的落点，那正是"两边都以为自己是 ghost"的来源。
        /// </summary>
        public void RecoverIfNeeded()
        {
            if (_lease == null) _lease = SessionLease.Acquire();
            if (!_lease.Owned)
            {
                // 另一个实例在跑：它的落点由它自己收尾。这里只留证，绝不 Clear。
                LastNotice = "守护会话由另一个实例持有 · 跳过崩溃复原（" + _lease.Why + "）";
                Diag.Hit("daemon.recover.skip");
                return;
            }

            if (!SessionStore.Exists) return;
            SessionRecord rec = SessionStore.Load();
            RecoveredLastSession = true;

            // 另一个实例还活着并持有会话（多开 / 更新中）：不要动它的账本。
            // 判活必须带创建时间：只比 PID 会把"号码被回收后发给的新进程"当成原主，
            // 于是复原被永久跳过，系统停在改过的状态。
            if (rec != null && rec.AppPid > 0 && rec.AppPid != SafePid() && SessionStore.Alive(rec.AppPid, rec.AppBorn))
            {
                // 落点属于另一个还活着的实例：它是这份记录的拥有者，会在自己正常退出时清掉。
                // 旧实现在这里 Clear()，等于把对方崩溃后唯一的复原依据删掉。
                return;
            }

            string name = rec == null ? "未知" : rec.ProfileName;
            int recoveredProcs = 0;
            bool procsOk = true;
            try { recoveredProcs = Pilot.RecoverTrackedProcs(rec); }
            catch (Exception ex) { Diag.Fail("daemon.recover.procs", ex); procsOk = false; }

            bool gpuOk = true;
            if (!string.IsNullOrEmpty(rec == null ? null : rec.ProfileId))
            {
                try
                {
                    ApplyReport r = _core.Engine.RevertAction("daemon-gpu-" + rec.ProfileId);
                    RecoveredEntries = r.Applied + recoveredProcs;
                    if (!r.Ok)
                    {
                        gpuOk = false;
                        RecoveredEntries = -1;
                    }
                }
                catch (Exception ex)
                {
                    // 崩溃自愈失败 = 上次的独显偏好仍留在系统里，而账本这次也没能复原。
                    // 审计行会写 -1（可见），原因进 Diag。
                    Diag.Fail("daemon.recover", ex);
                    gpuOk = false;
                    RecoveredEntries = -1;
                }
            }
            else if (recoveredProcs > 0)
            {
                RecoveredEntries = recoveredProcs;
            }

            _core.Engine.Ledger.AppendAudit("DAEMON recover 上次会话未正常收尾 (" + name + ") 复原 " + RecoveredEntries + " 项");
            if (procsOk && gpuOk)
            {
                SessionStore.Clear();
            }
            else
            {
                Diag.Hit("daemon.recover.landing.kept");
                LastNotice = "上次会话复原未完成（" + name + "），已保留崩溃落点";
            }
        }

        /// <summary>最近一局的战报（档案页读取）。</summary>
        public SessionReport LastSession { get; private set; }

        /// <summary>
        /// 供电切换：电池下自动撤掉 0.5ms 精度租约（它会让 CPU 永不休眠、风扇常转），
        /// 插回电源后再恢复。用户不需要知道这件事发生过。
        /// </summary>
        private void OnPowerChanged(bool onAc)
        {
            // 供电回调来自 go.power 后台线程：Toast / RefreshDaemonUi 都碰 WPF 视觉树，
            // 必须经 Post 搬回 UI 线程。旧实现直接调用，电池切换那一刻必抛跨线程异常。
            Post("daemon.power", delegate { SyncPowerPolicy(onAc, Power.PowerSaverActive); });
        }

        private void OnPowerSaverChanged(bool saverActive)
        {
            Post("daemon.power.saver", delegate { SyncPowerPolicy(Power.OnAc, saverActive); });
        }

        private void SyncPowerPolicy(bool onAc, bool saverActive)
        {
            GameProfile a = Watcher.Active;
            bool wantsTimer = a != null && a.Timer;
            bool allowExtreme = onAc && !saverActive;
            if (!allowExtreme)
            {
                if (_core.Timer.Active) _core.Timer.Release(TimerOwner.Daemon);
                LastNotice = !onAc
                    ? "已切到电池供电 · 自动撤下精度租约"
                    : "系统处于节能模式 · 自动撤下精度租约";
                if (_core.Shell != null && Ui.Animate && wantsTimer)
                {
                    string msg = !onAc
                        ? "电池供电 · 已释放 0.5ms 精度以省电，插回电源自动恢复"
                        : "系统已启用节能模式 · 已释放 0.5ms 精度，关闭节能后自动恢复";
                    _core.Shell.Toast(msg, true);
                }
            }
            else
            {
                if (wantsTimer) _core.Timer.Request(TimerOwner.Daemon, 0.5);
                LastNotice = "已恢复交流高性能供电 · 恢复战术策略";
            }
            _core.State.Raise(Dirty.Power);
            if (_core.Shell != null) _core.Shell.RefreshDaemonUi();
        }

        private static int SafePid()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch (Exception ex)
            {
                // 取不到自身 pid 会让"另一实例还活着"的判定跳过，复原仍然执行——
                // 这是安全方向（多复原一次是幂等的）。
                Diag.Hit("daemon.pid");
                Diag.Fail("daemon.pid.detail", ex);
                return 0;
            }
        }

        // ---- 生命周期 ------------------------------------------------------
        public void Start()
        {
            // App 用 Background 优先级延迟投递 Start()：窗口若在此之前关闭，
            // Shutdown 已经跑完，Start 再执行会重新装托盘图标、起巡检、重取租约，
            // 而没有任何人会再拆它们（图标残影 + 退出后仍在扫描）。
            if (_shuttingDown)
            {
                Diag.Hit("daemon.start.after.shutdown");
                return;
            }
            RecoverIfNeeded();
            if (TrayEnabled)
            {
                // 左键：把窗口带到眼前（不做显隐切换——点托盘却把窗口藏起来最反直觉）；
                // 右键：菜单开合切换，绝不重复重建（重建会让菜单闪一下换个位置）。
                Tray.LeftUp += delegate { Post("daemon.tray.left", ToggleWindow); };
                Tray.DoubleClick += delegate { Post("daemon.tray.double", RestoreWindow); };
                Tray.RightUp += delegate { Post("daemon.tray.right", ShowMenu); };
                if (!Tray.Install("GameOptimizer · 守护待命"))
                {
                    // 托盘注册失败（Explorer 正在重启 / 被安全软件拦）：图标不在，用户点不到任何东西。
                    // TrayHost 自己有 8 次重试；这里让失败在本次会话里可见，而不是"安静地没有托盘"。
                    Diag.Hit("daemon.tray.install");
                    LastNotice = "托盘图标注册失败：" + (Tray.LastError == null ? "未知原因" : Tray.LastError) + "（稍后自动重试）";
                    Notify("托盘图标未注册", LastNotice);
                }
            }
            Watcher.Start();
            Watcher.Enabled = true;
            Power.Start();
            UpdateTip();
        }

        /// <summary>
        /// 唯一退出实现。四类入口（关窗→Application.Exit、托盘菜单、系统注销、--diag 到期）
        /// 全部走这里；重复调用返回同一份报告，不会重复拆服务。
        /// 步骤按注册顺序执行（见 ShutdownPipeline）：托盘优先、再断输入源、最后复原并放锁。
        /// </summary>
        public void Shutdown() { Shutdown("explicit"); }

        public ShutdownReport Shutdown(string trigger)
        {
            lock (_shutdownGate)
            {
                if (_lastShutdown != null) return _lastShutdown;

                // 先立起闸门：此后 Watcher/Power 的 Stopped 回调不会再往调度器里排东西。
                _shuttingDown = true;

                // 停机前捕获当前目标：Watcher.Stop 内部会 Detach 清空 Active，
                // 而它的 Stopped 回调被闸门挡下，独显偏好的复原必须显式做一次。
                GameProfile active = Watcher.Active;
                int activePid = Watcher.ActivePid;

                ShutdownPipeline p = new ShutdownPipeline();
                // ① 托盘优先：NIM_DELETE 只等一次 Shell_NotifyIcon 调用，是用户唯一
                //    每天都能看见的退出结果（图标残影）。它必须排在"可能被注册表回滚
                //    或战报收尾拖住"的步骤之前。UI 步就地执行（HwndSource 只能自己线程销毁）。
                p.Add("tray.menu", 600, true, delegate { CloseMenu(); });
                p.Add("tray.icon", 900, true, delegate { if (Tray != null) Tray.Remove(); });
                // ② 先断输入源，再按依赖逆序拆消费者。
                p.Add("power.stop", 800, false, delegate { Power.Dispose(); });
                p.Add("watcher.stop", 3000, false, delegate { Watcher.Stop(); });
                // ③ 巡检线程已 Join 才能动 Recorder：Join 超时说明 Step 可能仍卡在 Pulse→Tick，
                //    此时 End() 会并发遍历 _accum，Dispose() 会把 Scan 的非托管缓冲释放掉而
                //    对方还在读（use-after-free）。宁可丢这一局的战报，也不能碰正在被使用的缓冲。
                p.Add("recorder.end", 2000, false,
                      delegate { return Watcher.StopJoined && Recorder.Active; },
                      delegate { Recorder.End(); });
                p.Add("pilot.stop", 800, false, delegate { Pilot.Stop(); });
                p.Add("recorder.dispose", 800, false,
                      delegate { return Watcher.StopJoined; },
                      delegate { Recorder.Dispose(); });
                // ④ 注册表复原最可能被安全软件拖住：给它最大的预算，但仍然有上限。
                //    这一步与后面两步是 critical：总预算到点也不能跳过，否则系统会停在改过的状态。
                p.Add("pilot.revert", 6000, false,
                      delegate { return Pilot.RevertOnExit && active != null; },
                      delegate { Pilot.Detach(active, activePid); }, true);
                p.Add("session.clear", 800, false, delegate { SessionStore.Clear(); }, true);
                // 租约必须在**取锁的同一条线程**上释放（命名互斥体按线程记所有权）。
                // 所以这一步是就地执行（uiThread=true），不能丢给工作线程。
                p.Add("session.lease", 400, true, delegate { ReleaseLease(); }, true);

                _lastShutdown = p.RunOnce(trigger);

                // 兜底：tray.icon 步若自己抛了（外壳重建中/NIM_DELETE 被拒），这里再摘一次。
                // 托盘残影是可见 bug，宁可多调一次幂等的 Remove()。
                if (Tray != null && Tray.Installed)
                {
                    try { Tray.Remove(); }
                    catch (Exception ex) { Diag.Fail("daemon.shutdown.tray.retry", ex); }
                }
                if (_lastShutdown.Timeouts > 0) Diag.Hit("daemon.shutdown.timeout");
                return _lastShutdown;
            }
        }

        private void ReleaseLease()
        {
            SessionLease l = _lease;
            _lease = null;
            if (l != null) l.Release();
        }

        /// <summary>
        /// 只摘托盘图标、不碰 WPF 对象：给 AppDomain.ProcessExit（Environment.Exit 掐断进程）用。
        /// Shell_NotifyIcon(NIM_DELETE) 是纯 Win32 调用，可以在任意线程执行；
        /// 而 HwndSource/Window 的销毁只能在创建它们的线程上做，所以完整管线不能走这条路。
        /// </summary>
        public void RemoveTrayIconNow()
        {
            TrayHost t = Tray;
            if (t == null) return;
            t.RemoveIconNow();
        }

        // ---- 事件（后台线程 → UI 线程）------------------------------------
        private void OnGameStarted(GameProfile p, int pid)
        {
            // 关停已开始：在途那一拍不许再挂载（否则会在 SessionStore.Clear 之后重建落点，
            // 下次启动误报崩溃自愈）。复原由 Shutdown 显式负责。
            if (_shuttingDown) return;
            if (!Passive) Pilot.Attach(p, pid);
            if (!Passive) Recorder.Begin(p);
            Post("daemon.ui.started", delegate
            {
                LastNotice = p.Name + " 已启动 · " + Pilot.LastReport;
                UpdateTip();
                // 写死 "CCD0 / 独显 / 0.5ms" 会和实际结果打架（可能"未配置策略"或"被进程保护拒绝"）。
                Notify("已挂载 " + p.Name, Pilot.LastReport);
                if (_core.Shell != null) _core.Shell.RefreshDaemonUi();
            });
        }

        private void OnGameStopped(GameProfile p, int pid)
        {
            // 关停路径的战报收尾与独显复原由 Shutdown 显式做（巡检已 Join，无并发）；
            // 这里只服务正常游戏退出，闸门立起后直接返回。
            if (_shuttingDown) return;
            SessionReport rep = null;
            if (!Passive)
            {
                try { rep = Recorder.End(); }
                catch (Exception ex)
                {
                    // 战报生成失败：托盘/界面会显示"已退出"但没有战报。计数，别让它变沉默。
                    Diag.Fail("daemon.recorder.end", ex);
                }
            }
            LastSession = rep;
            Pilot.Detach(p, pid);
            // Detach 内部会把真实结果写进 LastReport（"已复原" / "复原失败：… 需要到还原中心手动处理"）。
            // 通知文案必须用它，不能写死"已复原"——那是在替一次没成功的回滚撒谎。
            string revertNote = Pilot.LastReport;
            Post("daemon.ui.stopped", delegate
            {
                LastNotice = (p == null ? "目标" : p.Name) + " 已退出 · " + (string.IsNullOrEmpty(revertNote) ? "未改系统参数" : revertNote);
                if (rep != null && _core.Shell != null && Ui.Animate)
                {
                    string top = rep.Top.Count > 0 ? (rep.Top[0].Name + " 占单核 " + rep.Top[0].CpuPercent.ToString("0.0") + "%") : "无后台抢占";
                    _core.Shell.Toast("战报 · " + rep.DurationText + " · " + rep.Verdict + " · " + top, rep.StutterWindows == 0);
                }
                UpdateTip();
                if (_core.Shell != null) _core.Shell.RefreshDaemonUi();
            });
        }

        public void Notify(string title, string body)
        {
            if (_core.Shell == null || !Ui.Animate) return;
            try { _core.Shell.Toast(title + " · " + body, true); }
            catch (Exception ex)
            {
                // Toast 失败只是少一条提示；但"挂载了却没提示"会让用户以为没生效。
                Diag.Fail("daemon.notify", ex);
            }
        }

        private void UpdateTip()
        {
            if (Tray == null || !Tray.Installed) return;
            GameProfile a = Watcher.Active;
            string tip = a == null
                ? "GameOptimizer · 守护中"
                : "GameOptimizer · 运行中：" + a.Name;
            Tray.UpdateTip(tip);
        }

        // ---- 后台 → UI 线程的唯一入口 ---------------------------------------
        /// <summary>
        /// 所有后台线程/托盘回调都从这里进 UI 线程：
        ///   · 关停后直接丢弃——不再往已销毁的窗口/控件里写；
        ///   · 回调整体包一层 try/catch，失败 Diag.Fail。
        /// 旧代码 5 处裸 BeginInvoke 一旦回调抛异常，异常只会进调度器，
        /// 用户看到"游戏起来了界面没反应"，而自检全绿。
        /// </summary>
        private void Post(string tag, Action a)
        {
            if (a == null || _shuttingDown) return;
            Dispatcher ui = _ui;
            if (ui == null || ui.HasShutdownStarted || ui.HasShutdownFinished) return;
            try { ui.BeginInvoke(new Action(delegate { Guard(tag, a); })); }
            catch (Exception ex) { Diag.Fail(tag, ex); }
        }

        private static void Guard(string tag, Action a)
        {
            try { a(); }
            catch (Exception ex) { Diag.Fail(tag, ex); }
        }

        // ---- 托盘菜单 ------------------------------------------------------
        /// <summary>菜单是否开着（自检 / 状态用）。</summary>
        public bool MenuOpen { get { return _menu != null; } }
        public TrayMenu Menu { get { return _menu; } }

        private long _menuClosedMs = -100000;
        /// <summary>菜单弹出重试预算：一次就够。失败两次说明是环境问题（WPF/焦点），再试只是闪屏。</summary>
        private const int MenuRetries = 1;

        /// <summary>
        /// 菜单开合切换。
        ///
        /// 关键细节：鼠标在图标上按下时，菜单会因为失去前台而先自己关掉，
        /// 随后外壳才把 WM_CONTEXTMENU 送到——如果这时老老实实"再开一个"，
        /// 用户看到的就是"右键关不掉，只是闪了一下"。所以：
        /// 上一次关闭发生在 450ms 内的右键请求，视作同一次点击的尾巴，直接吞掉。
        ///
        /// 弹出失败（焦点被抢、显示器刚变化、WPF 还没到 Render 优先级）不再静默：
        /// 记 Diag + 140ms 后重试一次；仍然失败才把原因写进 LastNotice。
        /// </summary>
        public void ShowMenu() { ShowMenu(0); }

        private void ShowMenu(int attempt)
        {
            try
            {
                if (_menu != null) { CloseMenu(); return; }
                if (ClickClock.NowMs - _menuClosedMs < 450)
                {
                    TrayTrace.Write("menu toggle: reopen suppressed");
                    _menuClosedMs = ClickClock.NowMs;
                    return;
                }
                ShowMenuCore();
            }
            catch (Exception ex)
            {
                TrayHost.LogTrayError(ex);
                // 半成品不能留在 _menu 里：MenuOpen 会永远为真，
                // 下一次右键变成"关掉一个已经不在的菜单"，用户再也打不开它。
                TrayMenu broken = _menu;
                _menu = null;
                if (broken != null) { try { broken.Close(); } catch (Exception cx) { Diag.Fail("daemon.menu.close.broken", cx); } }
                if (attempt < MenuRetries)
                {
                    Diag.Hit("daemon.menu.retry");
                    ScheduleMenuRetry(attempt + 1);
                    return;
                }
                LastNotice = "托盘菜单打不开：" + ex.Message;
                Diag.Fail("daemon.menu", ex);
            }
        }

        private void ScheduleMenuRetry(int attempt)
        {
            Dispatcher ui = _ui;
            if (ui == null || ui.HasShutdownStarted || ui.HasShutdownFinished) return;
            try
            {
                DispatcherTimer t = null;
                t = new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background,
                    delegate
                    {
                        // 只停自己：期间若又排了新的重试，不能把新的从纳管里踢出去。
                        if (t != null) t.Stop();
                        if (_shuttingDown) return;
                        ShowMenu(attempt);
                    }, ui);
                t.Start();
            }
            catch (Exception ex)
            {
                // 没有调度器（自检里的裸线程）时无法重试：这不是静默失败，Dig + LastNotice 已给。
                Diag.Fail("daemon.menu.retry.schedule", ex);
                LastNotice = "托盘菜单打不开（无法安排重试）：" + ex.Message;
            }
        }

        private void CloseMenu()
        {
            TrayMenu m = _menu;
            _menu = null;
            if (m != null) { try { m.Close(); } catch (Exception ex) { Diag.Fail("daemon.menu.close", ex); } }
            _menuClosedMs = ClickClock.NowMs;
        }

        private void ShowMenuCore()
        {
            if (_menu != null) { try { _menu.Close(); } catch (Exception ex) { Diag.Fail("daemon.menu.reopen", ex); } _menu = null; }

            TrayMenu m = new TrayMenu();
            _menu = m;
            m.Closed += delegate
            {
                if (ReferenceEquals(_menu, m)) { _menu = null; _menuClosedMs = ClickClock.NowMs; }
                TrayTrace.Write("menu closed");
            };

            GameProfile a = Watcher.Active;
            m.SetStatus(a == null ? "守护待命 · 未检测到目标游戏" : "运行中 · " + a.Name, a != null);

            m.AddHeader("控制台");
            bool windowShown = _core.Shell != null && _core.Shell.IsVisible && !_core.Shell.Parked;
            if (windowShown)
                m.AddItem("隐藏到托盘", "后台继续守护 · 点托盘图标可再唤出", delegate { if (_core.Shell != null) _core.Shell.Park(); }, false);
            else
                m.AddItem("打开主界面", "Ctrl+Shift+G", delegate { RestoreWindow(); }, false);
            m.AddItem("一键极致模式", a == null ? "先启动一个已建档的游戏" : (a.Name + " · CCD0 + 独显 + 0.5ms"),
                delegate
                {
                    if (a == null) { Notify("没有正在运行的目标游戏", "先在「战术档案」里为游戏建立档案"); return; }
                    Pilot.ApplyExtreme(a);
                    if (_core.Shell != null) _core.Shell.RefreshDaemonUi();
                }, false);

            m.AddRule();
            m.AddHeader("系统");
            m.AddItem("恢复默认", "按账本逆序复原全部写入",
                delegate
                {
                    ApplyReport r = _core.Engine.RevertAll();
                    Notify("恢复默认", r.Ok ? ("已复原 " + r.Applied + " 项") : (r.Failed + " 项失败"));
                    if (_core.Shell != null) _core.Shell.RefreshDaemonUi();
                }, true);

            m.AddCheck("游戏自动守护", "启动即挂载 · 退出按账本复原", Watcher.Enabled, delegate (bool on)
            {
                Watcher.Enabled = on;
                UpdateTip();
            });
            m.AddCheck("托盘常驻", "关闭窗口后继续在后台守护", CloseToTray, delegate (bool on) { CloseToTray = on; });
            m.AddCheck("开机自启", "登录后在托盘静默待命", Autostart,
                delegate (bool on) { if (!SetAutostart(on)) Notify("开机自启未生效", LastNotice); });

            GameProfile[] items = _core.Profiles.Snapshot();
            if (items.Length > 0)
            {
                m.AddRule();
                m.AddHeader("档案切换");
                int shown = Math.Min(4, items.Length);
                for (int i = 0; i < shown; i++)
                {
                    GameProfile p = items[i];
                    string hint = FormatProfilePolicyHint(p);
                    m.AddItem(p.Name, hint.Length == 0 ? "未配置策略" : hint,
                        delegate { Watcher.ForceAttach(p, 0); }, false);
                }
            }

            m.AddRule();
            m.AddItem("退出 GameOptimizer", "同时撤销本进程的全部系统写入", delegate { ExitApp(); }, true);

            m.ShowAtAnchor(Tray.AnchorX, Tray.AnchorY);
            TrayTrace.Write("menu shown anchor=" + Tray.AnchorX + "," + Tray.AnchorY + " rows=" + m.RowCount);
        }

        internal static string FormatProfilePolicyHint(GameProfile p)
        {
            if (p == null) return "";
            List<string> parts = new List<string>(5);
            string ccd = p.CcdMode;
            if (string.Equals(ccd, "Ccd0", StringComparison.OrdinalIgnoreCase)) parts.Add("CCD0");
            else if (string.Equals(ccd, "Ccd1", StringComparison.OrdinalIgnoreCase)) parts.Add("CCD1");
            else if (string.Equals(ccd, "Physical", StringComparison.OrdinalIgnoreCase)) parts.Add("仅物理核");
            else if (string.Equals(ccd, "All", StringComparison.OrdinalIgnoreCase)) parts.Add("全核");

            if (p.HighGpu) parts.Add("独显");

            string prio = p.PriorityMode;
            if (string.Equals(prio, "High", StringComparison.OrdinalIgnoreCase)) parts.Add("高优先级");
            else if (string.Equals(prio, "AboveNormal", StringComparison.OrdinalIgnoreCase)) parts.Add("高于正常");
            else if (string.Equals(prio, "Normal", StringComparison.OrdinalIgnoreCase)) parts.Add("正常优先级");
            else if (string.Equals(prio, "BelowNormal", StringComparison.OrdinalIgnoreCase)) parts.Add("低于正常");
            else if (string.Equals(prio, "Idle", StringComparison.OrdinalIgnoreCase)) parts.Add("空闲优先级");

            if (p.EcoBackground) parts.Add("后台EcoQoS");
            if (p.Timer) parts.Add("0.5ms");
            return string.Join(" ", parts.ToArray());
        }

        // ---- 窗口 / 托盘过渡 ------------------------------------------------
        public void RestoreWindow()
        {
            if (_core.Shell == null) return;
            if (MenuOpen) CloseMenu();
            _core.Shell.Unpark();
        }

        /// <summary>
        /// 左键托盘：把窗口带到眼前。**不做显隐切换**——
        /// 托盘图标点一下就"藏起来"是最反直觉的行为之一（用户点它就是为了看窗口）。
        /// 收起窗口有明确的入口：标题栏 ×、最小化、以及菜单里的「隐藏到托盘」。
        /// </summary>
        public void ToggleWindow()
        {
            // 与 RestoreWindow 同义（左键/双击都把窗口带到眼前），复用同一实现。
            RestoreWindow();
        }

        public void ExitApp()
        {
            Shutdown("tray.exit");
            Application app = Application.Current;
            if (app != null) app.Shutdown();
            else Environment.Exit(0);
        }

        // ---- 开机自启（HKCU Run，可逆）-------------------------------------
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "GameOptimizer";

        public void LoadAutostart()
        {
            try
            {
                RegValue v = Reg.Read("HKCU", RunKey, RunName);
                Autostart = v.Existed;
                if (!v.Existed && v.Error != null)
                {
                    // 读注册表失败 ≠ 没配置自启：勾选框会显示"未启用"，而真实状态未知。
                    LastNotice = "开机自启状态读取失败：" + v.Error;
                    Diag.Fail("daemon.autostart.read", new InvalidOperationException(v.Error));
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("daemon.autostart.read", ex);
                LastNotice = "开机自启状态读取失败：" + ex.Message;
            }
        }

        /// <summary>写 HKCU Run。返回 false 时 LastNotice 带原因，菜单勾选框不应被当成"已设置"。</summary>
        public bool SetAutostart(bool on)
        {
            try
            {
                if (on)
                {
                    string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                    Reg.Write("HKCU", RunKey, RunName, "\"" + exe + "\" --tray", "String");
                    // 写完回读：权限/安全软件可能让 SetValue 静默无效。
                    RegValue back = Reg.Read("HKCU", RunKey, RunName);
                    if (!back.Existed)
                    {
                        LastNotice = "开机自启写入未生效（注册表回读不存在）";
                        Diag.Fail("daemon.autostart.write", new InvalidOperationException(LastNotice));
                        return false;
                    }
                }
                else
                {
                    Reg.Delete("HKCU", RunKey, RunName);
                    if (Reg.Read("HKCU", RunKey, RunName).Existed)
                    {
                        LastNotice = "开机自启删除未生效（注册表回读仍存在）";
                        Diag.Fail("daemon.autostart.write", new InvalidOperationException(LastNotice));
                        return false;
                    }
                }
                Autostart = on;
                LastNotice = on ? "已开启开机自启" : "已关闭开机自启";
                return true;
            }
            catch (Exception ex)
            {
                // 这是"勾了却没生效"的典型：必须让用户看到，而不是勾选框自己跳一下。
                Diag.Fail("daemon.autostart.write", ex);
                LastNotice = "开机自启设置失败：" + ex.Message;
                return false;
            }
        }
    }
}
