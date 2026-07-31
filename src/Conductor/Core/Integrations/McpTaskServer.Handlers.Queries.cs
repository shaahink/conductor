using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Store;

namespace Conductor.Core.Integrations;

public partial class McpTaskServer
{
    // ---------------------------------------------------------------- F8.1: chat MCP tools

    private JsonElement HandleRunQuery(JsonElement? args)
    {
        if (_store == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "run_query requires store (no plan state available)." });

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
            var rows = _store.Query(sql);
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
        if (_store == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "ledger_list requires store (no plan state available)." });

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
            var rows = _store.QueryLedger(
                _runId,
                string.IsNullOrWhiteSpace(stageId) ? null : stageId,
                string.IsNullOrWhiteSpace(kind) ? null : kind);
            var result = rows.Take(tail).Select(r => new
            {
                id = r.Id.ToString(),
                stageId = r.StageId,
                kind = r.Kind,
                content = r.Content,
                createdAt = r.CreatedAt,
            }).ToArray();
            return JsonSerializer.SerializeToElement(new { ok = true, entries = result, count = result.Length });
        }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"Failed to query ledger: {ex.Message}" });
        }
    }

    // ---------------------------------------------------------------- M7.2: tracked bugs

    private JsonElement HandleBugNew(JsonElement? args)
    {
        if (_store == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "bug_new requires store (no plan state available)." });

        var title = "";
        string? detail = null, stageId = null;
        var severity = "medium";
        if (args is { } a)
        {
            if (a.TryGetProperty("title", out var t)) title = t.GetString() ?? "";
            if (a.TryGetProperty("detail", out var d)) detail = d.GetString();
            if (a.TryGetProperty("severity", out var s)) severity = s.GetString() ?? "medium";
            if (a.TryGetProperty("stage_id", out var st)) stageId = st.GetString();
        }
        if (string.IsNullOrWhiteSpace(title))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "title is required" });

        var id = _store.WriteBug(_runId, title, detail,
            severity, string.IsNullOrWhiteSpace(stageId) ? null : stageId, foundSession: null);
        return id > 0
            ? JsonSerializer.SerializeToElement(new { ok = true, id, title, severity, status = "open" })
            : JsonSerializer.SerializeToElement(new { ok = false, error = "bug write failed (see run.db error log)." });
    }

    private JsonElement HandleBugList(JsonElement? args)
    {
        if (_store == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "bug_list requires store (no plan state available)." });

        var status = "open";
        if (args is { } a && a.TryGetProperty("status", out var s))
            status = s.GetString() ?? "open";
        var filter = status.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : status;

        // SF0.4: this run's rows plus the open ones earlier runs in this repo left behind, each tagged
        // with the plan that filed it. `bug_list` is how an agent checks what is already known before
        // hunting; a per-run view told it "nothing known" the moment a new run started.
        static object Describe(Store.BugRow b, string? carriedFromPlan) => new
        {
            id = b.Id,
            title = b.Title,
            detail = b.Detail,
            severity = b.Severity,
            status = b.Status,
            stageId = b.StageId,
            foundSession = b.FoundSession,
            fixedSession = b.FixedSession,
            carriedFromPlan,
        };

        var carriedRows = _store.QueryCarriedBugs(_runId);
        var list = _store.QueryBugs(_runId, filter).Select(b => Describe(b, null))
            .Concat(carriedRows.Select(c => Describe(c.Bug, c.PlanName)))
            .ToArray();
        return JsonSerializer.SerializeToElement(new
        {
            ok = true,
            bugs = list,
            count = list.Length,
            carried = carriedRows.Count,
        });
    }

    private JsonElement HandleBugFix(JsonElement? args)
    {
        if (_store == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "bug_fix requires store (no plan state available)." });

        long id = 0;
        var wontfix = false;
        if (args is { } a)
        {
            if (a.TryGetProperty("id", out var i) && i.TryGetInt64(out var iv)) id = iv;
            if (a.TryGetProperty("wontfix", out var w) && w.ValueKind is JsonValueKind.True or JsonValueKind.False) wontfix = w.GetBoolean();
        }
        if (id <= 0)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "id is required" });

        var status = wontfix ? "wontfix" : "fixed";
        return _store.UpdateBugStatus(_runId, id, status, fixedSession: null)
            ? JsonSerializer.SerializeToElement(new { ok = true, id, status })
            : JsonSerializer.SerializeToElement(new { ok = false, error = $"no bug #{id} in this repo's run.db" });
    }

    private JsonElement HandleSessionDetail(JsonElement? args)
    {
        if (_store == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "session_detail requires store (no plan state available)." });

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
            var s = _store.QuerySessionByNumber(_runId, sessionNumber);
            if (s == null)
                return JsonSerializer.SerializeToElement(new { ok = false, error = $"Session #{sessionNumber} not found in store." });

            if (!string.IsNullOrWhiteSpace(stageId) && !s.StageId.Equals(stageId, StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.SerializeToElement(new { ok = false, error = $"Session #{sessionNumber} stage '{s.StageId}' does not match filter '{stageId}'." });

            var gateRows = _store.QueryGatesForSession(_runId, sessionNumber);

            var gates = gateRows.Select(g => new
            {
                name = g.Name,
                tier = g.Tier,
                passed = g.Passed,
                skipped = g.Skipped,
                scope = g.Scope,
            }).ToArray();

            return JsonSerializer.SerializeToElement(new
            {
                ok = true,
                session = new
                {
                    number = sessionNumber,
                    stageId = s.StageId,
                    kind = s.Kind,
                    outcome = s.Outcome,
                    startedUtc = s.StartedUtc,
                    endedUtc = s.EndedUtc,
                    attempt = s.Attempt.ToString(),
                    resumeCount = s.ResumeCount.ToString(),
                    gateSummary = s.GateSummary,
                    resultSummary = s.ResultSummary,
                    newlyDone = s.NewlyDone,
                    // SC7.2: what that session actually did — tool mix, files it wrote, claims,
                    // bg-start purposes as a storyline, build/test commands. Reading an earlier
                    // session is now one tool call instead of a transcript crawl.
                    digest = Core.Events.SessionDigest.FromJson(s.Digest)?.Render(),
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

        if (_store != null)
        {
            try
            {
                _store.WriteInjection(_runId, "mcp", null, string.IsNullOrWhiteSpace(stageId) ? null : stageId, content);
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
