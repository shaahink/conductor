using Conductor.Core.Planning;

namespace Conductor.Core.Http;

/// <summary>M6.1/M6.2 over the wire: the TUI posts a plan/tracker file path (or inline markdown) and,
/// with <c>Apply=false</c>, gets back the diff to preview; with <c>Apply=true</c> the diff is applied.
/// Deterministic parse only (no model call) — the zero-spend import path lives in the Face too.</summary>
public sealed record PlanImportRequestDto(string Source, bool Apply);

/// <summary>The computed diff, wire-shaped: added stages/gates and, for existing ones, the fields that
/// would change. Mirrors <see cref="PlanDiff"/>; gate changes reuse <see cref="PlanStageChangeDto"/>
/// with <c>Id</c> holding the gate name.</summary>
public sealed record PlanDiffDto(
    IReadOnlyList<PlanStageDto> AddedStages, IReadOnlyList<PlanStageChangeDto> ChangedStages,
    IReadOnlyList<PlanGateDto> AddedGates, IReadOnlyList<PlanStageChangeDto> ChangedGates)
{
    public static PlanDiffDto From(PlanDiff d) => new(
        AddedStages: [.. d.AddedStages.Select(s => new PlanStageDto(
            s.Id, s.Title, s.Sessions, s.Kind, s.Agent?.Model, s.Workflow, s.Persona, s.Notes,
            s.DependsOn is { Count: > 0 } ? [.. s.DependsOn] : []))],
        ChangedStages: [.. d.ChangedStages.Select(c => new PlanStageChangeDto(
            c.Id, [.. c.Fields.Select(f => new PlanFieldChangeDto(f.Field, f.Old, f.New))]))],
        AddedGates: [.. d.AddedGates.Select(g => new PlanGateDto(g.Name, g.Command, g.Tier, g.TimeoutMinutes, g.Optional))],
        ChangedGates: [.. d.ChangedGates.Select(c => new PlanStageChangeDto(
            c.Name, [.. c.Fields.Select(f => new PlanFieldChangeDto(f.Field, f.Old, f.New))]))]);
}

public sealed record PlanStageChangeDto(string Id, IReadOnlyList<PlanFieldChangeDto> Fields);
