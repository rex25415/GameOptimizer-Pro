// ============================================================================
//  TrayHost.Icons.cs — 三态托盘动态图标预生成与 HICON 缓存管理
//  启动/安装时预生成 3 个高质量 32bpp DIB HICON：
//    Idle  = #38BDF8（冰蓝待命）
//    Guard = #10B981（翠绿守护）
//    Alert = #F59E0B（琥珀告警）
//  退出与 Remove 时通过 DestroyIcon 干净销毁，零 GDI/HICON 句柄泄漏。
// ============================================================================
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Go.Tray
{
    public sealed partial class TrayHost
    {
        public const uint ColorIdle  = 0xFF38BDF8;
        public const uint ColorGuard = 0xFF10B981;
        public const uint ColorAlert = 0xFFF59E0B;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public int fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bpp, byte[] bits);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

        private readonly IntPtr[] _stateIcons = new IntPtr[3];
        private bool _stateIconsReady;

        /// <summary>返回三态对应的标准信号色（供图标与托盘菜单光点共用）。</summary>
        public static uint ColorForState(TrayVisualState state)
        {
            switch (state)
            {
                case TrayVisualState.Guard: return ColorGuard;
                case TrayVisualState.Alert: return ColorAlert;
                default: return ColorIdle;
            }
        }

        private static string LabelForState(TrayVisualState state)
        {
            switch (state)
            {
                case TrayVisualState.Guard: return "守护中";
                case TrayVisualState.Alert: return "需关注";
                default: return "守护待命";
            }
        }

        private void EnsureStateIcons()
        {
            if (_stateIconsReady) return;
            try
            {
                int cx = GetSystemMetrics(SM_CXSMICON);
                int size = cx > 0 ? Math.Max(32, cx * 2) : 32;
                if (size > 64) size = 64;

                _stateIcons[(int)TrayVisualState.Idle] = RenderStateHIcon(TrayVisualState.Idle, size);
                _stateIcons[(int)TrayVisualState.Guard] = RenderStateHIcon(TrayVisualState.Guard, size);
                _stateIcons[(int)TrayVisualState.Alert] = RenderStateHIcon(TrayVisualState.Alert, size);

                _stateIconsReady = _stateIcons[0] != IntPtr.Zero
                                && _stateIcons[1] != IntPtr.Zero
                                && _stateIcons[2] != IntPtr.Zero;
                if (!_stateIconsReady)
                {
                    Diag.Hit("tray.stateicons.partial");
                    ReleaseStateIcons();
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("tray.stateicons.build", ex);
                TrayTrace.Write("state icon prebuild failed: " + ex.Message);
                ReleaseStateIcons();
            }
        }

        private IntPtr IconForState(TrayVisualState state)
        {
            int idx = (int)state;
            if (_stateIconsReady && idx >= 0 && idx < _stateIcons.Length && _stateIcons[idx] != IntPtr.Zero)
                return _stateIcons[idx];
            if (_fallbackIcon == IntPtr.Zero)
                _fallbackIcon = LoadIcon(out _fallbackOwned);
            return _fallbackIcon;
        }

        private void ReleaseStateIcons()
        {
            for (int i = 0; i < _stateIcons.Length; i++)
            {
                IntPtr h = _stateIcons[i];
                _stateIcons[i] = IntPtr.Zero;
                if (h != IntPtr.Zero)
                {
                    try { DestroyIcon(h); }
                    catch (Exception ex) { Diag.Hit("tray.stateicon.destroy"); TrayTrace.Write("DestroyIcon state: " + ex.Message); }
                }
            }
            _stateIconsReady = false;
        }

        /// <summary>释放预生成的三态 HICON 缓存及兜底加载图标。系统共享图标不归我们所有，不能销毁。</summary>
        private void ReleaseIcon()
        {
            ReleaseStateIcons();
            if (_fallbackIcon != IntPtr.Zero)
            {
                if (_fallbackOwned)
                {
                    try { DestroyIcon(_fallbackIcon); }
                    catch (Exception ex) { /* 降级：句柄已失效时 DestroyIcon 会失败，最多泄漏一张图标 */ Diag.Hit("tray.icon.destroy"); TrayTrace.Write("DestroyIcon: " + ex.Message); }
                }
                _fallbackIcon = IntPtr.Zero;
                _fallbackOwned = false;
            }
            _icon = IntPtr.Zero;
        }

        /// <summary>切换托盘三态视觉图标（无后缀重载，兼容无默认参数调用）。</summary>
        public void SetVisualState(TrayVisualState state)
        {
            SetVisualState(state, null);
        }

        /// <summary>
        /// 切换托盘三态视觉图标（Idle #38BDF8 / Guard #10B981 / Alert #F59E0B），
        /// 可选更新托盘悬停提示后缀；未安装或尚未建立图标时仅记录状态待 Install 生效。
        /// </summary>
        public void SetVisualState(TrayVisualState state, string tipSuffix)
        {
            bool stateChanged = (_visualState != state);
            _visualState = state;

            string newTip = null;
            if (tipSuffix != null)
            {
                string root = string.IsNullOrEmpty(_baseTip) ? "GameOptimizer" : _baseTip;
                newTip = tipSuffix.Length > 0 ? (root + " · " + tipSuffix) : (root + " · " + LabelForState(state));
            }
            else if (string.IsNullOrEmpty(_tip))
            {
                newTip = "GameOptimizer · " + LabelForState(state);
            }

            if (!_installed)
            {
                if (newTip != null) _tip = newTip;
                return;
            }

            EnsureStateIcons();
            IntPtr h = IconForState(state);
            bool iconChanged = (h != IntPtr.Zero && h != _icon);
            if (h != IntPtr.Zero) _icon = h;

            bool tipChanged = false;
            int tipHash = _tipHash;
            if (newTip != null)
            {
                tipHash = newTip.GetHashCode();
                tipChanged = (tipHash != _tipHash);
                _tip = newTip;
            }

            if (!stateChanged && !iconChanged && !tipChanged) return;

            uint flags = 0;
            if (iconChanged || stateChanged)
            {
                _data.hIcon = _icon;
                flags |= NIF_ICON;
            }
            if (tipChanged)
            {
                _data.szTip = Clip(_tip, 126);
                flags |= NIF_TIP;
            }
            if (flags == 0) return;

            _data.uFlags = flags;
            if (Shell_NotifyIcon(NIM_MODIFY, ref _data))
            {
                if (tipChanged) _tipHash = tipHash;
                TrayTrace.Write("visual-state=" + state + (tipChanged ? (" tip=" + _tip) : ""));
            }
            else
            {
                TrayTrace.Write("visual-state modify failed: " + state);
            }
            _data.uFlags = NIF_TIP;
        }

        private static IntPtr LoadIcon(out bool owned)
        {
            owned = false;
            try
            {
                int cx = GetSystemMetrics(SM_CXSMICON);
                int cy = GetSystemMetrics(SM_CYSMICON);
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameOptimizer.ico");
                if (File.Exists(ico))
                {
                    IntPtr h = LoadImage(IntPtr.Zero, ico, IMAGE_ICON, cx * 2, cy * 2, LR_LOADFROMFILE);
                    if (h != IntPtr.Zero) { owned = true; return h; }
                    h = LoadImage(IntPtr.Zero, ico, IMAGE_ICON, cx, cy, LR_LOADFROMFILE);
                    if (h != IntPtr.Zero) { owned = true; return h; }
                }
                IntPtr large, small;
                int n = ExtractIconEx(System.Reflection.Assembly.GetEntryAssembly().Location, 0, out large, out small, 1);
                if (n > 0 && small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero) { try { DestroyIcon(large); } catch { /* 可忽略：释放失败最多泄漏一张图标，不能因此丢掉整个托盘 */ } }
                    owned = true;
                    return small;
                }
                if (large != IntPtr.Zero)
                {
                    if (small != IntPtr.Zero) { try { DestroyIcon(small); } catch { /* 可忽略：同上，句柄无效时 DestroyIcon 会失败，但启动流程不该被它打断 */ } }
                    owned = true;
                    return large;
                }
                if (small != IntPtr.Zero) { try { DestroyIcon(small); } catch { /* 可忽略：同上，句柄无效时 DestroyIcon 会失败，但启动流程不该被它打断 */ } }
            }
            catch (Exception ex)
            {
                Diag.Fail("tray.icon.load", ex);
                TrayTrace.Write("icon load failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try { owned = false; return System.Drawing.SystemIcons.Application.Handle; }
            catch (Exception ex)
            {
                Diag.Fail("tray.icon.fallback", ex);
                TrayTrace.Write("system icon fallback failed: " + ex.Message);
                return IntPtr.Zero;
            }
        }

        private static IntPtr RenderStateHIcon(TrayVisualState state, int size)
        {
            uint colArgb = ColorForState(state);
            double s = size;
            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                double radius = s * 0.22;

                // 深色精密底板 + 顶部微高光
                LinearGradientBrush plate = new LinearGradientBrush(
                    ArgbColor(Brand.PlateTop), ArgbColor(Brand.PlateBottom), 90);
                plate.Freeze();
                dc.DrawRoundedRectangle(plate, null, new Rect(0.5, 0.5, s - 1, s - 1), radius, radius);

                LinearGradientBrush lip = new LinearGradientBrush(
                    Color.FromArgb(30, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
                lip.Freeze();
                dc.DrawRoundedRectangle(lip, null, new Rect(1, 1, s - 2, s * 0.42), radius, radius);

                // 外圈态别强调边：守护/告警提亮以便任务栏小尺寸快速辨识
                byte rimAlpha = state == TrayVisualState.Idle ? (byte)0x70 : (byte)0xBA;
                Pen rimPen = new Pen(FrozenBrush(ArgbColor(ArgbAlpha(colArgb, rimAlpha))), Math.Max(1.2, s * 0.045));
                rimPen.Freeze();
                dc.DrawRoundedRectangle(null, rimPen, new Rect(0.75, 0.75, s - 1.5, s - 1.5), radius, radius);

                // 品牌三角母版（与 Brand.MarkPath 完全同源）
                double pad = s * 0.16;
                double k = (s - pad * 2) / 24.0;
                TransformGroup tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(k, k));
                tg.Children.Add(new TranslateTransform(pad, pad));

                Geometry gOuter = Geometry.Parse(Brand.MarkPath).Clone();
                gOuter.Transform = tg;
                gOuter.Freeze();

                Pen pOuter = new Pen(FrozenBrush(ArgbColor(colArgb)), Brand.MarkStroke * k * 1.15);
                pOuter.LineJoin = PenLineJoin.Miter;
                pOuter.StartLineCap = PenLineCap.Round;
                pOuter.EndLineCap = PenLineCap.Round;
                pOuter.Freeze();
                dc.DrawGeometry(null, pOuter, gOuter);

                Geometry gInner = Geometry.Parse(Brand.MarkInnerPath).Clone();
                gInner.Transform = tg;
                gInner.Freeze();

                uint innerCol = state == TrayVisualState.Idle ? 0xCCF4F8FD : ArgbAlpha(colArgb, 0xCC);
                Pen pInner = new Pen(FrozenBrush(ArgbColor(innerCol)), Brand.MarkInnerStroke * k * 1.1);
                pInner.LineJoin = PenLineJoin.Round;
                pInner.StartLineCap = PenLineCap.Round;
                pInner.EndLineCap = PenLineCap.Round;
                pInner.Freeze();
                dc.DrawGeometry(null, pInner, gInner);

                // 右下角高对比状态信标点（暗色隔离环确保浅色/深色任务栏均清晰）
                double dotR = Math.Max(3.6, s * 0.165);
                double haloR = dotR + Math.Max(1.6, s * 0.055);
                Point pt = new Point(s - dotR - s * 0.07, s - dotR - s * 0.07);
                dc.DrawEllipse(FrozenBrush(Color.FromArgb(0xFF, 0x05, 0x07, 0x0B)), null, pt, haloR, haloR);
                dc.DrawEllipse(FrozenBrush(ArgbColor(colArgb)), null, pt, dotR, dotR);
            }

            RenderTargetBitmap rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            byte[] pixels = new byte[size * size * 4];
            rtb.CopyPixels(pixels, size * 4, 0);
            return CreateHIconFromPbgra32(pixels, size, size);
        }

        private static IntPtr CreateHIconFromPbgra32(byte[] pixels, int w, int h)
        {
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = w;
            bi.biHeight = -h; // 负高 = 自顶向下 DIB，行序与 CopyPixels 一致
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            bi.biCompression = 0; // BI_RGB

            IntPtr bits;
            IntPtr hColor = IntPtr.Zero;
            IntPtr hMask = IntPtr.Zero;
            try
            {
                hColor = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
                if (hColor == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
                Marshal.Copy(pixels, 0, bits, pixels.Length);

                int strideMask = ((w + 15) / 16) * 2;
                byte[] maskBits = new byte[strideMask * h];
                hMask = CreateBitmap(w, h, 1, 1, maskBits);
                if (hMask == IntPtr.Zero) return IntPtr.Zero;

                ICONINFO ii = new ICONINFO();
                ii.fIcon = 1;
                ii.hbmColor = hColor;
                ii.hbmMask = hMask;
                return CreateIconIndirect(ref ii);
            }
            finally
            {
                if (hColor != IntPtr.Zero) DeleteObject(hColor);
                if (hMask != IntPtr.Zero) DeleteObject(hMask);
            }
        }

        private static uint ArgbAlpha(uint argb, byte a)
        {
            return ((uint)a << 24) | (argb & 0x00FFFFFFu);
        }

        private static Color ArgbColor(uint argb)
        {
            return Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
        }

        private static SolidColorBrush FrozenBrush(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
