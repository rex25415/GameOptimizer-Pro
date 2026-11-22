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

                // 5) Rev143 更新器离线全链路测试：版本解析 / 域名白名单 / SHA-256 校验与防篡改 / PE 头守卫 / 原子热替换 / 脱敏摘要
                int r1 = AppUpdater.ParseRevNumber("GameOptimizer Pro (Latest · Rev 148)", "latest", "");
                int r2 = AppUpdater.ParseRevNumber("", "v1.45.0", "");
                string fakeJson = "{\"tag_name\":\"latest\",\"name\":\"GameOptimizer Pro (Latest · Rev 148)\",\"html_url\":\"https://github.com/rex25415/GameOptimizer-Pro/releases/tag/latest\",\"assets\":[{\"name\":\"GameOptimizer-Pro-win-x64.exe\",\"browser_download_url\":\"https://github.com/rex25415/GameOptimizer-Pro/releases/download/latest/GameOptimizer-Pro-win-x64.exe\"},{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/rex25415/GameOptimizer-Pro/releases/download/latest/SHA256SUMS.txt\"}]}";
                AppUpdater.UpdateCheckResult parsedOk = AppUpdater.ParseReleaseJson(fakeJson, 143);
                AppUpdater.UpdateCheckResult parsedSame = AppUpdater.ParseReleaseJson(fakeJson, 148);
                string evilJson = "{\"tag_name\":\"latest\",\"name\":\"Rev 199\",\"assets\":[{\"name\":\"GameOptimizer-Pro-win-x64.exe\",\"browser_download_url\":\"https://evil.example.com/payload.exe\"}]}";
                AppUpdater.UpdateCheckResult parsedEvil = AppUpdater.ParseReleaseJson(evilJson, 143);
                Check(r1 == 148 && r2 == 145
                      && parsedOk.Ok && parsedOk.HasUpdate && parsedOk.RemoteRev == 148 && !string.IsNullOrEmpty(parsedOk.Sha256Url)
                      && parsedSame.Ok && !parsedSame.HasUpdate
                      && !parsedEvil.Ok,
                      "Rev143 更新解析与域名守卫：滚动 latest 修订号识别与非官方域名拦截全通过",
                      "r1=" + r1 + " r2=" + r2 + " hasUpd=" + parsedOk.HasUpdate + " blockEvil=" + (!parsedEvil.Ok));

                // 6) SHA-256 校验、防篡改、PE 头守卫与热替换测试
                string updDir = Path.Combine(root, "UpdateTest");
                Directory.CreateDirectory(updDir);
                string curBin = Path.Combine(updDir, "GameOptimizer.exe");
                string goodStaged = Path.Combine(updDir, "good.tmp");
                string badPeStaged = Path.Combine(updDir, "badpe.tmp");
                byte[] origBytes = new byte[140 * 1024];
                origBytes[0] = (byte)'M'; origBytes[1] = (byte)'Z'; origBytes[2] = 1;
                byte[] newBytes = new byte[150 * 1024];
                newBytes[0] = (byte)'M'; newBytes[1] = (byte)'Z'; newBytes[2] = 2;
                byte[] notPeBytes = new byte[140 * 1024];
                notPeBytes[0] = (byte)'N'; notPeBytes[1] = (byte)'O';
                File.WriteAllBytes(curBin, origBytes);
                File.WriteAllBytes(goodStaged, newBytes);
                File.WriteAllBytes(badPeStaged, notPeBytes);

                string goodSha = AppUpdater.ComputeSha256(goodStaged);
                string sumsText = goodSha + "  GameOptimizer-Pro-win-x64.exe\r\n";
                string parsedSha = AppUpdater.ParseExpectedSha256(sumsText, "GameOptimizer-Pro-win-x64.exe");

                AppUpdater.UpdateApplyResult badPeRes = AppUpdater.ApplyStagedFile(badPeStaged, curBin, "");
                AppUpdater.UpdateApplyResult badHashRes = AppUpdater.ApplyStagedFile(goodStaged, curBin, new string('0', 64));
                AppUpdater.UpdateApplyResult goodRes = AppUpdater.ApplyStagedFile(goodStaged, curBin, parsedSha);
                byte[] afterBytes = File.ReadAllBytes(curBin);
                string[] prevBackups = Directory.GetFiles(updDir, "GameOptimizer.exe.prev.*");

                Check(string.Equals(parsedSha, goodSha, StringComparison.OrdinalIgnoreCase)
                      && !badPeRes.Ok && !badHashRes.Ok
                      && goodRes.Ok && goodRes.ShaVerified
                      && afterBytes.Length == newBytes.Length && afterBytes[2] == 2
                      && prevBackups.Length == 1,
                      "Rev143 热更新安全闸门：拦截非 PE 文件与哈希不匹配，校验通过后原子热替换成功",
                      "shaMatch=" + goodRes.ShaVerified + " prev=" + prevBackups.Length);

                // 7) 脱敏环境摘要隐私守卫：绝不包含用户名或本地路径
                Core sumCore = new Core();
                sumCore.DetectHardware();
                string summary = AppUpdater.BuildSanitizedSummary(sumCore);
                string user = Environment.UserName ?? "";
                bool noUserLeak = string.IsNullOrEmpty(user) || summary.IndexOf(user, StringComparison.OrdinalIgnoreCase) < 0;
                bool noPathLeak = summary.IndexOf(":\\", StringComparison.Ordinal) < 0;
                Check(summary.IndexOf("Rev " + Brand.Rev, StringComparison.Ordinal) >= 0
                      && summary.IndexOf("OS:", StringComparison.Ordinal) >= 0
                      && summary.IndexOf("CPU:", StringComparison.Ordinal) >= 0
                      && noUserLeak && noPathLeak,
                      "Rev143 脱敏硬件摘要：包含修订号与硬件口径且零用户名/本地路径泄露",
                      "len=" + summary.Length + " clean=" + (noUserLeak && noPathLeak));
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
