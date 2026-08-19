using System.Globalization;
using System.Text.Json;

using Conductor.Core.Evidence;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// KS4.5 — the verdict engine's judge limb: spawn the advisory reviewer, record what it said, and
/// record where it disagrees with the measurements.
/// <para><b>The ordering IS the guarantee.</b> <see cref="JudgeSessionAsync"/> is handed the decision
/// that has already been made, and returns only evidence. Nothing on this path calls
/// <see cref="SessionVerdict.Decide"/>, so there is no arrangement of judge output — no score, no
/// verdict word, no crash, no timeout — that can produce a different disposition than the one computed
/// before the second model was spawned. That is asserted three ways in KS4_5JudgeTests rather than
/// trusted: behaviourally over the whole decision table, as a source rule over the decision path, and
/// live through the harness with a judge that condemns a green session and blesses a red one.</para>
/// </summary>
public sealed partial class VerdictEngine
{
    /// <summary>How much of the attempt diff the judge is shown. A review is worth having on the shape
    /// of a change; a review of a 40k-line diff is worth having on the first part of it, and the
    /// alternative — silently spending a model's whole context on generated files — buys nothing.</summary>
    internal const int JudgeDiffMaxChars = 24_000;

    /// <summary>Consult the advisory judge and fold its review into the evidence. Returns
    /// <paramref name="e"/> untouched when no judge is configured, when it could not be read, or when
    /// anything at all goes wrong: a review is a nice-to-have, and the run's grade does not depend on
    /// it — by construction, since the caller has already decided.</summary>
    private async Task<SessionEvidence> JudgeSessionAsync(SessionEvidence e, VerdictDecision decided,
        SessionRecord rec, StageConfig stage, WorkPass w, CancellationToken ct)
    {
        if (_ctx.Plan.Judge is not { Enabled: true } cfg) return e;

        var prompt = _ctx.Prompts.Judge(stage,
            $"{decided.Disposition} — gates {(e.GatesGreen ? "green" : "RED")}, {w.WorkCommits.Count} work commit(s), " +
            $"newly DONE [{string.Join(",", rec.NewlyDone)}]",
            rec.GateSummary ?? "-",
            w.WorkCommits.Count > 0 ? string.Join("; ", w.WorkCommits.Take(8)) : "(none)",
            await AttemptDiffForJudgeAsync(rec, ct).ConfigureAwait(false),
            SessionResult.Parse(rec.ResultSummary).ToCompact(1200),
            cfg.Focus);

        _ctx.Log("consulting judge (advisory — the verdict above is already settled)…");
        var reply = await Judge.ReviewAsync(_ctx.Plan, prompt, _ctx.Log).ConfigureAwait(false);
        // KS5.2's rule: the bill is recorded whether or not the answer was usable.
        _ctx.Ledger.Record(reply.Spend, _ctx.State.SessionCounter, "judge review");

        if (reply.Review is not { } review)
        {
            _ctx.Log("judge produced no readable review — no advisory row this session");
            return e;
        }

        var agreement = review.Against(e.GatesGreen);
        var scoreText = review.Score is { } sc ? sc.ToString(CultureInfo.InvariantCulture) : "-";
        _ctx.Log($"judge review: {review.Verdict} (score {scoreText}) — {AgreementLine(agreement)}. " +
                 "Recorded as evidence; the verdict above is unchanged.");

        rec.JudgeReviewPath = await WriteJudgeArtifactAsync(rec, stage, cfg, review, agreement, e, decided, ct)
            .ConfigureAwait(false);

        var detail = $"{AgreementLine(agreement)}. {review.Summary ?? ""}".Trim();
        if (review.Findings.Count > 0) detail += $" Findings: {string.Join(" | ", review.Findings.Take(3))}";
        return e with
        {
            AdvisoryRows = [.. e.AdvisoryRows,
                new AdvisoryEvidence($"judge:{cfg.Command}", review.Verdict, review.Score, Trunc(detail, 600))],
        };
    }

    /// <summary>The advisory rows, as one line beside the verdict — or null when there are none.
    /// The wording is deliberate and it is the only place a reader meets both at once: the rows are
    /// named as ADVISORY and the sentence says what did decide, because the failure this checkpoint
    /// guards against is a human reading a judge's 12/100 as a grade.</summary>
    internal static string? AdvisoryNote(SessionEvidence e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.AdvisoryRows.Count == 0) return null;
        var rows = string.Join(" · ", e.AdvisoryRows.Select(r =>
            $"{r.Source}: {r.Verdict}{(r.Score is { } sc ? $" {sc.ToString(CultureInfo.InvariantCulture)}/100" : "")}"));
        return $"advisory (recorded, NOT part of the verdict — the gates above decided): {rows}";
    }

    private static string AgreementLine(JudgeAgreement agreement) => agreement switch
    {
        JudgeAgreement.Agrees => "agrees with the deterministic signals",
        JudgeAgreement.Disagrees => "DISAGREES with the deterministic signals",
        _ => "no clear opinion either way",
    };

    /// <summary>The attempt's own diff (KS4.4), clipped. Read here rather than re-derived from git,
    /// because that file is the one artifact that is guaranteed to describe THIS attempt and to exclude
    /// the engine's own writes.</summary>
    private async Task<string> AttemptDiffForJudgeAsync(SessionRecord rec, CancellationToken ct)
    {
        if (rec.AttemptDiffPath is not { Length: > 0 } path) return "(this attempt changed nothing tracked)";
        try
        {
            var text = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : "";
            if (text.Length == 0) return "(this attempt changed nothing tracked)";
            return text.Length <= JudgeDiffMaxChars
                ? text
                : text[..JudgeDiffMaxChars] + $"\n… diff truncated at {JudgeDiffMaxChars} characters …";
        }
        catch (IOException ex)
        {
            _ctx.Log($"judge: attempt diff unreadable ({ex.Message}) — reviewing without it");
            return "(attempt diff unavailable)";
        }
    }

    /// <summary>The review, on disk, in the structured shape it arrived in — plus the measurement it
    /// was compared against, so the artifact can be read years later without the run's state beside it.
    /// Returns null if anything fails: an artifact may never cost a session its verdict.</summary>
    private async Task<string?> WriteJudgeArtifactAsync(SessionRecord rec, StageConfig stage, JudgeConfig cfg,
        JudgeReview review, JudgeAgreement agreement, SessionEvidence e, VerdictDecision decided, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(_ctx.StateDir, "judge");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir,
                $"{stage.Id}-session-{rec.Number.ToString("000", CultureInfo.InvariantCulture)}.json");
            var payload = new
            {
                kind = "advisory-judge-review",
                note = "ADVISORY ONLY (KS4.5). This is one model's opinion, recorded as evidence. " +
                       "No code path lets it change a gate result, a session outcome or a checkpoint's status.",
                session = rec.Number,
                stage = stage.Id,
                judge = cfg.Command,
                verdict = review.Verdict,
                score = review.Score,
                summary = review.Summary,
                findings = review.Findings,
                agreement = agreement.ToString(),
                deterministic = new
                {
                    gatesGreen = e.GatesGreen,
                    disposition = decided.Disposition.ToString(),
                    outcome = decided.Outcome?.ToString(),
                    workCommits = e.WorkCommitCount,
                    newlyDone = rec.NewlyDone,
                },
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _ctx.Log($"judge: review could not be written ({ex.Message}) — the row survives, the artifact does not");
            return null;
        }
    }
}
