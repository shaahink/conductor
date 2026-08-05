namespace Conductor.Models;

public sealed class AuditConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Max audit sessions per phase before giving up and moving on (or escalating).</summary>
    public int MaxAttempts { get; set; } = 1;
    /// <summary>P2: when true, the audit runs as a read-only lane in parallel with the next stage's
    /// deliver, instead of blocking as a sequential session. Default true.</summary>
    public bool EnableParallel { get; set; } = true;
}
