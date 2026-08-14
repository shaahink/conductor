using Microsoft.Data.Sqlite;

namespace Conductor.Core.Store;

/// <summary>
/// KS1.3 — "is anything actually driving this store right now?", asked in exactly one place.
///
/// <para><b>Why it is not a column.</b> <c>runs.status</c> says what the last engine to write the row
/// believed. An engine that is killed, that loses its machine, or that predates KS0.2's park words
/// never gets to write the correction, so the row goes on saying <c>running</c> for ever — four such
/// rows on this machine are the whole of FU-F1-06. A listing that prints that column raw is not
/// reporting a fact, it is repeating a claim nobody has checked.</para>
///
/// <para><b>The rule, and it is the repair pass's rule.</b> <see cref="StateRepair"/> already had to
/// answer this question before it dared write a store, and got it right the expensive way. Rather
/// than a second opinion that can drift from it, the rule moved here and the repair pass calls it:
/// a live tracked pid in the store's own <c>pids</c> table, OR the engine lock on the repo's
/// <c>.conductor</c> held by a live process. Both are needed. The pids table tracks agents and faces,
/// not the engine, so BETWEEN sessions a run an engine is very much still driving has no live pid at
/// all; the lock answers for that gap.</para>
///
/// <para><b>Reconciling is not repairing.</b> Nothing here writes. <see cref="Reconcile"/> returns a
/// word for a caller to PRINT; the stored value is untouched and every surface keeps it beside the
/// reconciled one, because "the row says running" and "a run is running" are different facts and only
/// <c>conductor state repair</c> gets to change the first.</para>
/// </summary>
public static class RunLiveness
{
    /// <summary>What a non-terminal status reads as when no engine holds the store. Eight characters,
    /// which is the width of the STATUS column in <c>conductor history</c> and the Face's picker — a
    /// reconciled word that does not fit is a reconciled word nobody sees.</summary>
    public const string Orphaned = "orphaned";

    /// <summary>
    /// Is an engine using this store right now? Per STORE, not per run: one database holds every run
    /// of one (repo, plan) pair, and the engine holds the file, so a listing asks once and reuses the
    /// answer for every row it found there.
    /// </summary>
    /// <param name="dbPath">The run database.</param>
    /// <param name="repo">The repo the catalogue records for it; the engine lock lives under its
    /// <c>.conductor</c>. Null or empty means the lock cannot be consulted, and only a live pid can
    /// then prove liveness.</param>
    public static bool StoreLooksLive(string dbPath, string? repo)
    {
        if (HasLivePid(dbPath)) return true;
        if (string.IsNullOrWhiteSpace(repo)) return false;
        return HasUnfinishedRun(dbPath)
               && EngineLock.IsHeldByLiveEngine(Path.Combine(repo, StateHome.ScratchDirName));
    }

    /// <summary>
    /// The word to render for one run. A terminal status is repeated exactly — a finished run is
    /// finished whoever is or is not holding the file. A non-terminal status survives only while
    /// something is actually driving the store; otherwise it becomes <see cref="Orphaned"/>, which
    /// says the true thing: the row was never closed and nobody is going to close it by running.
    /// </summary>
    public static string Reconcile(string? storedStatus, bool storeLooksLive)
    {
        var stored = storedStatus ?? "";
        return storeLooksLive || RunRecord.IsTerminal(stored) ? stored : Orphaned;
    }

    /// <summary>
    /// Is this row a run something is still DOING work from? Both halves of the question in one
    /// place, because the negation is where the mistake gets made: the row has to be unfinished AND
    /// an engine has to be holding the store. An unfinished row whose engine was killed reconciles to
    /// <see cref="Orphaned"/> — it is a claim nobody closed, not work in flight — so a caller that
    /// decides from the raw column alone goes red on every repo whose last run died, which is the
    /// exact population this class was written to reconcile.
    /// <para>The complement of <see cref="Reconcile"/>: true precisely when the reconciled word is
    /// neither terminal nor <see cref="Orphaned"/>.</para>
    /// </summary>
    public static bool IsStillGoing(string? storedStatus, bool storeLooksLive) =>
        storeLooksLive && !RunRecord.IsTerminal(storedStatus);

    /// <summary>Does this store hold a run that is not over? KS0.2 widened it from
    /// <c>status = 'running'</c>, and the widening is load-bearing rather than tidy: once parks are
    /// written to the column (<see cref="RunRecord.StatusText"/>), a run an engine is holding open at
    /// a <c>needs_human</c> prompt no longer says <c>running</c> — and the narrow query would have
    /// called that store idle and let the repair write it out from under the engine. Terminal is the
    /// short list; everything else counts as unfinished, which is the safe direction to be wrong
    /// in.</summary>
    public static bool HasUnfinishedRun(string dbPath)
    {
        try
        {
            using var c = StateDedup.OpenReadOnly(dbPath);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT status FROM runs";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!RunRecord.IsTerminal(r.IsDBNull(0) ? null : r.GetString(0)))
                    return true;
            return false;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A process this store tracked, still alive and still the one that was tracked. Opened
    /// <c>Mode=ReadOnly</c> so asking cannot disturb the engine being asked about.</summary>
    public static bool HasLivePid(string dbPath)
    {
        try
        {
            using var c = StateDedup.OpenReadOnly(dbPath);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT p.pid, p.started_utc FROM pids p
                JOIN runs r ON r.run_id = p.run_id
                WHERE r.status = 'running' AND p.exited_utc IS NULL
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (PidLiveness.LooksAlive((int)r.GetInt64(0), SqliteRunStore.ParseUtc(r.GetString(1))))
                    return true;
            return false;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }
}
