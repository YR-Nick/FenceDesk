using System.Windows.Media;
using FenceDesk.Models;
using FenceDesk.Services;

namespace FenceDesk.Models;

public sealed class SearchResult : ObservableObject
{
    private ImageSource? _icon;

    public SearchResult(FenceItemModel item, string fenceTitle, string accentKey)
    {
        Item = item;
        FenceTitle = fenceTitle;
        Accent = AccentPalette.Get(accentKey);
        _icon = item.Icon;
    }

    public FenceItemModel Item { get; }
    public string FenceTitle { get; }
    public AccentInfo Accent { get; }
    public string Name => Item.DisplayName;
    public string Path => Item.Path;
    public string ExtensionLabel => Item.ExtensionLabel;

    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    public async Task EnsureIconAsync()
    {
        if (Icon is not null) return;
        Icon = await IconService.GetAsync(Item.Path);
    }
}
