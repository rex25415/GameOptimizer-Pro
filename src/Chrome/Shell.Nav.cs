// ============================================================================
//  Shell.Nav.cs — 导航与持久化（Shell 分片）
//  页面切换 / 历史栈 / 全局快捷键 / ui.json 读写
//  来源：Shell.cs（1324 行单体）按职责拆分，partial 共享字段，行为零变化。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IOPath = System.IO.Path;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Go
{
    public partial class Shell : Window
    {
        // ====================================================================
        //  导航
        // ====================================================================
        /// <summary>诊断：最近一次页面构建（视觉树）与进入（数据装配）各耗多久。</summary>
        public double LastHostMs;
        public double LastEnterMs;

        public void Go(string key) { Go(key, true); }

        /// <summary>账本 / 权限变了：所有页面作废，当前页立刻重建。
        /// 旧实现只 Invalidate 不重建，而 Go() 对"同一页"直接 return ——
        /// 结果当前页失去总线订阅、继续显示应用前的旧树，用户以为动作没生效。</summary>
        public void RefreshAllPages()
        {
            System.Collections.Generic.IList<Page> all = _core.Pages;
            for (int i = 0; i < all.Count; i++) all[i].Invalidate();
            // 只对当前页 force 重建是不够的：Invalidate 只是把 Page._built 置空，
            // 而其它页的 ThinScroll 还缓存在 _wraps 里。之后再导航到那一页，
            // Go() 命中缓存、不会调用 Host() → 视觉树停在旧数据，订阅数还是 0。
            // 清空缓存让每一页在**被访问时**重建：不付整体重建的卡顿，也不会看到旧树。
            _wraps.Clear();
            _host.Content = null;
            Page cur = _cur;
            if (cur != null) Go(cur.Key, false, true);
        }

        private void Go(string key, bool record) { Go(key, record, false); }

        private void Go(string key, bool record, bool force)
        {
            Page p = _core.Get(key);
            if (p == null || (p == _cur && !force)) return;
            if (force) _wraps.Remove(key);
            int dir = _navDir;
            if (record)
            {
                if (_histPos < _history.Count - 1) _history.RemoveRange(_histPos + 1, _history.Count - _histPos - 1);
                _history.Add(key);
                _histPos = _history.Count - 1;
            }
            PhaseNav(key, true);
            if (_cur != null) _cur.LeavePage();

            foreach (KeyValuePair<string, NavRow> kv in _navs) kv.Value.SetActive(kv.Key == key);

            ThinScroll wrap;
            bool fresh = false;
            UIElement built = null;
            if (!_wraps.TryGetValue(key, out wrap))
            {
                long h0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    built = p.Host(_core);
                }
                catch (Exception ex)
                {
                    // 一页构建失败不该把整个导航卡住：给一个可见的错误页，
                    // 记账后仍然完成这次导航（否则用户点到这一页就没反应）。
                    Diag.Fail("nav.host", ex);
                    built = Ui.Empty(Glyph.Warn, "此页构建失败", ex.GetType().Name + " · " + ex.Message, T.Warn, null);
                }
                LastHostMs = (System.Diagnostics.Stopwatch.GetTimestamp() - h0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                wrap = new ThinScroll(built);
                // 关键：禁用横向滚动让内容宽度被视口约束，
                // 否则 Reflow 会拿到"子元素撑大后的宽度"，断点判断跟着失效。
                wrap.View.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                _wraps[key] = wrap;
                fresh = true;
            }
            _host.Content = wrap;
            // 构建成功（或降级成错误页）之后才认这一页：Host 抛异常时 _cur 不该提前指过去。
            _cur = p;
            _crumb.Text = Ui.Spaced(p.En.ToUpperInvariant());
            if (!Rs.ReduceMotion && Ui.Animate)
            {
                // 换页动效：容器整体缩放 + 位移 + 淡入；首建页再把顶层卡片做 35ms 阶梯。
                // 两者叠在一起，读起来是"这台仪器翻到下一页"，而不是"内容突然出现"。
                Motion.PageIn(wrap, dir);
                Panel rp = fresh ? built as Panel : null;
                if (rp != null && rp.Children.Count > 1)
                    for (int i = 0; i < rp.Children.Count; i++)
                        Motion.SlideIn(rp.Children[i], 0, 10, i * 35);
            }
            else wrap.Opacity = 1;
            long e0 = System.Diagnostics.Stopwatch.GetTimestamp();
            p.OnEnter();
            LastEnterMs = (System.Diagnostics.Stopwatch.GetTimestamp() - e0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            UpdateHistoryButtons();
            PhaseNav(key, false);
        }

        // ====================================================================
        //  页面切换的相位标签（给 Kernel/Jank.cs 的停顿时钟用）
        // ====================================================================
        //  只做一件事：告诉尺子「这段时间在干导航」，于是 stall 会归到 nav:<页> 上，
        //  而不是落进 idle: 里让人去猜。字符串只在换页时拼一次。
        private void PhaseNav(string key, bool on)
        {
            if (on) Jank.Phase("nav:" + key);
            else Jank.Phase(_jankIdle);
        }

        /// <summary>在导轨里上下移动键盘焦点（↑↓ 键）。</summary>
        public void MoveRailFocus(NavRow from, int dir)
        {
            if (_navList == null || from == null) return;
            int i = _navList.Children.IndexOf(from);
            if (i < 0) return;
            for (int step = 1; step <= _navList.Children.Count; step++)
            {
                int j = i + dir * step;
                if (j < 0 || j >= _navList.Children.Count) return;
                NavRow r = _navList.Children[j] as NavRow;
                if (r == null || r.Visibility != Visibility.Visible) continue;
                r.Focus();
                return;
            }
        }

        private void Navigate(int delta)
        {
            int next = _histPos + delta;
            if (next < 0 || next >= _history.Count) return;
            _navDir = delta;
            _histPos = next;
            Go(_history[next], false);
            _navDir = 1;
            UpdateHistoryButtons();
        }

        private void UpdateHistoryButtons()
        {
            if (_histBack == null) return;
            _histBack.Opacity = _histPos > 0 ? 1.0 : 0.25;
            _histFwd.Opacity = _histPos < _history.Count - 1 ? 1.0 : 0.25;
        }
        // ====================================================================
        //  键盘
        // ====================================================================
        private void OnKey(object s, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

            if (ctrl && e.Key == Key.K) { ShowPalette(); e.Handled = true; return; }
            if (e.Key == Key.Escape && _palette != null && _palette.Visibility == Visibility.Visible) { HidePalette(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.F && _palette != null && _palette.Visibility == Visibility.Visible) { e.Handled = true; return; }

            if (alt && e.Key == Key.Left) { Navigate(-1); e.Handled = true; return; }
            if (alt && e.Key == Key.Right) { Navigate(1); e.Handled = true; return; }

            if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                int idx = e.Key - Key.D1;
                if (idx < _core.Pages.Count) Go(_core.Pages[idx].Key);
                e.Handled = true;
                return;
            }
            if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
            {
                if (_core.Pages.Count > 9) Go(_core.Pages[9].Key);
                e.Handled = true;
                return;
            }
        }

        // ====================================================================
        //  状态持久化
        // ====================================================================
        private void RestoreState()
        {
            if (SkipStateRestore) return;
            try
            {
                if (!File.Exists(_statePath)) return;
                JavaScriptSerializer js = new JavaScriptSerializer();
                UiState st = js.Deserialize<UiState>(File.ReadAllText(_statePath, Encoding.UTF8));
                if (st == null) return;
                // 多屏：上一次的位置可能在副屏，按"主屏工作区"夹取会把它拽回主屏。
                // 这里改用整个虚拟桌面，只要标题栏还有至少 200×120 落在可见区域就照原样恢复。
                double vl = SystemParameters.VirtualScreenLeft;
                double vt = SystemParameters.VirtualScreenTop;
                double vr = vl + SystemParameters.VirtualScreenWidth;
                double vb = vt + SystemParameters.VirtualScreenHeight;

                if (st.Width > 400 && st.Width <= SystemParameters.VirtualScreenWidth) Width = st.Width;
                if (st.Height > 300 && st.Height <= SystemParameters.VirtualScreenHeight) Height = st.Height;
                if (st.Left > vl - 40 && st.Left + 200 < vr) Left = st.Left;
                if (st.Top > vt - 20 && st.Top + 120 < vb) Top = st.Top;
                if (st.Maximized) WindowState = WindowState.Maximized;
                if (!string.IsNullOrEmpty(st.Page)) _pendingPage = st.Page;
                if (!string.IsNullOrEmpty(st.Theme)) T.ApplyTheme(st.Theme);
            }
            catch (Exception ex) { Diag.Fail("ui.state.load", ex); }
        }

        private string _pendingPage;

        public void RestorePage()
        {
            if (!string.IsNullOrEmpty(_pendingPage) && _core.Get(_pendingPage) != null) Go(_pendingPage);
            else Go("overview");
            UpdateHistoryButtons();
        }

        public void SaveState()
        {
            if (SkipStateRestore) return;
            try
            {
                UiState st = new UiState();
                st.Maximized = WindowState == WindowState.Maximized;
                Rect r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                st.Left = r.X; st.Top = r.Y; st.Width = r.Width; st.Height = r.Height;
                st.Page = _cur != null ? _cur.Key : "overview";
                st.Theme = T.ThemeId;
                JavaScriptSerializer js = new JavaScriptSerializer();
                string dir = IOPath.GetDirectoryName(_statePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_statePath, js.Serialize(st), new UTF8Encoding(false));
            }
            catch (Exception ex) { Diag.Fail("ui.state.save", ex); }
        }

        /// <summary>运行时切换主题：联动派生色后重建外壳与当前页，使全局立即换色（无需重启）。</summary>
        public void ApplyThemeLive(string id)
        {
            if (!T.ApplyTheme(id)) return;
            SaveState();
            string cur = _cur != null ? _cur.Key : "overview";
            if (_cur != null) { try { _cur.LeavePage(); } catch (Exception ex) { Diag.Fail("page.onleave", ex); } }
            _cur = null;
            Build();                 // Build 内部清空导航/页面缓存与侧栏状态指纹，用新强调色重建

            // 清掉所有页面的已构建树：Build() 只清了外壳与包装容器，
            // 页面自己的 _built 还握着旧强调色的画笔。不失效它，
            // 切过去看到的仍是上一个主题——换主题看起来像只换了外壳。
            System.Collections.Generic.IList<Page> all = _core.Pages;
            for (int i = 0; i < all.Count; i++) { try { all[i].Invalidate(); } catch { Diag.Hit("page.invalidate"); } }
            Go(cur, false, true);    // 不往历史栈重复塞记录
            ApplyShellLayout();
            SyncMaximizeChrome();
            RefreshDrift();
            RefreshDaemonUi();
            string name = id;
            for (int i = 0; i < T.Themes.Length; i++)
                if (T.Themes[i].Id == id) { name = T.Themes[i].Name; break; }
            Toast("已切换主题 · " + name, true);
        }
    }
}