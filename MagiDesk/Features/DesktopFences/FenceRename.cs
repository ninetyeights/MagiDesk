using System.IO;

namespace MagiDesk.Features.DesktopFences;

internal static class FenceRename
{
    internal static string Target(string source, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("文件名无效，请勿使用路径分隔符或末尾空格、句点。");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
            throw new ArgumentException("不能使用 Windows 保留的设备名称。");
        return Path.Combine(Path.GetDirectoryName(source)!, name);
    }
}
