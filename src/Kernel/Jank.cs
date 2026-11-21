// ============================================================================
//  Jank.cs — UI 线程停顿时钟（"卡不卡"的第一把尺子）
//
//  它只回答一个问题：用户盯着窗口的时候，UI 线程有没有被占住。
//  做法：在 UI 线程上挂一个 15ms 的 DispatcherTimer，量相邻两次触发的实际间隔。
//  间隔远超预期 = 这一段时间里 UI 线程没能回到消息循环 = 用户看见的一次顿。
//
//  为什么现有的读数都不能替代它：
//    · alloc-test 量分配量：分配多不等于看得见卡（60Hz 的冻结画刷不花任何代价）；
//    · --timing 量启动分段：只看得到启动那 2.4 秒，之后一整个会话是盲区；
//    · Perf 的 JitterP99 / TimerMs 量的是后台线程唤醒与系统定时器颗粒度：
//      后台线程抖动 12ms 的时候，UI 线程完全可能一动不动、非常流畅。
//  三者都回答不了「刚才那 80ms 用户是不是被冻住了」。
//
//  尺子的口径（报告 header 里重复一遍，避免读者误读）：
//    · 步进 15ms；间隔超过 2.5 倍（38ms）记一次 stall。
//    · 记的是「间隔」而不是「超时」：间隔里含一个正常步进，所以 38ms 的门槛
//      约等于 23ms 的实际停顿。宁可用整段间隔，也不做主观扣减。
//    · 相位标签由调用方给（Jank.Phase("tick:overview")），只在变化时赋值。
//    · DispatcherTimer 走 Normal 优先级：比它更低的排队活也会推迟它——而那正是
//      用户会感觉到的那种推迟（渲染/输入永远排在 Normal 之前，不会被它误伤）。
//    · 为什么是 15ms 而不是 16ms（这一个是实测出来的，不是拍脑袋）：
//      WPF 的 DispatcherTimer 落到 Win32 SetTimer，而 SetTimer/WM_TIMER 的派发被
//      钉在 15.625ms 的系统节拍上。步进写 16ms，每次都比一个节拍长一点点，
//      于是它有时踩第 1 拍、有时踩第 2 拍——实测（240 拍，同一台机器）
//      16ms → p50=30ms、直方图在 15ms 和 31ms 双峰（15x29 / 31x49）；
//      15ms → p50=15ms、90% 落在 14~17ms（15x90 / 16x58）。
//      双峰的尺子读不出 40ms 和 60ms 的区别，等于没有刻度。步进 15ms 换来的
//      是一个稳定的节拍，代价是标称 16.7fps 变成 15ms 一次——对测量无影响。
//      另：请求 0.5ms 定时器精度（NtSetTimerResolution）实测**不能**改变这个行为
//      （WM_TIMER 的合并发生在另一层），所以这里不去动系统的精度与功耗。
//    · 已知误报：拖动标题栏 / 打开系统菜单时 Windows 进入模态消息循环，
//      DispatcherTimer 不再被派发。那不是卡，是尺子被按住了。
//    · 尺子自己也要花时间：每 15ms 一次回调，实测 < 0.01% CPU；但它会让进程
//      每 15ms 醒一次，所以只该在诊断会话里开着，不该常驻。
//
//  零分配：固定环形缓冲 64 条 + 相位聚合固定 24 槽 + 间隔直方图 256 桶，
//          全部在类型加载时一次分配；触发路径只写 int 与字符串引用，不拼接、不装箱。
//  线程：只在 UI 线程写；Report() 任何线程可调（锁内快照，格式化在锁外）。
// ============================================================================
using System;
using System.Diagnostics;
using System.Text;
using System.Windows.Threading;

namespace Go
{
    public static class Jank
    {
        // ---- 口径 ----------------------------------------------------------
        private const int PeriodMs = 15;
        private const double StallFactor = 2.5;
        private const int StallMs = 38;      // ceil(15 * 2.5)

        // ---- 容量（全部固定，稳态零分配） ----------------------------------
        private const int RingSize = 64;
        private const int TopN = 10;
        private const int MaxPhases = 24;
        private const int HistBins = 256;    // 0..254ms 精确，255 = 溢出

        private struct Stall
        {
            public int Ms;          // 相邻两次触发的实际间隔
            public int AtMs;        // 相对 Begin 的毫秒
            public string Phase;    // 记录时的相位（字符串引用，不复制、不拼接）
        }

        private struct Slot
        {
            public string Phase;
            public int Count;
            public int MaxMs;
            public long SumMs;
        }

        private static readonly object _lock = new object();
        private static readonly Stall[] _ring = new Stall[RingSize];
        private static readonly Slot[] _slots = new Slot[MaxPhases];
        private static readonly int[] _hist = new int[HistBins];

        private static DispatcherTimer _timer;
        private static long _t0;            // Begin 的 Stopwatch 时间戳
        private static DateTime _wall0;     // Begin 的挂钟时刻（报告里给绝对时间戳）
        private static long _tEnd;          // Stop 的时刻（0 = 还在跑）
        private static long _last;          // 上一次触发的时间戳
        private static long _ticks;
        private static long _stalls;
        private static long _sumMs;
        private static int _maxMs;
        private static int _ringHead;
        private static int _ringCount;
        private static int _slotCount;
        private static int _slotOverflow;
        private static int _warm;           // 第一拍只记基线（含定时器启动延迟）

        private static volatile string _phase = "boot";

        // ====================================================================
        //  生命周期
        // ====================================================================

        /// <summary>当前相位标签（没有 Dispatcher 时也有值，只是没人读）。</summary>
        public static string PhaseName { get { return _phase; } }

        /// <summary>尺子是否在跑。</summary>
        public static bool Running { get { lock (_lock) { return _timer != null; } } }

        /// <summary>
        /// 在 UI 线程上开表。必须在有 Dispatcher 且已在跑消息循环的进程里调用；
        /// 离屏渲染 / 自检 / 控制台模式都不要调（那里没有消息循环，尺子只会读到 0）。
        /// 返回 false 表示没有可用的 Dispatcher（调用方不该把它当失败）。
        /// </summary>
        public static bool Begin()
        {
            // Dispatcher.CurrentDispatcher 永远不为 null（没有就给当前线程新建一个），
            // 所以"没有可用 Dispatcher"这个判断以前是死代码：在没有消息循环的线程上调 Begin
            // 会返回 true 并装一个永不触发的定时器，Report 于是报 state=running 而实际没在量。
            // FromThread 才是"这个线程有没有调度器"的真判据。
            Dispatcher d = Dispatcher.FromThread(System.Threading.Thread.CurrentThread);
            if (d == null || d.HasShutdownStarted || d.HasShutdownFinished) return false;
            if (!d.CheckAccess()) return false;         // 只允许 UI 线程开表

            lock (_lock)
            {
                if (_timer != null) return true;

                // 可重复 Begin：把上一轮的数据清干净，同一进程里能连测两次。
                _ringHead = 0; _ringCount = 0;
                _slotCount = 0; _slotOverflow = 0;
                _ticks = 0; _stalls = 0; _sumMs = 0; _maxMs = 0;
                Array.Clear(_hist, 0, _hist.Length);
                for (int i = 0; i < _ring.Length; i++) { _ring[i].Phase = null; _ring[i].Ms = 0; _ring[i].AtMs = 0; }
                for (int i = 0; i < _slots.Length; i++) { _slots[i].Phase = null; _slots[i].Count = 0; }

                _t0 = Stopwatch.GetTimestamp();
                _wall0 = DateTime.Now;
                _tEnd = 0;
                _last = _t0;
                _warm = 1;

                DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Normal, d);
                t.Interval = TimeSpan.FromMilliseconds(PeriodMs);
                t.Tick += OnTick;
                _timer = t;
                t.Start();
            }
            return true;
        }

        /// <summary>停表。数据保留，之后照样可以 Report（数字冻结在停表那一刻）。</summary>
        public static void Stop()
        {
            lock (_lock)
            {
                if (_timer != null)
                {
                    try { _timer.Stop(); _timer.Tick -= OnTick; } catch { /* 释放或收尾失败不改变本次结果 */ }
                    _timer = null;
                }
                _tEnd = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>设置相位标签。只在真的变化时赋值；传常量或缓存好的字符串，别在节拍里拼接。</summary>
        public static void Phase(string phase)
        {
            if (phase == null) return;
            string cur = _phase;
            if (object.ReferenceEquals(cur, phase)) return;
            _phase = phase;
        }

        // ====================================================================
        //  采样
        // ====================================================================
        private static void OnTick(object s, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            long prev = _last;
            _last = now;

            long delta = now - prev;
            // 整数运算 + 显式饱和：旧实现 (int)(delta * 1000.0 / Frequency) 在
            // 一次 >35 分钟的挂起（合盖/休眠/调试器断点）后超过 int.MaxValue，
            // unchecked 转换得到 int.MinValue，于是"最长停顿"被记成 0。
            long msL = delta * 1000L / Stopwatch.Frequency;
            if (msL < 0) msL = 0;
            if (msL > int.MaxValue - 1) msL = int.MaxValue - 1;
            int ms = (int)msL;

            lock (_lock)
            {
                // 首拍只记基线：定时器启动延迟可能高达上百毫秒，若先累计再判 warm，
                // 会出现"stalls=0 但 max=100ms"的自相矛盾读数。warm 检查必须在累计之前。
                if (_warm > 0) { _warm--; return; }

                _ticks++;
                _sumMs += ms;
                if (ms > _maxMs) _maxMs = ms;
                int bin = ms < HistBins ? ms : HistBins - 1;
                _hist[bin]++;

                if (ms <= StallMs) return;

                _stalls++;
                string ph = _phase;                    // 只搬引用

                Stall st;
                st.Ms = ms;
                long atL = (now - _t0) * 1000L / Stopwatch.Frequency;
                if (atL < 0) atL = 0;
                if (atL > int.MaxValue - 1) atL = int.MaxValue - 1;
                st.AtMs = (int)atL;
                st.Phase = ph;
                _ring[_ringHead] = st;
                _ringHead++;
                if (_ringHead >= RingSize) _ringHead = 0;
                if (_ringCount < RingSize) _ringCount++;

                Agg(ph, ms);
            }
        }

        /// <summary>相位聚合：按标签累计次数 / 峰值 / 总时长（槽位固定，满了进 overflow）。</summary>
        private static void Agg(string ph, int ms)
        {
            for (int i = 0; i < _slotCount; i++)
            {
                if (object.ReferenceEquals(_slots[i].Phase, ph) || string.Equals(_slots[i].Phase, ph))
                {
                    _slots[i].Count++;
                    if (ms > _slots[i].MaxMs) _slots[i].MaxMs = ms;
                    _slots[i].SumMs += ms;
                    return;
                }
            }
            if (_slotCount < MaxPhases)
            {
                Slot sl;
                sl.Phase = ph;
                sl.Count = 1;
                sl.MaxMs = ms;
                sl.SumMs = ms;
                _slots[_slotCount] = sl;
                _slotCount++;
                return;
            }
            _slotOverflow++;
        }

        // ====================================================================
        //  报告
        // ====================================================================

        /// <summary>可读多行文本：总时长 / stall 次数 / 按相位聚合 / 最长的 10 条。任何线程可调。</summary>
        public static string Report()
        {
            int ticks, stalls, maxMs, ringCount, slotCount, slotOverflow;
            long sumMs, elapsedMs;
            bool running;
            Stall[] top = new Stall[RingSize];
            Slot[] slots = new Slot[MaxPhases];
            int[] hist = new int[HistBins];

            lock (_lock)
            {
                ticks = _ticks > int.MaxValue ? int.MaxValue : (int)_ticks;
                stalls = _stalls > int.MaxValue ? int.MaxValue : (int)_stalls;
                maxMs = _maxMs;
                sumMs = _sumMs;
                ringCount = _ringCount;
                slotCount = _slotCount;
                slotOverflow = _slotOverflow;
                running = _timer != null;
                elapsedMs = elapsed(_tEnd > 0 ? _tEnd : Stopwatch.GetTimestamp());
                for (int i = 0; i < ringCount; i++) top[i] = _ring[i];
                for (int i = 0; i < slotCount; i++) slots[i] = _slots[i];
                Array.Copy(_hist, hist, HistBins);
            }

            SortTop(top, ringCount);

            // 全部走 Append：旧实现每行都用加号把片段和数字串起来，一份报告要造
            // 几十个中间字符串，而这些数字每秒都在变。这里不再产生任何拼接结果。
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("ui-thread stall clock  (period=").Append(PeriodMs)
              .Append("ms, stall>").Append(StallMs).Append("ms, ring=").Append(RingSize).AppendLine(")");
            if (ticks == 0 && elapsedMs == 0)
            {
                sb.AppendLine("state=not-started   (only enabled for real runs with a Dispatcher: --diag)");
                return sb.ToString();
            }

            sb.Append("state=").Append(running ? "running" : "stopped");
            sb.Append("  elapsed=");
            AppendSec(sb, elapsedMs);
            sb.Append("  ticks=").Append(ticks);
            sb.Append("  avg=");
            if (ticks > 0) sb.Append((sumMs / (double)ticks).ToString("0.0"));
            else sb.Append('-');
            sb.AppendLine("ms");

            sb.Append("stalls=").Append(stalls);
            sb.Append("  max=").Append(maxMs).Append("ms");
            sb.Append("  gap p50=").Append(Pct(hist, ticks, 0.50));
            sb.Append(" p95=").Append(Pct(hist, ticks, 0.95));
            sb.Append(" p99=").Append(Pct(hist, ticks, 0.99)).AppendLine(" ms");

            AppendPhases(sb, slots, slotCount, slotOverflow);
            AppendTop(sb, top, ringCount, stalls);
            sb.AppendLine("note: gap = 相邻两次触发的实际间隔（含一个 15ms 步进）；粒度为 Win32 定时器节拍 15.625ms，");
            sb.AppendLine("      所以 15/31/47ms 是节拍刻度，不是三次不同的停顿。拖动标题栏/系统菜单会被记成误报。");
            return sb.ToString();
        }

        /// <summary>按相位聚合：回答"该去哪个页面/哪段活里找"。</summary>
        private static void AppendPhases(StringBuilder sb, Slot[] slots, int slotCount, int slotOverflow)
        {
            sb.AppendLine("by phase:");
            if (slotCount == 0 && slotOverflow == 0)
            {
                sb.AppendLine("  (none)");
                return;
            }
            for (int i = 0; i < slotCount; i++)
            {
                sb.Append("  ");
                AppendPad(sb, slots[i].Phase, 20);
                sb.Append(" x").Append(slots[i].Count);
                sb.Append("  max=").Append(slots[i].MaxMs).Append("ms  sum=").Append(slots[i].SumMs).AppendLine("ms");
            }
            if (slotOverflow > 0)
            {
                sb.Append("  ");
                AppendPad(sb, "(overflow)", 20);
                sb.Append(" x").Append(slotOverflow).AppendLine();
            }
        }

        /// <summary>最长的 10 条：毫秒 + 相位 + 相对时间。ring 只留最近 64 条，样本多时要看这一行说明。</summary>
        private static void AppendTop(StringBuilder sb, Stall[] top, int ringCount, int stalls)
        {
            sb.Append("top ").Append(TopN).AppendLine(" (gap / phase / +since begin):");
            if (ringCount == 0)
            {
                sb.AppendLine("  (none)");
                return;
            }
            int n = ringCount < TopN ? ringCount : TopN;
            for (int i = 0; i < n; i++)
            {
                sb.Append("  ");
                AppendNum(sb, top[i].Ms, 5);
                sb.Append("ms  ");
                AppendPad(sb, top[i].Phase, 21);
                sb.Append("  ");
                AppendSec(sb, top[i].AtMs);
                sb.Append("  ");
                AppendWall(sb, top[i].AtMs);
                sb.AppendLine();
            }
            if (stalls > ringCount)
            {
                sb.Append("  (ring holds the latest ").Append(ringCount)
                  .Append(" of ").Append(stalls).AppendLine(" stalls)");
            }
        }

        private static long elapsed(long now)
        {
            if (_t0 == 0) return 0;
            return (now - _t0) * 1000L / Stopwatch.Frequency;
        }

        /// <summary>按毫秒降序、同毫秒按时间先后。</summary>
        private static void SortTop(Stall[] arr, int n)
        {
            for (int i = 1; i < n; i++)
            {
                Stall key = arr[i];
                int j = i - 1;
                while (j >= 0 && arr[j].Ms < key.Ms)
                {
                    arr[j + 1] = arr[j];
                    j--;
                }
                arr[j + 1] = key;
            }
        }

        private static int Pct(int[] hist, int total, double p)
        {
            if (total <= 0) return 0;
            long want = (long)Math.Ceiling(total * p);
            if (want < 1) want = 1;
            long acc = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                acc += hist[i];
                if (acc >= want) return i;
            }
            return hist.Length - 1;
        }

        /// <summary>绝对时间戳：和 audit / error.log 对表时用。直接追加，不建中间字符串。</summary>
        private static void AppendWall(StringBuilder sb, int atMs)
        {
            if (_wall0.Ticks == 0) return;
            sb.Append(_wall0.AddMilliseconds(atMs).ToString("HH:mm:ss.fff"));
        }

        /// <summary>"+1.234s"，直接追加到共享缓冲。</summary>
        private static void AppendSec(StringBuilder sb, long ms)
        {
            sb.Append('+').Append((ms / 1000.0).ToString("0.000")).Append('s');
        }

        /// <summary>
        /// 定宽左对齐。旧实现每格 new 一个 StringBuilder，一份报告几十个对象；
        /// 相位行的宽度决定了"哪一段在卡"能不能一眼对齐，所以这里仍然要补空格。
        /// </summary>
        private static void AppendPad(StringBuilder sb, string s, int width)
        {
            if (s == null) s = "(none)";
            sb.Append(s);
            for (int i = s.Length; i < width; i++) sb.Append(' ');
        }

        /// <summary>定宽右对齐的整数。</summary>
        private static void AppendNum(StringBuilder sb, int v, int width)
        {
            string s = v.ToString();
            for (int i = s.Length; i < width; i++) sb.Append(' ');
            sb.Append(s);
        }
    }
}
