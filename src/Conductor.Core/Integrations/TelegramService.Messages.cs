using System.Globalization;
using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;

namespace Conductor.Core.Integrations;

public sealed partial class TelegramService
{
    /// <summary>K5.2 — the session-end push, rebuilt from the owner's own transcribed run (15
    /// sessions, $97.46, five defects).
    /// <para>The session number is printed ONCE and comes from the record, not from the live
    /// counter: the identity line stamped in <see cref="SendAsync"/> carries
    /// <see cref="SessionEndPush.Number"/>, and this body no longer opens with a second copy that a
    /// late push could disagree with.</para>
    /// <para>The stage carries its title. The result is RENDERED from the K5.1 contract rather than
    /// re-cut — the caller hands over the record whole and the bounding happens here, once. A
    /// rollover says what it landed and that its gates are deferred, not "(not recorded)". And every
    /// push carries a progress line, which fifteen messages of that run did not have between
    /// them.</para></summary>
    public async Task PushSessionEndAsync(SessionEndPush push, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(push);
        if (!_started) return;

        var sb = new StringBuilder();
        // K5.4: the outcome leads. The stage and its title moved to the context line the stamp
        // applies to EVERY push, so this no longer renders them a second time.
        sb.Append("<b>").Append(EscapeHtml(push.Outcome)).Append("</b>");
        if (push.Duration is { } d) sb.Append(" · ").Append(EscapeHtml(Elapsed(d)));
        sb.AppendLine();

        var progress = ProgressLine(push.Stage);
        if (progress.Length > 0) sb.AppendLine(EscapeHtml(progress));

        var landed = LandedLine(push);
        if (landed.Length > 0) sb.AppendLine(landed);

        sb.AppendLine($"gates: {EscapeHtml(GatesLine(push))}");

        var result = ResultLines(push.ResultSummary);
        if (result.Length > 0) sb.AppendLine(RemoteLinks.LinkifyPullRequests(result, Remote()));

        // K5.4: money with headroom, not four decimals of a number with nothing to compare it to.
        sb.Append(MoneyLine.ForSession(push.CostUsd, _state.TotalCostUsd, _plan.Limits.MaxRunCostUsd));
        if (push.Score is { } score) sb.Append(" · score ").Append(EscapeHtml($"{score:0}/100"));

        if (RemoteLinks.Report(Remote(), Branch()) is { } report)
            sb.Append("\n<a href=\"").Append(EscapeHtml(report)).Append("\">the run's report</a>");

        // K5.4: a session that advanced is informational; one that ended blocked or needing the owner
        // is the whole reason disable_notification exists.
        await EnqueueAsync(sb.ToString(), push.Number, SessionSeverity(push.Outcome), null, ct, push.Stage)
            .ConfigureAwait(false);
    }

    /// <summary>Only outcomes the owner can do something about are allowed to buzz.</summary>
    private static PushSeverity SessionSeverity(string outcome) =>
        outcome.Contains("Attention", StringComparison.OrdinalIgnoreCase)
        || outcome.Contains("Blocked", StringComparison.OrdinalIgnoreCase)
        || outcome.Contains("Failed", StringComparison.OrdinalIgnoreCase)
            ? PushSeverity.Alert : PushSeverity.Quiet;

    /// <summary>K5.4 — the run is over, said in the order the owner reads it: what happened, what it
    /// cost against its cap, how much of the plan actually landed, how long it took, and where the
    /// report is. The repo, the branch and the stage ride the context line like every other push, so
    /// none of them is spelled out here a second time.</summary>
    public async Task PushRunCompleteAsync(RunCompletePush push, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(push);
        if (!_started) return;

        var clean = push.SkippedStages.Count == 0;
        var sb = new StringBuilder();
        sb.Append("<b>").Append(clean ? "run complete" : "run complete, with stages skipped").Append("</b>");
        if (push.Duration is { } d) sb.Append(" · ").Append(EscapeHtml(Elapsed(d)));
        sb.AppendLine();

        sb.AppendLine(EscapeHtml(
            $"{push.CheckpointsDone}/{push.CheckpointsTotal} checkpoints · {push.Sessions} session"
            + (push.Sessions == 1 ? "" : "s")));
        if (!clean)
            sb.AppendLine(EscapeHtml($"skipped: {string.Join(", ", push.SkippedStages)}"));

        sb.Append(MoneyLine.ForRun(_state.TotalCostUsd, _plan.Limits.MaxRunCostUsd));

        if (RemoteLinks.Report(Remote(), Branch()) is { } report)
            sb.Append("\n<a href=\"").Append(EscapeHtml(report)).Append("\">the run's report</a>");

        // A finished run is one of the two things worth a buzz — the other is a run that has parked.
        await EnqueueAsync(sb.ToString(), null, PushSeverity.Alert, null, ct).ConfigureAwait(false);
    }

    /// <summary>K5.4 — evidence ARRIVES. K5.3 registered the artifacts and pushed their paths, which
    /// from a phone is a list of file names on a machine the owner is not at; the case the whole item
    /// exists for is a screenshot conductor took, and a path is not a screenshot.
    /// <para>This is the same method with a new BODY, not a second path: every artifact is sent as
    /// itself — <c>sendPhoto</c> for a visual kind, <c>sendDocument</c> otherwise, both decided by
    /// <see cref="TelegramLimits.MethodFor"/> — with the text line it used to push as the caption. A
    /// batch beyond <see cref="EvidenceFilesPerPush"/> would be a flood, so the rest are still
    /// announced as text, which is exactly what they were before.</para></summary>
    public async Task PushEvidenceAsync(IReadOnlyList<EvidenceArtifact> artifacts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (!_started || artifacts.Count == 0) return;

        var sendable = artifacts.Take(EvidenceFilesPerPush).ToList();
        foreach (var a in sendable)
        {
            var absolute = ResolveArtifact(a.Path);
            var caption = EvidenceCaption(a, artifacts.Count);
            if (absolute is null)
            {
                await EnqueueAsync(caption + "\n<i>not attached — the path did not resolve to a file</i>",
                    a.SessionNumber, PushSeverity.Quiet, null, ct).ConfigureAwait(false);
                continue;
            }

            await EnqueueAsync(caption, a.SessionNumber, PushSeverity.Quiet,
                new OutboundAttachment(absolute, EvidenceKinds.IsVisual(a.Kind), caption), ct, a.StageId)
                .ConfigureAwait(false);
        }

        var rest = artifacts.Skip(EvidenceFilesPerPush).ToList();
        if (rest.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"<b>evidence</b> — {rest.Count} further artifact{(rest.Count == 1 ? "" : "s")}, not attached");
        foreach (var a in rest.Take(EvidenceLinesPerPush))
            sb.AppendLine("• " + EvidenceLine(a));
        if (rest.Count > EvidenceLinesPerPush)
            sb.AppendLine($"+{rest.Count - EvidenceLinesPerPush} more");
        await EnqueueAsync(sb.ToString().TrimEnd(), null, PushSeverity.Quiet, null, ct).ConfigureAwait(false);
    }

    /// <summary>The caption that rides the file. Bounded by Telegram's 1024-character caption limit —
    /// a quarter of the message limit — so it is composed short rather than clipped from a body.</summary>
    private string EvidenceCaption(EvidenceArtifact a, int batchSize)
    {
        var sb = new StringBuilder();
        var batch = batchSize > 1 ? $" ({batchSize} new)" : "";
        sb.AppendLine($"<b>evidence</b>{batch}");
        sb.AppendLine("• " + EvidenceLine(a));
        var progress = ProgressLine(a.StageId);
        if (progress.Length > 0) sb.Append(EscapeHtml(progress));
        return sb.ToString().TrimEnd();
    }

    private static string EvidenceLine(EvidenceArtifact a)
    {
        var where = a.CheckpointId is { Length: > 0 } cp ? $" — {cp}" : "";
        return $"{EscapeHtml(a.Path)} ({a.Kind}, {Size(a.Bytes)}){EscapeHtml(where)}";
    }

    /// <summary>An artifact path is repo-relative when the file is inside the repo and absolute when
    /// it is not (K5.3). The wire needs an absolute one, and a path that no longer resolves must
    /// degrade to the text line rather than throwing inside a fire-and-forget push.</summary>
    private string? ResolveArtifact(string path)
    {
        try
        {
            if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
            var joined = Path.GetFullPath(Path.Combine(_plan.Repo, path));
            return File.Exists(joined) ? joined : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    /// <summary>How many artifacts of one batch are sent as files. A watcher sweep that finds thirty
    /// screenshots must not send thirty photos; the rest are announced exactly as K5.3 announced
    /// them.</summary>
    private const int EvidenceFilesPerPush = 4;

    private const int EvidenceLinesPerPush = 8;

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    /// <summary>A rollover runs no gate battery and burns no attempt — that is what a rollover MEANS
    /// (K1.1) — so "(not recorded)" reads as a fault where there is none.</summary>
    private static string GatesLine(SessionEndPush push) =>
        !string.IsNullOrWhiteSpace(push.GateSummary) ? push.GateSummary
        : push.IsRollover ? "deferred — the session rolled over, no attempt burned"
        : "(not recorded)";

    /// <summary>What the session actually put on disk. K1.1 records commits and claims on the
    /// rollover path too; until K5.2 nothing rendered them, so a rollover that had shipped a pull
    /// request pushed a message that said nothing at all.</summary>
    /// <remarks>K5.4: the commits are LINKS when the repo has a remote — a sha in a chat is a string
    /// the owner has to carry back to a machine.</remarks>
    private string LandedLine(SessionEndPush push)
    {
        var parts = new List<string>(2);
        if (push.Commits > 0)
        {
            var count = $"{push.Commits} commit{(push.Commits == 1 ? "" : "s")}";
            var shas = push.CommitShas ?? [];
            parts.Add(shas.Count == 0
                ? count
                : count + " (" + string.Join(", ",
                    shas.Take(CommitLinksPerPush).Select(s => RemoteLinks.Commit(Remote(), s))) +
                    (shas.Count > CommitLinksPerPush ? ", …)" : ")"));
        }
        if (push.NewlyDone.Count > 0) parts.Add($"claimed {EscapeHtml(string.Join(", ", push.NewlyDone))}");
        return parts.Count > 0 ? "landed: " + string.Join(" · ", parts) : "";
    }

    private const int CommitLinksPerPush = 3;

    /// <summary>A duration a human reads at a glance — <c>1h 12m</c>, not <c>01:12:34.567</c>.</summary>
    internal static string Elapsed(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m"
        : $"{(int)d.TotalSeconds}s";

    /// <summary>K5.1's structure, rendered. The caller passes the record WHOLE — cutting it here,
    /// once, is the difference between a bounded message and the same paragraph cut twice.</summary>
    private static string ResultLines(string? resultSummary)
    {
        var parsed = SessionResult.Parse(resultSummary);
        if (!parsed.IsStructured)
        {
            var raw = parsed.ToCompact(TelegramResultMaxChars);
            return raw.Length > 0 ? "result: " + EscapeHtml(raw) : "";
        }

        var sb = new StringBuilder();
        sb.Append("result: <b>").Append(EscapeHtml(parsed.Headline)).Append("</b>");
        foreach (var o in parsed.Outcomes) sb.Append("\n  • ").Append(EscapeHtml(o));
        if (parsed.Gaps.Length > 0) sb.Append("\ngaps: ").Append(EscapeHtml(parsed.Gaps));
        if (parsed.Evidence.Count > 0) sb.Append("\nevidence: ").Append(EscapeHtml(string.Join(", ", parsed.Evidence)));
        return Clip(sb.ToString(), TelegramResultMaxChars);
    }

    /// <summary>The stage as an id AND a title. It was rendered as a bare letter — "— G" — because
    /// the id was passed and the title was never looked up.</summary>
    internal string StageLabel(string stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId)) return "-";
        var title = _plan.Stages.FirstOrDefault(s =>
            string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase))?.Title;
        return string.IsNullOrWhiteSpace(title) ? stageId : $"{stageId} — {Clip(title.Trim(), 64)}";
    }

    /// <summary>Where the run is, in one line. Fifteen messages of the owner's run carried no
    /// checkpoint count, no stage progress and no ETA between them.</summary>
    internal string ProgressLine(string? stageId)
    {
        TrackerSnapshot track;
        try { track = _progress.Read(_plan, CancellationToken.None); }
        catch (IOException) { return ""; }
        catch (InvalidOperationException) { return ""; }

        if (track.Checkpoints.Count == 0) return "";
        var line = $"progress: {track.Checkpoints.Count(c => c.IsDone)}/{track.Checkpoints.Count} checkpoints";

        var stage = string.IsNullOrWhiteSpace(stageId) ? _state.CurrentStage : stageId;
        if (!string.IsNullOrWhiteSpace(stage))
        {
            var rows = track.ForStage(stage).ToList();
            if (rows.Count > 0) line += $" · {stage} {rows.Count(c => c.IsDone)}/{rows.Count}";
        }
        return line;
    }

    private const int TelegramResultMaxChars = 900;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

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
