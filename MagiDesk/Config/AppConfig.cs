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
    /// <summary>Floating (drag anywhere, free overlay) vs AppBar (taskbar-style
    /// strip pinned to the top edge that reserves screen space). See
    /// <see cref="DockMode"/>.</summary>
    public DockMode BrowserDockMode     { get; set; } = DockMode.Floating;
    /// <summary>Dock chrome colour scheme: follow Windows (live), or force
    /// light / dark. See <see cref="DockTheme"/>.</summary>
    public DockTheme BrowserDockTheme   { get; set; } = DockTheme.System;
    public bool BrowserDockAlignLeft { get; set; } = false;
    public Dictionary<string, string> BrowserLaunchArguments { get; set; } = new();
    /// <summary>Size (DIPs) of each avatar button in the dock.</summary>
    public int    BrowserDockButtonSize { get; set; } = 36;
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

    // ---- Desktop fences (custom-rendered desktop icon boxes) -------------
    /// <summary>Show desktop boxes without changing system desktop icons.</summary>
    public bool DesktopFencesEnabled { get; set; } = false;
    public bool DesktopUnifiedSurface { get; set; } = false;
    public Dictionary<string, DesktopIconPosition> DesktopIconPositions { get; set; } = new();
    public bool DesktopIconPositionsImported { get; set; }
    public bool DesktopMultiMonitorImported { get; set; }
    public uint DesktopFencesHotkeyMods { get; set; } = 2 | 4;
    public uint DesktopFencesHotkeyVk { get; set; } = 0x46; // Ctrl+Shift+F; zero disables.
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
    public bool QuickGridEnabled          { get; set; } = true;
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
    public bool EdgeSnapEnabled        { get; set; } = true;
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
    private static readonly string BackupPath = ConfigPath + ".bak";
    private static readonly string TempPath   = ConfigPath + ".tmp";
    private static readonly string BackupDir  = Path.Combine(
        Path.GetDirectoryName(ConfigPath)!, "backups");
    /// <summary>How many rolling daily snapshots to keep in <see cref="BackupDir"/>.</summary>
    private const int KeepDailyBackups = 7;

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
        var cfg = TryLoad(ConfigPath);
        if (cfg is not null) return cfg;

        // Primary unreadable (empty, half-written, or otherwise corrupt).
        // Quarantine it BEFORE returning so the next Save() doesn't silently
        // overwrite the evidence — that's exactly how we lost data once.
        QuarantineCorrupt(ConfigPath);

        cfg = TryLoad(BackupPath);
        if (cfg is not null)
        {
            try { File.Copy(BackupPath, ConfigPath, overwrite: true); }
            catch (Exception ex) { Log($"LOAD restore from .bak copy failed: {ex.Message}"); }
            Log("LOAD recovered from .bak");
            return cfg;
        }

        Log("LOAD falling back to defaults — neither config.json nor .bak usable");
        return new AppConfig();
    }

    private static AppConfig? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) { Log($"LOAD no file at {Path.GetFileName(path)}"); return null; }
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) { Log($"LOAD empty {Path.GetFileName(path)}"); return null; }
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
            if (cfg is null) { Log($"LOAD null after deserialize {Path.GetFileName(path)}"); return null; }
            MigrateBrowserKeys(cfg);
            Log($"LOAD ok {Path.GetFileName(path)} Layouts={cfg.Layouts.Count} Assignments={cfg.MonitorAssignments.Count}");
            return cfg;
        }
        catch (Exception ex) { Log($"LOAD failed {Path.GetFileName(path)}: {ex.Message}"); return null; }
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

    private static void QuarantineCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var dest = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(path, dest);
            Log($"quarantined corrupt config → {Path.GetFileName(dest)}");
        }
        catch (Exception ex) { Log($"quarantine failed: {ex.Message}"); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            // Atomic write: serialize into .tmp, then File.Replace swaps it
            // into place and moves the previous good copy into .bak. If the
            // process dies mid-write the half-written file is .tmp (ignored
            // by Load), not config.json. The .bak file lets a future Load
            // recover from a corrupted primary — see Load() above.
            File.WriteAllText(TempPath, json);
            if (File.Exists(ConfigPath))
                File.Replace(TempPath, ConfigPath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(TempPath, ConfigPath);
            Log($"SAVE ok Layouts={Layouts.Count} bytes={json.Length}");
            RollDailyBackup();
        }
        catch (Exception ex) { Log($"SAVE failed: {ex}"); }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>
    /// On the first Save of each calendar day, copy the just-written config
    /// into <c>backups/config-YYYYMMDD.json</c> and prune snapshots beyond
    /// <see cref="KeepDailyBackups"/>. Fast on subsequent Save() calls — just
    /// a File.Exists check — so the high-frequency settings UI doesn't churn
    /// disk. Independent of <c>.bak</c>, which only ever holds the previous
    /// version.
    /// </summary>
    private static void RollDailyBackup()
    {
        try
        {
            var today     = DateTime.Now.ToString("yyyyMMdd");
            var todayPath = Path.Combine(BackupDir, $"config-{today}.json");
            if (File.Exists(todayPath)) return;
            Directory.CreateDirectory(BackupDir);
            File.Copy(ConfigPath, todayPath);
            Log($"daily backup → config-{today}.json");

            // Filenames sort lexicographically by date thanks to yyyyMMdd, so
            // descending == newest first; keep the top N, delete the rest.
            var stale = Directory.GetFiles(BackupDir, "config-*.json")
                                 .OrderByDescending(f => f)
                                 .Skip(KeepDailyBackups);
            foreach (var f in stale)
            {
                try { File.Delete(f); Log($"pruned old backup {Path.GetFileName(f)}"); }
                catch (Exception ex) { Log($"prune {Path.GetFileName(f)} failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log($"daily backup failed: {ex.Message}"); }
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
