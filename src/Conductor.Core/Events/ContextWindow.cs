namespace Conductor.Core.Events;

/// <summary>
/// K4.1 — what a session CARRIED per turn, as distinct from what it spent in total.
/// </summary>
/// <remarks>
/// Every other token number in this engine is an integral: <see cref="LiveMetrics.SessionTokenTotals"/>
/// and <c>SessionRecord.TokensTotal</c> add every turn's usage together, so a session reads as "7.5M
/// tokens" — a figure 30-50x larger than any context window and therefore useless for the question an
/// operator actually asks, which is how full the window was. The prefix re-sent on every API call is
/// what drives ~66% of this project's bill (98% of tokens are cache reads), and nothing in the tree
/// measured it.
/// <para>The measurement: for one deduplicated assistant message, the prompt the API was handed is
/// <c>input_tokens + cache_creation_input_tokens + cache_read_input_tokens</c> — cached prefix plus
/// whatever was fresh. That sum is the number <c>/context</c> shows. Deduplication is not optional:
/// claude re-emits one message once per content block carrying the SAME usage, so an undeduplicated
/// mean is a mean over phantom turns (see <c>AgentStreamState.TryCountMessageOnce</c>).</para>
/// <para>Turns with no reported usage are not observed at all rather than counted as zero, so a mean
/// stays a mean over real API calls.</para>
/// </remarks>
public sealed record ContextWindowStats(long HighWaterTokens, long MeanTurnTokens, int Turns)
{
    /// <summary>No per-turn usage was observed. Deliberately distinct from a measured zero: a provider
    /// that reports no usage and a session that burned nothing must not read the same.</summary>
    public static readonly ContextWindowStats None = new(0, 0, 0);

    public bool Measured => Turns > 0;

    /// <summary>The operator sentence, e.g. <c>"95k mean · 135k high water · 78 turns"</c>.</summary>
    public string Describe() => Measured
        ? $"{Compact(MeanTurnTokens)} mean · {Compact(HighWaterTokens)} high water · {Turns} turns"
        : "not measured";

    private static string Compact(long tokens) => tokens >= 1_000
        ? (tokens / 1000d).ToString(tokens >= 10_000 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + "k"
        : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// K4.1 — accumulates per-turn context sizes into a <see cref="ContextWindowStats"/>. Lives beside the
/// live token fold rather than inside it because two callers need the same arithmetic: the provider
/// stream (which owns what a running session records) and <see cref="LiveMetrics"/> (which recovers the
/// same figures from persisted <see cref="TokenDelta"/> events, so a run that finished before this
/// checkpoint existed still yields its context profile).
/// </summary>
public sealed class ContextWindowMeter
{
    private readonly Lock _lock = new();
    private long _highWater;
    private long _sum;
    private int _turns;

    /// <summary>Record one API call's prompt size. Non-positive sizes are ignored — a turn the wire
    /// reported no usage for is absent from the sample, not a zero in it.</summary>
    public void Observe(long promptTokens)
    {
        if (promptTokens <= 0) return;
        lock (_lock)
        {
            _turns++;
            _sum += promptTokens;
            if (promptTokens > _highWater) _highWater = promptTokens;
        }
    }

    public ContextWindowStats Snapshot()
    {
        lock (_lock)
            return _turns == 0 ? ContextWindowStats.None : new ContextWindowStats(_highWater, _sum / _turns, _turns);
    }

    /// <summary>Fold a sequence of per-turn prompt sizes in one call — the replay path.</summary>
    public static ContextWindowStats From(IEnumerable<long> promptSizes)
    {
        ArgumentNullException.ThrowIfNull(promptSizes);
        var meter = new ContextWindowMeter();
        foreach (var size in promptSizes) meter.Observe(size);
        return meter.Snapshot();
    }
}
