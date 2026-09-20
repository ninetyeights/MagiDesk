using System.Windows;
using System.Windows.Input;

namespace MagiDesk.Features.DesktopFences;

internal static class DesktopIconLayout
{
    internal static Point Limit(Point point, double width, double height, double cellWidth, double cellHeight, bool snap)
    {
        double x = double.IsFinite(point.X) ? point.X : 0, y = double.IsFinite(point.Y) ? point.Y : 0;
        if (snap)
        {
            x = Math.Round(x / cellWidth, MidpointRounding.AwayFromZero) * cellWidth;
            y = Math.Round(y / cellHeight, MidpointRounding.AwayFromZero) * cellHeight;
        }
        double maxX = Math.Max(0, width - cellWidth), maxY = Math.Max(0, height - cellHeight);
        if (snap) { maxX = Math.Floor(maxX / cellWidth) * cellWidth; maxY = Math.Floor(maxY / cellHeight) * cellHeight; }
        return new Point(Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY));
    }

    internal static Point[] Resolve(IReadOnlyList<string> keys, IReadOnlyDictionary<string, Point> saved,
        double width, double height, double cellWidth, double cellHeight, bool snap)
    {
        int columns = Math.Max(1, (int)(width / cellWidth)), rows = Math.Max(1, (int)(height / cellHeight));
        var result = new Point[keys.Count];
        var occupied = new HashSet<(int X, int Y)>();
        void Occupy(Point point)
        {
            for (int x = (int)(point.X / cellWidth); x <= (int)((point.X + cellWidth - 1) / cellWidth); x++)
            for (int y = (int)(point.Y / cellHeight); y <= (int)((point.Y + cellHeight - 1) / cellHeight); y++) occupied.Add((x, y));
        }
        var pending = new List<int>();
        for (int i = 0; i < keys.Count; i++)
        {
            if (!saved.TryGetValue(keys[i], out var point)) { pending.Add(i); continue; }
            // Keep overflow pages reachable while recovering horizontally after a
            // resolution change. Normal saved points are bounded by their page.
            double pageHeight = rows * cellHeight;
            int maxPage = Math.Max(0, (keys.Count - 1) / (columns * rows));
            int page = double.IsFinite(point.Y) ? Math.Max(0, (int)Math.Min(maxPage, point.Y / pageHeight)) : 0;
            point = Limit(new Point(point.X, point.Y - page * pageHeight), width, height, cellWidth, cellHeight, snap);
            point.Y += page * pageHeight;
            if (snap && occupied.Contains(((int)(point.X / cellWidth), (int)(point.Y / cellHeight)))) { pending.Add(i); continue; }
            result[i] = point; Occupy(point);
        }
        int slot = 0;
        foreach (int i in pending)
        {
            // Fill column-first, then add scrollable overflow pages.
            for (; ; slot++)
            {
                var point = new Point(slot / rows % columns * cellWidth,
                    (slot % rows + slot / (rows * columns) * rows) * cellHeight);
                if (occupied.Contains(((int)(point.X / cellWidth), (int)(point.Y / cellHeight)))) continue;
                result[i] = point; Occupy(point); slot++; break;
            }
        }
        return result;
    }

    internal static Point[] Move(IReadOnlyList<Point> positions, Vector delta, double width, double height,
        double cellWidth, double cellHeight, bool snap)
    {
        if (positions.Count == 0) return [];
        if (snap)
        {
            var anchor = positions[0] + delta;
            delta = new Vector(Math.Round(anchor.X / cellWidth) * cellWidth - positions[0].X,
                Math.Round(anchor.Y / cellHeight) * cellHeight - positions[0].Y);
            width = Math.Max(cellWidth, Math.Floor(width / cellWidth) * cellWidth);
            height = Math.Max(cellHeight, Math.Floor(height / cellHeight) * cellHeight);
        }
        double dx = Math.Clamp(delta.X, -positions.Min(p => p.X),
            Math.Max(-positions.Min(p => p.X), width - cellWidth - positions.Max(p => p.X)));
        double dy = Math.Clamp(delta.Y, -positions.Min(p => p.Y),
            Math.Max(-positions.Min(p => p.Y), height - cellHeight - positions.Max(p => p.Y)));
        return positions.Select(p => p + new Vector(dx, dy)).ToArray();
    }

    internal static int Next(IReadOnlyList<Point> points, int current, Key key)
    {
        if (points.Count == 0) return -1;
        if (key == Key.Home || current < 0) return 0;
        if (key == Key.End) return points.Count - 1;
        var origin = points[current];
        int best = current; double score = double.PositiveInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            var d = points[i] - origin;
            double forward = key switch { Key.Left => -d.X, Key.Right => d.X, Key.Up => -d.Y, Key.Down => d.Y, _ => 0 };
            if (forward <= 0) continue;
            double side = key is Key.Left or Key.Right ? Math.Abs(d.Y) : Math.Abs(d.X);
            double candidate = forward * forward + 4 * side * side;
            if (candidate < score) { best = i; score = candidate; }
        }
        return best;
    }

    internal static Point[] AvoidOccupied(Point[] proposed, Point[] original, IReadOnlyList<Point> others,
        double width, double height, double cellWidth, double cellHeight)
    {
        if (proposed.Length == 0) return proposed;
        var occupied = others.Select(p => ((int)Math.Round(p.X / cellWidth), (int)Math.Round(p.Y / cellHeight))).ToHashSet();
        bool Fits(Point[] points) => points.All(p => !occupied.Contains(((int)Math.Round(p.X / cellWidth), (int)Math.Round(p.Y / cellHeight))));
        if (Fits(proposed)) return proposed;
        double minX = proposed.Min(p => p.X), minY = proposed.Min(p => p.Y);
        int spanX = (int)Math.Round((proposed.Max(p => p.X) - minX) / cellWidth);
        int spanY = (int)Math.Round((proposed.Max(p => p.Y) - minY) / cellHeight);
        int columns = Math.Max(1, (int)(width / cellWidth)), rows = Math.Max(1, (int)(height / cellHeight));
        var candidates = new List<Point>();
        for (int x = 0; x < columns - spanX; x++)
        for (int y = 0; y < rows - spanY; y++) candidates.Add(new Point(x * cellWidth, y * cellHeight));
        foreach (var candidate in candidates.OrderBy(p => (p - new Point(minX, minY)).LengthSquared))
        {
            var moved = proposed.Select(p => p + (candidate - new Point(minX, minY))).ToArray();
            if (Fits(moved)) return moved;
        }
        return original; // No room for the whole selection: don't split or overlap it.
    }
}
