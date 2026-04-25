using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagiDesk.Features.Zones;

namespace MagiDesk.Config;

public enum ResizeMode
{
    /// <summary>3×3 grid: 4 corners + 4 edges; center falls to bottom-right.</summary>
    ThreeByThree = 0,
    /// <summary>2×2 quadrants: 4 corners only (AltSnap default).</summary>
    TwoByTwoCorners = 1,
}

public enum ModifierKey
{
    Alt  = 0,
    Ctrl = 1,
}

/// <summary>
/// Process-wide settings. Kept intentionally small — plain JSON at
/// %APPDATA%\MagiDesk\config.json, read once at startup and rewritten
/// whenever the user changes something in MainWindow.
/// </summary>
public sealed class AppConfig
{
    // ---- Window Drag tool ------------------------------------------------
    public bool        WindowDragEnabled { get; set; } = true;
    public ResizeMode  ResizeMode        { get; set; } = ResizeMode.TwoByTwoCorners;
    /// <summary>Legacy single-modifier field — superseded by MoveModMask /
    /// ResizeModMask. Kept for config back-compat; migration fans it out to
    /// the masks on first load if both masks are zero.</summary>
    public ModifierKey Modifier          { get; set; } = ModifierKey.Alt;
    /// <summary>Bitmask of modifiers (Alt=1, Ctrl=2, Win=8) that must ALL be
    /// held for the left-drag move action to activate. Multiple bits allowed.</summary>
    public uint        MoveModMask       { get; set; } = 1; // Alt
    /// <summary>Same bitmask for the right-drag resize action.</summary>
    public uint        ResizeModMask     { get; set; } = 1; // Alt

    // ---- Zones (FancyZones-style) ----------------------------------------
    public bool    ZonesEnabled        { get; set; } = true;
    public int     ZonesRows           { get; set; } = 2;
    public int     ZonesColumns        { get; set; } = 2;
    public int     ZonesSpacing        { get; set; } = 8;
    /// <summary>
    /// When enabled, dragging a previously-snapped window WITHOUT Shift
    /// restores it to its pre-snap size (cursor stays on the title bar
    /// proportional to where the user grabbed it).
    /// </summary>
    public bool    ZonesRestoreOnDrag  { get; set; } = true;
    /// <summary>
    /// User-drawn custom layout as fractions of the work area. When set
    /// (non-null, non-empty), overrides the rows/cols grid.
    /// </summary>
    public List<RelRect>? ZonesCustom  { get; set; } = null;
    /// <summary>
    /// Per-row heights as fractions (sum = 1). Legacy field from the old
    /// grid editor — kept for migration only; new editor uses ZonesTree.
    /// </summary>
    public List<double>? ZonesRowPercents { get; set; } = null;
    public List<double>? ZonesColPercents { get; set; } = null;

    /// <summary>
    /// Legacy single-layout tree. Kept for migration only: on first load
    /// with this field set, we wrap it as a profile in <see cref="Layouts"/>
    /// and clear this field. New writes go through <see cref="Layouts"/>.
    /// </summary>
    public LayoutNode? ZonesTree { get; set; } = null;
    public int         ZonesTreeNextId { get; set; } = 0;

    // ---- Main window geometry --------------------------------------------
    /// <summary>Main window pixel bounds (virtual desktop coords). Null on
    /// first launch so we fall through to the XAML defaults.</summary>
    public double? WindowLeft     { get; set; }
    public double? WindowTop      { get; set; }
    public double? WindowWidth    { get; set; }
    public double? WindowHeight   { get; set; }
    public bool    WindowMaximized{ get; set; }

    /// <summary>User-created named layouts. Built-in templates are not stored here.</summary>
    public List<LayoutProfile> Layouts { get; set; } = new();

    /// <summary>
    /// Monitor ID → layout reference. Value is either <c>builtin:&lt;key&gt;</c>
    /// (for one of <see cref="BuiltInTemplates"/>) or a custom profile's
    /// <c>Guid.ToString()</c>.
    /// </summary>
    public Dictionary<string, string> MonitorAssignments { get; set; } = new();

    /// <summary>Last-selected monitor in the picker, restored on reopen.</summary>
    public string? SelectedMonitorId { get; set; }

    // ---- Browser badges (Chrome profile indicators) ----------------------
    public bool BrowserBadgeEnabled { get; set; } = true;
    /// <summary>Badge pill height in DIPs. Width is auto (avatar + name).</summary>
    public int  BrowserBadgeHeight  { get; set; } = 26;
    /// <summary>Show the profile name next to the avatar. Off = avatar-only pill.</summary>
    public bool BrowserBadgeShowName { get; set; } = true;
    /// <summary>When true, only show the first whitespace-separated word of
    /// the profile name. Keeps long names like "N808 Carlos Ribeiro" from
    /// overflowing the pill.</summary>
    public bool BrowserBadgeFirstWordOnly { get; set; } = true;
    /// <summary>Horizontal offset in DIPs: how far the badge's right edge is
    /// inset from the Chrome window's right frame edge. Stored in DIPs so it
    /// produces the same visual gap across different monitor DPIs.</summary>
    public double BrowserBadgeOffsetRight { get; set; } = 10;
    /// <summary>Vertical offset in DIPs: how far the badge's top edge is
    /// from the Chrome window's top frame edge.</summary>
    public double BrowserBadgeOffsetTop   { get; set; } = 42;
    /// <summary>When true, badges become click-through-disabled and draggable
    /// so the user can reposition them. Dragging saves the new offsets.</summary>
    public bool   BrowserBadgeUnlocked    { get; set; } = false;
    /// <summary>Per-profile overrides keyed by Chrome profile directory
    /// (e.g. "Default", "Profile 1"). Profiles not in the map use defaults.</summary>
    public Dictionary<string, BrowserProfileSettings> BrowserProfiles { get; set; } = new();

    // ---- Profile dock (taskbar-like floating strip of Chrome profiles) ----
    public bool   BrowserDockEnabled    { get; set; } = true;
    /// <summary>Size (DIPs) of each avatar button in the dock.</summary>
    public int    BrowserDockButtonSize { get; set; } = 36;
    /// <summary>Dock window position in DIPs. -1 = not yet positioned; the
    /// service will center it on the primary work area.</summary>
    public double BrowserDockX { get; set; } = -1;
    public double BrowserDockY { get; set; } = -1;
    /// <summary>Named profile groups. Profiles within a group render
    /// contiguously on the dock with a small gap separating groups.
    /// Profiles not in any group appear after all groups.</summary>
    public List<BrowserDockGroup> BrowserDockGroups { get; set; } = new();
    /// <summary>Visual style used between adjacent dock groups.</summary>
    public DockGroupSeparator BrowserDockSeparator { get; set; } = DockGroupSeparator.Gap;
    /// <summary>When true, profiles that aren't in any named group are
    /// hidden from the dock. Useful if the user has many profiles and only
    /// cares about the ones they've explicitly curated.</summary>
    public bool BrowserDockHideUngrouped { get; set; } = false;
    /// <summary>Explicit display order of ungrouped profiles. Profiles not in
    /// this list fall through to catalog order at the end. Populated lazily on
    /// first drag-drop reorder of an ungrouped profile so existing setups
    /// keep their catalog-based ordering until the user actually reorders.</summary>
    public List<string> BrowserDockUngroupedOrder { get; set; } = new();
    /// <summary>When true, drag-drop reordering and group-membership changes
    /// from the dock context menu are disabled. Avatar / visibility / close
    /// actions remain available — the lock only freezes layout, not function.</summary>
    public bool BrowserDockLocked { get; set; } = false;

    // ---- Quick Grid (ad-hoc rows×cols picker via hotkey) -----------------
    public bool QuickGridEnabled          { get; set; } = true;
    public bool QuickGridRestoreOnDrag    { get; set; } = true;
    /// <summary>Hotkey as RegisterHotKey modifiers bitmask. ALT=1, CTRL=2, SHIFT=4, WIN=8.</summary>
    public uint QuickGridHotkeyMods       { get; set; } = 2 | 4; // Ctrl+Shift
    /// <summary>Virtual-Key code. Default = VK_G (0x47).</summary>
    public uint QuickGridHotkeyVk         { get; set; } = 0x47;
    /// <summary>When true the picker shows every monitor; otherwise only the active window's monitor.</summary>
    public bool QuickGridShowAllMonitors  { get; set; } = false;
    /// <summary>Default rows/cols used the first time a new monitor is seen.</summary>
    public int  QuickGridDefaultRows      { get; set; } = 4;
    public int  QuickGridDefaultCols      { get; set; } = 6;
    /// <summary>Per-monitor rows×cols overrides. Key = monitor device name (szDevice).</summary>
    public Dictionary<string, QuickGridCells> QuickGridPerMonitor { get; set; } = new();

    // ------------------------------------------------------------ singleton

    // NOTE: ConfigPath + JsonOpts must be declared BEFORE _current.
    // Static field initializers run top-to-bottom; if _current (via Load())
    // runs first, these are still at their default values (null/null), so
    // Load() silently falls through to a fresh AppConfig and the next Save
    // overwrites the saved file — which is exactly what caused reopened
    // custom layouts to disappear.
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagiDesk",
        "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // Enums go as names ("TwoByTwoCorners", "Ctrl") instead of numeric
        // values, so the file is self-documenting and survives enum
        // reorderings without silently flipping user preferences.
        Converters = { new JsonStringEnumConverter() },
    };

    private static AppConfig _current = Load();
    public static AppConfig Current => _current;

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg is not null)
                {
                    cfg.MigrateLegacy();
                    Log($"LOAD ok Layouts={cfg.Layouts.Count} Assignments={cfg.MonitorAssignments.Count}");
                    return cfg;
                }
                Log("LOAD null after deserialize");
            }
            else Log($"LOAD no file at {ConfigPath}");
        }
        catch (Exception ex) { Log($"LOAD failed: {ex}"); }
        return new AppConfig();
    }

    /// <summary>
    /// Fold the old single-layout fields (ZonesTree / ZonesCustom) into the
    /// new profile library if they're present and no profiles exist yet.
    /// </summary>
    private void MigrateLegacy()
    {
        if (Layouts.Count > 0 || ZonesTree is null) return;
        Layouts.Add(new LayoutProfile
        {
            Name   = "Custom 1",
            Tree   = ZonesTree,
            NextId = Math.Max(1, ZonesTreeNextId),
        });
        ZonesTree       = null;
        ZonesTreeNextId = 0;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(ConfigPath, json);
            Log($"SAVE ok Layouts={Layouts.Count} bytes={json.Length}");
        }
        catch (Exception ex) { Log($"SAVE failed: {ex}"); }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Fired after a successful save so any open settings page can
    /// refresh its controls — useful when the tray menu or another page
    /// mutates config while this page is visible.</summary>
    public static event Action? Changed;

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "magidesk.log"),
                $"{DateTime.Now:HH:mm:ss.fff} CFG {msg}\n");
        }
        catch { }
    }
}
