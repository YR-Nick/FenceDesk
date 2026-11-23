using System.IO;
using FenceDesk.Models;

namespace FenceDesk.Services;

/// <summary>
/// "按使用次数"排序的排名规则。
/// 用带半衰期的热度（不是裸计数）——常用的排前面，但两个月没碰过的应用会自然回落，
/// 不会永远占着第一屏。
/// </summary>
public static class UsageRanking
{
    /// <summary>热度半衰期：30 天不用，权重减半。</summary>
    public const double HalfLifeDays = 30.0;

    /// <summary>每 3 次热度算一个档；同档内不再互相插队，避免"多用一次就换位"。</summary>
    public const double BucketSize = 3.0;

    /// <summary>把历史热度按经过的时间衰减到 nowUtc。</summary>
    public static double Effective(FenceItemModel item, DateTime nowUtc)
    {
        if (item.UsageScore <= 0) return 0;
        var days = (nowUtc - item.UsageDecayedUtc).TotalDays;
        if (days <= 0) return item.UsageScore;
        return item.UsageScore * Math.Pow(0.5, days / HalfLifeDays);
    }

    /// <summary>热度分档，用于稳定排序。</summary>
    public static int Bucket(FenceItemModel item, DateTime nowUtc)
        => (int)Math.Floor(Effective(item, nowUtc) / BucketSize);

    /// <summary>记录一次启动：先把旧热度衰减到此刻，再加 1。</summary>
    public static void Record(FenceItemModel item, DateTime nowUtc)
    {
        item.UsageScore = Effective(item, nowUtc) + 1.0;
        item.UsageDecayedUtc = nowUtc;
        item.LaunchCount++;
    }

    /// <summary>按使用次数排序：热度档位高的在前，同档按实际热度、再按名称。</summary>
    public static List<FenceItemModel> Order(IEnumerable<FenceItemModel> items, DateTime nowUtc)
        => items
            .OrderByDescending(i => Bucket(i, nowUtc))
            .ThenByDescending(i => Effective(i, nowUtc))
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>通用排序入口（名称 / 类型 / 修改时间 / 使用次数）。</summary>
    public static List<FenceItemModel> Order(IEnumerable<FenceItemModel> items, FenceSortMode mode,
        DateTime nowUtc)
    {
        var list = items.ToList();
        return mode switch
        {
            FenceSortMode.Type => list
                .OrderBy(i => i.IsDirectory ? "0" : Path.GetExtension(i.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            FenceSortMode.Modified => list
                .OrderByDescending(LastWrite)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            FenceSortMode.Usage => Order(list, nowUtc),
            FenceSortMode.Manual => list,
            _ => list.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
        };
    }

    private static DateTime LastWrite(FenceItemModel item)
    {
        try
        {
            return File.GetLastWriteTime(item.Path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
