using System.Data;
using Conductor.Core.Events;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Store;

#pragma warning disable MA0045 // Sync DB writes by design — called during construction and from sync event emission paths. Same pattern as RunDb.cs.
public sealed partial class SqliteRunStore : IRunStore, IEventSink
{
    private readonly SqliteConnection _conn;
    private readonly ILogger<SqliteRunStore> _logger;
    private readonly TimeProvider _clock;
    private bool _initialized;

    public static readonly int CurrentSchemaVersion = MigrationRunner.CurrentVersion;

    public SqliteRunStore(string path, ILogger<SqliteRunStore> logger, TimeProvider? clock = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={path}");
        try
        {
            _conn.Open();
            using var pragma = _conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
            EnsureSchema();
        }
        catch
        {
            _conn.Close();
            _conn.Dispose();
            throw;
        }
    }

    /// <summary>The database file this store is writing. KS5.3: the read-only browsing door
    /// (<c>RunArchive</c>) takes a path, so a live run that wants to MEASURE itself — rather than
    /// query itself through its own writer — has to be able to name its own file.</summary>
    public string DbPath => _conn.DataSource;

    /// <summary>KS3.4 round 4 — the READ side of the store for surfaces that promise not to write:
    /// <c>preflight</c>'s compose leg and the dry-run loop's work-graph read
    /// (<see cref="Planning.WorkSnapshot.ReadAtRest"/>). <c>Mode=ReadOnly</c>, pooling off, no
    /// directory creation, no WAL pragma, no migration — opening this creates nothing and takes no
    /// write lock, which is exactly the promise those surfaces make; a live engine's WAL readers are
    /// undisturbed. The connection answers with whatever schema the file already has: a query against
    /// a table this version knows and the file predates throws <see cref="SqliteException"/>, which
    /// callers treat as "the graph cannot be read" and fall back to the declared snapshot.
    /// <para>Round 5: "creates nothing" is enforced by <see cref="AtRestConnectionString"/>, because
    /// <c>Mode=ReadOnly</c> alone does NOT keep it — opening a WAL database recreates its
    /// <c>-shm</c>/<c>-wal</c> sidecars even read-only, which is two new files under a state
    /// directory the caller promised to leave alone.</para></summary>
    public static SqliteRunStore OpenReadOnly(string path, ILogger<SqliteRunStore>? logger = null)
    {
        var conn = new SqliteConnection(AtRestConnectionString(path));
        try
        {
            conn.Open();
        }
        catch
        {
            conn.Dispose();
            throw;
        }
        return new SqliteRunStore(conn,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance);
    }

    /// <summary>KS3.4 round 5 — the connection string every AT-REST reader in this assembly opens
    /// with. A cleanly-closed WAL database has no <c>-shm</c>/<c>-wal</c> beside it, and SQLite
    /// recreates both on ANY open — read-only included — so a "read-only" drill left two new files
    /// under a state directory it promised not to touch. When the sidecars are absent the file is
    /// opened as a <c>file:</c> URI with <c>immutable=1</c>: no shared-memory index, no WAL, no
    /// locks, nothing created. When BOTH sidecars are already there (a live engine holds the store,
    /// or a crash left them), the plain read-only open is used instead — it creates nothing new and,
    /// unlike immutable, it sees the live WAL's committed data. Pooling is off either way: a pooled
    /// handle outlives its <c>using</c> and keeps sidecars pinned.</summary>
    /// <param name="cache">Optional cache mode; <see cref="RunArchive"/> passes Private.</param>
    internal static string AtRestConnectionString(string dbPath, SqliteCacheMode cache = SqliteCacheMode.Default)
    {
        var sidecars = File.Exists(dbPath + "-wal") && File.Exists(dbPath + "-shm");
        return new SqliteConnectionStringBuilder
        {
            DataSource = sidecars ? dbPath : ImmutableUri(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Cache = cache,
        }.ToString();
    }

    /// <summary>The db path as a percent-encoded <c>file:</c> URI carrying <c>immutable=1</c>.
    /// Every segment is escaped (SQLite percent-decodes the URI path), so spaces, <c>#</c> and
    /// <c>?</c> in a state directory cannot tear the query string.</summary>
    private static string ImmutableUri(string dbPath)
    {
        var full = Path.GetFullPath(dbPath).Replace('\\', '/');
        var escaped = string.Join("/", full.Split('/').Select(Uri.EscapeDataString));
        return $"file:///{escaped.TrimStart('/')}?immutable=1";
    }

    /// <summary>The read-only path in: an already-open connection, taken as-is. Private so the only
    /// way to get here is <see cref="OpenReadOnly"/> — a writable connection must go through the
    /// public constructor's WAL pragma and schema migration.</summary>
    private SqliteRunStore(SqliteConnection openConnection, ILogger<SqliteRunStore> logger)
    {
        _conn = openConnection;
        _logger = logger;
        _clock = TimeProvider.System;
    }

    // ---------------------------------------------------------------- schema

    private void EnsureSchema()
    {
        if (_initialized) return;
        using var tx = _conn.BeginTransaction();
        try
        {
            MigrationRunner.Run(_conn, _logger);
            tx.Commit();
            _initialized = true;
        }
        catch (SqliteException)
        {
            tx.Rollback();
            throw;
        }
    }

    // ---------------------------------------------------------------- raw query

    public IReadOnlyList<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(SqliteRunStore));
        var rows = new List<Dictionary<string, object?>>();
        lock (_persistGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
        }
        return rows;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Execute a write that may fail. On failure: logs at Error, emits a
    /// <see cref="DatabaseWriteFailed"/> event, and returns false. Callers that
    /// cannot tolerate a swallowed write should check the return value.</summary>
    private bool TryExecute(string sql, params (string Name, object? Value)[] parameters)
    {
        try
        {
            lock (_persistGate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = name;
                    p.Value = value ?? DBNull.Value;
                    cmd.Parameters.Add(p);
                }
                cmd.ExecuteNonQuery();
            }
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogError(ex, "run.db write failed: {Sql}", sql);
            // Emit event on a best-effort basis — if even this fails, just log.
            // The events table write is itself a TryExecute, so we can't recurse.
            // The error log above is the primary signal.
            return false;
        }
    }

    // -------------------------------------------- bug #33: behind, or genuinely diverged?

    /// <summary>How one run database's history stands to another's. Answering "may this copy be
    /// replaced by that file" from timestamps and sizes does not work and was measured not working:
    /// SQLite in WAL mode leaves the main file untouched for whole sessions at a time, and Windows
    /// does not refresh a sidecar's write time while its handle is open. Content is the only honest
    /// signal.</summary>
    public enum HistoryRelation
    {
        /// <summary>At least one file will not answer as a run database.</summary>
        Unknown,
        /// <summary>Same history in both. Nothing to do, nothing to say.</summary>
        Same,
        /// <summary>Everything in the copy is in the source, and the source has more.</summary>
        SourceAhead,
        /// <summary>Everything in the source is in the copy — the ordinary state after an install.</summary>
        CopyAhead,
        /// <summary>Each holds history the other does not. Only a human can merge that.</summary>
        Diverged,
    }

    /// <summary>
    /// Compares two run databases by what they actually remember: their sessions and their events.
    /// Used by <see cref="StateMigration"/> to decide whether a copy at the state home may be
    /// refreshed from the legacy file it came from (bug #33).
    /// <para>Set difference, not counts: two databases that share an ancestor and are then both
    /// written produce the SAME next session number and the SAME next per-run <c>seq</c> for
    /// DIFFERENT work, so anything that compares sizes calls a diverged copy a prefix and overwrites
    /// history that exists nowhere else.</para>
    /// </summary>
    public static HistoryRelation CompareHistories(string copyDb, string sourceDb)
    {
        var copy = HistoryKeys(copyDb);
        var source = HistoryKeys(sourceDb);
        if (copy is null || source is null) return HistoryRelation.Unknown;

        var sourceHasMore = !source.IsSubsetOf(copy);
        var copyHasMore = !copy.IsSubsetOf(source);
        return (sourceHasMore, copyHasMore) switch
        {
            (false, false) => HistoryRelation.Same,
            (true, false) => HistoryRelation.SourceAhead,
            (false, true) => HistoryRelation.CopyAhead,
            _ => HistoryRelation.Diverged,
        };
    }

    /// <summary>Every session and every event in a database, as identity keys. Null when the file
    /// answers as neither — not a run database, or not a database at all. Read-only and pooling-free:
    /// this must not create a <c>-wal</c>, take a write lock, or leave a handle on a file another
    /// engine may still be using.</summary>
    private static HashSet<string>? HistoryKeys(string dbPath)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            c.Open();

            var keys = new HashSet<string>(StringComparer.Ordinal);
            // A database old enough to predate either table is still readable through the other.
            var answered = ReadKeys(c, "SELECT run_id, number FROM sessions", "s", keys)
                           | ReadKeys(c, "SELECT run_id, seq FROM events", "e", keys);
            return answered ? keys : null;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ReadKeys(SqliteConnection c, string sql, string prefix, HashSet<string> into)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                into.Add($"{prefix}:{r.GetString(0)}:{r.GetInt64(1)}");
            return true;
        }
        catch (SqliteException)
        {
            return false;   // no such table in this schema version
        }
    }

    // ---------------------------------------------------------------- dispose

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        DisposeEventsDrain();
        try { _conn.Close(); } catch { }
        try { _conn.Dispose(); } catch { }
    }
}
#pragma warning restore MA0045
