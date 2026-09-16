using System.Windows.Input;

namespace MagiDesk.Features.DesktopFences;

internal static class FenceKeyboardNavigation
{
    internal static int NextColumnFirst(int current, int count, int rows, Key key)
    {
        if (count <= 0) return -1;
        if (key == Key.Home || current < 0) return 0;
        if (key == Key.End) return count - 1;
        rows = Math.Max(1, rows);
        return key switch
        {
            Key.Up => current % rows == 0 ? current : current - 1,
            Key.Down => current % rows == rows - 1 ? current : Math.Min(count - 1, current + 1),
            Key.Left => current < rows ? current : current - rows,
            Key.Right => current / rows >= (count - 1) / rows ? current : Math.Min(count - 1, current + rows),
            _ => current,
        };
    }

    internal static int Next(int current, int count, int columns, Key key)
    {
        if (count <= 0) return -1;
        if (key == Key.Home) return 0;
        if (key == Key.End) return count - 1;
        if (current < 0) return 0;
        columns = Math.Max(1, columns);
        int delta = key switch { Key.Left => -1, Key.Right => 1, Key.Up => -columns, Key.Down => columns, _ => 0 };
        return Math.Clamp(current + delta, 0, count - 1);
    }
}
