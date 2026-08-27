namespace Conductor.Core.Release;

/// <summary>The courier state as the SCHEDULER and the filesystem report it. Nothing here dials
/// Telegram: one <c>getUpdates</c> consumer per token, and the live courier owns it (trap 4).</summary>
public sealed record CourierFacts(
    bool TokenSet,
    string? PersistedScope,
    bool TaskRegistered,
    string? SchedulerState,
    bool Running,
    int? Pid,
    int Chats,
    int Projects,
    bool RepoAllowed);

/// <summary>One run in the store and how much of it GitHub has been told.
/// <para>KS1.3's rule applies here as everywhere: <paramref name="Status"/> is the RECONCILED word
/// (<c>RunLiveness.Reconcile</c>), not <c>runs.status</c> raw, and <paramref name="StoredStatus"/>
/// keeps the row's own claim beside it. An engine that was killed never got to correct its row, so
/// four runs on this machine say <c>running</c> for ever — and a backfill line that believed the
/// column would refuse to name any of them as owed a record.</para></summary>
/// <param name="InFlight">Is something still DOING work from this run — <c>RunLiveness.IsStillGoing</c>,
/// asked by the probe rather than reconstructed here from a list of status words. A list would rot:
/// the park vocabulary has grown twice already, and a verdict that named <c>running</c> and
/// <c>paused</c> would silently start calling the next park word "finished" and demand a backfill of
/// a run that has not ended.</param>
public sealed record MirroredRun(string RunId, string PlanName, string Status, int MirroredIssues, bool InFlight)
{
    /// <summary>What the row itself claims, when that differs from the reconciled word.</summary>
    public string? StoredStatus { get; init; }
}

/// <summary><paramref name="CurrentRunId"/> is the run doing the asking, if any — its own backfill
/// is the closing act and cannot be owed before it ends.</summary>
public sealed record BackfillFacts(
    string? Repo,
    IReadOnlyList<MirroredRun> Runs,
    string? CurrentRunId);
