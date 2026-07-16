using Conductor.Models;

namespace Conductor.Core.Http;

/// <summary>M6.3: the editable plan, read fresh from disk for <c>GET /plan</c> so the TUI plan editor
/// always sees the current file (including edits it just saved). Distinct from <see cref="StateDto"/>,
/// which is live run state folded from the event log; this is authoring surface.</summary>
public sealed record PlanDto(
    string Name, int PlanVersion, string PlanFile, string GatePolicy, string? DefaultWorkflow,
    string DefaultModel, IReadOnlyList<string> Workflows,
    IReadOnlyList<PlanStageDto> Stages, IReadOnlyList<PlanGateDto> Gates, PlanLimitsDto Limits,
    PlanQaDto? Qa = null)
{
    public static PlanDto FromPlan(PlanConfig p)
    {
        var workflows = new List<string> { "deliver-verify", "big-dev-then-big-audit", "docs-only", "spike" };
        if (p.Workflows is { Count: > 0 })
            foreach (var name in p.Workflows.Keys)
                if (!workflows.Contains(name, StringComparer.Ordinal)) workflows.Add(name);

        return new PlanDto(
            Name: p.Name,
            PlanVersion: p.PlanVersion,
            PlanFile: p.PlanFilePath,
            GatePolicy: p.GatePolicy,
            DefaultWorkflow: p.DefaultWorkflow,
            DefaultModel: p.Agent.Model ?? "(agent default)",
            Workflows: workflows,
            Stages: [.. p.Stages.Select(s => new PlanStageDto(
                s.Id, s.Title, s.Sessions, s.Kind,
                p.ResolveAgent(s).Model,
                s.Workflow ?? p.DefaultWorkflow,
                s.Persona, s.Notes,
                s.DependsOn is { Count: > 0 } ? [.. s.DependsOn] : [],
                s.Qa?.Mode, s.Qa?.VerifierThreshold))],
            Gates: [.. p.Gates.Select(g => new PlanGateDto(g.Name, g.Command, g.Tier, g.TimeoutMinutes, g.Optional))],
            Limits: new PlanLimitsDto(
                p.Limits.StallMinutes, p.Limits.SessionTimeoutMinutes,
                p.Limits.MaxRunCostUsd, p.Limits.MaxRunTokens, p.Limits.VerifierThreshold,
                p.Limits.MaxSessions),
            Qa: p.Pipeline?.Qa is { } qa
                ? new PlanQaDto(qa.Mode, qa.VerifierThreshold, qa.AuditCoversPriorSessions)
                : null);
    }
}

public sealed record PlanStageDto(
    string Id, string Title, int Sessions, string Kind, string? Model,
    string? Workflow, string? Persona, string? Notes, IReadOnlyList<string> DependsOn,
    string? QaMode = null, int? QaThreshold = null);

public sealed record PlanGateDto(string Name, string Command, string Tier, int TimeoutMinutes, bool Optional);
