using System.Text.Json;
using MagiDesk.Config;

namespace MagiDesk.Features.ProfileDock;

internal static class DockItemRefreshSignature
{
    // Layout, theme and transient foreground state must not invalidate icon controls.
    internal static string Capture(DockItem item, BrowserProfileSettings? settings, string menuContext)
        => JsonSerializer.Serialize(new { item.Profile, item.Application, item.RunningOnly, settings, menuContext });
}
