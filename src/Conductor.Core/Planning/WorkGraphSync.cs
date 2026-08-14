using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;

using StoreRow = Conductor.Core.Store.CheckpointRow;

namespace Conductor.Core.Planning;

/// <summary>
/// W1.2: THE plan → work-graph sync, invoked at every boundary where declared work can change —
/// run start, the G3.2 live reload, /plan/edit and /plan/import apply, and `plan add-stage`.
/// Declared sources (tracker markdown, inline progress.checkpoints, script) all normalize through
/// <see cref="IProgressProvider"/> into one declared-work list; the graph is upserted from it with
/// provenance, never clobbering runtime status:
/// <list type="bullet">
/// <item>new declared items land with their full declared state (add);</item>
/// <item>existing items refresh their declared title only;</item>
/// <item>items whose declaration disappeared are retired as <c>archived</c> — status history kept,
/// never deleted — but only when the retirement is unambiguous (their stage left the plan, or the
/// declared source re-declared their stage without them);</item>
/// <item>a re-declared archived item revives with its declared status;</item>
/// <item>a plan stage with no work item at all gets ONE scaffolded checkpoint (`{stage}.1`), so a
/// stage added mid-run is schedulable and visible on the board immediately (gap G13's harm — a
/// zero-item stage parking the run mid-flight — is structurally gone).</item>
/// </list>
/// When anything changed, the tracker view regenerates (callers opt out where the tracker was the
/// input moments ago). Pure orchestration over the W1.1 store adapters — no SQL, no new stores.
/// <para>KS3.4 round 5: the decisions live in ONE private function (<see cref="Decide"/>) with two
/// renderers — <see cref="Sync"/> executes them against the store, <see cref="ProjectView"/> answers
/// what the store's checkpoint view WILL read after that execution, without writing. The loop runs
/// this sync before its first work read, so any surface that models a launch from a store it may not
/// write (<c>preflight</c>, the dry-run loop) must read the projection, never the graph as it lies
/// at rest and never the declaration alone.</para>
/// </summary>
public static class WorkGraphSync
{
    public sealed record SyncResult(int Added, int TitlesRefreshed, int Scaffolded, int Archived, int Revived)
    {
        public static readonly SyncResult Empty = new(0, 0, 0, 0, 0);
        public bool Changed => Added + TitlesRefreshed + Scaffolded + Archived + Revived > 0;

        public string Summary =>
            $"{Added} added · {TitlesRefreshed} titles · {Scaffolded} scaffolded · {Archived} archived · {Revived} revived";
    }

    public static SyncResult Sync(PlanConfig plan, IRunStore store, string runId,
        Action<string>? log = null, bool regenerateTracker = true)
    {
        // 1 · the declared-work list, whatever shape the plan keeps it in.
        TrackerSnapshot declared;
        string provenance;
        try
        {
            var provider = ProgressProviderFactory.Create(plan);
            provenance = provider.Name == "markdown-table" ? "tracker" : "plan";
            declared = provider.Read(plan);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            log?.Invoke($"work-graph sync skipped — declared work unreadable ({ex.Message}); graph left untouched");
            return SyncResult.Empty;
        }

        // 2 · the graph as it stands, and the ONE decision over it — the same function preflight's
        // projection reads, so the drill and this sync cannot disagree about what changes. All
        // decisions are taken against THIS fold, then executed — the store adapters are idempotent,
        // so decision/execution order cannot double-apply.
        var graph = new TaskGraph();
        graph.Fold(store.ReadAllEvents(runId));
        var delta = Decide(plan, declared, graph);

        // 3 · execute: adds + title refreshes ride the W1.1 seed adapter (add / refresh-title /
        // never-touch-status is exactly its contract).
        if (delta.Added.Count + delta.TitleRefreshes.Count > 0)
        {
            store.SeedCheckpoints(runId, declared.Checkpoints.Select(c =>
                (c.Id, c.StageId, c.Title, DeclaredStatusLabel(c), c.Commit, c.Evidence)));
        }

        foreach (var row in delta.Revives)
            store.UpdateCheckpoint(runId, row.Id, DeclaredStatusLabel(row), row.Commit, row.Evidence, source: provenance);

        foreach (var item in delta.Archives)
            store.UpdateCheckpoint(runId, item.TaskId, "ARCHIVED", "-", "-", source: provenance);

        foreach (var (stage, id) in delta.Scaffolds)
        {
            if (graph.Find(id) is { Status: "archived" })
                store.UpdateCheckpoint(runId, id, "TODO", "-", "-", source: "plan");
            else
                store.SeedCheckpoints(runId, [(id, stage.Id, stage.Title, "TODO", "-", "-")]);
        }

        var result = new SyncResult(delta.Added.Count, delta.TitleRefreshes.Count,
            delta.Scaffolds.Count, delta.Archives.Count, delta.Revives.Count);
        if (result.Changed)
        {
            log?.Invoke($"work-graph sync: {result.Summary}");

            // 4 · the tracker is a generated view of the graph — refresh it so the ENGINE's
            // schedule (still tracker-fed until W1.3/W1.4) sees the change without a restart.
            if (regenerateTracker)
            {
                try { TrackerGenerator.Write(plan, store, runId, declared.HandoffBlock); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"work-graph sync: tracker regeneration failed ({ex.Message}) — view catches up at session end");
                }
            }
        }
        return result;
    }

    /// <summary>KS3.4 round 5 — the checkpoint view the store will serve AFTER <see cref="Sync"/>
    /// runs against this declaration and this graph, computed WITHOUT writing: the declared row set
    /// carrying the graph's status wherever the graph knows the id, the declared status where it
    /// does not (adds, revives), minus the retired rows, plus the zero-item-stage scaffolds.
    /// <para>This exists because the run loop MUTATES its scheduling input before it reads it —
    /// <c>RunLoop.RunAsync</c> syncs the declared plan into the graph before its first
    /// <c>ReadWork()</c> — so a drill that read the graph at rest saw neither the loop's input nor
    /// the declaration: every checkpoint declared since the last session was invisible to it, and
    /// every retired one still visible. Rows are shaped and ordered exactly as
    /// <c>SqliteRunStore.GetCheckpoints</c> serves them (stage, then id, ordinal; archived rows out
    /// of view), and every rule here — the title-refresh gate, the revive transition the fold would
    /// refuse, the commit kept when the declaration says "-" — mirrors the seed/update adapters the
    /// executing half drives, through the same <see cref="Decide"/> call.</para></summary>
    public static IReadOnlyList<StoreRow> ProjectView(PlanConfig plan, TrackerSnapshot declared, TaskGraph graph)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(graph);
        var delta = Decide(plan, declared, graph);

        // SeedCheckpoints runs only when something is new or renamed — and it is the only writer
        // that refreshes titles, so an unaccompanied revive keeps its old title. Mirrored, not fixed.
        var seedRuns = delta.Added.Count + delta.TitleRefreshes.Count > 0;
        var archivedIds = new HashSet<string>(delta.Archives.Select(a => a.TaskId), StringComparer.Ordinal);
        var revivesById = new Dictionary<string, CheckpointRow>(StringComparer.Ordinal);
        foreach (var row in delta.Revives) revivesById.TryAdd(row.Id, row);
        var declaredById = new Dictionary<string, CheckpointRow>(StringComparer.Ordinal);
        foreach (var row in declared.Checkpoints) declaredById.TryAdd(row.Id, row);

        var view = new List<StoreRow>();
        foreach (var item in graph.Checkpoints())
        {
            if (item.Status == "archived")
            {
                // Revived when re-declared — with the declared status, unless the fold's transition
                // table refuses it (archived → skipped is not a move), in which case the item stays
                // archived and out of view, exactly as the executed UpdateCheckpoint would land.
                if (!revivesById.TryGetValue(item.TaskId, out var row)) continue;
                var to = DeclaredGraphStatus(row);
                if (!TaskGraph.IsValidTransition("archived", to)) continue;
                view.Add(new StoreRow(item.TaskId, item.StageId, RefreshedTitle(item, seedRuns, declaredById),
                    TaskWrites.Label(to), Keep(row.Commit, item.Commit), Keep(row.Evidence, item.Evidence),
                    item.Confirmed));
                continue;
            }
            if (archivedIds.Contains(item.TaskId)) continue;
            view.Add(new StoreRow(item.TaskId, item.StageId, RefreshedTitle(item, seedRuns, declaredById),
                TaskWrites.Label(item.Status), item.Commit, item.Evidence, item.Confirmed));
        }

        foreach (var row in delta.Added)
        {
            // A new item lands todo and only a non-TODO declaration re-states it — so a TODO add
            // keeps the fold's own "-" placeholders whatever the tracker cell said.
            var status = DeclaredGraphStatus(row);
            view.Add(new StoreRow(row.Id, row.StageId, row.Title, TaskWrites.Label(status),
                status == "todo" ? "-" : Keep(row.Commit, "-"),
                status == "todo" ? "-" : Keep(row.Evidence, "-"),
                Confirmed: false));
        }

        foreach (var (stage, id) in delta.Scaffolds)
        {
            view.Add(graph.Find(id) is { Status: "archived" } buried
                ? new StoreRow(id, buried.StageId, buried.Title, TaskWrites.Label("todo"),
                    buried.Commit, buried.Evidence, buried.Confirmed)
                : new StoreRow(id, stage.Id, stage.Title, TaskWrites.Label("todo"), "-", "-", Confirmed: false));
        }

        return [.. view.OrderBy(r => r.StageId, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    /// <summary>The checkpoint view with NO sync modelled — what <c>GetCheckpoints</c> serves from
    /// this fold as it lies. The degradation partner of <see cref="ProjectView"/>: when the declared
    /// source cannot be read, <see cref="Sync"/> skips and the loop schedules on the graph alone.</summary>
    public static IReadOnlyList<StoreRow> GraphRows(TaskGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return [.. graph.Checkpoints().Where(t => t.Status != "archived")
            .Select(t => new StoreRow(t.TaskId, t.StageId, t.Title, TaskWrites.Label(t.Status),
                t.Commit, t.Evidence, t.Confirmed))];
    }

    /// <summary>What this sync would do, as data — one decision read by both halves.</summary>
    private sealed record Delta(
        IReadOnlyList<CheckpointRow> Added,
        IReadOnlyList<CheckpointRow> TitleRefreshes,
        IReadOnlyList<CheckpointRow> Revives,
        IReadOnlyList<TaskItem> Archives,
        IReadOnlyList<(StageConfig Stage, string Id)> Scaffolds);

    private static Delta Decide(PlanConfig plan, TrackerSnapshot declared, TaskGraph graph)
    {
        var checkpoints = graph.Checkpoints();
        var declaredIds = new HashSet<string>(declared.Checkpoints.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var declaredStages = new HashSet<string>(declared.Checkpoints.Select(c => c.StageId), StringComparer.OrdinalIgnoreCase);
        var planStages = new HashSet<string>(plan.Stages.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        var added = new List<CheckpointRow>();
        var titles = new List<CheckpointRow>();
        var revives = new List<CheckpointRow>();
        foreach (var row in declared.Checkpoints)
        {
            var existing = graph.Find(row.Id);
            if (existing == null) added.Add(row);
            else if (existing.Status == "archived") revives.Add(row);
            else if (!existing.Title.Equals(row.Title, StringComparison.Ordinal)) titles.Add(row);
        }

        // Safety rail: an empty declared list against a populated graph is far more likely a torn
        // tracker read (or a mid-rewrite file) than a deliberate "retire everything" — never archive
        // on that signal alone.
        var archives = declared.Checkpoints.Count == 0
            ? []
            : checkpoints.Where(item =>
                    item.Status != "archived"
                    && !declaredIds.Contains(item.TaskId)
                    && (!planStages.Contains(item.StageId) || declaredStages.Contains(item.StageId)))
                .ToList();

        // A stage with neither declared nor live graph work gets one scaffolded checkpoint.
        var scaffolds = plan.Stages.Where(s =>
                !declaredStages.Contains(s.Id)
                && !checkpoints.Any(t => t.Status != "archived" && t.StageId.Equals(s.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(s => (Stage: s, Id: ScaffoldId(s, graph, plan.Conventions)))
            .ToList();

        return new Delta(added, titles, revives, archives, scaffolds);
    }

    /// <summary>`{stage}.N` when it round-trips through the plan's stage-id conventions (a tracker
    /// re-parse must land the row back in the same stage), else the stage id itself; suffixed past
    /// any non-archived collision.</summary>
    private static string ScaffoldId(StageConfig stage, TaskGraph graph, ProgressConventions conventions)
    {
        for (var n = 1; n < 100; n++)
        {
            var candidate = $"{stage.Id}.{n}";
            if (!conventions.DeriveStageId(candidate).Equals(stage.Id, StringComparison.OrdinalIgnoreCase)) break;
            var item = graph.Find(candidate);
            if (item == null || item.Status == "archived") return candidate;
        }
        return stage.Id;
    }

    private static string DeclaredStatusLabel(CheckpointRow row) =>
        row.IsDone ? "DONE" : row.IsInProgress ? "IN PROGRESS" : row.IsBlocked ? "BLOCKED"
        : row.IsSkipped ? "SKIPPED" : "TODO";

    /// <summary>The declared status in the graph's vocabulary — by construction the same word the
    /// executing half's <c>DeclaredStatusLabel</c> → <c>GraphStatus</c> round trip lands on.</summary>
    private static string DeclaredGraphStatus(CheckpointRow row) =>
        row.IsDone ? "done" : row.IsInProgress ? "in_progress" : row.IsBlocked ? "blocked"
        : row.IsSkipped ? "skipped" : "todo";

    /// <summary>The declared title, but only when the seed adapter actually runs this sync — the
    /// title-refresh write rides <c>SeedCheckpoints</c>, which nothing invokes on a sync that adds
    /// and renames nothing.</summary>
    private static string RefreshedTitle(TaskItem item, bool seedRuns, Dictionary<string, CheckpointRow> declaredById)
        => seedRuns && declaredById.TryGetValue(item.TaskId, out var d) && !string.IsNullOrWhiteSpace(d.Title)
            ? d.Title : item.Title;

    /// <summary>The fold keeps the last non-empty attribution: a "-" (or blank) declared cell means
    /// "nothing to record" and never blanks what the graph already knows.</summary>
    private static string Keep(string declared, string existing)
        => string.IsNullOrWhiteSpace(declared) || declared == "-" ? existing : declared;
}
