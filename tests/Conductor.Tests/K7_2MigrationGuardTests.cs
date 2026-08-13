using Conductor.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K7.2: a run.db whose <c>schema_version</c> row understates what is physically applied must
/// converge on open instead of crashing. This repo's own run.db reached exactly that state —
/// version 9 with v10's <c>sessions.soft_break</c> already on the table — and killed every newer
/// engine with <c>duplicate column name: soft_break</c> (bugs #28 and #29). The tolerance is
/// narrow on purpose: only already-applied DDL is skipped.
/// </summary>
public sealed class K7_2MigrationGuardTests : IDisposable
{
    private readonly string _dbPath;

    public K7_2MigrationGuardTests() =>
        _dbPath = Path.Combine(Path.GetTempPath(), $"conductor-migrate-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public void Version_row_that_understates_the_schema_converges_instead_of_crashing()
    {
        // The exact shape measured on this repo on 2026-08-05: v10 applied, v11 and v12 not,
        // schema_version still reading 9.
        CreateThenRewind(9, "engine_version", "engine_commit", "engine_dirty", "limits_json",
            "engine", "limits", "context_high_water", "context_mean_turn", "context_turns",
            "limits_json_at_launch", "limits_reload_count", "limits_reloaded_utc");

        Assert.Equal(1, ColumnCount("sessions", "soft_break"));

        Reopen();

        Assert.Equal(SqliteRunStore.CurrentSchemaVersion, StoredVersion());
        Assert.Equal(1, ColumnCount("sessions", "soft_break"));
        Assert.Equal(1, ColumnCount("runs", "engine_commit"));
        Assert.Equal(1, ColumnCount("sessions", "context_high_water"));
        // KS1.1: v13 lands on the same climb.
        Assert.Equal(1, ColumnCount("runs", "limits_json_at_launch"));
        Assert.Equal(1, ColumnCount("runs", "limits_reload_count"));
        Assert.Equal(1, ColumnCount("runs", "limits_reloaded_utc"));
    }

    /// <summary>KS1.1 — the rewind the archive has to survive, from the other side: a database left at
    /// v13 minus one column, opened by the ENGINE rather than the reader. The replay fills the gap and
    /// the row that was already there keeps its launch snapshot, which is the property that makes the
    /// column worth having; a migration that refilled it from limits_json would pass every column-count
    /// assertion above and still have overwritten where the run began.</summary>
    [Fact]
    public void A_partly_applied_v13_keeps_the_launch_snapshot_it_already_had()
    {
        Reopen();
        using (var store = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance))
            store.InitializeRun("run-k72-v13", "core", "C:\\repo", "master",
                new Conductor.Core.EngineStamp("0.3.1", "abcdef", false), "{\"sessionTokenCap\":24000000}");
        SqliteConnection.ClearAllPools();

        Execute("ALTER TABLE runs DROP COLUMN limits_reloaded_utc");
        Execute("UPDATE schema_version SET version = 12");

        Reopen();

        Assert.Equal(SqliteRunStore.CurrentSchemaVersion, StoredVersion());
        Assert.Equal(1, ColumnCount("runs", "limits_reloaded_utc"));
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT limits_json_at_launch FROM runs WHERE run_id = 'run-k72-v13'";
        Assert.Equal("{\"sessionTokenCap\":24000000}", cmd.ExecuteScalar() as string);
    }

    [Fact]
    public void A_partly_applied_multi_statement_migration_fills_in_only_the_missing_column()
    {
        // v11 is six ALTER statements. Remove one and rewind to v10: the whole script fails on the
        // first duplicate, so the replay has to run statement by statement and skip the five that
        // are already there rather than giving up on the sixth.
        CreateThenRewind(10, "engine_commit", "context_high_water", "context_mean_turn", "context_turns");

        Reopen();

        Assert.Equal(SqliteRunStore.CurrentSchemaVersion, StoredVersion());
        Assert.Equal(1, ColumnCount("runs", "engine_commit"));
        Assert.Equal(1, ColumnCount("runs", "engine_version"));
        Assert.Equal(1, ColumnCount("sessions", "context_turns"));
    }

    [Fact]
    public void A_genuine_migration_failure_still_throws()
    {
        CreateThenRewind(9);
        Execute("DROP TABLE sessions");

        var ex = Assert.Throws<SqliteException>(Reopen);
        Assert.Contains("sessions", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Builds a current database, then rewinds the version row and drops the named columns to
    /// recreate a half-migrated file. Columns are matched against both tables that carry them.
    /// </summary>
    private void CreateThenRewind(int version, params string[] dropColumns)
    {
        Reopen();
        foreach (var column in dropColumns)
        {
            var table = ColumnCount("runs", column) == 1 ? "runs" : "sessions";
            Execute($"ALTER TABLE {table} DROP COLUMN {column}");
        }

        Execute($"UPDATE schema_version SET version = {version}");
    }

    private void Reopen()
    {
        using var store = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
    }

    private void Execute(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private int StoredVersion() => Scalar("SELECT version FROM schema_version LIMIT 1");

    private int ColumnCount(string table, string column) =>
        Scalar($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'");

    private int Scalar(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
