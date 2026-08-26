using Conductor.Core.Store;

namespace Conductor.Core.History;

/// <summary>
/// DV6.1 — the bug ledger of an archived run database, read the way every other archive read works:
/// read-only, one connection per call, nothing held open.
///
/// <para><b>Why the archive and not the store.</b> <c>github sync --backfill</c> may be pointed at
/// ANY run database — another era's, a copy, a run that finished months ago — and opening one of
/// those with <c>SqliteRunStore</c> would migrate it forward. That is not hypothetical here: an
/// engine build whose schema version is ahead of the installed one migrated the live store at KS10.1
/// and the driving engine then refused it. A backfill looks; it does not upgrade.</para>
/// </summary>
public sealed partial class RunArchive
{
    /// <summary>Every bug in this database, any run, any status, newest first — the same set
    /// <c>IRunStore.QueryBugLedger</c> answers for a live run.
    ///
    /// <para><b>No join to <c>runs</c>, on purpose.</b> The live store's version joins for the plan
    /// name; here the plan name is paired on in C# from <see cref="Runs"/>. KS1.6's scan reads a
    /// FILE's whole SQL, so a join to that table beside the word <c>status</c> — even a bug's status —
    /// is indistinguishable from the snapshot read it exists to keep out. The rule is right and the
    /// join was never needed: this is a reader, and readers get their run facts from the archive's
    /// own door.</para></summary>
    public IReadOnlyList<BugRow> BugLedger()
    {
        try
        {
            var rows = Query(
                "SELECT id, run_id, title, detail, severity, status, stage_id, found_session, " +
                "fixed_session, created_at, updated_at FROM bugs ORDER BY id DESC");
            return [.. rows.Select(SqliteRunStore.ToBugRow)];
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // A database old enough to predate the bugs table has an empty ledger, not a broken one.
            return [];
        }
    }
}
