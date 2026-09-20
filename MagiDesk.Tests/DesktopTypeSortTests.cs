using System.IO;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Tests;

internal static class DesktopTypeSortTests
{
    private static DesktopItem Item(string name, string? type, bool folder = false) =>
        new(@"C:\" + name, name, null, folder, 0, default, default) { TypeName = type };
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Type sort regression"); }
    private static List<DesktopItem> Sort(params DesktopItem[] items) => DesktopItems.Sort(items, SortBy.Type, false, shellTypes: true);

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("desktop types: localized type takes precedence over extension", () =>
        {
            var a = Item("a.aaa", "Z type"); var z = Item("z.zzz", "A type");
            Check(Sort(a, z).SequenceEqual(new[] { z, a }));
        });
        yield return ("desktop types: equivalent types use display name", () =>
        {
            var a = Item("a.jpeg", "Image"); var z = Item("z.bmp", "Image");
            Check(Sort(z, a).SequenceEqual(new[] { a, z }));
        });
        yield return ("desktop types: descending reverses type and name", () =>
        {
            var items = new[] { Item("a.x", "A"), Item("b.y", "A"), Item("c.z", "Z") };
            Check(DesktopItems.Sort(items, SortBy.Type, true, true).SequenceEqual(items.Reverse()));
        });
        yield return ("desktop types: ordinary boxes retain extension sorting", () =>
        {
            var a = Item("a.aaa", "Z"); var z = Item("z.zzz", "A");
            Check(DesktopItems.Sort(new[] { z, a }, SortBy.Type, false).SequenceEqual(new[] { a, z }));
        });
        yield return ("desktop types: failed Shell lookup has deterministic fallback", () =>
        {
            Check(DesktopItems.TypeSortName(Item("a.xyz", null)) == ".xyz");
            Check(DesktopItems.TypeSortName(Item("folder.xyz", null, true)) == "");
            Check(DesktopItems.TypeSortName(Item("noextension", " ")) == "");
        });
        yield return ("desktop types: special icons remain first in both directions", () =>
        {
            var pc = Item("pc", "Z") with { Path = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" };
            var bin = Item("bin", "A") with { Path = RecycleBinDrop.PathId };
            var file = Item("a.txt", "A");
            foreach (bool desc in new[] { false, true })
                Check(DesktopItems.Sort(new[] { file, bin, pc }, SortBy.Type, desc, true).SequenceEqual(new[] { pc, bin, file }));
        });
        yield return ("desktop types: other sort keys and source order unaffected", () =>
        {
            var items = new[] { Item("z.a", "A"), Item("a.z", "Z") };
            Check(DesktopItems.Sort(items, SortBy.Name, false, true).SequenceEqual(items.Reverse()));
            Check(items[0].Name == "z.a");
        });
        yield return ("desktop types: real background enumeration captures Shell type", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "MagiDesk-TypeTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "test.txt"), "test");
                Directory.CreateDirectory(Path.Combine(dir, "folder.with.extension"));
                var items = Task.Run(() => DesktopItems.EnumerateFolder(dir)).GetAwaiter().GetResult();
                Check(items.Count == 2 && items.All(i => !string.IsNullOrWhiteSpace(i.TypeName)));
                Check(items.Single(i => i.IsFolder).TypeName != items.Single(i => !i.IsFolder).TypeName);
            }
            finally { Directory.Delete(dir, true); }
        });
    }
}
