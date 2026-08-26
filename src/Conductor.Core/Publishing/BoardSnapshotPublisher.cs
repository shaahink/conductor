using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.Http;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Publishing;

/// <summary>
/// DV6.3 — the board page, made at a boundary and written where the run keeps its own files.
///
/// <para><b>Composed, not queried.</b> Every field comes from a projection that already exists and
/// is already pinned by tests: <see cref="ControlPlaneMapper.FromSnapshot"/> +
/// <see cref="ControlPlaneMapper.WithBudget"/> for the run, <see cref="TaskGraph"/> for the cards,
/// <see cref="OwnerQueue.Collect"/> for the obligations, <see cref="EvidenceRegistry"/> for the
/// proof and <see cref="LedgerSummary"/> for the bugs. This class adds no arithmetic of its own —
/// it is the assembly, and the page is the render.</para>
///
/// <para><b>Best effort, and silent about nothing.</b> A boundary must not fail because a page could
/// not be written, so the composition catches what a store or a disk can throw and returns null —
/// but the caller LOGS that, because a page that stopped being produced two days ago and said
/// nothing is exactly the failure DV1.1 exists to prevent.</para>
/// </summary>
public static class BoardSnapshotPublisher
{
    /// <summary>How many artifacts the page lists. The whole registry would put a hundred paths
    /// between the board and the footer on a phone; the run's own <c>/evidence</c> answers the rest.</summary>
    public const int EvidenceRows = 20;

    /// <summary>The five contracts, folded into the page's model.</summary>
    public static BoardSnapshot Compose(PlanConfig plan, RunState state, TrackerSnapshot track,
        DashboardSnapshot dash, IRunStore? store, string boundary, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);

        var dto = ControlPlaneMapper.WithBudget(
            ControlPlaneMapper.FromSnapshot(dash, state.RunId, plan.Repo, plan.PlanDir,
                state.MaxSessionTokensThisRun, plan.Tracker, plan.StateDir),
            plan.Limits, state);

        IReadOnlyList<ConductorEvent> events = [];
        try { events = store?.ReadAllEvents(state.RunId) ?? []; }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* a page without cards still states the run */ }

        var graph = new TaskGraph();
        graph.Fold(events);
        var tasks = ControlPlaneMapper.FromTasks(
            [.. graph.Tasks.Where(t => !string.Equals(t.Status, "archived", StringComparison.Ordinal))]);

        var owner = OwnerQueueDto.From(OwnerQueue.Collect(plan, state, track, nowUtc), nowUtc);
        var evidence = EvidenceRegistry.From(events).Latest(EvidenceRows)
            .Select(ControlPlaneMapper.FromArtifact).ToList();

        return new BoardSnapshot(dto, tasks, owner, evidence,
            LedgerSummary.Line(store, plan.StateDir), boundary, nowUtc);
    }

    /// <summary>Where the page lives between boundaries: one file, overwritten, beside the run's
    /// other generated records. Atomic, because the courier may be reading it to attach it at the
    /// moment the next boundary rewrites it.</summary>
    public static string PathFor(PlanConfig plan) =>
        Path.Combine(plan?.StateDir ?? ".", BoardSnapshotHtml.FileName);

    /// <summary>What a boundary got: the file, and the model it was rendered from — so the caption
    /// that rides the file is composed from the same board and cannot state a different one.</summary>
    public sealed record PublishedBoard(string Path, BoardSnapshot Snapshot);

    /// <summary>Compose, render, write. Returns the page and its model, or null with the reason in
    /// <paramref name="refusal"/> — never a half-written file and never an exception into the
    /// boundary.</summary>
    public static PublishedBoard? Publish(PlanConfig plan, RunState state, TrackerSnapshot track,
        DashboardSnapshot dash, IRunStore? store, string boundary, DateTime nowUtc, out string refusal)
    {
        refusal = "";
        try
        {
            var snap = Compose(plan, state, track, dash, store, boundary, nowUtc);
            var path = PathFor(plan);
            AtomicFile.Write(path, BoardSnapshotHtml.Render(snap));
            return new PublishedBoard(path, snap);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            refusal = ex.Message;
            return null;
        }
    }
}
