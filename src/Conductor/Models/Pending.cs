namespace Conductor.Models;

public sealed class PendingFix
{
    public int FromSession { get; set; }
    public string GateFailures { get; set; } = "";
    public string ProgressSummary { get; set; } = "";
    public string VerifierFindings { get; set; } = "";
    public int? VerifierScore { get; set; }
}

public sealed class PendingVerify
{
    public int FromSession { get; set; }
    public string StageId { get; set; } = "";
    public string StageStartHead { get; set; } = "";
}

public sealed class PendingResume
{
    public int FromSession { get; set; }
    public string ClaudeSessionId { get; set; } = "";
    public string Reason { get; set; } = "";
    public int ResumeCount { get; set; }
}
