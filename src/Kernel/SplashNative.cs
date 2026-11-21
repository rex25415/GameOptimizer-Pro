// ============================================================================
//  SplashNative.cs — 原生启动卡（不依赖 WPF）
//
//  为什么不用 WPF 窗口做启动页：
//  实测它要 949ms 构造（WPF 字体栈冷初始化）+ 1144ms 开第一个窗口；
//  而主窗口在它之后只要 162ms。也就是说 WPF 启动页把"第一次开窗"的开销
//  挪到了自己身上——总时间不亏，但用户在 2.7 秒里什么都看不到。
//
//  这里用 GDI+ 画一张 396x132 的卡片，约 40ms 出图，主线程继续去建 WPF。
//  线程、窗口、绘图全部隔离，任何异常都直接放弃（绝不拖累主程序）。
// ============================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;

namespace Go
{
    public static class SplashNative
    {
        private const string ClassName = "GoSplashWnd";
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_PAINT = 0x000F;
        private const int WM_CLOSE = 0x0010;
        private const int WM_DESTROY = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x, y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int L, T, R, B; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        private static extern bool GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int idx);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        private static Thread _thread;
        private static IntPtr _hwnd = IntPtr.Zero;
        private static volatile bool _closing;

        private static int _w, _h;

        public static void Show()
        {
            try
            {
                if (_thread != null) return;
                _closing = false;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            catch (Exception ex)
            {
                // 启动卡是纯装饰：建不出来就当作没有，绝不能拖累主程序。
                // 但 _thread 已被赋值，这里回滚成 null 才能让下一次 Show 还有机会。
                _thread = null;
                Diag.Fail("splash.show", ex);
                Go.Tray.TrayTrace.Write("splash show failed: " + ex.Message);
            }
        }

        public static void Close()
        {
            try
            {
                // 先把"要关"的状态立起来，再投消息：启动卡线程可能还没建出窗口，
                // 只靠 PostMessage 会漏掉（窗口随后才创建，于是那张卡永远停在屏幕中央）。
                _closing = true;
                if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                // 不再 WaitOne：旧实现在 Show 与 app.Run 之间阻塞 UI 线程最多 50ms，正压在首帧渲染前。
                // 线程是 IsBackground，WM_CLOSE 由它自己的消息循环异步处理，进程退出自然收尾。
            }
            catch (Exception ex)
            {
                // 关闭失败不是错误：线程是后台线程，进程退出时自然结束。
                Diag.Fail("splash.close", ex);
                Go.Tray.TrayTrace.Write("splash close: " + ex.Message);
            }
        }

        private static void Loop()
        {
            IntPtr mine = IntPtr.Zero;
            try
            {
                WNDCLASSEX wc = new WNDCLASSEX();
                wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc);
                wc.hInstance = GetModuleHandle(null);
                wc.lpszClassName = ClassName;
                wc.style = 0x0002;   // CS_HREDRAW | CS_VREDRAW 之外只要重绘
                RegisterClassEx(ref wc);

                uint dpi = GetDpiForSystem();
                if (dpi < 96) dpi = 96;
                double k = dpi / 96.0;
                _w = (int)Math.Round(396 * k);
                _h = (int)Math.Round(132 * k);

                int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
                int x = (sw - _w) / 2, y = (sh - _h) / 2 - (int)(40 * k);

                _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
                    ClassName, "GameOptimizer", WS_POPUP, x, y, _w, _h,
                    IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero) return;
                mine = _hwnd;

                // Close() 已经先发生了（主窗口比启动卡线程还快）：建出来立刻拆掉，
                // 否则这张置顶卡会一直盖在桌面上，直到进程结束。
                if (_closing)
                {
                    DestroyWindow(mine);
                    _hwnd = IntPtr.Zero;
                    return;
                }

                try
                {
                    int round = DWMWCP_ROUND;
                    DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                }
                catch { /* 可忽略：Win10 / 未开 DWM 合成时这个属性不存在，下面的 SetWindowRgn 会兜住圆角 */ }

                // WS_POPUP 窗口不会自动拿到 DWM 圆角，用区域裁一次（半径 10 DIP）
                try
                {
                    int rad = (int)Math.Round(20 * k);
                    IntPtr rgn = CreateRoundRectRgn(0, 0, _w + 1, _h + 1, rad, rad);
                    if (rgn != IntPtr.Zero) SetWindowRgn(_hwnd, rgn, true);
                }
                catch { /* 可忽略：圆角区域建不出来只是方角，卡片本身照常显示 */ }

                ShowWindow(_hwnd, 4);   // SW_SHOWNOACTIVATE
                SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref m);
                    DispatchMessage(ref m);
                }
            }
            catch (Exception ex)
            {
                // 原生窗口/绘图任何一步失败都不值得让主程序付出代价：直接放弃启动卡。
                Go.Tray.TrayTrace.Write("splash loop: " + ex.Message);
            }
            finally
            {
                // 线程退出后必须把状态清干净：旧代码让 _thread 永远非 null，
                // 于是"关闭后再显示"变成静默空操作，也没有任何重试入口。
                if (_hwnd == mine) _hwnd = IntPtr.Zero;
                if (_thread == Thread.CurrentThread) _thread = null;
            }
        }

        private static readonly WndProcDelegate _proc = WndProc;

        private delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);

        private static IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            try
            {
                if (msg == WM_PAINT)
                {
                    PAINTSTRUCT ps;
                    IntPtr hdc = BeginPaint(h, out ps);
                    try { Paint(hdc); }
                    catch (Exception ex)
                    {
                        // 单帧绘制失败（字体/DC 异常）不能打断消息循环：卡片留空比进程崩好。
                        // 但"启动卡一片黑"要能归因，所以记账。
                        Diag.Fail("splash.paint", ex);
                    }
                    EndPaint(h, ref ps);
                    return IntPtr.Zero;
                }
                if (msg == WM_CLOSE) { DestroyWindow(h); return IntPtr.Zero; }
                if (msg == WM_DESTROY) { _hwnd = IntPtr.Zero; PostQuitMessage(0); return IntPtr.Zero; }
            }
            catch (Exception ex)
            {
                // 窗口过程永远不能把异常抛回 user32：落回 DefWindowProc 即可。
                // 但消息被静默丢弃会表现为"卡片不刷新/关不掉"，必须记账。
                Diag.Fail("splash.wndproc", ex);
            }
            return DefWindowProc(h, msg, w, l);
        }

        private static void Paint(IntPtr hdc)
        {
            uint dpi = GetDpiForSystem();
            if (dpi < 96) dpi = 96;
            float k = dpi / 96f;

            using (Graphics g = Graphics.FromHdc(hdc))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                int w = _w, h = _h;

                // 1) 深空蓝曜玻璃机身
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new Rectangle(0, 0, Math.Max(1, w), Math.Max(1, h)),
                    Color.FromArgb(0x16, 0x23, 0x3A),
                    Color.FromArgb(0x07, 0x0C, 0x17), 90f))
                    g.FillRectangle(bg, 0, 0, w, h);

                // 2) 左上冰蓝 + 右下电钴双色极光场
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(-w * 0.18f, -h * 0.55f, w * 0.88f, h * 1.65f);
                    using (PathGradientBrush pgb = new PathGradientBrush(gp))
                    {
                        pgb.CenterColor = Color.FromArgb(0x42, 0x5F, 0xD9, 0xFF);
                        pgb.SurroundColors = new Color[] { Color.FromArgb(0, 0x5F, 0xD9, 0xFF) };
                        g.FillRectangle(pgb, 0, 0, w, h);
                    }
                }
                using (GraphicsPath gp2 = new GraphicsPath())
                {
                    gp2.AddEllipse(w * 0.42f, h * 0.12f, w * 0.82f, h * 1.50f);
                    using (PathGradientBrush pgb2 = new PathGradientBrush(gp2))
                    {
                        pgb2.CenterColor = Color.FromArgb(0x34, 0x3B, 0x82, 0xF6);
                        pgb2.SurroundColors = new Color[] { Color.FromArgb(0, 0x3B, 0x82, 0xF6) };
                        g.FillRectangle(pgb2, 0, 0, w, h);
                    }
                }

                // 3) 精密 16DIP 蓝图点阵底纹
                int step = Math.Max(8, (int)Math.Round(16 * k));
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)))
                {
                    for (int y = step / 2; y < h; y += step)
                        for (int x = step / 2; x < w; x += step)
                            g.FillRectangle(dot, x, y, 1, 1);
                }

                // 4) 顶部 1px 机加折射高光棱 + 外缘发丝边
                using (Pen border = new Pen(Color.FromArgb(0x36, 0x5F, 0xD9, 0xFF), Math.Max(1f, k)))
                    g.DrawRectangle(border, 0.5f, 0.5f, w - 1f, h - 1f);
                using (LinearGradientBrush bev = new LinearGradientBrush(
                    new PointF(0, 0), new PointF(w, 0),
                    Color.FromArgb(0, 0x5F, 0xD9, 0xFF), Color.FromArgb(0, 0x5F, 0xD9, 0xFF)))
                {
                    ColorBlend cb = new ColorBlend(3);
                    cb.Positions = new float[] { 0f, 0.35f, 1f };
                    cb.Colors = new Color[]
                    {
                        Color.FromArgb(0, 0x5F, 0xD9, 0xFF),
                        Color.FromArgb(0x88, 0x8F, 0xE6, 0xFF),
                        Color.FromArgb(0, 0x5F, 0xD9, 0xFF)
                    };
                    bev.InterpolationColors = cb;
                    g.FillRectangle(bev, 10 * k, 1 * k, w - 20 * k, Math.Max(1f, k));
                }

                float pad = 26 * k;
                float box = 36 * k;
                float bx = pad, by = pad - 2 * k;

                // 5) 阳极氧化倒角品牌徽标框 + 双层发光三角标
                using (GraphicsPath boxPath = RoundRect(bx, by, box, box, 8 * k))
                {
                    using (SolidBrush bf = new SolidBrush(Color.FromArgb(0x1E, 0x5F, 0xD9, 0xFF)))
                        g.FillPath(bf, boxPath);
                    using (Pen bp = new Pen(Color.FromArgb(0x66, 0x5F, 0xD9, 0xFF), Math.Max(1f, k)))
                        g.DrawPath(bp, boxPath);
                }

                float t = 20 * k;
                float mx = bx + (box - t) / 2f;
                float my = by + (box - t) / 2f;
                float cx = mx + t / 2f;
                float top = my + t * 0.06f;
                float bot = my + t * 0.94f;
                PointF a = new PointF(cx, top);
                PointF b = new PointF(mx + t * 0.95f, bot);
                PointF c = new PointF(mx + t * 0.05f, bot);
                using (Pen glowInk = new Pen(Color.FromArgb(0x38, 0x5F, 0xD9, 0xFF), 4.2f * k))
                {
                    glowInk.LineJoin = LineJoin.Round;
                    g.DrawLines(glowInk, new PointF[] { a, b, c, a });
                }
                using (Pen ink = new Pen(Color.FromArgb(0x5F, 0xD9, 0xFF), 1.8f * k))
                {
                    ink.LineJoin = LineJoin.Round;
                    g.DrawLines(ink, new PointF[] { a, b, c, a });
                }
                using (Pen inner = new Pen(Color.FromArgb(0xF4, 0xF8, 0xFD), 1.4f * k))
                {
                    inner.LineJoin = LineJoin.Round;
                    PointF d = new PointF(cx, my + t * 0.50f);
                    PointF e = new PointF(cx - t * 0.21f, bot - t * 0.08f);
                    PointF f = new PointF(cx + t * 0.21f, bot - t * 0.08f);
                    g.DrawLines(inner, new PointF[] { e, d, f });
                }

                float tx = bx + box + 14 * k;
                using (Font fb = FontOf("Bahnschrift", 13.5f * k, FontStyle.Bold))
                using (Font fm = FontOf("Cascadia Mono", 8.5f * k, FontStyle.Regular))
                using (SolidBrush hi = new SolidBrush(Color.FromArgb(0xF7, 0xFA, 0xFF)))
                using (SolidBrush sub = new SolidBrush(Color.FromArgb(0x8A, 0x9D, 0xB8)))
                using (SolidBrush ac = new SolidBrush(Color.FromArgb(0x5F, 0xD9, 0xFF)))
                {
                    g.DrawString("G A M E O P T I M I Z E R", fb, hi, tx, pad - 1 * k);
                    g.DrawString("VANGUARD CONSOLE", fm, sub, tx + 1 * k, pad + 21 * k);
                    g.DrawString("REV " + Brand.Rev, fm, ac, w - pad - 48 * k, h - pad - 20 * k);
                }

                // 6) 底部仪器刻度条与光学高光端帽
                float ly = h - pad - 4 * k;
                float lw = w - pad * 2;
                float lh = Math.Max(1.5f, 2 * k);
                using (SolidBrush track = new SolidBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)))
                    g.FillRectangle(track, pad, ly, lw, lh);
                using (LinearGradientBrush hot = new LinearGradientBrush(
                    new PointF(pad, ly), new PointF(pad + 136 * k, ly),
                    Color.FromArgb(0xFF, 0x5F, 0xD9, 0xFF), Color.FromArgb(0x00, 0x5F, 0xD9, 0xFF)))
                    g.FillRectangle(hot, pad, ly, 136 * k, lh);
            }
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = r * 2f;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Font FontOf(string family, float px, FontStyle style)
        {
            try
            {
                Font f = new Font(family, px, style, GraphicsUnit.Pixel);
                if (f != null) return f;
            }
            catch { /* 可忽略：指定字体不存在，下一行用通用无衬线兜底，启动卡不能因此不画字 */ }
            return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel);
        }
    }
}
