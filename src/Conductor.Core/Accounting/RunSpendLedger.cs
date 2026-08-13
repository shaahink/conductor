using Conductor.Core.Store;

namespace Conductor.Core.Accounting;

/// <summary>
/// KS5.2 — the ONE place a model invocation that is not the delivery agent becomes money the run knows
/// about: a <c>costs</c> row, an accrual against the cap, and a log line that says which of the two
/// happened.
/// <para>Before this, seven of the eight process-spawning paths that take a model wrote nothing at all
/// (lanes, fix-lanes, the parallel audit, the supervisor hook, the status agent, the auth probe) and the
/// eighth wrote a number nobody had been charged. A run could spend an afternoon of lane time and
/// report the same total as one that had run no lanes, because the only spend the engine counted was
/// the one it had a session record for.</para>
/// <para><b>Session key.</b> <c>costs.session_number</c> is NOT NULL, so every row needs one. The key is
/// the session the run is ON — the last session's number between sessions, and 0 before the first one
/// has started (the auth probe). Chosen explicitly rather than dropping the row: a spend with an
/// awkward key is still a spend, and <c>MoneyAnalyzer</c> already buckets a cost row whose session it
/// cannot date as "unknown" instead of losing it.</para>
/// <para><b>Accrual.</b> The engine passes an <c>accrue</c> callback that folds the receipt into
/// <c>RunContext.RunSideCostUsd</c>, which is half of the total <c>CheckBudgetCap</c> compares and half
/// of what <c>/state</c> serves. A ledger built without one — the <c>watch</c> supervisor, in a
/// different process from the engine — records the row and says so, and does not pretend to have moved
/// a cap it cannot see.</para>
/// </summary>
public sealed class RunSpendLedger
{
    private readonly IRunStore? _store;
    private readonly string _runId;
    private readonly Action<SpendReceipt>? _accrue;
    private readonly Action<string>? _log;

    /// <param name="store">Where the row goes. Null is legal (a dry run, a plan with no database) and
    /// means the accrual still happens — the cap governs a run that is not recording history too.</param>
    /// <param name="runId">The run the spend belongs to. Empty means there is no run to key a row to;
    /// nothing is written and the caller is told why.</param>
    /// <param name="accrue">Folds the receipt into the run's live budget. Null for a recorder outside
    /// the engine process.</param>
    /// <param name="log">The run log.</param>
    public RunSpendLedger(IRunStore? store, string runId, Action<SpendReceipt>? accrue = null, Action<string>? log = null)
    {
        _store = store;
        _runId = runId ?? "";
        _accrue = accrue;
        _log = log;
    }

    /// <summary>Record one model invocation's billed spend.</summary>
    /// <param name="receipt">What the provider reported, or null when it reported nothing.</param>
    /// <param name="sessionNumber">The session to key the row to (see the type's remarks).</param>
    /// <param name="what">What was spawned, for the log line — "advisor consult", "analysis lane 'x'".</param>
    /// <returns>True when a row was written or an accrual made.</returns>
    public bool Record(SpendReceipt? receipt, int sessionNumber, string what)
    {
        if (receipt is null)
        {
            // Not zero. A surface that renders an unknown as $0.00 is the failure this checkpoint is
            // about; the run says out loud that it cannot price this one.
            _log?.Invoke($"{what}: the provider reported no billed figure — not recorded (unknown, not zero)");
            return false;
        }
        if (_runId.Length == 0)
        {
            _log?.Invoke($"{what}: ${receipt.CostUsd:0.0000} billed, but there is no run to key a cost row to — stated, not recorded");
            return false;
        }

        _store?.RecordCost(_runId, Math.Max(0, sessionNumber), receipt.Category,
            receipt.TokensIn, receipt.TokensOut, receipt.TokensThink, receipt.TokensCacheRead,
            receipt.CostUsd, receipt.WallMs);
        _accrue?.Invoke(receipt);

        _log?.Invoke($"{what}: ${receipt.CostUsd:0.0000} billed ({receipt.Category}, {receipt.Tokens} tokens) — " +
                     (_accrue is null ? "recorded in the ledger" : "counted against the run cap"));
        return true;
    }
}
