using System.Text;
using System.Text.Json;
using Conductor.Core.Events;

namespace Conductor.Core.Integrations;

public partial class McpTaskServer
{
    // ---------------------------------------------------------------- F8.1: chat MCP tools

    private JsonElement HandleRunQuery(JsonElement? args)
    {
        if (_runDb == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "run_query requires run.db (no plan state available)." });

        var sql = "";
        if (args is { } a && a.TryGetProperty("sql", out var s))
            sql = (s.GetString() ?? "").Trim();

        if (string.IsNullOrEmpty(sql))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "sql is required." });

        if (!sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && !sql.StartsWith("select", StringComparison.Ordinal)
            && !sql.StartsWith("--", StringComparison.Ordinal))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "Only SELECT queries are allowed." });

        try
        {
            var rows = _runDb.Query(sql);
            var result = rows.Select(r =>
            {
                var d = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var kv in r) d[kv.Key] = kv.Value?.ToString();
                return d;
            }).ToArray();
            return JsonSerializer.SerializeToElement(new { ok = true, rows = result, count = result.Length });
        }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"Query failed: {ex.Message}" });
        }
    }

    private JsonElement HandleLedgerList(JsonElement? args)
    {
        if (_runDb == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "ledger_list requires run.db (no plan state available)." });

        var stageId = "";
        var tail = 20;
        var kind = "";
        if (args is { } a)
        {
            if (a.TryGetProperty("stageId", out var s)) stageId = s.GetString() ?? "";
            if (a.TryGetProperty("tail", out var t) && t.TryGetInt32(out var tv) && tv > 0) tail = tv;
            if (a.TryGetProperty("kind", out var k)) kind = k.GetString() ?? "";
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append("SELECT id, run_id, session_number, stage_id, kind, content, created_at FROM ledger WHERE 1=1");
            var parameters = new List<(string Name, object? Value)>();
            if (!string.IsNullOrWhiteSpace(stageId))
            {
                sb.Append(" AND stage_id = @stageId");
                parameters.Add(("@stageId", stageId));
            }
            if (!string.IsNullOrWhiteSpace(kind))
            {
                sb.Append(" AND kind = @kind");
                parameters.Add(("@kind", kind));
            }
            sb.Append(" ORDER BY id DESC LIMIT @tail");
            parameters.Add(("@tail", tail));

            var rows = _runDb.Query(sb.ToString(), [.. parameters]);
            var result = rows.Select(r => new
            {
                id = r.GetValueOrDefault("id")?.ToString(),
                stageId = r.GetValueOrDefault("stage_id")?.ToString(),
                kind = r.GetValueOrDefault("kind")?.ToString(),
                content = r.GetValueOrDefault("content")?.ToString(),
                createdAt = r.GetValueOrDefault("created_at")?.ToString(),
            }).ToArray();
            return JsonSerializer.SerializeToElement(new { ok = true, entries = result, count = result.Length });
        }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"Failed to query ledger: {ex.Message}" });
        }
    }

    private JsonElement HandleSessionDetail(JsonElement? args)
    {
        if (_runDb == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "session_detail requires run.db (no plan state available)." });

        var sessionNumber = 0;
        var stageId = "";
        if (args is { } a)
        {
            if (a.TryGetProperty("sessionNumber", out var sn) && sn.TryGetInt32(out var snv)) sessionNumber = snv;
            if (a.TryGetProperty("stageId", out var s)) stageId = s.GetString() ?? "";
        }
        if (sessionNumber <= 0)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "sessionNumber is required." });

        try
        {
            var sql = "SELECT * FROM sessions WHERE run_id = @runId AND number = @num";
            var parameters = new List<(string Name, object? Value)> { (("@runId", _runId)), (("@num", sessionNumber)) };
            if (!string.IsNullOrWhiteSpace(stageId))
            {
                sql += " AND stage_id = @stageId";
                parameters.Add(("@stageId", stageId));
            }
            sql += " LIMIT 1";

            var rows = _runDb.Query(sql, [.. parameters]);
            if (rows.Count == 0)
                return JsonSerializer.SerializeToElement(new { ok = false, error = $"Session #{sessionNumber} not found in run.db." });

            var r = rows[0];

            var gateRows = _runDb.Query(
                "SELECT name, tier, passed, skipped, optional, exit_code, duration_ms, scope FROM gates WHERE session_number = @num ORDER BY id",
                ("@num", sessionNumber));

            var gates = gateRows.Select(g => new
            {
                name = g.GetValueOrDefault("name")?.ToString(),
                tier = g.GetValueOrDefault("tier")?.ToString(),
                passed = g.GetValueOrDefault("passed")?.ToString() == "1",
                skipped = g.GetValueOrDefault("skipped")?.ToString() == "1",
                scope = g.GetValueOrDefault("scope")?.ToString(),
            }).ToArray();

            return JsonSerializer.SerializeToElement(new
            {
                ok = true,
                session = new
                {
                    number = sessionNumber,
                    stageId = r.GetValueOrDefault("stage_id")?.ToString(),
                    kind = r.GetValueOrDefault("kind")?.ToString(),
                    outcome = r.GetValueOrDefault("outcome")?.ToString(),
                    startedUtc = r.GetValueOrDefault("started_utc")?.ToString(),
                    endedUtc = r.GetValueOrDefault("ended_utc")?.ToString(),
                    attempt = r.GetValueOrDefault("attempt")?.ToString(),
                    resumeCount = r.GetValueOrDefault("resume_count")?.ToString(),
                    gateSummary = r.GetValueOrDefault("gate_summary")?.ToString(),
                    resultSummary = r.GetValueOrDefault("result_summary")?.ToString(),
                    newlyDone = r.GetValueOrDefault("newly_done")?.ToString(),
                },
                gates,
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"Failed to query session: {ex.Message}" });
        }
    }

    private JsonElement HandleInjectInstruction(JsonElement? args)
    {
        var content = "";
        var stageId = "";
        if (args is { } a)
        {
            if (a.TryGetProperty("content", out var c)) content = c.GetString() ?? "";
            if (a.TryGetProperty("stageId", out var s)) stageId = s.GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(content))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "content is required." });

        if (_runDb != null)
        {
            try
            {
                _runDb.WriteInjection(_runId, "mcp", null, string.IsNullOrWhiteSpace(stageId) ? null : stageId, content);
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                return JsonSerializer.SerializeToElement(new { ok = false, error = $"Failed to write injection: {ex.Message}" });
            }
#pragma warning restore CA1031
        }

        return JsonSerializer.SerializeToElement(new { ok = true, stageId = string.IsNullOrWhiteSpace(stageId) ? null : stageId });
    }

    private void WriteJournal(ConductorEvent evt)
    {
#pragma warning disable MA0045
        var stamped = evt with
        {
            Ts = DateTime.UtcNow,
            Seq = 0,
        };
        var json = JsonSerializer.Serialize(stamped, EventJsonContext.Default.ConductorEvent);
        var dir = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.AppendAllText(_journalPath, json + Environment.NewLine);
#pragma warning restore MA0045
    }
}
