using MagiDesk.Native;

namespace MagiDesk.Features.Zones;

/// <summary>A single rectangular drop-target region on a monitor, in screen pixels.</summary>
internal sealed record Zone(int Index, NativeMethods.RECT Bounds);
