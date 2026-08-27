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

    /// <summary>Bug #39: close the session records a dead engine left open in this run's persisted
    /// state — the write half of <see cref="Orchestration.SessionReconcile"/>, for
    /// <c>conductor run close</c>. Returns the session numbers closed; empty when there was nothing
    /// to close or no state to read.</summary>
    public IReadOnlyList<int> CloseDanglingSessions(string runId, string planName, DateTime endedUtc)
    {
        var json = LoadRunStateJson(runId);
        if (string.IsNullOrEmpty(json)) return [];
        RunState? state;
        try { state = System.Text.Json.JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts); }
        catch (System.Text.Json.JsonException) { return []; }
        if (state is null) return [];
        var closed = Orchestration.SessionReconcile.CloseDangling(state, endedUtc);
        if (closed.Count == 0) return closed;
        SaveRunState(runId, planName, System.Text.Json.JsonSerializer.Serialize(state, PlanConfig.JsonOpts));
        return closed;
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
