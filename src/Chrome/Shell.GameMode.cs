// ============================================================================
//  Shell.GameMode.cs — 一键游戏模式 + 前台全屏检测（Shell 分片）
//  游戏模式 = 组合既有账本动作（游戏模式 / 关 DVR / 关空闲维护）+ 0.5ms 定时租约。
//  全部可逆：退出时按账本还原 + 释放租约。不写任何绕过账本的系统项。
//  全屏检测只提示、不自动改系统：把决定权留给用户。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Go
{
    public partial class Shell : Window
    {
        private bool _gameMode;
        private bool _gmPrompted;

        public bool GameModeOn { get { return _gameMode; } }

        private static readonly string[] GameModeIds = new string[] { "game-mode", "game-dvr", "maint-off" };

        /// <summary>切换一键游戏模式。</summary>
        public void ToggleGameMode() { SetGameMode(!_gameMode); }

        public void SetGameMode(bool on)
        {
            if (on == _gameMode) return;
            if (on)
            {
                List<ActionDef> sel = new List<ActionDef>();
                foreach (string id in GameModeIds)
                {
                    ActionDef a = _core.Engine.Find(id);
                    if (a != null && _core.Engine.Probe(a) != AState.Applied) sel.Add(a);
                }
                foreach (ActionDef a in sel)
                {
                    if (!_core.Engine.CanWrite(a))
                    {
                        Toast("游戏模式包含需管理员项 · 点左下角状态行提权重启", false);
                        return;
                    }
                }
                ApplyReport rep = _core.Engine.ApplyMany(sel);
                _core.Timer.Request(TimerOwner.GameMode, 0.5);
                _gameMode = true;
                Ignite();
                Toast(rep.Ok
                    ? ("游戏模式已启用 · " + rep.Applied + " 项策略 + 0.5ms 定时")
                    : ("游戏模式部分失败 " + rep.Failed + " 项"), rep.Ok);
            }
            else
            {
                int reverted = 0;
                foreach (string id in GameModeIds)
                {
                    ApplyReport r = _core.Engine.RevertAction(id);
                    reverted += r.Applied;
                }
                _core.Timer.Release(TimerOwner.GameMode);
                _gameMode = false;
                Toast("游戏模式已退出 · 还原 " + reverted + " 项", true);
            }
            // 只删包装容器是不够的：页面自己的 _built 还在，重新挂上去的还是旧树。
            if (_cur != null) { _wraps.Remove(_cur.Key); _cur.Invalidate(); }
            RefreshChromeGameMode();
            SyncTrayState();
        }

        // ---- 点火仪式：一键游戏模式时的引擎轰鸣视觉爆发 ----------------------
        /// <summary>中心扩散的强调色环 + 淡出，纯合成层（Scale/Opacity），不触发布局。</summary>
        public void Ignite()
        {
            if (!Ui.Animate || Rs.ReduceMotion || _root == null) return;
            Border ring = new Border();
            ring.IsHitTestVisible = false;
            ring.HorizontalAlignment = HorizontalAlignment.Center;
            ring.VerticalAlignment = VerticalAlignment.Center;
            ring.Width = 120; ring.Height = 120;
            ring.CornerRadius = new CornerRadius(60);
            ring.BorderThickness = new Thickness(2);
            ring.BorderBrush = T.S(T.Accent);
            ring.Background = T.S(T.AccentA(0x10));
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            ScaleTransform st = new ScaleTransform(0.2, 0.2);
            ring.RenderTransform = st;
            ring.Opacity = 0.9;
            _root.Children.Add(ring);

            Duration dur = new Duration(TimeSpan.FromMilliseconds(650));
            CubicEase ease = new CubicEase();
            ease.EasingMode = EasingMode.EaseOut;
            Motion.Tween(0.2, 16, 650, delegate (double v) { st.ScaleX = v; st.ScaleY = v; });
            DoubleAnimation fade = new DoubleAnimation(0.9, 0, dur);
            fade.EasingFunction = ease;
            fade.Completed += delegate { try { _root.Children.Remove(ring); } catch { /* 元素已被移除：动画回调不能抛 */ } };
            ring.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // ---- 前台全屏检测 --------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

        /// <summary>窗口覆盖整块显示器（含无边框全屏）即视为全屏。只提示一次，不自动改系统。</summary>
        private void CheckFullscreen(IntPtr h, int pid)
        {
            if (_gameMode || _gmPrompted) return;
            try
            {
                if (Marshal.SizeOf(typeof(MONITORINFO)) != 40) return;
                RECT r;
                if (!GetWindowRect(h, out r)) return;
                IntPtr mon = MonitorFromWindow(h, 2);
                if (mon == IntPtr.Zero) return;
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfoW(mon, ref mi)) return;
                bool covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
                           && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
                if (!covers) return;
                _gmPrompted = true;
                Toast("检测到全屏应用 · Ctrl+K 搜索「游戏模式」一键启用", true);
            }
            catch (Exception ex) { Diag.Fail("gamemode.prompt", ex); }
        }
    }
}