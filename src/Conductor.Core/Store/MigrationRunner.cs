using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Store;

#pragma warning disable MA0045 // Migrations run synchronously during RunDb construction (no async ctors); the DB is a local file. Same pattern as RunDb.cs.
internal static class MigrationRunner
{
    /// <summary>Current schema version — the highest migration we ship.</summary>
    public const int CurrentVersion = 12;

    /// <summary>Column added to embedded resource path: folder path segments joined by <c>.</c></summary>
    private static readonly string ResourcePrefix = "Conductor.Core.Store.Migrations.";

    /// <summary>
    /// Run migrations inside the given transaction. On a fresh database every migration
    /// (v1 through <see cref="CurrentVersion"/>) is applied. On an existing database only
    /// migrations after the stored version are applied.
    /// </summary>
    public static void Run(SqliteConnection conn, ILogger logger)
    {
        var stored = GetStoredVersion(conn);

        if (stored is { } sv)
        {
            if (sv > CurrentVersion)
                throw new InvalidOperationException(
                    $"run.db schema version is newer ({sv}) than supported ({CurrentVersion}). " +
                    "Use a newer Conductor build.");
            if (sv == CurrentVersion)
                return;

            logger.LogInformation("Migrating run.db from v{From} to v{To}", sv, CurrentVersion);
            for (var v = sv + 1; v <= CurrentVersion; v++)
                Apply(conn, v, logger);
            SetVersion(conn, CurrentVersion);
        }
        else
        {
            logger.LogInformation("Creating fresh run.db (v{Version})", CurrentVersion);
            for (var v = 1; v <= CurrentVersion; v++)
                Apply(conn, v, logger);
            InsertVersion(conn, CurrentVersion);
        }
    }

    // ---------------------------------------------------------------- internals

    private static int? GetStoredVersion(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM schema_version";
            if ((long)cmd.ExecuteScalar()! == 0)
                return null;
            cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
            return (int)(long)cmd.ExecuteScalar()!;
        }
        catch (SqliteException)
        {
            return null; // table doesn't exist — fresh DB
        }
    }

    private static void Apply(SqliteConnection conn, int version, ILogger logger)
    {
        var name = $"v{version}_";
        // Uses a LINQ-like scan — there are only a handful of migrations.
        var resource = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(n => n.StartsWith(ResourcePrefix + name, StringComparison.Ordinal));

        if (resource == null)
            throw new InvalidOperationException(
                $"Migration v{version} not found in embedded resources (prefix: {ResourcePrefix + name}). " +
                "The build may be missing a .sql file — check that all migration files have " +
                "Build Action = EmbeddedResource.");

        var sql = ReadResource(resource);
        logger.LogDebug("Applying migration v{Version}: {Resource}", version, resource);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsAlreadyApplied(ex))
        {
            // K7.2: the version row can understate what is physically in the file — this repo's own
            // run.db carried schema_version 9 with v10's sessions.soft_break already on the table, and
            // every newer engine died on open with "duplicate column name: soft_break". Replay the
            // script one statement at a time and skip only the parts that are already there; anything
            // else still throws, and the caller's SetVersion then converges the row.
            var skipped = ReplayTolerantly(conn, sql);
            logger.LogWarning(
                "Migration v{Version} was already partially applied (schema_version understated it); " +
                "replayed it statement by statement and skipped {Skipped} already-applied statement(s).",
                version, skipped);
        }
    }

    /// <summary>
    /// True for the DDL errors that mean "this statement's effect is already in the database":
    /// SQLite reports <c>duplicate column name: x</c> for a repeated ADD COLUMN and
    /// <c>table/index x already exists</c> for a repeated CREATE.
    /// </summary>
    private static bool IsAlreadyApplied(SqliteException ex) =>
        ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-runs a migration script statement by statement, tolerating only already-applied DDL.
    /// Returns how many statements were skipped. Any other failure propagates unchanged.
    /// </summary>
    private static int ReplayTolerantly(SqliteConnection conn, string sql)
    {
        var skipped = 0;
        foreach (var statement in SplitStatements(sql))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (IsAlreadyApplied(ex))
            {
                skipped++;
            }
        }

        return skipped;
    }

    /// <summary>
    /// Splits a migration script on statement-terminating semicolons, ignoring the ones inside
    /// string literals, line comments and block comments. Whitespace- and comment-only fragments
    /// are dropped. Migration scripts are ours and contain no compound statements (no triggers).
    /// </summary>
    private static List<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var start = 0;
        var inString = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (c is '\n') inLineComment = false;
            }
            else if (inBlockComment)
            {
                if (c is '*' && next is '/') { inBlockComment = false; i++; }
            }
            else if (inString)
            {
                // '' is an escaped quote inside a literal, not the end of one.
                if (c is '\'' && next is '\'') i++;
                else if (c is '\'') inString = false;
            }
            else if (c is '-' && next is '-') { inLineComment = true; i++; }
            else if (c is '/' && next is '*') { inBlockComment = true; i++; }
            else if (c is '\'') { inString = true; }
            else if (c is ';')
            {
                AddIfExecutable(statements, sql[start..i]);
                start = i + 1;
            }
        }

        AddIfExecutable(statements, sql[start..]);
        return statements;
    }

    private static void AddIfExecutable(List<string> statements, string fragment)
    {
        // A fragment of only comments and whitespace executes as a no-op; drop it so the skip
        // count reports real statements.
        var hasCode = fragment.Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal));

        if (hasCode)
            statements.Add(fragment);
    }

    private static void SetVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE schema_version SET version = @ver";
        cmd.Parameters.AddWithValue("@ver", version);
        cmd.ExecuteNonQuery();
    }

    private static void InsertVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO schema_version (version) VALUES (@ver)";
        cmd.Parameters.AddWithValue("@ver", version);
        cmd.ExecuteNonQuery();
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream == null)
            throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
#pragma warning restore MA0045
