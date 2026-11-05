using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FenceDesk.Models;
using FenceDesk.Services;
using Microsoft.Win32;

namespace FenceDesk.Windows;

public partial class HiddenVaultWindow : Window
{
    private readonly MainWindow _host;
    private readonly FenceModel _fence;

    public HiddenVaultWindow(MainWindow host, FenceModel fence)
    {
        InitializeComponent();
        _host = host;
        _fence = fence;

        var hasPassword = !string.IsNullOrWhiteSpace(_fence.HiddenPasswordHash);
        LockPanel.Visibility = hasPassword ? Visibility.Visible : Visibility.Collapsed;
        ContentPanel.Visibility = hasPassword ? Visibility.Collapsed : Visibility.Visible;
        PasswordButton.Content = hasPassword ? "修改密码" : "设置密码";
        Loaded += (_, _) =>
        {
            if (hasPassword) UnlockPassword.Focus();
            else RefreshItems();
        };
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (HiddenVaultService.VerifyPassword(UnlockPassword.Password,
                _fence.HiddenPasswordHash, _fence.HiddenPasswordSalt))
        {
            LockPanel.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Visible;
            UnlockPassword.Clear();
            UnlockError.Text = string.Empty;
            RefreshItems();
            return;
        }

        UnlockError.Text = "密码不正确";
        UnlockPassword.SelectAll();
        UnlockPassword.Focus();
    }

    private void RefreshItems()
    {
        var items = _fence.Items.ToList();
        ItemsList.ItemsSource = items;
        CountText.Text = $"{items.Count} 项";
        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasswordButton.Content = string.IsNullOrWhiteSpace(_fence.HiddenPasswordHash) ? "设置密码" : "修改密码";
    }

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "添加到隐藏围栏" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            _host.AddToHiddenFence(file);
        }
        RefreshItems();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "把文件夹添加到隐藏围栏" };
        if (dialog.ShowDialog(this) != true) return;
        _host.AddToHiddenFence(dialog.FolderName);
        RefreshItems();
    }

    private void OpenItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FenceItemModel item })
        {
            _host.LaunchItem(item);
        }
    }

    private void RestoreItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FenceItemModel item }) return;
        _host.RestoreHiddenItem(item);
        RefreshItems();
    }

    private void Password_Click(object sender, RoutedEventArgs e)
    {
        var hasPassword = !string.IsNullOrWhiteSpace(_fence.HiddenPasswordHash);
        if (hasPassword)
        {
            var current = PasswordDialog.Ask("验证当前密码", "请输入隐藏围栏当前密码");
            if (current is null) return;
            if (!HiddenVaultService.VerifyPassword(current,
                    _fence.HiddenPasswordHash, _fence.HiddenPasswordSalt))
            {
                _host.ShowToast("当前密码不正确");
                return;
            }
        }

        var password = PasswordDialog.Ask("设置隐藏围栏密码",
            "输入新密码；留空表示取消密码。密码只锁 FenceDesk 入口，不是文件加密。", confirm: true);
        if (password is null) return;

        if (string.IsNullOrWhiteSpace(password))
        {
            _fence.HiddenPasswordHash = null;
            _fence.HiddenPasswordSalt = null;
            _host.ShowToast("已取消隐藏围栏密码");
        }
        else
        {
            var (hash, salt) = HiddenVaultService.HashPassword(password);
            _fence.HiddenPasswordHash = hash;
            _fence.HiddenPasswordSalt = salt;
            _host.ShowToast("已设置隐藏围栏密码，请牢记；忘记后无法通过 FenceDesk 解锁");
        }

        _host.SaveNow();
        RefreshItems();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshItems();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;

        // 使用 Windows 原生的非客户区拖动，避免 WPF 捕获鼠标/设置 Left/Top
        // 在桌面子窗口所有者场景下卡死。
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
