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

    /// <summary>
    /// KS7.4 — the template for a session that FORKS an earlier one instead of starting cold.
    /// Placeholders: {prompt}, {claudeSessionId} (the session being forked FROM), {sessionId} (the new
    /// id). Unset = no forking, whatever <see cref="ForkKinds"/> says: the mechanism is opt-in because
    /// only the plan knows whether its agent CLI supports it.
    /// </summary>
    /// <remarks>
    /// Measured on claude 2.1.235 rather than assumed: <c>--fork-session</c> composes with
    /// <c>--session-id</c> and the CLI honours the id we ask for, so conductor does not surrender id
    /// control to fork — crash recovery and transcript correlation keep working. The carried
    /// conversation arrives as a cache READ (30,098 read / 0 write on a 30k base), which is why a fork
    /// is not a more expensive resume: it measured 0.15% larger and $0.0001 cheaper than resuming the
    /// same conversation.
    /// <para>The failure mode to know: a fork resumes a transcript ON DISK. If the agent CLI has pruned
    /// the base session, the fork cannot start. That is the second reason this is opt-in.</para>
    /// </remarks>
    public List<string>? ForkArgs { get; set; }

    /// <summary>KS7.4 — which session kinds fork the stage's previous session rather than starting
    /// cold. Names match <c>SessionKind</c>, case-insensitively: <c>["fix","audit"]</c> is the pairing
    /// the checkpoint asks for — both are sessions ABOUT work another session just did, and both
    /// otherwise pay full fresh-input rates to rediscover it. Empty or unset = nothing forks.</summary>
    public List<string>? ForkKinds { get; set; }
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
    /// <summary>KS7.1: the permission posture this agent runs under — the deny rules that bound its
    /// blast radius, and the <c>--permission-mode</c> it is launched with. Null (the default) means
    /// the plan's own <see cref="Args"/> decide exactly as they did before this field existed, so no
    /// existing plan changes behaviour by upgrading.</summary>
    public PermissionsConfig? Permissions { get; set; }
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
            ForkArgs = o.ForkArgs is { Count: > 0 } ? o.ForkArgs : ForkArgs,
            ForkKinds = o.ForkKinds is { Count: > 0 } ? o.ForkKinds : ForkKinds,
            Output = !string.IsNullOrWhiteSpace(o.Output) && o.Output != def.Output ? o.Output : Output,
            Provider = !string.IsNullOrWhiteSpace(o.Provider) ? o.Provider : Provider,
            SystemPrompt = o.SystemPrompt ?? SystemPrompt,
            Model = o.Model ?? Model,
            Temperature = o.Temperature ?? Temperature,
            InheritMcpServers = o.InheritMcpServers ?? InheritMcpServers,
            Permissions = Permissions == null ? o.Permissions : Permissions.Merge(o.Permissions),
        };
        return m;
    }
}
