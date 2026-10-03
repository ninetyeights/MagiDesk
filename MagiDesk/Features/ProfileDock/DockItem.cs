using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Features.ProfileDock;

/// <summary>Presentation boundary; never encode applications as browser profiles.
/// Existing profile keys/config remain unchanged. Future layouts can reference Key.</summary>
public sealed record DockItem
{
    public ChromeProfile? Profile { get; }
    public DockApplication? Application { get; }
    public bool RunningOnly { get; init; }
    public string Key => Profile?.Key ?? ApplicationKey(Application!.Id);
    public string Name => Profile?.Name ?? Application!.Name;
    public DockItem(ChromeProfile profile) => Profile = profile;
    public DockItem(DockApplication application) => Application = application;
    public static string ApplicationKey(string id) => "app:" + id;
}
