namespace Conductor.Core.Events;

/// <summary>
/// KS7.1 — the run's permission posture refused a tool call. Emitted from the agent's own stream the
/// moment the CLI says so (<c>{"type":"system","subtype":"permission_denied"}</c>), not reconstructed
/// afterwards from a transcript, so a refusal is queryable on the event log with the session that hit
/// it attached.
/// </summary>
/// <remarks>
/// This is the falsifiable half of the posture. A deny list that never fires and a deny list that is
/// not loaded look identical from outside the process — and print mode makes that worse, because the
/// CLI silently ignores a settings file that fails validation. A <see cref="ToolRefused"/> row is the
/// proof the profile was live: it can only exist if the rules reached the session.
/// </remarks>
public sealed record ToolRefused : ConductorEvent
{
    /// <summary>The tool the session tried to use — <c>Bash</c>, <c>WebFetch</c>, an MCP tool name.</summary>
    public required string ToolName { get; init; }

    /// <summary>The CLI's own sentence about the refusal, carried verbatim. Conductor does not
    /// rephrase it: the wire's wording is what an auditor compares against the deny rule.</summary>
    public required string Message { get; init; }

    /// <summary>The CLI's <c>decision_reason_type</c> (e.g. <c>subcommandResults</c>) — why the rule
    /// matched, when the CLI says. Null when the envelope carries none.</summary>
    public string? ReasonType { get; init; }

    /// <summary>The stage the refusal happened in, when the emitting session knows it.</summary>
    public string? StageId { get; init; }
}
