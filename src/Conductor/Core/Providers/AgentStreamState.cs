using System.Text;

namespace Conductor.Core.Providers;

public sealed class AgentStreamState(Action<string, string> emit, Action<long, long, long, long, decimal>? onTokenDelta = null)
{
    private readonly Lock _lock = new();
    private readonly StringBuilder _buffer = new();
    private readonly Action<long, long, long, long, decimal>? _onTokenDelta = onTokenDelta;

    public void Emit(string kind, string text) => emit(kind, text);

    public void EmitTokenDelta(long input, long output, long reasoning, long cacheRead, decimal costUsd)
        => _onTokenDelta?.Invoke(input, output, reasoning, cacheRead, costUsd);

    public void AppendResultLine(string s)
    {
        lock (_lock) _buffer.AppendLine(s);
    }

    public string ResultBufferSnapshot()
    {
        lock (_lock) return _buffer.ToString();
    }

    public string? ResultText { get; set; }
    public bool ResultIsError { get; set; }

    /// <summary>W3.2: set by the provider the moment the wire says the credential is dead (an HTTP
    /// 401 / <c>authentication_failed</c> envelope), so the run can park on the FIRST retry instead
    /// of inferring it from the result text ten retries later — if a result envelope arrives at all.
    /// Null while the credential is good.</summary>
    public string? AuthFailure { get; set; }
    public decimal? CostUsd { get; set; }
    public int? NumTurns { get; set; }
    public long? TokensInput { get; set; }
    public long? TokensOutput { get; set; }
    public long? TokensReasoning { get; set; }
    public long? TokensCacheRead { get; set; }
}
