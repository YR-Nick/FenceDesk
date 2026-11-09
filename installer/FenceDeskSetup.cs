using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("FenceDesk Setup")]
[assembly: AssemblyDescription("FenceDesk 桌面围栏 安装程序")]
[assembly: AssemblyCompany("FenceDesk")]
[assembly: AssemblyProduct("FenceDesk")]
[assembly: AssemblyVersion("1.3.54.0")]
[assembly: AssemblyFileVersion("1.3.54.0")]

namespace FenceDeskSetup
{
    internal static class Theme
    {
        public static readonly Color Window = Color.FromArgb(0x12, 0x15, 0x1C);
        public static readonly Color Panel = Color.FromArgb(0x1A, 0x1F, 0x29);
        public static readonly Color Stroke = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
        public static readonly Color Text = Color.FromArgb(0xF4, 0xF6, 0xFA);
        public static readonly Color TextMuted = Color.FromArgb(0x8B, 0x96, 0xA9);
        public static readonly Color Accent = Color.FromArgb(0x33, 0xD6, 0xC0);
        public static readonly Color AccentDark = Color.FromArgb(0x0A, 0x1A, 0x18);
        public static readonly Color Danger = Color.FromArgb(0xFF, 0x7D, 0x8E);
        public static readonly Color Track = Color.FromArgb(0x24, 0x2B, 0x38);
    }

    /// <summary>细长进度条，深色主题下比系统 ProgressBar 更协调。</summary>
    internal sealed class SlimProgress : Control
    {
        private double _value;

        public SlimProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 8;
        }

        public double Value
        {
            get { return _value; }
            set
            {
                _value = Math.Max(0, Math.Min(1, value));
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var brush = new SolidBrush(Theme.Track)) FillRound(g, brush, track);
            if (_value <= 0) return;

            int w = Math.Max(4, (int)Math.Round(track.Width * _value));
            Rectangle fill = new Rectangle(0, 0, w, track.Height);
            using (var brush = new SolidBrush(Theme.Accent)) FillRound(g, brush, fill);
        }

        private static void FillRound(Graphics g, Brush brush, Rectangle rect)
        {
            int radius = Math.Max(1, rect.Height);
            using (var path = new GraphicsPath())
            {
                path.AddArc(rect.X, rect.Y, radius, radius, 180, 90);
                path.AddArc(rect.Right - radius, rect.Y, radius, radius, 270, 90);
                path.AddArc(rect.Right - radius, rect.Bottom - radius, radius, radius, 0, 90);
                path.AddArc(rect.X, rect.Bottom - radius, radius, radius, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }
    }

    internal static class InstallerCore
    {
        public const string Version = "1.3.54";
        public const string RunValueName = "FenceDesk";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FenceDesk";
        public const string PayloadResource = "FenceDeskPayload.zip";

        public static string DefaultTarget
        {
            get
            {
                if (MachineWide)
                {
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "FenceDesk");
                }

                // 拿不到真实的管理员写权限时按当前用户安装，不写系统目录；
                // 再挑一个确实可写的目录，避免装到一半才报"访问被拒绝"。
                string[] candidates =
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs", "FenceDesk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FenceDesk"),
                };

                foreach (string candidate in candidates)
                {
                    if (CanWriteTo(candidate)) return candidate;
                }

                return candidates[0];
            }
        }

        /// <summary>
        /// 当前进程是否真的可以写 Program Files。
        /// 只判断 IsInRole(Administrator) 是不够的：UAC 过滤令牌下它也会返回 true，
        /// 但写入 Program Files 仍然被拒绝（用户就是这样踩到"访问被拒绝"的）。
        /// </summary>
        public static bool MachineWide
        {
            get
            {
                if (!IsAdministrator()) return false;

                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (string.IsNullOrEmpty(programFiles)) return false;
                return CanWriteTo(programFiles);
            }
        }

        public static bool CanWriteTo(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".fencedesk-write-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string SourceDirectory
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static string GetInstalledLocation()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(UninstallKey))
                {
                    if (key == null) return null;
                    string location = key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(location) && Directory.Exists(location)) return location;
                    string uninstall = key.GetValue("UninstallString") as string;
                    if (!string.IsNullOrEmpty(uninstall))
                    {
                        string path = uninstall.Trim('"');
                        return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        public static string GetInstalledVersion()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(UninstallKey))
                {
                    return key == null ? null : key.GetValue("DisplayVersion") as string;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把内嵌的程序文件包直接解到安装目录。
        /// 不经过 %TEMP%：有些机器（安全策略、沙箱、杀软）不允许在临时目录创建文件，
        /// 之前"先解包到 TEMP 再复制"的写法会直接失败。
        /// </summary>
        private static void ExtractPayloadTo(string target, Action<string, double> progress)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(PayloadResource))
            {
                if (stream == null)
                {
                    // 开发版本：安装包旁边就是程序文件。
                    if (!File.Exists(Path.Combine(SourceDirectory, "FenceDesk.exe")))
                    {
                        throw new InvalidOperationException(
                            "安装包缺少内嵌的程序文件，且同目录下没有 FenceDesk.exe。\r\n" +
                            "请重新下载完整的 FenceDesk 安装包。");
                    }

                    ScanDirectory(SourceDirectory, target, string.Empty, progress, 0, 1);
                    return;
                }

                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    int total = Math.Max(1, zip.Entries.Count);
                    int done = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        done++;
                        string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        if (ShouldSkip(name)) continue;

                        string destination = Path.Combine(target, name);
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }

                        string parent = Path.GetDirectoryName(destination);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        try
                        {
                            entry.ExtractToFile(destination, true);
                        }
                        catch (Exception ex)
                        {
                            throw new IOException(
                                "写入 " + destination + " 失败：" + ex.Message +
                                "\r\n（通常是文件被占用或当前权限无法写入该目录）", ex);
                        }
                        Report(progress, "正在安装 " + entry.Name, 0.1 + 0.6 * done / total);
                    }
                }
            }
        }

        private static void ScanDirectory(string source, string target, string prefix,
            Action<string, double> progress, int done, int total)
        {
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (ShouldSkip(name)) continue;
                string destination = Path.Combine(target, prefix, name);
                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(file, destination, true);
                Report(progress, "正在安装 " + name, 0.1 + 0.6 * ++done / Math.Max(1, total + done));
            }

            foreach (string directory in Directory.GetDirectories(source))
            {
                ScanDirectory(directory, target, Path.Combine(prefix, Path.GetFileName(directory)),
                    progress, done, total);
            }
        }

        private static bool ShouldSkip(string name)
        {
            string file = Path.GetFileName(name);
            if (string.IsNullOrEmpty(file)) return false;
            if (string.Equals(file, "FenceDeskSetup.exe", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(file, "Uninstall.exe", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(file, "QUICKSTART.txt", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.StartsWith("Install-FenceDesk", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.StartsWith("Uninstall-FenceDesk", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(file, "FenceDeskSetup.log", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static void Install(string target, bool createDesktop, bool launchApp, Action<string, double> progress)
        {
            string previous = GetInstalledLocation();

            Report(progress, "正在停止正在运行的 FenceDesk…", 0.04);
            if (!StopRunningApp())
            {
                throw new InvalidOperationException(
                    "FenceDesk 正在运行，而且当前权限无法结束它（它可能是以管理员身份启动的）。\r\n" +
                    "请先退出 FenceDesk（托盘图标右键 → 退出 FenceDesk），再运行安装程序。");
            }

            if (!string.IsNullOrEmpty(previous) && !PathsEqual(previous, target))
            {
                Report(progress, "正在迁移旧安装 " + previous, 0.06);
            }

            Report(progress, "正在准备安装目录…", 0.08);
            Directory.CreateDirectory(target);

            Report(progress, "正在释放程序文件…", 0.1);
            ExtractPayloadTo(target, progress);

            string installedExe = Path.Combine(target, "FenceDesk.exe");
            if (!File.Exists(installedExe))
            {
                throw new IOException("FenceDesk.exe 没有被写入安装目录，请检查磁盘权限。");
            }

            Report(progress, "正在注册卸载程序…", 0.74);
            File.Copy(Application.ExecutablePath, Path.Combine(target, "Uninstall.exe"), true);

            Report(progress, "正在写入开机启动项…", 0.8);
            WriteRunEntry(installedExe);

            Report(progress, "正在登记到“应用和功能”…", 0.84);
            WriteUninstallEntry(target, installedExe);

            Report(progress, "正在创建快捷方式…", 0.9);
            try
            {
                CreateShortcuts(target, installedExe, createDesktop);
            }
            catch (Exception ex)
            {
                Log("创建快捷方式失败：" + ex);
            }

            if (!string.IsNullOrEmpty(previous) && !PathsEqual(previous, target))
            {
                Report(progress, "正在清理旧安装目录…", 0.95);
                TryRemoveOldInstall(previous);
            }

            Report(progress, "安装完成", 1.0);
            Log("已安装到 " + target);

            if (launchApp)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = installedExe,
                        UseShellExecute = true,
                        WorkingDirectory = target,
                    });
                }
                catch (Exception ex)
                {
                    Log("启动失败：" + ex);
                }
            }
        }

        private static void Report(Action<string, double> progress, string message, double value)
        {
            if (progress != null) progress(message, value);
        }

        public static void Uninstall(string installDir, bool silent, Action<string, double> progress)
        {
            if (!silent)
            {
                DialogResult result = MessageBox.Show(
                    "确定要卸载 FenceDesk 吗？\r\n\r\n" +
                    "用户布局（%APPDATA%\\FenceDesk）会保留，桌面图标会自动恢复。",
                    "卸载 FenceDesk", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.Yes) return;
            }

            Report(progress, "正在停止 FenceDesk…", 0.1);
            StopRunningApp();

            // 卸载前一定要把被接管的桌面图标放出来，否则会留下空白桌面。
            Report(progress, "正在恢复桌面图标…", 0.25);
            try
            {
                string exe = Path.Combine(installDir, "FenceDesk.exe");
                if (File.Exists(exe))
                {
                    using (Process process = Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "--restore-desktop",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }))
                    {
                        if (process != null) process.WaitForExit(4000);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("恢复桌面图标失败：" + ex);
            }

            Report(progress, "正在删除启动项…", 0.4);
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null) key.DeleteValue(RunValueName, false);
                }

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null) key.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex)
            {
                Log("删除启动项失败：" + ex);
            }

            Report(progress, "正在移除卸载登记…", 0.5);
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
            }
            catch (Exception ex)
            {
                Log("删除卸载登记失败：" + ex);
            }

            Report(progress, "正在删除快捷方式…", 0.6);
            try
            {
                string startMenu = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "FenceDesk");
                if (Directory.Exists(startMenu)) Directory.Delete(startMenu, true);

                string desktopLink = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "FenceDesk.lnk");
                if (File.Exists(desktopLink)) File.Delete(desktopLink);
            }
            catch (Exception ex)
            {
                Log("删除快捷方式失败：" + ex);
            }

            Report(progress, "正在删除程序文件…", 0.75);
            ScheduleDirectoryRemoval(installDir);
            Report(progress, "卸载完成", 1.0);
            Log("已卸载 " + installDir);
        }

        private static void TryRemoveOldInstall(string previous)
        {
            try
            {
                if (!Directory.Exists(previous)) return;
                // 只清理确实属于 FenceDesk 的目录，避免误删用户自己选的位置。
                if (!File.Exists(Path.Combine(previous, "FenceDesk.exe"))) return;
                if (PathsEqual(previous, SourceDirectory)) return;

                foreach (string file in Directory.GetFiles(previous)) File.Delete(file);
                foreach (string dir in Directory.GetDirectories(previous)) Directory.Delete(dir, true);
                Directory.Delete(previous, true);
                Log("已清理旧安装目录 " + previous);
            }
            catch (Exception ex)
            {
                Log("清理旧安装目录失败：" + ex);
            }
        }

        /// <summary>结束正在运行的 FenceDesk；返回是否全部结束（高权限实例可能杀不掉）。</summary>
        private static bool StopRunningApp()
        {
            foreach (Process process in Process.GetProcessesByName("FenceDesk"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch
                {
                    // 可能是以管理员身份启动的实例，普通权限结束不了
                }
            }

            try
            {
                return Process.GetProcessesByName("FenceDesk").Length == 0;
            }
            catch
            {
                return true;
            }
        }

        private static void WriteRunEntry(string installedExe)
        {
            // 优先写机器级启动项（所有用户）；没有管理员权限时退回当前用户，
            // 保证即使用户拒绝了 UAC，开机自启依然可用。
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        key.SetValue(RunValueName, "\"" + installedExe + "\" --autostart", RegistryValueKind.String);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("写入 HKLM 启动项失败，改为当前用户：" + ex.Message);
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (key == null) throw new IOException("无法写入启动项（HKLM 与 HKCU 都不可用）。");
                key.SetValue(RunValueName, "\"" + installedExe + "\" --autostart", RegistryValueKind.String);
            }
        }

        private static void WriteUninstallEntry(string installDir, string installedExe)
        {
            string uninstallCommand = "\"" + Path.Combine(installDir, "Uninstall.exe") + "\" --uninstall";
            RegistryKey root = TryOpenUninstallRoot();
            using (RegistryKey key = root.CreateSubKey(UninstallKey))
            {
                if (key == null) throw new IOException("无法写入卸载登记。");
                key.SetValue("DisplayName", "FenceDesk 桌面围栏", RegistryValueKind.String);
                key.SetValue("DisplayVersion", Version, RegistryValueKind.String);
                key.SetValue("Publisher", "FenceDesk", RegistryValueKind.String);
                key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                key.SetValue("DisplayIcon", installedExe + ",0", RegistryValueKind.String);
                key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", EstimateSize(installDir) / 1024, RegistryValueKind.DWord);
            }
        }

        /// <summary>有管理员权限就写系统级卸载登记，否则写在当前用户下。</summary>
        private static RegistryKey TryOpenUninstallRoot()
        {
            if (MachineWide)
            {
                try
                {
                    Registry.LocalMachine.CreateSubKey(UninstallKey).Close();
                    return Registry.LocalMachine;
                }
                catch (Exception ex)
                {
                    Log("HKLM 卸载登记不可写，改为当前用户：" + ex.Message);
                }
            }

            return Registry.CurrentUser;
        }

        private static int EstimateSize(string dir)
        {
            try
            {
                long total = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                return total > int.MaxValue ? int.MaxValue : (int)total;
            }
            catch
            {
                return 0;
            }
        }

        private static void CreateShortcuts(string installDir, string installedExe, bool createDesktop)
        {
            // 有管理员权限就建到"所有用户"菜单，否则建到当前用户菜单。
            string programsRoot = MachineWide
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                : Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string menuFolder = Path.Combine(programsRoot, "FenceDesk");
            Directory.CreateDirectory(menuFolder);

            CreateShortcut(Path.Combine(menuFolder, "FenceDesk.lnk"), installedExe, installDir, string.Empty,
                "FenceDesk 桌面围栏");
            // 应急用：桌面图标被隐藏、围栏又没起来时，从开始菜单一键恢复，不用记命令行。
            CreateShortcut(Path.Combine(menuFolder, "恢复桌面图标.lnk"), installedExe, installDir,
                "--restore-desktop", "恢复被 FenceDesk 隐藏的系统桌面图标");
            CreateShortcut(Path.Combine(menuFolder, "卸载 FenceDesk.lnk"),
                Path.Combine(installDir, "Uninstall.exe"), installDir, "--uninstall", "卸载 FenceDesk");

            if (createDesktop)
            {
                string desktopRoot = MachineWide
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                    : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktopRoot, "FenceDesk.lnk"), installedExe, installDir, string.Empty,
                    "FenceDesk 桌面围栏");
            }
        }

        private static void CreateShortcut(string linkPath, string target, string workingDir, string arguments,
            string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用。");

            object shell = Activator.CreateInstance(shellType);
            try
            {
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { linkPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
                    new object[] { target });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                    new object[] { workingDir });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut,
                    new object[] { arguments });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                    new object[] { target + ",0" });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                    new object[] { description });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                Marshal.ReleaseComObject(shortcut);
            }
            finally
            {
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        private static void ScheduleDirectoryRemoval(string installDir)
        {
            try
            {
                string quoted = installDir.Replace("'", "''");
                string script =
                    "for ($i = 0; $i -lt 30; $i++) { " +
                    "  if (Test-Path -LiteralPath '" + quoted + "') { " +
                    "    Remove-Item -LiteralPath '" + quoted + "' -Recurse -Force -ErrorAction SilentlyContinue " +
                    "  } " +
                    "  if (-not (Test-Path -LiteralPath '" + quoted + "')) { break } " +
                    "  Start-Sleep -Milliseconds 400 " +
                    "}";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"" + script + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex)
            {
                Log("安排删除目录失败：" + ex);
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }

            foreach (string directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\'),
                    Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + "\r\n";
            string[] targets =
            {
                Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "FenceDeskSetup.log"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "FenceDesk", "FenceDeskSetup.log"),
                Path.Combine(Path.GetTempPath(), "FenceDeskSetup.log"),
            };

            foreach (string path in targets)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path, line);
                    return;
                }
                catch
                {
                    // 换下一个位置
                }
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox _pathBox;
        private readonly CheckBox _desktopCheck;
        private readonly CheckBox _launchCheck;
        private readonly Label _status;
        private readonly Label _hint;
        private readonly SlimProgress _progress;
        private readonly Button _primary;
        private readonly Button _cancel;
        private readonly Panel _header;
        private bool _installing;
        private bool _finished;

        public SetupForm()
        {
            Text = "FenceDesk 安装程序";
            ClientSize = new Size(660, 400);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = new Font("Microsoft YaHei UI", 9F);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _header = new Panel();
            _header.Dock = DockStyle.Top;
            _header.Height = 92;
            _header.BackColor = Color.FromArgb(0x0D, 0x11, 0x18);
            _header.Paint += HeaderPaint;
            Controls.Add(_header);

            Label version = new Label();
            version.Text = "v" + InstallerCore.Version;
            version.ForeColor = Theme.Accent;
            version.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            version.AutoSize = true;
            version.Location = new Point(566, 28);
            _header.Controls.Add(version);

            Label title = new Label();
            title.Text = "FenceDesk 桌面围栏";
            title.ForeColor = Theme.Text;
            title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(28, 20);
            _header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "安装到本机，登录 Windows 时自动启动，并接管桌面图标";
            subtitle.ForeColor = Theme.TextMuted;
            subtitle.Font = new Font("Microsoft YaHei UI", 9F);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(30, 54);
            _header.Controls.Add(subtitle);

            Label pathLabel = new Label();
            pathLabel.Text = "安装位置";
            pathLabel.ForeColor = Theme.TextMuted;
            pathLabel.AutoSize = true;
            pathLabel.Location = new Point(30, 112);
            Controls.Add(pathLabel);

            _pathBox = new TextBox();
            _pathBox.Text = InstallerCore.DefaultTarget;
            _pathBox.Location = new Point(30, 136);
            _pathBox.Width = 486;
            _pathBox.BorderStyle = BorderStyle.FixedSingle;
            _pathBox.BackColor = Theme.Panel;
            _pathBox.ForeColor = Theme.Text;
            Controls.Add(_pathBox);

            Button browse = new Button();
            browse.Text = "浏览…";
            browse.Location = new Point(524, 134);
            browse.Width = 104;
            browse.Height = 28;
            StyleGhost(browse);
            browse.Click += BrowseClick;
            Controls.Add(browse);

            _hint = new Label();
            _hint.AutoSize = false;
            _hint.Location = new Point(30, 172);
            _hint.Width = 598;
            _hint.Height = 36;
            _hint.ForeColor = Theme.TextMuted;
            _hint.Font = new Font("Microsoft YaHei UI", 8.5F);
            Controls.Add(_hint);

            string installed = InstallerCore.GetInstalledLocation();
            if (!InstallerCore.MachineWide)
            {
                _hint.Text = "当前没有管理员权限，将安装到当前用户目录（同样会开机自启）。\r\n" +
                             "如果你已经把它装在 C:\\Program Files，请先退出 FenceDesk，" +
                             "再右键安装包选“以管理员身份运行”。";
                _hint.ForeColor = Color.FromArgb(0xFF, 0xB8, 0x6B);
            }
            else if (!string.IsNullOrEmpty(installed) &&
                !installed.TrimEnd('\\').Equals(InstallerCore.DefaultTarget.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                string installedVersion = InstallerCore.GetInstalledVersion();
                _hint.Text = "检测到已安装：" + installed + (string.IsNullOrEmpty(installedVersion)
                    ? ""
                    : "（v" + installedVersion + "）") + "\r\n安装到新位置后，旧目录会被自动清理。";
                _hint.ForeColor = Color.FromArgb(0xFF, 0xB8, 0x6B);
            }
            else if (!string.IsNullOrEmpty(installed))
            {
                _hint.Text = "已安装 v" + (InstallerCore.GetInstalledVersion() ?? "?") + "，本次将升级到 v" +
                             InstallerCore.Version + "。";
            }
            else
            {
                _hint.Text = "程序文件、开始菜单快捷方式和卸载入口都会安装到上面的目录。";
            }

            _desktopCheck = new CheckBox();
            _desktopCheck.Text = InstallerCore.MachineWide ? "创建桌面快捷方式（所有用户）" : "创建桌面快捷方式";
            _desktopCheck.Checked = true;
            _desktopCheck.AutoSize = true;
            _desktopCheck.ForeColor = Theme.Text;
            _desktopCheck.Location = new Point(32, 214);
            Controls.Add(_desktopCheck);

            _launchCheck = new CheckBox();
            _launchCheck.Text = "安装完成后启动 FenceDesk";
            _launchCheck.Checked = true;
            _launchCheck.AutoSize = true;
            _launchCheck.ForeColor = Theme.Text;
            _launchCheck.Location = new Point(32, 242);
            Controls.Add(_launchCheck);

            Label note = new Label();
            note.Text = "安装需要管理员权限（写入系统启动项和卸载登记），日常运行不需要。";
            note.ForeColor = Color.FromArgb(0x72, 0x7D, 0x8F);
            note.AutoSize = true;
            note.Location = new Point(32, 276);
            Controls.Add(note);

            _progress = new SlimProgress();
            _progress.Location = new Point(30, 312);
            _progress.Width = 598;
            _progress.Visible = false;
            Controls.Add(_progress);

            _status = new Label();
            _status.Text = "准备安装";
            _status.ForeColor = Theme.TextMuted;
            _status.AutoSize = true;
            _status.Location = new Point(30, 326);
            Controls.Add(_status);

            _primary = new Button();
            _primary.Text = "安装";
            _primary.Location = new Point(418, 352);
            _primary.Width = 96;
            _primary.Height = 32;
            StyleAccent(_primary);
            _primary.Click += PrimaryClick;
            Controls.Add(_primary);

            _cancel = new Button();
            _cancel.Text = "取消";
            _cancel.Location = new Point(524, 352);
            _cancel.Width = 104;
            _cancel.Height = 32;
            StyleGhost(_cancel);
            _cancel.Click += CancelClick;
            Controls.Add(_cancel);

            AcceptButton = _primary;
            CancelButton = _cancel;
        }

        private static void StyleAccent(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Theme.Accent;
            button.ForeColor = Theme.AccentDark;
            button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
        }

        private static void StyleGhost(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Theme.Stroke;
            button.BackColor = Color.FromArgb(0x16, 0x1B, 0x24);
            button.ForeColor = Theme.TextMuted;
            button.Cursor = Cursors.Hand;
        }

        private void HeaderPaint(object sender, PaintEventArgs e)
        {
            using (var pen = new Pen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)))
            {
                e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
            }

            using (var brush = new LinearGradientBrush(
                       new Rectangle(_header.Width - 220, -40, 220, 150),
                       Color.FromArgb(0x40, 0x33, 0xD6, 0xC0), Color.Transparent,
                       LinearGradientMode.ForwardDiagonal))
            {
                e.Graphics.FillRectangle(brush, _header.Width - 220, 0, 220, _header.Height);
            }
        }

        private void BrowseClick(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 FenceDesk 安装位置";
                dialog.SelectedPath = _pathBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) _pathBox.Text = dialog.SelectedPath;
            }
        }

        private void PrimaryClick(object sender, EventArgs e)
        {
            if (_finished)
            {
                Close();
                return;
            }

            if (_installing) return;

            string target = _pathBox.Text.Trim();
            if (target.Length == 0)
            {
                MessageBox.Show(this, "请选择安装位置。", "FenceDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            StartInstall(target);
        }

        private void StartInstall(string target)
        {
            _installing = true;
            _primary.Enabled = false;
            _cancel.Enabled = false;
            _pathBox.Enabled = false;
            _desktopCheck.Enabled = false;
            _launchCheck.Enabled = false;
            _progress.Visible = true;
            _progress.Value = 0;
            _status.ForeColor = Theme.TextMuted;

            var worker = new Thread(() =>
            {
                try
                {
                    InstallerCore.Install(target, _desktopCheck.Checked, _launchCheck.Checked,
                        (message, value) => BeginInvoke(new Action(() => UpdateProgress(message, value))));
                    BeginInvoke(new Action(FinishInstall));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() => FailInstall(ex)));
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UpdateProgress(string message, double value)
        {
            if (IsDisposed) return;
            if (value >= 0) _progress.Value = value;
            _status.Text = message;
        }

        private void FinishInstall()
        {
            _installing = false;
            _finished = true;
            _progress.Value = 1;
            _status.Text = "安装完成，已设置登录时自动启动";
            _status.ForeColor = Theme.Accent;
            _primary.Text = "完成";
            _primary.Enabled = true;
            _cancel.Text = "打开安装目录";
        }

        private void FailInstall(Exception ex)
        {
            _installing = false;
            _progress.Visible = false;
            _primary.Enabled = true;
            _cancel.Enabled = true;
            _pathBox.Enabled = true;
            _desktopCheck.Enabled = true;
            _launchCheck.Enabled = true;
            _status.Text = "安装失败";
            _status.ForeColor = Theme.Danger;
            MessageBox.Show(this, "安装失败：\r\n" + ex.Message, "FenceDesk", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void CancelClick(object sender, EventArgs e)
        {
            if (_finished)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _pathBox.Text.Trim(),
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    // ignore
                }
            }

            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_installing)
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }
    }

    /// <summary>卸载界面：复用同一套深色外观。</summary>
    internal sealed class UninstallForm : Form
    {
        private readonly SlimProgress _progress;
        private readonly Label _status;
        private readonly Button _uninstall;
        private readonly string _installDir;
        private bool _busy;

        public UninstallForm(string installDir)
        {
            _installDir = installDir;
            Text = "卸载 FenceDesk";
            ClientSize = new Size(560, 260);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = new Label();
            title.Text = "卸载 FenceDesk";
            title.ForeColor = Theme.Text;
            title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(28, 24);
            Controls.Add(title);

            Label info = new Label();
            info.Text = "安装目录：" + installDir + "\r\n卸载会删除程序文件、快捷方式和启动项；\r\n" +
                        "用户布局（%APPDATA%\\FenceDesk）会保留。";
            info.ForeColor = Theme.TextMuted;
            info.AutoSize = true;
            info.Location = new Point(30, 70);
            Controls.Add(info);

            _progress = new SlimProgress();
            _progress.Location = new Point(30, 158);
            _progress.Width = 498;
            _progress.Visible = false;
            Controls.Add(_progress);

            _status = new Label();
            _status.Text = "";
            _status.ForeColor = Theme.TextMuted;
            _status.AutoSize = true;
            _status.Location = new Point(30, 172);
            Controls.Add(_status);

            _uninstall = new Button();
            _uninstall.Text = "卸载";
            _uninstall.Location = new Point(318, 208);
            _uninstall.Width = 96;
            _uninstall.Height = 32;
            _uninstall.FlatStyle = FlatStyle.Flat;
            _uninstall.FlatAppearance.BorderSize = 0;
            _uninstall.BackColor = Theme.Danger;
            _uninstall.ForeColor = Color.FromArgb(0x1A, 0x08, 0x0C);
            _uninstall.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _uninstall.Cursor = Cursors.Hand;
            _uninstall.Click += UninstallClick;
            Controls.Add(_uninstall);

            Button close = new Button();
            close.Text = "关闭";
            close.Location = new Point(424, 208);
            close.Width = 104;
            close.Height = 32;
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderSize = 1;
            close.FlatAppearance.BorderColor = Theme.Stroke;
            close.BackColor = Color.FromArgb(0x16, 0x1B, 0x24);
            close.ForeColor = Theme.TextMuted;
            close.Click += delegate { if (!_busy) Close(); };
            Controls.Add(close);

            AcceptButton = _uninstall;
        }

        private void UninstallClick(object sender, EventArgs e)
        {
            if (_busy) return;
            DialogResult confirm = MessageBox.Show(this,
                "确定卸载 FenceDesk 吗？\r\n\r\n桌面图标会自动恢复，用户布局会保留。",
                "卸载 FenceDesk", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            _busy = true;
            _uninstall.Enabled = false;
            _progress.Visible = true;
            _progress.Value = 0;
            _status.Text = "正在卸载…";

            var worker = new Thread(() =>
            {
                try
                {
                    InstallerCore.Uninstall(_installDir, true,
                        (message, value) => BeginInvoke(new Action(() =>
                        {
                            if (value >= 0) _progress.Value = value;
                            _status.Text = message;
                        })));
                    BeginInvoke(new Action(() =>
                    {
                        _status.Text = "卸载完成";
                        _status.ForeColor = Theme.Accent;
                        _progress.Value = 1;
                        MessageBox.Show(this, "FenceDesk 已卸载。", "FenceDesk", MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                        Close();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _busy = false;
                        _uninstall.Enabled = true;
                        _status.Text = "卸载失败";
                        _status.ForeColor = Theme.Danger;
                        MessageBox.Show(this, "卸载失败：\r\n" + ex.Message, "FenceDesk", MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }));
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
            bool uninstall = args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));

            // 真正能写 Program Files 才算"机器级安装"；UAC 过滤令牌下
            // IsInRole(Administrator) 也会是 true，但写入仍会被拒绝。
            if (!InstallerCore.MachineWide)
            {
                InstallerCore.Log("当前没有 Program Files 的写权限（UAC 过滤令牌或被拒绝提权）。");

                if (!silent)
                {
                    // 先尝试用管理员权限重新启动自己（正常机器会弹 UAC）。
                    if (TryRelaunchElevated(args))
                    {
                        return;
                    }

                    DialogResult answer = MessageBox.Show(
                        "安装到 C:\\Program Files 需要管理员权限。\r\n\r\n" +
                        "是否改为只给当前用户安装？\r\n" +
                        "（装到 %LOCALAPPDATA%\\Programs\\FenceDesk，开机自启、快捷方式同样有效）",
                        "FenceDesk 安装程序", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button1);
                    if (answer != DialogResult.Yes) return;
                }
            }

            if (uninstall)
            {
                string installDir = Path.GetDirectoryName(Application.ExecutablePath);
                string overrideDir = GetArgValue(args, "--dir=");
                if (!string.IsNullOrEmpty(overrideDir)) installDir = overrideDir;

                if (silent)
                {
                    try
                    {
                        InstallerCore.Uninstall(installDir, true, null);
                    }
                    catch (Exception ex)
                    {
                        InstallerCore.Log("静默卸载失败：" + ex);
                    }

                    return;
                }

                Application.Run(new UninstallForm(installDir));
                return;
            }

            if (silent)
            {
                string target = GetArgValue(args, "--dir=");
                if (string.IsNullOrEmpty(target)) target = InstallerCore.DefaultTarget;
                try
                {
                    InstallerCore.Install(target, true, false, null);
                }
                catch (Exception ex)
                {
                    InstallerCore.Log("静默安装失败：" + ex);
                    Environment.Exit(1);
                }

                return;
            }

            Application.Run(new SetupForm());
        }

        private static string GetArgValue(string[] args, string prefix)
        {
            foreach (string arg in args)
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(prefix.Length).Trim('"');
                }
            }

            return null;
        }

        /// <summary>用 runas 重新启动自己以获取管理员权限；用户取消 UAC 时返回 false。</summary>
        private static bool TryRelaunchElevated(string[] args)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = string.Join(" ", args.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a)),
                };
                Process process = Process.Start(startInfo);
                return process != null;
            }
            catch (Exception ex)
            {
                InstallerCore.Log("提权启动失败：" + ex.Message);
                return false;
            }
        }
    }
}
