using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace FenceDesk.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<FenceModel> Fences { get; set; } = new();
    public List<string> IgnoredPaths { get; set; } = new();

    /// <summary>
    /// 每个被"从围栏移除"的项目当时的文件时间。
    /// 有了它，重装软件、重新创建同名快捷方式后能被识别成"新东西"重新收纳，
    /// 而不是像以前那样永久忽略。
    /// </summary>
    public Dictionary<string, DateTime> IgnoredPathStamps { get; set; } = new();
}

public sealed class AppSettings
{
    public bool HideDesktopIcons { get; set; } = true;
    public bool OwnsHiddenIcons { get; set; }
    public bool DoubleClickHide { get; set; } = true;
    public bool AutoImportNew { get; set; } = true;
    public bool IncludeHiddenDesktopItems { get; set; }
    public bool ShowMusicIsland { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
    public bool ShowDock { get; set; } = true;
    public int IconSize { get; set; } = 42;
    public bool HasInitialized { get; set; }
    public bool WelcomeShown { get; set; }
    public int SettingsMigrationVersion { get; set; }
}

public sealed class FenceModel : ObservableObject
{
    private string _title = "新建围栏";
    private string _accent = "teal";
    private double _x = 80;
    private double _y = 80;
    private double _width = 340;
    private double _height = 390;
    private bool _collapsed;
    private bool _locked;
    private string? _categoryKey;
    private string? _folderPath;
    private ObservableCollection<FenceItemModel> _items = new();

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title
    {
        get => _title;
        set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? "未命名围栏" : value.Trim());
    }

    public string Accent
    {
        get => _accent;
        set => Set(ref _accent, value);
    }

    public double X
    {
        get => _x;
        set => Set(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => Set(ref _y, value);
    }

    public double Width
    {
        get => _width;
        set => Set(ref _width, Math.Max(250, value));
    }

    public double Height
    {
        get => _height;
        set => Set(ref _height, Math.Max(150, value));
    }

    public bool Collapsed
    {
        get => _collapsed;
        set => Set(ref _collapsed, value);
    }

    public bool Locked
    {
        get => _locked;
        set => Set(ref _locked, value);
    }

    /// <summary>隐藏围栏：不在桌面画布上渲染，只能通过隐藏围栏入口打开。</summary>
    public bool IsHidden { get; set; }

    public string? HiddenPasswordHash { get; set; }

    public string? HiddenPasswordSalt { get; set; }

    /// <summary>分类键，用于“整理桌面”时自动找到对应的围栏。手动创建的围栏为 null。</summary>
    public string? CategoryKey
    {
        get => _categoryKey;
        set => Set(ref _categoryKey, value);
    }

    /// <summary>非空时，此围栏是实时显示该文件夹内容的“文件夹门户”。</summary>
    public string? FolderPath
    {
        get => _folderPath;
        set
        {
            if (Set(ref _folderPath, value))
            {
                OnPropertyChanged(nameof(IsPortal));
            }
        }
    }

    [JsonIgnore]
    public bool IsPortal => !string.IsNullOrWhiteSpace(FolderPath);

    [JsonIgnore]
    public ObservableCollection<FenceItemModel> Items
    {
        get => _items;
        set
        {
            if (ReferenceEquals(_items, value)) return;
            _items.CollectionChanged -= Items_CollectionChanged;
            _items = value ?? new ObservableCollection<FenceItemModel>();
            _items.CollectionChanged += Items_CollectionChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Count));
        }
    }

    [JsonPropertyName("items")]
    public List<FenceItemModel> SavedItems { get; set; } = new();

    [JsonIgnore]
    public int Count => Items.Count;

    public FenceModel()
    {
        _items.CollectionChanged += Items_CollectionChanged;
    }

    public void RaiseCountChanged()
    {
        OnPropertyChanged(nameof(Count));
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Count));
    }
}

public sealed class FenceItemModel : ObservableObject
{
    private string _path = string.Empty;
    private string? _customName;
    private string? _arguments;
    private bool _isMissing;
    private bool _isSelected;
    private ImageSource? _icon;
    private bool _iconLoadStarted;
    private bool _isPortalItem;

    public string Path
    {
        get => _path;
        set
        {
            if (Set(ref _path, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(ExtensionLabel));
                OnPropertyChanged(nameof(TooltipText));
                OnPropertyChanged(nameof(IsDirectory));
            }
        }
    }

    public string? CustomName
    {
        get => _customName;
        set
        {
            if (Set(ref _customName, value)) OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string? Arguments
    {
        get => _arguments;
        set => Set(ref _arguments, value);
    }

    /// <summary>加入隐藏围栏之前是否已经是隐藏属性，取消隐藏时据此恢复。</summary>
    public bool WasHidden { get; set; }

    [JsonIgnore]
    public bool IsDirectory
    {
        get
        {
            try { return Directory.Exists(Path); }
            catch { return false; }
        }
    }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName.Trim();
            if (string.IsNullOrWhiteSpace(Path)) return "未知项目";

            var name = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
            var ext = System.IO.Path.GetExtension(Path);
            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                name = System.IO.Path.GetFileNameWithoutExtension(name);
            }

            name = name.Replace(" - 快捷方式", "", StringComparison.OrdinalIgnoreCase)
                       .Replace(" - Shortcut", "", StringComparison.OrdinalIgnoreCase)
                       .Trim();
            return string.IsNullOrWhiteSpace(name) ? "未命名项目" : name;
        }
    }

    [JsonIgnore]
    public string ExtensionLabel
    {
        get
        {
            if (IsDirectory) return "DIR";
            var ext = System.IO.Path.GetExtension(Path);
            if (string.IsNullOrEmpty(ext)) return "FILE";
            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return "APP";
            var label = ext.TrimStart('.').ToUpperInvariant();
            return label.Length > 4 ? label[..4] : label;
        }
    }

    [JsonIgnore]
    public string TooltipText
    {
        get
        {
            var text = DisplayName + "\n" + Path;
            if (!string.IsNullOrWhiteSpace(Arguments)) text += "\n参数：" + Arguments;
            return text;
        }
    }

    [JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    [JsonIgnore]
    public bool IconLoadStarted
    {
        get => _iconLoadStarted;
        set => _iconLoadStarted = value;
    }

    [JsonIgnore]
    public bool IsMissing
    {
        get => _isMissing;
        set => Set(ref _isMissing, value);
    }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    [JsonIgnore]
    public bool IsPortalItem
    {
        get => _isPortalItem;
        set => _isPortalItem = value;
    }

    public FenceItemModel()
    {
    }

    public FenceItemModel(string path, bool portalItem = false)
    {
        Path = path;
        IsPortalItem = portalItem;
    }
}

public enum FenceSortMode
{
    Name,
    Type,
    Modified,
}

public sealed record FenceDragPayload(string FenceId, string Path)
{
    public const string Format = "FenceDesk.FenceItem.v1";
}

public sealed class AccentInfo
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required Color Color { get; init; }
    public required SolidColorBrush Brush { get; init; }
    public required SolidColorBrush SoftBrush { get; init; }
}

public static class AccentPalette
{
    public static readonly IReadOnlyList<AccentInfo> All = new List<AccentInfo>
    {
        Make("teal", "青碧", 0x3A, 0xDB, 0xC4),
        Make("blue", "星蓝", 0x74, 0xA9, 0xFF),
        Make("violet", "雾紫", 0xB7, 0x9C, 0xFF),
        Make("amber", "琥珀", 0xFF, 0xB8, 0x6B),
        Make("green", "薄荷", 0x7F, 0xE0, 0xA8),
        Make("rose", "玫瑰", 0xFF, 0x91, 0xA4),
        Make("slate", "石墨", 0x9E, 0xAB, 0xBE),
    };

    public static AccentInfo Get(string? key)
        => All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static string Next(string? key)
    {
        var info = Get(key);
        for (var i = 0; i < All.Count; i++)
        {
            if (ReferenceEquals(All[i], info) || All[i].Key == info.Key)
            {
                return All[(i + 1) % All.Count].Key;
            }
        }
        return All[0].Key;
    }

    private static AccentInfo Make(string key, string name, byte r, byte g, byte b)
    {
        var color = Color.FromRgb(r, g, b);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var soft = new SolidColorBrush(Color.FromArgb(0x2A, r, g, b));
        soft.Freeze();
        return new AccentInfo { Key = key, Name = name, Color = color, Brush = brush, SoftBrush = soft };
    }
}
