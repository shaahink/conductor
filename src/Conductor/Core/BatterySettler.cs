using Conductor.Core.Store;

namespace Conductor.Core;

/// <summary>SC4.1: what the battery waited for, and what was still running when it stopped waiting.</summary>
/// <param name="Watched">Live bg children found when the settle began.</param>
/// <param name="StillAlive">How many were still running when waiting stopped — 0 means settled.</param>
/// <param name="Waited">Wall time spent waiting.</param>
/// <param name="Names">Labels of the children watched, in the order they were found.</param>
public sealed record BatterySettleOutcome(int Watched, int StillAlive, TimeSpan Waited, IReadOnlyList<string> Names)
{
    /// <summary>Nothing was running — the common case, and a free one.</summary>
    public static BatterySettleOutcome Nothing { get; } = new(0, 0, TimeSpan.Zero, []);
}

/// <summary>
/// SC4.1: the gate battery judges the WORK, not the session's own teardown.
///
/// devcontext #12: a battery started one second after the agent exited, failed on output the
/// session's own background build was still writing, and queued a paid fix session for a defect
/// that did not exist. The battery is the engine's independent verdict, so it has to be taken
/// against a settled tree — which means waiting for the children the session deliberately
/// backgrounded (<c>conductor bg start</c>, the MCP <c>bg_start</c> tool) to actually exit.
///
/// Bounded on purpose: a child that never exits (a dev server the agent left running) must delay
/// the verdict, not cancel it. When the cap runs out the battery starts anyway and says so — a
/// wait that could block a run forever would be a worse failure than the one it prevents.
/// </summary>
public static class BatterySettler
{
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The live bg children attributable to <paramref name="sessionNumber"/>. A row with no session
    /// recorded is included: an unattributed background child of this run is far more likely to be
    /// the session that just exited than nobody's. A row stamped with an EARLIER session is not —
    /// that is somebody's long-lived server, and holding every future battery for it would trade
    /// devcontext #12 for a permanent stall.
    /// </summary>
    public static IReadOnlyList<PidRow> LiveChildren(IRunStore? store, string? runId, int? sessionNumber)
    {
        if (store == null || string.IsNullOrEmpty(runId)) return [];
        try
        {
            return store.GetAllPids(runId)
                .Where(p => p.ExitedUtc == null)
                .Where(p => p.Purpose.StartsWith(StallDetector.BgPurposePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(p => p.SessionNumber == null || sessionNumber == null || p.SessionNumber == sessionNumber)
                .Where(p => PidLiveness.LooksAlive(p.Pid, p.StartedUtc))
                .ToList();
        }
        catch (InvalidOperationException) { return []; }
    }

    /// <summary>
    /// Hold until this session's bg children have exited, or until <paramref name="cap"/> elapses.
    /// Sweeps run.db on every pass, so a finished child's row is closed here rather than lingering
    /// as phantom liveness for `bg status` and the stall rail.
    /// </summary>
    /// <param name="cap">Ceiling on the wait. Zero or negative disables the settle entirely.</param>
    /// <param name="log">Line sink taking (message, outcome) — the engine's LogWithOutcome.</param>
    public static async Task<BatterySettleOutcome> SettleAsync(
        IRunStore? store, string? runId, int? sessionNumber, TimeSpan cap,
        Action<string, string?>? log = null, TimeSpan? poll = null, CancellationToken ct = default)
    {
        if (cap <= TimeSpan.Zero) return BatterySettleOutcome.Nothing;

        PidLiveness.Sweep(store, runId);
        var watching = LiveChildren(store, runId, sessionNumber);
        if (watching.Count == 0) return BatterySettleOutcome.Nothing;

        var names = watching.Select(Label).ToList();
        var startedUtc = DateTime.UtcNow;
        log?.Invoke($"battery settle: {watching.Count} bg child(ren) of this session still running — holding gates up to " +
                    $"{cap.TotalSeconds:0}s so the verdict judges the work, not the teardown: {string.Join(", ", names)}", null);

        var alive = watching;
        var interval = poll ?? DefaultPoll;
        while (alive.Count > 0 && DateTime.UtcNow - startedUtc < cap && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            PidLiveness.Sweep(store, runId);
            alive = LiveChildren(store, runId, sessionNumber);
        }

        var waited = DateTime.UtcNow - startedUtc;
        if (alive.Count == 0)
            log?.Invoke($"battery settle: {watching.Count} bg child(ren) exited after {waited.TotalSeconds:0.0}s — starting gates", "pass");
        else
            log?.Invoke($"battery settle: {alive.Count} bg child(ren) still running after the {cap.TotalSeconds:0}s cap — starting gates " +
                        $"anyway; a gate failure here may be theirs, not the work's: {string.Join(", ", alive.Select(Label))}", "warn");

        return new BatterySettleOutcome(watching.Count, alive.Count, waited, names);
    }

    private static string Label(PidRow p)
    {
        var purpose = p.Purpose.StartsWith(StallDetector.BgPurposePrefix, StringComparison.OrdinalIgnoreCase)
            ? p.Purpose[StallDetector.BgPurposePrefix.Length..]
            : p.Purpose;
        return $"{purpose} (pid {p.Pid})";
    }
}
