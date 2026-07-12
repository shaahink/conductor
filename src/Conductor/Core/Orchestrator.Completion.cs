using System.Text;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Core;

public sealed partial class Orchestrator
{
    private async Task<bool> ConfirmCompletionAsync(CancellationToken ct)
    {
        var lastOutcome = state.History.LastOrDefault()?.Outcome;
        if (_lastGates != null && GateRunner.AllRequiredPassed(_lastGates) &&
            lastOutcome is SessionOutcome.Advanced or SessionOutcome.Progress)
            return true;

        Log("tracker reports all checkpoints DONE — running the gate battery to confirm before closing the plan");
        state.Status = RunStatus.VerifyingGates;
        Save();
        PushIdleSnapshot();
        var gates = await RunGateBatteryAsync(ct).ConfigureAwait(false);
        _lastGates = gates;
        state.Status = RunStatus.Idle;
        EmitGates(gates, "completion");
        _runOverheadUsd += gates.Sum(g => g.EstimatedCostUsd(plan.Limits.OverheadCostPerSecond));
        state.PerRunOverheadCostUsd = _runOverheadUsd;
        if (GateRunner.AllRequiredPassed(gates)) return true;

        state.AttemptsThisStage++;
        state.PendingFix = new PendingFix
        {
            FromSession = state.History.LastOrDefault()?.Number ?? 0,
            GateFailures = GateRunner.FailureDetails(gates),
            ProgressSummary = "tracker claims all checkpoints DONE, but the gate battery is red — the claims are not yet true",
        };
        Log("completion NOT confirmed — gates red; queuing a fix session");
        Save();
        return false;
    }

    private void CompletePlan(TrackerSnapshot track)
    {
        state.Status = RunStatus.Completed;
        state.AttentionReason = state.SkippedStages.Count > 0
            ? $"plan complete EXCEPT skipped stages: {string.Join(", ", state.SkippedStages)}"
            : null;
        Log($"🎉 plan '{plan.Name}' complete — {track.Checkpoints.Count(c => c.IsDone)}/{track.Checkpoints.Count} checkpoints done");
        events.Emit(new RunFinished
        {
            Status = state.Status.ToString(),
            Sessions = state.SessionCounter,
            CheckpointsDone = track.Checkpoints.Count(c => c.IsDone),
            CheckpointsTotal = track.Checkpoints.Count,
        });
        _runDb?.RecordRunEnd(state.RunId, state.Status.ToString());
        SaveAndReport();
        Notify($"Conductor: plan {plan.Name} COMPLETE ({state.SessionCounter} sessions)");
    }

    private void NeedsHuman(string reason)
    {
        state.Status = RunStatus.NeedsHuman;
        state.AttentionReason = reason;
        events.Emit(new AttentionRequested { Reason = reason });
        Log($"🛑 NEEDS HUMAN: {reason}");
        SaveAndReport();
        Notify($"Conductor {plan.Name}: needs attention — {reason}");
        _ = telegram.PushWithKeyboardAsync(reason,
        [
            ("Resume", "resume"),
            ("Skip Stage", "skip"),
            ("Inject…", "inject:needsHuman"),
            ("Chat", "chat:needsHuman"),
        ]);
    }

    private bool IdenticalStallPattern(SessionRecord rec)
    {
        if (rec.NewCommits is { Count: > 0 }) return false;
        var summary = rec.ResultSummary?.Trim();
        if (!string.IsNullOrEmpty(summary)) return false;

        var stalledCount = 1;
        for (var i = state.History.Count - 2; i >= 0; i--)
        {
            var prev = state.History[i];
            if (prev.Outcome != SessionOutcome.Stalled) break;
            if (prev.NewCommits is { Count: 0 } && string.IsNullOrEmpty(prev.ResultSummary?.Trim()))
            {
                stalledCount++;
                if (stalledCount >= 2) return true;
            }
            else break;
        }
        return false;
    }

    private void ReflectionStep(SessionRecord rec)
    {
        if (string.IsNullOrWhiteSpace(rec.ResultSummary)) return;

        var text = rec.ResultSummary;
        var idx = text.IndexOf("SESSION-RESULT:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        var difficulty = text[(idx + "SESSION-RESULT:".Length)..].Trim();
        if (difficulty.Length == 0) return;
        if (difficulty.Length > 500)
            difficulty = difficulty[..497] + "…";

        _lessons.Append(rec.Stage, rec.Number, difficulty);
    }

#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
    private void ParseAuditFollowups(string stageId)
    {
        var handoverPath = Path.Combine(plan.StateDir, "handovers", $"{stageId}.md");
        if (!File.Exists(handoverPath)) return;

        var followupsPath = Path.Combine(plan.StateDir, "followups.md");
        var existing = File.Exists(followupsPath) ? File.ReadAllText(followupsPath, Encoding.UTF8) : "";

        var bullets = new List<string>();
        try
        {
            var content = File.ReadAllText(handoverPath, Encoding.UTF8);
            var lines = content.Split('\n');
            var inSection = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.StartsWith("## ", StringComparison.Ordinal) || t.StartsWith("### ", StringComparison.Ordinal))
                {
                    var heading = t.ToLowerInvariant();
                    inSection = heading.Contains("weak", StringComparison.Ordinal) || heading.Contains("deferred", StringComparison.Ordinal) ||
                                heading.Contains("bugs not fixed", StringComparison.Ordinal) || heading.Contains("unfixed", StringComparison.Ordinal) ||
                                heading.Contains("concrete follow", StringComparison.Ordinal);
                }
                else if (inSection && (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)
                         || (t.StartsWith("### ", StringComparison.Ordinal) && t.Contains("D-"))))
                {
                    var bullet = t.TrimStart('-', '*', ' ').Trim();
                    if (bullet.Length > 0) bullets.Add(bullet);
                }
            }
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        if (bullets.Count == 0) return;

        var sb = new StringBuilder();
        var prevExists = existing.Length > 0;
        if (!prevExists)
        {
            sb.AppendLine("# Conductor followups (auto-tracked from audit handovers)");
            sb.AppendLine();
            sb.AppendLine("| Id | Item | Stage | Status |");
            sb.AppendLine("|---|---|---|---|");
        }
        else
        {
            sb.Append(existing.TrimEnd());
        }

        var added = 0;
        var sid = stageId;
        foreach (var bullet in bullets)
        {
            var title = bullet.Length > 80 ? bullet[..77] + "…" : bullet;
            if (existing.Contains(title, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!prevExists && added == 0)
                sb.AppendLine();
            sb.AppendLine($"| FU-{sid}-{added + 1:00} | {title} | {sid} | OPEN |");
            added++;
        }

        if (added > 0)
        {
            File.WriteAllText(followupsPath, sb.ToString().TrimEnd() + Environment.NewLine, Encoding.UTF8);
            Log($"followups: {added} new item(s) from {stageId} audit tracked in followups.md");
        }
    }
#pragma warning restore MA0045
}
