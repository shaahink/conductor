using System.Data;
using System.Text.Json;
using Conductor.Models;

namespace Conductor.Core.Store;

public sealed partial class SqliteRunStore
{
    // ---------------------------------------------------------------- run_state table

    public string? GetLatestRunId(string planName)
    {
        var rows = Query(
            "SELECT run_id FROM runs WHERE plan_name = @planName ORDER BY started_utc DESC LIMIT 1",
            ("@planName", planName));
        return rows.Count > 0 ? (string)rows[0]["run_id"]! : null;
    }

    public string? LoadRunStateJson(string runId)
    {
        var rows = Query(
            "SELECT state_json FROM run_state WHERE run_id = @runId",
            ("@runId", runId));
        return rows.Count > 0 ? (string)rows[0]["state_json"]! : null;
    }

    public void SaveRunState(string runId, string planName, string stateJson)
    {
        TryExecute(
            "INSERT OR REPLACE INTO run_state (run_id, plan_name, state_json, updated_utc) " +
            "VALUES (@runId, @planName, @json, @now)",
            ("@runId", runId),
            ("@planName", planName),
            ("@json", stateJson),
            ("@now", _clock.GetUtcNow().ToString("O")));
    }
}
