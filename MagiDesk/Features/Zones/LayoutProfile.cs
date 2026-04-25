using System.Text.Json.Serialization;
using System.Windows;

namespace MagiDesk.Features.Zones;

/// <summary>
/// A named, persistable layout. Built-in templates are generated on demand
/// (see <see cref="BuiltInTemplates"/>) and identified by <see cref="BuiltInKey"/>;
/// user-created layouts persist in AppConfig with their own <see cref="Id"/>.
/// </summary>
public sealed class LayoutProfile
{
    public Guid         Id       { get; set; } = Guid.NewGuid();
    public string       Name     { get; set; } = "Layout";
    public LayoutNode?  Tree     { get; set; }
    public int          NextId   { get; set; }

    /// <summary>Non-null for built-in templates only — not serialised.</summary>
    [JsonIgnore]
    public string? BuiltInKey    { get; set; }

    public LayoutTree ToTree()
    {
        if (Tree is null)
            return LayoutTree.UniformGrid(1, 1);
        return new LayoutTree(Tree, Math.Max(1, NextId));
    }

    /// <summary>
    /// Returns a working copy whose mutations don't affect this profile.
    /// Used by the editor so Esc/cancel leaves the stored profile intact.
    /// </summary>
    public LayoutTree ToTreeClone()
    {
        if (Tree is null) return LayoutTree.UniformGrid(1, 1);
        var json   = System.Text.Json.JsonSerializer.Serialize<LayoutNode>(Tree);
        var cloned = System.Text.Json.JsonSerializer.Deserialize<LayoutNode>(json)!;
        return new LayoutTree(cloned, Math.Max(1, NextId));
    }

    public void WriteFrom(LayoutTree tree)
    {
        Tree   = tree.Root;
        NextId = tree.NextId;
    }
}

/// <summary>
/// Stateless factory for the built-in templates shown in the picker.
/// Each template ID ("none", "focus", "cols-3", …) is persisted in
/// <see cref="MagiDesk.Config.AppConfig.MonitorAssignments"/> as
/// <c>builtin:&lt;key&gt;</c>.
/// </summary>
public static class BuiltInTemplates
{
    public const string Prefix = "builtin:";

    public sealed record Template(string Key, string Name, Func<LayoutTree> Build);

    public static IReadOnlyList<Template> All { get; } = new Template[]
    {
        new("none",     "No layout",      BuildNoLayout),
        new("cols-2",   "2 Columns",      () => LayoutTree.UniformGrid(1, 2)),
        new("cols-3",   "Columns",        () => LayoutTree.UniformGrid(1, 3)),
        new("grid-2x2", "Grid",           () => LayoutTree.UniformGrid(2, 2)),
        new("grid-3x2", "Grid 3×2",       () => LayoutTree.UniformGrid(2, 3)),
        new("priority", "Priority Grid",  BuildPriorityGrid),
    };

    public static Template? Find(string key)
    {
        foreach (var t in All) if (t.Key == key) return t;
        return null;
    }

    public static bool IsBuiltIn(string assignment, out string key)
    {
        if (assignment.StartsWith(Prefix, StringComparison.Ordinal))
        {
            key = assignment[Prefix.Length..];
            return true;
        }
        key = string.Empty;
        return false;
    }

    public static string MakeRef(string key) => Prefix + key;

    // ---- template builders -----------------------------------------------

    private static LayoutTree BuildNoLayout() => new(new ZoneLeaf { Id = 0 }, 1);

    /// <summary>Wide main zone + two stacked helpers on the right.</summary>
    private static LayoutTree BuildPriorityGrid()
    {
        var right = new SplitNode
        {
            Orientation = SplitOrientation.Horizontal,
            Fractions   = new() { 0.5, 0.5 },
            Children    = new List<LayoutNode>
            {
                new ZoneLeaf { Id = 1 },
                new ZoneLeaf { Id = 2 },
            },
        };
        var root = new SplitNode
        {
            Orientation = SplitOrientation.Vertical,
            Fractions   = new() { 0.6, 0.4 },
            Children    = new List<LayoutNode>
            {
                new ZoneLeaf { Id = 0 },
                right,
            },
        };
        return new LayoutTree(root, 3);
    }
}
