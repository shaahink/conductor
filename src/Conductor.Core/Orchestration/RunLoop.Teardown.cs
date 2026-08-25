namespace Conductor.Core.Orchestration;

public sealed partial class RunLoop
{
    /// <summary>Everything the loop owes whoever comes next, on EVERY way out of it — the normal
    /// close, `--once`, a cancel, an abort, and an exception. It is a method rather than a `finally`
    /// body because the list keeps growing and each addition has arrived the same way: something was
    /// lost on the one exit path nobody thought about.</summary>
    private void Teardown()
    {
        // DV2.4, bug #68 — the budget lands in the store on EVERY exit path, not just the ones
        // that remembered. PersistBudget() folds a session's cost and tokens into RunState in
        // memory; only Save() puts them where a restarting engine looks, and `--once` — the exit
        // the watch supervisor and every scripted rig take — returned without one. Measured: after
        // a --once exit run_state held cost and tokens as literal 0 while the live counters had
        // them, so limits.maxRunCostUsd was a per-PROCESS cap and a repeatedly restarted run could
        // spend without bound. Fixed at the funnel rather than at the `return`s that happened to
        // be missing it, because a new one arrives every era — the same argument as EnsureRunRow
        // (bug #27), and for the same reason: ordering is what was fragile.
        try { _ctx.Save(); }
        // An unwritable store must never be the reason teardown throws — but it must not be
        // silent either, because what it swallowed is the run's own spend.
        catch (Exception ex) { _ctx.Log($"budget not persisted at teardown: {ex.Message}", "warn"); }
        _ctx.DisposeTranscript();
        // KS9.2: the mirror owns an HttpClient. Attached at run start, DRAINED then released
        // here — a pass in flight at teardown must be allowed to finish, or a once-mode run
        // publishes a board it was halfway through writing.
        _ctx.DetachMirror(TimeSpan.FromSeconds(60));
        ReleaseLock();
    }
}
