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

    /// <summary>Map the legacy <c>output</c> mode to a provider name (back-compat inference).</summary>
    public static string InferFromOutput(string output) => (output ?? "").ToLowerInvariant() switch
    {
        "opencode-json" => "opencode",
        "stream-json" => "claude",
        _ => "text",
    };
}
