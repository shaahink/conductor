using Conductor.Core.History;

namespace Conductor.Core.Fleet;

/// <summary>
/// K3.2: the history half of the picker's envelope. The live half comes from probing ports; this
/// half comes from the state catalogue, which is the only place a finished run still exists.
/// <para>A run that is currently answering on a port is NOT a past run, even though the catalogue
/// knows it — it is already in the list above, and printing it twice would make the picker look like
/// it found two of something there is one of.</para>
/// </summary>
public static class FacePastRuns
{
    /// <summary>The picker is a screen, not a report. Beyond this many the answer is
    /// <c>conductor history</c>, which pages and filters.</summary>
    public const int DefaultMax = 8;

    /// <summary>Reads the catalogue and returns the finished runs, newest first.</summary>
    /// <param name="root">State home root.</param>
    /// <param name="liveRunIds">Run ids already listed as live; excluded.</param>
    public static IReadOnlyList<FacePastRun> Read(
        string root, IEnumerable<string>? liveRunIds = null, int max = DefaultMax)
    {
        var live = new HashSet<string>(liveRunIds ?? [], StringComparer.OrdinalIgnoreCase);
        var past = new List<FacePastRun>();
        foreach (var row in RunHistory.List(root))
        {
            if (past.Count >= max) break;
            if (row.Run is not { } run || live.Contains(run.RunId)) continue;
            var (done, total) = RunHistory.CheckpointCounts(row);
            // KS1.3: the reconciled word. The picker's whole job is to tell a live run from a past
            // one, and it was labelling engines that died months ago `running` because the column
            // said so — the one place that lie is most likely to be acted on.
            past.Add(new FacePastRun(
                run.Repo, string.IsNullOrEmpty(run.PlanName) ? row.Plan : run.PlanName,
                run.RunId, row.Status, done, total, run.CostUsd, run.LastActivityUtc, row.RunDbPath));
        }
        return past;
    }
}
