using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace FenceDesk.Services;

public static class ArchiveService
{
    private static readonly string[] SupportedExtensions =
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz",
    };

    public static bool IsArchive(string path)
        => SupportedExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public static bool CanCompress(string path) => File.Exists(path) || Directory.Exists(path);

    public static Task CompressAsync(string source, string destination)
        => Task.Run(() => Compress(source, destination));

    public static Task ExtractAsync(string archive, string destination)
        => Task.Run(() => Extract(archive, destination));

    private static void Compress(string source, string destination)
    {
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException("要压缩的文件或文件夹不存在。", source);
        }

        if (File.Exists(destination)) File.Delete(destination);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

        if (Directory.Exists(source))
        {
            var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var destinationPath = Path.GetFullPath(destination);
            if (destinationPath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("压缩包不能保存到被压缩的文件夹内部。");
            }

            ZipFile.CreateFromDirectory(source, destination, CompressionLevel.Optimal,
                includeBaseDirectory: true);
        }
        else
        {
            using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.Optimal);
        }
    }

    private static void Extract(string archive, string destination)
    {
        if (!File.Exists(archive)) throw new FileNotFoundException("压缩文件不存在。", archive);
        Directory.CreateDirectory(destination);

        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: false);
            return;
        }

        var sevenZip = FindSevenZip();
        if (sevenZip is not null)
        {
            RunExtractor(sevenZip, new[] { "x", "-y", "-o" + destination, archive }, destination);
            return;
        }

        var unrar = FindUnRar();
        if (unrar is not null && archive.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            RunExtractor(unrar, new[] { "x", "-y", archive, destination + Path.DirectorySeparatorChar }, destination);
            return;
        }

        throw new InvalidOperationException("这个压缩格式需要安装 7-Zip 或 WinRAR 才能解压。");
    }

    private static void RunExtractor(string executable, IEnumerable<string> arguments, string destination)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(destination) ?? destination,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动解压程序。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("解压失败：" + LastLines(error + output));
        }
    }

    private static string? FindSevenZip()
    {
        foreach (var path in new[]
                 {
                     @"C:\Program Files\7-Zip\7z.exe",
                     @"C:\Program Files (x86)\7-Zip\7z.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         @"Programs\7-Zip\7z.exe"),
                 })
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? FindUnRar()
    {
        foreach (var path in new[]
                 {
                     @"C:\Program Files\WinRAR\UnRAR.exe",
                     @"C:\Program Files (x86)\WinRAR\UnRAR.exe",
                     @"C:\Program Files\WinRAR\WinRAR.exe",
                 })
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string LastLines(string text)
    {
        var lines = text.Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", lines.TakeLast(4));
    }
}
