using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FenceDesk.Services;

namespace FenceDesk;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private MainWindow? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--set-autostart-elevated", StringComparison.OrdinalIgnoreCase)))
        {
            var enable = !e.Args.Any(a => a.Equals("--disable-autostart", StringComparison.OrdinalIgnoreCase));
            var ok = DesktopService.SetAutoStartElevated(enable, out var helperError);
            LogService.Info("提权设置开机启动：" + (ok ? "成功" : "失败 " + helperError));
            Shutdown(ok ? 0 : 1);
            return;
        }

        // 应急恢复：桌面图标被隐藏、围栏窗口却起不来时，双击带这个参数的快捷方式即可恢复。
        if (e.Args.Any(a => a.Equals("--restore-desktop", StringComparison.OrdinalIgnoreCase)))
        {
            var restored = DesktopService.SetDesktopIconsHidden(false, takeOwnership: false, 2500);
            LogService.Info("命令行恢复桌面图标：" + (restored ? "成功" : "失败"));
            Shutdown(restored ? 0 : 1);
            return;
        }

        // 看门狗：主进程被任务管理器强杀时，Windows 不会执行任何退出清理，
        // 桌面图标会一直保持隐藏。这个隐藏进程只做一件事——等主进程结束后
        // 把图标恢复出来。
        var watchdogIndex = Array.FindIndex(e.Args,
            a => a.Equals("--watchdog", StringComparison.OrdinalIgnoreCase));
        if (watchdogIndex >= 0)
        {
            var exitCode = RunIconWatchdog(watchdogIndex + 1 < e.Args.Length ? e.Args[watchdogIndex + 1] : null);
            Shutdown(exitCode);
            return;
        }

        // 启动自检：上次若是被强杀 / 直接重启，桌面图标可能还停在隐藏状态。
        // 这里先无条件恢复可见，保证任何情况下都先有一个正常桌面；
        // 之后 MainWindow 会按设置重新隐藏并由围栏接管。
        try
        {
            if (DesktopService.AreDesktopIconsHidden())
            {
                LogService.Info("启动自检：桌面图标处于隐藏状态，先恢复可见");
                DesktopService.SetDesktopIconsHidden(false, takeOwnership: false, 2500);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("启动自检恢复桌面图标失败", ex);
        }

        var hiddenIndex = Array.FindIndex(e.Args,
            a => a.Equals("--add-hidden", StringComparison.OrdinalIgnoreCase));
        if (hiddenIndex >= 0 && hiddenIndex + 1 < e.Args.Length)
        {
            HiddenVaultService.EnqueueRequest(e.Args[hiddenIndex + 1]);
        }

        HiddenVaultService.RegisterExplorerMenu();

        var elevatedRestartMarker = Path.Combine(AppPaths.DataDirectory, "elevated-restart.flag");
        var fromElevatedRestart = File.Exists(elevatedRestartMarker);
        if (fromElevatedRestart)
        {
            try { File.Delete(elevatedRestartMarker); } catch { }
        }

        if (!fromElevatedRestart && DesktopService.IsProcessElevated())
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    File.WriteAllText(elevatedRestartMarker, "1");
                    LogService.Info("检测到管理员启动，尝试通过 Explorer 以普通权限重启");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{exe}\"",
                        UseShellExecute = true,
                    });
                    Shutdown();
                    return;
                }
            }
            catch (Exception ex)
            {
                LogService.Error("普通权限重启失败，继续以管理员运行", ex);
            }
        }

        _instanceMutex = new Mutex(true, @"Local\FenceDesk.DesktopFences.1.0", out var isFirstInstance);
        _ownsMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        if (e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
        {
            var demoDir = Path.Combine(AppContext.BaseDirectory, "demo-data");
            Environment.SetEnvironmentVariable("FENCEDESK_DATA", demoDir);
        }

        if (e.Args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase)))
        {
            var portableDir = Path.Combine(AppContext.BaseDirectory, "data");
            Environment.SetEnvironmentVariable("FENCEDESK_DATA", portableDir);
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogService.Error("AppDomain unhandled: " + args.ExceptionObject);
            DesktopService.EmergencyRestore();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DesktopService.EmergencyRestore();

        try
        {
            _main = new MainWindow();
            MainWindow = _main;
            _main.Show();
        }
        catch (Exception ex)
        {
            LogService.Error("Startup failed", ex);
            MessageBox.Show("FenceDesk 启动失败：\n" + ex.Message, "FenceDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>
    /// 看门狗：等主进程退出后，如果桌面图标仍然隐藏就恢复。
    /// 覆盖“任务管理器结束进程 / 崩溃”这类完全不执行退出清理的场景。
    /// </summary>
    private static int RunIconWatchdog(string? pidText)
    {
        if (!int.TryParse(pidText, out var pid) || pid <= 0) return 1;

        Process? main;
        try
        {
            main = Process.GetProcessById(pid);
        }
        catch
        {
            return 0; // 主进程已经不在
        }

        try
        {
            main.WaitForExit();
        }
        catch
        {
            // ignore
        }

        // 正常退出会自己恢复图标，这里留一点时间给它先做完。
        Thread.Sleep(1200);

        try
        {
            if (DesktopService.AreDesktopIconsHidden())
            {
                LogService.Info("看门狗：主进程已退出但桌面图标仍隐藏，恢复可见");
                DesktopService.SetDesktopIconsHidden(false, takeOwnership: false, 2500);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("看门狗恢复桌面图标失败", ex);
        }

        return 0;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error("Dispatcher unhandled", e.Exception);
        DesktopService.EmergencyRestore();
        try
        {
            _main?.SaveNow();
        }
        catch
        {
            // ignore
        }

        MessageBox.Show("FenceDesk 遇到错误，桌面图标已恢复：\n" + e.Exception.Message,
            "FenceDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
        Shutdown(-1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DesktopService.EmergencyRestore();
        if (_ownsMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch
            {
                // ignore
            }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

