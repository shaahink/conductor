namespace Conductor.Core.Http;

/// <summary>P2: the plan-wide QA dial (pipeline.qa) surfaced to the TUI editor — editable via the
/// <c>qa</c> target on <c>POST /plan/edit</c>, live-applied at the next session boundary.</summary>
public sealed record PlanQaDto(string Mode, int? VerifierThreshold, bool AuditCoversPriorSessions);
