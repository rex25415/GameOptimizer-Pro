// ============================================================================
//  NumTicker.cs — 数字滚动器：大数字平滑计数而非每秒硬跳
//  复用 Motion 补间池；委托缓存为字段（遵守「调用方不得每次分配闭包」）；
//  格式化结果不变时不写 Text，避免每帧重排。reduce-motion / 离屏立即落终态。
// ============================================================================
using System;
using System.Windows.Controls;

namespace Go
{
    public sealed class NumTicker
    {
        private readonly TextBlock _t;
        private readonly Func<double, string> _fmt;
        private readonly Action<double> _apply;
        private double _shown = double.NaN;

        public NumTicker(TextBlock t, Func<double, string> fmt)
        {
            _t = t;
            _fmt = fmt;
            _apply = Apply;
        }

        private void Apply(double v)
        {
            _shown = v;
            string s = _fmt(v);
            if (_t.Text != s) _t.Text = s;
        }

        /// <summary>平滑滚到目标值；首帧 / 减少动效时立即落位。</summary>
        public void Set(double target, int ms)
        {
            if (double.IsNaN(_shown)) { Apply(target); return; }
            if (Math.Abs(target - _shown) < 1e-9) return;
            Motion.Tween(_shown, target, ms, _apply);
        }

        public void Snap(double v) { Apply(v); }

        /// <summary>直接显示非数字文本（如探针待机时的 —），并复位计数状态使下次 Set 立即落位。</summary>
        public void ShowText(string s)
        {
            _shown = double.NaN;
            if (_t.Text != s) _t.Text = s;
        }
    }
}