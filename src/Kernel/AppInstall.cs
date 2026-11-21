// ============================================================================
//  AppInstall.cs — 一体化本机安装、用户自选快捷方式与原生自卸载引擎（Rev 142）
//
//  核心理念：只有一个单文件 exe，不分裂版本、不附带外置 .bat / Uninstall.ps1 脚本。
//    1) 安装与快捷方式同步：复制到 %LOCALAPPDATA%\Programs\GameOptimizer\GameOptimizer.exe，
//       严格按用户勾选创建或移除「桌面快捷方式」「开始菜单入口」「开机静默进托盘」；
//    2) 原生卸载对接：HKCU\...\Uninstall\GameOptimizerPro 直接指向
//       "<InstalledExe>" --uninstall（内置深色原生界面，零 PowerShell 黑框）；
//    3) 干净自清理：卸载时停止其他实例、清除所有快捷方式/自启/卸载项，
//       若当前正从安装目录运行则从 TEMP 发起隐藏自删命令，退出后 1 秒内彻底清空目录。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Go
{
    public static class AppInstall
    {
        public const string ShortcutFileName = "GameOptimizer Pro.lnk";
        public const string UninstallRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GameOptimizerPro";
        public const string RunRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValueName = "GameOptimizer";

        public sealed class InstallOptions
        {
            public bool CreateDesktopShortcut;
            public bool CreateStartMenuShortcut;
            public bool EnableAutostart;

            public static InstallOptions DefaultFor(InstallStatus st)
            {
                InstallOptions o = new InstallOptions();
                if (st != null && st.IsInstalled)
                {
                    o.CreateDesktopShortcut = st.HasDesktopShortcut;
                    o.CreateStartMenuShortcut = st.HasStartMenuShortcut;
                    o.EnableAutostart = st.HasAutostart;
                }
                else
                {
                    // 首次安装默认勾选桌面快捷方式；开始菜单与开机自启由用户按需自行勾选，绝不强塞
                    o.CreateDesktopShortcut = true;
                    o.CreateStartMenuShortcut = false;
                    o.EnableAutostart = false;
                }
                return o;
            }
        }

        public sealed class InstallStatus
        {
            public string CurrentExe;
            public string InstallDir;
            public string InstalledExe;
            public string DataDir;
            public bool RunningFromInstallDir;
            public bool RunningFromSourceTree;
            public bool InstalledBinaryExists;
            public bool HasDesktopShortcut;
            public bool HasStartMenuShortcut;
            public bool HasAutostart;
            public bool HasUninstallEntry;

            public bool IsInstalled
            {
                get { return InstalledBinaryExists || HasUninstallEntry; }
            }

            public bool NeedsInstallPrompt
            {
                get
                {
                    return !RunningFromInstallDir
                        && !RunningFromSourceTree
                        && !IsInstalled;
                }
            }

            public bool FullyInstalled
            {
                get { return InstalledBinaryExists && (HasDesktopShortcut || HasStartMenuShortcut || HasUninstallEntry); }
            }
        }

        public sealed class InstallResult
        {
            public bool Ok;
            public string InstalledExe;
            public bool DesktopShortcutActive;
            public bool StartMenuShortcutActive;
            public bool AutostartActive;
            public bool HotSwapped;
            public bool UninstallRegistered;
            public string Error;
        }

        public sealed class UninstallResult
        {
            public bool Ok;
            public int RemovedShortcuts;
            public bool RemovedInstallDir;
            public bool DeferredSelfDelete;
            public bool RemovedUserData;
            public string Error;
        }

        // ---- 原生 COM IShellLinkW / Shell32 ---------------------------------

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            [PreserveSig] int GetIDList(out IntPtr ppidl);
            [PreserveSig] int SetIDList(IntPtr pidl);
            [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            [PreserveSig] int GetHotKey(out short pwHotkey);
            [PreserveSig] int SetHotKey(short wHotKey);
            [PreserveSig] int GetShowCmd(out int piShowCmd);
            [PreserveSig] int SetShowCmd(int iShowCmd);
            [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
            [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        // ---- 路径辅助 -------------------------------------------------------

        public static string DefaultInstallDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "GameOptimizer");
        }

        public static string DefaultDataDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameOptimizer");
        }

        public static string DefaultDesktopDir()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        public static string DefaultStartMenuProgramsDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
        }

        public static string CurrentExePath()
        {
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                if (entry != null && !string.IsNullOrEmpty(entry.Location))
                    return Path.GetFullPath(entry.Location);
            }
            catch { }
            try
            {
                Process p = Process.GetCurrentProcess();
                if (p != null && p.MainModule != null && !string.IsNullOrEmpty(p.MainModule.FileName))
                    return Path.GetFullPath(p.MainModule.FileName);
            }
            catch { }
            return "";
        }

        private static string NormPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.TrimEnd('\\', '/'); }
        }

        private static bool PathEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(NormPath(a), NormPath(b), StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInSourceTree(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
                for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(dir); depth++)
                {
                    if (File.Exists(Path.Combine(dir, "build.ps1")) &&
                        File.Exists(Path.Combine(dir, "src", "App.cs")))
                        return true;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return false;
        }

        // ---- 探测 -----------------------------------------------------------

        public static InstallStatus Probe()
        {
            return Probe(CurrentExePath(), DefaultInstallDir(), DefaultDataDir(), DefaultDesktopDir(), DefaultStartMenuProgramsDir(), true);
        }

        public static InstallStatus Probe(string currentExe, string installDir, string desktopDir, string startMenuDir)
        {
            return Probe(currentExe, installDir, DefaultDataDir(), desktopDir, startMenuDir, false);
        }

        public static InstallStatus Probe(string currentExe, string installDir, string dataDir, string desktopDir, string startMenuDir, bool readRegistry)
        {
            InstallStatus st = new InstallStatus();
            st.CurrentExe = NormPath(currentExe);
            st.InstallDir = NormPath(installDir);
            st.DataDir = NormPath(dataDir);
            st.InstalledExe = string.IsNullOrEmpty(st.InstallDir) ? "" : Path.Combine(st.InstallDir, "GameOptimizer.exe");

            string curDir = "";
            try { if (!string.IsNullOrEmpty(st.CurrentExe)) curDir = NormPath(Path.GetDirectoryName(st.CurrentExe)); }
            catch { }

            st.RunningFromInstallDir = !string.IsNullOrEmpty(curDir) && PathEquals(curDir, st.InstallDir);
            st.RunningFromSourceTree = IsInSourceTree(st.CurrentExe);
            st.InstalledBinaryExists = !string.IsNullOrEmpty(st.InstalledExe) && File.Exists(st.InstalledExe);
            st.HasDesktopShortcut = HasShortcutTargeting(desktopDir, st.InstalledExe)
                || (!string.IsNullOrEmpty(st.CurrentExe) && HasShortcutTargeting(desktopDir, st.CurrentExe));
            st.HasStartMenuShortcut = HasShortcutTargeting(startMenuDir, st.InstalledExe)
                || (!string.IsNullOrEmpty(st.CurrentExe) && HasShortcutTargeting(startMenuDir, st.CurrentExe));

            if (readRegistry)
            {
                st.HasAutostart = HasRunEntry();
                st.HasUninstallEntry = HasUninstallRegistry();
            }
            return st;
        }

        private static bool HasRunEntry()
        {
            try
            {
                using (RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegSubKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(RunValueName);
                    return v != null && !string.IsNullOrEmpty(v.ToString());
                }
            }
            catch { return false; }
        }

        private static bool HasUninstallRegistry()
        {
            try
            {
                using (RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallRegSubKey, false))
                {
                    return k != null;
                }
            }
            catch { return false; }
        }

        private static bool HasShortcutTargeting(string folder, string targetExe)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(targetExe)) return false;
            if (!Directory.Exists(folder)) return false;
            try
            {
                string canonical = Path.Combine(folder, ShortcutFileName);
                if (File.Exists(canonical) && PathEquals(ReadShortcutTarget(canonical), targetExe))
                    return true;
                string[] files = Directory.GetFiles(folder, "*GameOptimizer*.lnk", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    if (PathEquals(files[i], canonical)) continue;
                    if (PathEquals(ReadShortcutTarget(files[i]), targetExe))
                        return true;
                }
            }
            catch (Exception ex) { Diag.Fail("install.probe.lnk", ex); }
            return false;
        }

        // ---- 原生 .lnk 读写 -------------------------------------------------

        public static bool CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description, string iconPath, int iconIndex)
        {
            if (string.IsNullOrEmpty(shortcutPath) || string.IsNullOrEmpty(targetPath)) return false;
            IShellLinkW link = null;
            try
            {
                string dir = Path.GetDirectoryName(shortcutPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                link = (IShellLinkW)new CShellLink();
                if (link.SetPath(targetPath) < 0) return false;
                if (!string.IsNullOrEmpty(workingDir)) link.SetWorkingDirectory(workingDir);
                if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
                if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, iconIndex);

                IPersistFile pf = (IPersistFile)link;
                pf.Save(shortcutPath, true);
                return File.Exists(shortcutPath);
            }
            catch (Exception ex)
            {
                Diag.Fail("install.lnk.create", ex);
                return false;
            }
            finally
            {
                if (link != null)
                {
                    try { Marshal.FinalReleaseComObject(link); }
                    catch { }
                }
            }
        }

        public static string ReadShortcutTarget(string shortcutPath)
        {
            if (string.IsNullOrEmpty(shortcutPath) || !File.Exists(shortcutPath)) return "";
            IShellLinkW link = null;
            try
            {
                link = (IShellLinkW)new CShellLink();
                IPersistFile pf = (IPersistFile)link;
                pf.Load(shortcutPath, 0);
                StringBuilder sb = new StringBuilder(520);
                int hr = link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                if (hr < 0) return "";
                return sb.ToString();
            }
            catch
            {
                return "";
            }
            finally
            {
                if (link != null)
                {
                    try { Marshal.FinalReleaseComObject(link); }
                    catch { }
                }
            }
        }

        // ---- 一体化安装与按勾选同步快捷方式 ----------------------------------

        public static InstallResult ApplyInstall(InstallOptions opt, Ledger auditLedger)
        {
            return ApplyInstall(CurrentExePath(), DefaultInstallDir(), DefaultDesktopDir(), DefaultStartMenuProgramsDir(), opt, true, auditLedger);
        }

        public static InstallResult ApplyInstall(
            string sourceExe,
            string installDir,
            string desktopDir,
            string startMenuDir,
            InstallOptions opt,
            bool touchRegistry,
            Ledger auditLedger)
        {
            InstallResult res = new InstallResult();
            if (opt == null) opt = new InstallOptions { CreateDesktopShortcut = true };
            try
            {
                if (string.IsNullOrEmpty(sourceExe) || !File.Exists(sourceExe))
                {
                    res.Error = "找不到当前可执行文件";
                    return res;
                }
                if (string.IsNullOrEmpty(installDir))
                {
                    res.Error = "安装目录不可用";
                    return res;
                }

                Directory.CreateDirectory(installDir);
                string targetExe = Path.Combine(installDir, "GameOptimizer.exe");
                res.InstalledExe = targetExe;

                CleanStaleFiles(installDir);

                // 1) 复制主程序到固定目录（支持旧实例 Windows 原子重命名热替换）
                if (!PathEquals(sourceExe, targetExe))
                {
                    try
                    {
                        File.Copy(sourceExe, targetExe, true);
                    }
                    catch (IOException)
                    {
                        string prev = Path.Combine(installDir, "GameOptimizer.exe.prev." + DateTime.Now.ToString("yyyyMMddHHmmss"));
                        File.Move(targetExe, prev);
                        File.Copy(sourceExe, targetExe, true);
                        res.HotSwapped = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        string prev = Path.Combine(installDir, "GameOptimizer.exe.prev." + DateTime.Now.ToString("yyyyMMddHHmmss"));
                        File.Move(targetExe, prev);
                        File.Copy(sourceExe, targetExe, true);
                        res.HotSwapped = true;
                    }
                }

                // 2) 桌面快捷方式：勾了就建并去重，取消勾选就干净移除
                if (!string.IsNullOrEmpty(desktopDir))
                {
                    if (opt.CreateDesktopShortcut)
                    {
                        string p = EnsureCanonicalShortcut(desktopDir, targetExe, installDir);
                        if (string.IsNullOrEmpty(p)) { res.Error = "创建桌面快捷方式失败"; return res; }
                        res.DesktopShortcutActive = true;
                    }
                    else
                    {
                        RemoveShortcutsTargeting(desktopDir, targetExe);
                        res.DesktopShortcutActive = false;
                    }
                }

                // 3) 开始菜单入口：完全由用户勾选决定
                if (!string.IsNullOrEmpty(startMenuDir))
                {
                    if (opt.CreateStartMenuShortcut)
                    {
                        string p = EnsureCanonicalShortcut(startMenuDir, targetExe, installDir);
                        res.StartMenuShortcutActive = !string.IsNullOrEmpty(p);
                    }
                    else
                    {
                        RemoveShortcutsTargeting(startMenuDir, targetExe);
                        res.StartMenuShortcutActive = false;
                    }
                }

                // 4) 开机静默进托盘 + Windows「已安装的应用」原生自卸载注册（指向 GameOptimizer.exe --uninstall）
                if (touchRegistry)
                {
                    res.AutostartActive = SetAutostart(targetExe, opt.EnableAutostart);
                    res.UninstallRegistered = RegisterUninstallEntry(installDir, targetExe);
                }

                try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
                catch { }

                if (auditLedger != null)
                {
                    try
                    {
                        auditLedger.AppendAudit("INSTALL apply -> " + targetExe
                            + " (desktop=" + res.DesktopShortcutActive
                            + ", startmenu=" + res.StartMenuShortcutActive
                            + ", autostart=" + res.AutostartActive + ")");
                    }
                    catch { }
                }

                res.Ok = true;
                return res;
            }
            catch (Exception ex)
            {
                Diag.Fail("install.apply", ex);
                res.Error = ex.Message;
                return res;
            }
        }

        // ---- 原生一体化自卸载（零外置脚本、零黑框） ----------------------------

        public static UninstallResult UninstallSelf(bool purgeUserData, bool scheduleSelfDelete, Ledger auditLedger)
        {
            return UninstallSelf(DefaultInstallDir(), DefaultDataDir(), DefaultDesktopDir(), DefaultStartMenuProgramsDir(),
                true, purgeUserData, scheduleSelfDelete, auditLedger);
        }

        public static UninstallResult UninstallSelf(
            string installDir,
            string dataDir,
            string desktopDir,
            string startMenuDir,
            bool touchRegistry,
            bool purgeUserData,
            bool scheduleSelfDelete,
            Ledger auditLedger)
        {
            UninstallResult res = new UninstallResult();
            try
            {
                string targetExe = string.IsNullOrEmpty(installDir) ? "" : Path.Combine(installDir, "GameOptimizer.exe");

                if (auditLedger != null && !purgeUserData)
                {
                    try { auditLedger.AppendAudit("UNINSTALL self -> " + installDir + " (purgeData=" + purgeUserData + ")"); }
                    catch { }
                }

                // 1) 停止其它正在运行的 GameOptimizer 实例（保留自身继续完成卸载）
                if (touchRegistry)
                {
                    StopOtherInstances();
                    SetAutostart(targetExe, false);
                    DeleteUninstallEntry();
                    DeleteLegacyScheduledTask();
                }

                // 2) 清理桌面与开始菜单快捷方式
                int removed = 0;
                if (!string.IsNullOrEmpty(desktopDir))
                    removed += RemoveShortcutsTargeting(desktopDir, targetExe);
                if (!string.IsNullOrEmpty(startMenuDir))
                    removed += RemoveShortcutsTargeting(startMenuDir, targetExe);
                res.RemovedShortcuts = removed;

                try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
                catch { }

                // 3) 可选清除本地配置与写前账本目录
                bool needDeferredData = false;
                if (purgeUserData && !string.IsNullOrEmpty(dataDir) && Directory.Exists(dataDir))
                {
                    try
                    {
                        Directory.Delete(dataDir, true);
                        res.RemovedUserData = true;
                    }
                    catch
                    {
                        needDeferredData = true;
                    }
                }

                // 4) 删除安装目录；若当前进程自身就位于安装目录内则延后 1 秒隐藏自删
                bool needDeferredInstall = false;
                if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                {
                    bool deletedNow = false;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            Directory.Delete(installDir, true);
                            deletedNow = true;
                            break;
                        }
                        catch
                        {
                            Thread.Sleep(120);
                        }
                    }
                    if (deletedNow)
                    {
                        res.RemovedInstallDir = true;
                    }
                    else if (scheduleSelfDelete)
                    {
                        needDeferredInstall = true;
                    }
                    else
                    {
                        res.Error = "安装目录被占用，未能立即删除";
                        return res;
                    }
                }

                if ((needDeferredInstall || needDeferredData) && scheduleSelfDelete)
                {
                    ScheduleHiddenCleanup(needDeferredInstall ? installDir : null, needDeferredData ? dataDir : null);
                    res.DeferredSelfDelete = true;
                    if (needDeferredData) res.RemovedUserData = true;
                }

                res.Ok = true;
                return res;
            }
            catch (Exception ex)
            {
                Diag.Fail("install.uninstall", ex);
                res.Error = ex.Message;
                return res;
            }
        }

        private static void StopOtherInstances()
        {
            int selfPid = Process.GetCurrentProcess().Id;
            Process[] list = null;
            try { list = Process.GetProcessesByName("GameOptimizer"); }
            catch { return; }
            if (list == null) return;

            List<Process> others = new List<Process>();
            for (int i = 0; i < list.Length; i++)
            {
                Process p = list[i];
                try
                {
                    if (p.Id == selfPid) { p.Dispose(); continue; }
                    try { p.CloseMainWindow(); }
                    catch { }
                    others.Add(p);
                }
                catch { try { p.Dispose(); } catch { } }
            }

            if (others.Count == 0) return;

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2200)
            {
                bool anyAlive = false;
                for (int i = 0; i < others.Count; i++)
                {
                    try { if (!others[i].HasExited) { anyAlive = true; break; } }
                    catch { }
                }
                if (!anyAlive) break;
                Thread.Sleep(150);
            }

            for (int i = 0; i < others.Count; i++)
            {
                try { if (!others[i].HasExited) others[i].Kill(); }
                catch { }
                finally { try { others[i].Dispose(); } catch { } }
            }
            Thread.Sleep(200);
        }

        private static void ScheduleHiddenCleanup(string installDir, string dataDir)
        {
            try
            {
                // 从 TEMP 启动隐藏 cmd，绝不占用 installDir 句柄，退出后循环重试删除目录
                StringBuilder cmd = new StringBuilder();
                cmd.Append("/d /c \"");
                cmd.Append("for /L %%i in (1,1,8) do (");
                cmd.Append("ping 127.0.0.1 -n 2 >nul");
                if (!string.IsNullOrEmpty(installDir))
                    cmd.Append(" & if exist \"").Append(installDir.Replace("\"", "")).Append("\" rmdir /s /q \"").Append(installDir.Replace("\"", "")).Append("\"");
                if (!string.IsNullOrEmpty(dataDir))
                    cmd.Append(" & if exist \"").Append(dataDir.Replace("\"", "")).Append("\" rmdir /s /q \"").Append(dataDir.Replace("\"", "")).Append("\"");
                cmd.Append(")\"");

                // 注意：cmd /c 直接执行（非批处理文件）循环变量用单 %i
                string args = cmd.ToString().Replace("%%i", "%i");

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = Path.GetTempPath();
                Process.Start(psi);
            }
            catch (Exception ex) { Diag.Fail("install.selfdelete", ex); }
        }

        private static int RemoveShortcutsTargeting(string folder, string targetExe)
        {
            int count = 0;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
            try
            {
                string[] existing = Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < existing.Length; i++)
                {
                    string file = existing[i];
                    string target = ReadShortcutTarget(file);
                    bool match = (!string.IsNullOrEmpty(targetExe) && PathEquals(target, targetExe))
                        || (string.Equals(Path.GetFileName(file), ShortcutFileName, StringComparison.OrdinalIgnoreCase)
                            && target.IndexOf("GameOptimizer", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!match) continue;
                    try { File.Delete(file); count++; }
                    catch { }
                }
            }
            catch (Exception ex) { Diag.Fail("install.lnk.remove", ex); }
            return count;
        }

        private static string EnsureCanonicalShortcut(string folder, string targetExe, string workDir)
        {
            try
            {
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                List<string> matches = new List<string>();
                string[] existing = Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < existing.Length; i++)
                {
                    if (PathEquals(ReadShortcutTarget(existing[i]), targetExe))
                        matches.Add(existing[i]);
                }

                string canonical = Path.Combine(folder, ShortcutFileName);
                string chosen = canonical;
                if (matches.Count == 1)
                {
                    chosen = matches[0];
                }
                else if (matches.Count > 1)
                {
                    chosen = matches[0];
                    for (int i = 0; i < matches.Count; i++)
                    {
                        string nm = Path.GetFileName(matches[i]);
                        if (nm != null && nm.IndexOf("Pro", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            chosen = matches[i];
                            break;
                        }
                    }
                    for (int i = 0; i < matches.Count; i++)
                    {
                        if (!PathEquals(matches[i], chosen))
                        {
                            try { File.Delete(matches[i]); }
                            catch { }
                        }
                    }
                }

                bool ok = CreateShortcut(
                    chosen,
                    targetExe,
                    workDir,
                    "GameOptimizer Pro · Windows 游戏性能控制台",
                    targetExe,
                    0);
                return ok ? chosen : "";
            }
            catch (Exception ex)
            {
                Diag.Fail("install.lnk.ensure", ex);
                return "";
            }
        }

        private static void CleanStaleFiles(string installDir)
        {
            try
            {
                string[] prevs = Directory.GetFiles(installDir, "GameOptimizer.exe.prev*", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < prevs.Length; i++)
                {
                    try { File.Delete(prevs[i]); }
                    catch { }
                }
                // 清理早期版本的外部 Uninstall.ps1，统一收编进主程序 --uninstall
                string oldUninst = Path.Combine(installDir, "Uninstall.ps1");
                if (File.Exists(oldUninst))
                {
                    try { File.Delete(oldUninst); }
                    catch { }
                }
            }
            catch { }
        }

        private static bool SetAutostart(string targetExe, bool enable)
        {
            try
            {
                using (RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunRegSubKey))
                {
                    if (key == null) return false;
                    if (enable && !string.IsNullOrEmpty(targetExe))
                    {
                        key.SetValue(RunValueName, "\"" + targetExe + "\" --tray", RegistryValueKind.String);
                        return true;
                    }
                    if (key.GetValue(RunValueName) != null) key.DeleteValue(RunValueName, false);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("install.autostart", ex);
                return false;
            }
        }

        private static bool RegisterUninstallEntry(string installDir, string targetExe)
        {
            try
            {
                string dispVer = Brand.Rev + ".0.2026.11";
                try
                {
                    FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(targetExe);
                    if (!string.IsNullOrEmpty(fvi.FileVersion)) dispVer = fvi.FileVersion;
                }
                catch { }

                long totalBytes = 0;
                try
                {
                    string[] files = Directory.GetFiles(installDir, "*", SearchOption.TopDirectoryOnly);
                    for (int i = 0; i < files.Length; i++)
                        totalBytes += new FileInfo(files[i]).Length;
                }
                catch { }
                int kb = (int)Math.Max(1, (totalBytes + 1023) / 1024);

                using (RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallRegSubKey))
                {
                    if (key == null) return false;
                    key.SetValue("DisplayName", "GameOptimizer Pro", RegistryValueKind.String);
                    key.SetValue("DisplayVersion", dispVer, RegistryValueKind.String);
                    key.SetValue("Publisher", "GameOptimizer Studio", RegistryValueKind.String);
                    key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                    key.SetValue("DisplayIcon", targetExe, RegistryValueKind.String);
                    key.SetValue("UninstallString", "\"" + targetExe + "\" --uninstall", RegistryValueKind.String);
                    key.SetValue("QuietUninstallString", "\"" + targetExe + "\" --uninstall --silent", RegistryValueKind.String);
                    key.SetValue("EstimatedSize", kb, RegistryValueKind.DWord);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                return true;
            }
            catch (Exception ex)
            {
                Diag.Fail("install.uninstall_reg", ex);
                return false;
            }
        }

        private static void DeleteUninstallEntry()
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallRegSubKey, false); }
            catch { }
        }

        private static void DeleteLegacyScheduledTask()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/Delete /TN GameOptimizerPro /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(1500);
                }
            }
            catch { }
        }
    }
}
