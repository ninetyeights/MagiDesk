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
    public bool AutomaticUpdateChecks { get; set; } = false;
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
    public bool ResizeSymmetricWithShift { get; set; } = true;
    public bool LinkedWindowResizeEnabled { get; set; } = false;

    // ---- Zones (FancyZones-style) ----------------------------------------
    public bool    ZonesEnabled        { get; set; } = false;
    /// <summary>Default rows/columns for a brand-new layout in the editor.</summary>
    public int     ZonesRows           { get; set; } = 2;
    public int     ZonesColumns        { get; set; } = 2;
    /// <summary>Gap (physical px) between zones in a default uniform grid.</summary>
    public int     ZonesSpacing        { get; set; } = 8;
    /// <summary>Legacy single custom-zone list (pre-layout-profiles). Kept as a
    /// fallback for old configs; new layouts live in <see cref="Layouts"/>.</summary>
    public List<RelRect>? ZonesCustom  { get; set; } = null;
    /// <summary>
    /// When enabled, dragging a previously-snapped window WITHOUT Shift
    /// restores it to its pre-snap size (cursor stays on the title bar
    /// proportional to where the user grabbed it).
    /// </summary>
    public bool    ZonesRestoreOnDrag  { get; set; } = true;
    /// <summary>Distance (physical pixels) from a zone edge within which the
    /// hover snap target expands to span 2 (along an edge) or 4 (at a corner)
    /// adjacent zones, when those zones form a clean rectangle. 0 disables
    /// the merge behavior. 14 is roughly a fingertip-width cushion at 100%
    /// scaling.</summary>
    public int     ZonesMergeBand      { get; set; } = 14;

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
    public bool BrowserBadgeEnabled { get; set; } = false;
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
    [JsonIgnore] // Position adjustment is a session, never restored on startup.
    public bool   BrowserBadgeUnlocked    { get; set; } = false;
    public Dictionary<string, BrowserBadgePosition> BrowserBadgePositions { get; set; } = new();
    [JsonIgnore]
    public string? BrowserBadgePositionScope { get; set; }
    /// <summary>Per-profile overrides keyed by Chrome profile directory
    /// (e.g. "Default", "Profile 1"). Profiles not in the map use defaults.</summary>
    public Dictionary<string, BrowserProfileSettings> BrowserProfiles { get; set; } = new();

    // ---- Profile dock (taskbar-like floating strip of Chrome profiles) ----
    public bool   BrowserDockEnabled    { get; set; } = false;
    /// <summary>Floating (drag anywhere, free overlay) vs AppBar (taskbar-style
    /// strip pinned to the top edge that reserves screen space). See
    /// <see cref="DockMode"/>.</summary>
    public DockMode BrowserDockMode     { get; set; } = DockMode.Floating;
    public int DockFloatingEdge { get; set; } = 2; // 0 free, 1 top, 2 bottom
    public int DockFloatingAlignment { get; set; } = 1; // left, center, right
    public int DockFloatingGap { get; set; } = 8; // DIPs
    public bool DockFloatingPositionLocked { get; set; }
    public bool DockFloatingAutoHide { get; set; }
    public bool DockRoundedCorners { get; set; }
    public int? DockFloatingDisplayMode { get; set; } // 0 smart, 1 auto-hide, 2 always; null migrates old auto-hide.
    public int DockFloatingHideDelayMs { get; set; } = 150;
    public string? DockFloatingAnchorMonitorId { get; set; }
    /// <summary>Dock chrome colour scheme: follow Windows (live), or force
    /// light / dark. See <see cref="DockTheme"/>.</summary>
    public DockTheme BrowserDockTheme   { get; set; } = DockTheme.System;
    public bool BrowserDockAlignLeft { get; set; } = false;
    public Dictionary<string, string> BrowserLaunchArguments { get; set; } = new();
    /// <summary>Size (DIPs) of each avatar button in the dock.</summary>
    public int    BrowserDockButtonSize { get; set; } = 36;
    public bool DockAdaptIconSize { get; set; }
    public Dictionary<string, int> DockMonitorIconSizes { get; set; } = new();
    /// <summary>Dock window position in DIPs. -1 = not yet positioned; the
    /// service will center it on the primary work area. Only used by the
    /// legacy single-monitor / "primary (auto)" path — explicit monitor
    /// targets persist per-monitor device-pixel positions in
    /// <see cref="BrowserDockMonitorPositions"/> instead.</summary>
    /// <summary>Free-floating position in physical pixels; null means never saved.</summary>
    public DockPoint? BrowserDockPositionPx { get; set; }
    public double BrowserDockX { get; set; } = -1;
    public double BrowserDockY { get; set; } = -1;
    /// <summary>Show the dock on a single monitor or on every monitor.</summary>
    public DockMonitorMode BrowserDockMonitorMode { get; set; } = DockMonitorMode.Single;
    /// <summary>In <see cref="DockMonitorMode.Single"/>, which monitor the dock
    /// sits on — the monitor's <c>szDevice</c> (e.g. <c>\\.\DISPLAY1</c>). Null
    /// (or an id that no longer resolves) falls back to the primary monitor and
    /// keeps the legacy free-drag behavior via <see cref="BrowserDockX"/>/Y.</summary>
    public string? BrowserDockMonitorId { get; set; } = null;
    /// <summary>Per-monitor floating positions (device pixels) keyed by
    /// <c>szDevice</c>. Used when the dock is pinned to an explicit monitor or
    /// spans all monitors — each window remembers where it was dragged on its
    /// own monitor. Missing entries default to centered along the top edge.</summary>
    public Dictionary<string, DockPoint> BrowserDockMonitorPositions { get; set; } = new();
    /// <summary>Named profile groups. Profiles within a group render
    /// contiguously on the dock with a small gap separating groups.
    /// Profiles not in any group appear after all groups.</summary>
    public List<BrowserDockGroup> BrowserDockGroups { get; set; } = new();
    public List<DockApplication> DockApplications { get; set; } = new();
    public List<DockNavigationGroup> DockNavigationGroups { get; set; } = new();
    public string? ActiveDockCollectionId { get; set; }
    public bool DockCollectionsInitialized { get; set; }
    public bool DockShowRunningApplications { get; set; } = true;
    public int DockOverflow { get; set; } // 0 scroll, 1 wrap; legacy 2 falls back to scroll
    public int DockMaxWidthPercent { get; set; } = 85;
    /// <summary>Visual style used between adjacent dock groups.</summary>
    public DockGroupSeparator BrowserDockSeparator { get; set; } = DockGroupSeparator.Gap;
    /// <summary>When true, profiles that aren't in any named group are
    /// hidden from the dock. Useful if the user has many profiles and only
    /// cares about the ones they've explicitly curated.</summary>
    public bool BrowserDockHideUngrouped { get; set; } = true;
    /// <summary>Explicit display order of ungrouped profiles. Profiles not in
    /// this list fall through to catalog order at the end. Populated lazily on
    /// first drag-drop reorder of an ungrouped profile so existing setups
    /// keep their catalog-based ordering until the user actually reorders.</summary>
    public List<string> BrowserDockUngroupedOrder { get; set; } = new();
    /// <summary>Prevent Dock content edits while retaining browsing, activation and collection switching.</summary>
    // Content layout lock (legacy JSON name retained); independent of floating position.
    public bool BrowserDockLocked { get; set; } = false;

    // ---- Desktop fences (custom-rendered desktop icon boxes) -------------
    /// <summary>Show unified desktop boxes, temporarily replacing system desktop icons.</summary>
    public bool DesktopFencesEnabled { get; set; } = false;
    public bool DesktopDefaultBoxInitialized { get; set; }
    public bool DesktopInitialClassificationInitialized { get; set; }
    public string? DesktopInitialClassificationBoxId { get; set; }
    public Dictionary<string, DesktopIconPosition> DesktopIconPositions { get; set; } = new();
    public bool DesktopIconPositionsImported { get; set; }
    public bool DesktopMultiMonitorImported { get; set; }
    public uint DesktopFencesHotkeyMods { get; set; } = 2 | 4;
    public uint DesktopFencesHotkeyVk { get; set; } = 0x44; // Ctrl+Shift+D; zero disables.
    /// <summary>User's fence boxes. One is flagged unsorted (the catch-all).
    /// The service seeds the unsorted box on first enable.</summary>
    public List<DesktopBox> DesktopBoxes { get; set; } = new();

    // ---- General app settings --------------------------------------------
    /// <summary>Show the system tray icon on startup. When off, closing the
    /// main window exits the app instead of hiding to tray.</summary>
    public bool TrayIconEnabled { get; set; } = true;
    /// <summary>Launch MagiDesk automatically when Windows starts (HKCU Run key).</summary>
    public bool AutoStartEnabled { get; set; } = false;

    // ---- Quick Grid (ad-hoc rows×cols picker via hotkey) -----------------
    public bool QuickGridEnabled          { get; set; } = false;
    public bool QuickGridPositionPreview { get; set; } = false;
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

    // ---- Edge snap (magnetic snapping during Alt-drag move) --------------
    /// <summary>Master switch for live edge snapping while moving a window with
    /// the Alt-drag (WindowDrag) modifier held.</summary>
    public bool EdgeSnapEnabled        { get; set; } = false;
    /// <summary>Snap distance in physical pixels: an edge within this many px of
    /// a target line jumps to align with it.</summary>
    public int  EdgeSnapBand           { get; set; } = 12;
    /// <summary>Snap to each monitor's work-area edges (excludes taskbar / AppBars).</summary>
    public bool EdgeSnapToMonitorEdges { get; set; } = true;
    /// <summary>Snap to nearby window edges: both opposite-edge contact and matching-edge alignment.</summary>
    public bool EdgeSnapToWindowEdges  { get; set; } = true;
    /// <summary>Align centers of nearby windows. Separate opt-in; the old combined
    /// EdgeSnapToWindowAlign JSON field is intentionally not migrated into this flag.</summary>
    public bool EdgeSnapToWindowCenters { get; set; } = false;

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

    public static string? RecoveryNotice { get; private set; }
    public static string? LastSaveError { get; private set; }
    public static event Action<string>? SaveFailed;
    private static readonly object SaveGate = new();
    private static AppConfig _current = Load();
    public static AppConfig Current => _current;

    private static AppConfig Load()
    {
        var cfg = MagiDesk.Infrastructure.ConfigStorage.Load(ConfigPath, Parse, out var notice);
        RecoveryNotice = notice;
        return cfg ?? new AppConfig();
    }

    private static AppConfig? Parse(string json)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
        if (cfg is null) return null;
        using (var document = JsonDocument.Parse(json))
            if (!document.RootElement.TryGetProperty(nameof(DockFloatingEdge), out _)) cfg.DockFloatingEdge = 0;
        MigrateBrowserKeys(cfg);
        MagiDesk.Features.ProfileDock.DockCollections.Ensure(cfg);
        return cfg;
    }

    /// <summary>
    /// Migrate browser-badge config from the original Chrome-only schema, where
    /// profiles were keyed by their bare directory name ("Default", "Profile 1"),
    /// to the multi-browser schema, where the key is browser-qualified
    /// ("chrome:Default"). Any key/list entry that doesn't already contain a
    /// ':' is assumed to be a legacy Chrome entry and gets the "chrome:" prefix.
    /// Profile directory names never contain ':', so the check can't misfire,
    /// and entries that are already qualified are left untouched (idempotent).
    /// </summary>
    private static void MigrateBrowserKeys(AppConfig cfg)
    {
        static string Q(string key) => key.Contains(':') ? key : "chrome:" + key;

        bool changed = false;

        if (cfg.BrowserProfiles.Count > 0 && cfg.BrowserProfiles.Keys.Any(k => !k.Contains(':')))
        {
            var migrated = new Dictionary<string, BrowserProfileSettings>();
            foreach (var (key, value) in cfg.BrowserProfiles)
                migrated[Q(key)] = value; // later duplicates win; collisions impossible here
            cfg.BrowserProfiles = migrated;
            changed = true;
        }

        foreach (var grp in cfg.BrowserDockGroups)
        {
            for (int i = 0; i < grp.ProfileDirs.Count; i++)
            {
                var q = Q(grp.ProfileDirs[i]);
                if (q != grp.ProfileDirs[i]) { grp.ProfileDirs[i] = q; changed = true; }
            }
        }

        for (int i = 0; i < cfg.BrowserDockUngroupedOrder.Count; i++)
        {
            var q = Q(cfg.BrowserDockUngroupedOrder[i]);
            if (q != cfg.BrowserDockUngroupedOrder[i]) { cfg.BrowserDockUngroupedOrder[i] = q; changed = true; }
        }

        if (changed) Log("migrated browser-badge config keys to browser-qualified form");
    }

    public void Save() => TrySave();

    public bool TrySave()
    {
        string? error;
        lock (SaveGate)
        {
            try
            {
                var json = JsonSerializer.Serialize(this, JsonOpts);
                MagiDesk.Infrastructure.ConfigStorage.TrySave(ConfigPath, json, out error);
            }
            catch (Exception ex) { error = ex.Message; }
            LastSaveError = error;
        }
        if (error is not null)
        {
            Log($"SAVE failed: {error}");
            try { SaveFailed?.Invoke(error); } catch { }
            return false;
        }
        // Isolate subscribers: one faulty service must not suppress all others.
        foreach (var handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { ((Action)handler)(); } catch (Exception ex) { Log($"Changed handler failed: {ex}"); }
        return true;
    }

    /// <summary>Fired after a successful save so any open settings page can
    /// refresh its controls — useful when the tray menu or another page
    /// mutates config while this page is visible.</summary>
    public static event Action? Changed;

    private static void Log(string msg)
    {
        try
        {
            MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} CFG {msg}\n");
        }
        catch { }
    }
}
