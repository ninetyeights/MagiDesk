using MagiDesk.Config;

namespace MagiDesk.Features.DesktopFences;

internal static class DesktopMembershipRecovery
{
    internal const int MissingLimit = 256;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    internal static bool Reconcile(IEnumerable<DesktopBox> boxes, DesktopMembershipSnapshot snapshot, DateTime now,
        Action<string, string>? renamed = null)
    {
        bool changed = false;
        var allBoxes = boxes.ToArray();
        // Explicit drag/drop wins over dormant recovery even when the assignment
        // happened before the latest watcher/snapshot discovered the file.
        foreach (var owner in allBoxes)
        foreach (var pending in owner.MemberReferences.Where(r => r.PendingAssignment).ToArray())
        {
            if (!snapshot.Files.TryGetValue(pending.Path, out var identity) || identity is null)
            {
                if (owner.IsUnsorted && snapshot.Covers(pending.Path) && !snapshot.Files.ContainsKey(pending.Path))
                { owner.MemberReferences.Remove(pending); changed = true; }
                continue;
            }
            foreach (var other in allBoxes)
            {
                var obsolete = other.MemberReferences.Where(r => !ReferenceEquals(r, pending)
                    && (r.Identity == identity || SamePath(r.Path, pending.Path))).ToArray();
                foreach (var record in obsolete)
                {
                    other.MemberReferences.Remove(record);
                    other.Members.RemoveAll(p => SamePath(p, record.Path));
                }
            }
            pending.Identity = identity;
            pending.PendingAssignment = false;
            if (owner.IsUnsorted) owner.MemberReferences.Remove(pending);
            else if (!owner.Members.Any(p => SamePath(p, pending.Path))) owner.Members.Add(pending.Path);
            changed = true;
        }
        var candidates = snapshot.Files.Where(p => p.Value is not null)
            .GroupBy(p => p.Value!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray(), StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in allBoxes.Where(b => !b.IsUnsorted && b.FolderPath is null))
        {
            foreach (var path in box.Members.Where(p => !DesktopItems.IsShellPath(p)))
            {
                if (box.MemberReferences.Any(r => SamePath(r.Path, path))) continue;
                box.MemberReferences.Add(new() { Path = path });
                changed = true;
            }
            var active = new List<string>();
            // Preserve legacy namespace membership; it is not a filesystem identity.
            active.AddRange(box.Members.Where(DesktopItems.IsShellPath));
            foreach (var reference in box.MemberReferences.ToArray())
            {
                if (reference.MissingSinceUtc is { } missing && now - missing > Retention)
                {
                    box.MemberReferences.Remove(reference);
                    changed = true;
                    continue;
                }
                string original = reference.Path;
                if (!snapshot.Covers(original))
                {
                    // Unavailable root is not evidence of deletion.
                    if (reference.MissingSinceUtc is null && claimed.Add(original)) active.Add(original);
                    continue;
                }
                bool exists = snapshot.Files.TryGetValue(original, out var currentId);
                if (exists && currentId is null)
                {
                    // Unsupported filesystem / access denied: preserve prior state,
                    // never reattach a missing item based on its name alone.
                    if (reference.MissingSinceUtc is null && claimed.Add(original)) active.Add(original);
                    continue;
                }
                if (reference.Identity is null && exists)
                {
                    reference.Identity = currentId;
                    changed = true;
                }
                string? resolved = exists && currentId == reference.Identity ? original : null;
                if (resolved is null && snapshot.IsComplete && reference.Identity is { } identity
                    && candidates.TryGetValue(identity, out var paths) && paths.Length == 1)
                    resolved = paths[0];
                if (resolved is not null && claimed.Add(resolved))
                {
                    if (resolved != original)
                    {
                        reference.Path = resolved;
                        renamed?.Invoke(original, resolved);
                        changed = true;
                    }
                    if (reference.MissingSinceUtc is not null) { reference.MissingSinceUtc = null; changed = true; }
                    active.Add(resolved);
                }
                else if (reference.MissingSinceUtc is null)
                {
                    reference.MissingSinceUtc = now;
                    changed = true;
                }
            }
            var expired = box.MemberReferences.Where(r => r.MissingSinceUtc is { } since
                && (r.Identity is null || now - since > Retention)).ToHashSet();
            foreach (var extra in box.MemberReferences.Where(r => r.MissingSinceUtc is not null && !expired.Contains(r))
                         .OrderByDescending(r => r.MissingSinceUtc).Skip(MissingLimit)) expired.Add(extra);
            if (box.MemberReferences.RemoveAll(expired.Contains) > 0) changed = true;
            if (!box.Members.SequenceEqual(active, StringComparer.Ordinal))
            {
                box.Members = active;
                changed = true;
            }
        }
        return changed;
    }

    internal static void Assign(IEnumerable<DesktopBox> boxes, DesktopBox target, IEnumerable<string> paths,
        DesktopMembershipSnapshot? snapshot)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? identity = null;
            snapshot?.Files.TryGetValue(path, out identity);
            foreach (var box in boxes)
            {
                var removedPaths = box.MemberReferences.Where(r => SamePath(r.Path, path)
                    || (identity is not null && r.Identity == identity)).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                box.Members.RemoveAll(p => SamePath(p, path) || removedPaths.Contains(p));
                box.MemberReferences.RemoveAll(r => SamePath(r.Path, path)
                    || (identity is not null && r.Identity == identity));
            }
            if (!target.IsUnsorted) target.Members.Add(path);
            // Bind using the next background snapshot, not a potentially stale ID
            // from before the user explicitly assigned a replacement at this path.
            if (!DesktopItems.IsShellPath(path)) target.MemberReferences.Add(new() { Path = path, PendingAssignment = true });
        }
    }

    private static bool SamePath(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
