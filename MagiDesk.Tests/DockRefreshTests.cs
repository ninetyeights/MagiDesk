using MagiDesk.Config;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Tests;

internal static class DockRefreshTests
{
    internal static void ItemInvalidation()
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var app = new DockApplication { Id = "a", Name = "Editor", LaunchPath = "editor.exe", ExecutablePath = "editor.exe" };
        var item = new DockItem(app);
        var settings = new BrowserProfileSettings();
        string initial = DockItemRefreshSignature.Capture(item, settings, "menu");
        Check(initial == DockItemRefreshSignature.Capture(new DockItem(app), settings, "menu"), "new catalog objects with identical content retain controls");
        app.Name = "Renamed";
        Check(initial != DockItemRefreshSignature.Capture(item, settings, "menu"), "in-place name mutation invalidates cached tooltip");
        app.Name = "Editor";
        app.IconPath = "custom.png";
        Check(initial != DockItemRefreshSignature.Capture(item, settings, "menu"), "icon edit invalidates the affected item");
        app.IconPath = null;
        Check(initial != DockItemRefreshSignature.Capture(item with { RunningOnly = true }, settings, "menu"), "runtime-to-pinned conversion refreshes actions");
        Check(initial != DockItemRefreshSignature.Capture(item, settings, "new-menu"), "collection menu changes invalidate stale actions");
        settings.AvatarText = "New";
        Check(initial != DockItemRefreshSignature.Capture(item, settings, "menu"), "mutable profile style changes invalidate visuals");
    }
}
