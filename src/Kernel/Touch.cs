// ============================================================================
//  Touch.cs — 触屏 / 掌机适配探测
//  掌机（ROG Ally / Legion Go / Steam Deck 的 Windows 模式）没有鼠标悬停，
//  手指的落点也比指针粗得多。这里统一决定"热区要不要放大一号"。
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace Go
{
    public static class Touch
    {
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private const int SM_MAXIMUMTOUCHES = 95;      // Win8+
        private const int SM_DIGITIZER = 94;

        private const int NID_INTEGRATED_TOUCH = 0x01;
        private const int NID_EXTERNAL_TOUCH = 0x02;

        private static bool _probed;
        private static bool _touch;
        private static int _maxTouch;

        /// <summary>设备是否带触摸输入（含掌机集成触屏）。</summary>
        public static bool IsTouchPrimary
        {
            get
            {
                if (!_probed) Probe();
                return _touch;
            }
        }

        public static int MaxContacts
        {
            get
            {
                if (!_probed) Probe();
                return _maxTouch;
            }
        }

        private static void Probe()
        {
            _probed = true;
            try
            {
                int d = GetSystemMetrics(SM_DIGITIZER);
                _maxTouch = GetSystemMetrics(SM_MAXIMUMTOUCHES);
                _touch = (d & (NID_INTEGRATED_TOUCH | NID_EXTERNAL_TOUCH)) != 0 && _maxTouch > 0;
            }
            catch (Exception ex)
            {
                // 探测失败按鼠标/键盘处理（保守降级：不放大小热区也不会误触）。
                // 但掌机上热区会因此偏小——计数，让"为什么这么难点"有据可查。
                Diag.Fail("touch.probe", ex);
                _touch = false;
            }
        }

        /// <summary>热区/行高放大系数：触屏 1.25，鼠标 1.0。</summary>
        public static double HitScale
        {
            get { return IsTouchPrimary ? 1.25 : 1.0; }
        }

        /// <summary>把基准高度按设备能力放大（向下取整到 2px，避免亚像素抖动）。</summary>
        public static double Scale(double v)
        {
            if (!IsTouchPrimary) return v;
            return Math.Floor(v * 1.25 / 2.0) * 2.0;
        }

        public static string Describe()
        {
            if (!IsTouchPrimary) return "鼠标 / 键盘";
            return "触屏 · " + MaxContacts + " 点";
        }
    }
}
