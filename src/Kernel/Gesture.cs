// ============================================================================
//  Gesture.cs — 指针语义内核（一处定义，全局一致）
//
//  为什么要有这一层：
//  按钮"在按下时触发 / 在移出后仍触发 / 右键也触发 / 拖拽后误触发"，
//  是桌面应用最容易被用户感知到的业余感来源。WPF 默认不会替你做对，
//  每个控件各写一套 MouseLeftButtonUp 必然发散。
//
//  这里给出唯一正确的按钮状态机：
//    · 左键按下 = 武装 + 捕获鼠标 + 视觉压下
//    · 左键抬起 = 仅在"仍然在控件内"时触发；否则取消
//    · 中途松手 / 丢失捕获 / 移出后松手 = 一律取消
//    · 右键 / 中键 = 永不触发动作（只允许冒泡成上下文菜单）
//    · 触发时刻 = 抬起，不是按下（Windows 标准）
//
//  ClickGate 是纯状态机（不依赖 WPF），可以被自检逐条断言。
// ============================================================================
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Go
{
    /// <summary>进程内单调毫秒时钟（不随系统时间跳变）。</summary>
    public static class ClickClock
    {
        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        public static long NowMs { get { return _sw.ElapsedMilliseconds; } }
    }

    /// <summary>
    /// 纯按钮状态机。所有输入都显式传入"现在"，因此可以离线驱动、逐帧断言。
    /// </summary>
    public struct ClickGate
    {
        private bool _armed;
        private long _pressedMs;

        public bool Armed { get { return _armed; } }

        public void Press(long nowMs)
        {
            _armed = true;
            _pressedMs = nowMs;
        }

        /// <summary>抬起。返回 true 表示这次应当触发一次点击。</summary>
        public bool Release(bool inside, long nowMs)
        {
            if (!_armed) return false;
            _armed = false;
            if (!inside) return false;
            if (nowMs < _pressedMs) return false;   // 时钟回退：宁可不出手
            return true;
        }

        public void Cancel() { _armed = false; }
    }

    /// <summary>
    /// 把一个 UIElement 变成一个"做法正确"的按钮。
    /// 视觉反馈通过 visual(pressed) 回调交给调用方，内核不碰画笔。
    /// </summary>
    public sealed class Pressable
    {
        /// <summary>累计触发次数（自检用）。</summary>
        public static int FireCount;

        private readonly UIElement _el;
        private readonly Action _click;
        private readonly Action<bool> _visual;
        private readonly bool _swallow;
        private ClickGate _gate;

        /// <summary>最近一次按下的时刻。用于"菜单刚弹出时不接受陈旧点击"。</summary>
        public long LastPressMs { get; private set; }

        /// <summary>一次性吞掉下一次点击（防误触）。触发后自动复位。</summary>
        public bool Suppress;

        /// <summary>置假可临时禁用（视觉由调用方负责）。</summary>
        public bool Enabled = true;

        public bool Pressed { get { return _gate.Armed; } }

        private Pressable(UIElement el, Action click, Action<bool> visual, bool swallow)
        {
            _el = el;
            _click = click;
            _visual = visual;
            _swallow = swallow;
        }

        public static Pressable On(UIElement el, Action click)
        {
            return On(el, click, null, false);
        }

        public static Pressable On(UIElement el, Action click, Action<bool> visual)
        {
            return On(el, click, visual, false);
        }

        /// <param name="swallow">true 时把左键消息标记为已处理，避免父容器再动一次。</param>
        public static Pressable On(UIElement el, Action click, Action<bool> visual, bool swallow)
        {
            Pressable p = new Pressable(el, click, visual, swallow);
            p.Hook();
            return p;
        }

        private void Hook()
        {
            _el.MouseLeftButtonDown += OnDown;
            _el.MouseLeftButtonUp += OnUp;
            _el.MouseMove += OnMove;
            _el.MouseLeave += OnLeave;
            _el.LostMouseCapture += OnLost;
            _el.MouseRightButtonUp += OnRightUp;

            // 键盘可达：Pressable 元素可聚焦，Enter/Space 触发，焦点环用强调色。
            // 仅在交互会话启用：离屏/自检（Ui.Animate=false）无 PresentationSource，
            // 焦点导航会构造空 inputSource 事件而崩溃。
            FrameworkElement fe = _el as FrameworkElement;
            if (fe != null && Ui.Animate)
            {
                fe.Focusable = true;
                fe.FocusVisualStyle = FocusVisual();
                fe.KeyDown += OnKey;
            }
        }

        private void OnKey(object s, KeyEventArgs e)
        {
            if (!Enabled) return;
            if (e.Key != Key.Enter && e.Key != Key.Space) return;
            e.Handled = true;
            if (Suppress) { Suppress = false; return; }
            FireCount++;
            if (_click != null)
            {
                try { _click(); }
                catch (Exception ex) { Go.Tray.TrayHost.LogTrayError(ex); }
            }
        }

        private static Style _focusVisual;
        private static Style FocusVisual()
        {
            if (_focusVisual != null) return _focusVisual;
            Style s = new Style(typeof(FrameworkElement));
            System.Windows.Controls.ControlTemplate ct = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Control));
            FrameworkElementFactory rect = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
            rect.SetValue(System.Windows.Shapes.Rectangle.MarginProperty, new Thickness(1));
            rect.SetValue(System.Windows.Shapes.Rectangle.StrokeProperty, T.S(T.AccentRing));
            rect.SetValue(System.Windows.Shapes.Rectangle.StrokeThicknessProperty, 1.0);
            rect.SetValue(System.Windows.Shapes.Rectangle.RadiusXProperty, 4.0);
            rect.SetValue(System.Windows.Shapes.Rectangle.RadiusYProperty, 4.0);
            rect.SetValue(System.Windows.Shapes.Rectangle.SnapsToDevicePixelsProperty, true);
            ct.VisualTree = rect;
            s.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, ct));
            s.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(-2)));
            _focusVisual = s;
            return s;
        }

        private void OnRightUp(object s, MouseButtonEventArgs e)
        {
            // 右键永不触发动作。吞掉它，避免外层容器把它当成"点了空白"。
            if (_swallow) e.Handled = true;
        }

        private void OnDown(object s, MouseButtonEventArgs e)
        {
            if (!Enabled) return;
            if (e.ChangedButton != MouseButton.Left) return;
            LastPressMs = ClickClock.NowMs;
            _gate.Press(LastPressMs);
            try { _el.CaptureMouse(); }
            catch { /* 降级：捕获失败仍按按下处理——抬起若落在控件内依旧算点击（Inside 判定）。计数以便看出捕获为何总失败 */ Diag.Hit("gesture.capture"); }
            SetVisual(true);
            if (_swallow) e.Handled = true;
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            if (!_gate.Armed) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                // 抬起事件丢了（窗口失焦等）：当场收摊，不要留下压下的死样子
                Cancel();
                return;
            }
            SetVisual(Inside());
        }

        private void OnLeave(object s, MouseEventArgs e)
        {
            if (!_gate.Armed) return;
            SetVisual(false);   // 保留捕获：拖回来仍然可以确认
        }

        private void OnLost(object s, MouseEventArgs e)
        {
            if (_gate.Armed) Cancel();
        }

        private void OnUp(object s, MouseButtonEventArgs e)
        {
            if (!_gate.Armed) return;
            bool inside = Inside();
            bool fire = _gate.Release(inside, ClickClock.NowMs);
            try { if (_el.IsMouseCaptured) _el.ReleaseMouseCapture(); } catch { /* 可忽略：元素已从树里移除时释放会抛；捕获状态随之消失，不影响点击判定 */ }
            SetVisual(false);
            if (_swallow) e.Handled = true;
            if (!fire) return;
            if (Suppress) { Suppress = false; return; }
            if (!Enabled) return;
            FireCount++;
            if (_click != null)
            {
                try { _click(); }
                catch (Exception ex) { Go.Tray.TrayHost.LogTrayError(ex); }
            }
        }

        private void Cancel()
        {
            _gate.Cancel();
            try { if (_el.IsMouseCaptured) _el.ReleaseMouseCapture(); } catch { /* 可忽略：取消路径上的释放失败不需要上报（状态机已经复位） */ }
            SetVisual(false);
        }

        private bool Inside()
        {
            try
            {
                Point p = Mouse.GetPosition(_el);
                Size sz = _el.RenderSize;
                return p.X >= 0 && p.Y >= 0 && p.X <= sz.Width && p.Y <= sz.Height;
            }
            catch (Exception ex)
            {
                // 取不到鼠标相对位置 → 视为指针在控件外（宁可不触发，也不误触）。
                // 这是"按钮点了没反应"的一种真实成因，必须进 Diag。
                Diag.Fail("gesture.inside", ex);
                return false;
            }
        }

        private void SetVisual(bool down)
        {
            if (_visual == null) return;
            try { _visual(down); }
            catch (Exception ex)
            {
                // 视觉回调由调用方提供：它失败不能反过来让点击状态机卡在按下态。
                // 但控件会因此"按下去弹不回来"，必须记账。
                Diag.Fail("gesture.visual", ex);
            }
        }
    }

    /// <summary>
    /// 悬停高亮：只动 Opacity（合成层），零布局、零重绘。
    /// </summary>
    public sealed class HoverFx
    {
        private readonly UIElement _layer;
        private readonly double _rest;
        private readonly double _lit;
        private readonly Action<double> _apply;
        private bool _on;

        public HoverFx(UIElement layer, double rest, double lit)
        {
            _layer = layer;
            _rest = rest;
            _lit = lit;
            _apply = Apply;
        }

        public void Set(bool on)
        {
            if (on == _on) return;
            _on = on;
            double from = _layer.Opacity;
            Motion.Tween(from, on ? _lit : _rest, on ? 110 : 150, _apply);
        }

        public void Snap(bool on)
        {
            _on = on;
            _layer.Opacity = on ? _lit : _rest;
        }

        private void Apply(double v) { _layer.Opacity = v; }
    }
}
