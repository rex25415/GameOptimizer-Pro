// ============================================================================
//  TrayHost.Messages.cs — 托盘消息解包（TrayHost 的 partial 分片）
//
//  为什么单独成文件：TrayHost.cs 已经 900 行，超过仓库"单文件 800 行"的硬约束。
//  这里集中"一条 Windows 消息怎么变成一次用户动作"：外壳广播 → 取坐标 →
//  过滤噪音/去重 → 分派。状态（_installed/_data/_src/_readding…）都在主分片，
//  partial 共享，行为零变化。
// ============================================================================
using System;

namespace Go.Tray
{
    public sealed partial class TrayHost
    {
        // ---- 消息解包 ------------------------------------------------------
        //  拆成四段（Rev86）：外壳广播 → 取坐标 → 过滤噪音/去重 → 分派。
        //  旧版是一个 113 行的 WndProc，问题不在长度，在"每加一个事件码都要改同一个函数"：
        //  上一轮 NIN_SELECT 就是这样漏掉兜底码的。
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_wmTaskbarCreated != 0 && (uint)msg == _wmTaskbarCreated)
            {
                handled = true;
                OnTaskbarCreated();
                return IntPtr.Zero;
            }
            if (msg != WM_TRAY) return IntPtr.Zero;
            handled = HandleTrayMessage(hwnd, wParam, lParam);
            return IntPtr.Zero;
        }

        /// <summary>
        /// Explorer 重启：外壳把托盘整块清空，之前 NIM_ADD 的注册随之消失。
        /// 不在这里补一次，图标就永久消失（用户看到的是"软件还活着，但点不到"）。
        ///
        /// 必须可重入：补注册期间外壳可能再广播一次（Explorer 连崩两次），
        /// 两条路径同时 NIM_DELETE + NIM_ADD 会让后到的那条把刚加好的图标删掉，
        /// 或者把 _installed 标成 false 之后再也补不回来。
        /// </summary>
        private void OnTaskbarCreated()
        {
            if (_src == null) { TrayTrace.Write("taskbar-created: no sink"); return; }
            if (_readding) { TrayTrace.Write("taskbar-created: reentrant skip"); return; }
            _readding = true;
            try
            {
                // 先尽力摘掉可能残留的旧注册（真重启时它已经没了，摘不掉也无害），
                // 再重新添加。只 ADD 不 DELETE 会在"图标其实还在"时被外壳拒绝。
                TryDeleteIcon("taskbar-created");
                _installed = false;   // 先标脏：补注册失败时调用方/重试逻辑能看出来
                if (AddIcon())
                {
                    TrayTrace.Write("re-added after TaskbarCreated");
                    TryPromote(0);
                }
                else
                {
                    TrayTrace.Write("re-add after TaskbarCreated failed");
                    ScheduleRetry();
                }
            }
            finally { _readding = false; }
        }

        /// <summary>尽力删除图标注册。外壳已经不在了（注销/重启）时删不掉是常态，不算失败。</summary>
        private void TryDeleteIcon(string where)
        {
            try
            {
                if (!Shell_NotifyIcon(NIM_DELETE, ref _data)) TrayTrace.Write("NIM_DELETE failed (" + where + ")");
            }
            catch (Exception ex)
            {
                // 见上：旧注册多半已经没了；删不掉不影响随后的重新添加。
                Diag.Hit("tray.delete." + where);
                TrayTrace.Write("NIM_DELETE threw (" + where + "): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 解析托盘回调的锚点。v4：wParam 低/高 16 位是屏幕坐标；v0：wParam 是图标 ID。
        /// 退回 v0 时若照抄这个解析，锚点会变成 (1,0)，菜单出现在屏幕左上角。
        /// </summary>
        private void UpdateAnchor(IntPtr wParam)
        {
            if (_v4)
            {
                long wp = wParam.ToInt64();
                AnchorX = (short)(wp & 0xFFFF);
                AnchorY = (short)((wp >> 16) & 0xFFFF);
                return;
            }
            POINT pt;
            if (GetCursorPos(out pt)) { AnchorX = pt.X; AnchorY = pt.Y; }
            else { AnchorX = 0; AnchorY = 0; }
        }

        /// <summary>鼠标经过图标/工具提示冒泡的噪音，不是用户动作。</summary>
        private static bool IsNoise(int evt)
        {
            // v4 下鼠标每移动一像素就发一条 WM_MOUSEMOVE，绝不能让它参与去重状态。
            return evt == 0x0200 || evt == 0x0406 || evt == 0x0407 || evt == 0x0201 || evt == 0x0204;
        }

        /// <summary>
        /// 同一次点击会被外壳连发两条：左键 WM_LBUTTONUP(v0) + NIN_SELECT、
        /// 右键 WM_RBUTTONUP(v0) + WM_CONTEXTMENU。它们必须归到同一个去重槽位，
        /// 否则一次点击会开两个菜单。键盘激活是独立事件，单独成槽。
        /// </summary>
        private static int DedupeSlot(int evt)
        {
            if (evt == WM_LBUTTONUP) return NIN_SELECT;
            if (evt == WM_RBUTTONUP) return WM_CONTEXTMENU;
            return evt;
        }

        /// <summary>返回 true 表示这条消息已被本窗口消费（与原实现的 handled 语义一致）。</summary>
        private bool HandleTrayMessage(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            LastEvent = evt;
            EventCount++;
            UpdateAnchor(wParam);

            if (IsNoise(evt)) return true;

            long now = ClickClock.NowMs;
            int slot = DedupeSlot(evt);
            TrayTrace.Write(EventName(evt) + " anchor=" + AnchorX + "," + AnchorY);

            // 去重：同一类事件 260ms 内只处理一次
            if (slot == _lastEventCode && now - _lastEventMs < 260)
            {
                TrayTrace.Write("  dup-skip");
                return true;
            }
            _lastEventCode = slot;
            _lastEventMs = now;

            return Dispatch(hwnd, evt);
        }

        /// <summary>事件码 → 对外回调。未识别的事件码返回 false，交给 WPF 默认处理。</summary>
        private bool Dispatch(IntPtr hwnd, int evt)
        {
            switch (evt)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                case WM_LBUTTONUP:
                    Fire(LeftUp);
                    return true;

                case WM_LBUTTONDBLCLK:
                    Fire(DoubleClick);
                    if (DoubleClick == null) Fire(LeftUp);
                    return true;

                case WM_CONTEXTMENU:
                case WM_RBUTTONUP:
                    // 文档规定的配方：先把本窗口提到前台并投一个 WM_NULL，
                    // 否则弹出的窗口不会因为"点到别处"而自动关闭。
                    SetForegroundWindow(hwnd);
                    PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                    Fire(RightUp);
                    return true;

                case NIN_BALLOONUSERCLICK:
                    Fire(LeftUp);
                    return true;

                case NIN_BALLOONSHOW:
                case NIN_BALLOONHIDE:
                case NIN_BALLOONTIMEOUT:
                    return true;
            }
            return false;
        }

        private static string EventName(int evt)
        {
            switch (evt)
            {
                case NIN_SELECT: return "NIN_SELECT";
                case NIN_KEYSELECT: return "NIN_KEYSELECT";
                case NIN_BALLOONUSERCLICK: return "NIN_BALLOONUSERCLICK";
                case NIN_BALLOONSHOW: return "NIN_BALLOONSHOW";
                case NIN_BALLOONHIDE: return "NIN_BALLOONHIDE";
                case NIN_BALLOONTIMEOUT: return "NIN_BALLOONTIMEOUT";
                case WM_CONTEXTMENU: return "WM_CONTEXTMENU";
                case WM_LBUTTONUP: return "WM_LBUTTONUP(v0)";
                case WM_RBUTTONUP: return "WM_RBUTTONUP(v0)";
                case WM_LBUTTONDBLCLK: return "WM_LBUTTONDBLCLK(v0)";
                default: return "0x" + evt.ToString("X4");
            }
        }

        private static void Fire(Action h)
        {
            if (h == null) return;
            try { h(); }
            catch (Exception ex) { LogTrayError(ex); }
        }
    }
}
