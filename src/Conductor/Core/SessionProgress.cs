using Conductor.Models;

namespace Conductor.Core;

/// <summary>SC4.3: the one answer to "did this session commit any work?".
///
/// <para>Two filters have to be applied together and they used to be applied nowhere together.
/// SC4.2 removed conductor's own <c>chore(conductor):</c> bookkeeping from the count, because
/// counting it scored a session green for the engine's own status writes. SC4.3 adds the commits
/// the session landed in the plan's <c>satelliteRepos</c>, because leaving them out scored a
/// delivered stage <c>NoProgress</c> twice (sk #3). Every consumer — the verdict, the workflow's
/// <c>hasCommits</c>, the circuit breaker, the stall detector — asks this, so the answer can no
/// longer differ between them.</para>
/// </summary>
public static class SessionProgress
{
    /// <summary>Every commit this session landed anywhere the plan declares, minus conductor's own
    /// bookkeeping. THE progress signal — never the raw <see cref="SessionRecord.NewCommits"/>,
    /// which stays untouched so report, status and history keep reporting what really landed here.</summary>
    public static List<string> WorkCommits(SessionRecord rec)
    {
        ArgumentNullException.ThrowIfNull(rec);
        return Git.ExcludeBookkeeping(rec.NewCommits.Concat(rec.SatelliteCommits));
    }

    /// <summary>True when the session committed real work in the primary repo or any satellite.</summary>
    public static bool HasWorkCommits(SessionRecord rec) => WorkCommits(rec).Count > 0;

    /// <summary>The last satellite commit as <c>&lt;sha&gt;@&lt;label&gt;</c>, for the checkpoint
    /// record's commit column when the delivery landed outside this repo. Null when there is none.
    /// The label is kept because the bare sha resolves in no repo the reader is standing in.</summary>
    public static string? LastSatelliteCommitRef(SessionRecord rec)
    {
        ArgumentNullException.ThrowIfNull(rec);
        var work = Git.ExcludeBookkeeping(rec.SatelliteCommits);
        if (work.Count == 0) return null;
        var row = work[^1];
        var sha = row.Split(' ')[0];
        var open = row.LastIndexOf('[');
        var label = open >= 0 && row.EndsWith(']') ? row[(open + 1)..^1] : "satellite";
        return $"{sha}@{label}";
    }
}
