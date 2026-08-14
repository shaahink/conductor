using Conductor.Core.History;

namespace Conductor.Core.Fleet;

/// <summary>
/// K3.2: the history half of the picker's envelope. The live half comes from probing ports; this
/// half comes from the state catalogue, which is the only place a finished run still exists.
/// <para>A run that is currently answering on a port is NOT a past run, even though the catalogue
/// knows it — it is already in the list above, and printing it twice would make the picker look like
/// it found two of something there is one of.</para>
/// <para><b>A row whose store this engine cannot open is listed too</b> (KS2.2). It used to be dropped
/// here — <c>row.Run</c> is null for exactly that case — which made the precise refusal KS2.2 built
/// (<c>that run's database is gone — nothing at &lt;path&gt;</c>) unreachable from the two surfaces
/// that matter: someone could only get it by typing the slug by hand. <c>conductor history</c> listed
/// the row all along, so the picker and the hub were the only places where a deleted database looked
/// like a run that had never existed. Such a row carries no run id, is selected by its slug, and says
/// what is wrong with it in <see cref="FacePastRun.Problem"/>.</para>
/// </summary>
public static class FacePastRuns
{
    /// <summary>The picker is a screen, not a report. Beyond this many the answer is
    /// <c>conductor history</c>, which pages and filters.</summary>
    public const int DefaultMax = 8;

    /// <summary>Reads the catalogue and returns the finished runs, newest first — and how many there
    /// were before the cap took its bite (KS2.4). A screen that shows eight of twenty-three and says
    /// only "8 past runs" is not listing this machine, it is describing its own first page; the count
    /// is what lets the picker say which of the two it is doing.</summary>
    /// <param name="root">State home root.</param>
    /// <param name="liveRunIds">Run ids already listed as live; excluded.</param>
    public static FacePastRunPage Read(
        string root, IEnumerable<string>? liveRunIds = null, int max = DefaultMax)
    {
        var live = new HashSet<string>(liveRunIds ?? [], StringComparer.OrdinalIgnoreCase);
        var past = new List<FacePastRun>();
        var total = 0;
        foreach (var row in RunHistory.List(root))
        {
            if (row.Run is not { } run)
            {
                total++;
                if (past.Count < max) past.Add(Unreadable(row));
                continue;
            }
            if (live.Contains(run.RunId)) continue;
            total++;
            // Counting continues past the cap on purpose: the loop no longer BREAKS at `max`, because
            // the number the picker has to disclose is the number of rows there are, and a loop that
            // stops at eight can only ever report eight.
            if (past.Count >= max) continue;
            var (done, checkpoints) = RunHistory.CheckpointCounts(row);
            // KS1.3: the reconciled word. The picker's whole job is to tell a live run from a past
            // one, and it was labelling engines that died months ago `running` because the column
            // said so — the one place that lie is most likely to be acted on.
            past.Add(new FacePastRun(
                run.Repo, string.IsNullOrEmpty(run.PlanName) ? row.Plan : run.PlanName,
                run.RunId, row.Status, done, checkpoints, run.CostUsd, run.LastActivityUtc, row.RunDbPath)
            {
                Selector = run.RunId,
            });
        }
        return new FacePastRunPage(past, total);
    }

    /// <summary>A catalogue row whose database did not open. No id, no progress, no money — none of
    /// that was readable — but a repo, a plan, the path, the catalogue's own last-seen stamp, and the
    /// sentence that says which kind of broken this is. <see cref="ArchiveView.Describe"/> writes that
    /// sentence for both this listing and the attach's refusal, so they cannot disagree.</summary>
    private static FacePastRun Unreadable(RunHistoryRow row) => new(
        row.Repo, row.Plan, RunId: "", row.Status, Done: 0, Total: 0, CostUsd: 0m,
        row.LastSeenFallback.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        row.RunDbPath)
    {
        // The slug is what such a row can be named by, and it is the selector `face --archive` and
        // `conductor history` both already accept for one.
        Selector = row.Slug,
        Problem = ArchiveView.Describe(row.RunDbPath, row.Problem),
    };
}

/// <summary>KS2.4 — one page of remembered runs, and the size of the thing it is a page of.
/// <para><see cref="Rows"/> is what fits a screen; <see cref="Total"/> is how many the catalogue holds
/// for the same question. They differ exactly when the cap bit, and the picker says so
/// (<c>showing 8 of 23 · conductor history for the rest</c>) instead of quietly presenting a page as
/// the whole. A list that is short because there is nothing more and a list that is short because it
/// stopped counting are different facts, and only one of them means "that run is not on this
/// machine".</para></summary>
public sealed record FacePastRunPage(IReadOnlyList<FacePastRun> Rows, int Total)
{
    /// <summary>Nothing remembered — the answer on a machine that has never finished a run.</summary>
    public static FacePastRunPage Empty { get; } = new([], 0);

    /// <summary>Did the cap bite? True means the picker must disclose the rest.</summary>
    public bool Truncated => Total > Rows.Count;
}
