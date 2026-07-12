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
}

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
