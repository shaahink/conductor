namespace Conductor.Core.History;

/// <summary>
/// KS1.3 — the shaping of <c>conductor history --json</c>, moved off the command so the promise made
/// to whoever parses it can be asserted without a console.
///
/// <para><b>The bug it exists to end.</b> The command emitted a <see cref="RunHistoryItemJson"/> with
/// an EMPTY run id for every catalogue entry the archive would not open: a run-shaped object naming
/// no run, sitting in <c>runs[]</c> beside real ones. Six of them collided on that empty key in a
/// downstream harvest, which then refused the whole payload — the numbers are in
/// <c>.conductor/evidence/KS0/ks0-1-catalogue-repair.md</c>. An entry that is not a run does not
/// belong in the array of runs; it belongs in its own, saying which of the two things went wrong.</para>
///
/// <para><b>Additive only.</b> <see cref="RunHistoryItemJson"/> is a published contract. Nothing here
/// renames or reorders a field: <c>status</c> keeps its name and gets sharper — it is the reconciled
/// word now, the one an operator should believe — and the raw column arrives beside it as
/// <c>storedStatus</c>, so a consumer written against the old shape reads what it always read and a
/// new one can still see what the row literally says.</para>
/// </summary>
public static class RunHistoryPayload
{
    /// <summary>The catalogue names a path with nothing at it.</summary>
    public const string ReasonMissing = "missing";

    /// <summary>A file is there and it is not a conductor run database.</summary>
    public const string ReasonNotARunDatabase = "not-a-run-database";

    /// <summary>The whole listing payload: the runs, and separately the catalogue entries that are not
    /// runs. Both arrays are always present, so a consumer never has to tell "no bad entries" from
    /// "this engine does not report them".</summary>
    public static RunHistoryListJson List(IEnumerable<RunHistoryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var runs = new List<RunHistoryItemJson>();
        var unreadable = new List<UnreadableEntryJson>();
        foreach (var row in rows)
        {
            if (row.Run is null) unreadable.Add(Unreadable(row));
            else runs.Add(Item(row));
        }
        return new RunHistoryListJson(runs, unreadable);
    }

    /// <summary>One readable run. Throws on an unreadable row rather than inventing a blank id for
    /// it — that invention is the defect this checkpoint closes, and a quiet fallback here would let
    /// it back in through the next caller.</summary>
    public static RunHistoryItemJson Item(RunHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var run = row.Run
            ?? throw new ArgumentException(
                "an unreadable catalogue entry has no run to shape - report it through Unreadable()", nameof(row));
        var (done, total) = RunHistory.CheckpointCounts(row);
        return new RunHistoryItemJson(
            run.RunId, run.Repo, string.IsNullOrEmpty(run.PlanName) ? row.Plan : run.PlanName,
            row.Status, run.EngineStampText, run.Branch,
            run.StartedUtc, run.EndedUtc, run.LastActivityUtc,
            run.Sessions, done, total, run.CostUsd, run.Tokens,
            row.RunDbPath, row.Slug, row.ImportedFrom, Readable: true,
            run.EngineCommit, run.EngineDirty, run.Limits,
            run.LimitsAtLaunch, run.LimitsReloads, run.LimitsReloadedUtc,
            run.Status, row.StoreLooksLive);
    }

    /// <summary>One catalogue entry that did not open, reported as what it is.</summary>
    public static UnreadableEntryJson Unreadable(RunHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new UnreadableEntryJson(
            row.RunDbPath, row.Repo, row.Plan, row.Slug, Reason(row.Problem), row.ImportedFrom);
    }

    /// <summary>The two words, and they are two on purpose: "that run's file was deleted" is a loss,
    /// "that path is not a run database" is a wrong index entry, and the repair for them differs.</summary>
    public static string Reason(RunDbProblem problem) => problem switch
    {
        RunDbProblem.NotARunDatabase => ReasonNotARunDatabase,
        _ => ReasonMissing,
    };
}

/// <summary>A catalogue entry that is not a run. Deliberately NOT run-shaped: no id, no status, no
/// cost — there is no run here to have any of those, and a shape that pretended otherwise is what
/// put six blank-id rows into a consumer's parser.</summary>
/// <param name="Reason">One of <see cref="RunHistoryPayload.ReasonMissing"/> or
/// <see cref="RunHistoryPayload.ReasonNotARunDatabase"/>.</param>
public sealed record UnreadableEntryJson(
    string RunDb, string Repo, string Plan, string Slug, string Reason, string? ImportedFrom);
