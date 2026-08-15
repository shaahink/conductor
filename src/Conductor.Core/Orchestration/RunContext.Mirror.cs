using Conductor.Core.Integrations.Github;

namespace Conductor.Core.Orchestration;

/// <summary>
/// KS9.2 — the run context's half of the live GitHub mirror: attach it, poke it at a boundary, drain
/// it at the end.
/// </summary>
/// <remarks>
/// Split out of <c>RunContext.cs</c> when that file crossed the 500-line ceiling (the architecture
/// ratchet's baseline is empty, so there is no debt entry to hide behind). The seam is the honest one:
/// everything here is about a destination OUTSIDE the run — an optional, off-by-default, one-way
/// mirror — whereas the rest of the context is the run's own state, budget and logging. Nothing in
/// here is read by the run loop's decisions; that is the whole design (D-7, ADR 0005).
/// </remarks>
public sealed partial class RunContext
{
    /// <summary>KS9.2 — the live GitHub mirror, or null when the plan has not opted in (which is the
    /// default, and is also what every plan written before KS9 gets). Deliberately NOT a constructor
    /// parameter and deliberately NOT an <c>IEventSink</c>: it is attached once at run start by
    /// <c>RunLoop</c>, it is a reconciler the boundaries poke, and a null one is simply never poked —
    /// so a plan without a <c>github</c> block runs the code path it ran before this existed.</summary>
    public GithubMirror? Mirror { get; private set; }

    /// <summary>Attach (or replace, on a plan reload) the run's mirror. Disposing the old one is the
    /// caller's business only at shutdown; a reload swaps the destination and the old client must not
    /// outlive it, so this disposes what it replaces.</summary>
    public void AttachMirror(GithubMirror? mirror)
    {
        if (ReferenceEquals(Mirror, mirror)) return;
        Mirror?.Dispose();
        Mirror = mirror;
    }

    /// <summary>Poke the mirror at a boundary. Null-safe, non-blocking and incapable of throwing —
    /// the three properties that let this be called from the verdict path without the verdict path
    /// caring whether GitHub exists, is configured, or is up.</summary>
    /// <remarks>The discard is the same SHAPE as the <c>Notify</c> idiom next door and not the same
    /// thing: that one drops faults on the floor, whereas <c>ReconcileAsync</c> is total — it catches
    /// its own exceptions, logs one line and holds the cursor, so there is provably no fault here to
    /// observe. The alternative, awaiting a network call on the verdict path, is the back-pressure
    /// this whole design exists to avoid.</remarks>
    public void MirrorBoard(string reason, string? runStatus = null) => _ = Mirror?.Fire(reason, runStatus);

    /// <summary>Shutdown: wait for whatever pass is in flight, then release the mirror. MEASURED on
    /// the live rig — a run in once-mode returns from the loop the instant its session ends, and
    /// releasing without draining disposed the HttpClient mid-pass, leaving one issue of a three-card
    /// board on GitHub and a cancellation where a real error belonged.</summary>
    public void DetachMirror(TimeSpan budget)
    {
        if (Mirror is not { } mirror) return;
        try { mirror.DrainAsync(budget).Wait(budget + TimeSpan.FromSeconds(5)); }
        catch (AggregateException ex) { Log($"github mirror: drain failed — {ex.InnerException?.Message ?? ex.Message}"); }
        AttachMirror(null);
    }

    /// <summary>The LAST pass, at run completion, waited for under a budget. Every other boundary is
    /// fire-and-forget because another one is coming; this one has nothing behind it, and a process
    /// that exited while the closing pass was in flight would leave the diary issue open on a run
    /// that had finished. The budget is a ceiling, not a promise: it expires, the run still ends, and
    /// the next process's run-start pass catches the board up.</summary>
    public void MirrorFinalPass(string reason, string runStatus, TimeSpan budget)
    {
        if (Mirror is not { } mirror) return;
        try
        {
            if (!mirror.Fire(reason, runStatus).Wait(budget))
                Log($"github mirror: closing pass did not finish within {budget.TotalSeconds:0}s — the board will catch up on the next run");
        }
        catch (AggregateException ex)
        {
            Log($"github mirror: closing pass failed — {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
