using System.Globalization;
using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Store;

namespace Conductor.Core.Integrations;

public sealed partial class TelegramService
{
    public async Task PushSessionEndAsync(int sessionNumber, string stage, string outcome, string? gateSummary,
        string? resultSummary, decimal? costUsd, decimal? score, CancellationToken ct = default)
    {
        if (!_started) return;

        var runCost = _state.TotalCostUsd > 0 ? $" | run: ${_state.TotalCostUsd:0.0000}" : "";
        var scoreStr = score.HasValue ? $" | score: {score:0}/100" : "";
        var sb = new StringBuilder();
        sb.AppendLine($"<b>s{sessionNumber} {outcome}</b> — {stage}");
        sb.AppendLine($"gates: {(string.IsNullOrWhiteSpace(gateSummary) ? "(not recorded)" : gateSummary)}");
        if (!string.IsNullOrWhiteSpace(resultSummary))
            sb.AppendLine($"result: {resultSummary}");
        sb.Append($"cost: ${costUsd ?? 0:0.0000}{runCost}{scoreStr}");

        await PushAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private string BuildStatusText()
    {
        TrackerSnapshot track;
        try { track = _progress.Read(_plan, CancellationToken.None); }
        catch (IOException) { track = new TrackerSnapshot(); }
        catch (InvalidOperationException) { track = new TrackerSnapshot(); }

        var sb = new StringBuilder();
        sb.AppendLine($"<b>Conductor — {_plan.Name}</b>");
        sb.AppendLine();
        sb.AppendLine($"Status: <b>{_state.Status}</b>");
        sb.AppendLine($"Stage: {_state.CurrentStage ?? "-"}  |  attempts used: {_state.AttemptsThisStage}");
        sb.AppendLine($"Checkpoints: {track.Checkpoints.Count(c => c.IsDone)}/{track.Checkpoints.Count} done");
        sb.AppendLine($"Sessions: {_state.SessionCounter}  |  Cost: ${_state.TotalCostUsd:0.0000}");

        if (_state.AttentionReason != null)
            sb.AppendLine($"\n{_state.AttentionReason}");

        if (_state.CurrentStage != null)
        {
            var rows = track.ForStage(_state.CurrentStage).ToList();
            if (rows.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"<b>{_state.CurrentStage} checkpoints:</b>");
                foreach (var r in rows.Take(10))
                {
                    var icon = r.IsDone ? "DONE" : r.IsInProgress ? "ACTV" : r.IsBlocked ? "BLKD" : "TODO";
                    sb.AppendLine($"  [{icon}] {r.Id}: {r.Title}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private string BuildTasksText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>Conductor — {_plan.Name}</b>");
        sb.AppendLine($"<b>Task Graph</b>");
        sb.AppendLine();

        var eventsPath = Path.Combine(_plan.StateDir, "events.jsonl");
        if (_store == null)
        {
            sb.AppendLine("(no store available)");
            return sb.ToString().TrimEnd();
        }

        var graph = new TaskGraph();
        graph.Fold(_store.ReadAllEvents(_state.RunId));

        if (graph.Count == 0)
        {
            sb.AppendLine("(no tasks recorded yet)");
            return sb.ToString().TrimEnd();
        }

        var checkpoints = graph.Tasks
            .GroupBy(t => t.CheckpointId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var ck in checkpoints)
        {
            sb.AppendLine($"<b>{ck.Key}:</b>");
            foreach (var task in ck.OrderBy(t => t.Order))
            {
                var icon = task.Status switch
                {
                    "done" => " DONE ",
                    "in_progress" => "▶ACTV ",
                    "skipped" => " SKIP ",
                    _ => "      ",
                };
                var src = task.Source.Length > 0 ? $" ({task.Source})" : "";
                sb.AppendLine($"  [{icon}] {task.Title}{src}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private async Task MaybeSendDailyDigestAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastDigestUtc < TimeSpan.FromHours(24) || _cfg?.AllowedChatIds is not { Count: > 0 } ids)
            return;

        _lastDigestUtc = DateTime.UtcNow;
        foreach (var cid in ids)
            await SendDailyDigestAsync(cid, ct).ConfigureAwait(false);
    }

    private async Task SendDailyDigestAsync(string chatId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>Conductor Daily Digest — {_plan.Name}</b>");
        sb.AppendLine($"Status: <b>{_state.Status}</b> | Stage: {_state.CurrentStage ?? "-"}");
        sb.AppendLine($"Sessions: {_state.SessionCounter} | Cost: ${_state.TotalCostUsd:0.0000}");

        if (_store != null)
        {
            try
            {
                var outcomes = _store.QuerySessionOutcomesByStage(_state.RunId);
                if (outcomes.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("<b>Session outcomes by stage:</b>");
                    foreach (var r in outcomes)
                    {
                        sb.AppendLine($"  {r.StageId}: {r.Outcome} ×{r.Count}");
                    }
                }

                var gates = _store.QueryRecentGateFailures(_state.RunId, 5);
                if (gates.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("<b>Recent gate failures:</b>");
                    foreach (var g in gates)
                    {
                        sb.AppendLine($"  FAIL: {g.Name} ({g.StageId})");
                    }
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine("All recent gates passed.");
                }
            }
#pragma warning disable CA1031
            catch { /* best-effort: digest is advisory */ }
#pragma warning restore CA1031
        }

        await SendAsync(chatId, sb.ToString().TrimEnd(), ct).ConfigureAwait(false);
    }

    private static string BuildInlineKeyboard(IReadOnlyList<(string Text, string CallbackData)> buttons)
    {
        var elements = new List<Dictionary<string, string>>(buttons.Count);
        foreach (var (text, data) in buttons)
            elements.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["text"] = text,
                ["callback_data"] = data,
            });

        var kb = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["inline_keyboard"] = new[] { elements },
        };

        return JsonSerializer.Serialize(kb, JsonOpts);
    }

    private static string EscapeHtml(string s)
    {
        return s.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
