using System.Text.RegularExpressions;
using System.IO;

namespace FenceDesk.Services;

/// <summary>一个桌面快捷方式解析后的信息。</summary>
public sealed record AppShortcut(string Path, string Target, string Arguments, DateTime LastWrite)
{
    /// <summary>同一个程序 + 同一套参数，才算"重复的应用"。</summary>
    public string Key => MakeKey(Target, Arguments);

    public static string MakeKey(string target, string? arguments)
        => target.ToLowerInvariant() + "|" + (arguments ?? string.Empty).Trim().ToLowerInvariant();

    public string DisplayName
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path);
            var ext = System.IO.Path.GetExtension(Path);
            if (ext.Length > 0) name = name[..^ext.Length];
            return name.Replace(" - 快捷方式", string.Empty).Replace(" - Shortcut", string.Empty).Trim();
        }
    }
}

/// <summary>一组重复的快捷方式：保留一个，其余是多余的。</summary>
public sealed record DuplicateAppGroup(AppShortcut Keeper, IReadOnlyList<AppShortcut> Extras);

/// <summary>
/// 找出"指向同一个程序、参数也相同"的多个快捷方式。
/// 桌面上的重复通常来自：升级安装时旧版快捷方式没删（FreeCAD 1.0 / 分区助手 10.7），
/// 或者同一程序手动建了两次（HBuilder X / HBuilderX）。
/// </summary>
public static class DuplicateAppFinder
{
    /// <summary>解析一组快捷方式（非 .lnk 的会被忽略）。</summary>
    public static List<AppShortcut> Describe(IEnumerable<string> paths)
    {
        var list = new List<AppShortcut>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
            if (!DesktopService.TryResolveShortcut(path, out var target, out var arguments)) continue;
            if (string.IsNullOrWhiteSpace(target)) continue;

            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(path); }
            catch { lastWrite = DateTime.MinValue; }

            list.Add(new AppShortcut(path, target, arguments ?? string.Empty, lastWrite));
        }

        return list;
    }

    /// <summary>
    /// 分组并挑出要保留的那个。
    /// 优先级：名字里带更高版本号 → 用户桌面（而不是公共桌面）→ 修改时间更新 → 名称。
    /// 桌面上的重复大多来自"升级后旧快捷方式没删"，所以版本号是最可靠的信号。
    /// </summary>
    public static List<DuplicateAppGroup> Find(IEnumerable<AppShortcut> shortcuts)
    {
        var groups = new List<DuplicateAppGroup>();

        foreach (var group in shortcuts.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            if (items.Count < 2) continue;

            var keeper = items
                .OrderByDescending(i => VersionOf(i.DisplayName) ?? new Version(0, 0))
                .ThenByDescending(i => IsUserDesktop(i.Path))
                .ThenByDescending(i => i.LastWrite)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .First();

            var extras = items.Where(i => !ReferenceEquals(i, keeper)).ToList();
            if (extras.Count == 0) continue;

            groups.Add(new DuplicateAppGroup(keeper, extras));
        }

        return groups;
    }

    /// <summary>从显示名里抽出版本号，例如 "分区助手 10.12.0" → 10.12.0，"FreeCAD 1.1" → 1.1。</summary>
    public static Version? VersionOf(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var match = Regex.Match(displayName, @"\d+(?:\.\d+)+");
        if (!match.Success) return null;
        return Version.TryParse(match.Value, out var version) ? version : null;
    }

    private static bool IsUserDesktop(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !path.StartsWith(@"C:\Users\Public", StringComparison.OrdinalIgnoreCase) &&
               path.StartsWith(profile, StringComparison.OrdinalIgnoreCase);
    }
}
