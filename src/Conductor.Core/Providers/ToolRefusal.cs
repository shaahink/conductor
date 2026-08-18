namespace Conductor.Core.Providers;

/// <summary>
/// KS7.1 — one tool call the permission posture refused, as the wire reported it. The provider's own
/// wording is carried verbatim rather than rephrased: what an auditor compares against a deny rule is
/// the CLI's sentence, not conductor's paraphrase of it.
/// </summary>
/// <param name="ToolName">The tool the session tried to use.</param>
/// <param name="Message">The refusal sentence the CLI emitted.</param>
/// <param name="ReasonType">The CLI's <c>decision_reason_type</c>, when the envelope carries one.</param>
public readonly record struct ToolRefusal(string ToolName, string Message, string? ReasonType)
{
    /// <summary>The single rendering of a refusal — the transcript line, the run log line and the
    /// evidence line are all this, so there is no second phrasing to drift.</summary>
    public string Line =>
        string.IsNullOrEmpty(Message) ? $"{ToolName} refused" : $"{ToolName} refused — {Message}";
}
