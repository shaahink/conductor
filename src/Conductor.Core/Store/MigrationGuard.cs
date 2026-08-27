using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Conductor.Core.Store;

/// <summary>
/// Bug #45 — a store an OLDER engine is driving is not migrated out from under it.
///
/// <para>Measured live at KS10.1: a session ran a reporting verb from a fresh build against the live
/// run.db, the store went v13 → v14 on open, and from that moment every new invocation of the PATH
/// binary — the 0.4.1 engine's own <c>task</c>, <c>note</c> and <c>bug</c> — refused with "schema
/// version is newer than supported". The run survived only because the supervisor held the
/// connection it had opened at v13; had it restarted it could not have reopened its own store.</para>
///
/// <para>The rule is one question asked before the migration runs, not a read-only mode for every
/// verb: <b>is an engine that cannot read the new schema holding this store right now?</b> A store
/// that is behind AND held by a live engine can only be held by an engine older than this build — an
/// engine at this version would already have migrated it — so migrating is exactly the lock-out, and
/// it is refused with the pid, the file and the two ways out. A behind store nobody holds migrates as
/// it always has; a store already at this version is never refused, whoever holds it.</para>
///
/// <para>Who holds the store is answered from the store itself: <c>runs.repo</c> (a v1 column) names
/// the repo whose <c>.conductor/conductor.lock</c> the engine writes, and the <c>pids</c> table names
/// the children it spawned — the same two facts <see cref="RunLiveness"/> reconciles a listing
/// from.</para>
/// </summary>
public static class MigrationGuard
{
    /// <summary>Why a migration was refused: the versions involved and who is holding the file.</summary>
    public sealed record Refusal(int Stored, int Supported, string Holder);

    /// <summary>Null when migrating is safe — the store is not behind, or nothing live is holding it.</summary>
    public static Refusal? Check(SqliteConnection conn, string dbPath, int supported)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var stored = MigrationRunner.StoredVersion(conn);
        if (stored is not { } sv || sv >= supported) return null;
        var holder = LiveHolder(dbPath);
        return holder is null ? null : new Refusal(sv, supported, holder);
    }

    /// <summary>The refusal as the sentence a verb prints: what the file is, who holds it, what this
    /// build would have done, and the two ways out. Stable so a test can pin the grep handles.</summary>
    public static string Message(string dbPath, Refusal r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return $"run.db at {dbPath} is schema v{r.Stored.ToString(CultureInfo.InvariantCulture)} and " +
               $"{r.Holder}; this build supports v{r.Supported.ToString(CultureInfo.InvariantCulture)} and " +
               "would migrate the file, locking that engine out of its own store (bug #45). " +
               "Stop the run first (conductor pause / abort), or use the build that is driving it.";
    }

    /// <summary>Who is live on this store, as a phrase — or null when nobody is. The engine lock is
    /// consulted through every repo the store's unfinished runs name, because the file, not the
    /// catalogue, is what the engine actually holds; the pids table answers for a spawned child whose
    /// engine has not written a lock this build can read.</summary>
    public static string? LiveHolder(string dbPath)
    {
        foreach (var repo in UnfinishedRepos(dbPath))
        {
            var stateDir = Path.Combine(repo, StateHome.ScratchDirName);
            if (EngineLock.Read(stateDir) is { } h && EngineLock.IsLive(h))
            {
                var since = h.StartedUtc is { } s ? $", started {s:yyyy-MM-dd HH:mm:ss}Z" : "";
                return $"an engine (pid {h.Pid.ToString(CultureInfo.InvariantCulture)}{since}) holds {EngineLock.PathFor(stateDir)}";
            }
        }
        return RunLiveness.HasLivePid(dbPath)
            ? "a process it tracks in its pids table is still running under an unfinished run"
            : null;
    }

    /// <summary>The repos of every run in the store that is not over, read at rest the way
    /// <see cref="RunLiveness.HasLivePid"/> reads — BEFORE any migration touches the file.
    /// <c>runs.repo</c> and <c>runs.status</c> are v1 columns, so every store this engine can open has
    /// them. A store with no runs table at all is a fresh file, and a fresh file has no holder.</summary>
    public static IReadOnlyList<string> UnfinishedRepos(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath)) return [];
            using var c = StateDedup.OpenReadOnly(dbPath);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT repo, status FROM runs";
            using var r = cmd.ExecuteReader();
            var repos = new List<string>();
            while (r.Read())
            {
                var status = r.IsDBNull(1) ? null : r.GetString(1);
                if (RunRecord.IsTerminal(status)) continue;
                var repo = r.IsDBNull(0) ? "" : r.GetString(0);
                if (repo.Length > 0 && !repos.Contains(repo, StringComparer.OrdinalIgnoreCase)) repos.Add(repo);
            }
            return repos;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            // No runs table, or a file that will not answer: nothing can be holding it yet.
            return [];
        }
    }
}
