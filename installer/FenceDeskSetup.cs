using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("FenceDesk Setup")]
[assembly: AssemblyDescription("FenceDesk desktop fences installer")]
[assembly: AssemblyCompany("FenceDesk")]
[assembly: AssemblyProduct("FenceDesk")]
[assembly: AssemblyVersion("1.3.44.0")]
[assembly: AssemblyFileVersion("1.3.44.0")]

namespace FenceDeskSetup
{
    internal static class InstallerCore
    {
        public const string Version = "1.3.44";
        public const string RunValueName = "FenceDesk";

        public static readonly string DefaultTarget =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FenceDesk");

        public static string SourceDirectory
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static void Install(string target, bool createDesktop, bool launchApp)
        {
            string source = SourceDirectory;
            string sourceExe = Path.Combine(source, "FenceDesk.exe");
            if (!File.Exists(sourceExe))
            {
                throw new InvalidOperationException(
                    "FenceDesk.exe was not found next to FenceDeskSetup.exe.\r\n" +
                    "Extract the complete release zip, then run the setup again.");
            }

            if (PathsEqual(source, target))
            {
                throw new InvalidOperationException("The install folder cannot be the same as the setup folder.");
            }

            StopRunningApp();
            Directory.CreateDirectory(target);

            string[] skipPrefixes = new[] { "Install-FenceDesk", "Uninstall-FenceDesk" };
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, "FenceDeskSetup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                if (skipPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                if (name.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(target, name), true);
            }

            string docsSource = Path.Combine(source, "docs");
            if (Directory.Exists(docsSource))
            {
                string docsTarget = Path.Combine(target, "docs");
                if (Directory.Exists(docsTarget)) Directory.Delete(docsTarget, true);
                CopyDirectory(docsSource, docsTarget);
            }

            string installedExe = Path.Combine(target, "FenceDesk.exe");
            if (!File.Exists(installedExe))
            {
                throw new IOException("FenceDesk.exe was not copied to the install folder.");
            }

            // The uninstaller is a copy of this setup executable, run with --uninstall.
            File.Copy(Application.ExecutablePath, Path.Combine(target, "Uninstall.exe"), true);

            WriteRunEntry(installedExe);
            WriteUninstallEntry(target, installedExe);

            try
            {
                CreateShortcuts(target, installedExe, createDesktop);
            }
            catch (Exception ex)
            {
                Log("Shortcut creation failed: " + ex);
            }

            Log("Installed to " + target);

            if (launchApp)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + installedExe + "\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Log("Launch failed: " + ex);
                }
            }
        }

        public static void Uninstall(string installDir, bool silent)
        {
            if (!silent)
            {
                DialogResult result = MessageBox.Show(
                    "Remove FenceDesk from this computer?\r\n\r\nUser layouts in %APPDATA%\\FenceDesk will be kept.",
                    "FenceDesk Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.Yes) return;
            }

            StopRunningApp();

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null) key.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex)
            {
                Log("Remove Run entry failed: " + ex);
            }

            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FenceDesk", false);
            }
            catch (Exception ex)
            {
                Log("Remove uninstall entry failed: " + ex);
            }

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
                Log("Remove shortcuts failed: " + ex);
            }

            Log("Uninstalled " + installDir);

            if (!silent)
            {
                MessageBox.Show("FenceDesk was removed.\r\nUser layouts in %APPDATA%\\FenceDesk were kept.",
                    "FenceDesk Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            ScheduleDirectoryRemoval(installDir);
        }

        private static void StopRunningApp()
        {
            foreach (Process process in Process.GetProcessesByName("FenceDesk"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
                catch
                {
                    // ignore
                }
            }
        }

        private static void WriteRunEntry(string installedExe)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (key == null) throw new IOException("Cannot open HKLM Run key.");
                key.SetValue(RunValueName, "\"" + installedExe + "\" --autostart", RegistryValueKind.String);
            }
        }

        private static void WriteUninstallEntry(string installDir, string installedExe)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FenceDesk"))
            {
                if (key == null) return;
                key.SetValue("DisplayName", "FenceDesk 桌面围栏", RegistryValueKind.String);
                key.SetValue("DisplayVersion", Version, RegistryValueKind.String);
                key.SetValue("Publisher", "FenceDesk", RegistryValueKind.String);
                key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                key.SetValue("DisplayIcon", installedExe + ",0", RegistryValueKind.String);
                key.SetValue("UninstallString", "\"" + Path.Combine(installDir, "Uninstall.exe") + "\" --uninstall",
                    RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void CreateShortcuts(string installDir, string installedExe, bool createDesktop)
        {
            string menuFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "FenceDesk");
            Directory.CreateDirectory(menuFolder);

            CreateShortcut(Path.Combine(menuFolder, "FenceDesk.lnk"), installedExe, installDir, string.Empty,
                "FenceDesk desktop fences");
            CreateShortcut(Path.Combine(menuFolder, "Uninstall FenceDesk.lnk"),
                Path.Combine(installDir, "Uninstall.exe"), installDir, "--uninstall", "Uninstall FenceDesk");

            if (createDesktop)
            {
                string desktopLink = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "FenceDesk.lnk");
                CreateShortcut(desktopLink, installedExe, installDir, string.Empty, "FenceDesk desktop fences");
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir,
            string arguments, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell is not available.");

            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { shortcutPath });
                if (shortcut == null) throw new InvalidOperationException("Cannot create shortcut.");

                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
                    new object[] { targetPath });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                    new object[] { workingDir });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut,
                    new object[] { arguments });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                    new object[] { targetPath + ",0" });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                    new object[] { description });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void ScheduleDirectoryRemoval(string installDir)
        {
            try
            {
                string arguments = "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + installDir + "\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                Log("Schedule removal failed: " + ex);
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

        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "FenceDeskSetup.log");
                File.AppendAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + "\r\n");
            }
            catch
            {
                // ignore
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox _pathBox;
        private readonly CheckBox _desktopCheck;
        private readonly CheckBox _launchCheck;
        private readonly Label _status;
        private readonly Button _installButton;

        public SetupForm()
        {
            Text = "FenceDesk 安装程序";
            ClientSize = new Size(620, 360);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 247, 250);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 76;
            header.BackColor = Color.FromArgb(20, 26, 34);
            Controls.Add(header);

            Label title = new Label();
            title.Text = "FenceDesk 桌面围栏";
            title.ForeColor = Color.White;
            title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(24, 17);
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "安装到 Program Files，并设置所有用户登录时自动启动";
            subtitle.ForeColor = Color.FromArgb(150, 220, 208);
            subtitle.Font = new Font("Microsoft YaHei UI", 9F);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(26, 47);
            header.Controls.Add(subtitle);

            Label pathLabel = new Label();
            pathLabel.Text = "安装位置";
            pathLabel.AutoSize = true;
            pathLabel.Location = new Point(24, 102);
            Controls.Add(pathLabel);

            _pathBox = new TextBox();
            _pathBox.Text = InstallerCore.DefaultTarget;
            _pathBox.Location = new Point(26, 124);
            _pathBox.Width = 500;
            Controls.Add(_pathBox);

            Button browse = new Button();
            browse.Text = "浏览...";
            browse.Location = new Point(534, 122);
            browse.Width = 62;
            browse.Height = 26;
            browse.Click += BrowseClick;
            Controls.Add(browse);

            _desktopCheck = new CheckBox();
            _desktopCheck.Text = "创建桌面快捷方式";
            _desktopCheck.Checked = true;
            _desktopCheck.AutoSize = true;
            _desktopCheck.Location = new Point(26, 174);
            Controls.Add(_desktopCheck);

            _launchCheck = new CheckBox();
            _launchCheck.Text = "安装完成后启动 FenceDesk";
            _launchCheck.Checked = true;
            _launchCheck.AutoSize = true;
            _launchCheck.Location = new Point(26, 202);
            Controls.Add(_launchCheck);

            Label note = new Label();
            note.Text = "将写入 HKLM 机器级启动项，需要管理员权限。程序运行时不需要管理员权限。";
            note.ForeColor = Color.FromArgb(100, 110, 125);
            note.AutoSize = true;
            note.Location = new Point(26, 238);
            Controls.Add(note);

            _status = new Label();
            _status.Text = "准备安装。";
            _status.ForeColor = Color.FromArgb(80, 90, 105);
            _status.AutoSize = true;
            _status.Location = new Point(26, 276);
            Controls.Add(_status);

            _installButton = new Button();
            _installButton.Text = "安装";
            _installButton.Location = new Point(420, 308);
            _installButton.Width = 84;
            _installButton.Height = 32;
            _installButton.BackColor = Color.FromArgb(51, 214, 192);
            _installButton.ForeColor = Color.FromArgb(8, 30, 27);
            _installButton.FlatStyle = FlatStyle.Flat;
            _installButton.FlatAppearance.BorderSize = 0;
            _installButton.Click += InstallClick;
            Controls.Add(_installButton);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(512, 308);
            cancel.Width = 84;
            cancel.Height = 32;
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);

            AcceptButton = _installButton;
            CancelButton = cancel;
        }

        private void BrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 FenceDesk 安装位置";
                dialog.SelectedPath = _pathBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void InstallClick(object sender, EventArgs e)
        {
            string target = _pathBox.Text.Trim();
            if (target.Length == 0)
            {
                MessageBox.Show(this, "请选择安装位置。", "FenceDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _installButton.Enabled = false;
            _status.Text = "正在复制文件并写入启动项...";
            _status.ForeColor = Color.FromArgb(30, 120, 100);
            Application.DoEvents();

            try
            {
                InstallerCore.Install(target, _desktopCheck.Checked, _launchCheck.Checked);
                MessageBox.Show(this,
                    "安装完成。\r\n\r\nFenceDesk 已设置为登录 Windows 时自动启动。",
                    "FenceDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                _installButton.Enabled = true;
                _status.Text = "安装失败。";
                _status.ForeColor = Color.FromArgb(200, 60, 60);
                MessageBox.Show(this, "安装失败：\r\n" + ex.Message, "FenceDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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

            if (!InstallerCore.IsAdministrator())
            {
                if (!silent)
                {
                    MessageBox.Show(
                        "FenceDesk 安装程序需要管理员权限。\r\n请右键 FenceDeskSetup.exe，选择“以管理员身份运行”。",
                        "FenceDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            if (uninstall)
            {
                string installDir = Path.GetDirectoryName(Application.ExecutablePath);
                string overrideDir = GetArgValue(args, "--dir=");
                if (!string.IsNullOrEmpty(overrideDir)) installDir = overrideDir;
                InstallerCore.Uninstall(installDir, silent);
                return;
            }

            if (silent)
            {
                string target = GetArgValue(args, "--dir=");
                if (string.IsNullOrEmpty(target)) target = InstallerCore.DefaultTarget;
                try
                {
                    InstallerCore.Install(target, true, false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "FenceDeskSetup.log"),
                            "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] FATAL " + ex + "\r\n");
                    }
                    catch { }
                    Environment.ExitCode = 1;
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
    }
}
