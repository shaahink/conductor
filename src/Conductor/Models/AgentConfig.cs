namespace Conductor.Models;

public sealed class AgentConfig
{
    public string Command { get; set; } = "claude";
    /// <summary>Placeholders: {prompt} {sessionId} {model}. {model} is substituted from <see cref="Model"/>
    /// (per-stage override → plan default), so `"--model", "{model}"` routes models per stage; when no model
    /// is set the flag+placeholder pair is dropped.</summary>
    public List<string> Args { get; set; } = new();
    /// <summary>Used to resume a stalled/interrupted agent session. Placeholders: {prompt} {claudeSessionId}</summary>
    public List<string>? ResumeArgs { get; set; }
    /// <summary>"stream-json" (claude) or "text" (opencode etc.). Legacy selector — kept for back-compat;
    /// when <see cref="Provider"/> is unset the provider is inferred from this (B2.4).</summary>
    public string Output { get; set; } = "stream-json";
    /// <summary>Selects the <c>IAgentProvider</c> adapter by name ("opencode", "claude", "text"). When
    /// empty the adapter is inferred from <see cref="Output"/> so existing plans are unchanged (B2.4, D-11).</summary>
    public string? Provider { get; set; }
    /// <summary>Optional system prompt injected before the base prompt (B7 persona templates).</summary>
    public string? SystemPrompt { get; set; }
    /// <summary>Optional model override for this agent (e.g. "claude-sonnet-4-20250514").</summary>
    public string? Model { get; set; }
    /// <summary>Optional sampling temperature (0.0–2.0). null = default.</summary>
    public double? Temperature { get; set; }
    // TokenCeiling was deleted in P0: it was defined and merged but enforced NOWHERE — a no-op trap
    // that looked active. The real per-session rollover knob is limits.maxSessionTokens (P5).
    /// <summary>Optional extra environment variables set on the agent process (e.g. OPENCODE_CONFIG).</summary>
    public Dictionary<string, string>? Env { get; set; }
    /// <summary>K1.4: whether a spawned session also gets the MCP servers the operator has configured on
    /// this machine (<c>~/.claude.json</c>, the repo's <c>.mcp.json</c>, opencode's own config) alongside
    /// conductor's own. Default (null) is true — a session that cannot see the operator's tools was the
    /// field bug. Set false when a run must not depend on the local setup at all; conductor-tasks is
    /// wired either way.</summary>
    public bool? InheritMcpServers { get; set; }
    /// <summary>Merges an optional override into this config, returning a new instance.
    /// A field whose value equals the C# default (e.g. "claude" for Command) is treated as unset
    /// and falls back to the base value — so a JSON override like {"systemPrompt":"x"} won't
    /// regress a base Command of "opencode" to "claude" (B7.1).</summary>
    public AgentConfig Merge(AgentConfig? o)
    {
        if (o == null) return this;
        var def = new AgentConfig();
        var m = new AgentConfig
        {
            Command = !string.IsNullOrWhiteSpace(o.Command) && o.Command != def.Command ? o.Command : Command,
            Args = o.Args.Count > 0 ? o.Args : Args,
            ResumeArgs = o.ResumeArgs is { Count: > 0 } ? o.ResumeArgs : ResumeArgs,
            Output = !string.IsNullOrWhiteSpace(o.Output) && o.Output != def.Output ? o.Output : Output,
            Provider = !string.IsNullOrWhiteSpace(o.Provider) ? o.Provider : Provider,
            SystemPrompt = o.SystemPrompt ?? SystemPrompt,
            Model = o.Model ?? Model,
            Temperature = o.Temperature ?? Temperature,
            InheritMcpServers = o.InheritMcpServers ?? InheritMcpServers,
        };
        return m;
    }
}
