using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FenceDesk.Models;

namespace FenceDesk.Services;

public static class LogService
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string DataDirectory => AppPaths.DataDirectory;

    private static string LogPath => _path ??= Path.Combine(DataDirectory, "fencedesk.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            if (ex is not null) line += Environment.NewLine + ex;
            lock (Gate)
            {
                var file = new FileInfo(LogPath);
                if (file.Exists && file.Length > 1_500_000)
                {
                    File.Move(LogPath, Path.Combine(DataDirectory, "fencedesk.old.log"), true);
                }
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take down the app.
        }
    }
}

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string DataDirectory { get; }
    public string StatePath => Path.Combine(DataDirectory, "workspace.json");
    public string BackupPath => Path.Combine(DataDirectory, "workspace.backup.json");
    public bool StateExists => File.Exists(StatePath);

    public WorkspaceStore()
    {
        DataDirectory = AppPaths.DataDirectory;
        Directory.CreateDirectory(DataDirectory);
        LogService.Info("数据目录：" + DataDirectory +
                        (AppPaths.IsUsingFallback ? "（AppData 不可写，已启用回退目录）" : string.Empty));
    }

    public AppState Load()
    {
        AppState? state = null;
        if (File.Exists(StatePath))
        {
            try
            {
                state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath, Encoding.UTF8), JsonOptions);
            }
            catch (Exception ex)
            {
                LogService.Error("读取 workspace.json 失败，尝试备份", ex);
                try
                {
                    if (File.Exists(BackupPath))
                    {
                        state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(BackupPath, Encoding.UTF8), JsonOptions);
                    }
                }
                catch (Exception backupEx)
                {
                    LogService.Error("读取备份失败", backupEx);
                }
            }
        }

        state ??= new AppState();
        Normalize(state);
        return state;
    }

    public void Save(AppState state)
    {
        try
        {
            foreach (var fence in state.Fences)
            {
                fence.SavedItems = fence.IsPortal
                    ? new List<FenceItemModel>()
                    : fence.Items.Where(i => !string.IsNullOrWhiteSpace(i.Path)).ToList();
            }

            var json = JsonSerializer.Serialize(state, JsonOptions);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));

            if (File.Exists(StatePath))
            {
                File.Replace(tmp, StatePath, BackupPath, true);
            }
            else
            {
                File.Move(tmp, StatePath);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("保存 workspace.json 失败", ex);
        }
    }

    public void Export(AppState state, string destination)
    {
        foreach (var fence in state.Fences)
        {
            fence.SavedItems = fence.IsPortal
                ? new List<FenceItemModel>()
                : fence.Items.Where(i => !string.IsNullOrWhiteSpace(i.Path)).ToList();
        }

        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(destination, json, new UTF8Encoding(false));
    }

    public AppState Import(string source)
    {
        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(source, Encoding.UTF8), JsonOptions)
                    ?? throw new InvalidDataException("配置文件内容为空。");
        Normalize(state);
        return state;
    }

    public static void Normalize(AppState state)
    {
        state.Settings ??= new AppSettings();
        state.Fences ??= new List<FenceModel>();
        state.IgnoredPaths ??= new List<string>();

        state.Settings.IconSize = Math.Clamp(state.Settings.IconSize, 32, 56);
        state.Fences = state.Fences.Where(f => f is not null).ToList();

        foreach (var fence in state.Fences)
        {
            if (string.IsNullOrWhiteSpace(fence.Id)) fence.Id = Guid.NewGuid().ToString("N");
            fence.Width = Math.Clamp(double.IsFinite(fence.Width) ? fence.Width : 340, 250, 900);
            fence.Height = Math.Clamp(double.IsFinite(fence.Height) ? fence.Height : 390, 150, 1200);
            fence.X = double.IsFinite(fence.X) ? fence.X : 80;
            fence.Y = double.IsFinite(fence.Y) ? fence.Y : 80;
            fence.Title = string.IsNullOrWhiteSpace(fence.Title) ? "未命名围栏" : fence.Title;
            if (string.IsNullOrWhiteSpace(fence.Accent)) fence.Accent = "teal";

            var saved = fence.SavedItems ?? new List<FenceItemModel>();
            var unique = new List<FenceItemModel>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in saved)
            {
                if (string.IsNullOrWhiteSpace(item.Path)) continue;
                if (!seen.Add(item.Path)) continue;
                item.IsPortalItem = fence.IsPortal;
                unique.Add(item);
            }

            fence.Items = new System.Collections.ObjectModel.ObservableCollection<FenceItemModel>(unique);
            if (fence.IsPortal)
            {
                fence.FolderPath = Environment.ExpandEnvironmentVariables(fence.FolderPath!);
                fence.Items.Clear();
            }
        }
    }
}
