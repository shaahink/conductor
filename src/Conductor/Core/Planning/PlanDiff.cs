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
    IReadOnlyList<GateChange> ChangedGates)
{
    public bool IsEmpty =>
        AddedStages.Count == 0 && ChangedStages.Count == 0 &&
        AddedGates.Count == 0 && ChangedGates.Count == 0;

    public int TotalChanges =>
        AddedStages.Count + ChangedStages.Count + AddedGates.Count + ChangedGates.Count;

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

        return new PlanDiff(added, changed, addedGates, changedGates);
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
