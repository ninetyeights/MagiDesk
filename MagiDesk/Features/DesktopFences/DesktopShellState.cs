using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MagiDesk.Features.DesktopFences;

internal static class DesktopShellState
{
    internal static bool SameItems(IReadOnlyList<DesktopItem> previous, IReadOnlyList<DesktopItem> current)
        => previous.OrderBy(i => i.Path, StringComparer.Ordinal).Select(i => (i.Path, i.Name))
            .SequenceEqual(current.OrderBy(i => i.Path, StringComparer.Ordinal).Select(i => (i.Path, i.Name)));
    internal static IReadOnlyList<DesktopItem> Merge(IReadOnlyList<DesktopItem> items, IReadOnlyList<DesktopItem>? latest)
        => latest is null ? items : items.Where(i => !i.IsShellItem).Concat(latest.Where(i => i.IsShellItem)).ToArray();
    internal static string? Capture(IEnumerable<string> identities)
    {
        try
        {
            var text = new StringBuilder();
            var recycle = new RecycleInfo { Size = (uint)Marshal.SizeOf<RecycleInfo>() };
            if (SHQueryRecycleBin(null, ref recycle) >= 0) text.Append(recycle.Items).Append(':').Append(recycle.Bytes);
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                Read(hive, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons", text, 1);
                Read(hive, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", text, 0);
                Read(hive, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace", text, 1);
                foreach (var id in identities.Where(DesktopItems.IsShellPath).Select(p => p[2..]).Distinct().Order())
                {
                    Read(hive, @"Software\Classes\CLSID\" + id, text, 1);
                    Read(hive, @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\" + id, text, 1);
                }
            }
            return text.ToString();
        }
        catch { return null; }
    }
    private static void Read(RegistryKey hive, string path, StringBuilder text, int depth)
    {
        text.Append(hive.Name).Append(path);
        using var key = hive.OpenSubKey(path);
        if (key is null) { text.Append("<missing>"); return; }
        foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal))
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string data = value switch { byte[] bytes => Convert.ToHexString(bytes), string[] lines => string.Join('\0', lines), _ => value?.ToString() ?? "" };
            text.Append(name.Length).Append(':').Append(name).Append(data.Length).Append(':').Append(data);
        }
        if (depth > 0) foreach (var child in key.GetSubKeyNames().Order(StringComparer.Ordinal)) Read(hive, path + "\\" + child, text, depth - 1);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RecycleInfo { public uint Size; public long Bytes, Items; }
    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref RecycleInfo info);
}
