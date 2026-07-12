using System.Text;
using Conductor.Core.Integrations;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Core;

public sealed partial class Orchestrator
{
    private async Task<bool> EscalateExhaustedStageAsync(StageConfig stage, TrackerSnapshot track, int maxAttempts)
    {
        Log($"stage {stage.Id} exhausted its attempt budget ({maxAttempts}) — consulting advisor");
        var last = state.History.LastOrDefault();
        var verdict = await ConsultAdvisorAsync(last, stage, track, $"attempt budget exhausted ({maxAttempts})").ConfigureAwait(false);
        if (verdict?.Action is AdvisorAction.Skip)
        {
            SkipStage(stage, $"advisor: {verdict.Reason}");
            return false;
        }
        if (verdict?.Action is AdvisorAction.Retry or AdvisorAction.Resume or AdvisorAction.ResetBudget)
        {
            Log($"advisor says {verdict.Action} ({verdict.Reason}) — granting {stage.Sessions} more attempts");
            state.AttemptsThisStage = maxAttempts - Math.Max(1, stage.Sessions);
            Save();
            return true;
        }
        NeedsHuman($"stage {stage.Id} used all {maxAttempts} attempts without completing — inspect and `conductor resume` (or `conductor skip`)" +
                   (verdict != null ? $" · advisor: {verdict.Reason}" : ""));
        return false;
    }

    private async Task<AdvisorVerdict?> ConsultAdvisorAsync(SessionRecord? rec, StageConfig stage, TrackerSnapshot track, string outcome)
    {
        var prompt = _prompts.Advisor(stage,
            outcome + (rec?.Outcome != null ? $" (last session: {rec.Outcome})" : ""),
            rec?.GateSummary ?? "-",
            rec != null ? string.Join("; ", rec.NewCommits.Take(6)) : "-",
            Trunc(track.HandoffBlock, 1200),
            Trunc(rec?.ResultSummary ?? "", 1200),
            state.AttemptsThisStage, MaxAttempts(stage));
        Log("consulting advisor…");
        var v = await Advisor.ConsultAsync(plan, prompt, Log).ConfigureAwait(false);
        Log(v != null ? $"advisor verdict: {v.Action} — {v.Reason}" : "advisor unavailable — using deterministic default");
        return v;
    }

    private async Task ApplyVerdictAsync(AdvisorVerdict? verdict, SessionRecord rec, StageConfig stage, AdvisorAction defaultAction)
    {
        var action = verdict?.Action ?? defaultAction;
        switch (action)
        {
            case AdvisorAction.Resume:
                QueueResume(rec, "advisor requested resume", force: true);
                break;
            case AdvisorAction.Skip:
                SkipStage(stage, $"advisor: {verdict?.Reason}");
                break;
            case AdvisorAction.NeedsHuman:
                NeedsHuman($"advisor: {verdict?.Reason ?? "human intervention required"}");
                break;
            case AdvisorAction.BlockRetry:
                state.AttemptsThisStage = MaxAttempts(stage);
                NeedsHuman($"advisor blocked retry: {verdict?.Reason ?? "stall pattern or repeated failure — human must clear before next attempt"}");
                break;
            case AdvisorAction.ResetBudget:
                Log($"advisor reset budget: {verdict?.Reason}");
                state.AttemptsThisStage = 0;
                state.PendingFix = null;
                state.PendingResume = null;
                Save();
                break;
            case AdvisorAction.ApplyFix:
                Log($"advisor apply-fix: {verdict?.Reason}");
                await RunRemediationAsync(verdict?.Reason ?? "advisor requested remediation").ConfigureAwait(false);
                state.AttemptsThisStage = Math.Max(0, state.AttemptsThisStage - 1);
                break;
            case AdvisorAction.RerunGates:
                Log($"advisor rerun-gates: {verdict?.Reason} — clearing pending fix, gates will determine next step");
                state.PendingFix = null;
                state.Status = RunStatus.Idle;
                Save();
                break;
            default:
                break;
        }
    }

    private async Task RunRemediationAsync(string reason)
    {
        var script = plan.Advisor?.RemediationScript;
        if (string.IsNullOrWhiteSpace(script))
        {
            Log("remediation: no remediation script configured — skipping");
            return;
        }
        try
        {
            Log($"remediation: running script — {script[..Math.Min(script.Length, 120)]}");
            var shell = string.IsNullOrWhiteSpace(ProcessRunner.DefaultShell) ? "powershell" : ProcessRunner.DefaultShell;
            var r = await ProcessRunner.RunShellAsync(shell, script, plan.Repo, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            Log($"remediation: script exited {r.ExitCode} in {r.Duration.TotalSeconds:0}s{(r.TimedOut ? " (timed out)" : "")}");
            if (r.ExitCode != 0)
                Log($"remediation: script non-zero exit — {r.Output[..Math.Min(r.Output.Length, 200)]}");
        }
        catch (Exception ex)
        {
            Log($"remediation: script failed — {ex.Message}");
        }
    }

    private void QueueResume(SessionRecord rec, string reason, bool countResume = true, bool force = false)
    {
        state.PendingResume = new PendingResume
        {
            FromSession = rec.Number,
            ClaudeSessionId = rec.ClaudeSessionId,
            Reason = reason,
            ResumeCount = rec.ResumeCount + (countResume ? 1 : 0),
        };
        if (force) state.PendingResume.ResumeCount = Math.Min(state.PendingResume.ResumeCount, plan.Limits.MaxResumesPerSession - 1);
    }

    private void SkipStage(StageConfig stage, string why)
    {
        if (!state.SkippedStages.Contains(stage.Id)) state.SkippedStages.Add(stage.Id);
        state.PendingFix = null;
        state.PendingResume = null;
        state.AttemptsThisStage = 0;
        Log($"⚠ stage {stage.Id} SKIPPED ({why}) — flagged for human review in the report");
        SaveAndReport();
    }

    private bool ShouldVerify(SessionRecord rec)
    {
        return rec.Kind == SessionKind.Deliver && plan.VerifyEachDelivery;
    }

#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
    private void WriteVerifierFollowups(string stageId, VerifierVerdict verdict)
    {
        var followupsPath = Path.Combine(plan.StateDir, "followups.md");
        var existing = FollowupParser.Read(followupsPath);
        var maxId = existing
            .Select(e => e.Id)
            .Where(id => id.StartsWith("FU-", StringComparison.Ordinal))
            .Select(id =>
            {
                var parts = id.Split('-');
                return parts.Length >= 3 && int.TryParse(parts[^1], out var n) ? n : 0;
            })
            .DefaultIfEmpty(0)
            .Max();

        var lines = new StringBuilder();
        lines.AppendLine("| id | item | detail | owning stage | status |");
        lines.AppendLine("|---|---|---|---|---|");
        foreach (var finding in verdict.Findings)
        {
            maxId++;
            var id = $"FU-F4-{maxId:00}";
            var item = finding.Length > 80 ? finding[..77] + "..." : finding;
            lines.AppendLine($"| {id} | {item} | {finding} | {stageId} | OPEN |");
        }

        if (File.Exists(followupsPath))
        {
            var content = "\n" + lines.ToString();
            File.AppendAllText(followupsPath, content, Encoding.UTF8);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(followupsPath)!);
            File.WriteAllText(followupsPath, "# Follow-ups\n\n" + lines.ToString(), Encoding.UTF8);
        }

        Log($"wrote {verdict.Findings.Count} verifier finding(s) to {followupsPath}");
    }
#pragma warning restore MA0045
}
