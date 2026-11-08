using System.IO;
using System.Windows.Threading;

namespace FenceDesk.Services;

/// <summary>
/// 监视 Windows 图标缓存的变化。
/// FenceDesk 接管桌面后会自己画图标，如果用户（或优化工具）重建了 iconcache、
/// 更新了图标索引，进程里缓存的旧位图就会变成空白方块，只有重启才恢复。
/// 这里通过「文件系统监视 + 定期比对签名」两种方式发现变化，然后让界面重新取图标。
/// </summary>
public sealed class IconCacheWatcher : IDisposable
{
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _poll;
    private FileSystemWatcher? _watcher;
    private string _signature = string.Empty;
    private bool _disposed;

    public IconCacheWatcher()
    {
        var silent = TimeSpan.FromMilliseconds(1500);
        _debounce = new DispatcherTimer { Interval = silent };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };

        // 兜底轮询：文件系统事件在整体替换缓存时可能漏掉。
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _poll.Tick += (_, _) =>
        {
            var signature = ComputeSignature();
            if (signature == _signature) return;
            _signature = signature;
            if (signature.Length > 0) Trigger();
        };
    }

    /// <summary>图标缓存疑似发生变化（已去抖）。</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_disposed) return;

        _signature = ComputeSignature();
        StartWatcher();
        _poll.Start();
    }

    /// <summary>内部触发一次（例如检测到资源管理器重启）。</summary>
    public void Trigger()
    {
        if (_disposed) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void StartWatcher()
    {
        try
        {
            _watcher?.Dispose();
            var directory = CacheDirectory();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                               NotifyFilters.CreationTime | NotifyFilters.Size,
            };

            _watcher.Created += OnFileEvent;
            _watcher.Changed += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += (_, _) =>
            {
                // 目录被删除/重建（优化工具常见）后重建监视器
                try { StartWatcher(); } catch { /* ignore */ }
            };

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            LogService.Error("启动图标缓存监视失败", ex);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? string.Empty;
        if (!name.StartsWith("iconcache", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("thumbcache", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Trigger();
    }

    private static string CacheDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Microsoft", "Windows", "Explorer");
    }

    /// <summary>用缓存目录里最新的写入时间和总大小做个轻量签名。</summary>
    private static string ComputeSignature()
    {
        try
        {
            var directory = CacheDirectory();
            if (!Directory.Exists(directory)) return string.Empty;

            long newest = 0;
            long total = 0;
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "iconcache*"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc.Ticks > newest) newest = info.LastWriteTimeUtc.Ticks;
                total += info.Length;
                count++;
            }

            return count + ":" + newest + ":" + total;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _debounce.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }
}
