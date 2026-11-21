// ============================================================================
//  Scheduler.Readout.cs — 03 调度与定时器：顶部读数带 + 快拍 / 慢拍分发（Scheduler 分片）
//
//  为什么切：500ms 热路径只碰 Snapshot、5 秒才碰注册表——这条开销纪律
//  只有把两条刷新链放在一起才看得见；散进各面板构建函数就没法核对了。
//
//  Rev86：原来散在三处的读数（矩阵 hero 的 60 号颗粒、矩阵右侧的 32/32 掩码、
//  页头的"颗粒 · 抖动"一行）收进一条四格读数带。
//  一条带回答四个问题：多细（颗粒）、允许谁（掩码）、几个核（活动核数）、
//  谁在撑着（租约）。四格同一构造：读数头（值 + 单位 + 变化量）→ 中段槽 → 注脚。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;

namespace Go.Pages
{
    public partial class Scheduler : Page
    {
        // ---- Rev130 2×2 四象限遥测 Bento 矩阵（停靠在硅晶拓扑右侧） --------------
        private UIElement BuildBand(Core c)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            g.RowDefinitions.Add(new RowDefinition());

            int total = Math.Max(1, c.Topo.Logical);

            // 1) 内核定时器颗粒：NtQueryTimerResolution 实测，不是任何一方的请求值。
            _bandTimer = Ui.Num("—", T.FsNumL, T.Hi);
            _dTimer = new DeltaChip();
            _spTimer = new Sparkline(TrendN);
            _spTimer.Height = 16;
            _spTimer.Tint = T.Accent;
            _spTimer.Opacity = 0.9;
            _bandTimerNote = Ui.Mono("等待采样", T.FsNano, T.Mute);

            // 2) 掩码：紧凑四象限里使用 18px 清晰等宽字号避免溢出。
            _maskFont = total > 32 ? 14 : 18;
            _bandMask = Ui.Mono("—", _maskFont, T.Hi);
            _maskStrip = new MaskStrip();
            _bandMaskNote = Ui.Mono("前台外部进程", T.FsNano, T.Mute);

            // 3) 活动核数：前台进程此刻允许用几个逻辑处理器。
            _bandCores = Ui.Num("—", T.FsNumL, T.Hi);
            _coresBar = new SegBar(0);
            _coresBar.Height = 3;
            _coresBar.Tint = T.Accent;
            _bandCoresNote = Ui.Mono("前台可用 / 逻辑处理器", T.FsNano, T.Mute);

            // 4) 租约：本工具是否正持有 0.5ms 精度（会话级，离页不撤销）。
            _bandLease = Ui.Num("—", T.FsNumL, T.Hi);
            _leaseBar = new SegBar(0);
            _leaseBar.Height = 3;
            _leaseBar.Tint = T.Accent;
            _bandLeaseNote = Ui.Mono("未持有 · 打开下方开关", T.FsNano, T.Mute);

            FrameworkElement[] cells = new FrameworkElement[4];
            cells[0] = Ui.GlassTile(Ui.Stat("TIMER RES", Head(_bandTimer, "ms", _dTimer), Slot(_spTimer), Ui.Note(_bandTimerNote)), 12, T.Accent);
            cells[1] = Ui.GlassTile(Ui.Stat("AFFINITY MASK", Head(_bandMask, "", null), Slot(_maskStrip), Ui.Note(_bandMaskNote)), 12, T.Accent2);
            cells[2] = Ui.GlassTile(Ui.Stat("ACTIVE CORES", Head(_bandCores, "/ " + total, null), Slot(_coresBar), Ui.Note(_bandCoresNote)), 12, T.Ok);
            cells[3] = Ui.GlassTile(Ui.Stat("TIMER LEASE", Head(_bandLease, "ms", null), Slot(_leaseBar), Ui.Note(_bandLeaseNote)), 12, T.Violet);

            Grid.SetColumn(cells[0], 0); Grid.SetRow(cells[0], 0);
            Grid.SetColumn(cells[1], 2); Grid.SetRow(cells[1], 0);
            Grid.SetColumn(cells[2], 0); Grid.SetRow(cells[2], 2);
            Grid.SetColumn(cells[3], 2); Grid.SetRow(cells[3], 2);
            for (int i = 0; i < 4; i++) g.Children.Add(cells[i]);
            return Ui.Panel(g, 14);
        }

        // ====================================================================
        //  快拍：只碰 Snapshot
        // ====================================================================
        private void RefreshFast()
        {
            Core c = C;
            if (c == null || _bandTimer == null) return;

            Snapshot s = c.Perf.Current;
            bool sampled = s.Tick > 0;

            PushTimerCell(s, sampled);
            PushLeaseCell(c, s, sampled);
            PushMatrix(c, s, sampled);
            PushDpc(s, sampled);

            // 目标进程：只在 pid 变化时重读（一次 OpenProcess）。掩码变了，
            // 读数带第二格与矩阵会一起在 UpdateMaskBlock 里被改写。
            SyncTarget(false);
        }

        /// <summary>第一格：颗粒度。趋势带每拍推一个点（时间轴等距，跳拍会让形状撒谎）。</summary>
        private void PushTimerCell(Snapshot s, bool sampled)
        {
            double ms = s.TimerMs;
            bool has = sampled && ms > 0;

            _spTimer.Push(has ? Math.Min(1, ms / 15.625) : 0);

            int back = (_hHead - 9 + TrendN) % TrendN;
            bool baseline = _hCount >= 9;
            double prev = baseline ? _hTimer[back] : ms;
            _hTimer[_hHead] = ms;
            _hHead = (_hHead + 1) % TrendN;
            if (_hCount < TrendN) _hCount++;

            // 颗粒变粗 = 唤醒变迟 = 变差，所以 higherIsBetter = false。
            // 变化量按 0.05 ms 刻度比较，只有刻度真的变了才拼字符串（稳态 0 分配）。
            int dq = (baseline && has && prev > 0) ? (int)Math.Round(Math.Abs(ms - prev) * 20) : -1;
            if (dq != _deltaShown)
            {
                _deltaShown = dq;
                _deltaBody = dq >= 0 ? ((dq / 20.0).ToString("0.0") + " ms") : "";
            }
            _dTimer.Set(Dir(ms, prev, 0.05), _deltaBody, false);

            int q = has ? (int)Math.Round(ms * 20) : -1;      // 0.05 ms 刻度
            if (q == _timerShown) return;
            _timerShown = q;
            Set(_bandTimer, has ? ms.ToString("0.0") : "—");

            double finest = Math.Min(s.TimerMinMs, s.TimerMaxMs);
            double coarsest = Math.Max(s.TimerMinMs, s.TimerMaxMs);
            Set(_bandTimerNote, (sampled && finest > 0)
                ? "可达 " + finest.ToString("0.0") + " – " + coarsest.ToString("0.0") + " ms · 内核实测"
                : "可达范围 — · 等待采样");
        }

        /// <summary>第四格：租约 + 抖动。租约与实测对不上时如实说"对不上"。</summary>
        private void PushLeaseCell(Core c, Snapshot s, bool sampled)
        {
            bool lease = c.Timer.Active;
            double granted = c.Timer.GrantedMs;
            if (_swTimer != null && _swTimer.On != lease) _swTimer.On = lease;

            double finest = Math.Min(s.TimerMinMs, s.TimerMaxMs);
            double coarsest = Math.Max(s.TimerMinMs, s.TimerMaxMs);
            int fq = (sampled && finest > 0) ? (int)Math.Round(finest * 10) : -1;
            int cq = (sampled && finest > 0) ? (int)Math.Round(coarsest * 10) : -1;
            if (fq != _rangeFinestShown || cq != _rangeCoarsestShown)
            {
                _rangeFinestShown = fq;
                _rangeCoarsestShown = cq;
                Set(_tRange, fq > 0
                    ? ("可达 " + finest.ToString("0.0") + " \u2013 " + coarsest.ToString("0.0") + " ms")
                    : "可达范围 \u2014");
            }

            int q = (lease && granted > 0) ? (int)Math.Round(granted * 100) : -1;
            int p99 = s.JitterLive ? (int)Math.Round(s.JitterP99Us) : -1;
            int avg = s.JitterLive ? (int)Math.Round(s.JitterAvgUs) : -1;
            if (q == _leaseShown && p99 == _jitterP99 && avg == _jitterAvg) return;
            _leaseShown = q;
            _jitterP99 = p99;
            _jitterAvg = avg;

            Set(_bandLease, q > 0 ? granted.ToString("0.0") : "—");
            _leaseBar.Value = q > 0 ? 1.0 : 0.0;
            Set(_bandLeaseNote, (q > 0 ? "持有中" : "未持有") + " · 唤醒抖动 P99 "
                + (p99 < 0 ? "—（探针待机）" : p99.ToString("0") + " µs"));

            Set(_tJitter, p99 >= 0
                ? ("唤醒抖动 P99 " + p99.ToString("0") + " µs · AVG " + avg.ToString("0") + " µs")
                : "唤醒抖动 —（探针待机）");
            Set(_leaseMeta, q > 0 ? ("租约 · 本工具持有 " + granted.ToString("0.0") + " ms") : "租约 · 未持有");
        }

        private void PushMatrix(Core c, Snapshot s, bool sampled)
        {
            bool coresOk = sampled && s.Tick >= 2 && s.Cores != null && s.Cores.Length > 0;
            if (_matrix != null && coresOk) _matrix.SetLoad(s.Cores);
            PushClusters(c, s.Cores, coresOk);
        }

        private void PushDpc(Snapshot s, bool sampled)
        {
            bool ok = sampled && s.Tick >= 2;
            if (_dpcBar != null) _dpcBar.Set(ok ? s.DpcPercent : 0, ok);
            int q = ok ? (int)Math.Round(s.DpcPercent * 100) : -1;
            if (q == _dpcShown) return;
            _dpcShown = q;
            Set(_dpcVal, q >= 0 ? ((q / 100.0).ToString("0.00") + " %") : "—");
        }

        private void PushClusters(Core c, double[] cores, bool sampled)
        {
            if (_ccdRun == null) return;
            int per = Math.Max(1, c.Topo.CoresPerCcd * Math.Max(1, c.Topo.ThreadsPerCore));
            int n = _ccdRun.Length;
            for (int i = 0; i < n; i++)
            {
                double avg = 0;
                bool ok = false;
                if (sampled && cores != null)
                {
                    int lo = i * per;
                    int hi = Math.Min(cores.Length, lo + per);
                    if (lo < hi)
                    {
                        double sum = 0;
                        for (int k = lo; k < hi; k++) sum += cores[k];
                        avg = sum / (hi - lo);
                        ok = true;
                    }
                }
                // 每簇读数按整数百分比比较：不变就不拼字符串（500ms 一拍，两簇 = 每天 34 万次）
                int pct = ok ? (int)Math.Round(avg * 100) : -1;
                if (pct != _ccdShown[i])
                {
                    _ccdShown[i] = pct;
                    SetRun(_ccdRun[i], pct >= 0 ? pct.ToString() : "—");
                }
                if (_spark[i] != null && ok) { _spark[i].Push(avg); _sparkCount[i]++; }
                // 走势只有几秒钟历史时，与其画一条看不出形状的短线，不如把"采集中"说出来。
                if (_ccdSub[i] != null)
                {
                    int sec = _sparkCount[i] / 2;
                    if (sec >= SparkSec) sec = SparkSec;
                    if (sec != _sparkShown[i])
                    {
                        _sparkShown[i] = sec;
                        _ccdSub[i].Text = sec >= SparkSec ? "60 s" : ("采样 " + sec + " s");
                    }
                }
            }
        }

        private static int Dir(double now, double prev, double eps)
        {
            double d = now - prev;
            if (d > eps) return 1;
            if (d < -eps) return -1;
            return 0;
        }

        // ====================================================================
        //  慢拍：注册表 6 项 + 量子策略（与审计表共用同一次读取，不重复打开键）
        // ====================================================================
        private void RefreshSlow()
        {
            Core c = C;
            if (c == null || _rows == null) return;

            int ok = 0, deviate = 0, unknown = 0;
            string prio = null;

            for (int i = 0; i < _rows.Length; i++)
            {
                string cur;
                int st = ReadAuditRow(_rows[i], out cur);
                if (st == AuditOk) ok++;
                else if (st == AuditUnknown) unknown++;
                else deviate++;
                if (i == 0) prio = cur;
            }

            // 量子策略与审计表第 0 行是同一个键：复用这次读数，不再单独 Ops.Probe 一次。
            UpdateQuantum(prio);

            string meta = ok + " 项符合";
            if (deviate > 0) meta += " · " + deviate + " 项偏离";
            if (unknown > 0) meta += " · " + unknown + " 项读不到";
            Set(_auditMeta, meta);
            if (_auditMeta != null) _auditMeta.Foreground = T.S(deviate > 0 ? T.Warn : (unknown > 0 ? T.Mute : T.Dim));

            SyncTarget(false);
        }

        /// <summary>
        /// 读一行并落色。返回结论码（不是"成没成功"）：
        ///   0 符合 / 1 偏离 / 2 值不存在 / 3 读不到。
        /// "不存在"和"读不到"必须分开——前者是这台机器的配置事实，后者是本次读数的失败，
        /// 合成一个布尔会让"拒绝访问"看起来像"值被改坏了"。
        /// </summary>
        private int ReadAuditRow(AuditRow r, out string cur)
        {
            RegValue rv = Reg.Read("HKLM", r.Path, r.ValueName);
            int st;
            if (rv.Denied) { cur = "(拒绝访问)"; st = AuditUnknown; }
            else if (rv.Error != null) { cur = "(读取失败)"; st = AuditUnknown; }
            else if (!rv.Existed) { cur = "(不存在)"; st = AuditMissing; }
            else
            {
                cur = Reg.ToText(rv.Value);
                if (cur == null) cur = "";
                st = string.Equals(cur, r.Expect, StringComparison.OrdinalIgnoreCase) ? AuditOk : AuditDeviate;
            }
            ApplyAuditRow(r, st, cur);
            return st;
        }

        private void UpdateQuantum(string cur)
        {
            if (_prioRun == null) return;
            int n = 0;
            bool parsed = cur != null && int.TryParse(cur, out n);
            if (!parsed)
            {
                bool denied = cur != null && cur.IndexOf("拒绝", StringComparison.Ordinal) >= 0;
                SetRun(_prioRun, "—");
                _prioRun.Foreground = T.S(T.Mute);
                Set(_prioHex, "—");
                Set(_prioNote, denied ? "读不到：该键需要管理员权限" : "读不到：值不存在或类型不匹配");
                _prioNote.Foreground = T.S(T.Mute);
                return;
            }

            SetRun(_prioRun, n.ToString());
            Set(_prioHex, "0x" + n.ToString("X2"));
            bool known = n == 2 || n == 38;
            _prioRun.Foreground = T.S(known ? T.Hi : T.Warn);

            string note;
            if (n == 2) note = "默认值 · 可变长量子 + 前台 3:1 加权";
            else if (n == 38) note = "短量子 + 前台 3:1 加权（激进档）";
            else if (n == 40) note = "固定时间片 + 取消前台加权（常见负优化）";
            else note = "非默认值 · 前台加权与时间片均由位域决定";
            Set(_prioNote, note);
            _prioNote.Foreground = T.S(known ? T.Mute : T.Warn);
        }
    }
}
