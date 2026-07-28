using System.Collections.Concurrent;
using Conductor.Models;

namespace Conductor.Core.Lanes;

/// <summary>
/// Tracks path claims across concurrent lanes to prevent file-level collisions.
/// When two lanes claim overlapping paths, the second is serialized behind the first.
/// Uses declared path claims from StageConfig.PathClaims (M3.3).
/// </summary>
public sealed class PathClaimTracker
{
    private readonly ConcurrentDictionary<string, string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>Normalize a repo-relative path for comparison.</summary>
    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').Trim('/').ToLowerInvariant();
    }

    /// <summary>Try to claim a set of paths for a stage. Returns true if the claim
    /// succeeds (no conflicts), false if any path is already claimed by another stage.</summary>
    public bool TryClaim(string stageId, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return true;

        lock (_lock)
        {
            var conflicts = new List<string>();
            foreach (var p in paths)
            {
                var norm = Normalize(p);
                if (_claimed.TryGetValue(norm, out var owner) && !owner.Equals(stageId, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add($"{p} (claimed by {owner})");
            }

            if (conflicts.Count > 0)
                return false;

            foreach (var p in paths)
                _claimed[Normalize(p)] = stageId;

            return true;
        }
    }

    /// <summary>Release all path claims for a stage.</summary>
    public void Release(string stageId)
    {
        lock (_lock)
        {
            var keys = _claimed.Where(kv => kv.Value.Equals(stageId, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
            foreach (var key in keys)
                _claimed.TryRemove(key, out _);
        }
    }

    /// <summary>Check if ANY of the given paths are currently claimed.</summary>
    public bool HasConflict(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        lock (_lock)
        {
            foreach (var p in paths)
            {
                if (_claimed.ContainsKey(Normalize(p)))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Current number of claimed paths.</summary>
    public int Count { get { lock (_lock) { return _claimed.Count; } } }
}
