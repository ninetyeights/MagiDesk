namespace MagiDesk.Config;

/// <summary>How a box arranges its item tiles.</summary>
public enum BoxLayout
{
    /// <summary>Icon above name, wrapped into rows (the default).</summary>
    Grid = 0,
    /// <summary>One item per row: small icon + name, top to bottom.</summary>
    List = 1,
}

/// <summary>The key a box sorts its items by (folders always come first).</summary>
public enum SortBy
{
    Name = 0,
    Type = 1,      // by file extension
    Size = 2,
    Modified = 3,  // last-write time (content change)
    Created = 4,   // creation time (when added to this folder) — for "newest first"
}

/// <summary>One desktop fence box: a named, positioned window holding a set of
/// desktop items (by file path). Exactly one box per desktop is the "unsorted"
/// catch-all, which shows every item not explicitly placed in another box.</summary>
public sealed class DesktopBox
{
    public string Id   { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "盒子";
    /// <summary>The catch-all box. Its <see cref="Members"/> is ignored — it
    /// renders every item not assigned to another box. Not user-deletable.</summary>
    public bool   IsUnsorted { get; set; }

    // Bounds in DIPs.
    public double X { get; set; } = 200;
    public double Y { get; set; } = 200;
    public double W { get; set; } = 420;
    public double H { get; set; } = 320;

    /// <summary>Rolled up to just the title bar (double-click the title toggles).
    /// <see cref="H"/> keeps the expanded height so it restores correctly.</summary>
    public bool Collapsed { get; set; }

    // ---- appearance ----
    /// <summary>Box tint color as "RRGGBB". Null = the default gray.</summary>
    public string? BgColorHex { get; set; }
    /// <summary>Background transparency, 0 (opaque) .. 90 (very see-through).</summary>
    public int  Transparency { get; set; } = 60;
    /// <summary>System background blur: 0 = off, positive = on (including legacy value 2).</summary>
    public int BackgroundBlur { get; set; }
    public bool ShowBorder { get; set; } = false;
    public bool RoundedCorners { get; set; }
    /// <summary>Show item names under/next to the icon. False = icon-only.</summary>
    public bool ShowLabels   { get; set; } = true;
    /// <summary>Tile arrangement.</summary>
    public BoxLayout Layout  { get; set; } = BoxLayout.Grid;
    /// <summary>Item sort key (folders first, then by this key).</summary>
    public SortBy Sort { get; set; } = SortBy.Name;
    /// <summary>Reverse the sort order.</summary>
    public bool SortDescending { get; set; }

    /// <summary>When set, this box mirrors a folder's contents (a "folder portal")
    /// instead of holding assigned desktop items. Double-clicking a subfolder
    /// navigates into it. Null = a normal item box.</summary>
    public string? FolderPath { get; set; }

    /// <summary>Item file paths placed in this box (ignored for the unsorted and
    /// folder-mapped boxes).</summary>
    public List<string> Members { get; set; } = new();

    // Read-only compatibility input, cleared after converting old tabs to boxes.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<DesktopTab>? Tabs { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveTabId { get; set; }
}
