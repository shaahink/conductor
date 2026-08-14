using System.Globalization;

using Conductor.Core.History;

using Microsoft.Data.Sqlite;

namespace Conductor.Core.Budget;

/// <summary>KS5.3 — how a configured budget stands against what the sessions actually measured.
/// Six answers, because "fine" and "cannot tell" are not the same answer and a surface that spells
/// them the same word is how a run whose floor was never measured came to read as agreement.</summary>
public enum BudgetAgreement
{
    /// <summary>No ceiling is configured, so there is nothing to disagree with.</summary>
    NoCeiling,

    /// <summary>No database, no run, or the read failed. Nothing is known — which is not agreement.</summary>
    CannotMeasure,

    /// <summary>Sessions exist and none of them closed a checkpoint: there is no floor to compare a
    /// ceiling against, and prescribing one from nothing would be a guess wearing a measurement's
    /// clothes.</summary>
    NoFloor,

    /// <summary>The ceiling clears the floor and the nudge clears the median closing session.</summary>
    Agrees,

    /// <summary>The ceiling sits under the smallest session that ever closed a checkpoint. Nothing can
    /// land in one session and the total rises — the failure that made one run worse than uncapped.</summary>
    CapBelowFloor,

    /// <summary>The nudge fires under the median closing session, so it interrupts every session
    /// before it could have finished on its own terms.</summary>
    NudgeBelowMedian,
}

/// <summary>KS5.3 — one comparison of a configured budget against a measured one, and the one
/// sentence that states it. <see cref="Sentence"/> is what doctor prints and what the reload logs:
/// the same words for the same numbers, because they come from the same place.</summary>
/// <param name="Agreement">Which of the six answers this is.</param>
/// <param name="Configured">The budget as configured, e.g. "cap 12M / nudge 9M". Empty when there is
/// no ceiling, because then there is nothing to quote.</param>
/// <param name="Detail">What the measurement says about it.</param>
/// <param name="Measured">The profile the comparison was made against, null when none could be.</param>
public sealed record BudgetVerdict(
    BudgetAgreement Agreement, string Configured, string Detail, BudgetProfile? Measured)
{
    /// <summary>True only for a real contradiction. "Cannot measure" is not a disagreement, and a
    /// surface that treated it as one would cry wolf on every run that has not closed anything yet.</summary>
    public bool Disagrees => Agreement is BudgetAgreement.CapBelowFloor or BudgetAgreement.NudgeBelowMedian;

    /// <summary>The sentence. One string, so doctor and the reload cannot word it differently.</summary>
    public string Sentence => Configured.Length == 0 ? Detail : $"{Configured} — {Detail}";

    /// <summary>The doctor check state this verdict warrants: only a contradiction warns.</summary>
    public string DoctorState => Disagrees ? "warn" : "ok";
}

/// <summary>
/// KS5.3 — the floor-versus-ceiling comparison, lifted out of <c>doctor</c> so the plan reload can
/// make it too.
/// <para>It was doctor's alone, and doctor runs before a run starts. The setting it checks is the one
/// most often edited MID-run: an operator parks the run, types a new <c>maxSessionTokens</c> into the
/// plan file, reloads, and the engine answered with the number it had just read back — never with
/// whether that number can hold a session of this run's own work. A ceiling under the floor is not a
/// tighter budget, it is a run that cannot land anything, and the boundary is the last moment before
/// it starts spending under it.</para>
/// <para>Pure and static, like <see cref="BudgetAnalyzer"/>: the verb, doctor, the reload and the
/// tests all call the same function over the same records. The measurement is separated from the
/// comparison on purpose — doctor measures the best run in a shared database, the reload measures the
/// run it is standing in, and both then ask the SAME question about the answer.</para>
/// </summary>
public static class BudgetDisagreement
{
    /// <summary>
    /// The comparison. <paramref name="measurable"/> is the caller's answer to "was there a database
    /// to read at all" — false makes this <see cref="BudgetAgreement.CannotMeasure"/> rather than
    /// letting an absent history read as a floor of zero, which every ceiling clears.
    /// </summary>
    public static BudgetVerdict Compare(
        long? maxSessionTokens, double? softBreakRatio, BudgetProfile? measured, bool measurable)
    {
        var cap = maxSessionTokens is { } c and > 0 ? c : (long?)null;
        if (cap is null)
            return new BudgetVerdict(BudgetAgreement.NoCeiling, "",
                "no session ceiling — sessions run until the agent stops", null);

        // The rail's own arithmetic, not a copy of it: SoftBreak.Threshold applies the same unset-ratio
        // fallback the session runner does, so no surface can describe a nudge the rail would not fire.
        var nudge = SoftBreak.Threshold(cap, softBreakRatio)!.Value;
        var configured = $"cap {BudgetAnalyzer.Millions(cap.Value)} / nudge {BudgetAnalyzer.Millions(nudge)}";

        if (!measurable)
            return new BudgetVerdict(BudgetAgreement.CannotMeasure, configured,
                "no history yet to measure it against", null);

        if (measured is null || measured.Current.Closers == 0)
            return new BudgetVerdict(BudgetAgreement.NoFloor, configured,
                "no session has closed a checkpoint yet, so there is no floor to measure it against", measured);

        var w = measured.Current;
        if (cap.Value < w.Floor)
            return new BudgetVerdict(BudgetAgreement.CapBelowFloor, configured,
                $"the cap is BELOW the measured {BudgetAnalyzer.Millions(w.Floor)} session floor. " +
                $"Nothing will land in one session. {measured.Prescription.Verdict}", measured);

        if (nudge < w.ClosingMedian)
            return new BudgetVerdict(BudgetAgreement.NudgeBelowMedian, configured,
                $"the nudge is {(nudge / (double)w.ClosingMedian).ToString("0.00", CultureInfo.InvariantCulture)}x " +
                $"the {BudgetAnalyzer.Millions(w.ClosingMedian)} median closing session, so it fires before a typical session could have finished. " +
                measured.Prescription.Verdict, measured);

        return new BudgetVerdict(BudgetAgreement.Agrees, configured,
            $"clears the {BudgetAnalyzer.Millions(w.Floor)} floor and the " +
            $"{BudgetAnalyzer.Millions(w.ClosingMedian)} median closing session ({w.Closers} measured). " +
            "conductor budget for the full profile", measured);
    }

    /// <summary>
    /// Doctor's measurement: the best run in a database that may hold many. Oldest first, and a run of
    /// THIS plan beats an unrelated one sharing the file — the check must describe the budget these
    /// sessions will run under, not whatever ran here last year. Null when nothing in the database
    /// ever closed a checkpoint.
    /// </summary>
    public static BudgetProfile? MeasureForPlan(RunArchive? archive, string? planName)
    {
        if (archive is null) return null;
        try
        {
            BudgetProfile? measured = null;
            var runs = archive.Runs()
                .OrderBy(r => string.Equals(r.PlanName, planName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(r => r.StartedUtc, StringComparer.Ordinal)
                .ToList();
            foreach (var run in runs)
            {
                var sessions = archive.Sessions(run.RunId);
                if (sessions.Count == 0) continue;
                var profile = BudgetAnalyzer.Analyze(run.RunId, run.PlanName, sessions, archive.SoftBreaks(run.RunId));
                if (profile.Current.Closers > 0) measured = profile;
            }
            return measured;
        }
        catch (SqliteException)
        {
            // Same contract SoftBreaks/ContextFromEvents keep: nothing to report is a valid answer, and
            // Compare turns it into "cannot measure" rather than into agreement.
            return null;
        }
    }

    /// <summary>
    /// The reload's measurement: one run, its own. A live run must be judged against what IT has been
    /// spending, not against a neighbour in the same database that happened to close bigger sessions.
    /// A run with no sessions yet measures cleanly to zero closers, which reads as "no floor".
    /// </summary>
    public static BudgetProfile? MeasureRun(RunArchive? archive, string runId)
    {
        if (archive is null || string.IsNullOrWhiteSpace(runId)) return null;
        try
        {
            return BudgetAnalyzer.Analyze(runId, "", archive.Sessions(runId), archive.SoftBreaks(runId));
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
