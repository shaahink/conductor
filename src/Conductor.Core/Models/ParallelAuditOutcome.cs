namespace Conductor.Models;

public sealed class ParallelAuditOutcome
{
    public string StageId { get; set; } = "";
    public AuditFindingSeverity MaxSeverity { get; set; }
    public string Findings { get; set; } = "";
    public bool Completed { get; set; }
}
