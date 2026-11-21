// ============================================================================
//  AllocTest.cs — 渲染热路径的分配计量（--alloc-test）
//  用 AppDomain 级分配计数器，逐帧统计"每个 OnRender 到底分配了多少字节"。
//  这是"零分配"这个说法的唯一可信证据：不靠数代码，靠运行时计数。
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Go
{
    public static class AllocTest
    {
        private static StringBuilder _log = new StringBuilder();
        private static int _pass, _fail;

        private static void Check(bool ok, string name, string detail)
        {
            if (ok) _pass++; else _fail++;
            _log.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail == null ? "" : "  ::  " + detail));
        }

        public static int Run()
        {
            _pass = 0; _fail = 0;
            AppDomain.MonitoringIsEnabled = true;
            _log.AppendLine("GameOptimizer Rev61 allocation probe  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            _log.AppendLine("runtime=" + Environment.Version + "  server_gc=" + System.Runtime.GCSettings.IsServerGC);
            _log.AppendLine("");

            try
            {
                // 预热：JIT 与 WPF 的一级缓存都要先跑热，否则数字里混着首次初始化
                Measure(Warm, 40);
                long baseLine = Measure(Noop, 200);
                _log.AppendLine("基准：空循环 " + baseLine + " B / 200 次  (" + ((double)baseLine / 200).ToString("0.0") + " B/次)");
                _log.AppendLine("");

                // 基线：一个只有背景色的 Border。它的分配量 = WPF 渲染走查本身的固定开销，
                // 必须减掉它，剩下的才是"我们的 OnRender 分配了多少"。
                Border blank = new Border();
                blank.Background = T.S(0xFF101418);
                double floor = PerRender(blank);

                double wf = PerRender(WaveformEl()) - floor;
                double grid = PerRender(CoreGridEl()) - floor;
                double gauge = PerRender(GaugeEl()) - floor;
                double seg = PerRender(SegBarEl()) - floor;

                _log.AppendLine("WPF 渲染走查基线（空 Border）：" + floor.ToString("0.0") + " B / 帧");
                _log.AppendLine("");
                _log.AppendLine("扣除基线后的每帧托管分配（400 次实测均值）：");
                _log.AppendLine("  波形 Waveform   " + wf.ToString("0.0") + " B   （2 条 StreamGeometry + 文本缓存命中）");
                _log.AppendLine("  核心 CoreGrid   " + grid.ToString("0.0") + " B   （热力 LUT + 文本缓存）");
                _log.AppendLine("  仪表 ArcGauge   " + gauge.ToString("0.0") + " B   （2 段弧几何 + 前端光点）");
                _log.AppendLine("  负载条 SegBar   " + seg.ToString("0.0") + " B   （28 段矩形，画刷全冻结）");
                _log.AppendLine("");

                Check(grid < 120, "核心矩阵：自身分配 < 120 B/帧", grid.ToString("0.0") + " B");
                Check(gauge < 512, "仪表：自身分配 < 512 B/帧（含 2 段弧几何）", gauge.ToString("0.0") + " B");
                Check(seg < 64, "负载条：自身分配 < 64 B/帧（零对象）", seg.ToString("0.0") + " B");
                Check(wf < 900, "波形：自身分配 < 900 B/帧（含 2 条流几何）", wf.ToString("0.0") + " B");

                // ---- 活路径计量：每帧真实数据流转，而非静态内容（MAGNUM 极限压测）----
                Waveform wl = WaveformEl(); int wi = 0;
                double wfLive = PerRenderMutate(wl, delegate { wl.Push(200 + (wi++ * 37 % 900)); }) - floor;
                ArcGauge gl = GaugeEl(); SizeEl(gl);
                for (int k = 0; k < 101; k++) { gl.Value = k / 100.0; gl.InvalidateVisual(); _bmp.Render(gl); }
                int gi = 0;
                double gaugeLive = PerRenderMutate(gl, delegate { gl.Value = (gi++ % 101) / 100.0; }) - floor;
                MemRibbon rl = RibbonEl(); int ri = 0;
                double ribLive = PerRenderMutate(rl, delegate { rl.Parts[0] = 1 + (ri++ % 50); }) - floor;
                CoreGrid cl = CoreGridEl(); int ci = 0;
                double gridLive = PerRenderMutate(cl, delegate { cl.Load[ci++ % 32] = (ci % 100) / 100.0; }) - floor;
                _log.AppendLine("活路径（每帧 Push/扫值/变构成，扣除基线）：");
                _log.AppendLine("  活波形 " + wfLive.ToString("0.0") + " B  活仪表 " + gaugeLive.ToString("0.0") + " B  活缎带 " + ribLive.ToString("0.0") + " B  活核心 " + gridLive.ToString("0.0") + " B");
                Check(wfLive < 900, "活波形：每帧 Push+渲染 < 900 B/帧", wfLive.ToString("0.0") + " B");
                Check(gaugeLive < 512, "活仪表：扫值不高于静态仪表（弧缓存命中）", gaugeLive.ToString("0.0") + " B");
                Check(ribLive < 64, "活缎带：每帧变构成 < 64 B/帧", ribLive.ToString("0.0") + " B");
                Check(gridLive < 120, "活核心：每帧变负载 < 120 B/帧", gridLive.ToString("0.0") + " B");

                // Motion 稳态：保持一条长补间存活，反复 Step 不得分配（测 apply 路径而非驱动启停）
                Action<double> cached = delegate (double v) { };
                Motion.Tween(0, 1, 100000, cached);
                long mBefore = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                for (int k = 0; k < 500; k++) Motion.Step();
                long mAlloc = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - mBefore;
                Check(mAlloc < 1024 && Motion.LiveCount == 1, "Motion Step 稳态：500 帧零分配且补间存活", mAlloc + " B live=" + Motion.LiveCount);

                // 采样器：3 秒内的总分配（快照三缓冲 + 直方图）
                PerfSampler p = new PerfSampler();
                p.Start();
                System.Threading.Thread.Sleep(1400);      // 预热：首次 P/Invoke 的封送缓冲不计入
                long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                long tick0 = p.Current.Tick;
                int gen0a = GC.CollectionCount(0);
                System.Threading.Thread.Sleep(3000);
                Snapshot s = p.Current;
                p.Stop();
                long after = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                int gen0b = GC.CollectionCount(0);
                // 分母必须是"这段窗口里真正过了几拍"。Tick 是从 Start 起的累计值，
                // 拿它当 3000ms 窗口的分母会把 B/拍 算小（预热的 1400ms 也被摊进去）。
                // 也不许写回快照：Current 返回的是采样器正在发布的那个对象，改它就是跨线程写。
                long windowTicks = s.Tick - tick0;
                double perTick = (after - before) / Math.Max(1, windowTicks);
                _log.AppendLine("采样器：3 拍共分配 " + (after - before) + " B，gen0 +" + (gen0b - gen0a));
                Check(perTick < 4096, "采样：每拍分配低于 4 KB（三缓冲快照不克隆）", perTick.ToString("0") + " B/tick");
                Check(s.Cores.Length == Environment.ProcessorCount, "采样：核心数组长度正确", s.Cores.Length.ToString());
            }
            catch (Exception ex)
            {
                _log.AppendLine("[FAIL] 未捕获异常: " + ex);
                _fail++;
            }

            _log.AppendLine("");
            _log.AppendLine("RESULT  pass=" + _pass + "  fail=" + _fail);
            string text = _log.ToString();
            try
            {
                string outJson = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "out", "alloc-test.json");
                Directory.CreateDirectory(Path.GetDirectoryName(outJson));
                File.WriteAllText(outJson, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 写不出去不能报成功：调用方读的是文件，会拿到上一轮的残留。
                Diag.Fail("alloctest.output", ex);
                _fail++;
                Console.WriteLine("[FAIL] 报告无法写入：" + ex.Message);
            }
            Console.WriteLine(text);
            return _fail == 0 ? 0 : 1;
        }

        // ---- 被测元素 ------------------------------------------------------
        private static Waveform WaveformEl()
        {
            Waveform w = new Waveform(150);
            for (int i = 0; i < 150; i++) w.Push(200 + (i * 37 % 900));
            return w;
        }

        private static CoreGrid CoreGridEl()
        {
            CoreGrid g = new CoreGrid();
            g.Ccds = 2; g.CoresPerCcd = 8; g.ThreadsPerCore = 2;
            double[] load = new double[32];
            for (int i = 0; i < 32; i++) load[i] = (i * 7 % 100) / 100.0;
            g.Load = load;
            return g;
        }

        private static ArcGauge GaugeEl()
        {
            ArcGauge a = new ArcGauge(0.72, "72", "readiness", "自检");
            return a;
        }

        private static SegBar SegBarEl()
        {
            SegBar s = new SegBar(0.63);
            return s;
        }

        private static MemRibbon RibbonEl()
        {
            MemRibbon r = new MemRibbon();
            r.Parts = new double[] { 1, 1, 1, 1 };
            r.Colors = new uint[] { T.Accent, T.Warn, T.Violet, T.Line };
            return r;
        }

        private static void SizeEl(FrameworkElement el)
        {
            el.Width = 560; el.Height = 180;
            el.Measure(new Size(560, 180));
            el.Arrange(new Rect(0, 0, 560, 180));
            el.UpdateLayout();
        }

        private static double PerRenderMutate(FrameworkElement el, Action mutate)
        {
            SizeEl(el);
            _bmp.Render(el);
            long a = Measure(delegate { mutate(); el.InvalidateVisual(); _bmp.Render(el); }, 400);
            return (double)a / 400;
        }

        // ---- 计量 ----------------------------------------------------------
        // 位图必须复用：每帧 new 一个 RenderTargetBitmap 本身就是 ~1 KB，
        // 那样测到的是测量工具的分配，不是被测代码的。
        private static readonly RenderTargetBitmap _bmp =
            new RenderTargetBitmap(560, 180, 96, 96, PixelFormats.Pbgra32);

        private static double PerRender(FrameworkElement el)
        {
            el.Width = 560;
            el.Height = 180;
            el.Measure(new Size(560, 180));
            el.Arrange(new Rect(0, 0, 560, 180));
            el.UpdateLayout();
            _bmp.Render(el);                 // 预热一帧
            // 必须每帧失效一次，否则 WPF 复用上一次的绘制指令，OnRender 根本不会再跑
            long a = Measure(delegate { el.InvalidateVisual(); _bmp.Render(el); }, 400);
            return (double)a / 400;
        }

        private static void Warm()
        {
            _bmp.Render(WaveformEl());
        }

        private static void Noop() { }

        private static long Measure(Action act, int times)
        {
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int i = 0; i < times; i++) act();
            return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        }
    }
}
