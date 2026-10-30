using System.IO;

namespace FenceDesk.Services;

/// <summary>
/// 选择并验证可写的数据目录。AppData 在部分受管环境、沙箱或杀软策略下可能被拒绝，
/// 因此依次尝试：环境变量 → Roaming AppData → 程序目录 data → LocalAppData → Temp。
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> LazyDataDirectory = new(ResolveDataDirectory);

    public static string DataDirectory => LazyDataDirectory.Value;
    public static bool IsUsingFallback { get; private set; }
    public static string? ResolutionError { get; private set; }

    private static string ResolveDataDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("FENCEDESK_DATA");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (TryPrepare(custom.Trim(), out var customError))
            {
                IsUsingFallback = false;
                return Path.GetFullPath(custom.Trim());
            }
            ResolutionError = "自定义数据目录不可写：" + customError;
        }

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FenceDesk");
        if (TryPrepare(roaming, out var roamingError))
        {
            IsUsingFallback = false;
            return roaming;
        }

        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (TryPrepare(portable, out var portableError))
        {
            IsUsingFallback = true;
            ResolutionError = "AppData 不可写，已使用程序目录：" + roamingError;
            return portable;
        }

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FenceDesk");
        if (TryPrepare(local, out var localError))
        {
            IsUsingFallback = true;
            ResolutionError = "AppData 不可写，已使用 LocalAppData：" + roamingError;
            return local;
        }

        var temp = Path.Combine(Path.GetTempPath(), "FenceDesk");
        if (TryPrepare(temp, out var tempError))
        {
            IsUsingFallback = true;
            ResolutionError = "AppData/LocalAppData 不可写，已使用临时目录：" + localError;
            return temp;
        }

        ResolutionError =
            $"无法找到可写的数据目录。\nRoaming: {roamingError}\nPortable: {portableError}\nLocal: {localError}\nTemp: {tempError}";
        throw new IOException(ResolutionError);
    }

    private static bool TryPrepare(string directory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".fencedesk-write-test-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x46);
            }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
