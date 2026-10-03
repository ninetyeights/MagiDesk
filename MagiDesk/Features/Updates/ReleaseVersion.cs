using System.Text.RegularExpressions;

namespace MagiDesk.Features.Updates;

internal sealed record ReleaseVersion(int Major, int Minor, int Patch, string[] Pre) : IComparable<ReleaseVersion>
{
    internal static ReleaseVersion? Parse(string text)
    {
        var match = Regex.Match(text, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) return null;
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
        if (pre.Any(p => p.All(char.IsAsciiDigit) && p.Length > 1 && p[0] == '0')) return null;
        return new(major, minor, patch, pre);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Pre.Length == 0 || other.Pre.Length == 0) return (Pre.Length == 0 ? 1 : 0).CompareTo(other.Pre.Length == 0 ? 1 : 0);
        for (int i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            string a = Pre[i], b = other.Pre[i];
            bool numericA = a.All(char.IsAsciiDigit), numericB = b.All(char.IsAsciiDigit);
            result = numericA && numericB ? (a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b))
                : numericA != numericB ? (numericA ? -1 : 1) : string.CompareOrdinal(a, b);
            if (result != 0) return result;
        }
        return Pre.Length.CompareTo(other.Pre.Length);
    }
}
