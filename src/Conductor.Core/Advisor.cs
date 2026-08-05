using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core;

public enum AdvisorAction
{
    BlockRetry,
    ResetBudget,
    NeedsHuman,
    ApplyFix,
    RerunGates,
    Retry,
    Resume,
    Skip,
}

public sealed record AdvisorVerdict(AdvisorAction Action, string Reason);

/// <summary>
/// Optional second brain: asks a cheap model (opencode/deepseek or claude haiku) what to do
/// when a session ends ambiguously. Any failure returns null — the orchestrator falls back
/// to its deterministic default.
/// </summary>
public static class Advisor
{
    public static async Task<AdvisorVerdict?> ConsultAsync(PlanConfig plan, string prompt, Action<string>? log = null)
    {
        var text = await AskTextAsync(plan, prompt, log).ConfigureAwait(false);
        if (text is null) return null;
        try
        {
            var m = Regex.Match(text, "\\{[^{}]*\"action\"[^{}]*\\}", RegexOptions.Singleline, ProgressConventions.RegexTimeout);
            if (!m.Success) { log?.Invoke("advisor gave no parseable verdict"); return null; }
            using var vdoc = JsonDocument.Parse(m.Value);
            var action = (vdoc.RootElement.TryGetProperty("action", out var act) ? act.GetString() : null)?.ToLowerInvariant() ?? "";
            var reason = vdoc.RootElement.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
            return TryParseAction(action) is { } parsed
                ? new AdvisorVerdict(parsed, reason)
                : null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"advisor failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>G1.1: run the advisor command with a prompt and return its <b>raw textual answer</b>,
    /// unwrapped from the provider envelope — the shared spawn path under both the verdict consult
    /// above and free-shape asks like plan import, where the answer is a document (a plan JSON), not
    /// an action verdict. Null when the advisor is off, times out, or fails; callers fall back.</summary>
    public static async Task<string?> AskTextAsync(PlanConfig plan, string prompt, Action<string>? log = null)
    {
        var a = plan.Advisor;
        if (a is not { Enabled: true } || string.IsNullOrWhiteSpace(a.Command)) return null;
        // SC3.4: a plan loaded from disk can no longer get here with an argless advisor — PlanConfig
        // refuses it — but a PlanConfig built in code still can. Spawning a CLI with nothing to answer
        // is how the advisor came to burn its whole timeout and return null, so say it instead.
        if (a.Args.Count == 0)
        {
            log?.Invoke($"advisor not consulted: advisor.args is empty, so '{a.Command}' would be spawned with no question — " +
                        $"set advisor.args (default: {string.Join(" ", AdvisorConfig.DefaultArgs)})");
            return null;
        }
        try
        {
            var args = ResolveArgs(a.Args, prompt);
            var r = await ProcessRunner.RunAsync(a.Command, args, plan.Repo, TimeSpan.FromMinutes(a.TimeoutMinutes)).ConfigureAwait(false);
            if (r.TimedOut) { log?.Invoke("advisor timed out"); return null; }
            return UnwrapEnvelope(r.Output, a.Output);
        }
        catch (Exception ex)
        {
            log?.Invoke($"advisor failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>SC3.4: advisor args live by the same rule as agent args (<see cref="AgentSession.ResolveArgs"/>)
    /// — an unfilled <c>{model}</c>, and the <c>--model</c>/<c>-m</c> flag in front of it, are dropped
    /// rather than passed through as a literal. The scaffold's advisor block ships with
    /// <c>"--model", "{model}"</c> for <c>plan import --model</c>; without this, every OTHER consult
    /// spawned the CLI asking for a model literally named <c>{model}</c> and got nothing back.</summary>
    internal static List<string> ResolveArgs(IReadOnlyList<string> template, string prompt)
    {
        var args = new List<string>(template.Count);
        foreach (var tok in template)
        {
            if (tok == "{model}")
            {
                if (args.Count > 0 && AgentSession.IsModelFlag(args[^1])) args.RemoveAt(args.Count - 1);
                continue;
            }
            args.Add(tok.Replace("{prompt}", prompt, StringComparison.Ordinal));
        }
        return args;
    }

    /// <summary>Peels the provider's transport envelope off the model's answer: "json" is claude's
    /// <c>{"result":"…"}</c> wrapper; "stream-json" is NDJSON whose final <c>{"type":"result",…}</c>
    /// line carries the answer. Anything unrecognised passes through raw.</summary>
    internal static string UnwrapEnvelope(string text, string outputKind)
    {
        if (outputKind.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                    return res.GetString() ?? text;
            }
            catch (JsonException) { /* raw */ }
        }
        else if (outputKind.Equals("stream-json", StringComparison.OrdinalIgnoreCase))
        {
            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "result" &&
                        doc.RootElement.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                        return res.GetString() ?? text;
                }
                catch (JsonException) { /* keep scanning */ }
            }
        }
        return text;
    }

    internal static AdvisorAction? TryParseAction(string action)
    {
        return action switch
        {
            "blockretry" or "block_retry" => AdvisorAction.BlockRetry,
            "resetbudget" or "reset_budget" => AdvisorAction.ResetBudget,
            "needshuman" or "needs_human" or "human" => AdvisorAction.NeedsHuman,
            "applyfix" or "apply_fix" => AdvisorAction.ApplyFix,
            "rerungates" or "rerun_gates" => AdvisorAction.RerunGates,
            "retry" => AdvisorAction.Retry,
            "resume" => AdvisorAction.Resume,
            "skip" => AdvisorAction.Skip,
            _ => null,
        };
    }
}
