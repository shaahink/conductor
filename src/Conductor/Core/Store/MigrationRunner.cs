using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Store;

#pragma warning disable MA0045 // Migrations run synchronously during RunDb construction (no async ctors); the DB is a local file. Same pattern as RunDb.cs.
internal static class MigrationRunner
{
    /// <summary>Current schema version — the highest migration we ship.</summary>
    public const int CurrentVersion = 9;

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
        cmd.ExecuteNonQuery();
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
