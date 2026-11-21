// ============================================================================
//  SelfTest.Rev141.cs — Rev 141/142 本机一体化安装、自选快捷方式与原生自卸载沙箱自检
//  全部操作严格限定在临时沙箱目录，绝不触碰真实桌面、开始菜单或系统卸载项。
// ============================================================================
using System;
using System.IO;
using System.Text;

namespace Go
{
    public static partial class SelfTest
    {
        private static void TestRev141DesktopInstall()
        {
            string root = Path.Combine(Path.GetTempPath(), "GameOptimizer-Rev142-Test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string dlDir = Path.Combine(root, "Downloads");
                string srcRepoDir = Path.Combine(root, "Repo");
                string srcOutDir = Path.Combine(srcRepoDir, "out");
                string instDir = Path.Combine(root, "Programs", "GameOptimizer");
                string dataDir = Path.Combine(root, "Data");
                string deskDir = Path.Combine(root, "Desktop");
                string smDir = Path.Combine(root, "StartMenu");

                Directory.CreateDirectory(dlDir);
                Directory.CreateDirectory(Path.Combine(srcRepoDir, "src"));
                Directory.CreateDirectory(srcOutDir);
                Directory.CreateDirectory(dataDir);
                Directory.CreateDirectory(deskDir);
                Directory.CreateDirectory(smDir);

                string fakeDlExe = Path.Combine(dlDir, "GameOptimizer-Pro-v1.42.0-win-x64.exe");
                File.WriteAllText(fakeDlExe, "stub-exe-payload-142", Encoding.ASCII);
                File.WriteAllText(Path.Combine(dataDir, "ledger.json"), "{}", Encoding.ASCII);

                string fakeSrcExe = Path.Combine(srcOutDir, "GameOptimizer.exe");
                File.WriteAllText(fakeSrcExe, "stub-src-payload", Encoding.ASCII);
                File.WriteAllText(Path.Combine(srcRepoDir, "build.ps1"), "# stub", Encoding.ASCII);
                File.WriteAllText(Path.Combine(srcRepoDir, "src", "App.cs"), "// stub", Encoding.ASCII);

                // 1) 下载目录探测 vs 源码开发仓探测
                AppInstall.InstallStatus stDl = AppInstall.Probe(fakeDlExe, instDir, dataDir, deskDir, smDir, false);
                AppInstall.InstallStatus stSrc = AppInstall.Probe(fakeSrcExe, instDir, dataDir, deskDir, smDir, false);
                Check(stDl.NeedsInstallPrompt && !stDl.RunningFromSourceTree && !stDl.IsInstalled
                      && stSrc.RunningFromSourceTree && !stSrc.NeedsInstallPrompt,
                      "Rev142 探测：下载便携态识别待安装，源码仓自动静音不打扰",
                      "dlPrompt=" + stDl.NeedsInstallPrompt + " srcPrompt=" + stSrc.NeedsInstallPrompt);

                // 2) 自选快捷方式：仅勾选桌面、不勾开始菜单 + 预置旧重复 .lnk 与旧 Uninstall.ps1 验证收敛清理
                string expectedExe = Path.Combine(instDir, "GameOptimizer.exe");
                Directory.CreateDirectory(instDir);
                File.WriteAllText(expectedExe, "old-stub", Encoding.ASCII);
                File.WriteAllText(Path.Combine(instDir, "Uninstall.ps1"), "# legacy", Encoding.ASCII);
                AppInstall.CreateShortcut(Path.Combine(deskDir, "OldShortcut.lnk"), expectedExe, instDir, "dup", expectedExe, 0);
                AppInstall.CreateShortcut(Path.Combine(deskDir, AppInstall.ShortcutFileName), expectedExe, instDir, "pro", expectedExe, 0);

                AppInstall.InstallOptions optDeskOnly = new AppInstall.InstallOptions();
                optDeskOnly.CreateDesktopShortcut = true;
                optDeskOnly.CreateStartMenuShortcut = false;
                AppInstall.InstallResult res1 = AppInstall.ApplyInstall(fakeDlExe, instDir, deskDir, smDir, optDeskOnly, false, null);

                string[] deskLnks1 = Directory.GetFiles(deskDir, "*.lnk");
                string[] smLnks1 = Directory.GetFiles(smDir, "*.lnk");
                bool legacyUninstCleaned = !File.Exists(Path.Combine(instDir, "Uninstall.ps1"));
                bool payloadUpdated = File.ReadAllText(expectedExe, Encoding.ASCII) == "stub-exe-payload-142";
                string deskTarget1 = deskLnks1.Length == 1 ? AppInstall.ReadShortcutTarget(deskLnks1[0]) : "";

                Check(res1.Ok && payloadUpdated && legacyUninstCleaned
                      && deskLnks1.Length == 1 && smLnks1.Length == 0
                      && string.Equals(Path.GetFullPath(deskTarget1), Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase),
                      "Rev142 自选安装：仅建桌面不塞开始菜单、旧重复 .lnk 去重并清除旧外置 Uninstall.ps1",
                      "desk=" + deskLnks1.Length + " sm=" + smLnks1.Length + " cleanLegacy=" + legacyUninstCleaned);

                // 3) 反向切换勾选：取消桌面快捷方式、勾选开始菜单入口 => 桌面图标被移除、开始菜单新建
                AppInstall.InstallOptions optSmOnly = new AppInstall.InstallOptions();
                optSmOnly.CreateDesktopShortcut = false;
                optSmOnly.CreateStartMenuShortcut = true;
                AppInstall.InstallResult res2 = AppInstall.ApplyInstall(fakeDlExe, instDir, deskDir, smDir, optSmOnly, false, null);
                AppInstall.InstallStatus stMid = AppInstall.Probe(expectedExe, instDir, dataDir, deskDir, smDir, false);
                Check(res2.Ok && !res2.DesktopShortcutActive && res2.StartMenuShortcutActive
                      && !stMid.HasDesktopShortcut && stMid.HasStartMenuShortcut && stMid.RunningFromInstallDir,
                      "Rev142 勾选同步：取消桌面快捷方式自动移除，勾选开始菜单实时创建",
                      "desk=" + stMid.HasDesktopShortcut + " sm=" + stMid.HasStartMenuShortcut);

                // 4) 原生内置卸载（保留数据 vs 彻底清除数据）+ --uninstall CLI 参数解析
                AppInstall.UninstallResult uKeep = AppInstall.UninstallSelf(instDir, dataDir, deskDir, smDir, false, false, false, null);
                bool dataPreserved = Directory.Exists(dataDir) && File.Exists(Path.Combine(dataDir, "ledger.json"));
                bool instGone = !Directory.Exists(instDir);
                bool smGone = Directory.GetFiles(smDir, "*.lnk").Length == 0;

                // 再次测试 purgeUserData=true
                Directory.CreateDirectory(instDir);
                File.WriteAllText(expectedExe, "stub", Encoding.ASCII);
                AppInstall.UninstallResult uPurge = AppInstall.UninstallSelf(instDir, dataDir, deskDir, smDir, false, true, false, null);
                bool dataPurged = !Directory.Exists(dataDir);

                Go.Daemon.StartupOptions cliOpt = Go.Daemon.StartupOptions.Parse(new string[] { "--uninstall", "--silent", "--purge-data" });
                Check(uKeep.Ok && instGone && smGone && dataPreserved && uPurge.Ok && dataPurged
                      && cliOpt.Uninstall && cliOpt.Silent && cliOpt.PurgeUserData,
                      "Rev142 原生自卸载：清理快捷方式与安装目录、默认保留账本、可选清空数据且 --uninstall 参数解析正确",
                      "keepOk=" + (uKeep.Ok && dataPreserved) + " purgeOk=" + (uPurge.Ok && dataPurged));
            }
            catch (Exception ex)
            {
                Check(false, "Rev142 一体化安装/卸载沙箱自检异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }
    }
}
