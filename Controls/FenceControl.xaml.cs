using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FenceDesk.Models;
using FenceDesk.Services;

namespace FenceDesk.Controls;

public partial class FenceControl : UserControl
{
    private readonly FenceModel _model;
    private readonly MainWindow _host;

    private bool _draggingFence;
    private Point _fenceDragStart;
    private double _fenceStartX;
    private double _fenceStartY;

    private bool _resizing;
    private Point _resizeStart;
    private double _resizeStartWidth;
    private double _resizeStartHeight;

    private FenceItemModel? _pressedItem;
    private Point _itemPressPoint;
    private bool _itemDragging;

    private bool _renaming;
    private bool _itemsSubscribed;
    private bool _unloaded;
    private bool _loadingIcons;
    private bool _iconReloadQueued;

    public FenceModel Model => _model;

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FenceControl), new PropertyMetadata(42.0));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public FenceControl(FenceModel model, MainWindow host)
    {
        _model = model;
        _host = host;

        InitializeComponent();
        DataContext = model;
        Width = model.Width;
        Height = model.Collapsed ? LayoutService.HeaderHeight : model.Height;

        ApplyAccent();
        ApplyLocked();
        ApplyCollapsed(false);
        UpdateEmptyState();
        UpdatePortalState();

        _model.Items.CollectionChanged += Items_CollectionChanged;
        _itemsSubscribed = true;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateScrollFade), DispatcherPriority.Background);
        Shell.MouseEnter += (_, _) => UpdateHoverState(true);
        Shell.MouseLeave += (_, _) => UpdateHoverState(false);
        Header.Cursor = Cursors.SizeAll;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _unloaded = false;
        ApplyLocked();
        ApplyCollapsed(false);
        RefreshMissing();
        RefreshIconsAsync();
        Dispatcher.BeginInvoke(new Action(UpdateScrollFade), DispatcherPriority.ApplicationIdle);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _unloaded = true;
        if (_itemsSubscribed)
        {
            _model.Items.CollectionChanged -= Items_CollectionChanged;
            _itemsSubscribed = false;
        }
    }

    private void Items_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
        UpdatePortalState();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshMissing();
            RefreshIconsAsync();
            UpdateScrollFade();
        }));
    }

    public void RefreshVisualState()
    {
        ApplyAccent();
        ApplyLocked();
        ApplyCollapsed(false);
        UpdateEmptyState();
        UpdatePortalState();
        RefreshMissing();
        RefreshIconsAsync();
    }

    public void StartRename() => BeginRename();

    public void RefreshIcons()
    {
        foreach (var item in _model.Items)
        {
            item.IconLoadStarted = false;
            IconService.Invalidate(item.Path);
        }

        RefreshIconsAsync();
    }

    public void RefreshMissing()
    {
        foreach (var item in _model.Items)
        {
            item.IsMissing = !DesktopService.PathExists(item.Path);
        }
    }

    private async void RefreshIconsAsync()
    {
        if (_loadingIcons)
        {
            _iconReloadQueued = true;
            return;
        }

        _loadingIcons = true;
        try
        {
            do
            {
                _iconReloadQueued = false;
                foreach (var item in _model.Items.ToList())
                {
                    if (_unloaded) return;
                    if (item.Icon is not null || item.IconLoadStarted) continue;
                    item.IconLoadStarted = true;
                    var target = item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                        ? DesktopService.ResolveShortcutTarget(item.Path)
                        : null;
                    var icon = await IconService.GetAsync(item.Path, target);
                    if (icon is not null && !_unloaded)
                    {
                        item.Icon = icon;
                    }
                    else if (icon is null)
                    {
                        // 允许下次刷新重试，避免一次失败后图标永久空白。
                        item.IconLoadStarted = false;
                    }
                }
            } while (_iconReloadQueued);
        }
        catch (Exception ex)
        {
            LogService.Error("刷新围栏图标失败", ex);
        }
        finally
        {
            _loadingIcons = false;
        }
    }

    private void UpdateEmptyState()
    {
        EmptyHint.Visibility = _model.Count == 0 && !_model.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        EmptyHintText.Text = _model.IsPortal ? "这个文件夹是空的" : "拖入文件或快捷方式";
        UpdateScrollFade();
    }

    private void UpdateScrollFade()
    {
        if (!IsLoaded || _model.Count == 0 || _model.Collapsed)
        {
            BottomFade.Visibility = Visibility.Collapsed;
            return;
        }

        var canScroll = ItemsScroll.ScrollableHeight > 1;
        var atBottom = ItemsScroll.VerticalOffset >= ItemsScroll.ScrollableHeight - 1;
        BottomFade.Visibility = canScroll && !atBottom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ItemsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateScrollFade();
    }

    private void UpdatePortalState()
    {
        PortalGlyph.Visibility = _model.IsPortal ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
    }

    private void ApplyAccent()
    {
        var accent = AccentPalette.Get(_model.Accent);
        AccentBar.Background = accent.Brush;
        CountBadge.Background = accent.SoftBrush;
        CountText.Foreground = accent.Brush;
        PortalGlyph.Foreground = accent.Brush;
        var border = new SolidColorBrush(Color.FromArgb(0x3A, accent.Color.R, accent.Color.G, accent.Color.B));
        border.Freeze();
        Shell.BorderBrush = border;
    }

    private void ApplyLocked()
    {
        if (!IsLoaded) return;
        ResizeGrip.Visibility = _model.Locked || _model.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        Header.Cursor = _model.Locked ? Cursors.Arrow : Cursors.SizeAll;
    }

    private void UpdateHoverState(bool hover)
    {
        var accent = AccentPalette.Get(_model.Accent);
        var alpha = hover ? (byte)0x68 : (byte)0x3A;
        var border = new SolidColorBrush(Color.FromArgb(alpha, accent.Color.R, accent.Color.G, accent.Color.B));
        border.Freeze();
        Shell.BorderBrush = border;
    }

    public void ApplyCollapsed(bool animate)
    {
        CollapseGlyph.Text = _model.Collapsed ? "\uE70D" : "\uE70E";
        ResizeGrip.Visibility = _model.Locked || _model.Collapsed ? Visibility.Collapsed : Visibility.Visible;

        if (!_model.Collapsed)
        {
            Body.Visibility = Visibility.Visible;
        }

        var target = _model.Collapsed ? LayoutService.HeaderHeight : _model.Height;

        if (!animate || !IsLoaded)
        {
            BeginAnimation(HeightProperty, null);
            Height = target;
            Body.Visibility = _model.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            UpdateEmptyState();
            return;
        }

        var from = ActualHeight > 1 ? ActualHeight : Height;
        BeginAnimation(HeightProperty, null);
        Height = from;
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            BeginAnimation(HeightProperty, null);
            Height = target;
            if (_model.Collapsed) Body.Visibility = Visibility.Collapsed;
            UpdateEmptyState();
        };
        BeginAnimation(HeightProperty, animation);
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _model.Collapsed = !_model.Collapsed;
        ApplyCollapsed(true);
        _host.SaveSoon();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenFenceMenu(false);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _host.RaiseFence(this);
        if (_renaming) return;

        if (e.ClickCount == 2)
        {
            _model.Collapsed = !_model.Collapsed;
            ApplyCollapsed(true);
            _host.SaveSoon();
            e.Handled = true;
            return;
        }

        if (_model.Locked) return;

        _fenceDragStart = e.GetPosition(_host.FenceCanvas);
        _fenceStartX = double.IsNaN(Canvas.GetLeft(this)) ? _model.X : Canvas.GetLeft(this);
        _fenceStartY = double.IsNaN(Canvas.GetTop(this)) ? _model.Y : Canvas.GetTop(this);
        _draggingFence = true;
        Header.CaptureMouse();
        e.Handled = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingFence || e.LeftButton != MouseButtonState.Pressed) return;

        var point = e.GetPosition(_host.FenceCanvas);
        var proposed = new Rect(
            _fenceStartX + point.X - _fenceDragStart.X,
            _fenceStartY + point.Y - _fenceDragStart.Y,
            ActualWidth,
            ActualHeight);

        var snap = LayoutService.SnapMove(
            proposed,
            _host.GetOtherFenceRects(this),
            _host.WorkAreaRect,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        var clamped = LayoutService.ClampPosition(snap.Bounds.X, snap.Bounds.Y, ActualWidth, ActualHeight, _host.WorkAreaRect);

        Canvas.SetLeft(this, clamped.X);
        Canvas.SetTop(this, clamped.Y);
        _host.ShowGuides(snap.GuideX, snap.GuideY);
        e.Handled = true;
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndFenceDrag(commit: true);
    }

    private void Header_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_draggingFence) EndFenceDrag(commit: true);
    }

    private void EndFenceDrag(bool commit)
    {
        if (!_draggingFence) return;
        _draggingFence = false;
        _host.HideGuides();
        if (Header.IsMouseCaptured) Header.ReleaseMouseCapture();
        if (!commit) return;

        _model.X = Canvas.GetLeft(this);
        _model.Y = Canvas.GetTop(this);
        _host.SaveSoon();
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_model.Locked || _model.Collapsed) return;
        _host.RaiseFence(this);
        _resizeStart = e.GetPosition(_host.FenceCanvas);
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        _resizing = true;
        ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(_host.FenceCanvas);
        var proposed = new Rect(
            Canvas.GetLeft(this),
            Canvas.GetTop(this),
            Math.Max(250, _resizeStartWidth + point.X - _resizeStart.X),
            Math.Max(150, _resizeStartHeight + point.Y - _resizeStart.Y));

        var snap = LayoutService.SnapResize(
            proposed,
            _host.GetOtherFenceRects(this),
            _host.WorkAreaRect,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));

        Width = snap.Bounds.Width;
        Height = snap.Bounds.Height;
        _host.ShowGuides(snap.GuideX, snap.GuideY);
        e.Handled = true;
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndResize(commit: true);
    }

    private void ResizeGrip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_resizing) EndResize(commit: true);
    }

    private void EndResize(bool commit)
    {
        if (!_resizing) return;
        _resizing = false;
        _host.HideGuides();
        if (ResizeGrip.IsMouseCaptured) ResizeGrip.ReleaseMouseCapture();
        if (!commit) return;
        _model.Width = Width;
        _model.Height = Height;
        _host.SaveSoon();
    }

    private void Fence_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _host.RaiseFence(this);
        OpenFenceMenu(true);
        e.Handled = true;
    }

    private void Header_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _host.RaiseFence(this);
        OpenFenceMenu(true);
        e.Handled = true;
    }

    private void Fence_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _host.RaiseFence(this);
    }

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not FenceItemModel item) return;
        _host.RaiseFence(this);
        SelectItem(item);

        if (e.ClickCount == 2)
        {
            _host.LaunchItem(item);
            e.Handled = true;
            return;
        }

        _pressedItem = item;
        _itemPressPoint = e.GetPosition(_host.FenceCanvas);
        _itemDragging = false;
    }

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem is null || _itemDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(_host.FenceCanvas);
        var dx = point.X - _itemPressPoint.X;
        var dy = point.Y - _itemPressPoint.Y;
        if (dx * dx + dy * dy < 42) return;

        _itemDragging = true;
        var item = _pressedItem;
        var data = new DataObject(FenceDragPayload.Format, new FenceDragPayload(_model.Id, item.Path));
        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            LogService.Error("拖拽图标失败", ex);
        }
        finally
        {
            _pressedItem = null;
            _itemDragging = false;
        }
    }

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _pressedItem = null;
        _itemDragging = false;
    }

    private void Item_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not FenceItemModel item) return;
        _host.RaiseFence(this);
        SelectItem(item);
        OpenItemMenu(item);
        e.Handled = true;
    }

    private void SelectItem(FenceItemModel selected)
    {
        foreach (var item in _model.Items)
        {
            item.IsSelected = ReferenceEquals(item, selected);
        }
    }

    private void Fence_DragOver(object sender, DragEventArgs e)
    {
        if (_model.IsPortal)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(FenceDragPayload.Format))
        {
            var payload = e.Data.GetData(FenceDragPayload.Format) as FenceDragPayload;
            e.Effects = payload is null || payload.FenceId == _model.Id
                ? DragDropEffects.None
                : DragDropEffects.Move;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void Fence_Drop(object sender, DragEventArgs e)
    {
        if (_model.IsPortal)
        {
            _host.ShowToast("文件夹门户会实时显示真实文件夹，请直接把文件放进该文件夹。");
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _host.AddExternalFiles(this, files);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(FenceDragPayload.Format) &&
            e.Data.GetData(FenceDragPayload.Format) is FenceDragPayload payload)
        {
            _host.MoveItemToFence(payload, this);
            e.Handled = true;
        }
    }

    private void OpenItemMenu(FenceItemModel item)
    {
        var menu = new ContextMenu();
        AddMenu(menu, "打开", () => _host.LaunchItem(item));
        if (!Directory.Exists(item.Path))
        {
            AddMenu(menu, "打开方式…", () => _host.OpenWithItem(item));
        }
        AddMenu(menu, "打开文件所在位置", () => DesktopService.RevealItemLocation(item.Path));
        menu.Items.Add(new Separator());
        AddMenu(menu, "压缩此文件", () => _host.CompressItem(item));
        if (ArchiveService.IsArchive(item.Path))
        {
            AddMenu(menu, "解压缩到当前文件夹", () => _host.ExtractItem(item, chooseFolder: false));
            AddMenu(menu, "解压缩到…", () => _host.ExtractItem(item, chooseFolder: true));
        }
        menu.Items.Add(new Separator());
        AddMenu(menu, "重命名…", () => _host.RenameItem(item));
        var delete = AddMenu(menu, "删除到回收站", () => _host.DeleteItem(item));
        delete.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        AddMenu(menu, "属性", () => _host.ShowItemProperties(item));
        menu.Items.Add(new Separator());
        AddMenu(menu, "复制完整路径", () =>
        {
            try
            {
                Clipboard.SetText(item.Path);
                _host.ShowToast("路径已复制");
            }
            catch
            {
                // ignore clipboard contention
            }
        });

        if (!_model.IsPortal)
        {
            menu.Items.Add(new Separator());
            AddMenu(menu, "从围栏中移除（不删除文件）", () => _host.RemoveItem(this, item));
        }
        else
        {
            menu.Items.Add(new Separator());
            AddMenu(menu, "打开该文件夹", () => DesktopService.OpenDirectory(_model.FolderPath!));
        }

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void OpenFenceMenu(bool fromMouse)
    {
        var menu = new ContextMenu();

        var title = new MenuItem
        {
            Header = _model.Title,
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());

        AddMenu(menu, "重命名", BeginRename);
        menu.Items.Add(BuildColorMenu());
        AddMenu(menu, _model.Collapsed ? "展开围栏" : "卷起围栏", () =>
        {
            _model.Collapsed = !_model.Collapsed;
            ApplyCollapsed(true);
            _host.SaveSoon();
        });

        var lockItem = new MenuItem
        {
            Header = "锁定位置",
            IsCheckable = true,
            IsChecked = _model.Locked,
        };
        lockItem.Click += (_, _) =>
        {
            _model.Locked = !_model.Locked;
            ApplyLocked();
            _host.SaveSoon();
        };
        menu.Items.Add(lockItem);

        menu.Items.Add(BuildSortMenu());
        menu.Items.Add(BuildPortalMenu());
        menu.Items.Add(new Separator());

        AddMenu(menu, "刷新围栏", () => _host.RefreshFence(this));

        var delete = AddMenu(menu, "删除围栏", () => _host.ConfirmDeleteFence(this));
        delete.Foreground = (Brush)Application.Current.Resources["DangerBrush"];

        menu.PlacementTarget = MoreButton;
        menu.Placement = fromMouse ? PlacementMode.MousePoint : PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem BuildColorMenu()
    {
        var colorMenu = new MenuItem { Header = "更改颜色" };
        foreach (var accent in AccentPalette.All)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(5),
                Background = accent.Brush,
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = accent.Name,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            });

            var item = new MenuItem { Header = panel };
            if (string.Equals(accent.Key, _model.Accent, StringComparison.OrdinalIgnoreCase))
            {
                item.IsCheckable = true;
                item.IsChecked = true;
            }
            item.Click += (_, _) =>
            {
                _model.Accent = accent.Key;
                ApplyAccent();
                _host.SaveSoon();
            };
            colorMenu.Items.Add(item);
        }
        return colorMenu;
    }

    private MenuItem BuildSortMenu()
    {
        var sortMenu = new MenuItem { Header = "排序图标" };
        AddMenu(sortMenu, "按名称", () => _host.SortFence(this, FenceSortMode.Name));
        AddMenu(sortMenu, "按类型", () => _host.SortFence(this, FenceSortMode.Type));
        AddMenu(sortMenu, "按修改日期", () => _host.SortFence(this, FenceSortMode.Modified));
        return sortMenu;
    }

    private MenuItem BuildPortalMenu()
    {
        var portalMenu = new MenuItem { Header = "文件夹门户" };
        if (_model.IsPortal)
        {
            AddMenu(portalMenu, "在资源管理器中打开", () => _host.OpenPortalFolder(this));
            AddMenu(portalMenu, "更换文件夹…", () => _host.ChoosePortalFolder(this));
            AddMenu(portalMenu, "刷新文件夹", () => _host.RefreshPortal(this));
            portalMenu.Items.Add(new Separator());
            AddMenu(portalMenu, "取消文件夹门户", () => _host.UnlinkPortal(this));
        }
        else
        {
            AddMenu(portalMenu, "设为文件夹门户…", () => _host.ChoosePortalFolder(this));
        }
        return portalMenu;
    }

    private static MenuItem AddMenu(ItemsControl parent, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        parent.Items.Add(item);
        return item;
    }

    private void BeginRename()
    {
        if (_renaming) return;
        if (_host.IsDesktopAttached)
        {
            _host.RenameFence(this, _model);
            return;
        }
        _renaming = true;
        _host.BeginInteractiveInput();
        RenameBox.Text = _model.Title;
        TitleText.Visibility = Visibility.Collapsed;
        RenameBox.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }));
    }

    private void CommitRename(bool save)
    {
        if (!_renaming) return;
        _renaming = false;
        RenameBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        if (save)
        {
            var text = RenameBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text) && text != _model.Title)
            {
                _model.Title = text;
                _host.SaveSoon();
            }
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitRename(false);
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_renaming) CommitRename(true);
    }
}
