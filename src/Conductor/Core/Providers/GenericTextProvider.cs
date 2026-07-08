namespace Conductor.Core.Providers;

/// <summary>
/// Fallback adapter for agents that stream unstructured text (today's <c>text</c> mode). Every line
/// becomes a truncated <c>raw</c> event; there is no usage/cost/turn structure to fold. Matches the
/// original <c>AgentSession</c> behaviour when <c>output</c> was neither JSON mode.
/// </summary>
public sealed class GenericTextProvider : IAgentProvider
{
    public string Name => "text";

    public bool DetectsUsageLimit(string evidence) => ProviderText.DetectsUsageLimit(evidence);

    public void ParseLine(string line, AgentStreamState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Emit("raw", ProviderText.Trunc(line ?? "", 220));
    }
}
