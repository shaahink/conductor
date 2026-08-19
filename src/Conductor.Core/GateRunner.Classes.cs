using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// The gate CLASSES, apart from the battery that runs them. Everything here answers the same
/// question in two different ways — <i>this gate exited 0; is it green?</i> — and none of it knows
/// how a gate is spawned, retried, cached or labelled, which is what the other half of
/// <see cref="GateRunner"/> is about.
/// </summary>
public static partial class GateRunner
{
    /// <summary>KS4.2: the whole of the PASS-TO-PASS decision for one passing regression gate —
    /// compare against the baseline, report what was lost, and advance the baseline only if nothing
    /// was. Separated from the battery loop because the set arithmetic is the part worth reading.
    /// </summary>
    /// <remarks>With no store there is nothing to compare against (an ad-hoc <c>conductor gate</c>
    /// run): the gate reports its count and the class degrades to a measurement with no memory,
    /// which is said out loud rather than passing as a clean comparison.</remarks>
    private static GateResult ApplyRegressionClass(
        GateConfig gate, GateResult result, IRunStore? db, string? runId, string? headSha, Action<string>? onProgress)
    {
        if (result.PassSet.Count == 0)
        {
            onProgress?.Invoke($"gate {gate.Name}: {GateClass.Glyph} — {GateClass.EmptyPassSetNotice}");
            return result with { RegressionNote = GateClass.EmptyPassSetNotice };
        }

        if (db is null || runId is null)
        {
            onProgress?.Invoke($"gate {gate.Name}: {result.PassSet.Count} checks passed — no run store, so nothing to compare them against");
            return result;
        }

        var baseline = db.GetGatePassSet(runId, gate.Name);
        if (baseline is null)
        {
            db.RecordGatePassSet(runId, gate.Name, headSha, result.PassSet);
            onProgress?.Invoke($"gate {gate.Name}: {result.PassSet.Count} checks passed — first sighting, recorded as the PASS-TO-PASS baseline");
            return result;
        }

        var lost = LostChecks(baseline, result.PassSet);
        if (lost.Count == 0)
        {
            db.RecordGatePassSet(runId, gate.Name, headSha, result.PassSet);
            var gained = result.PassSet.Count - baseline.Count;
            onProgress?.Invoke($"gate {gate.Name}: {result.PassSet.Count} checks passed, all {baseline.Count} in the baseline still pass" +
                               (gained > 0 ? $" (+{gained} new)" : ""));
            return result;
        }

        // Deliberately NOT recorded: see the anti-laundering note on GateClass. The baseline stays
        // where it was, so the next session is asked the same question this one answered wrong.
        onProgress?.Invoke($"gate {gate.Name}: {GateClass.Glyph} — the gate exited 0, but {lost.Count} check(s) " +
                           $"that passed before no longer pass: {Names(lost)}");
        return result with { Regressions = lost };
    }

    /// <summary>KS4.3 — the whole of the mutation decision for one passing mutation gate: work out
    /// which files this branch changed, score the gate's report over exactly those, and compare the
    /// result to the plan's threshold.</summary>
    /// <remarks>
    /// <para>Three outcomes, and the middle one is the one worth naming. <b>Nothing to score</b> — the
    /// branch changed no mutable source — carries no finding and is green, because a docs checkpoint
    /// has no mutants and pretending otherwise would teach everyone to ignore the class. <b>Nothing
    /// readable</b> — it changed mutable source and the report scores none of it — is RED, because
    /// that is what a stale report, a mis-pointed path and a narrowed mutate glob all look like, and
    /// none of them is distinguishable from a perfect score by exit code. <b>A number</b> is compared
    /// to the bar.</para>
    /// </remarks>
    internal static async Task<GateResult> ApplyMutationClassAsync(
        PlanConfig plan, GateConfig gate, GateResult result, Action<string>? onProgress, CancellationToken ct)
    {
        if (gate.Mutation is not { } cfg) return result;
        var cwd = ResolveCwd(plan, gate);

        IReadOnlyCollection<string>? scope = null;
        if (!cfg.WholeReport)
        {
            var changed = Git.ChangedFiles(plan.Repo, cfg.BaseRev)
                .Where(MutationConfig.IsMutableSource).ToList();
            if (changed.Count == 0)
            {
                onProgress?.Invoke($"gate {gate.Name}: nothing to score — this branch changed no mutable source against {cfg.BaseRev}");
                return result;
            }
            scope = changed;
        }

        var score = await MutationReportReader.ReadFileAsync(cfg, cwd, scope, ct).ConfigureAwait(false);
        var where = MutationReportReader.Locate(cfg, cwd) ?? cfg.Path;
        if (score is null || score.Counted == 0)
        {
            var note = $"{GateClass.UnreadableMutationNotice} (report: {where}" +
                       (scope is null ? ")" : $"; changed files: {Names(scope.ToList())})");
            onProgress?.Invoke($"gate {gate.Name}: {GateClass.MutationGlyph} — {note}");
            return result with { Mutation = new MutationFinding(null, cfg.Threshold, 0, 0, 0, [], note) };
        }

        var finding = new MutationFinding(score.Percent, cfg.Threshold, score.Counted, score.Survived,
            score.NoCoverage, score.Survivors.Select(s => s.ToString()).ToList(), null);
        if (finding.IsShortfall)
            onProgress?.Invoke($"gate {gate.Name}: {GateClass.MutationGlyph} — the gate exited 0, but only " +
                               $"{score.Percent:0.##}% of {score.Counted} mutants in the changed files were killed " +
                               $"(threshold {cfg.Threshold:0.##}%); {score.Survivors.Count} survived");
        else
            onProgress?.Invoke($"gate {gate.Name}: mutation score {score.Percent:0.##}% over {score.Counted} mutants " +
                               $"in {score.ScoredFiles.Count} changed file(s) — clears {cfg.Threshold:0.##}%");
        return result with { Mutation = finding };
    }

    /// <summary>KS4.2, the set difference the class is: baseline names absent from the current pass
    /// set. A rename is a loss and a deletion is a loss, because from here they are the same event —
    /// the check that was passing is not passing now, under any name this gate reports.</summary>
    public static IReadOnlyList<string> LostChecks(IEnumerable<string> baseline, IEnumerable<string> current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        var now = new HashSet<string>(current, StringComparer.Ordinal);
        return baseline.Where(b => !now.Contains(b)).Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.Ordinal).ToList();
    }

    /// <summary>Lost checks for a log line: enough names to act on, never the whole of a suite.</summary>
    internal static string Names(IReadOnlyList<string> lost, int max = 10)
        => string.Join(", ", lost.Take(max)) + (lost.Count > max ? $" (and {lost.Count - max} more)" : "");

    /// <summary>The class finding for a gate that is red for one, or null when its redness is an
    /// ordinary exit code. One accessor rather than the same two-branch test at each of the six
    /// surfaces that render a battery — which is how four of them came to render none of it.</summary>
    public static string? ClassDetail(GateResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.HasRegressions ? RegressionDetail(r) : r.HasMutationShortfall ? MutationDetail(r) : null;
    }

    /// <summary>KS4.2's distinct reporting, in the words both fix-brief renderers use — this one and
    /// <see cref="GateFailureSpill"/>, which is the one an ordinary run actually goes through.</summary>
    internal static string RegressionDetail(GateResult r)
    {
        if (r.RegressionNote is { } note)
            return $"### Gate `{r.Name}` — {GateClass.Glyph} CLASS\n```\n{note}\n```";
        var shown = string.Join("\n", r.Regressions.Take(50).Select(c => "  - " + c));
        var more = r.Regressions.Count > 50 ? $"\n  … and {r.Regressions.Count - 50} more" : "";
        return $"### Gate `{r.Name}` — {GateClass.Glyph} CLASS (PASS-TO-PASS): the gate EXITED 0, and " +
               $"{r.Regressions.Count} check(s) that passed earlier in this run no longer pass.\n```\n{shown}{more}\n```\n" +
               "These are not new failures — they are checks that have stopped being reported as passing at all " +
               "(deleted, renamed, skipped, filtered out of the run, or excluded from the project). Restore them, " +
               "or the delivery is not a delivery. The baseline was NOT advanced, so this will be asked again.";
    }

    /// <summary>KS4.3 — the fix brief a mutation shortfall writes. Like a regression it has no failing
    /// assertion to paste, so the block has to carry the whole finding: the score, the bar, and the
    /// surviving mutants by file and line, which are the exact places a test asserts nothing.</summary>
    internal static string MutationDetail(GateResult r)
    {
        if (r.Mutation is not { } m) return "";
        if (m.Note is { } note)
            return $"### Gate `{r.Name}` — {GateClass.MutationGlyph} CLASS\n```\n{note}\n```";
        var shown = string.Join("\n", m.Survivors.Take(50).Select(c => "  - " + c));
        var more = m.Survivors.Count > 50 ? $"\n  … and {m.Survivors.Count - 50} more" : "";
        return $"### Gate `{r.Name}` — {GateClass.MutationGlyph} CLASS (mutation score): the gate EXITED 0, and " +
               $"the suite killed {m.Score:0.##}% of the {m.Counted} mutants planted in the files this branch " +
               $"changed — the bar is {m.Threshold:0.##}%.\n```\n{shown}{more}\n```\n" +
               $"{m.Survived} mutant(s) survived a passing test run and {m.NoCoverage} were never executed at all. " +
               "Each line above is a change to the implementation that NO test noticed. This is not a coverage " +
               "number and cannot be raised by executing more lines: add or strengthen assertions until a broken " +
               "implementation makes a test go red, or delete the code no behaviour depends on.";
    }
}
