using System.IO;
using MagiDesk.Native;

namespace MagiDesk.Features.DesktopFences;

internal static class MappedFolderRecovery
{
    internal sealed record Request(string BoxId, string Path, string? Identity);
    internal sealed record Result(Request Original, string? Identity, string? RecoveredPath, bool RootMatches = true);

    internal static Result Probe(Request request, Func<string, string?>? read = null,
        Func<string, string, string?>? resolve = null)
    {
        read ??= DesktopFileIdentity.Read; resolve ??= DesktopFileIdentity.Resolve;
        var current = Directory.Exists(request.Path) ? read(request.Path) : null;
        if (request.Identity is null) return new(request, current, null);
        if (current == request.Identity) return new(request, request.Identity, null);
        var found = resolve(request.Path, request.Identity);
        // Recheck after resolving: a concurrent replacement must never inherit the mapping.
        if (found is not null && Directory.Exists(found) && read(found) == request.Identity)
            return new(request, request.Identity, found);
        return new(request, request.Identity, null, RootMatches: false);
    }

    internal static bool IsCurrent(Result result, string? path, string? identity)
        => string.Equals(path, result.Original.Path, StringComparison.OrdinalIgnoreCase)
            && identity == result.Original.Identity;
}
