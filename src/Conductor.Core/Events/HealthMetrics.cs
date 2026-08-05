namespace Conductor.Core.Events;

public static partial class HealthMetrics
{
    public enum Severity { Ok, Warn, Alert }

    public sealed record Thresholds
    {
        public int FailureLoopStreak { get; init; } = 3;
        public int GateFailureStreak { get; init; } = 3;
        public int GateOscillationFlips { get; init; } = 3;
        public long ContextSaturationTokens { get; init; } = 20_000_000;
        public double HighRetryRate { get; init; } = 0.5;
        public int MinSessionsForRetryFlag { get; init; } = 4;
        public static Thresholds Default { get; } = new();
    }
}
