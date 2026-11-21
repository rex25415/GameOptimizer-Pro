// ============================================================================
//  Scheduler.Affinity.cs — 03 调度与定时器：前台进程掩码的读取、同步与写入（分片）
//  为什么切：这一块是全页唯一写别人进程的地方（OpenProcess / 亲和性掩码 / 优先级类）。
//  版面在 Scheduler.Matrix.cs，写入路径与它的前置条件集中在这里，审的时候只看这一个文件。
//
//  Rev86：掩码的"读出结果"现在有三个落点，全部由 UpdateMaskBlock 一个方法写：
//  读数带第二格（十六进制）、矩阵上方那句等宽读数、矩阵本身的三态。
//  三处同一份事实——不再有"band 说 32、矩阵画 0xFFFFFFFF"这种自相矛盾的画面。
// ============================================================================
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Scheduler : Page
    {
        // ---- 目标进程 --------------------------------------------------------
        /// <summary>
        /// 读目标进程的亲和性掩码。
        /// 只在「前台外部进程变了」或动作之后真正 OpenProcess —— 旧实现每 500ms
        /// 打开一次句柄并读掩码，稳态每秒 2 次；现在稳态是 0 次。
        /// </summary>
        private void SyncTarget(bool force)
        {
            Core c = C;
            if (c == null || _matrix == null) return;

            int foreign = c.LastForeignPid;
            if (!force && foreign == _seenForeign) return;
            _seenForeign = foreign;

            _targetSelf = (foreign == 0);
            int pid = _targetSelf ? SelfPid : foreign;
            _targetPid = pid;
            _targetName = ProcName.OrPid(pid);

            long mask = Affinity.Read(pid);
            _targetMask = mask;
            _targetKnown = mask != 0;
            _targetAt = DateTime.Now;
            _targetPrio = ProcessPower.ReadPriority(pid);
            bool eco;
            _targetEcoKnown = ProcessPower.ReadEcoQoS(pid, out eco);
            _targetEco = _targetEcoKnown && eco;

            _matrix.SetMask(mask, _targetKnown);
            UpdateMaskBlock(c);
            UpdateProcSchedBlock(c);
        }

        /// <summary>掩码的唯一写入点：读数带两格 + 矩阵读数行 + 矩阵的三态引用。</summary>
        private void UpdateMaskBlock(Core c)
        {
            long all = Affinity.AllMask();
            long ccd0 = Affinity.CcdMask(c.Topo, 0);
            long ccd1 = c.Topo.Ccds > 1 ? Affinity.CcdMask(c.Topo, 1) : 0;
            long phys = Affinity.PhysicalOnlyMask(c.Topo);
            int total = Math.Max(1, c.Topo.Logical);

            int allow = _targetKnown ? Bits(_targetMask) : 0;
            string hex = _targetKnown ? ("0x" + _targetMask.ToString("X")) : "—";

            // ---- 读数带：第二格（掩码）+ 第三格（活动核数）--------------------
            Set(_bandMask, hex);
            if (_maskStrip != null) _maskStrip.Set(_targetMask, _targetKnown, total);
            Set(_bandCores, _targetKnown ? allow.ToString() : "—");
            if (_coresBar != null) _coresBar.Value = _targetKnown ? (double)allow / total : 0.0;
            Set(_bandCoresNote, !_targetKnown
                ? "读不到 · 前台进程已退出 / 无权限"
                : (allow >= total ? "全部可用 · 未限制亲和性" : ("已排除 " + (total - allow) + " 个逻辑处理器")));
            Set(_bandMaskNote, _targetKnown
                ? (_targetName + " · PID " + _targetPid)
                : "读不到 · 已退出 / 被挂起 / 无权限");

            // ---- 矩阵读数行：当前掩码 = 0x… · 谁 · 何时读的 -------------------
            string tail;
            if (_targetKnown)
            {
                string who = _targetSelf ? (_targetName + "（本工具自身 · 未识别前台外部窗口）") : _targetName;
                tail = " · " + who + " · PID " + _targetPid + " · 读于 " + _targetAt.ToString("HH:mm:ss");
            }
            else tail = " · " + _targetName + " · PID " + _targetPid + " · 亲和性读不到（已退出 / 被挂起 / 无权限）";
            SetMaskLine(hex, tail);

            Set(_allowText, _targetKnown ? ("选中 " + allow) : "选中 —");
            Set(_denyText, _targetKnown ? ("未选中 " + Math.Max(0, total - allow)) : "未选中 —");

            bool writable = !_targetSelf && _targetPid > 4 && _targetPid != SelfPid;
            bool isPhys = _targetKnown && c.Topo.ThreadsPerCore > 1 && phys != 0 && _targetMask == phys;
            bool isCcd0 = _targetKnown && _targetMask == ccd0 && ccd0 != all;
            bool isCcd1 = _targetKnown && c.Topo.Ccds > 1 && ccd1 != 0 && _targetMask == ccd1 && ccd1 != all;
            bool isAll = _targetKnown && _targetMask == all;

            if (_btnPhys != null)
            {
                _btnPhys.Enabled = writable && phys != 0;
                _btnPhys.Kind = (writable && isPhys) ? BtnKind.Accent : BtnKind.Ghost;
            }
            if (_btnCcd0 != null)
            {
                _btnCcd0.Enabled = writable && ccd0 != 0;
                _btnCcd0.Kind = (writable && isCcd0) ? BtnKind.Accent : BtnKind.Ghost;
            }
            if (_btnCcd1 != null)
            {
                _btnCcd1.Enabled = writable && ccd1 != 0;
                _btnCcd1.Kind = (writable && isCcd1) ? BtnKind.Accent : BtnKind.Ghost;
            }
            if (_btnFree != null)
            {
                _btnFree.Enabled = writable;
                _btnFree.Kind = (writable && !isAll && _targetKnown) ? BtnKind.Accent : BtnKind.Ghost;
            }

            if (_maskBadge != null)
            {
                if (!_targetKnown) _maskBadge.Set("未测量", T.Mute);
                else if (isCcd0) _maskBadge.Set("已锁定 CCD0", T.Accent);
                else if (isCcd1) _maskBadge.Set("已锁定 CCD1", T.Accent);
                else if (isPhys) _maskBadge.Set("仅物理核", T.Accent);
                else if (isAll) _maskBadge.Set("全部逻辑处理器", T.Dim);
                else _maskBadge.Set("部分掩码", T.Text);
            }
        }

        private void UpdateProcSchedBlock(Core c)
        {
            bool writable = !_targetSelf && _targetPid > 4 && _targetPid != SelfPid;
            if (_procSchedTarget != null)
            {
                string t = writable
                    ? (_targetName + " · PID " + _targetPid)
                    : (_targetName + "（自身 · 只读）");
                Set(_procSchedTarget, t);
                _procSchedTarget.Foreground = T.S(writable ? T.Dim : T.Mute);
            }
            if (_prioCurBadge != null)
            {
                if (_targetPrio == 0) _prioCurBadge.Set("\u2014", T.Mute);
                else
                {
                    uint col = _targetPrio == ProcessPower.PriorityHigh ? T.Ok
                             : (_targetPrio == ProcessPower.PriorityAboveNormal ? T.Accent
                             : (_targetPrio == ProcessPower.PriorityIdle || _targetPrio == ProcessPower.PriorityBelowNormal ? T.Warn : T.Dim));
                    _prioCurBadge.Set(ProcessPower.PriorityLabel(_targetPrio), col);
                }
            }
            if (_btnPrioNormal != null)
            {
                _btnPrioNormal.Enabled = writable;
                _btnPrioNormal.Kind = _targetPrio == ProcessPower.PriorityNormal ? BtnKind.Accent : BtnKind.Ghost;
            }
            if (_btnPrioAbove != null)
            {
                _btnPrioAbove.Enabled = writable;
                _btnPrioAbove.Kind = _targetPrio == ProcessPower.PriorityAboveNormal ? BtnKind.Accent : BtnKind.Ghost;
            }
            if (_btnPrioHigh != null)
            {
                _btnPrioHigh.Enabled = writable;
                _btnPrioHigh.Kind = _targetPrio == ProcessPower.PriorityHigh ? BtnKind.Accent : BtnKind.Ghost;
            }
            if (_swEco != null)
            {
                _ecoSuppress = true;
                _swEco.On = _targetEcoKnown && _targetEco;
                _swEco.SetReadOnly(!writable || !_targetEcoKnown);
                _ecoSuppress = false;
            }
            if (_ecoStateText != null)
            {
                if (!writable)
                {
                    Set(_ecoStateText, "需锁定外部前台窗口后可调整");
                    _ecoStateText.Foreground = T.S(T.Mute);
                }
                else if (!_targetEcoKnown)
                {
                    Set(_ecoStateText, "当前不可读取（受保护或系统不支持）");
                    _ecoStateText.Foreground = T.S(T.Mute);
                }
                else if (_targetEco)
                {
                    Set(_ecoStateText, "已开启 · 调度偏向能效核/节能档");
                    _ecoStateText.Foreground = T.S(T.Warn);
                }
                else
                {
                    Set(_ecoStateText, "已关闭 · 释放全性能核心调度");
                    _ecoStateText.Foreground = T.S(T.Ok);
                }
            }
        }

        /// <summary>
        /// 掩码读数行。前缀与尾巴是灰的，只有十六进制本身是亮的——
        /// 这一行里唯一要逐位读的东西就是它，其余部分只是它的定语。
        /// 只在字符串真的变了才重建 Inlines（5 秒一拍，掩码一天也换不了几次）。
        /// </summary>
        private void SetMaskLine(string hex, string tail)
        {
            if (_maskLine == null) return;
            string key = hex + "|" + tail;
            if (key == _maskLineKey) return;
            _maskLineKey = key;

            _maskLine.Inlines.Clear();
            Run a = new Run("当前掩码 = ");
            a.Foreground = T.S(T.Mute);
            _maskLine.Inlines.Add(a);

            Run b = new Run(hex);
            b.Foreground = T.S(_targetKnown ? T.Hi : T.Mute);
            _maskLine.Inlines.Add(b);

            Run c = new Run(tail);
            c.Foreground = T.S(T.Dim);
            _maskLine.Inlines.Add(c);
        }

        private static int Bits(long mask)
        {
            int n = 0;
            for (int i = 0; i < Affinity.MaxBits; i++) if (((mask >> i) & 1L) != 0) n++;
            return n;
        }

        // ---- 亲和性与目标直调动作 --------------------------------------------
        private void ToggleSingleCore(Core c, int coreIndex)
        {
            int pid = ActionTarget(c);
            if (pid == 0) return;
            if (coreIndex < 0 || coreIndex >= Affinity.MaxBits) return;
            long cur = _targetKnown ? _targetMask : Affinity.Read(pid);
            if (cur == 0) cur = Affinity.AllMask();
            long bit = 1L << coreIndex;
            long next = (cur & bit) != 0 ? (cur & ~bit) : (cur | bit);
            if (next == 0)
            {
                c.Shell.Toast("至少需要保留 1 个允许运行的逻辑处理器", false);
                return;
            }
            ApplyMaskPreset(c, next, "单核 T" + coreIndex);
        }

        private void ToggleCluster(Core c, int ccdIndex)
        {
            int pid = ActionTarget(c);
            if (pid == 0 || c == null) return;
            long cluster = Affinity.CcdMask(c.Topo, ccdIndex);
            if (cluster == 0) return;
            long cur = _targetKnown ? _targetMask : Affinity.Read(pid);
            if (cur == 0) cur = Affinity.AllMask();
            bool allInClusterOn = (cur & cluster) == cluster;
            long next = allInClusterOn ? (cur & ~cluster) : (cur | cluster);
            if (next == 0) next = cluster; // 避免全关：改锁至本簇
            ApplyMaskPreset(c, next, "CCD" + ccdIndex);
        }

        private void ApplyMaskPreset(Core c, long mask, string label)
        {
            int pid = ActionTarget(c);
            if (pid == 0) return;
            if (mask == 0)
            {
                c.Shell.Toast("当前拓扑无法计算「" + label + "」掩码", false);
                return;
            }

            string err;
            bool ok = Affinity.Apply(pid, mask, out err);
            string name = ProcName.OrPid(pid);
            string hex = "0x" + mask.ToString("X");
            if (ok)
            {
                c.Shell.Toast("已设置 " + label + "：" + name + " · " + hex, true);
                c.Engine.Ledger.AppendAudit("AFFINITY " + name + " pid=" + pid + " (" + label + ") -> " + hex);
            }
            else
            {
                c.Shell.Toast("亲和性设置失败：" + (string.IsNullOrEmpty(err) ? "访问被拒绝" : err), false);
                c.Engine.Ledger.AppendAudit("AFFINITY FAIL " + name + " (" + label + ") -> " + err);
            }

            SyncTarget(true);
        }

        private void RestoreAll(Core c)
        {
            int pid = ActionTarget(c);
            if (pid == 0) return;

            string err;
            bool ok = Affinity.Restore(pid, out err);
            string name = ProcName.OrPid(pid);

            if (ok)
            {
                c.Shell.Toast("已恢复全核亲和性：" + name, true);
                c.Engine.Ledger.AppendAudit("AFFINITY " + name + " pid=" + pid + " -> ALL");
            }
            else
            {
                c.Shell.Toast("恢复全核失败：" + (string.IsNullOrEmpty(err) ? "访问被拒绝" : err), false);
                c.Engine.Ledger.AppendAudit("AFFINITY RESTORE FAIL " + name + " -> " + err);
            }

            SyncTarget(true);
        }

        private void ApplyTargetPriority(Core c, uint prioClass)
        {
            int pid = ActionTarget(c);
            if (pid == 0) return;

            string err;
            bool ok = ProcessPower.SetPriority(pid, prioClass, false, out err);
            string name = ProcName.OrPid(pid);
            string label = ProcessPower.PriorityLabel(prioClass);
            if (ok)
            {
                c.Shell.Toast("已设置 " + name + " 优先级：" + label, true);
                c.Engine.Ledger.AppendAudit("PRIO " + name + " pid=" + pid + " -> " + label);
            }
            else
            {
                c.Shell.Toast("优先级设置失败（" + name + "）：" + (string.IsNullOrEmpty(err) ? "权限不足或受保护进程" : err), false);
                c.Engine.Ledger.AppendAudit("PRIO FAIL " + name + " pid=" + pid + " -> " + err);
            }
            SyncTarget(true);
        }

        private void ApplyTargetEcoQoS(Core c, bool enable)
        {
            int pid = ActionTarget(c);
            if (pid == 0)
            {
                SyncTarget(true);
                return;
            }

            string err;
            bool ok = ProcessPower.SetEcoQoS(pid, enable, false, out err);
            string name = ProcName.OrPid(pid);
            if (ok)
            {
                c.Shell.Toast("已" + (enable ? "开启" : "关闭") + " " + name + " EcoQoS 能效模式", true);
                c.Engine.Ledger.AppendAudit("ECOQOS " + name + " pid=" + pid + " -> " + (enable ? "ON" : "OFF"));
            }
            else
            {
                c.Shell.Toast("EcoQoS 设置失败（" + name + "）：" + (string.IsNullOrEmpty(err) ? "权限不足或系统不支持" : err), false);
                c.Engine.Ledger.AppendAudit("ECOQOS FAIL " + name + " pid=" + pid + " -> " + err);
            }
            SyncTarget(true);
        }

        /// <summary>动作的目标：校验 PID 有效（>4）、非自身且为捕获到的外部前台进程。</summary>
        private int ActionTarget(Core c)
        {
            int pid = c == null ? 0 : c.LastForeignPid;
            if (pid <= 4 || pid == SelfPid)
            {
                if (c != null && c.Shell != null)
                    c.Shell.Toast("未识别到有效的外部前台进程：切到目标窗口后再操作", false);
                return 0;
            }
            return pid;
        }
    }
}
