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
        var a = plan.Advisor;
        if (a is not { Enabled: true } || string.IsNullOrWhiteSpace(a.Command)) return null;
        try
        {
            var args = a.Args.Select(x => x.Replace("{prompt}", prompt)).ToList();
            var r = await ProcessRunner.RunAsync(a.Command, args, plan.Repo, TimeSpan.FromMinutes(a.TimeoutMinutes)).ConfigureAwait(false);
            if (r.TimedOut) { log?.Invoke("advisor timed out"); return null; }
            var text = r.Output;
            if (a.Output.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                // claude -p --output-format json wraps the answer in {"result": "..."}
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                        text = res.GetString() ?? text;
                }
                catch (JsonException) { /* fall through to regex */ }
            }
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
