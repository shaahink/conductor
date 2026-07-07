using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core;

public sealed record AdvisorVerdict(string Action, string Reason);

/// <summary>
/// Optional second brain: asks a cheap model (opencode/deepseek or claude haiku) what to do
/// when a session ends ambiguously. Any failure returns null — the orchestrator falls back
/// to its deterministic default.
/// </summary>
public static class Advisor
{
    public static AdvisorVerdict? Consult(PlanConfig plan, string prompt, Action<string>? log = null)
    {
        var a = plan.Advisor;
        if (a is not { Enabled: true } || string.IsNullOrWhiteSpace(a.Command)) return null;
        try
        {
            var args = a.Args.Select(x => x.Replace("{prompt}", prompt)).ToList();
            var r = ProcessRunner.Run(a.Command, args, plan.Repo, TimeSpan.FromMinutes(a.TimeoutMinutes));
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
            var m = Regex.Match(text, "\\{[^{}]*\"action\"[^{}]*\\}", RegexOptions.Singleline);
            if (!m.Success) { log?.Invoke("advisor gave no parseable verdict"); return null; }
            using var vdoc = JsonDocument.Parse(m.Value);
            var action = (vdoc.RootElement.TryGetProperty("action", out var act) ? act.GetString() : null)?.ToLowerInvariant() ?? "";
            var reason = vdoc.RootElement.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
            return action is "retry" or "resume" or "skip" or "human"
                ? new AdvisorVerdict(action, reason)
                : null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"advisor failed: {ex.Message}");
            return null;
        }
    }
}
