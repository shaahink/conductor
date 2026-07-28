namespace Conductor.Core.Events;

public static partial class HealthMetrics
{
    public sealed record HealthFlag(Severity Severity, string Code, string Detail);

    public sealed record HealthReport(int Sessions, int Retries, double RetryRate, IReadOnlyList<HealthFlag> Flags)
    {
        public Severity Worst => Flags.Count == 0 ? Severity.Ok : Flags.Max(f => f.Severity);
    }
}
