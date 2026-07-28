using Conductor.Core.Planning;

namespace Conductor.Core.Http;

/// <summary>M6.1/M6.2 over the wire: the TUI posts a plan/tracker file path (or inline markdown) and,
/// with <c>Apply=false</c>, gets back the diff to preview; with <c>Apply=true</c> the diff is applied.
/// A structured doc parses deterministically (no model call); G1.1: freeform prose routes through the
/// plan's advisor model instead — <c>Model</c> optionally fills the advisor's <c>{model}</c>
/// placeholder, same convention as the CLI's <c>--model</c>.</summary>
public sealed record PlanImportRequestDto(string Source, bool Apply, string? Model = null);

/// <summary>The computed diff, wire-shaped: added stages/gates and, for existing ones, the fields that
/// would change. Mirrors <see cref="PlanDiff"/>; gate changes reuse <see cref="PlanStageChangeDto"/>
/// with <c>Id</c> holding the gate name.</summary>
public sealed record PlanDiffDto(
    IReadOnlyList<PlanStageDto> AddedStages, IReadOnlyList<PlanStageChangeDto> ChangedStages,
    IReadOnlyList<PlanGateDto> AddedGates, IReadOnlyList<PlanStageChangeDto> ChangedGates,
    // W4.1: the declared work an import brings. Additive and last, so existing clients are untouched.
    IReadOnlyList<PlanCheckpointDto>? AddedCheckpoints = null)
{
    public static PlanDiffDto From(PlanDiff d) => new(
        AddedStages: [.. d.AddedStages.Select(s => new PlanStageDto(
            s.Id, s.Title, s.Sessions, s.Kind, s.Agent?.Model, s.Workflow, s.Persona, s.Notes,
            s.DependsOn is { Count: > 0 } ? [.. s.DependsOn] : []))],
        ChangedStages: [.. d.ChangedStages.Select(c => new PlanStageChangeDto(
            c.Id, [.. c.Fields.Select(f => new PlanFieldChangeDto(f.Field, f.Old, f.New))]))],
        AddedGates: [.. d.AddedGates.Select(g => new PlanGateDto(g.Name, g.Command, g.Tier, g.TimeoutMinutes, g.Optional))],
        ChangedGates: [.. d.ChangedGates.Select(c => new PlanStageChangeDto(
            c.Name, [.. c.Fields.Select(f => new PlanFieldChangeDto(f.Field, f.Old, f.New))]))],
        AddedCheckpoints: [.. d.AddedCheckpoints.Select(c =>
            new PlanCheckpointDto(c.Id, c.StageId, c.Title, c.Status ?? "TODO"))]);
}

public sealed record PlanStageChangeDto(string Id, IReadOnlyList<PlanFieldChangeDto> Fields);
