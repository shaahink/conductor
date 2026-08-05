using System.Text;
using Conductor.Core.Events;

namespace Conductor.Core.Providers;

public sealed class AgentStreamState(
    Action<string, string> emit,
    Action<long, long, long, long, decimal>? onTokenDelta = null,
    Action<ToolCall, string>? onTool = null)
{
    private readonly Lock _lock = new();
    private readonly StringBuilder _buffer = new();
    private readonly HashSet<string> _countedMessageIds = new(StringComparer.Ordinal);
    private readonly Action<long, long, long, long, decimal>? _onTokenDelta = onTokenDelta;

    public void Emit(string kind, string text) => emit(kind, text);

    /// <summary>SC7.1 — a tool call, emitted with its STRUCTURE intact. A consumer that wants the
    /// fields (the transcript, the verdict's out-of-repo check) wires <c>onTool</c>; one that only
    /// renders a line (the <c>bg logs</c> tail, a test) wires nothing and still gets the same text on
    /// the plain <c>tool</c> channel. Two channels rather than a widened <c>emit</c> so no existing
    /// caller has to care.</summary>
    public void EmitTool(ToolCall tool, string text)
    {
        if (onTool != null) onTool(tool, text);
        else emit("tool", text);
    }

    public void EmitTokenDelta(long input, long output, long reasoning, long cacheRead, decimal costUsd)
        => _onTokenDelta?.Invoke(input, output, reasoning, cacheRead, costUsd);

    /// <summary>SC2.3: true the FIRST time a given wire message id is offered for live accounting,
    /// false every time after. A provider whose stream re-emits one message once per content block
    /// (claude does — a thinking block and a text block of the same <c>message.id</c> arrive as two
    /// lines carrying the SAME usage) calls this before folding that usage, so the live ticker counts
    /// each API call once instead of overcounting 3-4x. The set is per-session because this state is:
    /// ids never leak between sessions.</summary>
    public bool TryCountMessageOnce(string messageId)
    {
        lock (_lock) return _countedMessageIds.Add(messageId);
    }

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
