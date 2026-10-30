using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FenceDesk.Services;
using Microsoft.Win32;

namespace FenceDesk.Windows;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _host;
    private bool _initializing = true;
    private bool _busy;

    public SettingsWindow(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        Loaded += (_, _) => LoadFromState();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void LoadFromState()
    {
        _initializing = true;
        var settings = _host.State.Settings;

        HideIconsCheck.IsChecked = settings.HideDesktopIcons;
        DoubleClickCheck.IsChecked = settings.DoubleClickHide;
        AutoImportCheck.IsChecked = settings.AutoImportNew;
        IncludeHiddenCheck.IsChecked = settings.IncludeHiddenDesktopItems;
        ShowDockCheck.IsChecked = settings.ShowDock;
        AutoStartCheck.IsChecked = DesktopService.IsAutoStartEnabled();
        if (AutoStartCheck.IsChecked == true)
        {
            AutoStartStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            AutoStartStatusText.Text = "当前已开启开机启动。";
        }
        else if (!DesktopService.CanWriteAutoStart())
        {
            AutoStartStatusText.Foreground = (Brush)FindResource("WarningBrush");
            AutoStartStatusText.Text = "当前运行环境限制了启动项写入。可点击“生成启动快捷方式”，再把它拖入启动文件夹。";
        }
        else
        {
            AutoStartStatusText.Text = "";
        }

        var size = settings.IconSize;
        SizeSmall.IsChecked = size <= 37;
        SizeMedium.IsChecked = size > 37 && size < 46;
        SizeLarge.IsChecked = size >= 46;
        IconStatusText.Text = settings.HideDesktopIcons
            ? "FenceDesk 正在接管桌面图标，退出时会自动恢复。"
            : "系统桌面图标由 Windows 管理。";
        DataPathText.Text = "数据目录：" + LogService.DataDirectory;
        ActionStatusText.Text = "";
        _initializing = false;
    }

    private async void HideIconsCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing || _busy) return;
        _busy = true;
        var desired = HideIconsCheck.IsChecked == true;
        HideIconsCheck.IsEnabled = false;
        IconStatusText.Text = "正在切换系统桌面图标…";

        var ok = await _host.SetDesktopIconsVisibilityAsync(desired);
        if (!ok)
        {
            _initializing = true;
            HideIconsCheck.IsChecked = !desired;
            _initializing = false;
            IconStatusText.Text = "切换失败。请确认资源管理器正在运行，然后重试。";
        }
        else
        {
            IconStatusText.Text = desired
                ? "系统桌面图标已隐藏，退出 FenceDesk 时会恢复。"
                : "系统桌面图标已恢复显示。";
        }

        HideIconsCheck.IsEnabled = true;
        _busy = false;
    }

    private void SimpleSetting_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _host.State.Settings.DoubleClickHide = DoubleClickCheck.IsChecked == true;
        _host.State.Settings.AutoImportNew = AutoImportCheck.IsChecked == true;
        _host.State.Settings.IncludeHiddenDesktopItems = IncludeHiddenCheck.IsChecked == true;
        _host.SaveSettings();
    }

    private void ShowDockCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _host.SetDockVisible(ShowDockCheck.IsChecked == true);
    }

    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var desired = AutoStartCheck.IsChecked == true;
        if (_host.SetAutoStart(desired, out var error))
        {
            AutoStartStatusText.Foreground = (Brush)FindResource("AccentBrush");
            AutoStartStatusText.Text = desired
                ? "已开启，将在下次登录 Windows 时自动启动。"
                : "已关闭开机启动。";
            return;
        }

        _initializing = true;
        AutoStartCheck.IsChecked = !desired;
        _initializing = false;
        AutoStartStatusText.Foreground = (Brush)FindResource("DangerBrush");
        AutoStartStatusText.ToolTip = error;

        string? generateError = null;
        if (desired && _host.GenerateStartupShortcut(out var path, out generateError))
        {
            _host.OpenStartupFolder();
            AutoStartStatusText.Text =
                "自动设置被当前权限环境拒绝。已生成 FenceDesk.lnk 并打开所在文件夹；请把快捷方式拖入启动文件夹。";
            AutoStartStatusText.ToolTip = path;
        }
        else
        {
            AutoStartStatusText.Text = desired
                ? "当前运行环境不允许自动写入启动项，生成快捷方式也失败了：" + generateError
                : "取消开机启动失败：" + error;
        }
    }

    private void GenerateStartupShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_host.GenerateStartupShortcut(out var path, out var error))
        {
            AutoStartStatusText.Foreground = (Brush)FindResource("AccentBrush");
            AutoStartStatusText.Text = "已生成启动快捷方式，请把它拖入启动文件夹。";
            AutoStartStatusText.ToolTip = path;
            _host.OpenStartupFolder();
        }
        else
        {
            AutoStartStatusText.Foreground = (Brush)FindResource("DangerBrush");
            AutoStartStatusText.Text = "生成快捷方式失败：" + error;
            AutoStartStatusText.ToolTip = null;
        }
    }

    private void OpenStartup_Click(object sender, RoutedEventArgs e)
    {
        _host.OpenStartupFolder();
        AutoStartStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        AutoStartStatusText.Text = "已打开启动文件夹；把生成的快捷方式拖进去即可。";
    }

    private void SizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not System.Windows.Controls.Primitives.ToggleButton button) return;
        if (button.Tag is not string tag || !int.TryParse(tag, out var size)) return;

        SizeSmall.IsChecked = size == 34;
        SizeMedium.IsChecked = size == 42;
        SizeLarge.IsChecked = size == 48;
        _host.ApplyIconSize(size);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _host.OrganizeDesktop();
        ActionStatusText.Text = "已重新扫描桌面，新图标会自动收纳到对应围栏。";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _host.ResetLayout();
        ActionStatusText.Text = "已重新生成围栏布局。";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 FenceDesk 布局",
            Filter = "FenceDesk 布局 (*.json)|*.json",
            FileName = $"FenceDesk-layout-{DateTime.Now:yyyyMMdd-HHmm}.json",
            AddExtension = true,
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog(this) != true) return;

        _host.ExportLayoutTo(dialog.FileName);
        ActionStatusText.Text = "布局已导出：" + Path.GetFileName(dialog.FileName);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 FenceDesk 布局",
            Filter = "FenceDesk 布局 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(this,
            "导入布局会替换当前所有围栏，但不会移动或删除任何桌面文件。是否继续？",
            "FenceDesk", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var ok = await _host.ImportLayoutAsync(dialog.FileName);
        ActionStatusText.Text = ok ? "布局已导入。" : "导入失败，请检查文件内容。";
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => _host.OpenDataFolder();

    private void ClearIgnored_Click(object sender, RoutedEventArgs e)
    {
        _host.ClearIgnoredPaths();
        ActionStatusText.Text = "已恢复之前被忽略的项目。点击“重新扫描桌面”可再次收纳。";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // ignore
        }
    }
}
