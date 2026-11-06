using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using FenceDesk.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using RarArchiveX = SharpCompress.Archives.Rar.RarArchive;
using SevenZipArchiveX = SharpCompress.Archives.SevenZip.SevenZipArchive;
using TarArchiveX = SharpCompress.Archives.Tar.TarArchive;
using ZipArchiveX = SharpCompress.Archives.Zip.ZipArchive;

namespace FenceDesk.Services;

/// <summary>
/// 压缩 / 解压。解压由 SharpCompress 在进程内完成（ZIP、7z、RAR、TAR、GZ、BZ2、XZ），
/// 不再要求用户安装 7-Zip 或 WinRAR；只有内置解码器失败时才回退到外部解压程序。
/// </summary>
public static class ArchiveService
{
    private const int BufferSize = 81920;

    private static readonly string[] SupportedExtensions =
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".tbz", ".xz", ".txz",
        ".tar.gz", ".tar.bz2", ".tar.xz",
    };

    public static bool IsArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return SupportedExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanCompress(string path) => File.Exists(path) || Directory.Exists(path);

    public static Task CompressAsync(
        string source,
        string destination,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Compress(source, destination, progress, cancellationToken), cancellationToken);

    public static Task ExtractAsync(string archivePath, string destination, ArchiveExtractOptions? options = null)
    {
        options ??= new ArchiveExtractOptions();
        return Task.Run(() => Extract(archivePath, destination, options), options.Cancellation);
    }

    // ---------------------------------------------------------------- 压缩

    private static void Compress(string source, string destination, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException("要压缩的文件或文件夹不存在。", source);
        }

        var destinationPath = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

        if (Directory.Exists(source))
        {
            var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
            if (destinationPath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("压缩包不能保存到被压缩的文件夹内部。");
            }
        }

        if (File.Exists(destinationPath)) File.Delete(destinationPath);

        if (File.Exists(source))
        {
            CompressSingleFile(source, destinationPath, progress, ct);
            return;
        }

        CompressDirectory(source, destinationPath, progress, ct);
    }

    private static void CompressSingleFile(string source, string destination, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var info = new FileInfo(source);
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        var entryName = info.Name;
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = ClampTimestamp(info.LastWriteTime);

        progress?.Report(new ArchiveProgress(0, entryName, 0, 1, 0, info.Length));
        long written;
        using (var input = info.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var output = entry.Open())
        {
            written = CopyWithProgress(input, output, info.Length, 0, 0, 1, entryName, progress, ct);
        }

        progress?.Report(new ArchiveProgress(1, entryName, 1, 1, written, info.Length));
    }

    private static void CompressDirectory(string source, string destination, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseDir = Path.GetDirectoryName(root);
        if (string.IsNullOrEmpty(baseDir)) baseDir = root;

        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            files.Add(new FileInfo(path));
        }

        files.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));
        var totalBytes = files.Sum(f => f.Length);
        var totalFiles = files.Count;

        progress?.Report(new ArchiveProgress(0, "", 0, totalFiles, 0, totalBytes));

        long written = 0;
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var entryName = ToEntryName(baseDir, file.FullName);
            if (string.IsNullOrEmpty(entryName)) continue;

            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = ClampTimestamp(file.LastWriteTime);

            using var input = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = entry.Open();
            written = CopyWithProgress(input, output, totalBytes, written, i, totalFiles, entryName, progress, ct);
        }

        progress?.Report(new ArchiveProgress(1, "", totalFiles, totalFiles, written, totalBytes));
    }

    // ---------------------------------------------------------------- 解压入口

    private static void Extract(string archivePath, string destination, ArchiveExtractOptions options)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("压缩文件不存在。", archivePath);

        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var ct = options.Cancellation;
        ct.ThrowIfCancellationRequested();

        options.Progress?.Report(Indeterminate("正在读取压缩包信息…"));

        try
        {
            // 压缩过的 tar（.tar.gz/.tar.bz2/.tar.xz）和单文件 .gz/.bz2/.xz 只能顺序读取，
            // 用 Reader 更可靠；ZIP/RAR/7z/TAR 可以随机访问，用 Archive 才能先算出总字节数。
            if (IsStreamingFormat(archivePath)) ExtractStreaming(archivePath, root, options);
            else ExtractArchived(archivePath, root, options);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsPasswordProblem(ex))
        {
            throw new InvalidOperationException("这个压缩包已加密，当前版本还不支持输入密码解压。", ex);
        }
        catch (Exception ex) when (IsUnsupportedFormat(ex))
        {
            if (!TryExternalExtract(archivePath, root, options.Progress, ct, out var externalError))
            {
                throw new InvalidOperationException("无法解压这个压缩包：" + externalError, ex);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("解压失败：" + ex.Message, ex);
        }
    }

    private static bool IsStreamingFormat(string path)
    {
        if (path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return false;
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".tbz", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".xz", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".txz", StringComparison.OrdinalIgnoreCase);
    }

    private static IArchive OpenArchive(string path, ReaderOptions options)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ZipArchiveX.OpenArchive(path, options);
        if (path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) return RarArchiveX.OpenArchive(path, options);
        if (path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) return SevenZipArchiveX.OpenArchive(path, options);
        if (path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return TarArchiveX.OpenArchive(path, options);
        return ArchiveFactory.OpenArchive(path, options);
    }

    // ---------------------------------------------------------------- 随机访问格式

    private static void ExtractArchived(string archivePath, string root, ArchiveExtractOptions options)
    {
        var ct = options.Cancellation;
        var progress = options.Progress;

        using var archive = OpenArchive(archivePath, BuildReaderOptions(options.Password));
        var entries = archive.Entries.ToList();
        var fileEntries = entries.Where(e => !e.IsDirectory).ToList();

        long totalBytes = 0;
        foreach (var entry in fileEntries)
        {
            if (entry.Size > 0) totalBytes += entry.Size;
        }

        var totalFiles = fileEntries.Count;
        var solid = archive.IsSolid;
        long written = 0;
        var completed = 0;
        ArchiveConflictDecision? applyAll = null;

        progress?.Report(new ArchiveProgress(totalBytes <= 0 ? -1 : 0, "", 0, totalFiles, 0, totalBytes));

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var key = NormalizeKey(entry.Key);
            if (string.IsNullOrEmpty(key)) continue;

            var targetPath = SafeCombine(root, key);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

            var decision = ArchiveConflictDecision.Overwrite;
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                decision = ResolveConflict(targetPath, key, entry.Size, options, ref applyAll);
                if (decision == ArchiveConflictDecision.Cancel)
                {
                    throw new OperationCanceledException("用户取消了解压。");
                }

                if (decision == ArchiveConflictDecision.Skip)
                {
                    // 固实压缩包必须顺序解压，跳过时仍要读完这一项的数据，
                    // 否则后面的条目会解出错误内容。
                    if (solid) Drain(entry);
                    completed++;
                    progress?.Report(new ArchiveProgress(
                        totalBytes <= 0 ? -1 : Clamp(written, totalBytes), key, completed, totalFiles, written, totalBytes));
                    continue;
                }

                if (decision == ArchiveConflictDecision.KeepBoth || Directory.Exists(targetPath))
                {
                    targetPath = UniqueFilePath(targetPath);
                }
                else if (File.Exists(targetPath))
                {
                    try { File.SetAttributes(targetPath, FileAttributes.Normal); } catch { /* ignore */ }
                }
            }

            using (var input = entry.OpenEntryStream())
            using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.SequentialScan))
            {
                written = CopyWithProgress(input, output, totalBytes, written, completed, totalFiles, key, progress, ct);
            }

            if (entry.LastModifiedTime is { } modified)
            {
                try { File.SetLastWriteTime(targetPath, modified); } catch { /* 时间戳不是关键 */ }
            }

            completed++;
            progress?.Report(new ArchiveProgress(
                totalBytes <= 0 ? -1 : Clamp(written, totalBytes), key, completed, totalFiles, written, totalBytes));
        }

        progress?.Report(new ArchiveProgress(1, "", totalFiles, totalFiles, written, totalBytes));
    }

    // ---------------------------------------------------------------- 顺序读取格式

    private static void ExtractStreaming(string archivePath, string root, ArchiveExtractOptions options)
    {
        var ct = options.Cancellation;
        var progress = options.Progress;

        // .gz 尾部 4 字节是解开后的大小，可以当作进度条的基数。
        var totalBytes = TryGetGzipSize(archivePath);
        long written = 0;
        var completed = 0;
        ArchiveConflictDecision? applyAll = null;

        progress?.Report(new ArchiveProgress(totalBytes <= 0 ? -1 : 0, "", 0, 0, 0, totalBytes));

        using var reader = ReaderFactory.OpenReader(archivePath, BuildReaderOptions(options.Password));
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();

            var entry = reader.Entry;
            var key = NormalizeKey(entry.Key);
            if (string.IsNullOrEmpty(key)) continue;

            var targetPath = SafeCombine(root, key);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

            var decision = ArchiveConflictDecision.Overwrite;
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                decision = ResolveConflict(targetPath, key, entry.Size, options, ref applyAll);
                if (decision == ArchiveConflictDecision.Cancel)
                {
                    throw new OperationCanceledException("用户取消了解压。");
                }

                if (decision == ArchiveConflictDecision.Skip)
                {
                    completed++;
                    progress?.Report(new ArchiveProgress(
                        totalBytes <= 0 ? -1 : Clamp(written, totalBytes), key, completed, 0, written, totalBytes));
                    continue;
                }

                if (decision == ArchiveConflictDecision.KeepBoth || Directory.Exists(targetPath))
                {
                    targetPath = UniqueFilePath(targetPath);
                }
                else if (File.Exists(targetPath))
                {
                    try { File.SetAttributes(targetPath, FileAttributes.Normal); } catch { /* ignore */ }
                }
            }

            using (var input = reader.OpenEntryStream())
            using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.SequentialScan))
            {
                written = CopyWithProgress(input, output, totalBytes, written, completed, 0, key, progress, ct);
            }

            if (entry.LastModifiedTime is { } modified)
            {
                try { File.SetLastWriteTime(targetPath, modified); } catch { /* 时间戳不是关键 */ }
            }

            completed++;
            progress?.Report(new ArchiveProgress(
                totalBytes <= 0 ? -1 : Clamp(written, totalBytes), key, completed, 0, written, totalBytes));
        }

        progress?.Report(new ArchiveProgress(1, "", completed, 0, written, totalBytes));
    }

    private static long TryGetGzipSize(string path)
    {
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return 0;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 18) return 0;
            Span<byte> buffer = stackalloc byte[4];
            stream.Seek(-4, SeekOrigin.End);
            var read = stream.Read(buffer);
            if (read != 4) return 0;
            var size = BitConverter.ToUInt32(buffer);
            return size == 0 ? 0 : size;
        }
        catch
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------- 公共逻辑

    private static ArchiveConflictDecision ResolveConflict(
        string targetPath,
        string entryPath,
        long entrySize,
        ArchiveExtractOptions options,
        ref ArchiveConflictDecision? applyAll)
    {
        if (applyAll is { } reused) return reused;
        if (options.ResolveConflict is null) return ArchiveConflictDecision.Overwrite;

        long existingSize = 0;
        try
        {
            if (File.Exists(targetPath)) existingSize = new FileInfo(targetPath).Length;
        }
        catch
        {
            // ignore
        }

        // 解压线程回调 UI：由宿主任意决定如何询问。
        var conflict = new ArchiveConflict(entryPath, targetPath, entrySize, existingSize, Directory.Exists(targetPath));
        var answer = options.ResolveConflict(conflict);
        if (answer.ApplyToAll) applyAll = answer.Decision;
        return answer.Decision;
    }

    private static void Drain(IArchiveEntry entry)
    {
        using var stream = entry.OpenEntryStream();
        var buffer = new byte[BufferSize];
        while (stream.Read(buffer, 0, buffer.Length) > 0)
        {
            // 只为推进固实数据流，不保存内容。
        }
    }

    private static long CopyWithProgress(
        Stream input,
        Stream output,
        long totalBytes,
        long written,
        int completedEntries,
        int totalFiles,
        string entryName,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        var lastReport = Environment.TickCount64;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            output.Write(buffer, 0, read);
            written += read;

            if (progress is null) continue;
            var now = Environment.TickCount64;
            if (now - lastReport < 70) continue;
            lastReport = now;
            progress.Report(new ArchiveProgress(
                totalBytes <= 0 ? -1 : Clamp(written, totalBytes),
                entryName,
                completedEntries,
                totalFiles,
                written,
                totalBytes));
        }

        return written;
    }

    private static ReaderOptions BuildReaderOptions(string? password)
    {
        var options = new ReaderOptions
        {
            ArchiveEncoding = new ArchiveEncoding
            {
                // 没有 UTF-8 标记的 ZIP 文件名按系统 ANSI 代码页解码，
                // 这样 Windows 资源管理器压出来的中文名不会变成乱码。
                Default = AnsiEncoding(),
                Password = Encoding.UTF8,
            },
            Password = password,
            LeaveStreamOpen = false,
        };

        return options;
    }

    private static Encoding AnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (codePage > 0) return Encoding.GetEncoding(codePage);
        }
        catch
        {
            // 没有代码页提供程序时退回 UTF-8。
        }

        return Encoding.UTF8;
    }

    // ---------------------------------------------------------------- 外部工具回退

    private static bool TryExternalExtract(string archivePath, string root, IProgress<ArchiveProgress>? progress,
        CancellationToken ct, out string error)
    {
        error = string.Empty;
        var sevenZip = FindSevenZip();
        if (sevenZip is not null)
        {
            return RunExtractor(sevenZip,
                new[] { "x", "-y", "-bd", "-o" + root, archivePath }, root, progress, ct, out error);
        }

        var unrar = FindUnRar();
        if (unrar is not null && archivePath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            return RunExtractor(unrar,
                new[] { "x", "-y", archivePath, root + Path.DirectorySeparatorChar }, root, progress, ct, out error);
        }

        error = "内置解码器无法识别这个压缩包，也没有找到 7-Zip 或 WinRAR。";
        return false;
    }

    private static bool RunExtractor(string executable, IEnumerable<string> arguments, string destination,
        IProgress<ArchiveProgress>? progress, CancellationToken ct, out string error)
    {
        error = string.Empty;
        progress?.Report(Indeterminate("正在调用外部解压程序…"));

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

        using var process = Process.Start(info);
        if (process is null)
        {
            error = "无法启动解压程序。";
            return false;
        }

        using var registration = ct.Register(() =>
        {
            try { process.Kill(true); } catch { /* ignore */ }
        });

        var output = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (process.ExitCode != 0)
        {
            error = LastLines(stderr + output);
            return false;
        }

        progress?.Report(new ArchiveProgress(1, "", 0, 0, 0, 0));
        return true;
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
                 })
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    // ---------------------------------------------------------------- 路径工具

    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        var normalized = key.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.Trim();
    }

    /// <summary>防止压缩包内的 ../ 或绝对路径把文件写到目标目录之外。</summary>
    private static string SafeCombine(string root, string relativeKey)
    {
        var trimmed = relativeKey.Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(root, trimmed));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("压缩包包含非法路径：" + relativeKey);
        }

        return combined;
    }

    private static string UniqueFilePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(dir, name + " (" + Guid.NewGuid().ToString("N")[..6] + ")" + ext);
    }

    private static string ToEntryName(string baseDir, string fullPath)
    {
        var relative = Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
        return relative.TrimStart('/');
    }

    private static DateTimeOffset ClampTimestamp(DateTime time)
    {
        // ZIP 时间戳最小是 1980-01-01。
        if (time.Year < 1980) return new DateTimeOffset(new DateTime(1980, 1, 1), TimeSpan.Zero);
        if (time.Year > 2107) return new DateTimeOffset(new DateTime(2107, 12, 31), TimeSpan.Zero);
        return new DateTimeOffset(time);
    }

    private static ArchiveProgress Indeterminate(string message)
        => new(-1, message, 0, 0, 0, 0);

    private static double Clamp(long written, long total)
        => total <= 0 ? 0 : Math.Clamp((double)written / total, 0, 1);

    private static bool IsPasswordProblem(Exception ex)
    {
        var name = ex.GetType().Name;
        if (name.Contains("Cryptographic", StringComparison.OrdinalIgnoreCase)) return true;
        var message = ex.Message ?? string.Empty;
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
               || message.Contains("密码", StringComparison.OrdinalIgnoreCase)
               || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsupportedFormat(Exception ex)
    {
        var name = ex.GetType().Name;
        return name is "InvalidFormatException" or "ArchiveException" or "ArchiveOperationException"
            || ex.InnerException is not null && IsUnsupportedFormat(ex.InnerException);
    }

    private static string LastLines(string text)
    {
        var lines = text.Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", lines.TakeLast(4));
    }
}
