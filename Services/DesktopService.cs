using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace FenceDesk.Services;

public sealed record DesktopItem(string Path, string Name, bool IsDirectory);

public static class DesktopService
{
    private const string HideIconsRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FenceDesk";
    private const string ScheduledTaskName = "FenceDesk_Autostart";
    private const int ToggleDesktopIconsCommand = 0x7402;

    private static readonly object IconGate = new();
    private static bool _ownsHiddenIcons;

    public static bool OwnsHiddenIcons
    {
        get
        {
            lock (IconGate) return _ownsHiddenIcons;
        }
    }

    public static IEnumerable<string> DesktopDirectories()
    {
        var personal = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        foreach (var path in new[] { personal, common })
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    public static List<DesktopItem> EnumerateDesktopItems(bool includeHidden = false)
    {
        var results = new List<DesktopItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in DesktopDirectories())
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex)
            {
                LogService.Error("枚举桌面失败: " + directory, ex);
                continue;
            }

            foreach (var path in entries)
            {
                try
                {
                    var name = Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("~$", StringComparison.Ordinal)) continue;

                    var attrs = File.GetAttributes(path);
                    if (!includeHidden &&
                        (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)))
                    {
                        continue;
                    }

                    if (!seen.Add(path)) continue;

                    results.Add(new DesktopItem(path, name, attrs.HasFlag(FileAttributes.Directory)));
                }
                catch (Exception ex)
                {
                    LogService.Error("读取桌面项目失败: " + path, ex);
                }
            }
        }

        return results
            .OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool PathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static bool Launch(string path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空";
            return false;
        }

        if (!PathExists(path))
        {
            error = "文件或文件夹已不存在";
            return false;
        }

        try
        {
            var isShortcut = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
            var elevated = IsProcessElevated();

            // 管理员运行时不要直接把子进程也提权；交给 Explorer 以普通权限启动。
            // 普通运行时使用桌面双击同样的 ShellExecute 路径，速度最快、兼容性最好。
            if (elevated)
            {
                LogService.Info("以 Explorer 启动（FenceDesk 管理员）: " + path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
                return true;
            }

            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };
            if (!isShortcut)
            {
                info.WorkingDirectory = Directory.Exists(path)
                    ? path
                    : Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            }

            LogService.Info("启动: " + path);
            var process = Process.Start(info);
            TryBringToForeground(process);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("启动失败: " + path, ex);
            error = ex.Message;
            return false;
        }
    }

    public static bool IsProcessElevated()
    {
        try
        {
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 0x0008, out var token))
            {
                return false;
            }

            try
            {
                return NativeMethods.GetTokenInformation(token, 20, out var elevated, 4, out _) && elevated != 0;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void TryBringToForeground(Process? process)
    {
        if (process is null) return;

        // 从桌面围栏启动时，FenceDesk 不是前台进程，新应用窗口可能被前台锁挡在后面。
        // 等窗口创建后主动恢复并置前，避免“任务管理器有进程但看不到界面”。
        for (var i = 0; i < 25; i++)
        {
            try
            {
                process.Refresh();
                if (process.HasExited) return;
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(handle, 9);
                    NativeMethods.SetForegroundWindow(handle);
                    LogService.Info("已将启动窗口置前: " + process.ProcessName);
                    return;
                }
            }
            catch
            {
                // ignore and retry
            }

            Thread.Sleep(120);
        }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogService.Error("打开资源管理器失败", ex);
        }
    }

    public static void RefreshDesktopSurface(out string? error)
    {
        error = null;
        try
        {
            string? wallpaper = null;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
            {
                wallpaper = key?.GetValue("Wallpaper") as string;
            }

            if (!string.IsNullOrWhiteSpace(wallpaper) && File.Exists(wallpaper))
            {
                // SPI_SETDESKWALLPAPER + SPIF_SENDCHANGE 让 Explorer 重新绘制当前壁纸，
                // 修复全屏应用退出后留下的黑屏/壁纸不刷新。
                NativeMethods.SystemParametersInfo(20, 0, wallpaper, 2);
            }

            NativeMethods.DwmFlush();
            NativeMethods.RedrawWindow(NativeMethods.GetDesktopWindow(), IntPtr.Zero, IntPtr.Zero, 0x0185);
            LogService.Info("已请求刷新桌面壁纸");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            LogService.Error("刷新桌面壁纸失败", ex);
        }
    }

    public static void RevealItemLocation(string path)
    {
        // Windows 桌面右键快捷方式的“打开文件所在位置”是打开目标程序的安装目录，
        // 不是快捷方式本身所在的桌面/开始菜单目录。
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = ResolveShortcutTarget(path);
            if (!string.IsNullOrWhiteSpace(target))
            {
                RevealInExplorer(target);
                return;
            }
        }

        RevealInExplorer(path);
    }

    public static void OpenDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogService.Error("打开文件夹失败", ex);
        }
    }

    public static void OpenDataDirectory() => OpenDirectory(LogService.DataDirectory);

    public static string? ResolveShortcutTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { path });
            if (shortcut is null) return null;

            var target = shortcut.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex)
        {
            LogService.Error("解析快捷方式目标失败: " + path, ex);
            return null;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public static bool RenamePath(string path, string newName, out string? newPath, out string? error)
    {
        newPath = null;
        error = null;
        try
        {
            if (!PathExists(path))
            {
                error = "文件或文件夹已不存在";
                return false;
            }

            var parent = Directory.Exists(path) ? Path.GetDirectoryName(path) : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent))
            {
                error = "无法获取所在目录";
                return false;
            }

            var target = Path.Combine(parent, newName);
            if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
            {
                newPath = path;
                return true;
            }
            if (PathExists(target))
            {
                error = "同名文件或文件夹已经存在";
                return false;
            }

            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            newPath = target;
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("重命名失败: " + path, ex);
            error = ex.Message;
            return false;
        }
    }

    public static bool DeleteToRecycleBin(string path, out string? error)
    {
        error = null;
        var from = IntPtr.Zero;
        try
        {
            if (!PathExists(path))
            {
                error = "文件或文件夹已不存在";
                return false;
            }

            from = Marshal.StringToHGlobalUni(path + "\0\0");
            var op = new NativeMethods.SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = NativeMethods.FO_DELETE,
                pFrom = from,
                pTo = IntPtr.Zero,
                fFlags = (ushort)(NativeMethods.FOF_ALLOWUNDO |
                                  NativeMethods.FOF_NOCONFIRMATION |
                                  NativeMethods.FOF_SILENT |
                                  NativeMethods.FOF_NOERRORUI),
                lpszProgressTitle = IntPtr.Zero,
            };

            var result = NativeMethods.SHFileOperation(ref op);
            if (result != 0 || op.fAnyOperationsAborted)
            {
                error = "回收站操作失败，错误代码 " + result;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("删除到回收站失败: " + path, ex);
            error = ex.Message;
            return false;
        }
        finally
        {
            if (from != IntPtr.Zero) Marshal.FreeHGlobal(from);
        }
    }

    public static bool ShowProperties(string path, out string? error)
    {
        error = null;
        try
        {
            if (!PathExists(path))
            {
                error = "文件或文件夹已不存在";
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Verb = "properties",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("打开属性失败: " + path, ex);
            error = ex.Message;
            return false;
        }
    }

    public static bool OpenWith(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "文件已不存在";
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "shell32.dll,OpenAs_RunDLL \"" + path + "\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("打开方式失败: " + path, ex);
            error = ex.Message;
            return false;
        }
    }

    public static bool ShowShellProperties(string shellTarget, out string? error)
    {
        error = null;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = shellTarget,
                Verb = "properties",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("打开系统项属性失败: " + shellTarget, ex);
            error = ex.Message;
            return false;
        }
    }

    public static bool EmptyRecycleBin(out string? error)
    {
        error = null;
        try
        {
            var result = NativeMethods.SHEmptyRecycleBin(
                IntPtr.Zero,
                null,
                NativeMethods.SHERB_NOCONFIRMATION |
                NativeMethods.SHERB_NOPROGRESSUI |
                NativeMethods.SHERB_NOSOUND);
            if (result < 0)
            {
                error = "错误代码 0x" + result.ToString("X8");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("清空回收站失败", ex);
            error = ex.Message;
            return false;
        }
    }

    public static bool AreDesktopIconsHidden()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HideIconsRegistryPath);
            var value = key?.GetValue("HideIcons");
            return value switch
            {
                int i => i != 0,
                long l => l != 0,
                _ => false,
            };
        }
        catch (Exception ex)
        {
            LogService.Error("读取桌面图标状态失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 使用 Explorer 桌面视图的“显示桌面图标”命令来切换图标可见性。
    /// 这是资源管理器右键菜单实际执行的命令，切换即时生效。
    /// </summary>
    public static bool SetDesktopIconsHidden(bool hidden, bool takeOwnership, int timeoutMs = 1600)
    {
        lock (IconGate)
        {
            try
            {
                if (AreDesktopIconsHidden() == hidden)
                {
                    if (hidden && takeOwnership) _ownsHiddenIcons = true;
                    if (!hidden) _ownsHiddenIcons = false;
                    return true;
                }

                var view = NativeMethods.GetDesktopListView();
                if (view == IntPtr.Zero)
                {
                    LogService.Error("找不到桌面图标视图，无法切换图标显示。");
                    return false;
                }

                NativeMethods.SendMessage(view, NativeMethods.WM_COMMAND, new IntPtr(ToggleDesktopIconsCommand), IntPtr.Zero);

                var deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    if (AreDesktopIconsHidden() == hidden)
                    {
                        if (hidden && takeOwnership) _ownsHiddenIcons = true;
                        if (!hidden) _ownsHiddenIcons = false;
                        return true;
                    }

                    Thread.Sleep(60);
                }

                LogService.Error($"切换桌面图标超时，期望 hidden={hidden}");
                return false;
            }
            catch (Exception ex)
            {
                LogService.Error("切换桌面图标失败", ex);
                return false;
            }
        }
    }

    public static void AdoptOwnershipIfHidden()
    {
        lock (IconGate)
        {
            if (AreDesktopIconsHidden()) _ownsHiddenIcons = true;
        }
    }

    public static void RestoreIconsIfOwned()
    {
        // 只要图标处于隐藏状态，退出/异常恢复时都强制恢复可见。
        // 不再依赖 _ownsHiddenIcons：崩溃或状态文件丢失时它可能已经不可靠，
        // 结果就是 HideIcons=1 残留、桌面一直空白。
        if (!AreDesktopIconsHidden()) return;
        LogService.Info("退出恢复：强制显示桌面图标");
        SetDesktopIconsHidden(false, takeOwnership: false);
    }

    public static void EmergencyRestore()
    {
        try
        {
            RestoreIconsIfOwned();
        }
        catch
        {
            // Never throw while shutting down.
        }
    }

    public static bool SetAutoStartElevated(bool enabled, out string? error)
    {
        // 该实例由 runas 启动，已经拥有完整的管理员令牌。
        return SetAutoStart(enabled, out error);
    }

    /// <summary>
    /// 受限令牌下直接写启动项会失败，尝试通过 runas 启动一个短生命周期的高权限实例。
    /// UAC 关闭时无提示；UAC 开启时用户会看到系统授权窗口。
    /// </summary>
    public static bool TryElevateAutoStart(bool enabled, out string? error)
    {
        error = null;
        if (!IsUacEnabled())
        {
            error = "系统 UAC 已关闭，当前受限令牌无法通过提权提升权限";
            return false;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            error = "无法获取程序路径";
            return false;
        }

        try
        {
            var arguments = "--set-autostart-elevated" + (enabled ? string.Empty : " --disable-autostart");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "无法启动提权进程";
                return false;
            }

            if (!process.WaitForExit(20000))
            {
                error = "提权进程超时";
                return false;
            }

            for (var i = 0; i < 20; i++)
            {
                if (IsAutoStartEnabled() == enabled) return true;
                Thread.Sleep(100);
            }

            error = "提权进程已结束，但启动项未生效";
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "已取消管理员授权";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsAutoStartEnabled()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath);
            if (key?.GetValue(RunValueName) is string value &&
                value.Contains(exe, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // 注册表可能在受限令牌下不可读，继续检查其他方式。
        }

        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(RunRegistryPath);
            if (machine?.GetValue(RunValueName) is string machineValue &&
                machineValue.Contains(exe, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (File.Exists(StartupShortcutPath) || File.Exists(StartupCommandPath))
            {
                var target = TryReadShortcutTarget(StartupShortcutPath);
                return string.IsNullOrWhiteSpace(target) ||
                       string.Equals(target, exe, StringComparison.OrdinalIgnoreCase) ||
                       File.Exists(StartupCommandPath);
            }
        }
        catch
        {
            // ignore
        }

        return ScheduledTaskExists();
    }

    public static bool SetAutoStart(bool enabled, out string? error)
    {
        error = null;
        var errors = new List<string>();

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe) && MachineRunValueMatches(exe))
            {
                return true;
            }

            if (TrySetRunAutoStart(true, out var runError))
            {
                return true;
            }
            errors.Add("注册表 Run：" + runError);

            if (TrySetStartupShortcut(true, out var startupError))
            {
                LogService.Info("开机启动已通过启动文件夹设置。");
                return true;
            }
            errors.Add("启动文件夹：" + startupError);

            if (TrySetScheduledTask(true, out var taskError))
            {
                LogService.Info("开机启动已通过计划任务设置。");
                return true;
            }
            errors.Add("计划任务：" + taskError);

            error = string.Join("；", errors);
            LogService.Error("设置开机启动失败：" + error);
            return false;
        }

        var ok = true;
        if (!TrySetRunAutoStart(false, out var disableRunError))
        {
            if (RunAutoStartValueExists())
            {
                ok = false;
                errors.Add("注册表 Run：" + disableRunError);
            }
        }
        if (!TrySetStartupShortcut(false, out var disableStartupError))
        {
            if (File.Exists(StartupShortcutPath) || File.Exists(StartupCommandPath))
            {
                ok = false;
                errors.Add("启动文件夹：" + disableStartupError);
            }
        }
        if (!TrySetScheduledTask(false, out var disableTaskError))
        {
            if (ScheduledTaskExists())
            {
                ok = false;
                errors.Add("计划任务：" + disableTaskError);
            }
        }

        var currentExe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentExe) && MachineRunValueMatches(currentExe))
        {
            if (!TrySetMachineRunAutoStart(false, out var disableMachineError))
            {
                ok = false;
                errors.Add("HKLM Run：" + disableMachineError);
            }
        }

        if (!ok)
        {
            error = string.Join("；", errors);
            LogService.Error("取消开机启动失败：" + error);
        }

        return ok;
    }

    private static bool RunAutoStartValueExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath);
            return key?.GetValue(RunValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    private static bool MachineRunValueMatches(string exe)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RunRegistryPath);
            return key?.GetValue(RunValueName) is string value &&
                   value.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetMachineRunAutoStart(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RunRegistryPath, writable: true);
            if (key is null)
            {
                error = "无法打开 HKLM Run 键";
                return false;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "无法获取程序路径";
                    return false;
                }
                key.SetValue(RunValueName, $"\"{exe}\" --autostart");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TrySetScheduledTask(bool enabled, out string? error)
    {
        error = null;
        try
        {
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "无法获取程序路径";
                    return false;
                }

                var args = new[]
                {
                    "/Create",
                    "/TN", ScheduledTaskName,
                    "/TR", $"\"{exe}\" --autostart",
                    "/SC", "ONLOGON",
                    "/RL", "LIMITED",
                    "/F",
                };
                if (RunSchtasks(args, out var output))
                {
                    return true;
                }

                error = CleanProcessOutput(output);
                return false;
            }

            var deleteArgs = new[] { "/Delete", "/TN", ScheduledTaskName, "/F" };
            if (RunSchtasks(deleteArgs, out var deleteOutput) || !ScheduledTaskExists())
            {
                return true;
            }

            error = CleanProcessOutput(deleteOutput);
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool ScheduledTaskExists()
    {
        try
        {
            var args = new[] { "/Query", "/TN", ScheduledTaskName };
            return RunSchtasks(args, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool RunSchtasks(IEnumerable<string> arguments, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            output = "无法启动 schtasks.exe";
            return false;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(8000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
            output = "schtasks.exe 超时";
            return false;
        }

        output = stdoutTask.GetAwaiter().GetResult() + "\n" + stderrTask.GetAwaiter().GetResult();
        return process.ExitCode == 0;
    }

    private static string CleanProcessOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "计划任务操作失败";
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return "计划任务操作失败";
        return string.Join(" ", lines.TakeLast(3));
    }

    private static bool TrySetRunAutoStart(bool enabled, out string? error)
    {
        error = null;
        try
        {
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "无法获取程序路径";
                    return false;
                }

                using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
                key.SetValue(RunValueName, $"\"{exe}\" --autostart");
                return true;
            }

            using var existing = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            existing?.DeleteValue(RunValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TrySetStartupShortcut(bool enabled, out string? error)
    {
        error = null;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                error = "无法获取程序路径";
                return false;
            }

            if (!enabled)
            {
                if (File.Exists(StartupShortcutPath)) File.Delete(StartupShortcutPath);
                if (File.Exists(StartupCommandPath)) File.Delete(StartupCommandPath);
                return true;
            }

            Directory.CreateDirectory(StartupFolderPath);
            if (TryCreateShortcutAt(exe, StartupShortcutPath, out var shortcutError))
            {
                return true;
            }

            try
            {
                var command = "@echo off" + Environment.NewLine +
                              "start \"\" \"" + exe + "\" --autostart" + Environment.NewLine;
                File.WriteAllText(StartupCommandPath, command, Encoding.UTF8);
                LogService.Info("WScript.Shell 创建快捷方式失败，已使用启动文件夹 .cmd 回退：" + shortcutError);
                return true;
            }
            catch (Exception ex)
            {
                error = "快捷方式：" + shortcutError + "；cmd：" + ex.Message;
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 受限环境下无法直接写启动文件夹时，在可写的数据目录生成一份启动快捷方式，
    /// 用户只需把它拖入启动文件夹即可。
    /// </summary>
    public static bool GenerateStartupShortcut(out string? shortcutPath, out string? error)
    {
        shortcutPath = null;
        error = null;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                error = "无法获取程序路径";
                return false;
            }

            Directory.CreateDirectory(AppPaths.DataDirectory);
            var lnk = Path.Combine(AppPaths.DataDirectory, "FenceDesk.lnk");
            if (TryCreateShortcutAt(exe, lnk, out var shortcutError))
            {
                shortcutPath = lnk;
                SelectInExplorer(lnk);
                return true;
            }

            var cmd = Path.Combine(AppPaths.DataDirectory, "FenceDesk.cmd");
            try
            {
                File.WriteAllText(cmd,
                    "@echo off" + Environment.NewLine +
                    "start \"\" \"" + exe + "\" --autostart" + Environment.NewLine,
                    Encoding.UTF8);
                shortcutPath = cmd;
                SelectInExplorer(cmd);
                return true;
            }
            catch (Exception cmdError)
            {
                error = "快捷方式：" + shortcutError + "；cmd：" + cmdError.Message;
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool CanWriteAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key is null) return false;
            // 不写入内容，只验证 key 是否以写权限打开。
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUacEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            return key?.GetValue("EnableLUA") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SelectInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }

    private static bool TryCreateShortcutAt(string exe, string shortcutPath, out string? error)
    {
        error = null;
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                error = "WScript.Shell 不可用";
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                error = "无法创建 WScript.Shell";
                return false;
            }

            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath });
            if (shortcut is null)
            {
                error = "无法创建快捷方式对象";
                return false;
            }

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--autostart" });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                new object[] { AppContext.BaseDirectory });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                new object[] { exe + ",0" });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                new object[] { "FenceDesk 桌面围栏" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            error = ex.InnerException.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string? TryReadShortcutTarget(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return null;
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath });
            if (shortcut is null) return null;
            return shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut,
                null) as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    public static string StartupFolderPath
    {
        get
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(startup))
            {
                startup = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            }
            return startup;
        }
    }

    public static string StartupShortcutPath => Path.Combine(StartupFolderPath, "FenceDesk.lnk");
    public static string StartupCommandPath => Path.Combine(StartupFolderPath, "FenceDesk.cmd");

    public static void OpenStartupFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:startup",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开启动文件夹失败", ex);
        }
    }

    public static void PlayErrorSound()
    {
        try
        {
            System.Media.SystemSounds.Hand.Play();
        }
        catch
        {
            // ignore
        }
    }
}
