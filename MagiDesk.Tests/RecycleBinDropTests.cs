using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Tests;

internal static class RecycleBinDropTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Recycle drop assertion failed"); }
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("recycle: namespace identity, not display name", () =>
        { Check(RecycleBinDrop.IsTarget(RecycleBinDrop.PathId.ToLowerInvariant())); Check(!RecycleBinDrop.IsTarget("Recycle Bin")); });
        yield return ("recycle: accepts move from files", () => Check(Effect(new[] { @"C:\test.txt" }) == DragDropEffects.Move));
        yield return ("recycle: rejects mixed shell selection", () => Check(Effect(new[] { @"C:\test.txt", RecycleBinDrop.PathId }) == DragDropEffects.None));
        yield return ("recycle: rejects empty selection", () => { Check(Effect(null) == 0); Check(Effect(Array.Empty<string>()) == 0); });
        foreach (var path in new[] { @"C:\", "relative.txt", "", "::invalid", @"C:\*.txt", "C:\\bad\0.txt" })
        {
            var invalid = path;
            yield return ($"recycle: invalid operand {path.Replace('\0', '_')}", () => Check(Effect(new[] { invalid }) == 0));
        }
        foreach (var modifier in new[] { DragDropKeyStates.ControlKey, DragDropKeyStates.ShiftKey, DragDropKeyStates.AltKey })
        {
            var keys = modifier;
            yield return ($"recycle: rejects {keys} gesture", () => Check(RecycleBinDrop.Effect(new[] { @"C:\a" }, DragDropEffects.All, keys) == 0));
        }
        yield return ("recycle: copy-only source is not deleted", () => Check(RecycleBinDrop.Effect(new[] { @"C:\a" }, DragDropEffects.Copy, 0) == 0));
        yield return ("recycle: native flags, owner and double-null operands", () =>
        {
            Check(ShellOps.Recycle(new[] { @"C:\one.txt", @"C:\two.txt" }, new IntPtr(123), (ref ShellOps.SHFILEOPSTRUCT op) =>
            {
                Check(op.wFunc == 3 && op.hwnd == new IntPtr(123) && op.pTo == IntPtr.Zero);
                Check((op.fFlags & 0x40) != 0 && (op.fFlags & (0x10 | 0x4 | 0x400)) == 0);
                var expected = "C:\\one.txt\0C:\\two.txt\0\0";
                Check(Marshal.PtrToStringUni(op.pFrom, expected.Length) == expected);
                return 0;
            }));
        });
        yield return ("recycle: native cancellation, failure and exception", () =>
        {
            var paths = new[] { @"C:\one.txt" };
            Check(!ShellOps.Recycle(paths, IntPtr.Zero, (ref ShellOps.SHFILEOPSTRUCT op) => { op.fAnyOperationsAborted = 1; return 0; }));
            Check(!ShellOps.Recycle(paths, IntPtr.Zero, (ref ShellOps.SHFILEOPSTRUCT op) => 5));
            Check(!ShellOps.Recycle(paths, IntPtr.Zero, (ref ShellOps.SHFILEOPSTRUCT op) => throw new IOException()));
        });
        yield return ("recycle: validate entire batch and deduplicate without deleting files", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "MagiDesk-RecycleTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "test.txt");
            try
            {
                File.WriteAllText(file, "unchanged");
                int calls = 0;
                bool Fake(IReadOnlyList<string> paths) { calls++; Check(paths.Count == 1); return true; }
                Check(!RecycleBinDrop.Execute(new[] { file, Path.Combine(dir, "missing") }, Fake));
                Check(calls == 0);
                Check(RecycleBinDrop.Execute(new[] { file, file }, Fake) && calls == 1);
                Check(!RecycleBinDrop.Execute(new[] { file }, _ => false));
                Check(File.ReadAllText(file) == "unchanged");
            }
            finally { Directory.Delete(dir, true); }
        });
    }
    private static DragDropEffects Effect(string[]? paths) => RecycleBinDrop.Effect(paths, DragDropEffects.Copy | DragDropEffects.Move, 0);
}
