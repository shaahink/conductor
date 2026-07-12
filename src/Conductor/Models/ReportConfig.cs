namespace Conductor.Models;

public sealed class ReportConfig
{
    public bool Commit { get; set; } = true;
    public bool Push { get; set; } = true;
    /// <summary>During a long session, rewrite+commit REPORT.md every N minutes with the latest agent
    /// activity so the AFK GitHub view reflects live progress. 0 = only report at session boundaries.</summary>
    public int HeartbeatMinutes { get; set; }
}
