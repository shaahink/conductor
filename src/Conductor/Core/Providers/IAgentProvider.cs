using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core.Providers;

public interface IAgentProvider
{
    /// <summary>Stable adapter name used for selection + diagnostics (e.g. <c>opencode</c>).</summary>
    string Name { get; }

    /// <summary>Parse one raw stdout line, appending events and folding usage into <paramref name="state"/>.</summary>
    void ParseLine(string line, AgentStreamState state);

    /// <summary>True when the evidence text contains this backend's usage/rate-limit phrasing.</summary>
    bool DetectsUsageLimit(string evidence);

    /// <summary>W3.2: True when the evidence text says the credential is dead (401, expired OAuth,
    /// invalid key). Distinct from a usage limit: no amount of backoff fixes it, so the run parks
    /// for a human instead of burning attempts.</summary>
    bool DetectsAuthFailure(string evidence);

    /// <summary>K1.3: does this backend's wire format carry a reasoning/thinking token count at all?
    /// <para>False is not "it reported zero" — it is "the question does not apply here", and the two
    /// must not render the same. <c>costs.tokens_think</c> was 0 on all 125 rows ever written because
    /// every one of them came from Claude, whose usage object bundles reasoning into output and has
    /// no thinking field; a permanent 0 in a money column reads as "no thinking happened", which is
    /// a claim conductor cannot make. The column stays, because opencode DOES report the number
    /// (<see cref="OpencodeProvider"/> folds <c>tokens.reasoning</c>); the surfaces label it
    /// not-applicable instead, and this flag is what they label it from.</para></summary>
    bool ReportsReasoningTokens { get; }
}

public static class AgentProviderFactory
{
    public static IAgentProvider Create(AgentConfig cfg)
        => ResolveName(cfg) switch
        {
            "opencode" => new OpencodeProvider(),
            "claude" => new ClaudeProvider(),
            _ => new GenericTextProvider(),
        };

    /// <summary>The canonical provider name ("claude" | "opencode" | "text") for a config — the SAME
    /// decision <see cref="Create"/> makes, which is why Create is written in terms of it: the two
    /// cannot disagree about what a plan is running.
    /// <para>Never read <see cref="AgentConfig.Provider"/> directly to display or serve the provider.
    /// It is nullable and most plans leave it unset, in which case the real provider is INFERRED from
    /// the legacy <c>output</c> mode (B2.4) — so the raw field is null exactly when the answer is
    /// interesting, and anything reading it would report null for a run that is plainly Claude.</para>
    /// </summary>
    public static string ResolveName(AgentConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var name = string.IsNullOrWhiteSpace(cfg.Provider) ? InferFromOutput(cfg.Output) : cfg.Provider;
        // Trim, not just lower: a hand-edited plan with `"provider": " opencode "` used to miss every
        // arm and land on the generic text adapter — i.e. silently parse an opencode stream with the
        // wrong parser, rather than fail. IsNullOrWhiteSpace above already treats all-blank as unset.
        return name.Trim().ToLowerInvariant() switch
        {
            "opencode" or "opencode-json" => "opencode",
            "claude" or "stream-json" => "claude",
            _ => "text",
        };
    }

    /// <summary>K1.3: does the provider a plan resolves to report reasoning tokens? Answered through
    /// <see cref="Create"/> so the adapter itself owns the answer and a surface cannot drift from the
    /// parser. Used to send "not applicable" rather than 0 for a backend that never reports one.</summary>
    public static bool ReportsReasoningTokens(AgentConfig cfg) => Create(cfg).ReportsReasoningTokens;

    /// <summary>Map the legacy <c>output</c> mode to a provider name (back-compat inference).</summary>
    public static string InferFromOutput(string output) => (output ?? "").ToLowerInvariant() switch
    {
        "opencode-json" => "opencode",
        "stream-json" => "claude",
        _ => "text",
    };
}
