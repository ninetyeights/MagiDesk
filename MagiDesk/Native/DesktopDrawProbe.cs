using System.IO;
using System.Runtime.InteropServices;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Native;

internal static class DesktopDrawProbe
{
    internal const string Argument = "--probe-desktop-drawing";
    internal static string LogPath => Path.Combine(Path.GetTempPath(), "magidesk-desktop-draw-probe.log");
    internal static string LibraryPath => Path.Combine(AppContext.BaseDirectory, "MagiDeskDesktopDrawProbe.dll");
    [StructLayout(LayoutKind.Sequential)]
    private struct Result
    {
        public uint Error;
        public int Callbacks, Notifications, CustomDraw, ItemDraw;
        public int PrePaint, PostPaint, OtherStage;
        public int NotifyItem, SkipDefault, DefaultDraw, PrePaintFlags;
        public int Attempted, Installed, Restored, InstallError, Modified;
        public int Matched, Suppressed, IdentityError;
        public int BlockedMouse, BlockedSelection, BlockedRename, InteractionReady;
        public int OwnerData, StateEvents, CorrectedSelection, CorrectionFailures;
        public int NavigationSkips, NavigationBoundary;
        public int OwnerExited, BackgroundMenus, MenuError;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ObserveDesktop(IntPtr listView, [MarshalAs(UnmanagedType.LPWStr)] string? target, int protectInteraction, uint ownerPid, out Result result);

    internal static int Run(string? target = null, bool protectInteraction = false, uint ownerPid = 0)
    {
        // Only the helper loads native code. No native DLL is loaded by the WPF main process.
        try
        {
            File.WriteAllText(LogPath, $"{DateTime.Now:O} desktop drawing probe v9 protectInteraction={protectInteraction} ownerPid={ownerPid}\n");
            if (!string.IsNullOrWhiteSpace(target))
            {
                target = Path.GetFullPath(target);
                var parent = Path.GetDirectoryName(target);
                if (!new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }
                    .Any(f => string.Equals(parent, Environment.GetFolderPath(f), StringComparison.OrdinalIgnoreCase))
                    || !(File.Exists(target) || Directory.Exists(target)))
                    throw new InvalidOperationException("请选择用户桌面或公共桌面目录中的现有文件或文件夹（不含子目录内的项目）。");
                Log($"target={target} attributesBefore={File.GetAttributes(target)}");
            }
            else target = null;
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("当前探针只支持 x64。");
            if (!File.Exists(LibraryPath)) { Log("native probe DLL missing"); return 5; }
            if (!DesktopIcons.TryLocate(out var view, out var list, out var pid))
                throw new InvalidOperationException("没有找到原生桌面图标控件。");
            Log($"desktop={view} list={list} explorerPid={pid}");
            // Explorer pins this experimental module for callback safety. Load a unique
            // temporary copy so rebuilding/replacing the application DLL stays possible.
            var probeDirectory = Path.Combine(Path.GetTempPath(), "MagiDesk-DesktopDrawProbe-runs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probeDirectory);
            var probeLibrary = Path.Combine(probeDirectory, "MagiDeskDesktopDrawProbe.dll");
            File.Copy(LibraryPath, probeLibrary);
            Log($"nativeCopy={probeLibrary}");
            var module = NativeLibrary.Load(probeLibrary);
            if (!NativeLibrary.TryGetExport(module, "ObserveDesktopV9", out var entry))
            {
                Log("原生探针 DLL 版本过旧，请重新编译并复制 DLL，再测试。");
                return 7;
            }
            var observe = Marshal.GetDelegateForFunctionPointer<ObserveDesktop>(entry);
            int code = observe(list, target, protectInteraction ? 1 : 0, ownerPid, out var result);
            Log($"result={code} error={result.Error} callbacks={result.Callbacks} notifications={result.Notifications} customDraw={result.CustomDraw} itemDraw={result.ItemDraw}");
            Log($"prePaint={result.PrePaint} postPaint={result.PostPaint} otherStage={result.OtherStage} notifyItem={result.NotifyItem} skipDefault={result.SkipDefault} defaultDraw={result.DefaultDraw} prePaintFlags=0x{result.PrePaintFlags:X}");
            Log($"attempted={result.Attempted} installed={result.Installed} restored={result.Restored} installError={result.InstallError} modified={result.Modified}");
            Log($"matched={result.Matched} suppressed={result.Suppressed} identityError=0x{result.IdentityError:X8}");
            Log($"interactionReady={result.InteractionReady} blockedMouse={result.BlockedMouse} blockedSelection={result.BlockedSelection} blockedRename={result.BlockedRename}");
            Log($"ownerData={result.OwnerData} stateEvents={result.StateEvents} correctedSelection={result.CorrectedSelection} correctionFailures={result.CorrectionFailures}");
            Log($"navigationSkips={result.NavigationSkips} navigationBoundary={result.NavigationBoundary}");
            Log($"ownerExited={result.OwnerExited} backgroundMenus={result.BackgroundMenus} menuError=0x{result.MenuError:X8}");
            if (target is not null)
            {
                bool exists = File.Exists(target) || Directory.Exists(target);
                Log($"targetExistsAfter={exists} attributesAfter={(exists ? File.GetAttributes(target).ToString() : "missing")}");
            }
            if (result.CorrectionFailures > 0) return 10;
            if (code != 0 && result.InstallError == 170) return 9;
            return code != 0 ? code : target is not null ? (result.Suppressed > 0 ? 0 : 6) : result.ItemDraw > 0 ? 0 : 3;
        }
        catch (Exception ex) { Log(ex.ToString()); return 1; }
    }
    private static void Log(string text)
    {
        try { File.AppendAllText(LogPath, text + "\n"); } catch { }
    }
}
