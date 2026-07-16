using Conductor.Models;
using Microsoft.Data.Sqlite;

namespace Conductor.Core.Store;

/// <summary>
/// Loads the latest persisted <see cref="RunState"/> for a plan straight from <c>run.db</c>, BEFORE
/// the DI host (and with it <see cref="SqliteRunStore"/> + migrations) exists. <c>state.json</c> died
/// in M2 — the live store is the <c>run_state</c> table (migration v5) — but <c>RunCommand</c> kept
/// loading the legacy file, so every <c>conductor run</c> silently started a FRESH run: new run id,
/// session #1, zero budget (found in the 2026-07-17 dogfood; "run again to resume" was a no-op).
/// Read-only and tolerant: a missing db, missing table, or torn JSON returns null — resume is
/// best-effort and must never block a fresh start.
/// </summary>
public static class RunStateResume
{
#pragma warning disable MA0045 // sync by design: runs once at command start, before any host exists
    public static RunState? TryLoadLatest(string runDbPath, string planName)
    {
        if (!File.Exists(runDbPath)) return null;
        try
        {
            using var conn = new SqliteConnection($"Data Source={runDbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT state_json FROM run_state WHERE plan_name = @plan ORDER BY updated_utc DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@plan", planName);
            if (cmd.ExecuteScalar() is not string json || json.Length == 0) return null;
            return System.Text.Json.JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts);
        }
        catch (Exception ex) when (ex is SqliteException or System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
#pragma warning restore MA0045
}
