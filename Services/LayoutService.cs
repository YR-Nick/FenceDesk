using System.Windows;
using FenceDesk.Models;

namespace FenceDesk.Services;

public readonly record struct SnapResult(Rect Bounds, double? GuideX, double? GuideY);

public static class LayoutService
{
    public const double FenceWidth = 340;
    public const double FenceHeight = 392;
    public const double HeaderHeight = 46;
    public const double Gap = 20;
    public const double Grid = 8;

    public static AppState CreateInitialState(Rect workArea, IReadOnlyList<DesktopItem> items)
    {
        var state = new AppState();
        state.Settings.HasInitialized = true;

        var groups = items
            .Select(item => new
            {
                Item = item,
                Category = CategoryRules.Classify(item.Path, item.IsDirectory),
            })
            .GroupBy(x => x.Category.Key)
            .Select(g => new
            {
                Category = g.First().Category,
                Items = g.Select(x => x.Item).ToList(),
            })
            .OrderBy(g => g.Category.Order)
            .ToList();

        if (groups.Count == 0)
        {
            state.Fences.Add(new FenceModel
            {
                Title = "我的围栏",
                Accent = "teal",
                X = workArea.Left + Math.Max(24, (workArea.Width - FenceWidth) / 2),
                Y = workArea.Top + 80,
                Width = FenceWidth,
                Height = 260,
            });
            return state;
        }

        var maxColumns = Math.Max(1, (int)((workArea.Width - 88 + Gap) / (FenceWidth + Gap)));
        maxColumns = Math.Min(maxColumns, 5);
        var columns = groups.Count <= maxColumns
            ? groups.Count
            : groups.Count <= maxColumns * 2
                ? (int)Math.Ceiling(groups.Count / 2.0)
                : maxColumns;

        var rows = (int)Math.Ceiling(groups.Count / (double)columns);
        var totalWidth = columns * FenceWidth + (columns - 1) * Gap;
        var startX = workArea.Left + Math.Max(24, (workArea.Width - totalWidth) / 2);
        var startY = workArea.Top + 64;

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var col = i % columns;
            var row = i / columns;
            var fence = new FenceModel
            {
                Title = group.Category.Title,
                Accent = group.Category.Accent,
                CategoryKey = group.Category.Key,
                X = startX + col * (FenceWidth + Gap),
                Y = startY + row * (FenceHeight + Gap),
                Width = FenceWidth,
                Height = FenceHeight,
            };

            foreach (var item in group.Items)
            {
                fence.Items.Add(new FenceItemModel(item.Path));
            }

            state.Fences.Add(fence);
        }

        return state;
    }

    public static Rect FindFreeSpot(Rect workArea, IEnumerable<Rect> occupied, double width, double height)
    {
        var existing = occupied.ToList();
        var margin = 40.0;
        var step = 30.0;

        for (var y = workArea.Top + 64; y <= workArea.Bottom - Math.Min(height, 160); y += step)
        {
            for (var x = workArea.Left + margin; x <= workArea.Right - width - margin; x += step)
            {
                var candidate = new Rect(x, y, width, height);
                if (existing.All(r => !r.IntersectsWith(candidate) &&
                                      !r.Contains(new Point(candidate.Left + 20, candidate.Top + 20))))
                {
                    return candidate;
                }
            }
        }

        var bottom = existing.Count == 0 ? workArea.Top + 80 : existing.Max(r => r.Bottom) + Gap;
        if (bottom > workArea.Bottom - 80)
        {
            bottom = workArea.Top + 64;
        }

        return new Rect(
            workArea.Left + Math.Max(margin, (workArea.Width - width) / 2),
            bottom,
            width,
            height);
    }

    public static Point ClampPosition(double x, double y, double width, double height, Rect area)
    {
        var minX = area.Left + 12 - width + 110;
        var maxX = area.Right - 110;
        var minY = area.Top + 4;
        var maxY = area.Bottom - HeaderHeight - 4;

        if (maxX < minX) minX = maxX = area.Left + 12;
        if (maxY < minY) minY = maxY = area.Top + 12;

        return new Point(
            Math.Clamp(x, minX, maxX),
            Math.Clamp(y, minY, maxY));
    }

    public static void ClampFence(FenceModel fence, Rect area)
    {
        var position = ClampPosition(fence.X, fence.Y, fence.Width, fence.Height, area);
        fence.X = position.X;
        fence.Y = position.Y;
        fence.Width = Math.Min(fence.Width, Math.Max(250, area.Width - 24));
        fence.Height = Math.Min(fence.Height, Math.Max(150, area.Height - 40));
    }

    public static SnapResult SnapMove(Rect proposed, IEnumerable<Rect> others, Rect area, bool disableSnap)
    {
        if (disableSnap)
        {
            return new SnapResult(proposed, null, null);
        }

        var x = proposed.X;
        var y = proposed.Y;
        double? guideX = null;
        double? guideY = null;

        var xTargets = new List<double> { area.Left + 40, area.Left + area.Width / 2, area.Right - proposed.Width - 40 };
        var yTargets = new List<double> { area.Top + 64 };

        foreach (var other in others)
        {
            xTargets.Add(other.Left);
            xTargets.Add(other.Right);
            xTargets.Add(other.Left + (other.Width - proposed.Width) / 2);
            yTargets.Add(other.Top);
            yTargets.Add(other.Bottom);
        }

        var bestX = double.MaxValue;
        foreach (var target in xTargets)
        {
            var delta = target - proposed.Left;
            if (Math.Abs(delta) < bestX && Math.Abs(delta) <= 10)
            {
                bestX = Math.Abs(delta);
                x = target;
                guideX = target;
            }

            var rightDelta = target - proposed.Right;
            if (Math.Abs(rightDelta) < bestX && Math.Abs(rightDelta) <= 10)
            {
                bestX = Math.Abs(rightDelta);
                x = target - proposed.Width;
                guideX = target;
            }
        }

        if (guideX is null)
        {
            var snapped = Math.Round(proposed.X / Grid) * Grid;
            if (Math.Abs(snapped - proposed.X) <= 3.5) x = snapped;
            else x = proposed.X;
        }

        var bestY = double.MaxValue;
        foreach (var target in yTargets)
        {
            var delta = target - proposed.Top;
            if (Math.Abs(delta) < bestY && Math.Abs(delta) <= 10)
            {
                bestY = Math.Abs(delta);
                y = target;
                guideY = target;
            }

            var bottomDelta = target - proposed.Bottom;
            if (Math.Abs(bottomDelta) < bestY && Math.Abs(bottomDelta) <= 10)
            {
                bestY = Math.Abs(bottomDelta);
                y = target - proposed.Height;
                guideY = target;
            }
        }

        if (guideY is null)
        {
            var snapped = Math.Round(proposed.Y / Grid) * Grid;
            if (Math.Abs(snapped - proposed.Y) <= 3.5) y = snapped;
            else y = proposed.Y;
        }

        return new SnapResult(new Rect(x, y, proposed.Width, proposed.Height), guideX, guideY);
    }

    public static SnapResult SnapResize(Rect proposed, IEnumerable<Rect> others, Rect area, bool disableSnap)
    {
        if (disableSnap) return new SnapResult(proposed, null, null);

        var result = proposed;
        double? guideX = null;
        double? guideY = null;

        foreach (var other in others)
        {
            if (Math.Abs(result.Right - other.Left) <= 10)
            {
                result.Width = other.Left - result.Left;
                guideX = other.Left;
                break;
            }
        }

        foreach (var other in others)
        {
            if (Math.Abs(result.Bottom - other.Top) <= 10)
            {
                result.Height = other.Top - result.Top;
                guideY = other.Top;
                break;
            }
        }

        result.Width = Math.Clamp(result.Width, 250, Math.Max(250, area.Right - result.Left - 12));
        result.Height = Math.Clamp(result.Height, 150, Math.Max(150, area.Bottom - result.Top - 12));
        return new SnapResult(result, guideX, guideY);
    }
}
