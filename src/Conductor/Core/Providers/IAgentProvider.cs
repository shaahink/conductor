using System.Text;
using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core.Providers;

/// <summary>
/// B2.4 — the provider seam that decouples the engine from any one agent backend (F-2, D-11).
/// An <see cref="AgentSession"/> owns the process + IO and hands each raw stdout line to its
/// provider; the provider is the <em>only</em> place that knows a backend's wire format
/// (opencode <c>--format json</c>, claude <c>stream-json</c>, or plain text). Adding a new agent is
/// a new adapter, never an edit to the session core or an <c>output</c> <c>switch</c> in the
/// Orchestrator.
/// </summary>
/// <remarks>
/// Kept line-oriented on purpose: the session's push/poll IO loop (and its stall watchdog) is
/// unchanged, so resumability and stall/timeout behaviour cannot regress (stage trap). The provider
/// appends UI events and folds usage into the caller-owned <see cref="AgentStreamState"/>; it never
/// touches the process, disk, or clock. <see cref="DetectsUsageLimit"/> carries the per-backend
/// rate-limit phrase detection that used to live in the Orchestrator.
/// </remarks>
public interface IAgentProvider
{
    /// <summary>Stable adapter name used for selection + diagnostics (e.g. <c>opencode</c>).</summary>
    string Name { get; }

    /// <summary>Parse one raw stdout line, appending events and folding usage into <paramref name="state"/>.</summary>
    void ParseLine(string line, AgentStreamState state);

    /// <summary>True when the evidence text contains this backend's usage/rate-limit phrasing.</summary>
    bool DetectsUsageLimit(string evidence);
}

/// <summary>
/// The mutable accumulator a provider folds a stream into, owned by <see cref="AgentSession"/>.
/// Events are emitted through the injected sink (enqueued for the dashboard); usage totals and the
/// result text are read back by the Orchestrator once the session ends. A provider only appends —
/// it never reads the process or the clock — so parsing is a pure function of the input lines and is
/// unit-testable without spawning anything.
/// </summary>
public sealed class AgentStreamState(Action<string, string> emit, Action<long, long, long, long, decimal>? onTokenDelta = null)
{
    private readonly Lock _lock = new();
    private readonly StringBuilder _buffer = new();
    private readonly Action<long, long, long, long, decimal>? _onTokenDelta = onTokenDelta;

    /// <summary>Enqueue a UI event (kind ∈ system|text|thinking|tool|result|stderr|raw).</summary>
    public void Emit(string kind, string text) => emit(kind, text);

    /// <summary>Emit a per-step token delta (R2.6, fixes F-3). Called by the provider on <c>step_finish</c>
    /// so the event log captures live token burn and the <c>LiveMetrics</c> projection can fold it.</summary>
    public void EmitTokenDelta(long input, long output, long reasoning, long cacheRead, decimal costUsd)
        => _onTokenDelta?.Invoke(input, output, reasoning, cacheRead, costUsd);

    /// <summary>Append a line to the streamed result buffer (opencode has no single result event).</summary>
    public void AppendResultLine(string s)
    {
        lock (_lock) _buffer.AppendLine(s);
    }

    /// <summary>Current result-buffer contents (the streamed text assembled so far).</summary>
    public string ResultBufferSnapshot()
    {
        lock (_lock) return _buffer.ToString();
    }

    public string? ResultText { get; set; }
    public bool ResultIsError { get; set; }
    public decimal? CostUsd { get; set; }
    public int? NumTurns { get; set; }
    public long? TokensInput { get; set; }
    public long? TokensOutput { get; set; }
    public long? TokensReasoning { get; set; }
    public long? TokensCacheRead { get; set; }
}

/// <summary>Builds the <see cref="IAgentProvider"/> for a plan's agent. Selection prefers the explicit
/// <see cref="AgentConfig.Provider"/> name; when absent it is inferred from the legacy
/// <see cref="AgentConfig.Output"/> so every existing plan keeps working unchanged (B2.4 back-compat).</summary>
public static class AgentProviderFactory
{
    public static IAgentProvider Create(AgentConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var name = string.IsNullOrWhiteSpace(cfg.Provider) ? InferFromOutput(cfg.Output) : cfg.Provider;
        return name.ToLowerInvariant() switch
        {
            "opencode" or "opencode-json" => new OpencodeProvider(),
            "claude" or "stream-json" => new ClaudeProvider(),
            _ => new GenericTextProvider(),
        };
    }

    /// <summary>Map the legacy <c>output</c> mode to a provider name (back-compat inference).</summary>
    public static string InferFromOutput(string output) => (output ?? "").ToLowerInvariant() switch
    {
        "opencode-json" => "opencode",
        "stream-json" => "claude",
        _ => "text",
    };
}

/// <summary>Shared line helpers + the usage-limit detector reused across adapters. The regex is the
/// same one the Orchestrator carried before B2.4 (no behaviour change), now owned by the provider
/// layer so a future backend can specialise its own rate-limit phrasing.</summary>
internal static class ProviderText
{
    private static readonly Regex UsageLimitRx = new(
        @"usage limit|rate.?limit|overloaded|quota|out of credit|insufficient credit|credit balance|429|too many requests|5-hour|weekly limit",
        RegexOptions.IgnoreCase, ProgressConventions.RegexTimeout);

    public static bool DetectsUsageLimit(string evidence)
        => !string.IsNullOrEmpty(evidence) && UsageLimitRx.IsMatch(evidence);

    public static string Trunc(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }
}
