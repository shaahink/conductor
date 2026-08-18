namespace Conductor.Core.Integrations.Messaging;

/// <summary>KS11.4 / CHAPAR CH-6 — how often one chat may make the engine do work.
///
/// <para>A sliding window per key, not a token bucket: the refusal has to answer "when may I ask
/// again", and a bucket's refill instant is a fiction the reader cannot see, while the oldest take
/// in the window is a real moment that really frees a slot.</para>
///
/// <para>The clock is injected because the interesting property — the window REOPENING — is
/// otherwise only assertable by a test that sleeps for ten minutes, and a test that sleeps is a test
/// that gets shortened until it proves nothing.</para></summary>
public sealed class PullRateLimiter
{
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, List<DateTime>> _takes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public PullRateLimiter(int max, TimeSpan window, Func<DateTime>? now = null)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _max = max;
        _window = window;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Takes one slot for <paramref name="key"/> if the window has room. When it does not,
    /// <paramref name="retryAfter"/> is how long until the oldest take falls out of the window —
    /// never negative, and never zero on a refusal.</summary>
    public bool TryTake(string key, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var now = _now();
            if (!_takes.TryGetValue(key, out var stamps)) _takes[key] = stamps = [];
            stamps.RemoveAll(t => now - t >= _window);

            if (stamps.Count < _max)
            {
                stamps.Add(now);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            var wait = _window - (now - stamps[0]);
            retryAfter = wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
            return false;
        }
    }
}
