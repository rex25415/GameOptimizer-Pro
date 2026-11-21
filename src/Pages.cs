// ============================================================================
//  Pages.cs — 页面基类与页面注册表
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go
{
    public abstract class Page
    {
        private UIElement _built;

        // ---- 状态总线订阅（SPINE）------------------------------------------
        // 页面不再靠 1Hz Tick 猜"世界变了没有"：构建期 Watch 一次，变化由总线推来。
        // 令牌是必需的——每次 Build 都会新建 lambda，Off(委托) 比不出同一性，
        // 于是页面重建时旧订阅会永远留在总线上，一直往已丢弃的控件里写字。
        private int[] _watchTokens = new int[4];
        private int _watchCount;
        private List<DispatcherTimer> _timers;
        private Dispatcher _ui;

        public abstract string Key { get; }
        public abstract string Idx { get; }
        public abstract string Title { get; }
        public abstract string En { get; }
        public abstract string Ico { get; }

        protected Core C;

        public UIElement Host(Core c)
        {
            C = c;
            // 记住构建页面的调度器：订阅回调可能来自守护线程，必须搬回这一条线程。
            if (_ui == null) _ui = Dispatcher.FromThread(Thread.CurrentThread);
            if (_built == null) _built = Build(c);
            return _built;
        }

        public abstract UIElement Build(Core c);

        /// <summary>
        /// 构建期订阅总线。返回的令牌自动登记，Invalidate 时一起退订。
        /// 回调里必须先判断控件是否已构建（_xxx == null）——页面离开后仍会收到通知，
        /// 那是刻意的：回到页面时应该已经是最新值，而不是等下一次 Tick 才补上。
        /// </summary>
        protected void Watch(Dirty mask, Action<Dirty> fn)
        {
            if (fn == null || mask == Dirty.None) return;
            if (C == null || C.State == null) return;

            int token = C.State.OnToken(mask, Wrap(fn));
            if (token == 0) return;
            if (_watchCount == _watchTokens.Length)
                Array.Resize(ref _watchTokens, _watchTokens.Length * 2);
            _watchTokens[_watchCount++] = token;
        }

        /// <summary>退订本页全部订阅。Invalidate 会自动调用；不要在外面重复调。</summary>
        protected void Unwatch()
        {
            if (_watchCount == 0) return;
            if (C != null && C.State != null)
            {
                for (int i = 0; i < _watchCount; i++) C.State.OffToken(_watchTokens[i]);
            }
            _watchCount = 0;
        }

        /// <summary>把回调包成"UI 线程 + 吞异常计数"的安全入口。</summary>
        private Action<Dirty> Wrap(Action<Dirty> fn)
        {
            Page self = this;
            return delegate (Dirty d) { self.Incoming(fn, d); };
        }

        private void Incoming(Action<Dirty> fn, Dirty d)
        {
            Dispatcher ui = _ui;
            if (ui != null && !ui.CheckAccess())
            {
                // 总线是同步广播的，而 GameWatcher / 电池感知都在别的线程 Raise。
                // 直接写控件会跨线程异常，所以在这里搬一次线程。
                try
                {
                    ui.BeginInvoke(DispatcherPriority.Background, (Action)delegate { Fire(fn, d); });
                    return;
                }
                catch (Exception ex) { Diag.Fail("page.watch.marshal", ex); }
            }
            Fire(fn, d);
        }

        private void Fire(Action<Dirty> fn, Dirty d)
        {
            try { fn(d); }
            catch (Exception ex) { Diag.Fail("page.watch", ex); }   // 一页坏掉不该带走总线
        }

        /// <summary>
        /// 丢弃已构建的视觉树，下次进入时重建。
        /// 之前没有这条路径，于是"运行时换主题"只换了外壳：页面的 _built 是第一次
        /// 构建时用旧强调色画出来的，之后永远复用。审计实测 16 个强调色元素里
        /// 有 12 个在换主题后仍是旧色 —— 用户以为功能坏了，其实是缓存没失效。
        /// </summary>
        public void Invalidate()
        {
            // 顺序有意义：先停定时器并退订，再丢掉视觉树。反过来的话，订阅或定时器回调可能在
            // 两者之间落地，写进一棵马上要丢的树——白做功，还留下强引用。
            StopTimers();
            Unwatch();
            _built = null;
        }

        /// <summary>由外壳在离开页面时统一调度，保证托管定时器绝不在后台空转。</summary>
        internal void LeavePage()
        {
            StopTimers();
            OnLeave();
        }

        /// <summary>创建受页面生命周期托管的定时器：离页/重建时自动停机。</summary>
        protected DispatcherTimer TrackTimer(int intervalMs, EventHandler onTick)
        {
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(Math.Max(16, intervalMs));
            if (onTick != null) t.Tick += onTick;
            if (_timers == null) _timers = new List<DispatcherTimer>(4);
            _timers.Add(t);
            return t;
        }

        /// <summary>停止全部已托管的页面定时器。</summary>
        protected void StopTimers()
        {
            if (_timers == null || _timers.Count == 0) return;
            for (int i = 0; i < _timers.Count; i++)
            {
                if (_timers[i] != null) _timers[i].Stop();
            }
        }

        /// <summary>采样节拍回调（仅在页面可见时被调用）。</summary>
        public virtual void Tick(Snapshot s) { }

        /// <summary>固定高度的空白间隔。10 个页面原本各写了一份同实现。</summary>
        protected static Border Gap(double h)
        {
            Border b = new Border();
            b.Height = h;
            b.Background = Brushes.Transparent;
            return b;
        }

        /// <summary>失败报告的第一行（3 个页面原本各写了一份）。</summary>
        protected static string FirstLine(ApplyReport r)
        {
            foreach (string l in r.Lines) return l;
            return "失败";
        }

        public virtual void OnEnter() { }
        public virtual void OnLeave() { }
    }

    public static class Registry
    {
        public static void Load(Core c)
        {
            c.Register(new Pages.Overview());
            c.Register(new Pages.Tuning());
            c.Register(new Pages.Scheduler());
            c.Register(new Pages.Memory());
            c.Register(new Pages.Graphics());
            c.Register(new Pages.Network());
            c.Register(new Pages.Processes());
            c.Register(new Pages.Profiles());
            c.Register(new Pages.Benchmark());
            c.Register(new Pages.Restore());
            c.Register(new Pages.LogPage());
        }
    }
}
