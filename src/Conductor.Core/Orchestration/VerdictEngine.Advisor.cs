using System.Text;

using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// The advisor limb of the verdict engine: consulting the LLM advisor, applying (or defaulting past)
/// its verdict, running remediation, skipping a stage, and writing verifier follow-ups. Split out of
/// <c>VerdictEngine.Phase.cs</c>, which had grown past its 500-line ceiling before SC2.4 touched it.
/// Behaviour is unchanged - this is a move, not a rewrite.
/// </summary>
public sealed partial class VerdictEngine
{
    // ── advisor helpers (private) ──

    private async Task<AdvisorVerdict?> ConsultAdvisorAsync(SessionRecord? rec, StageConfig stage, TrackerSnapshot track, string outcome)
    {
        var prompt = _ctx.Prompts.Advisor(stage,
            outcome + (rec?.Outcome != null ? $" (last session: {rec.Outcome})" : ""),
            rec?.GateSummary ?? "-",
            rec != null ? string.Join("; ", rec.NewCommits.Take(6)) : "-",
            Trunc(track.HandoffBlock, 1200),
            // K5.1: whole fields, dropped from the bottom, instead of 1200 characters cut mid-word.
            SessionResult.Parse(rec?.ResultSummary).ToCompact(1200),
            _ctx.State.AttemptsThisStage, MaxAttempts(stage));
        _ctx.Log("consulting advisor\u2026");
        var started = DateTime.UtcNow;
        var v = await Advisor.ConsultAsync(_ctx.Plan, prompt, _ctx.Log).ConfigureAwait(false);
        var elapsed = DateTime.UtcNow - started;
        _ctx.Log(v != null ? $"advisor verdict: {v.Action} — {v.Reason}" : "advisor unavailable — using deterministic default");
        if (_ctx.Store is { } store)
        {
            var c = 0.0005m * (decimal)elapsed.TotalSeconds;
            store.RecordCost(_ctx.State.RunId, _ctx.State.SessionCounter, "advisor", 0, 0, 0, 0, c, (long)elapsed.TotalMilliseconds);
            _ctx.RunOverheadUsd += c; _ctx.PersistBudget();
        }
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
                _ctx.State.AttemptsThisStage = MaxAttempts(stage);
                NeedsHuman($"advisor blocked retry: {verdict?.Reason ?? "stall pattern or repeated failure — human must clear before next attempt"}");
                break;
            case AdvisorAction.ResetBudget:
                _ctx.Log($"advisor reset budget: {verdict?.Reason}");
                _ctx.State.AttemptsThisStage = 0;
                _ctx.State.PendingFix = null;
                _ctx.State.PendingResume = null;
                _ctx.Save();
                break;
            case AdvisorAction.ApplyFix:
                _ctx.Log($"advisor apply-fix: {verdict?.Reason}");
                await RunRemediationAsync(verdict?.Reason ?? "advisor requested remediation").ConfigureAwait(false);
                _ctx.State.AttemptsThisStage = Math.Max(0, _ctx.State.AttemptsThisStage - 1);
                break;
            case AdvisorAction.RerunGates:
                _ctx.Log($"advisor rerun-gates: {verdict?.Reason} — clearing pending fix, gates will determine next step");
                _ctx.State.PendingFix = null;
                _ctx.State.Status = RunStatus.Idle;
                _ctx.Save();
                break;
            default:
                break;
        }
    }

    private async Task RunRemediationAsync(string reason)
    {
        var script = _ctx.Plan.Advisor?.RemediationScript;
        if (string.IsNullOrWhiteSpace(script))
        {
            _ctx.Log("remediation: no remediation script configured — skipping");
            return;
        }
        try
        {
            _ctx.Log($"remediation: running script — {script[..Math.Min(script.Length, 120)]}");
            var shell = string.IsNullOrWhiteSpace(ProcessRunner.DefaultShell) ? "powershell" : ProcessRunner.DefaultShell;
            var r = await ProcessRunner.RunShellAsync(shell, script, _ctx.Plan.Repo, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            _ctx.Log($"remediation: script exited {r.ExitCode} in {r.Duration.TotalSeconds:0}s{(r.TimedOut ? " (timed out)" : "")}");
            if (r.ExitCode != 0)
                _ctx.Log($"remediation: script non-zero exit — {r.Output[..Math.Min(r.Output.Length, 200)]}");
        }
        catch (Exception ex)
        {
            _ctx.Log($"remediation: script failed — {ex.Message}");
        }
    }

    internal void SkipStage(StageConfig stage, string why)
    {
        if (!_ctx.State.SkippedStages.Contains(stage.Id)) _ctx.State.SkippedStages.Add(stage.Id);
        _ctx.State.PendingFix = null;
        _ctx.State.PendingResume = null;
        _ctx.State.AttemptsThisStage = 0;
        _ctx.Log($"\u26a0 stage {stage.Id} SKIPPED ({why}) — flagged for human review in the report");
        _saveAndReport();
    }

    // SF0.1 / bug 11: ShouldVerify lived here and was called from nowhere after M3.1 gave the
    // next-step decision to the workflow — it was the ONLY reader of plan.verifyEachDelivery, which
    // is how that key came to be settable and inert. The key now enters the live decision at the
    // bottom of QaPolicyExtensions.EffectiveSkipVerification; a private method nothing calls is not
    // a reader, so it is gone rather than left to imply one.

#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
    private void WriteVerifierFollowups(string stageId, VerifierVerdict verdict)
    {
        var followupsPath = Path.Combine(_ctx.StateDir, "followups.md");
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

        _ctx.Log($"wrote {verdict.Findings.Count} verifier finding(s) to {followupsPath}");
    }
#pragma warning restore MA0045

}
