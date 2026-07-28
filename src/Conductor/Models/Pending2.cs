namespace Conductor.Models;

public sealed class PendingPhaseGate
{
    public string StageId { get; set; } = "";
    public string StageStartHead { get; set; } = "";
}

public sealed class PendingAudit
{
    public string StageId { get; set; } = "";
    public string StageStartHead { get; set; } = "";
}

public sealed class PendingParallelAudit
{
    public string StageId { get; set; } = "";
    public string StageStartHead { get; set; } = "";
}
