using System.Globalization;
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
        AppendChannels(sb);

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

    /// <summary>DV1.1 — the outbound channels, on the surface a reader asks from their phone.
    ///
    /// <para><c>/status</c> is the one question an away-from-keyboard operator asks, and until now it
    /// could answer it in full while the run's github mirror had been dead since the first boundary.
    /// The roll-up is unconditional — a reader must be able to tell "this build does not report
    /// channels" from "the channels are fine" — and each broken one gets its own line with the exact
    /// command that clears it.</para>
    ///
    /// <para>Started-ness is deliberately not passed even though this runs inside the engine: the
    /// composer is constructed by <c>TelegramService.AdoptPlan</c> and asking the service that owns
    /// it whether it has started, in order to answer a message the service itself just delivered, is
    /// a question with only one answer.</para></summary>
    private void AppendChannels(StringBuilder sb)
    {
        var channels = ChannelHealthProbe.Collect(_plan);
        sb.AppendLine($"Channels: {EscapeHtml(ChannelHealthProbe.SummaryLine(channels))}");
        foreach (var c in ChannelHealthProbe.Loud(channels))
        {
            sb.AppendLine();
            sb.AppendLine($"<b>{EscapeHtml(c.Channel)} {c.Word}</b> - {EscapeHtml(c.Detail)}");
            sb.AppendLine(c.FixCommand.Length > 0
                ? $"fix: <code>{EscapeHtml(c.FixCommand)}</code>"
                : $"fix: {EscapeHtml(c.Fix)}");
        }
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
            catch { /* best-effort: digest is advisory */ }
        }

        if (LedgerLine() is { Length: > 0 } ledger)
        {
            sb.AppendLine();
            sb.AppendLine(EscapeHtml(ledger));
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
    /// <summary>
    /// DV6.1 — the one line that makes the ledger visible: how much is outstanding, and how long the
    /// oldest of it has been.
    ///
    /// <para><b>Why in the digest.</b> The bug table and followups.md are real, durable and
    /// invisible: measured at the start of this era, 28 open bugs and a dozen open followup rows
    /// reached exactly one surface - a session's own prompt. The digest is the one message a day a
    /// reader is guaranteed to see, and one line in it is the whole intervention.</para>
    ///
    /// <para>DV6.3 gave the board page the same line, so the counting moved to
    /// <see cref="LedgerSummary"/> and both surfaces read one answer. The rules it keeps - the age is
    /// a BUG age because followups.md carries no dates, and an empty ledger renders nothing - live
    /// there with it.</para>
    /// </summary>
    public string LedgerLine() => LedgerSummary.Line(_store, _plan.StateDir);
}
