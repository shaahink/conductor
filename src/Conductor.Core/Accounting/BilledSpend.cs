using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Core.Accounting;

/// <summary>
/// KS5.2 — reads what a model invocation was BILLED out of the output it already produced.
/// <para>Through the provider seam and nowhere else: <see cref="IAgentProvider.ParseLine"/> folds the
/// wire into an <see cref="AgentStreamState"/>, and the figure comes off
/// <see cref="AgentStreamState.CostUsd"/> — the same parser, the same envelope and the same number the
/// delivery agent's own row is written from. Nothing here multiplies a token count by a rate; if the
/// provider reported nothing, this returns null and the caller says "not known" rather than "zero".</para>
/// <para>It works on a captured buffer rather than a live stream because every path this exists for
/// (the advisor, the lanes, the audit worktree, the supervisor hook, the auth probe) spawns through
/// <c>ProcessRunner</c> and reads the output at the end. The stream is already over by the time anyone
/// asks what it cost.</para>
/// </summary>
public static class BilledSpend
{
    /// <summary>Fold <paramref name="output"/> through <paramref name="provider"/> and return the
    /// billed receipt, or null when the wire carried no cost.</summary>
    public static SpendReceipt? Read(IAgentProvider provider, string category, string? output, long wallMs)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(output)) return null;

        // A no-op emit: this is an accounting read, not a transcript. The provider parsers emit a line
        // per envelope by design, and a sink that threw here would turn "we could not price it" into a
        // crashed lane.
        var state = new AgentStreamState((_, _) => { });
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) provider.ParseLine(t, state);
        }

        // `--output-format json` (the advisor's default) is ONE pretty-printed object, not NDJSON, so
        // the per-line pass above sees only fragments. Re-offer the whole buffer before giving up:
        // ParseLine parses whatever string it is handed, and a JSON document is a legal "line".
        if (state.CostUsd is null)
        {
            var whole = output.Trim();
            if (whole.StartsWith('{')) provider.ParseLine(whole, state);
        }

        if (state.CostUsd is not { } cost) return null;
        return new SpendReceipt(category, cost,
            state.TokensInput ?? 0, state.TokensOutput ?? 0,
            state.TokensReasoning ?? 0, state.TokensCacheRead ?? 0, wallMs);
    }

    /// <summary>The billed receipt for a spawn made from an <see cref="AgentConfig"/> — the lanes, the
    /// audit worktree, the auth probe. The provider is resolved exactly as the run resolves it, so a
    /// lane is priced by the same adapter as the session it advises.</summary>
    public static SpendReceipt? Read(AgentConfig agent, string category, string? output, long wallMs)
        => agent is null ? null : Read(AgentProviderFactory.Create(agent), category, output, wallMs);

    /// <summary>The billed receipt for the advisor, whose config carries its own transport word
    /// (<c>output</c>: text | json | stream-json) rather than an <see cref="AgentConfig"/>.
    /// <para>The declared kind is tried first, then the command's own provider: <c>output</c> defaults
    /// to <c>"text"</c> and most shipped advisor blocks leave it there while passing
    /// <c>--output-format stream-json</c> in <c>args</c> — reading only the declared kind would price
    /// those at nothing while the wire was carrying the number.</para></summary>
    public static SpendReceipt? Read(AdvisorConfig advisor, string category, string? output, long wallMs)
    {
        if (advisor is null) return null;
        var declared = AgentProviderFactory.Create(new AgentConfig { Command = advisor.Command, Output = advisor.Output });
        return Read(declared, category, output, wallMs)
            ?? ReadFromCommand(advisor.Command, category, output, wallMs);
    }

    /// <summary>The billed receipt for a bare COMMAND LINE — the <c>watch</c> supervisor hook, which is
    /// a shell string an operator wrote (<c>claude -p --output-format stream-json …</c>) and not a
    /// config object. The adapter is chosen by the provider named in the command; anything else parses
    /// as plain text, reports no cost, and is honestly recorded as unknown.</summary>
    public static SpendReceipt? ReadFromCommand(string? command, string category, string? output, long wallMs)
        => Read(ProviderFor(command), category, output, wallMs);

    /// <summary>Which adapter can price a command line. Substring, not argv parsing: the command may be
    /// wrapped in a shell (<c>powershell -c "claude …"</c>), and the question is only which wire format
    /// to expect.</summary>
    internal static IAgentProvider ProviderFor(string? command)
    {
        var c = command ?? "";
        if (c.Contains("opencode", StringComparison.OrdinalIgnoreCase)) return new OpencodeProvider();
        if (c.Contains("claude", StringComparison.OrdinalIgnoreCase)) return new ClaudeProvider();
        return new GenericTextProvider();
    }
}
