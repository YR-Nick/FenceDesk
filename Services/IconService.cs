using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FenceDesk.Services;

public static class IconService
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> ShellCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.Run(action);
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    public static async Task<ImageSource?> GetAsync(string path, string? targetPath = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var key = path.Trim() + "|" + (targetPath ?? string.Empty).Trim();
        var result = await Cache.GetOrAdd(key, p => new Lazy<Task<ImageSource?>>(
            () => RunOnStaAsync(() => LoadCore(path, targetPath)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (result is null)
        {
            // 某些 UWP/应用商店快捷方式第一次取图标会失败，不要把失败结果永久缓存。
            Cache.TryRemove(key, out _);
        }
        return result;
    }

    public static Task<ImageSource?> GetShellIconAsync(string parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return Task.FromResult<ImageSource?>(null);
        var key = parsingName.Trim();
        return ShellCache.GetOrAdd(key, p => new Lazy<Task<ImageSource?>>(
            () => RunOnStaAsync(() => LoadShellCore(p)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public static void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var key = path.Trim();
        Cache.TryRemove(key, out _);
        foreach (var item in Cache.Keys.Where(k =>
                     k.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Cache.TryRemove(item, out _);
        }
    }

    private static ImageSource? LoadShellCore(string parsingName)
    {
        var pidl = IntPtr.Zero;
        var hIcon = IntPtr.Zero;
        try
        {
            if (NativeMethods.SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0 ||
                pidl == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.SHFILEINFO();
            var size = (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>();
            var result = NativeMethods.SHGetFileInfo(pidl, 0, ref info, size,
                NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            if (result == IntPtr.Zero) return null;
            hIcon = info.hIcon;
            if (hIcon == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            LogService.Error("加载系统图标失败: " + parsingName, ex);
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(hIcon);
            if (pidl != IntPtr.Zero) NativeMethods.ILFree(pidl);
        }
    }

    private static ImageSource? LoadCore(string path, string? targetPath = null)
    {
        var hIcon = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(targetPath) &&
                !string.Equals(targetPath, path, StringComparison.OrdinalIgnoreCase) &&
                !targetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                (File.Exists(targetPath) || Directory.Exists(targetPath)))
            {
                var targetIcon = LoadCore(targetPath);
                if (targetIcon is not null) return targetIcon;
            }

            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = DesktopService.ResolveShortcutTarget(path);
                if (!string.IsNullOrWhiteSpace(target) &&
                    !string.Equals(target, path, StringComparison.OrdinalIgnoreCase) &&
                    !target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(target) || Directory.Exists(target)))
                {
                    var targetIcon = LoadCore(target);
                    if (targetIcon is not null) return targetIcon;
                }
            }

            var info = new NativeMethods.SHFILEINFO();
            var size = (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>();
            var result = NativeMethods.SHGetFileInfo(path, 0, ref info, size,
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                hIcon = info.hIcon;
            }

            if (hIcon == IntPtr.Zero)
            {
                info = new NativeMethods.SHFILEINFO();
                var fallback = NativeMethods.SHGetFileInfo(path, 0, ref info, size,
                    NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_LARGEICON);
                if (fallback != IntPtr.Zero)
                {
                    var iid = typeof(NativeMethods.IImageList).GUID;
                    if (NativeMethods.SHGetImageList(NativeMethods.SHIL_EXTRALARGE, ref iid, out var list) == 0)
                    {
                        try
                        {
                            if (list.GetIcon(info.iIcon, NativeMethods.ILD_TRANSPARENT, out hIcon) != 0)
                            {
                                hIcon = IntPtr.Zero;
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(list);
                        }
                    }
                }
            }

            if (hIcon == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            LogService.Error("加载图标失败: " + path, ex);
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(hIcon);
        }
    }
}
