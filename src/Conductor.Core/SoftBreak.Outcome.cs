using System.Text.Json;

namespace Conductor.Core;

public static partial class SoftBreak
{
    /// <summary>What the session record carries — delivered, re-delivered, and obeyed. This is the
    /// measurement half of the rail, kept in its own file from the signal and the delivery marker
    /// because it is the piece a later tuning pass reads rather than a piece the rail runs on.</summary>
    public sealed record Outcome
    {
        public long ThresholdTokens { get; init; }
        public long CeilingTokens { get; init; }
        public int DeliveredCount { get; init; }
        public DateTime? FirstUtc { get; init; }
        public long FirstAtTokens { get; init; }
        public DateTime? LastUtc { get; init; }
        public long LastAtTokens { get; init; }

        /// <summary>The session was nudged, and it ended on its own terms under its ceiling — no
        /// budget kill, no crossing. This is the number the whole checkpoint exists to make readable:
        /// eleven of eleven were false before it could be counted.</summary>
        public bool Obeyed { get; init; }

        public bool Delivered => DeliveredCount > 0;
        public bool Restated => DeliveredCount > 1;

        public string Summary() => DeliveredCount == 0
            ? "soft-break: signalled, never delivered"
            : $"soft-break: delivered ×{DeliveredCount}, first at {FirstAtTokens / 1000.0:0.#}k, " +
              $"last at {LastAtTokens / 1000.0:0.#}k, {(Obeyed ? "OBEYED" : "not obeyed")}";
    }

    public static string ToJson(Outcome outcome) => JsonSerializer.Serialize(outcome, Json);
}
