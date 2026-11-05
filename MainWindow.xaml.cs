using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FenceDesk.Controls;
using FenceDesk.Models;
using FenceDesk.Services;
using FenceDesk.Windows;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace FenceDesk;

public partial class MainWindow : Window
{
    private const int HotkeySearch = 0x5101;
    private const int HotkeyToggle = 0x5102;

    private readonly WorkspaceStore _store = new();
    private readonly Dictionary<string, FenceControl> _fences = new();
    private readonly HashSet<string> _launchingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _portalWatchers = new();
    private readonly Dictionary<string, DispatcherTimer> _portalTimers = new();
    private readonly List<FileSystemWatcher> _desktopWatchers = new();
    private readonly SemaphoreSlim _desktopIconLock = new(1, 1);

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _desktopScanTimer;
    private readonly DispatcherTimer _desktopReconcileTimer;
    private readonly DispatcherTimer _hiddenRequestTimer;
    private readonly DispatcherTimer _zOrderTimer;
    private readonly DispatcherTimer _desktopGuardTimer;

    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private TrayService? _tray;
    private MouseHook? _mouseHook;
    private KeyboardHook? _keyboardHook;
    private FenceControl? _selectedFence;
    private FenceItemModel? _selectedItem;
    private SearchWindow? _search;
    private SettingsWindow? _settings;
    private readonly Dictionary<string, Image> _systemIconImages = new();
    private readonly Dictionary<string, TextBlock> _systemIconFallbacks = new();
    private readonly Dictionary<string, string> _systemIconSources = new();

    private bool _initialized;
    private bool _fencesHidden;
    private bool _recycleBinBusy;
    private bool _originalDesktopMode;
    private bool _originalDesktopBusy;
    private bool _hideIconsBeforeOriginal;
    private DateTime _showDesktopGuardUntil;
    private bool _allowActivation;
    private bool _desktopAttached;
    private IntPtr _desktopHost;
    private IntPtr _lastForegroundWindow;
    private DateTime _lastGuardTick;
    private DateTime _lastDesktopRefreshUtc;
    private bool _isClosing;
    private bool _closed;
    private int _zCounter;

    public AppState State { get; private set; }
    public Rect WorkAreaRect { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized &&
                DateTime.UtcNow < _showDesktopGuardUntil &&
                !_isClosing && !_originalDesktopMode)
            {
                WindowState = WindowState.Normal;
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && DateTime.UtcNow < _showDesktopGuardUntil &&
                !_isClosing && !_originalDesktopMode)
            {
                Show();
            }
        };

        WorkAreaRect = SystemParameters.WorkArea;
        Left = WorkAreaRect.Left;
        Top = WorkAreaRect.Top;
        Width = WorkAreaRect.Width;
        Height = WorkAreaRect.Height;

        State = _store.Load();
        if (!State.Settings.HasInitialized)
        {
            State = LayoutService.CreateInitialState(WorkAreaRect, DesktopService.EnumerateDesktopItems());
            State.Settings.LaunchAtLogin = DesktopService.IsAutoStartEnabled();
            _store.Save(State);
        }
        else
        {
            foreach (var fence in State.Fences)
            {
                LayoutService.ClampFence(fence, WorkAreaRect);
            }
            State.Settings.LaunchAtLogin = DesktopService.IsAutoStartEnabled();
        }

        if (State.Settings.SettingsMigrationVersion < 1)
        {
            // 1.3.18 的启动恢复曾把 HideDesktopIcons 误写成 false，导致桌面图标
            // 和围栏同时显示。这里做一次性迁移；用户之后仍可在设置里关闭。
            if (State.Settings.HasInitialized &&
                !State.Settings.HideDesktopIcons &&
                !State.Settings.OwnsHiddenIcons &&
                State.Fences.Count > 0)
            {
                State.Settings.HideDesktopIcons = true;
            }

            State.Settings.SettingsMigrationVersion = 1;
            _store.Save(State);
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        _toastTimer = new DispatcherTimer();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            FadeOutToast();
        };

        _desktopScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _desktopScanTimer.Tick += (_, _) =>
        {
            _desktopScanTimer.Stop();
            if (_isClosing) return;
            if (State.Settings.AutoImportNew)
            {
                ImportNewDesktopItems(silent: false);
            }
            else
            {
                PruneMissingItems(notify: false);
            }
        };

        _hiddenRequestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hiddenRequestTimer.Tick += (_, _) => ProcessHiddenRequests();
        _hiddenRequestTimer.Start();

        _desktopReconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _desktopReconcileTimer.Tick += (_, _) =>
        {
            if (_isClosing || !_initialized || !State.Settings.AutoImportNew) return;
            var added = ImportNewDesktopItems(silent: true);
            if (added > 0) LogService.Info($"周期扫描补充收纳 {added} 个桌面项目");
        };
        _desktopReconcileTimer.Start();

        _zOrderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _zOrderTimer.Tick += (_, _) => KeepAtBottom();

        // 桌面子窗口的 z-order 会被 Explorer 的 SHELLDLL_DefView 改变，
        // 定期把自己提回 WorkerW 的顶层，否则围栏看起来在、鼠标却点到桌面。
        _desktopGuardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _desktopGuardTimer.Tick += (_, _) =>
        {
            EnsureAboveDesktop();
            CheckDesktopSurfaceRefresh();
        };
        _desktopGuardTimer.Start();
        _lastGuardTick = DateTime.UtcNow;
        _lastDesktopRefreshUtc = DateTime.UtcNow;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += (_, _) =>
        {
            if (!_allowActivation)
            {
                Dispatcher.BeginInvoke(new Action(KeepAtBottom), DispatcherPriority.Background);
            }
        };
        Deactivated += (_, _) =>
            Dispatcher.BeginInvoke(new Action(EnsureAboveDesktop), DispatcherPriority.Background);
        Closing += OnClosing;
        Closed += OnClosed;

        Dock.MouseEnter += (_, _) =>
            Dock.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        Dock.MouseLeave += (_, _) =>
        {
            if (Dock.Visibility == Visibility.Visible)
            {
                Dock.BeginAnimation(OpacityProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(160)));
            }
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        LogService.Info("开始桌面挂载");
        AttachToDesktop();
        KeepAtBottom();
        LogService.Info("进程提权状态: " + (DesktopService.IsProcessElevated() ? "管理员" : "普通用户"));

        if (!NativeMethods.RegisterHotKey(_hwnd, HotkeySearch,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x46))
        {
            LogService.Error("注册 Ctrl+Alt+F 热键失败。Win32=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }

        if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyToggle,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x44))
        {
            LogService.Error("注册 Ctrl+Alt+D 热键失败。Win32=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RenderFences();
            BuildSystemIconsPanel();
            UpdateSystemIconsVisibility();
            UpdateOriginalDesktopButton();

            _tray = new TrayService(
                () => ToggleFences(),
                OpenSearch,
                () => OrganizeDesktop(),
                CreateNewFence,
                OpenSettings,
                ExitApp);
            _tray.UpdateHidden(_fencesHidden);

            _mouseHook = new MouseHook();
            _mouseHook.PossibleDesktopDoubleClick += screenPoint =>
                Dispatcher.BeginInvoke(new Action(() => HandleDesktopDoubleClick(screenPoint)));
            _mouseHook.ShowDesktopClicked += () =>
                Dispatcher.BeginInvoke(new Action(HandleShowDesktopClicked));
            _mouseHook.LeftButtonDown += point =>
            {
                if (!_desktopAttached) return;
                try
                {
                    Dispatcher.Invoke(new Action(() => PrepareMouseClick(point)), DispatcherPriority.Send);
                }
                catch
                {
                    // 关闭过程中忽略
                }
            };

            _keyboardHook = new KeyboardHook { TryHandle = TryHandleShortcut };

            SetupDesktopWatchers();
            _zOrderTimer.Start();
            _initialized = true;
            UpdateDockVisibility();
            ApplyIconSize(State.Settings.IconSize);

            if (!State.Settings.WelcomeShown)
            {
                State.Settings.WelcomeShown = true;
                SaveSoon();
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    var count = State.Fences.Sum(f => f.Count);
                    ShowToast(count > 0
                        ? $"已按类型收纳 {count} 个桌面图标 · 双击桌面可隐藏围栏"
                        : "欢迎使用 FenceDesk · 把文件拖进围栏即可开始整理", 3600);
                }), DispatcherPriority.ApplicationIdle);
            }

            _ = InitializeDesktopIconsAsync();
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(1600);
                if (!_isClosing) ImportNewDesktopItems(silent: true);
            }));
        }
        catch (Exception ex)
        {
            LogService.Error("初始化桌面围栏失败", ex);
            MessageBox.Show( "桌面围栏初始化失败：\n" + ex.Message, "FenceDesk",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RenderFences()
    {
        DisposePortalWatchers();
        FenceCanvas.Children.Clear();
        _fences.Clear();
        _zCounter = 0;

        foreach (var fence in State.Fences)
        {
            if (fence.IsHidden) continue;
            LayoutService.ClampFence(fence, WorkAreaRect);
            if (fence.IsPortal)
            {
                LoadPortalItems(fence, notify: false);
            }

            AddFenceControl(fence, animate: false);
            if (fence.IsPortal) EnsurePortalWatcher(fence);
        }
    }

    private void BuildSystemIconsPanel()
    {
        if (SystemIconsPanel.Children.Count > 0) return;

        AddSystemIcon("此电脑", "shell:MyComputerFolder", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "\uE7F4");
        AddSystemIcon("回收站", "shell:RecycleBinFolder", "::{645FF040-5081-101B-9F08-00AA002F954E}", "\uE74D");
        AddSystemIcon("网络", "shell:NetworkPlacesFolder", "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "\uE968");
        AddSystemIcon("用户文件夹", "shell:UserProfile",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "\uE77B");
    }

    private void AddSystemIcon(string label, string shellTarget, string iconSource, string fallbackGlyph)
    {
        var button = new Button
        {
            Style = (Style)FindResource("DesktopIconButton"),
            ToolTip = label,
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var iconGrid = new Grid { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center };
        var tile = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
        };
        var fallback = new TextBlock
        {
            Text = fallbackGlyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xE0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image
        {
            Width = 42,
            Height = 42,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        iconGrid.Children.Add(tile);
        iconGrid.Children.Add(fallback);
        iconGrid.Children.Add(image);
        stack.Children.Add(iconGrid);
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xF5)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
            MaxWidth = 74,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        button.Content = stack;
        button.ContextMenu = BuildSystemIconMenu(label, shellTarget);
        button.Click += (_, _) => OpenSystemShellItem(shellTarget);
        SystemIconsPanel.Children.Add(button);

        _systemIconImages[label] = image;
        _systemIconFallbacks[label] = fallback;
        _systemIconSources[label] = iconSource;
        _ = LoadSystemIconAsync(iconSource, image, fallback);
    }

    private async Task RefreshSystemIconAsync(string label)
    {
        if (!_systemIconImages.TryGetValue(label, out var image) ||
            !_systemIconFallbacks.TryGetValue(label, out var fallback) ||
            !_systemIconSources.TryGetValue(label, out var source))
        {
            return;
        }

        IconService.InvalidateShell(source);
        await Task.Delay(220);
        var icon = await IconService.GetShellIconAsync(source);
        if (icon is null) return;
        image.Source = icon;
        fallback.Visibility = Visibility.Collapsed;
    }

    private ContextMenu BuildSystemIconMenu(string label, string shellTarget)
    {
        var menu = new ContextMenu();
        AddSystemMenu(menu, "打开", () => OpenSystemShellItem(shellTarget));
        menu.Items.Add(new Separator());

        switch (label)
        {
            case "此电脑":
                AddSystemMenu(menu, "属性", OpenThisPcProperties);
                AddSystemMenu(menu, "管理", OpenComputerManagement);
                break;
            case "回收站":
                AddSystemMenu(menu, "清空回收站", ClearRecycleBin);
                AddSystemMenu(menu, "属性", OpenRecycleBinProperties);
                break;
            case "网络":
                AddSystemMenu(menu, "属性", OpenNetworkProperties);
                break;
            case "用户文件夹":
                AddSystemMenu(menu, "属性", () =>
                {
                    if (!DesktopService.ShowProperties(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out var error))
                    {
                        ShowToast("无法打开属性：" + error);
                    }
                });
                break;
        }

        return menu;
    }

    private static void AddSystemMenu(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void OpenThisPcProperties()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:about",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开此电脑属性失败", ex);
            ShowToast("无法打开此电脑属性");
        }
    }

    private void OpenComputerManagement()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "compmgmt.msc",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开计算机管理失败", ex);
            ShowToast("无法打开计算机管理");
        }
    }

    private async void ClearRecycleBin()
    {
        if (_recycleBinBusy) return;

        var result = ConfirmWindow.Ask("清空回收站",
            "确定清空回收站吗？\n\n回收站中的文件将被永久删除。", "清空", "取消");
        EnsureInteractiveState();
        if (!result) return;

        _recycleBinBusy = true;
        try
        {
            ShowToast("正在清空回收站…");
            var emptyResult = await Task.Run(RecycleBinService.Empty);
            ShowToast(emptyResult == RecycleBinEmptyResult.AlreadyEmpty
                ? "回收站已经是空的"
                : "回收站已清空");

            for (var i = 0; i < 3; i++)
            {
                await RefreshSystemIconAsync("回收站");
                await Task.Delay(240);
            }
        }
        catch (Exception ex)
        {
            ShowToast("清空回收站失败：" + ex.Message);
            DesktopService.PlayErrorSound();
        }
        finally
        {
            _recycleBinBusy = false;
        }
    }

    private void OpenRecycleBinProperties()
    {
        if (!DesktopService.ShowShellProperties("shell:RecycleBinFolder", out var error))
        {
            ShowToast("无法打开回收站属性：" + error);
        }
    }

    private void OpenNetworkProperties()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "control.exe",
                Arguments = "/name Microsoft.NetworkAndSharingCenter",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开网络属性失败", ex);
            ShowToast("无法打开网络属性");
        }
    }

    private static async Task LoadSystemIconAsync(string source, Image image, TextBlock fallback)
    {
        var icon = await IconService.GetShellIconAsync(source);
        if (icon is null) return;
        image.Source = icon;
        fallback.Visibility = Visibility.Collapsed;
    }

    private static void OpenSystemShellItem(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开系统图标失败: " + target, ex);
        }
    }

    private void UpdateSystemIconsVisibility()
    {
        SystemIconsPanel.Visibility =
            !_originalDesktopMode && !_fencesHidden && State.Settings.HideDesktopIcons
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private FenceControl AddFenceControl(FenceModel fence, bool animate)
    {
        var control = new FenceControl(fence, this)
        {
            IconSize = State.Settings.IconSize,
        };
        Canvas.SetLeft(control, fence.X);
        Canvas.SetTop(control, fence.Y);
        Canvas.SetZIndex(control, ++_zCounter);
        FenceCanvas.Children.Add(control);
        _fences[fence.Id] = control;

        if (animate)
        {
            control.Opacity = 0;
            control.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        return control;
    }

    public void RaiseFence(FenceControl control)
    {
        Canvas.SetZIndex(control, ++_zCounter);
    }

    public List<Rect> GetOtherFenceRects(FenceControl self)
    {
        var result = new List<Rect>();
        foreach (var control in _fences.Values)
        {
            if (ReferenceEquals(control, self)) continue;
            if (control.Visibility != Visibility.Visible) continue;
            var left = double.IsNaN(Canvas.GetLeft(control)) ? control.Model.X : Canvas.GetLeft(control);
            var top = double.IsNaN(Canvas.GetTop(control)) ? control.Model.Y : Canvas.GetTop(control);
            result.Add(new Rect(left, top, control.ActualWidth, control.ActualHeight));
        }
        return result;
    }

    public void ShowGuides(double? x, double? y)
    {
        if (x is not null)
        {
            GuideV.X1 = x.Value;
            GuideV.X2 = x.Value;
            GuideV.Y1 = 0;
            GuideV.Y2 = Math.Max(1, GuideCanvas.ActualHeight);
            GuideV.Visibility = Visibility.Visible;
        }
        else
        {
            GuideV.Visibility = Visibility.Collapsed;
        }

        if (y is not null)
        {
            GuideH.Y1 = y.Value;
            GuideH.Y2 = y.Value;
            GuideH.X1 = 0;
            GuideH.X2 = Math.Max(1, GuideCanvas.ActualWidth);
            GuideH.Visibility = Visibility.Visible;
        }
        else
        {
            GuideH.Visibility = Visibility.Collapsed;
        }
    }

    public void HideGuides()
    {
        GuideV.Visibility = Visibility.Collapsed;
        GuideH.Visibility = Visibility.Collapsed;
    }

    public void SaveSoon()
    {
        if (_isClosing) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try
        {
            State.Settings.LaunchAtLogin = DesktopService.IsAutoStartEnabled();
            _store.Save(State);
        }
        catch (Exception ex)
        {
            LogService.Error("保存状态失败", ex);
        }
    }

    public void SaveSettings()
    {
        SaveSoon();
        UpdateDockVisibility();
    }

    public bool IsDesktopAttached => _desktopAttached;

    public void RenameFence(FenceControl control, FenceModel model)
    {
        if (!InputDialog.Show("重命名围栏", "输入围栏名称", model.Title, out var title)) return;
        if (string.Equals(model.Title, title, StringComparison.Ordinal)) return;
        model.Title = title;
        control.RefreshVisualState();
        SaveSoon();
        ShowToast("围栏已重命名");
    }

    public void BeginInteractiveInput()
    {
        _allowActivation = true;
        try
        {
            Activate();
            NativeMethods.SetForegroundWindow(_hwnd);
        }
        catch
        {
            // ignore foreground lock failures
        }
        Dispatcher.BeginInvoke(new Action(() => _allowActivation = false), DispatcherPriority.ApplicationIdle);
    }

    public void ShowToast(string message, int durationMs = 2400)
    {
        if (_isClosing) return;
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
        _toastTimer.Start();
    }

    private void FadeOutToast()
    {
        var fade = new DoubleAnimation(Toast.Opacity, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    public void ToggleFences(bool? show = null)
    {
        if (_originalDesktopMode)
        {
            _ = ToggleOriginalDesktopAsync();
            return;
        }

        var shouldShow = show ?? _fencesHidden;
        if (shouldShow == !_fencesHidden) return;
        _fencesHidden = !shouldShow;

        if (shouldShow)
        {
            FenceCanvas.Visibility = Visibility.Visible;
            FenceCanvas.IsHitTestVisible = true;
            FenceCanvas.Opacity = 1;
            FenceCanvas.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });

            if (State.Settings.ShowDock)
            {
                Dock.Visibility = Visibility.Visible;
                Dock.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 0.94, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
            }

            ShowToast("桌面围栏已显示");
        }
        else
        {
            FenceCanvas.IsHitTestVisible = false;
            var fade = new DoubleAnimation(FenceCanvas.Opacity, 0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            fade.Completed += (_, _) =>
            {
                if (_fencesHidden) FenceCanvas.Visibility = Visibility.Collapsed;
            };
            FenceCanvas.BeginAnimation(OpacityProperty, fade);

            if (Dock.Visibility == Visibility.Visible)
            {
                var dockFade = new DoubleAnimation(Dock.Opacity, 0, TimeSpan.FromMilliseconds(150));
                dockFade.Completed += (_, _) =>
                {
                    if (_fencesHidden) Dock.Visibility = Visibility.Collapsed;
                };
                Dock.BeginAnimation(OpacityProperty, dockFade);
            }

            ShowToast("围栏已隐藏 · 双击桌面或 Ctrl+Alt+D 恢复", 2800);
        }

        UpdateSystemIconsVisibility();
        _tray?.UpdateHidden(_fencesHidden);
    }

    private void UpdateDockVisibility()
    {
        if (!_initialized || _fencesHidden || !State.Settings.ShowDock)
        {
            Dock.Visibility = Visibility.Collapsed;
            return;
        }

        Dock.Visibility = Visibility.Visible;
        Dock.BeginAnimation(OpacityProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(120)));
    }

    public void ApplyIconSize(int size)
    {
        State.Settings.IconSize = Math.Clamp(size, 32, 56);
        foreach (var control in _fences.Values)
        {
            control.IconSize = State.Settings.IconSize;
        }
        SaveSoon();
    }

    public void SetDockVisible(bool visible)
    {
        State.Settings.ShowDock = visible;
        UpdateDockVisibility();
        SaveSoon();
    }

    public void OpenSearch()
    {
        if (_search is { IsVisible: true })
        {
            _search.Activate();
            _search.FocusQuery();
            return;
        }

        var results = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fence in State.Fences)
        {
            if (fence.IsHidden) continue;
            foreach (var item in fence.Items)
            {
                if (!seen.Add(item.Path)) continue;
                results.Add(new SearchResult(item, fence.Title, fence.Accent));
            }
        }

        _search = new SearchWindow(this, results);
        _search.Closed += (_, _) =>
        {
            _search = null;
            Dispatcher.BeginInvoke(new Action(EnsureAboveDesktop), DispatcherPriority.Background);
        };
        _search.Show();
        _search.Activate();
        _search.FocusQuery();
    }

    public void OpenSettings()
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(this);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    public void CreateNewFence()
    {
        if (_fencesHidden) ToggleFences(true);
        var spot = FindFreeSpot(340, 300);
        var accent = State.Fences.Count == 0 ? "teal" : AccentPalette.Next(State.Fences[^1].Accent);
        var fence = new FenceModel
        {
            Title = "新建围栏",
            Accent = accent,
            X = spot.X,
            Y = spot.Y,
            Width = 340,
            Height = 300,
        };
        State.Fences.Add(fence);
        var control = AddFenceControl(fence, animate: true);
        SaveSoon();
        ShowToast("已新建围栏");
        Dispatcher.BeginInvoke(new Action(control.StartRename), DispatcherPriority.Input);
    }

    public void OrganizeDesktop()
    {
        var added = ImportNewDesktopItems(silent: false);
        if (added == 0)
        {
            ShowToast("桌面已经很整洁，没有发现新的图标");
        }
    }

    private int ImportNewDesktopItems(bool silent)
    {
        if (_isClosing) return 0;

        PruneMissingItems(notify: false);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fence in State.Fences)
        {
            foreach (var item in fence.Items)
            {
                known.Add(item.Path);
            }
        }

        var ignored = new HashSet<string>(State.IgnoredPaths, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var enumerated = 0;

        foreach (var desktopItem in DesktopService.EnumerateDesktopItems(State.Settings.IncludeHiddenDesktopItems))
        {
            enumerated++;
            if (desktopItem.Path.Contains("最新游戏下载", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Info($"导入候选: {desktopItem.Path} ignored={ignored.Contains(desktopItem.Path)} known={known.Contains(desktopItem.Path)}");
            }
            if (ignored.Contains(desktopItem.Path) || known.Contains(desktopItem.Path)) continue;
            var targetPath = desktopItem.IsDirectory
                ? null
                : DesktopService.ResolveShortcutTarget(desktopItem.Path);
            var category = CategoryRules.Classify(desktopItem.Path, desktopItem.IsDirectory, targetPath);
            var target = EnsureCategoryFence(category);
            if (target.Model.Items.Any(i => string.Equals(i.Path, desktopItem.Path, StringComparison.OrdinalIgnoreCase)))
            {
                known.Add(desktopItem.Path);
                continue;
            }

            target.Model.Items.Add(new FenceItemModel(desktopItem.Path));
            known.Add(desktopItem.Path);
            added++;
        }

        if (added > 0)
        {
            SaveSoon();
            if (!silent) ShowToast($"已收纳 {added} 个新图标");
        }

        LogService.Info($"桌面导入: 枚举 {enumerated}，新增 {added}");
        return added;
    }

    private int PruneMissingItems(bool notify)
    {
        var removed = 0;
        foreach (var fence in State.Fences)
        {
            if (fence.IsPortal) continue;
            var missing = fence.Items
                .Where(item => string.IsNullOrWhiteSpace(item.Path) || !DesktopService.PathExists(item.Path))
                .ToList();
            foreach (var item in missing)
            {
                fence.Items.Remove(item);
                IconService.Invalidate(item.Path);
                removed++;
            }
        }

        if (removed > 0)
        {
            SaveSoon();
            if (notify) ShowToast($"已清理 {removed} 个失效项目");
        }

        return removed;
    }

    public void RefreshFence(FenceControl control)
    {
        var removed = PruneMissingItems(notify: false);
        if (control.Model.IsPortal)
        {
            RefreshPortal(control);
        }
        else
        {
            control.RefreshMissing();
            control.RefreshIcons();
        }

        ShowToast(removed > 0 ? $"已清理 {removed} 个失效项目" : "围栏已刷新");
    }

    private FenceControl EnsureCategoryFence(CategoryDefinition category)
    {
        var existing = State.Fences.FirstOrDefault(f =>
            !f.IsPortal &&
            (string.Equals(f.CategoryKey, category.Key, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(f.Title, category.Title, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            if (string.IsNullOrWhiteSpace(existing.CategoryKey))
            {
                existing.CategoryKey = category.Key;
            }
            if (_fences.TryGetValue(existing.Id, out var existingControl)) return existingControl;
            return AddFenceControl(existing, animate: true);
        }

        var spot = FindFreeSpot(340, 360);
        var fence = new FenceModel
        {
            Title = category.Title,
            Accent = category.Accent,
            CategoryKey = category.Key,
            X = spot.X,
            Y = spot.Y,
            Width = 340,
            Height = 360,
        };
        State.Fences.Add(fence);
        SaveSoon();
        return AddFenceControl(fence, animate: true);
    }

    private Rect FindFreeSpot(double width, double height)
    {
        var occupied = State.Fences
            .Select(f => new Rect(f.X, f.Y, f.Width, f.Collapsed ? LayoutService.HeaderHeight : f.Height))
            .ToList();
        occupied.Add(new Rect(WorkAreaRect.Right - 330, WorkAreaRect.Bottom - 82, 330, 82));
        return LayoutService.FindFreeSpot(WorkAreaRect, occupied, width, height);
    }

    public void AddExternalFiles(FenceControl target, IEnumerable<string> files)
    {
        if (target.Model.IsPortal) return;

        var added = 0;
        foreach (var file in files)
        {
            try
            {
                var fullPath = Path.GetFullPath(file);
                if (!DesktopService.PathExists(fullPath)) continue;
                if (target.Model.Items.Any(i => string.Equals(i.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                target.Model.Items.Add(new FenceItemModel(fullPath));
                State.IgnoredPaths.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
                added++;
            }
            catch (Exception ex)
            {
                LogService.Error("添加文件失败: " + file, ex);
            }
        }

        if (added > 0)
        {
            SaveSoon();
            ShowToast($"已添加 {added} 项到「{target.Model.Title}」");
        }
        else
        {
            ShowToast("这些项目已经在围栏中了");
        }
    }

    public void MoveItemToFence(FenceDragPayload payload, FenceControl target)
    {
        if (target.Model.IsPortal) return;
        var source = State.Fences.FirstOrDefault(f => f.Id == payload.FenceId);
        if (source is null || source.Id == target.Model.Id) return;
        var item = source.Items.FirstOrDefault(i =>
            string.Equals(i.Path, payload.Path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        if (!source.IsPortal) source.Items.Remove(item);
        if (!target.Model.Items.Any(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            target.Model.Items.Add(new FenceItemModel(item.Path) { CustomName = item.CustomName });
            ShowToast($"已移动到「{target.Model.Title}」");
        }
        else
        {
            ShowToast("目标围栏中已经包含这个项目");
        }

        SaveSoon();
    }

    public void RemoveItem(FenceControl fence, FenceItemModel item)
    {
        if (fence.Model.IsPortal) return;
        if (!fence.Model.Items.Remove(item)) return;
        if (!State.IgnoredPaths.Any(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            State.IgnoredPaths.Add(item.Path);
        }
        SaveSoon();
        ShowToast("已从围栏中移除，桌面文件不会被删除");
    }

    public async void LaunchItem(FenceItemModel item)
    {
        var path = item.Path;
        if (string.IsNullOrWhiteSpace(path) || !_launchingPaths.Add(path)) return;

        try
        {
            var (ok, error) = await RunOnStaThreadAsync(() =>
            {
                var launched = DesktopService.Launch(path, out var launchError);
                return (launched, launchError);
            });

            if (ok)
            {
                item.IsMissing = false;
                return;
            }

            item.IsMissing = true;
            ShowToast("无法打开「" + item.DisplayName + "」" + (string.IsNullOrWhiteSpace(error) ? "" : "：" + error));
            DesktopService.PlayErrorSound();
        }
        catch (Exception ex)
        {
            LogService.Error("启动失败: " + path, ex);
            ShowToast("无法打开「" + item.DisplayName + "」");
        }
        finally
        {
            await Task.Delay(650);
            _launchingPaths.Remove(path);
        }
    }

    private static Task<T> RunOnStaThreadAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "FenceDesk launch",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public void RenameItem(FenceItemModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || !DesktopService.PathExists(item.Path))
        {
            RemovePathFromAllFences(item.Path);
            ShowToast("文件已不存在，已从围栏移除");
            SaveSoon();
            return;
        }

        var oldPath = item.Path;
        var isDirectory = Directory.Exists(oldPath);
        var extension = isDirectory ? string.Empty : Path.GetExtension(oldPath);
        var keepExtension = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
        var initial = !isDirectory && keepExtension ? item.DisplayName : Path.GetFileName(oldPath);

        if (!InputDialog.Show("重命名", "输入新的名称（扩展名会保留）", initial, out var name)) return;
        name = name.Trim();

        if (keepExtension &&
            name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^extension.Length].Trim();
        }

        var finalName = keepExtension ? name + extension : name;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(finalName) ||
            finalName is "." or ".." ||
            finalName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowToast("名称包含无效字符");
            return;
        }

        if (!DesktopService.RenamePath(oldPath, finalName, out var newPath, out var error) ||
            string.IsNullOrWhiteSpace(newPath))
        {
            ShowToast("重命名失败：" + error);
            DesktopService.PlayErrorSound();
            return;
        }

        State.IgnoredPaths.RemoveAll(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));

        foreach (var fence in State.Fences)
        {
            foreach (var other in fence.Items)
            {
                if (!string.Equals(other.Path, oldPath, StringComparison.OrdinalIgnoreCase)) continue;
                IconService.Invalidate(other.Path);
                other.Path = newPath;
                other.Icon = null;
                other.IconLoadStarted = false;
            }
        }

        IconService.Invalidate(oldPath);
        RefreshAllFenceIcons();
        RefreshPortalViews();
        SaveSoon();
        ShowToast("已重命名为「" + Path.GetFileName(newPath) + "」");
    }

    public void SetSelectedItem(FenceControl fence, FenceItemModel item)
    {
        _selectedFence = fence;
        _selectedItem = item;
    }

    private bool TryHandleShortcut(FenceShortcut shortcut)
    {
        if (_isClosing || _fencesHidden || _originalDesktopMode) return false;
        var item = _selectedItem;
        var fence = _selectedFence;
        if (item is null || fence is null) return false;
        if (!fence.Model.Items.Contains(item)) return false;
        if (!IsDesktopOrSelfForeground()) return false;

        switch (shortcut)
        {
            case FenceShortcut.Delete:
                Dispatcher.BeginInvoke(new Action(() => DeleteItem(item)), DispatcherPriority.Input);
                return true;
            case FenceShortcut.Copy:
                Dispatcher.BeginInvoke(new Action(() => CopySelectedItem(item)), DispatcherPriority.Input);
                return true;
            case FenceShortcut.Paste:
                Dispatcher.BeginInvoke(new Action(async () => await PasteIntoSelectedFenceAsync(fence)),
                    DispatcherPriority.Input);
                return true;
            default:
                return false;
        }
    }

    private void CopySelectedItem(FenceItemModel item)
    {
        try
        {
            if (!DesktopService.PathExists(item.Path))
            {
                ShowToast("文件已不存在");
                return;
            }

            var files = new StringCollection { item.Path };
            Clipboard.SetFileDropList(files);
            ShowToast("已复制「" + item.DisplayName + "」");
        }
        catch (Exception ex)
        {
            LogService.Error("复制文件失败", ex);
            ShowToast("复制失败：" + ex.Message);
        }
    }

    private async Task PasteIntoSelectedFenceAsync(FenceControl fence)
    {
        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                ShowToast("剪贴板里没有可粘贴的文件");
                return;
            }

            var sources = Clipboard.GetFileDropList()
                .Cast<string>()
                .Where(DesktopService.PathExists)
                .ToList();
            if (sources.Count == 0)
            {
                ShowToast("剪贴板里没有可粘贴的文件");
                return;
            }

            var destination = fence.Model.IsPortal && !string.IsNullOrWhiteSpace(fence.Model.FolderPath) &&
                              Directory.Exists(fence.Model.FolderPath)
                ? fence.Model.FolderPath
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            var copied = await Task.Run(() =>
            {
                var results = new List<string>();
                foreach (var source in sources)
                {
                    try
                    {
                        results.Add(CopyIntoFolder(source, destination));
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("粘贴失败: " + source, ex);
                    }
                }
                return results;
            });

            if (copied.Count == 0)
            {
                ShowToast("粘贴失败，没有文件被复制");
                return;
            }

            if (fence.Model.IsPortal)
            {
                LoadPortalItems(fence.Model, notify: false);
                fence.RefreshVisualState();
            }
            else
            {
                foreach (var path in copied)
                {
                    if (!fence.Model.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        fence.Model.Items.Add(new FenceItemModel(path));
                    }
                }
                RefreshAllFenceIcons();
            }

            SaveSoon();
            ShowToast($"已粘贴 {copied.Count} 个项目");
        }
        catch (Exception ex)
        {
            LogService.Error("粘贴到围栏失败", ex);
            ShowToast("粘贴失败：" + ex.Message);
        }
    }

    private static string CopyIntoFolder(string source, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        var isDirectory = Directory.Exists(source);
        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "未命名";
        var target = UniqueCopyPath(Path.Combine(destinationFolder, name), isDirectory);

        if (isDirectory)
        {
            CopyDirectoryRecursive(source, target);
        }
        else
        {
            File.Copy(source, target, overwrite: false);
        }

        return target;
    }

    private static string UniqueCopyPath(string target, bool isDirectory)
    {
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var folder = Path.GetDirectoryName(target) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(target);
        var extension = isDirectory ? string.Empty : Path.GetExtension(target);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, name + " (" + Guid.NewGuid().ToString("N") + ")" + extension);
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectoryRecursive(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
    }

    public void DeleteItem(FenceItemModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || !DesktopService.PathExists(item.Path))
        {
            RemovePathFromAllFences(item.Path);
            if (ReferenceEquals(_selectedItem, item))
            {
                _selectedItem = null;
                _selectedFence = null;
            }
            ShowToast("文件已不存在，已从围栏移除");
            SaveSoon();
            return;
        }

        var result = ConfirmWindow.Ask("删除到回收站",
            $"将「{item.DisplayName}」移到回收站？\n\n文件会进入回收站，不会立即永久删除。", "删除", "取消");
        EnsureInteractiveState();
        if (!result) return;

        var oldPath = item.Path;
        var affectedFences = State.Fences
            .Where(f => !f.IsHidden && !f.IsPortal && f.Items.Contains(item))
            .ToList();

        if (!DesktopService.DeleteToRecycleBin(oldPath, out var error))
        {
            ShowToast("删除失败：" + error);
            DesktopService.PlayErrorSound();
            return;
        }

        RemovePathFromAllFences(oldPath);
        if (ReferenceEquals(_selectedItem, item))
        {
            _selectedItem = null;
            _selectedFence = null;
        }

        var removedEmptyFences = 0;
        foreach (var fence in affectedFences.Where(f => f.Items.Count == 0).ToList())
        {
            RemoveFence(fence);
            removedEmptyFences++;
        }

        if (!State.IgnoredPaths.Any(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase)))
        {
            State.IgnoredPaths.Add(oldPath);
        }

        RefreshAllFenceIcons();
        RefreshPortalViews();
        SaveSoon();
        ShowToast(removedEmptyFences > 0 ? "已移到回收站，空围栏已自动移除" : "已移到回收站");
    }

    private void RemovePathFromAllFences(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        foreach (var fence in State.Fences)
        {
            var matches = fence.Items
                .Where(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var match in matches)
            {
                fence.Items.Remove(match);
            }
        }
        IconService.Invalidate(path);
    }

    public FenceModel EnsureHiddenFence()
    {
        var fence = State.Fences.FirstOrDefault(f => f.IsHidden);
        if (fence is not null) return fence;

        fence = new FenceModel
        {
            Title = "隐藏围栏",
            Accent = "violet",
            IsHidden = true,
            Width = 420,
            Height = 420,
        };
        State.Fences.Add(fence);
        SaveNow();
        return fence;
    }

    public FenceModel? HiddenFence => State.Fences.FirstOrDefault(f => f.IsHidden);

    private void ProcessHiddenRequests()
    {
        if (_isClosing) return;
        foreach (var path in HiddenVaultService.TakeRequests())
        {
            AddToHiddenFence(path);
        }
    }

    public bool AddToHiddenFence(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !DesktopService.PathExists(path))
        {
            ShowToast("文件或文件夹不存在");
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var fence = EnsureHiddenFence();
        if (fence.Items.Any(i => string.Equals(i.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("已经在隐藏围栏中");
            return true;
        }

        if (!HiddenVaultService.TrySetHiddenAttribute(fullPath, true, out var wasHidden, out var error))
        {
            ShowToast("无法设置隐藏属性：" + error);
            return false;
        }

        RemovePathFromAllFences(fullPath);
        fence.Items.Add(new FenceItemModel(fullPath) { WasHidden = wasHidden });
        SaveNow();
        // 不要在右键菜单点击栈里同步重建所有围栏控件，否则 WPF 可能在
        // 菜单关闭前失去鼠标捕获，表现为界面卡住直到切换应用。
        Dispatcher.BeginInvoke(new Action(RenderFences), DispatcherPriority.Background);
        ShowToast("已加入隐藏围栏");
        return true;
    }

    public void AddToHiddenFence(FenceItemModel item) => AddToHiddenFence(item.Path);

    public void RestoreHiddenItem(FenceItemModel item)
    {
        var fence = HiddenFence;
        if (fence is null) return;

        if (DesktopService.PathExists(item.Path))
        {
            HiddenVaultService.TrySetHiddenAttribute(item.Path, false, out _, out _);
        }

        fence.Items.Remove(item);
        SaveNow();
        Dispatcher.BeginInvoke(new Action(RenderFences), DispatcherPriority.Background);
        ShowToast("已取消隐藏，文件回到桌面可见状态");
    }

    public void OpenHiddenVault()
    {
        try
        {
            var fence = EnsureHiddenFence();
            var window = new HiddenVaultWindow(this, fence)
            {
                Owner = this,
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            LogService.Error("打开隐藏围栏失败", ex);
            ShowToast("打开隐藏围栏失败：" + ex.Message);
        }
    }

    public async void CompressItem(FenceItemModel item)
    {
        if (!DesktopService.PathExists(item.Path))
        {
            ShowToast("文件或文件夹已不存在");
            return;
        }

        var name = Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dialog = new SaveFileDialog
        {
            Title = "压缩为 ZIP",
            Filter = "ZIP 压缩包|*.zip",
            FileName = name + ".zip",
            DefaultExt = ".zip",
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(item.Path) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            ShowToast("正在压缩…");
            await ArchiveService.CompressAsync(item.Path, dialog.FileName);
            ShowToast("已压缩为 " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            ShowToast("压缩失败：" + ex.Message);
            DesktopService.PlayErrorSound();
        }
    }

    public async void ExtractItem(FenceItemModel item, bool chooseFolder)
    {
        if (!File.Exists(item.Path) || !ArchiveService.IsArchive(item.Path))
        {
            ShowToast("这个文件不是可解压的压缩包");
            return;
        }

        var parent = Path.GetDirectoryName(item.Path) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var stem = ArchiveStem(item.Path);
        string destinationParent = parent;

        if (chooseFolder)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择解压到哪个文件夹",
                InitialDirectory = parent,
            };
            if (dialog.ShowDialog(this) != true) return;
            destinationParent = dialog.FolderName;
        }

        Directory.CreateDirectory(destinationParent);
        var destination = UniqueDirectory(destinationParent, stem);

        try
        {
            ShowToast("正在解压…");
            await ArchiveService.ExtractAsync(item.Path, destination);
            ShowToast("已解压到 " + Path.GetFileName(destination));
            DesktopService.OpenDirectory(destination);
        }
        catch (Exception ex)
        {
            ShowToast("解压失败：" + ex.Message);
            DesktopService.PlayErrorSound();
        }
    }

    public void OpenWithItem(FenceItemModel item)
    {
        if (!DesktopService.OpenWith(item.Path, out var error))
        {
            ShowToast("无法打开“打开方式”：" + error);
        }
    }

    private static string ArchiveStem(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^7];
        }
        return Path.GetFileNameWithoutExtension(name);
    }

    private static string UniqueDirectory(string parent, string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) stem = "解压文件";
        var candidate = Path.Combine(parent, stem);
        var index = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(parent, stem + " (" + index + ")");
            index++;
        }
        return candidate;
    }

    public void ShowItemProperties(FenceItemModel item)
    {
        if (!DesktopService.ShowProperties(item.Path, out var error))
        {
            ShowToast("无法打开属性：" + error);
        }
    }

    private void RefreshAllFenceIcons()
    {
        foreach (var control in _fences.Values)
        {
            control.RefreshMissing();
            control.RefreshIcons();
        }
    }

    private void RefreshPortalViews()
    {
        foreach (var control in _fences.Values.Where(c => c.Model.IsPortal).ToList())
        {
            RefreshPortal(control);
        }
    }

    public void SortFence(FenceControl control, FenceSortMode mode)
    {
        var items = control.Model.Items.ToList();
        IEnumerable<FenceItemModel> sorted = mode switch
        {
            FenceSortMode.Type => items
                .OrderBy(i => i.IsDirectory ? "0" : Path.GetExtension(i.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            FenceSortMode.Modified => items
                .OrderByDescending(GetModifiedTime)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        };

        control.Model.Items.Clear();
        foreach (var item in sorted)
        {
            control.Model.Items.Add(item);
        }
        SaveSoon();
    }

    private static DateTime GetModifiedTime(FenceItemModel item)
    {
        try
        {
            return File.GetLastWriteTime(item.Path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    public void ConfirmDeleteFence(FenceControl control)
    {
        var fence = control.Model;
        var message = fence.IsPortal
            ? $"删除「{fence.Title}」门户围栏？\n只会移除围栏，不会删除文件夹里的任何内容。"
            : $"删除「{fence.Title}」？\n围栏里的文件不会被删除。";
        var result = ConfirmWindow.Ask("删除围栏", message, "删除", "取消");
        EnsureInteractiveState();
        if (!result) return;

        RemoveFence(fence);
        SaveNow();
        ShowToast("围栏已删除");
    }

    private void RemoveFence(FenceModel fence)
    {
        StopPortal(fence.Id);
        if (_fences.Remove(fence.Id, out var control))
        {
            FenceCanvas.Children.Remove(control);
        }
        State.Fences.Remove(fence);
    }

    public void ChoosePortalFolder(FenceControl control)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择要实时显示在围栏里的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = control.Model.FolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

        control.Model.FolderPath = dialog.SelectedPath;
        control.Model.CategoryKey = null;
        LoadPortalItems(control.Model, notify: true);
        EnsurePortalWatcher(control.Model);
        control.RefreshVisualState();
        SaveSoon();
        ShowToast("文件夹门户已创建，内容会实时更新");
    }

    public void OpenPortalFolder(FenceControl control)
    {
        if (control.Model.FolderPath is { } folder) DesktopService.OpenDirectory(folder);
    }

    public void RefreshPortal(FenceControl control)
    {
        if (!control.Model.IsPortal) return;
        LoadPortalItems(control.Model, notify: true);
        control.RefreshVisualState();
    }

    public void UnlinkPortal(FenceControl control)
    {
        if (!control.Model.IsPortal) return;
        StopPortal(control.Model.Id);
        control.Model.FolderPath = null;
        foreach (var item in control.Model.Items)
        {
            item.IsPortalItem = false;
        }
        control.Model.CategoryKey = null;
        control.RefreshVisualState();
        SaveSoon();
        ShowToast("已转为普通围栏");
    }

    private void LoadPortalItems(FenceModel fence, bool notify)
    {
        if (string.IsNullOrWhiteSpace(fence.FolderPath)) return;
        var folder = fence.FolderPath;
        if (!Directory.Exists(folder))
        {
            fence.Items.Clear();
            fence.RaiseCountChanged();
            if (notify) ShowToast("文件夹不存在或无法访问");
            return;
        }

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(folder)
                .Where(IsVisibleEntry)
                .Select(path => new
                {
                    Path = path,
                    IsDirectory = Directory.Exists(path),
                    Name = Path.GetFileName(path),
                })
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(400)
                .ToList();

            fence.Items.Clear();
            foreach (var entry in entries)
            {
                fence.Items.Add(new FenceItemModel(entry.Path, portalItem: true));
            }
            fence.RaiseCountChanged();
        }
        catch (Exception ex)
        {
            LogService.Error("读取门户文件夹失败: " + folder, ex);
            if (notify) ShowToast("读取文件夹失败：" + ex.Message);
        }
    }

    private static bool IsVisibleEntry(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("~$", StringComparison.Ordinal)) return false;
            var attrs = File.GetAttributes(path);
            return !attrs.HasFlag(FileAttributes.Hidden) && !attrs.HasFlag(FileAttributes.System);
        }
        catch
        {
            return false;
        }
    }

    private void EnsurePortalWatcher(FenceModel fence)
    {
        StopPortal(fence.Id);
        if (!fence.IsPortal || string.IsNullOrWhiteSpace(fence.FolderPath) || !Directory.Exists(fence.FolderPath))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(fence.FolderPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            FileSystemEventHandler handler = (_, _) => Dispatcher.BeginInvoke(new Action(() => SchedulePortalRefresh(fence)));
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Changed += handler;
            watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(new Action(() => SchedulePortalRefresh(fence)));
            watcher.Error += (_, args) => LogService.Error("门户监视失败: " + fence.FolderPath, args.GetException());
            _portalWatchers[fence.Id] = watcher;
        }
        catch (Exception ex)
        {
            LogService.Error("创建门户监视失败: " + fence.FolderPath, ex);
        }
    }

    private void SchedulePortalRefresh(FenceModel fence)
    {
        if (_isClosing) return;
        if (!_portalTimers.TryGetValue(fence.Id, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_isClosing) return;
                if (_fences.TryGetValue(fence.Id, out var control))
                {
                    LoadPortalItems(fence, notify: false);
                    control.RefreshVisualState();
                }
            };
            _portalTimers[fence.Id] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private void StopPortal(string fenceId)
    {
        if (_portalWatchers.Remove(fenceId, out var watcher))
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        if (_portalTimers.Remove(fenceId, out var timer))
        {
            timer.Stop();
        }
    }

    private void SetupDesktopWatchers()
    {
        foreach (var directory in DesktopService.DesktopDirectories())
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                FileSystemEventHandler handler = (_, _) => ScheduleDesktopScan();
                watcher.Created += handler;
                watcher.Deleted += handler;
                watcher.Changed += handler;
                watcher.Renamed += (_, _) => ScheduleDesktopScan();
                watcher.Error += (_, args) => LogService.Error("桌面监视失败: " + directory, args.GetException());
                _desktopWatchers.Add(watcher);
            }
            catch (Exception ex)
            {
                LogService.Error("创建桌面监视失败: " + directory, ex);
            }
        }
    }

    private void ScheduleDesktopScan()
    {
        if (_isClosing || !_initialized) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _desktopScanTimer.Stop();
            _desktopScanTimer.Start();
        }));
    }

    private void HandleDesktopDoubleClick(Point screenPoint)
    {
        if (_isClosing || _originalDesktopMode || !State.Settings.DoubleClickHide) return;
        if (!IsDesktopOrSelfForeground()) return;
        if (IsPointOnFenceOrDock(screenPoint)) return;
        ToggleFences();
    }

    private void HandleShowDesktopClicked()
    {
        if (_isClosing || _originalDesktopMode) return;
        _showDesktopGuardUntil = DateTime.UtcNow.AddSeconds(2.5);
        _ = RestoreAfterShowDesktopAsync();
    }

    private async Task RestoreAfterShowDesktopAsync()
    {
        // Windows 的“显示桌面”可能会把顶层窗口最小化甚至隐藏；围栏本身要保持显示。
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(120);
            if (_isClosing || _originalDesktopMode) return;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            if (!_fencesHidden)
            {
                FenceCanvas.Visibility = Visibility.Visible;
                FenceCanvas.IsHitTestVisible = true;
                Dock.Visibility = State.Settings.ShowDock ? Visibility.Visible : Visibility.Collapsed;
                SystemIconsPanel.Visibility = State.Settings.HideDesktopIcons
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            EnsureAboveDesktop();
        }
    }

    private void EnsureAboveDesktop()
    {
        if (_hwnd == IntPtr.Zero || _isClosing) return;

        if (_desktopAttached)
        {
            EnsureDesktopHost();
        }

        if (!NativeMethods.IsWindowVisible(_hwnd))
        {
            NativeMethods.ShowWindow(_hwnd, 5);
        }

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private void EnsureDesktopHost()
    {
        if (_hwnd == IntPtr.Zero || _isClosing) return;

        var defView = NativeMethods.GetDesktopListView();
        if (defView == IntPtr.Zero) return;
        var host = NativeMethods.GetParent(defView);
        if (host == IntPtr.Zero || !NativeMethods.IsWindow(host)) return;

        var currentParent = NativeMethods.GetParent(_hwnd);
        if (currentParent == host) return;

        // Explorer 在“显示桌面”、切换桌面等操作后会重建 WorkerW。
        // 原宿主销毁后围栏会变成不可点击的孤儿子窗口，必须重新挂载。
        LogService.Info("桌面宿主变化，重新挂载: " + currentParent + " -> " + host);
        AttachToDesktop();
    }

    private void CheckDesktopSurfaceRefresh()
    {
        var now = DateTime.UtcNow;
        var gap = now - _lastGuardTick;
        _lastGuardTick = now;

        var foreground = NativeMethods.GetForegroundWindow();
        var previousForeground = _lastForegroundWindow;
        _lastForegroundWindow = foreground;

        var wasDesktop = previousForeground == _hwnd || NativeMethods.IsDesktopWindow(previousForeground);
        var desktopVisible = foreground == _hwnd || NativeMethods.IsDesktopWindow(foreground);
        var enteredDesktop = desktopVisible && !wasDesktop;
        var uiWasStalled = gap > TimeSpan.FromSeconds(2);
        if (!enteredDesktop && !(desktopVisible && uiWasStalled)) return;
        if (now - _lastDesktopRefreshUtc < TimeSpan.FromSeconds(10)) return;

        _lastDesktopRefreshUtc = now;
        LogService.Info(uiWasStalled
            ? "检测到界面短暂停顿，刷新桌面壁纸"
            : "检测到桌面重新成为前台，刷新桌面壁纸");
        _ = RefreshDesktopSurfaceAsync();
    }

    private async Task RefreshDesktopSurfaceAsync()
    {
        await Task.Run(() => DesktopService.RefreshDesktopSurface(out _));
        if (_isClosing) return;
        InvalidateVisual();
        FenceCanvas.InvalidateVisual();
    }

    private void EnsureInteractiveState()
    {
        Mouse.Capture(null);
        EnsureAboveDesktop();
    }

    private void PrepareMouseClick(Point screenPoint)
    {
        if (_isClosing || !ShouldPrepareMouseClick(screenPoint)) return;

        if (!IsEnabled)
        {
            Mouse.Capture(null);
        }

        EnsureAboveDesktop();

        if (IsEnabled) return;
        var modalOpen = Application.Current.Windows.OfType<Window>().Any(w =>
            w != this && w.IsVisible && (w is ConfirmWindow || w is InputDialog));
        if (!modalOpen)
        {
            LogService.Info("鼠标点击前恢复主窗口交互状态");
            IsEnabled = true;
        }
    }

    private bool ShouldPrepareMouseClick(Point screenPoint)
    {
        try
        {
            var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT(
                (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
            if (hwnd == IntPtr.Zero) return false;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)Environment.ProcessId)
            {
                // 围栏本身已经是最上层且可用时，不要在点击前做任何 z-order/捕获操作。
                // 只有窗口被禁用时才需要恢复；右键菜单等弹窗一律跳过。
                return hwnd == _hwnd && !IsEnabled;
            }

            var root = NativeMethods.GetAncestor(hwnd, 2);
            return NativeMethods.IsDesktopWindow(hwnd) ||
                   (root != IntPtr.Zero && NativeMethods.IsDesktopWindow(root));
        }
        catch
        {
            return false;
        }
    }

    private bool IsDesktopOrSelfForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return foreground == _hwnd || NativeMethods.IsDesktopWindow(foreground);
    }

    private bool IsPointOnFenceOrDock(Point screenPoint)
    {
        try
        {
            var point = PointFromScreen(screenPoint);
            return IsPointOnFenceOrDockLocal(point);
        }
        catch
        {
            return false;
        }
    }

    private bool IsPointOnFenceOrDockLocal(Point point)
    {
        if (_originalDesktopMode)
        {
            if (Dock.Visibility != Visibility.Visible) return false;
            var dockTopLeft = Dock.TranslatePoint(new Point(0, 0), this);
            return new Rect(dockTopLeft.X, dockTopLeft.Y, Dock.ActualWidth, Dock.ActualHeight)
                .Contains(point);
        }

        foreach (var control in _fences.Values)
        {
            if (control.Visibility != Visibility.Visible) continue;
            var topLeft = control.TranslatePoint(new Point(0, 0), this);
            var rect = new Rect(topLeft.X, topLeft.Y, control.ActualWidth, control.ActualHeight);
            if (rect.Contains(point)) return true;
        }

        if (Dock.Visibility == Visibility.Visible)
        {
            var topLeft = Dock.TranslatePoint(new Point(0, 0), this);
            if (new Rect(topLeft.X, topLeft.Y, Dock.ActualWidth, Dock.ActualHeight).Contains(point)) return true;
        }

        if (SystemIconsPanel.Visibility == Visibility.Visible)
        {
            var topLeft = SystemIconsPanel.TranslatePoint(new Point(0, 0), this);
            if (new Rect(topLeft.X, topLeft.Y, SystemIconsPanel.ActualWidth, SystemIconsPanel.ActualHeight)
                .Contains(point)) return true;
        }

        return false;
    }

    private bool IsInteractivePoint(Point point)
    {
        if (!_initialized || _fencesHidden) return false;
        return IsPointOnFenceOrDockLocal(point);
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        if (IsPointOnFenceOrDockLocal(position)) return;
        ShowDesktopMenu();
        e.Handled = true;
    }

    private void ShowDesktopMenu()
    {
        var menu = new ContextMenu();
        AddMenu(menu, "新建围栏", CreateNewFence);
        AddMenu(menu, "整理桌面", OrganizeDesktop);
        AddMenu(menu, "搜索围栏    Ctrl+Alt+F", OpenSearch);
        menu.Items.Add(new Separator());
        AddMenu(menu, "设置", OpenSettings);
        AddMenu(menu, _fencesHidden ? "显示围栏" : "隐藏围栏", () => ToggleFences());
        AddMenu(menu, State.Settings.HideDesktopIcons ? "显示系统桌面图标" : "隐藏系统桌面图标",
            () => _ = SetDesktopIconsVisibilityAsync(!State.Settings.HideDesktopIcons));
        menu.Items.Add(new Separator());
        AddMenu(menu, "退出 FenceDesk", ExitApp);

        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static MenuItem AddMenu(ItemsControl parent, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        parent.Items.Add(item);
        return item;
    }

    private void AttachToDesktop()
    {
        try
        {
            var defView = NativeMethods.GetDesktopListView();
            _desktopHost = defView != IntPtr.Zero
                ? NativeMethods.GetParent(defView)
                : NativeMethods.FindWindow("Progman", null);
            if (_desktopHost == IntPtr.Zero)
            {
                LogService.Info("桌面挂载跳过：未找到 Progman/WorkerW");
                return;
            }

            var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE).ToInt64();
            style |= NativeMethods.WS_CHILD;
            style &= ~NativeMethods.WS_POPUP;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

            var previous = NativeMethods.SetParent(_hwnd, _desktopHost);
            if (previous == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
            {
                LogService.Error("桌面挂载失败: Win32=" + Marshal.GetLastWin32Error());
                return;
            }

            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0,
                (int)Width, (int)Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW |
                NativeMethods.SWP_FRAMECHANGED);
            _desktopAttached = true;
            LogService.Info("已挂载到桌面窗口: " + _desktopHost);
        }
        catch (Exception ex)
        {
            LogService.Error("桌面挂载异常", ex);
        }
    }

    private void KeepAtBottom()
    {
        if (_hwnd == IntPtr.Zero || _isClosing) return;

        if (_desktopAttached)
        {
            EnsureAboveDesktop();
            return;
        }

        // 桌面成为前台时不要把围栏沉到桌面下面，否则“显示桌面”后围栏会看不到。
        if (NativeMethods.IsDesktopWindow(NativeMethods.GetForegroundWindow()))
        {
            return;
        }

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                if (!_desktopAttached && !_allowActivation)
                {
                    handled = true;
                    return new IntPtr(NativeMethods.MA_NOACTIVATE);
                }
                break;
            case NativeMethods.WM_NCHITTEST:
            {
                if (TryGetScreenPoint(lParam, out var screenPoint))
                {
                    try
                    {
                        var point = PointFromScreen(screenPoint);
                        if (!IsInteractivePoint(point))
                        {
                            handled = true;
                            return new IntPtr(NativeMethods.HTTRANSPARENT);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
                break;
            }
            case NativeMethods.WM_HOTKEY:
            {
                var id = wParam.ToInt32();
                if (id == HotkeySearch)
                {
                    handled = true;
                    Dispatcher.BeginInvoke(new Action(OpenSearch));
                }
                else if (id == HotkeyToggle)
                {
                    handled = true;
                    Dispatcher.BeginInvoke(new Action(() => ToggleFences()));
                }
                break;
            }
            case 0x007E: // WM_DISPLAYCHANGE
            case 0x001A: // WM_SETTINGCHANGE
                Dispatcher.BeginInvoke(new Action(UpdateWorkArea), DispatcherPriority.Background);
                break;
        }

        return IntPtr.Zero;
    }

    private static bool TryGetScreenPoint(IntPtr lParam, out Point point)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        point = new Point(x, y);
        return true;
    }

    private void UpdateWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (area.Equals(WorkAreaRect)) return;

        WorkAreaRect = area;
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;

        foreach (var fence in State.Fences)
        {
            LayoutService.ClampFence(fence, area);
            if (_fences.TryGetValue(fence.Id, out var control))
            {
                Canvas.SetLeft(control, fence.X);
                Canvas.SetTop(control, fence.Y);
                control.Width = Math.Min(fence.Width, Math.Max(250, area.Width - 24));
                if (!fence.Collapsed) control.Height = Math.Min(fence.Height, Math.Max(150, area.Height - 40));
            }
        }

        KeepAtBottom();
        SaveSoon();
    }

    public async Task<bool> SetDesktopIconsVisibilityAsync(bool hidden)
    {
        return await ApplyDesktopIconStateAsync(hidden, takeOwnership: hidden, notify: true);
    }

    private async Task InitializeDesktopIconsAsync()
    {
        await Task.Delay(250);
        if (_isClosing) return;

        var wantHidden = State.Settings.HideDesktopIcons;

        if (DesktopService.AreDesktopIconsHidden())
        {
            // 只恢复图标，不修改用户的 HideDesktopIcons 设置。
            LogService.Info("启动恢复桌面图标可见状态");
            await Task.Run(() => DesktopService.SetDesktopIconsHidden(false, takeOwnership: false, 1800));
            State.Settings.OwnsHiddenIcons = false;
            SaveSoon();
        }

        if (wantHidden)
        {
            if (DesktopService.AreDesktopIconsHidden())
            {
                if (State.Settings.OwnsHiddenIcons)
                {
                    DesktopService.AdoptOwnershipIfHidden();
                    SaveSoon();
                }
            }
            else
            {
                await ApplyDesktopIconStateAsync(true, takeOwnership: true, notify: false);
            }
        }
        else if (State.Settings.OwnsHiddenIcons)
        {
            await ApplyDesktopIconStateAsync(false, takeOwnership: false, notify: false);
        }
    }

    private async Task<bool> ApplyDesktopIconStateAsync(bool hidden, bool takeOwnership, bool notify)
    {
        await _desktopIconLock.WaitAsync();
        try
        {
            var ok = await Task.Run(() => DesktopService.SetDesktopIconsHidden(hidden, takeOwnership, 1800));
            if (ok)
            {
                State.Settings.HideDesktopIcons = hidden;
                State.Settings.OwnsHiddenIcons = DesktopService.OwnsHiddenIcons;
                UpdateSystemIconsVisibility();
                SaveSoon();
                if (notify)
                {
                    ShowToast(hidden ? "已隐藏系统桌面图标" : "已显示系统桌面图标");
                }
            }
            else if (notify)
            {
                ShowToast("切换桌面图标失败，请稍后重试");
            }

            return ok;
        }
        catch (Exception ex)
        {
            LogService.Error("切换桌面图标异常", ex);
            if (notify) ShowToast("切换桌面图标失败：" + ex.Message);
            return false;
        }
        finally
        {
            _desktopIconLock.Release();
        }
    }

    public bool SetAutoStart(bool enabled, out string? error)
    {
        if (!DesktopService.SetAutoStart(enabled, out error))
        {
            var directError = error;
            if (!DesktopService.TryElevateAutoStart(enabled, out var elevatedError))
            {
                error = (directError ?? "直接写入失败") + "；提权设置：" + elevatedError;
                ShowToast("设置开机启动失败：" + error);
                return false;
            }
        }

        State.Settings.LaunchAtLogin = enabled;
        SaveSoon();
        error = null;
        return true;
    }

    public void OpenStartupFolder() => DesktopService.OpenStartupFolder();

    public bool GenerateStartupShortcut(out string? path, out string? error)
        => DesktopService.GenerateStartupShortcut(out path, out error);

    public void ExportLayoutTo(string path)
    {
        try
        {
            _store.Export(State, path);
            ShowToast("布局已导出");
        }
        catch (Exception ex)
        {
            LogService.Error("导出布局失败", ex);
            ShowToast("导出失败：" + ex.Message);
        }
    }

    public async Task<bool> ImportLayoutAsync(string path)
    {
        try
        {
            var imported = _store.Import(path);
            var currentSettings = State.Settings;
            State.Fences = imported.Fences;
            State.IgnoredPaths = imported.IgnoredPaths;
            State.Settings = currentSettings;
            State.Settings.HasInitialized = true;
            foreach (var fence in State.Fences) LayoutService.ClampFence(fence, WorkAreaRect);
            RenderFences();
            SaveNow();
            ShowToast("布局已导入");
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("导入布局失败", ex);
            ShowToast("导入失败：" + ex.Message);
            return false;
        }
    }

    public void ResetLayout()
    {
        var result = ConfirmWindow.Ask("重新扫描桌面",
            "重新扫描桌面并按类型重建所有围栏？\n当前围栏布局会被替换，桌面文件不会被移动或删除。", "重建", "取消");
        EnsureInteractiveState();
        if (!result) return;

        var fresh = LayoutService.CreateInitialState(WorkAreaRect,
            DesktopService.EnumerateDesktopItems(State.Settings.IncludeHiddenDesktopItems));
        var hiddenFences = State.Fences.Where(f => f.IsHidden).ToList();
        State.Fences = fresh.Fences;
        State.Fences.AddRange(hiddenFences);
        State.IgnoredPaths.Clear();
        State.Settings.HasInitialized = true;
        RenderFences();
        SaveNow();
        ShowToast("已重新整理桌面");
    }

    public void OpenDataFolder() => DesktopService.OpenDataDirectory();

    public void ClearIgnoredPaths()
    {
        State.IgnoredPaths.Clear();
        SaveSoon();
        ShowToast("已恢复之前从围栏移除的项目，重新整理桌面即可再次收纳");
    }

    private void DockAdd_Click(object sender, RoutedEventArgs e) => CreateNewFence();

    private void DockOrganize_Click(object sender, RoutedEventArgs e) => OrganizeDesktop();

    private void DockSearch_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void DockSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void DockHiddenVault_Click(object sender, RoutedEventArgs e) => OpenHiddenVault();

    private void DockHide_Click(object sender, RoutedEventArgs e)
    {
        if (_originalDesktopMode)
        {
            _ = ToggleOriginalDesktopAsync();
            return;
        }

        ToggleFences(false);
    }

    private async void DockOriginalDesktop_Click(object sender, RoutedEventArgs e)
    {
        await ToggleOriginalDesktopAsync();
    }

    private async Task ToggleOriginalDesktopAsync()
    {
        if (_isClosing || _originalDesktopBusy) return;
        _originalDesktopBusy = true;
        try
        {
            if (_originalDesktopMode)
            {
                await ExitOriginalDesktopAsync();
            }
            else
            {
                await EnterOriginalDesktopAsync();
            }
        }
        finally
        {
            _originalDesktopBusy = false;
        }
    }

    private async Task EnterOriginalDesktopAsync()
    {
        _hideIconsBeforeOriginal = State.Settings.HideDesktopIcons;
        _originalDesktopMode = true;
        FenceCanvas.Visibility = Visibility.Collapsed;
        SystemIconsPanel.Visibility = Visibility.Collapsed;
        UpdateOriginalDesktopButton();

        if (_hideIconsBeforeOriginal)
        {
            var ok = await SetDesktopIconsForOriginalAsync(showOriginal: true);
            if (!ok)
            {
                _originalDesktopMode = false;
                FenceCanvas.Visibility = Visibility.Visible;
                UpdateSystemIconsVisibility();
                UpdateOriginalDesktopButton();
                ShowToast("切换原桌面失败，请稍后重试");
                return;
            }
        }

        ShowToast("已切换到原桌面 · 点击桌面按钮返回围栏");
    }

    private async Task ExitOriginalDesktopAsync()
    {
        _originalDesktopMode = false;
        FenceCanvas.Visibility = Visibility.Visible;
        UpdateSystemIconsVisibility();
        UpdateOriginalDesktopButton();

        if (_hideIconsBeforeOriginal)
        {
            await SetDesktopIconsForOriginalAsync(showOriginal: false);
        }

        ShowToast("已返回 FenceDesk 围栏桌面");
    }

    private async Task<bool> SetDesktopIconsForOriginalAsync(bool showOriginal)
    {
        await _desktopIconLock.WaitAsync();
        try
        {
            var hidden = !showOriginal;
            var ok = await Task.Run(() =>
                DesktopService.SetDesktopIconsHidden(hidden, takeOwnership: hidden, 1800));
            if (ok)
            {
                State.Settings.OwnsHiddenIcons = DesktopService.OwnsHiddenIcons;
                SaveSoon();
            }
            return ok;
        }
        finally
        {
            _desktopIconLock.Release();
        }
    }

    private void UpdateOriginalDesktopButton()
    {
        if (DockOriginalDesktopGlyph == null) return;
        DockOriginalDesktop.ToolTip = _originalDesktopMode
            ? "返回 FenceDesk 围栏桌面"
            : "切换到原桌面（显示 Windows 桌面图标）";
        DockOriginalDesktopGlyph.Foreground = (Brush)FindResource(
            _originalDesktopMode ? "AccentBrush" : "TextPrimaryBrush");
    }

    private void DisposePortalWatchers()
    {
        foreach (var watcher in _portalWatchers.Values)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // ignore
            }
        }
        _portalWatchers.Clear();

        foreach (var timer in _portalTimers.Values) timer.Stop();
        _portalTimers.Clear();
    }

    private void DisposeDesktopWatchers()
    {
        foreach (var watcher in _desktopWatchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // ignore
            }
        }
        _desktopWatchers.Clear();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        _saveTimer.Stop();
        _toastTimer.Stop();
        _desktopScanTimer.Stop();
        _zOrderTimer.Stop();
        DisposeDesktopWatchers();
        DisposePortalWatchers();

        try
        {
            SaveNow();
        }
        catch
        {
            // ignore
        }

        try
        {
            DesktopService.RestoreIconsIfOwned();
            State.Settings.OwnsHiddenIcons = false;
            _store.Save(State);
        }
        catch (Exception ex)
        {
            LogService.Error("退出时恢复桌面图标失败", ex);
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeySearch);
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyToggle);
        }

        _mouseHook?.Dispose();
        _mouseHook = null;
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        _tray?.Dispose();
        _tray = null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _search?.Close();
        _settings?.Close();
        DesktopService.EmergencyRestore();
        Application.Current.Shutdown();
    }

    private void ExitApp()
    {
        Close();
    }
}
