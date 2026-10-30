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

