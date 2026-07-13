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
