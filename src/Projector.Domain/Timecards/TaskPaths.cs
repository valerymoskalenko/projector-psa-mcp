namespace Projector.Domain.Timecards;

/// <summary>
/// Builds readable task paths ("User Story 101 > Analysis & Design") from Projector's parent task UIDs.
/// Task names repeat within a project (many "Development" tasks under different parents), so the path is what
/// tells them apart. Projector returns the whole task tree, closed parents included, in the same list.
/// </summary>
public static class TaskPaths
{
    public const string Separator = " > ";

    /// <summary>Guards against a bad parent chain; Projector trees seen so far are 3 levels deep.</summary>
    private const int MaxDepth = 12;

    /// <summary>Path per task UID. A task whose parent is not in the list starts its path at itself.</summary>
    public static IReadOnlyDictionary<string, string> Build(IEnumerable<(string Uid, string? Name, string? ParentUid)> tasks)
    {
        var byUid = new Dictionary<string, (string? Name, string? ParentUid)>(StringComparer.Ordinal);
        foreach (var (uid, name, parentUid) in tasks)
        {
            byUid[uid] = (name?.Trim(), string.IsNullOrWhiteSpace(parentUid) ? null : parentUid.Trim());
        }

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var uid in byUid.Keys)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = uid;
            while (current is not null && names.Count < MaxDepth && seen.Add(current)
                && byUid.TryGetValue(current, out var node))
            {
                names.Add(string.IsNullOrEmpty(node.Name) ? "?" : node.Name);
                current = node.ParentUid;
            }

            names.Reverse();
            paths[uid] = string.Join(Separator, names);
        }

        return paths;
    }
}
