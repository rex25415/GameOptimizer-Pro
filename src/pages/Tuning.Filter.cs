// ============================================================================
//  Tuning.Filter.cs — 02 调优矩阵：实时关键字检索与预设达成度条（Tuning 分片）
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Tuning
    {
        private Border BuildSearchInput()
        {
            Border wrap = new Border();
            wrap.Height = 32;
            wrap.Width = 232;
            wrap.CornerRadius = new CornerRadius(16);
            wrap.Background = T.S(T.Inset);
            wrap.BorderBrush = T.S(T.LineMid);
            wrap.BorderThickness = new Thickness(1);
            wrap.Padding = new Thickness(12, 0, 12, 0);
            wrap.VerticalAlignment = VerticalAlignment.Center;
            wrap.SnapsToDevicePixels = true;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            System.Windows.Shapes.Path mag = Ui.Icon(Glyph.Search, 11, T.Dim, 1.35);
            mag.Margin = new Thickness(0, 0, 7, 0);
            mag.VerticalAlignment = VerticalAlignment.Center;
            mag.IsHitTestVisible = false;
            g.Children.Add(mag);

            Grid inputHost = new Grid();
            Grid.SetColumn(inputHost, 1);

            _searchPh = Ui.Tx("搜索动作、键名或分组…", T.FsMicro, T.Mute, T.FCn);
            _searchPh.VerticalAlignment = VerticalAlignment.Center;
            _searchPh.IsHitTestVisible = false;
            _searchPh.Visibility = string.IsNullOrEmpty(_searchQuery) ? Visibility.Visible : Visibility.Collapsed;
            inputHost.Children.Add(_searchPh);

            _searchBox = new TextBox();
            _searchBox.Text = _searchQuery ?? "";
            _searchBox.FontSize = T.FsMicro;
            _searchBox.FontFamily = T.FCn;
            _searchBox.Foreground = T.S(T.Hi);
            _searchBox.CaretBrush = T.S(T.Accent);
            _searchBox.Background = Brushes.Transparent;
            _searchBox.BorderThickness = new Thickness(0);
            _searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            _searchBox.TextChanged += delegate
            {
                _searchQuery = _searchBox.Text == null ? "" : _searchBox.Text.Trim();
                if (_searchPh != null)
                    _searchPh.Visibility = _searchQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                ApplyFilter();
            };
            _searchBox.GotKeyboardFocus += delegate
            {
                wrap.BorderBrush = T.S(T.AccentA(0x78));
                mag.Stroke = T.S(T.Accent);
            };
            _searchBox.LostKeyboardFocus += delegate
            {
                wrap.BorderBrush = T.S(T.LineMid);
                mag.Stroke = T.S(T.Dim);
            };
            _searchBox.PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == System.Windows.Input.Key.Escape && _searchBox.Text.Length > 0)
                {
                    _searchBox.Text = "";
                    e.Handled = true;
                }
            };
            inputHost.Children.Add(_searchBox);

            g.Children.Add(inputHost);
            wrap.Child = g;
            return wrap;
        }

        private bool MatchSearch(ActionDef a)
        {
            if (string.IsNullOrEmpty(_searchQuery)) return true;
            if (a == null) return false;
            string q = _searchQuery;
            if (ContainsI(a.Name, q) || ContainsI(a.Desc, q) || ContainsI(a.Group, q) || ContainsI(a.Id, q)) return true;
            if (a.Ops != null)
            {
                for (int i = 0; i < a.Ops.Length; i++)
                {
                    Op o = a.Ops[i];
                    if (o == null) continue;
                    if (ContainsI(o.Name, q) || ContainsI(o.KvKey, q) || ContainsI(o.Path, q)) return true;
                }
            }
            return false;
        }

        private static bool ContainsI(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void UpdatePresetBar(CardRef cr, double trackW)
        {
            if (cr == null || cr.Fill == null) return;
            double w = trackW > 0 ? trackW : 160;
            int tot = cr.Total > 0 ? cr.Total : 1;
            int done = Math.Max(0, tot - cr.Pending);
            double frac = (double)done / tot;
            if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
            cr.Fill.Width = Math.Round(w * frac);
            cr.Fill.Background = T.S(cr.Pending == 0 ? T.Ok : (cr.Ready == 0 ? T.Warn : T.Accent));
        }

        /// <summary>自检钩子：写入关键字并触发实时过滤。</summary>
        public void TestSetSearch(string q)
        {
            _searchQuery = q == null ? "" : q.Trim();
            if (_searchBox != null) _searchBox.Text = _searchQuery;
            else ApplyFilter();
        }

        /// <summary>自检钩子：返回当前可见调优行数。</summary>
        public int TestVisibleRowCount()
        {
            int n = 0;
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Root.Visibility == Visibility.Visible) n++;
            return n;
        }

        private UIElement BuildStat()
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.VerticalAlignment = VerticalAlignment.Center;

            _statNum = Ui.Mono("— / —", T.FsBody, T.Hi);
            _statNum.MinWidth = 58;
            _statNum.TextAlignment = TextAlignment.Right;
            sp.Children.Add(_statNum);

            TextBlock lbl = Ui.Tx("已优化", T.FsNano, T.Dim, T.FCn);
            lbl.Margin = new Thickness(6, 0, 0, 0);
            sp.Children.Add(lbl);

            _statSep = new Border();
            _statSep.Width = 1;
            _statSep.Height = 12;
            _statSep.Background = T.S(T.Line);
            _statSep.Margin = new Thickness(10, 0, 10, 0);
            _statSep.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(_statSep);

            _statLdg = Ui.Tx("账本 — 项", T.FsNano, T.Mute, T.FCn);
            _statLdg.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(_statLdg);

            Grid bar = new Grid();
            bar.Width = 4;
            bar.Height = 16;
            bar.Margin = new Thickness(11, 0, 0, 0);
            bar.VerticalAlignment = VerticalAlignment.Center;

            Border track = new Border();
            track.CornerRadius = new CornerRadius(2);
            track.Background = T.S(T.LineHi);
            bar.Children.Add(track);

            _statFill = new Border();
            _statFill.CornerRadius = new CornerRadius(2);
            _statFill.Background = T.S(T.Accent);
            _statFill.VerticalAlignment = VerticalAlignment.Bottom;
            _statFill.Height = 0;
            bar.Children.Add(_statFill);

            sp.Children.Add(bar);

            Border cap = new Border();
            cap.Height = 26;
            cap.CornerRadius = new CornerRadius(13);
            cap.BorderThickness = new Thickness(1);
            cap.BorderBrush = T.S(T.LineMid);
            cap.Background = T.S(T.Inset);
            cap.Padding = new Thickness(12, 0, 10, 0);
            cap.Margin = new Thickness(0, 0, 14, 1);
            cap.VerticalAlignment = VerticalAlignment.Bottom;
            cap.Child = sp;
            return cap;
        }
    }
}
