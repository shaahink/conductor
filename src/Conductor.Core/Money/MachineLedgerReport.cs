namespace Conductor.Core.Money;

/// <summary>
/// KS5.1 — a stretch of wall-clock time the operator asked about: "today", "this week (7d)", "this
/// month", or whatever <c>--since</c> spelled. Half-open on purpose (<c>[Since, Until)</c>) so two
/// adjacent windows cannot both claim the same session.
/// <para><b>It is compared against a SESSION's start, never against a run's last activity.</b> A run
/// that began in June and closed a checkpoint this morning spent money in both, and the whole-run
/// <c>--since</c> filter (<c>RunHistory.MatchesRun</c>) answers "this week" with June's bill.</para>
/// </summary>
/// <param name="Label">What to call it in the table and the JSON.</param>
/// <param name="Since">Inclusive lower bound, UTC.</param>
/// <param name="Until">Exclusive upper bound, UTC; null means "up to now".</param>
public sealed record MachineLedgerWindow(string Label, DateTimeOffset Since, DateTimeOffset? Until)
{
    /// <summary>True when a session started inside this window.</summary>
    public bool Contains(DateTimeOffset when)
        => when >= Since && (Until is not { } until || when < until);
}

/// <summary>
/// KS5.1 — one run as the machine ledger sees it: which store it was read out of, its lifetime money
/// exactly as <c>conductor money</c> reports it, and the same money sliced by the asked-for windows.
/// <para><see cref="Run"/> is a <see cref="MoneyRun"/> and not a copy of one: the cross-check between
/// this verb and <c>money --run &lt;id&gt; --json</c> has to hold to the cent, and the only way to
/// guarantee that is for both to be the same record produced by the same function.</para>
/// </summary>
/// <param name="DbPath">The database this run was read from. Part of the identity used to notice that
/// two catalogue entries are pointing at one file.</param>
/// <param name="Run">The run's lifetime money, from <see cref="MoneyAnalyzer.AnalyzeRun"/>.</param>
/// <param name="Periods">One line per requested window, in the order they were requested.</param>
/// <param name="Undated">Billed rows whose session carries no start time, so no window can honestly
/// claim them. Counted in <see cref="Run"/>'s total, never in a period.</param>
public sealed record MachineLedgerRun(
    string DbPath, MoneyRun Run, IReadOnlyList<MoneyLine> Periods, MoneyLine Undated);

/// <summary>
/// KS5.1 — what this machine spent, across every store it knows about, with no repo and no plan
/// argument in the question.
/// <para><b>Each real run is counted once.</b> The catalogue is an index and it has minted duplicates
/// before (KS0.1: one <c>run.db</c> living in five stores, 37 rows for 25 runs), so identity here is
/// the run id, never the entry that pointed at it.</para>
/// </summary>
/// <param name="Scope">The question, in the words the operator used.</param>
/// <param name="Root">The state home that was read.</param>
/// <param name="Stores">Distinct databases the counted runs came out of.</param>
/// <param name="DuplicateRunsCollapsed">How many catalogue rows resolved to a run already counted.
/// Printed rather than swallowed: a machine growing copies of its own history is worth saying.</param>
/// <param name="Runs">The runs that were counted, oldest activity first.</param>
/// <param name="Ledger">The same rollup <c>money</c> builds over those runs — the months, the
/// categories and the per-run totals, from <see cref="MoneyAnalyzer.Combine"/>.</param>
/// <param name="Periods">One line per requested window, summed over every counted run.</param>
/// <param name="Undated">Billed rows no window can claim, summed over every counted run.</param>
public sealed record MachineLedgerReport(
    string Scope, string Root, int Stores, int DuplicateRunsCollapsed,
    IReadOnlyList<MachineLedgerRun> Runs, MoneyReport Ledger,
    IReadOnlyList<MoneyLine> Periods, MoneyLine Undated)
{
    /// <summary>Every billed dollar in the counted runs. The same line <see cref="Ledger"/> carries,
    /// relabelled — derived rather than stored so the headline number and the per-run rollup cannot
    /// drift apart by one edit.</summary>
    public MoneyLine Total => Ledger.Total with { Label = MachineLedger.TotalLabel };

    /// <summary>True when this machine has no catalogue, no local database, or nothing billed in
    /// either. A stated answer, not an error: a machine that has spent nothing is not broken.</summary>
    public bool NothingRecorded => Runs.Count == 0;
}
