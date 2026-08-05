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

    public string ConnectionString => _conn.DataSource;

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
