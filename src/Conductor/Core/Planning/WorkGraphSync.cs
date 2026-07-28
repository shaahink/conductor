using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;

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

        // 2 · the graph as it stands. All decisions are taken against THIS fold, then executed —
        // the store adapters are idempotent, so decision/execution order cannot double-apply.
        var graph = new TaskGraph();
        graph.Fold(store.ReadAllEvents(runId));
        var checkpoints = graph.Checkpoints();

        var declaredIds = new HashSet<string>(declared.Checkpoints.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var declaredStages = new HashSet<string>(declared.Checkpoints.Select(c => c.StageId), StringComparer.OrdinalIgnoreCase);
        var planStages = new HashSet<string>(plan.Stages.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var titles = 0;
        var revives = new List<CheckpointRow>();
        foreach (var row in declared.Checkpoints)
        {
            var existing = graph.Find(row.Id);
            if (existing == null) added++;
            else if (existing.Status == "archived") revives.Add(row);
            else if (!existing.Title.Equals(row.Title, StringComparison.Ordinal)) titles++;
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
            .ToList();

        // 3 · execute: adds + title refreshes ride the W1.1 seed adapter (add / refresh-title /
        // never-touch-status is exactly its contract).
        if (added + titles > 0)
        {
            store.SeedCheckpoints(runId, declared.Checkpoints.Select(c =>
                (c.Id, c.StageId, c.Title, DeclaredStatusLabel(c), c.Commit, c.Evidence)));
        }

        foreach (var row in revives)
            store.UpdateCheckpoint(runId, row.Id, DeclaredStatusLabel(row), row.Commit, row.Evidence, source: provenance);

        foreach (var item in archives)
            store.UpdateCheckpoint(runId, item.TaskId, "ARCHIVED", "-", "-", source: provenance);

        foreach (var stage in scaffolds)
        {
            var id = ScaffoldId(stage, graph, plan.Conventions);
            if (graph.Find(id) is { Status: "archived" })
                store.UpdateCheckpoint(runId, id, "TODO", "-", "-", source: "plan");
            else
                store.SeedCheckpoints(runId, [(id, stage.Id, stage.Title, "TODO", "-", "-")]);
        }

        var result = new SyncResult(added, titles, scaffolds.Count, archives.Count, revives.Count);
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
        row.IsDone ? "DONE" : row.IsInProgress ? "IN PROGRESS" : row.IsBlocked ? "BLOCKED" : "TODO";
}
