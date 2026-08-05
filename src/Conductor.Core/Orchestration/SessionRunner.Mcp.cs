using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Lanes;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
public sealed partial class SessionRunner
{
    // ── soft-break + MCP wiring ──

    /// <summary>K1.2: the signal is written when the session crosses its soft threshold and RE-WRITTEN
    /// as it keeps spending, because the hook quotes the remaining budget out of it. A nudge that keeps
    /// repeating the number it saw at the crossing is telling the agent something that stopped being
    /// true a long turn ago. The re-write step scales with the margin (see
    /// <see cref="SoftBreak.RestateTokenStep"/>), so this is a handful of small file writes over the
    /// tail of a session, not one per poll.</summary>
    private void CheckSoftBreak(AgentSession agent, TrackerSnapshot preTrack)
    {
        if (ComputeSoftThreshold() is not { } thresh) return;
        var liveTokens = LiveTokens(agent);
        if (liveTokens < thresh) return;

        var maxTokens = _ctx.EffectiveMaxSessionTokens!.Value;
        var activeCp = preTrack.Checkpoints.FirstOrDefault(c => c.IsOpen)?.Id;
        var signal = new SoftBreak.Signal(liveTokens, maxTokens, thresh, activeCp, DateTime.UtcNow);
        var first = !_ctx.SoftBreakSignalled;
        if (!first && liveTokens - _softBreakSignalledAtTokens < SoftBreak.RestateTokenStep(signal)) return;

        _ctx.SoftBreakSignalled = true;
        _softBreakSignalledAtTokens = liveTokens;
        SoftBreak.WriteSignal(_ctx.Plan.StateDir, signal);
        if (!first) return; // the event and both log lines belong to the crossing, not to every refresh

        _ctx.Events.Emit(new SoftBreakRequested
        {
            LiveTokens = liveTokens,
            TokenBudget = maxTokens,
            CurrentCheckpointId = activeCp,
        });
        _ctx.Log($"soft-break: {liveTokens / 1000.0:0.#}k tokens >= {thresh / 1000.0:0.#}k threshold — nudge raised, re-stated every {SoftBreak.RestateTokenStep(signal) / 1000.0:0.#}k tokens or {SoftBreak.RestateInterval.TotalMinutes:0} minutes until the session ends");
        _ctx.Sink.Log($"[soft-break] {liveTokens / 1000.0:0.#}k/{maxTokens / 1000.0:0.#}k tokens — agent has been nudged to hand off");
    }

    /// <summary>Where the live token count stood when the signal file was last written. Reset per
    /// session beside <see cref="RunContext.SoftBreakSignalled"/>.</summary>
    private long _softBreakSignalledAtTokens;

    /// <summary>K1.2: the measurement. Folds what the hook wrote back — how many times the notice was
    /// actually put in front of the agent, and when — into a record the session carries, so the next
    /// tuning pass reads whether the cooperative rail fired instead of inferring it from an outcome
    /// column. <paramref name="budgetKilled"/> is the engine's own hard stop; a session that had to be
    /// killed did not obey, whatever it was told.</summary>
    private SoftBreak.Outcome? ReadSoftBreakOutcome(bool budgetKilled, long sessionTokens)
    {
        if (!_ctx.SoftBreakSignalled) return null;
        if (ComputeSoftThreshold() is not { } thresh) return null;
        var ceiling = _ctx.EffectiveMaxSessionTokens ?? 0;
        var d = SoftBreak.ReadDelivery(_ctx.Plan.StateDir);
        return new SoftBreak.Outcome
        {
            ThresholdTokens = thresh,
            CeilingTokens = ceiling,
            DeliveredCount = d?.Count ?? 0,
            FirstUtc = d is { Count: > 0 } ? d.FirstUtc : null,
            FirstAtTokens = d?.FirstAtTokens ?? 0,
            LastUtc = d is { Count: > 0 } ? d.LastUtc : null,
            LastAtTokens = d?.LastAtTokens ?? 0,
            Obeyed = (d?.Count ?? 0) > 0 && !budgetKilled && (ceiling <= 0 || sessionTokens < ceiling),
        };
    }

    /// <summary>Every token this session has been charged for so far, from the live stream.</summary>
    internal static long LiveTokens(AgentSession agent) =>
        (agent.TokensInput ?? 0) + (agent.TokensOutput ?? 0)
        + (agent.TokensReasoning ?? 0) + (agent.TokensCacheRead ?? 0);

    /// <summary>B13.2: true when this session has spent its whole token ceiling and must stop NOW.</summary>
    /// <remarks>The ceiling used to be read only AFTER the agent exited, which made it a label rather
    /// than a limit: a session ran its full length, and the engine then noted that it had been over
    /// budget the whole time. That is the wrong shape for the cost being controlled. A session's bill is
    /// roughly turns × context, and context only grows, so the last quarter of a long session costs
    /// several times the first — which is exactly the part a ceiling has to be able to cut. Enforcing it
    /// live turns a quadratic into a sequence of linear pieces.
    /// <para>Killing here is safe in the way that matters: the agent edits the working tree directly, so
    /// the files it has written survive, and the run's own rollover path takes it from there with a
    /// handoff. What is lost is the agent's in-flight reasoning, which is precisely the thing that had
    /// become expensive to keep. The cooperative nudge fires far earlier (<c>softBreakRatio</c>), so a
    /// session that pays attention to it lands its work and ends on its own terms well before this.</para>
    /// </remarks>
    private bool OverSessionTokenBudget(AgentSession agent) =>
        _ctx.EffectiveMaxSessionTokens is { } max && LiveTokens(agent) >= max;

    /// <summary>B13.2: ends the session that has spent its ceiling, and says so on both surfaces.
    /// Always returns true — the caller records that the kill was ours.</summary>
    private bool EndOnBudget(AgentSession agent, SessionRecord rec)
    {
        _ctx.Log($"session #{rec.Number} hit its token ceiling ({LiveTokens(agent) / 1_000_000.0:0.##}M ≥ " +
                 $"{_ctx.EffectiveMaxSessionTokens!.Value / 1_000_000.0:0.##}M) — ending it here; the next session starts fresh");
        _ctx.Sink.Log("[budget] session token ceiling reached — ending the session so the next one starts on a small context");
        agent.Kill();
        return true;
    }

    /// <summary>B13.2: what a budget-killed session cost. A killed session never emits the result
    /// envelope carrying <c>total_cost_usd</c>, so the one session the rail acts on would otherwise be
    /// the one session reporting $0 — and the ledger would read as though stopping early were free.
    /// Priced at what THIS run has actually been billed per token; null when no finished session has
    /// set a rate yet, because inventing one is worse than admitting the gap.</summary>
    private decimal? PriceBudgetKill(AgentSession agent) =>
        LiveCostEstimator.ObservedRatePerToken(_ctx.State.History) is { } rate
            ? decimal.Round(LiveTokens(agent) * rate, 4, MidpointRounding.AwayFromZero)
            : null;

    private long? ComputeSoftThreshold() =>
        SoftBreak.Threshold(_ctx.EffectiveMaxSessionTokens, _ctx.Plan.Limits.SoftBreakRatio);

    private void CleanSoftBreakSignal()
    {
        // B13.3: the delivered-marker goes with the signal. Leaving it behind would make the NEXT
        // session's nudge a no-op — the quietest possible way to lose the cooperative rail again.
        foreach (var name in new[] { SoftBreak.SignalFileName, SoftBreak.DeliveredFileName })
        {
            var signalFile = Path.Combine(_ctx.Plan.StateDir, name);
            try { if (File.Exists(signalFile)) File.Delete(signalFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>W2.1: the MCP wiring for one session. Both provider shapes are written every time —
    /// which one the child actually reads is decided by how it is launched (env var vs CLI flag), and
    /// writing both keeps that decision at the launch site instead of inside the config writer.</summary>
    internal sealed record McpWiring(string OpencodeConfigPath, string ClaudeConfigPath, string? ClaudeSettingsPath = null);

    /// <summary>B13.3: writes the per-session claude settings file carrying the budget hook, and returns
    /// its path (null when the session has no token ceiling, so no notice can ever be due). Written
    /// beside the MCP configs and deleted with them.</summary>
    private string? WriteBudgetHookSettings()
    {
        if (_ctx.EffectiveMaxSessionTokens is null) return null;
        var conductorExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(conductorExe) || !File.Exists(conductorExe)) return null;
        try
        {
            // Forward slashes, on Windows, deliberately. The agent CLI runs a hook command through a
            // shell, and that shell reads `\` as an escape: the same command with native separators
            // parses to a mangled path, the hook silently never runs, and the only symptom is a
            // cooperative rail that appears wired and does nothing — which is the exact failure this
            // whole change exists to remove. Windows accepts forward slashes in both positions.
            var exe = conductorExe.Replace('\\', '/');
            var stateDir = _ctx.Plan.StateDir.Replace('\\', '/');
            var settings = new
            {
                hooks = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["PostToolUse"] = new[]
                    {
                        new
                        {
                            matcher = "*",
                            hooks = new[]
                            {
                                new
                                {
                                    type = "command",
                                    command = $"\"{exe}\" hook-budget --state-dir \"{stateDir}\"",
                                    timeout = 10,
                                },
                            },
                        },
                    },
                },
            };
            var path = Path.Combine(_ctx.Plan.StateDir, "settings.budget.json");
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Log($"B13.3: could not write the budget hook settings: {ex.Message}");
            return null;
        }
    }

    /// <summary>W2.1: emits the conductor-tasks MCP server in BOTH provider dialects. Opencode reads a
    /// <c>{mcp:{name:{type:"local",command:[exe,...args]}}}</c> file via <c>OPENCODE_CONFIG</c>; claude
    /// reads a <c>{mcpServers:{name:{type:"stdio",command:exe,args:[...]}}}</c> file via
    /// <c>--mcp-config</c>. Until this wrote the second shape, every claude-provider run launched with
    /// an empty <c>mcp_servers</c> list — the task/note/bug verbs were wired and unreachable.
    /// <para>K1.4: the config MERGES the operator's own servers (<see cref="OperatorMcpServers"/>)
    /// instead of replacing them. It used to write conductor-tasks alone, so a session saw the task
    /// verbs and nothing the operator had configured. conductor-tasks is written first and the
    /// inherited servers cannot displace it; <c>agent.inheritMcpServers: false</c> opts a plan out
    /// when a run must not depend on the local setup at all.</para></summary>
    private async Task<McpWiring?> WireMcpServerAsync(SessionRecord rec, StageConfig stage, CancellationToken ct)
    {
        try
        {
            var conductorExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(conductorExe) || !File.Exists(conductorExe))
                return null;

            var eventsPath = Path.Combine(_ctx.Plan.StateDir, "events.jsonl");
            var journalPath = Path.Combine(_ctx.Plan.StateDir, "mcp-journal.jsonl");
            var runId = _ctx.State.RunId;
            var stateDir = _ctx.Plan.StateDir;
            var repoPath = _ctx.Plan.Repo;

            var commandArgs = new List<string>
            {
                "mcp-serve",
                "--events", eventsPath,
                "--journal", journalPath,
                "--run-id", runId,
                "--state-dir", stateDir,
                // K3.1: run.db left the state dir for the machine-level home, so the child server
                // has to be TOLD where it is — deriving it from the events path would silently
                // create a second, empty database beside the transcripts.
                "--run-db", _ctx.Plan.RunDbPath,
                "--repo", repoPath,
                // SC4.1: whose bg children these are. The battery settle waits on this session's.
                "--session", rec.Number.ToString(),
            };

            var opencodeServers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["conductor-tasks"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "local",
                    ["command"] = new[] { conductorExe }.Concat(commandArgs).ToArray(),
                    ["enabled"] = true,
                }
            };

            var claudeServers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["conductor-tasks"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "stdio",
                    ["command"] = conductorExe,
                    ["args"] = commandArgs.ToArray(),
                }
            };

            await InheritOperatorMcpServersAsync(stage, repoPath, opencodeServers, claudeServers, ct).ConfigureAwait(false);

            var opencodeConfig = new { mcp = opencodeServers };
            var claudeConfig = new { mcpServers = claudeServers };

            var opts = new JsonSerializerOptions { WriteIndented = true };
            var opencodePath = Path.Combine(_ctx.Plan.StateDir, "mcp-config.json");
            var claudePath = Path.Combine(_ctx.Plan.StateDir, "mcp-config.claude.json");
            await File.WriteAllTextAsync(opencodePath, JsonSerializer.Serialize(opencodeConfig, opts), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(claudePath, JsonSerializer.Serialize(claudeConfig, opts), ct).ConfigureAwait(false);
            return new McpWiring(opencodePath, claudePath, WriteBudgetHookSettings());
        }
        catch (Exception ex)
        {
            _ctx.Log($"I1: failed to write MCP config: {ex.Message}");
            return null;
        }
    }

    /// <summary>K1.4: folds the operator's configured MCP servers into the two per-session configs.
    /// conductor-tasks is already in both maps and is never overwritten — <see cref="OperatorMcpServers"/>
    /// drops an operator entry of that name — so the worst an inherited config can do is add a server.
    /// A plan that must not depend on the machine's local setup sets <c>agent.inheritMcpServers: false</c>.
    /// </summary>
    private async Task InheritOperatorMcpServersAsync(StageConfig stage, string repoPath,
        Dictionary<string, object> opencodeServers, Dictionary<string, object> claudeServers, CancellationToken ct)
    {
        if (_ctx.Plan.ResolveAgent(stage).InheritMcpServers == false)
        {
            _ctx.Log("K1.4: agent.inheritMcpServers is false — the session gets conductor-tasks only");
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Fold(await OperatorMcpServers.ForOpencodeAsync(repoPath, home, ct).ConfigureAwait(false), opencodeServers, "opencode");
        Fold(await OperatorMcpServers.ForClaudeAsync(repoPath, home, ct).ConfigureAwait(false), claudeServers, "claude");

        void Fold(OperatorMcpServers.Merged merged, Dictionary<string, object> into, string dialect)
        {
            foreach (var kv in merged.Servers)
                into[kv.Key] = kv.Value;
            if (merged.Sources.Count > 0)
                _ctx.Log($"K1.4: {dialect} config inherits {merged.Servers.Count} operator MCP server(s) " +
                         $"[{string.Join(", ", merged.Servers.Keys)}] from {string.Join(", ", merged.Sources)}");
            foreach (var note in merged.Notes)
                _ctx.Log($"K1.4: {dialect} MCP inheritance — {note}");
        }
    }

    /// <summary>W2.1: the CLI flags a claude-shaped child needs to actually load the server. Appended
    /// only when the plan's own args do not already carry <c>--mcp-config</c>, so a plan that wires MCP
    /// by hand keeps full control instead of getting a second, conflicting config.</summary>
    internal static IReadOnlyList<string> McpArgsFor(string providerName, IReadOnlyList<string>? plannedArgs, string claudeConfigPath)
        => McpArgsFor(providerName, plannedArgs, claudeConfigPath, null);

    internal static IReadOnlyList<string> McpArgsFor(string providerName, IReadOnlyList<string>? plannedArgs,
        string claudeConfigPath, string? budgetSettingsPath)
    {
        if (!string.Equals(providerName, "claude", StringComparison.Ordinal)) return [];
        var args = new List<string>();
        // --strict-mcp-config: the session gets exactly what this file says and nothing discovered
        // behind the engine's back. K1.4 changed what that file says — it is now conductor's server
        // MERGED with the operator's own — so strict buys determinism (one place decides, and the log
        // names it) rather than exclusion, which is what it used to buy and what made a user-scope
        // server invisible to every spawned session.
        if (plannedArgs == null || !plannedArgs.Any(a => a.Contains("--mcp-config", StringComparison.Ordinal)))
            args.AddRange(["--mcp-config", claudeConfigPath, "--strict-mcp-config"]);
        // B13.3: same rule for the budget hook — a plan that passes its own --settings keeps full
        // control rather than being handed a second, conflicting file.
        if (budgetSettingsPath is { Length: > 0 }
            && (plannedArgs == null || !plannedArgs.Any(a => a.Contains("--settings", StringComparison.Ordinal))))
            args.AddRange(["--settings", budgetSettingsPath]);
        return args;
    }

    private void CleanupMcpConfig(McpWiring? wiring)
    {
        if (wiring == null) return;
        foreach (var path in new[] { wiring.OpencodeConfigPath, wiring.ClaudeConfigPath, wiring.ClaudeSettingsPath })
        {
            if (string.IsNullOrEmpty(path)) continue;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void FoldMcpJournal()
    {
        var journalPath = Path.Combine(_ctx.Plan.StateDir, "mcp-journal.jsonl");
        if (!File.Exists(journalPath)) return;
        try
        {
            var journalEvents = EventLog.ReadAll(journalPath);
            if (journalEvents.Count == 0) return;
            foreach (var evt in journalEvents)
                _ctx.Events.Emit(evt);
            File.Delete(journalPath);
            _ctx.Log($"MCP journal folded: {journalEvents.Count} event(s) merged into event log");
        }
        catch (Exception ex)
        {
            _ctx.Log($"MCP journal fold failed: {ex.Message}");
        }
    }

    private string? BuildRolloverResumeHint(TrackerSnapshot preTrack)
    {
        if (_ctx.Store == null) return null;
        try
        {
            var allEvents = _ctx.Store.ReadAllEvents(_ctx.State.RunId);
            var taskGraph = new TaskGraph();
            taskGraph.Fold(allEvents);
            var activeCp = preTrack.ForStage(_ctx.State.CurrentStage ?? "")
                .FirstOrDefault(c => c.IsOpen);
            if (activeCp == null) return null;
            var next = taskGraph.CurrentTask(activeCp.Id);
            return next != null
                ? $"next sub-task: {next.Title} [{next.Status}]"
                : null;
        }
        catch (Exception ex)
        {
            _ctx.Log($"task-graph resume hint failed: {ex.Message}");
            return null;
        }
    }
}
