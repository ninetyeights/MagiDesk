using MagiDesk.Native;

namespace MagiDesk.Features.Zones;

/// <summary>A single rectangular drop-target region on a monitor, in screen pixels.</summary>
internal sealed record Zone(int Index, NativeMethods.RECT Bounds);

/// <summary>The set of zones a window will snap into, plus the resulting
/// merged rectangle. Holds 1 zone for an interior hover, 2 zones when the
/// cursor is near an internal edge between adjacent zones that form a
/// rectangle, or 4 zones when at a 2×2 corner intersection.</summary>
internal sealed record ZoneSelection(IReadOnlyList<Zone> Zones, NativeMethods.RECT MergedBounds);
