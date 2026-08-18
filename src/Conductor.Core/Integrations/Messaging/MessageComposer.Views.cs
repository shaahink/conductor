using System.Text;

using Conductor.Core.Events;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>KS11.1 — the PULLED half of composition: what a reader gets when they ask, rather than
/// what the run says unprompted. CH-6 is about to grow this side hard (evidence on demand, money,
/// tokens), which is exactly why it is a page of its own rather than more of the push file.</summary>
public sealed partial class MessageComposer
{
    // ────────────────────────────── the pulled views ──────────────────────────────

    /// <summary>What <c>/status</c> answers.</summary>
    public string StatusText()
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
            sb.AppendLine($"\n{_state.AttentionReason}{Staleness.Since(_state.AttentionSinceUtc)}");

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

    /// <summary>What <c>/tasks</c> answers.</summary>
    public string TasksText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>Conductor — {_plan.Name}</b>");
        sb.AppendLine($"<b>Task Graph</b>");
        sb.AppendLine();

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

    /// <summary>The once-a-day summary, and what <c>/daily</c> answers on demand.
    ///
    /// <para>KS11.5 / CH-5: recomposed into the grammar every other message uses — a headline that
    /// says where the run is, the day's work under it, a proof line carrying the gate verdict, and
    /// the telemetry in monospace. It used to open with <c>Cost: $0.4242</c> and no progress, no cap
    /// and no tokens at all: the one message a day a reader is guaranteed to see said less about the
    /// run than every push around it.</para></summary>
    public string DailyDigestText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>{EscapeHtml(PlanName())} — daily digest</b>");
        sb.AppendLine(EscapeHtml($"{_state.Status} · stage {StageLabel(_state.CurrentStage ?? "")} · "
                               + $"{_state.SessionCounter} session{(_state.SessionCounter == 1 ? "" : "s")} so far"));

        var gates = "";
        if (_store != null)
        {
            try
            {
                var outcomes = _store.QuerySessionOutcomesByStage(_state.RunId);
                if (outcomes.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("<b>Sessions by stage</b>");
                    foreach (var r in outcomes)
                        sb.AppendLine(EscapeHtml($"  {r.StageId}: {r.Outcome} ×{r.Count}"));
                }

                var failures = _store.QueryRecentGateFailures(_state.RunId, 5);
                gates = failures.Count == 0
                    ? "all recent gates passed"
                    : string.Join(", ", failures.Select(g => $"FAIL {g.Name} ({g.StageId})"));
            }
#pragma warning disable CA1031
            catch { /* best-effort: digest is advisory */ }
#pragma warning restore CA1031
        }

        if (ProofLine(gates, []) is { Length: > 0 } proof)
        {
            sb.AppendLine();
            sb.AppendLine(proof);
        }

        sb.AppendLine();
        sb.Append(Telemetry(PulledFacts()));
        return sb.ToString().TrimEnd();
    }
}
