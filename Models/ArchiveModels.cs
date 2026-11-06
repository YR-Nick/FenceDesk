namespace FenceDesk.Models;

/// <summary>解压 / 压缩进度快照。Fraction 小于 0 表示无法预估总量（不确定进度）。</summary>
public readonly record struct ArchiveProgress(
    double Fraction,
    string CurrentEntry,
    int CompletedEntries,
    int TotalEntries,
    long WrittenBytes,
    long TotalBytes)
{
    public bool IsIndeterminate => Fraction < 0;

    /// <summary>0-100 的整数百分比，不确定进度时返回 -1。</summary>
    public int Percent => Fraction < 0 ? -1 : (int)Math.Clamp(Math.Round(Fraction * 100), 0, 100);
}

public enum ArchiveConflictDecision
{
    Overwrite,
    Skip,
    KeepBoth,
    Cancel,
}

/// <summary>解压时发现目标文件已存在的问题。</summary>
public sealed record ArchiveConflict(
    string EntryPath,
    string TargetPath,
    long EntrySize,
    long ExistingSize,
    bool TargetIsDirectory)
{
    public string DisplayName => TargetPath;
}

public sealed record ArchiveConflictAnswer(ArchiveConflictDecision Decision, bool ApplyToAll);

public sealed class ArchiveExtractOptions
{
    public IProgress<ArchiveProgress>? Progress { get; init; }

    /// <summary>遇到同名文件时询问用户；为 null 时直接覆盖。</summary>
    public Func<ArchiveConflict, ArchiveConflictAnswer>? ResolveConflict { get; init; }

    public CancellationToken Cancellation { get; init; }

    public string? Password { get; init; }
}
