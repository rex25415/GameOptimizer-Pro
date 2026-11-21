// ============================================================================
//  WinNative.cs — 原生 Win32 轻量系统动作（零子进程）
//
//  1) DnsFlushResolverCache (dnsapi.dll)：毫秒级刷新本机 DNS 解析缓存，
//     替代派生 ipconfig.exe /flushdns。
//  2) SystemParametersInfoW (user32.dll)：写/还原 Control Panel 下的鼠标、
//     键盘与桌面参数后即时通知 Windows 内核生效，不必注销或重启。
//     严格遵守只读模式与沙箱隔离：测试与离屏渲染永不触碰宿主桌面状态。
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace Go
{
    public static class WinNative
    {
        private const uint SPI_SETKEYBOARDDELAY = 0x0017;
        private const uint SPI_SETKEYBOARDSPEED = 0x000B;
        private const uint SPI_SETMOUSE = 0x0004;
        private const uint SPI_SETMENUSHOWDELAY = 0x006B;
        private const uint SPIF_SENDCHANGE = 0x0002;

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        private static extern uint DnsFlushResolverCacheNative();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

        /// <summary>原生调用 dnsapi.dll 刷新 Windows DNS 客户端缓存（无黑窗、零子进程）。</summary>
        public static bool FlushDnsCache()
        {
            if (Power.ReadOnlyMode || Ledger.ReadOnlyMode) return false;
            try
            {
                return DnsFlushResolverCacheNative() != 0;
            }
            catch (Exception ex)
            {
                Diag.Fail("winnative.dns.flush", ex);
                return false;
            }
        }

        /// <summary>
        /// 当写入/还原 Control Panel\Mouse|Keyboard|Desktop 注册表后，
        /// 从注册表回读当前值并通过 SystemParametersInfo 广播，让设置即刻生效。
        /// 沙箱测试（Ledger.DirOverride != null）或只读模式下不广播，绝不干扰宿主。
        /// </summary>
        public static void SyncControlPanelIfNeeded(string hive, string path)
        {
            if (Power.ReadOnlyMode || Ledger.ReadOnlyMode || !string.IsNullOrEmpty(Ledger.DirOverride)) return;
            if (!string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(path)) return;

            try
            {
                if (string.Equals(path, @"Control Panel\Mouse", StringComparison.OrdinalIgnoreCase))
                {
                    int t1 = ReadInt("HKCU", path, "MouseThreshold1", 6);
                    int t2 = ReadInt("HKCU", path, "MouseThreshold2", 10);
                    int sp = ReadInt("HKCU", path, "MouseSpeed", 1);
                    int[] arr = new int[] { t1, t2, sp };
                    SystemParametersInfoW(SPI_SETMOUSE, 0, arr, SPIF_SENDCHANGE);
                }
                else if (string.Equals(path, @"Control Panel\Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    int delay = Clamp(ReadInt("HKCU", path, "KeyboardDelay", 1), 0, 3);
                    int speed = Clamp(ReadInt("HKCU", path, "KeyboardSpeed", 31), 0, 31);
                    SystemParametersInfoW(SPI_SETKEYBOARDDELAY, (uint)delay, IntPtr.Zero, SPIF_SENDCHANGE);
                    SystemParametersInfoW(SPI_SETKEYBOARDSPEED, (uint)speed, IntPtr.Zero, SPIF_SENDCHANGE);
                }
                else if (string.Equals(path, @"Control Panel\Desktop", StringComparison.OrdinalIgnoreCase))
                {
                    int ms = Clamp(ReadInt("HKCU", path, "MenuShowDelay", 400), 0, 4000);
                    SystemParametersInfoW(SPI_SETMENUSHOWDELAY, (uint)ms, IntPtr.Zero, SPIF_SENDCHANGE);
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("winnative.spi.sync", ex);
            }
        }

        private static int ReadInt(string hive, string path, string name, int fallback)
        {
            RegValue rv = Reg.Read(hive, path, name);
            if (!rv.Existed || rv.Value == null) return fallback;
            int v;
            return int.TryParse(Reg.ToText(rv.Value).Trim(), out v) ? v : fallback;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
