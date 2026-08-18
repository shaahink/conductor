using System.Text;
using Conductor.Core.Events;

namespace Conductor.Core.Providers;

public sealed class AgentStreamState(
    Action<string, string> emit,
    TokenDeltaSink? onTokenDelta = null,
    Action<ToolCall, string>? onTool = null,
    Action<ToolRefusal>? onRefusal = null)
{
    private readonly List<ToolRefusal> _refusals = new();
    private readonly Lock _lock = new();
    private readonly StringBuilder _buffer = new();
    private readonly HashSet<string> _countedMessageIds = new(StringComparer.Ordinal);
    private readonly TokenDeltaSink? _onTokenDelta = onTokenDelta;
    private readonly ContextWindowMeter _context = new();

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

    /// <summary>KS7.1 — the permission posture refused a tool call. Kept as a THIRD channel beside
    /// <c>tool</c> for the same reason that one exists: a refusal is not a tool call that happened,
    /// and folding it into the tool stream would make the watchdog's "last tool call" clock tick on a
    /// session that is being blocked rather than working. The line still reaches the transcript on
    /// its own <c>refusal</c> kind, so a run with no refusal sink loses nothing readable.</summary>
    public void EmitRefusal(ToolRefusal refusal)
    {
        lock (_lock) _refusals.Add(refusal);
        emit("refusal", refusal.Line);
        onRefusal?.Invoke(refusal);
    }

    /// <summary>Every refusal this session hit, in order. The count is what a session report quotes;
    /// the list is what the evidence file carries.</summary>
    public IReadOnlyList<ToolRefusal> Refusals
    {
        get { lock (_lock) return _refusals.ToArray(); }
    }

    public void EmitTokenDelta(long input, long output, long reasoning, long cacheRead, decimal costUsd)
        => _onTokenDelta?.Invoke(input, output, reasoning, cacheRead, 0, costUsd);

    /// <summary>KS7.3 — the same delta, with the cache-write half of <paramref name="input"/> named.
    /// <paramref name="cacheWrite"/> is a SUBSET of <paramref name="input"/>, never a peer of it; see
    /// <see cref="TokensCacheWrite"/> for why the totals were left alone.</summary>
    public void EmitTokenDelta(long input, long output, long reasoning, long cacheRead, long cacheWrite, decimal costUsd)
        => _onTokenDelta?.Invoke(input, output, reasoning, cacheRead, cacheWrite, costUsd);

    /// <summary>K4.1 — record the prompt size of ONE deduplicated API call: everything the wire says was
    /// sent up, cached prefix included. Separate from <see cref="EmitTokenDelta"/> because the two answer
    /// different questions — the delta accrues an integral, this keeps a distribution — and because a
    /// provider that cannot report a per-call prompt size must be able to accrue tokens without
    /// fabricating a context reading.</summary>
    public void ObserveContext(long promptTokens) => _context.Observe(promptTokens);

    /// <summary>K4.1 — per-turn context high water and mean for this session so far.</summary>
    public ContextWindowStats Context => _context.Snapshot();

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

    /// <summary>
    /// KS7.3 — of <see cref="TokensInput"/>, how many tokens were CACHE WRITES
    /// (<c>cache_creation_input_tokens</c>). A subset, not a fifth bucket.
    /// </summary>
    /// <remarks>
    /// The wire has always reported a four-way split and this state carried three of it: cache-creation
    /// was added into <see cref="TokensInput"/> and lost its name there (ClaudeProvider's ReadUsage says
    /// so in as many words — "the state has four buckets and no cache-write bucket"). That fold is not a
    /// bug and is deliberately NOT undone here: <c>SessionRecord.TokensTotal</c> sums the buckets to gate
    /// <c>limits.maxSessionTokens</c>, 18 archived runs and every cost row in the store were written
    /// against that meaning, and re-basing it would silently restate history.
    /// <para>So the split is recovered by NAMING the part rather than by moving it. Every total is
    /// unchanged to the token; what is new is that a consumer — the OTel export, a cost model that wants
    /// the 1.25x/2x write rates rather than the 1x fresh rate — can now ask which part of the input was a
    /// write. The invariant a reader must keep: <c>TokensCacheWrite &lt;= TokensInput</c>, and adding it
    /// to a total that already contains <see cref="TokensInput"/> double-counts.</para>
    /// </remarks>
    public long? TokensCacheWrite { get; set; }
}

/// <summary>KS7.3 — one deduplicated API call's usage, cache split included. A named delegate rather
/// than a sixth <c>Action&lt;...&gt;</c> arity: the positional tuple was already at the edge of
/// readable, and <c>cacheWrite</c> is the one parameter a caller must not confuse with its neighbour
/// (it is contained IN <paramref name="input"/>).</summary>
public delegate void TokenDeltaSink(
    long input, long output, long reasoning, long cacheRead, long cacheWrite, decimal costUsd);
