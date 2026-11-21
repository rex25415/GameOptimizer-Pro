// ============================================================================
//  TrayMenu.Layout.cs — 托盘菜单 DPI 缩放 / 多屏防贴边布局 / 离屏预览（partial 分片）
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Go.Tray
{
    public sealed partial class TrayMenu
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECTF { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECTF rcMonitor;
            public RECTF rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegGetValue(IntPtr hkey, string sub, string value, int flags, IntPtr type, IntPtr data, ref int size);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>在给定锚点（屏幕物理像素）弹出：先量内容、定 DPI、算位置，最后 Show。</summary>
        public void ShowAtAnchor(int anchorX, int anchorY)
        {
            _anchorX = anchorX;
            _anchorY = anchorY;

            int px, py;
            if (anchorX != 0 || anchorY != 0) { px = anchorX; py = anchorY; }
            else
            {
                POINT c;
                if (!GetCursorPos(out c)) { c.X = 0; c.Y = 0; }
                px = c.X; py = c.Y;
            }

            _scale = ScaleForPoint(px, py);
            Layout(px, py);

            _openedMs = ClickClock.NowMs;
            _frame.Opacity = 0;

            Show();
            CorrectDpi(px, py);
            Activate();
            try { _frame.Focus(); } catch { /* 降级：焦点拿不到只影响键盘导航，鼠标仍可用；菜单已经显示出来了 */ Diag.Hit("traymenu.focus"); }
            Motion.Tween(0, 1, 110, _fadeApply);
        }

        private static double ScaleForPoint(int px, int py)
        {
            try
            {
                POINT pt; pt.X = px; pt.Y = py;
                IntPtr mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero)
                {
                    uint dx, dy;
                    if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return dx / 96.0;
                }
            }
            catch { /* 降级：shcore 不可用（Win7）/ 显示器消失 → 落到下面的窗口 DPI 与主屏回退 */ Diag.Hit("traymenu.dpi.monitor"); }

            Window host = Application.Current == null ? null : Application.Current.MainWindow;
            if (host != null)
            {
                try
                {
                    IntPtr hh = new System.Windows.Interop.WindowInteropHelper(host).Handle;
                    uint dpi = hh == IntPtr.Zero ? 0 : GetDpiForWindow(hh);
                    if (dpi > 0) return dpi / 96.0;
                }
                catch { /* 降级：窗口句柄还没建好 → 继续用 VisualTreeHelper 的缩放 */ Diag.Hit("traymenu.dpi.window"); }
                try
                {
                    double s = VisualTreeHelper.GetDpi(host).DpiScaleX;
                    if (s > 0.5 && s < 4.0) return s;
                }
                catch { /* 降级：取不到缩放 → 最后回退 1.0（下面的 return），菜单位置会偏但不会崩 */ Diag.Hit("traymenu.dpi.fallback"); }
            }
            return 1.0;
        }

        /// <summary>按目标屏幕工作区算位置（DIP）。调用前 _scale 必须是目标屏的缩放。</summary>
        private void Layout(int px, int py)
        {
            double scale = _scale <= 0 ? 1.0 : _scale;
            double cx = px / scale;
            double cy = py / scale;

            Rect wa = WorkArea(cx, cy, scale);

            FrameworkElement fe = Content as FrameworkElement;
            Size d = MeasureContent(fe);

            // 屏幕太矮：先收掉副标题，再收页脚，绝不把菜单顶出工作区
            if (d.Height > wa.Height * 0.74 && !_compact)
            {
                _compact = true;
                for (int i = 0; i < _hints.Count; i++) _hints[i].Visibility = Visibility.Collapsed;
                d = MeasureContent(fe);
            }
            if (d.Height > wa.Height * 0.94)
            {
                _footer.Visibility = Visibility.Collapsed;
                _footerRule.Visibility = Visibility.Collapsed;
                d = MeasureContent(fe);
            }

            double w = d.Width > 10 ? d.Width : WidthDip + Gutter * 2;
            double h = d.Height > 10 ? d.Height : 400;

            // 默认：锚点左上方（任务栏在底部时的标准姿势）
            double left = cx - w + 10;
            double top = cy - h - 10;
            if (top < wa.Top + 4) top = cy + 12;
            if (left < wa.Left + 4) left = wa.Left + 4;
            if (left + w > wa.Right - 4) left = wa.Right - w - 4;
            if (top + h > wa.Bottom - 4) top = wa.Bottom - h - 4;
            if (top < wa.Top + 4) top = wa.Top + 4;

            // 永不让光标落在菜单上（否则第一次点击就可能误触某一项）
            if (cx >= left && cx <= left + w && cy >= top && cy <= top + h)
            {
                if (cy - top > h * 0.5) top = Math.Max(wa.Top + 4, cy - h - 12);
                else top = Math.Min(wa.Bottom - h - 4, cy + 12);
            }

            Left = left;
            Top = top;
        }

        private Size MeasureContent(FrameworkElement fe)
        {
            if (fe == null) return new Size(WidthDip + Gutter * 2, 400);
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return fe.DesiredSize;
        }

        private static Rect WorkArea(double cxDip, double cyDip, double scale)
        {
            try
            {
                POINT pt;
                pt.X = (int)(cxDip * scale);
                pt.Y = (int)(cyDip * scale);
                IntPtr mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero)
                {
                    MONITORINFO mi = new MONITORINFO();
                    mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                    if (GetMonitorInfo(mon, ref mi) && mi.rcWork.R > mi.rcWork.L)
                        return new Rect(mi.rcWork.L / scale, mi.rcWork.T / scale,
                                        (mi.rcWork.R - mi.rcWork.L) / scale, (mi.rcWork.B - mi.rcWork.T) / scale);
                }
            }
            catch (Exception ex)
            {
                // 多屏 API 失败 → 回退 SystemParameters.WorkArea（单屏工作区），菜单仍可见。
                // 但多屏机器上菜单位置会明显偏，必须能查。
                Diag.Fail("traymenu.workarea", ex);
            }
            return SystemParameters.WorkArea;
        }

        /// <summary>落到目标屏后真实 DPI 可能和预判不同（混合 DPI 多屏）：差得明显就重算位置——此时还没淡入，看不到跳动。</summary>
        private void CorrectDpi(int px, int py)
        {
            try
            {
                double real = VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (real <= 0.5 || real > 4.0) return;
                if (Math.Abs(real - _scale) < 0.02) return;
                _scale = real;
                Layout(px, py);
            }
            catch (Exception ex)
            {
                // 重排失败时保留首次布局的位置；菜单已经 Show 出来，不能在这里抛。
                Diag.Fail("traymenu.correctdpi", ex);
            }
        }

        // ---- 主题 ----------------------------------------------------------
        private static bool IsLightTaskbar()
        {
            try
            {
                int v = ReadDword(unchecked((int)0x80000001), @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme");
                return v == 1;
            }
            catch { /* 降级：读不到主题偏好 = 按深色任务栏处理（默认主题），只影响卡片描边深浅 */ Diag.Hit("traymenu.lighttaskbar"); return false; }
        }

        private static int ReadDword(int hkey, string sub, string name)
        {
            IntPtr buf = Marshal.AllocHGlobal(8);
            try
            {
                int size = 4;
                IntPtr h = new IntPtr(hkey);
                // 0x10 = RRF_RT_REG_DWORD。传 0x02（REG_SZ）去读 DWORD 必然失败，
                // 于是 IsLightTaskbar 恒为 false，浅色任务栏下菜单描边永远是深色。
                int st = RegGetValue(h, sub, name, 0x00000010, IntPtr.Zero, buf, ref size);
                if (st != 0 || size < 4) return 0;
                return Marshal.ReadInt32(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---- 离屏预览（截图回归用）-----------------------------------------
        public static void RenderPreview(string pngPath)
        {
            TrayMenu m = new TrayMenu();
            m.SetStatus(TrayVisualState.Guard, "守护中 · Counter-Strike 2");
            m.SetPosture("CS2 · CCD0", "0.50 ms", "卓越性能");
            m.AddHeader("控制台");
            m.AddItem("打开主界面", "Ctrl+Shift+G", null, false);
            m.AddItem("一键极致模式", "Counter-Strike 2 · CCD0 + 独显 + 0.5ms", null, false);
            m.AddRule();
            m.AddHeader("系统");
            m.AddItem("恢复默认", "按账本逆序复原全部写入", null, true);
            m.AddCheck("游戏自动守护", "启动即挂载 · 退出按账本复原", true, delegate { });
            m.AddCheck("托盘常驻", "关闭窗口后继续在后台守护", true, delegate { });
            m.AddCheck("开机自启", "登录后在托盘静默待命", false, delegate { });
            m.AddRule();
            m.AddHeader("档案切换");
            m.AddItem("Counter-Strike 2", "CCD0 独显 0.5ms", null, false);
            m.AddRule();
            m.AddItem("退出 GameOptimizer", "同时撤销本进程的全部系统写入", null, true);

            m.Left = 200;
            m.Top = 200;
            m.Show();
            m.UpdateLayout();
            m.Dispatcher.Invoke(new Action(delegate { }), System.Windows.Threading.DispatcherPriority.Render);

            try
            {
                int w = (int)Math.Ceiling(m.ActualWidth);
                int h = (int)Math.Ceiling(m.ActualHeight);
                RenderTargetBitmap rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
                rtb.Render((Visual)m.Content);
                PngBitmapEncoder enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (System.IO.FileStream fs = System.IO.File.Create(pngPath)) enc.Save(fs);
                Console.WriteLine("shot " + pngPath + "  " + w + "x" + h);
            }
            catch (Exception ex) { Diag.Fail("traymenu.render", ex); Console.WriteLine("tray menu render failed: " + ex.Message); }
            finally { m.Close(); }
        }
    }
}
