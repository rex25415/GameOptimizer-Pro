// ============================================================================
//  SelfTest.Rev141.cs — Rev 141 DESKTOP CITIZEN 本机安装与原生快捷方式沙箱自检
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
            string root = Path.Combine(Path.GetTempPath(), "GameOptimizer-Rev141-Test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string dlDir = Path.Combine(root, "Downloads");
                string srcRepoDir = Path.Combine(root, "Repo");
                string srcOutDir = Path.Combine(srcRepoDir, "out");
                string instDir = Path.Combine(root, "Programs", "GameOptimizer");
                string deskDir = Path.Combine(root, "Desktop");
                string smDir = Path.Combine(root, "StartMenu");

                Directory.CreateDirectory(dlDir);
                Directory.CreateDirectory(Path.Combine(srcRepoDir, "src"));
                Directory.CreateDirectory(srcOutDir);
                Directory.CreateDirectory(deskDir);
                Directory.CreateDirectory(smDir);

                string fakeDlExe = Path.Combine(dlDir, "GameOptimizer-Pro-v1.41.0-win-x64.exe");
                File.WriteAllText(fakeDlExe, "stub-exe-payload", Encoding.ASCII);

                string fakeSrcExe = Path.Combine(srcOutDir, "GameOptimizer.exe");
                File.WriteAllText(fakeSrcExe, "stub-src-payload", Encoding.ASCII);
                File.WriteAllText(Path.Combine(srcRepoDir, "build.ps1"), "# stub", Encoding.ASCII);
                File.WriteAllText(Path.Combine(srcRepoDir, "src", "App.cs"), "// stub", Encoding.ASCII);

                // 1) 普通下载路径探测：非安装目录 + 非源码仓 + 无桌面快捷方式 => 需要温和提示
                AppInstall.InstallStatus stDl = AppInstall.Probe(fakeDlExe, instDir, deskDir, smDir);
                Check(stDl.NeedsInstallPrompt && !stDl.RunningFromSourceTree && !stDl.RunningFromInstallDir && !stDl.HasDesktopShortcut,
                      "Rev141 探测：下载目录运行单文件 exe 时识别为待安装状态",
                      "prompt=" + stDl.NeedsInstallPrompt + " src=" + stDl.RunningFromSourceTree);

                // 2) 源码开发仓探测：含 build.ps1 + src\App.cs => 开发者自编译零打扰
                AppInstall.InstallStatus stSrc = AppInstall.Probe(fakeSrcExe, instDir, deskDir, smDir);
                Check(stSrc.RunningFromSourceTree && !stSrc.NeedsInstallPrompt,
                      "Rev141 探测：源码仓 out\\GameOptimizer.exe 自动识别并静默不弹窗",
                      "src=" + stSrc.RunningFromSourceTree + " prompt=" + stSrc.NeedsInstallPrompt);

                // 3) 沙箱一键安装 + 去重验证（预置一个指向目标 exe 的旧重复 .lnk，验证被去重为 1 个）
                string expectedExe = Path.Combine(instDir, "GameOptimizer.exe");
                Directory.CreateDirectory(instDir);
                File.WriteAllText(expectedExe, "old-stub", Encoding.ASCII);
                string dupLnk = Path.Combine(deskDir, "OldShortcut.lnk");
                bool preLnkOk = AppInstall.CreateShortcut(dupLnk, expectedExe, instDir, "dup", expectedExe, 0);
                string proLnk = Path.Combine(deskDir, AppInstall.ShortcutFileName);
                bool preLnkOk2 = AppInstall.CreateShortcut(proLnk, expectedExe, instDir, "pro", expectedExe, 0);

                AppInstall.InstallResult res = AppInstall.InstallSelf(fakeDlExe, instDir, deskDir, smDir, false, null);
                string deskTarget = AppInstall.ReadShortcutTarget(res.DesktopShortcut);
                string smTarget = AppInstall.ReadShortcutTarget(res.StartMenuShortcut);
                string[] deskLnks = Directory.GetFiles(deskDir, "*.lnk");
                bool uninstWritten = File.Exists(Path.Combine(instDir, "Uninstall.ps1"));
                bool payloadUpdated = File.ReadAllText(expectedExe, Encoding.ASCII) == "stub-exe-payload";

                Check(preLnkOk && preLnkOk2 && res.Ok && payloadUpdated && uninstWritten
                      && deskLnks.Length == 1
                      && string.Equals(Path.GetFullPath(deskTarget), Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase)
                      && string.Equals(Path.GetFullPath(smTarget), Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase),
                      "Rev141 原生安装：复制主程序/写 Uninstall.ps1/原生 IShellLinkW 创建与去重桌面及开始菜单快捷方式全通过",
                      "ok=" + res.Ok + " lnks=" + deskLnks.Length + " desk=" + Path.GetFileName(deskTarget) + " sm=" + Path.GetFileName(smTarget));

                // 4) 安装完成后再次探测：下载路径不再提示，安装路径识别为 RunningFromInstallDir
                AppInstall.InstallStatus stAfterDl = AppInstall.Probe(fakeDlExe, instDir, deskDir, smDir);
                AppInstall.InstallStatus stFromInst = AppInstall.Probe(expectedExe, instDir, deskDir, smDir);
                Check(stAfterDl.FullyInstalled && !stAfterDl.NeedsInstallPrompt
                      && stFromInst.RunningFromInstallDir && !stFromInst.NeedsInstallPrompt
                      && stFromInst.HasDesktopShortcut && stFromInst.HasStartMenuShortcut,
                      "Rev141 安装后探测：桌面与开始菜单快捷方式均被正确回读且引导条自动收敛",
                      "fully=" + stAfterDl.FullyInstalled + " fromInst=" + stFromInst.RunningFromInstallDir);
            }
            catch (Exception ex)
            {
                Check(false, "Rev141 DESKTOP CITIZEN 沙箱自检异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }
    }
}
