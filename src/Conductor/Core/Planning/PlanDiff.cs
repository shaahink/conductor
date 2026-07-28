using Conductor.Core;
using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>M6.2: the diff between the current plan and an incoming import. Re-importing a changed
/// mega-plan must show what it would add or alter and change only that — never silently clobber
/// hand-tuned stages or gates. <see cref="Compute"/> is pure (no I/O); the caller renders it, then
/// applies only on confirmation via <see cref="Apply"/>.</summary>
public sealed record PlanDiff(
    IReadOnlyList<StageConfig> AddedStages,
    IReadOnlyList<StageChange> ChangedStages,
    IReadOnlyList<GateConfig> AddedGates,
    IReadOnlyList<GateChange> ChangedGates,
    IReadOnlyList<ImportedCheckpoint> AddedCheckpoints)
{
    /// <summary>Back-compat overload for callers that predate W4.1's declared work.</summary>
    public PlanDiff(
        IReadOnlyList<StageConfig> addedStages,
        IReadOnlyList<StageChange> changedStages,
        IReadOnlyList<GateConfig> addedGates,
        IReadOnlyList<GateChange> changedGates)
        : this(addedStages, changedStages, addedGates, changedGates, []) { }

    public bool IsEmpty =>
        AddedStages.Count == 0 && ChangedStages.Count == 0 &&
        AddedGates.Count == 0 && ChangedGates.Count == 0 && AddedCheckpoints.Count == 0;

    public int TotalChanges =>
        AddedStages.Count + ChangedStages.Count + AddedGates.Count + ChangedGates.Count + AddedCheckpoints.Count;

    /// <summary>Compare the incoming import against the current plan. Stages/gates present in the plan
    /// but absent from the import are left untouched (never removed) — mid-plan edits are additive by
    /// design. Only fields the import actually carries are considered a change.</summary>
    public static PlanDiff Compute(PlanConfig current, ImportResult incoming)
    {
        var added = new List<StageConfig>();
        var changed = new List<StageChange>();
        foreach (var s in incoming.Stages)
        {
            var existing = current.Stages.FirstOrDefault(e => string.Equals(e.Id, s.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null) { added.Add(s); continue; }

            var fields = new List<FieldChange>();
            AddIf(fields, "title", existing.Title, s.Title);
            if (s.Sessions > 0 && s.Sessions != existing.Sessions)
                fields.Add(new FieldChange("sessions", existing.Sessions.ToString(), s.Sessions.ToString()));
            AddIf(fields, "kind", existing.Kind, s.Kind);
            AddIf(fields, "notes", existing.Notes, s.Notes);
            var oldDeps = existing.DependsOn is { Count: > 0 } ? string.Join(",", existing.DependsOn) : null;
            var newDeps = s.DependsOn is { Count: > 0 } ? string.Join(",", s.DependsOn) : null;
            AddIf(fields, "dependsOn", oldDeps, newDeps);
            if (fields.Count > 0) changed.Add(new StageChange(s.Id, fields));
        }

        var addedGates = new List<GateConfig>();
        var changedGates = new List<GateChange>();
        foreach (var g in incoming.Gates)
        {
            var existing = current.Gates.FirstOrDefault(e => string.Equals(e.Name, g.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) { addedGates.Add(g); continue; }
            var fields = new List<FieldChange>();
            AddIf(fields, "command", existing.Command, g.Command);
            AddIf(fields, "tier", existing.Tier, g.Tier);
            if (g.TimeoutMinutes > 0 && g.TimeoutMinutes != existing.TimeoutMinutes)
                fields.Add(new FieldChange("timeoutMinutes", existing.TimeoutMinutes.ToString(), g.TimeoutMinutes.ToString()));
            if (fields.Count > 0) changedGates.Add(new GateChange(g.Name, fields));
        }

        // W4.1: declared work the plan does not have yet. Comparison is against what the plan
        // ALREADY declares (inline checkpoints and, for a markdown-table plan, its tracker rows), so
        // a re-import adds only what is genuinely new and never resurrects a retired item or
        // overwrites a delivered one's status.
        var declared = DeclaredIds(current);
        var addedCheckpoints = new List<ImportedCheckpoint>();
        foreach (var c in incoming.Checkpoints)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || declared.Contains(c.Id)) continue;
            if (addedCheckpoints.Any(x => x.Id.Equals(c.Id, StringComparison.OrdinalIgnoreCase))) continue;
            addedCheckpoints.Add(c);
        }

        return new PlanDiff(added, changed, addedGates, changedGates, addedCheckpoints);
    }

    private static HashSet<string> DeclaredIds(PlanConfig plan)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in plan.Progress?.Checkpoints ?? []) ids.Add(c.Id);
        try
        {
            foreach (var row in ProgressProviderFactory.Create(plan).Read(plan).Checkpoints) ids.Add(row.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable, unconfigured or absent declared source is exactly the case an import
            // is here to fill.
        }
        return ids;
    }

    /// <summary>Apply exactly what this diff describes to the plan (add new stages/gates, update changed
    /// fields on existing ones), then bump the plan version and save. Untouched entries stay as-is.</summary>
    public void Apply(PlanConfig plan)
    {
        ApplyChanges(plan);
        plan.Save();
    }

    /// <summary>Apply without saving — for callers that must validate the mutated plan before
    /// persisting it (the control plane's import handler runs CollectErrors between apply and save,
    /// so a model-shaped import can never write an invalid plan file).</summary>
    public void ApplyChanges(PlanConfig plan)
    {
        foreach (var s in AddedStages)
            plan.Stages.Add(s);
        foreach (var ch in ChangedStages)
        {
            var existing = plan.Stages.FirstOrDefault(e => string.Equals(e.Id, ch.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null) continue;
            foreach (var f in ch.Fields) ApplyStageField(existing, f);
        }
        foreach (var g in AddedGates)
            plan.Gates.Add(g);
        foreach (var ch in ChangedGates)
        {
            var existing = plan.Gates.FirstOrDefault(e => string.Equals(e.Name, ch.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) continue;
            foreach (var f in ch.Fields) ApplyGateField(existing, f);
        }
        ApplyCheckpoints(plan);
    }

    /// <summary>
    /// W4.1: land the imported work in the plan's own declared-work channel, so W1.2's sync picks it
    /// up at the next boundary and the plan is drivable with no hand-authored tracker.
    ///
    /// Inline <c>progress.checkpoints</c> is that channel. A markdown-table plan is migrated to it —
    /// its existing tracker rows are folded in FIRST, so nothing is lost — which is the W1 model
    /// stated plainly: the plan declares the work, and the tracker is the generated view of it. A
    /// <c>script</c> provider owns its own declared work and is left alone.
    /// </summary>
    private void ApplyCheckpoints(PlanConfig plan)
    {
        if (AddedCheckpoints.Count == 0) return;
        plan.Progress ??= new ProgressConfig();
        if (string.Equals(plan.Progress.Kind, "script", StringComparison.OrdinalIgnoreCase)) return;

        var existing = plan.Progress.Checkpoints ?? [];
        var byId = new HashSet<string>(existing.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

        if (!string.Equals(plan.Progress.Kind, "plan-checkpoints", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var row in ProgressProviderFactory.Create(plan).Read(plan).Checkpoints)
                {
                    if (!byId.Add(row.Id)) continue;
                    existing.Add(new PlanCheckpoint
                    {
                        Id = row.Id,
                        Title = row.Title,
                        Status = row.IsDone ? "DONE" : "TODO",
                        Commit = row.Commit ?? "",
                        Evidence = row.Evidence ?? "",
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                // Nothing readable to preserve — the import is the first declaration.
            }
            plan.Progress.Kind = "plan-checkpoints";
        }

        foreach (var c in AddedCheckpoints)
        {
            if (!byId.Add(c.Id)) continue;
            existing.Add(new PlanCheckpoint
            {
                Id = c.Id,
                Title = c.Title,
                Status = string.IsNullOrWhiteSpace(c.Status) ? "TODO" : c.Status!.Trim(),
            });
        }

        // Stable, human-readable order: by stage as the plan lists them, then by id within a stage.
        var stageOrder = plan.Stages.Select((s, i) => (s.Id, i))
            .ToDictionary(x => x.Id, x => x.i, StringComparer.OrdinalIgnoreCase);
        plan.Progress.Checkpoints = [.. existing
            .OrderBy(c => stageOrder.TryGetValue(StageOf(c.Id), out var i) ? i : int.MaxValue)
            .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static string StageOf(string checkpointId)
    {
        var dot = checkpointId.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? checkpointId[..dot] : checkpointId;
    }

    private static void ApplyStageField(StageConfig stage, FieldChange f)
    {
        switch (f.Field)
        {
            case "title": stage.Title = f.New ?? stage.Title; break;
            case "notes": stage.Notes = f.New; break;
            case "kind": stage.Kind = f.New ?? stage.Kind; break;
            case "sessions": if (int.TryParse(f.New, out var n) && n > 0) stage.Sessions = n; break;
            case "dependsOn":
                stage.DependsOn = string.IsNullOrWhiteSpace(f.New)
                    ? null
                    : [.. f.New.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                break;
        }
    }

    private static void ApplyGateField(GateConfig gate, FieldChange f)
    {
        switch (f.Field)
        {
            case "command": gate.Command = f.New ?? gate.Command; break;
            case "tier": gate.Tier = f.New ?? gate.Tier; break;
            case "timeoutMinutes": if (int.TryParse(f.New, out var n) && n > 0) gate.TimeoutMinutes = n; break;
        }
    }

    private static void AddIf(List<FieldChange> fields, string field, string? oldVal, string? newVal)
    {
        // Only a non-empty incoming value that actually differs counts as a change.
        if (string.IsNullOrEmpty(newVal)) return;
        if (string.Equals(oldVal, newVal, StringComparison.Ordinal)) return;
        fields.Add(new FieldChange(field, oldVal, newVal));
    }
}
