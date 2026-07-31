using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Http;

public sealed partial class ControlPlaneServer
{
    // GET /state — its fold, its live-metrics layer, and the tracker read — lives in
    // ControlPlaneServer.State.cs.

    private async Task WriteTasksAsync(HttpListenerContext ctx)
    {
        var events = ReadEvents();
        var graph = new TaskGraph();
        graph.Fold(events);
        // Archived items (W1.2) left the declared plan — history stays in the log, off the board.
        var live = graph.Tasks.Where(t => t.Status != "archived").ToList();
        await WriteJsonAsync(ctx, ControlPlaneDto.FromTasks(live), ControlPlaneJsonContext.Default.TasksDto).ConfigureAwait(false);
    }

    private async Task StreamEventsAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        ctx.Response.SendChunked = true;
        var output = ctx.Response.OutputStream;
        var lastSeq = ParseSince(ctx);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // SC2.4: ask the database for the tail, not for everything. ReadEvents() selects every
                // row of the run and deserialises it — once a second, per connected client, to find the
                // nought-to-few that are new. The WHERE seq > ? is the same filter this loop was doing
                // in C# after paying for the whole read.
                var events = ReadEventsAfter(lastSeq);
                foreach (var evt in events.OrderBy(e => e.Seq))
                {
                    var json = JsonSerializer.Serialize<ConductorEvent>(evt, EventJsonContext.Default.ConductorEvent);
                    var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                    await output.WriteAsync(frame, ct).ConfigureAwait(false);
                    lastSeq = evt.Seq;
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
        {
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { /* best effort */ }
        }
    }

    private async Task StreamTranscriptAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        ctx.Response.SendChunked = true;
        var output = ctx.Response.OutputStream;
        var transcriptPath = Path.Combine(_plan.StateDir, "transcript.jsonl");
        var lastSeq = ParseSince(ctx);
        // SC2.4: one backlog read on connect (to honour ?since=), then bytes appended since — instead
        // of deserialising every transcript line ever written, once a second, forever.
        var tail = new FileLineTail();
        tail.Follow(transcriptPath);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var raw in tail.ReadAppended())
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    TranscriptLine? line;
                    try { line = JsonSerializer.Deserialize(raw, TranscriptJsonContext.Default.TranscriptLine); }
                    catch (JsonException) { continue; }
                    if (line == null || line.Seq <= lastSeq) continue;
                    // SC7.1: a line written before schema v2 is served upgraded — v1 stamped, tool
                    // name recovered — so a client never has to carry two readers.
                    line = TranscriptLine.ReadV1OrV2(line);
                    var json = JsonSerializer.Serialize(line, TranscriptJsonContext.Default.TranscriptLine);
                    var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                    await output.WriteAsync(frame, ct).ConfigureAwait(false);
                    lastSeq = line.Seq;
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
        {
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { /* best effort */ }
        }
    }

    private static long ParseSince(HttpListenerContext ctx)
        => long.TryParse(ctx.Request.QueryString["since"], out var since) ? since : 0;

    // ── M5.3: native console — stream the current session's RAW agent stdout ──

    /// <summary>Streams the raw agent stdout of the current session over SSE: exactly what the CLI is
    /// printing, straight from the per-session raw log AgentSession tees to <c>.conductor/logs/</c>. This
    /// is the "see what the agent is actually doing" pane; the transcript stream is the parsed/folded view.
    /// A client reconnects with <c>?since=</c> (a line index) to resume. When a new session's log appears
    /// the line counter resets so the pane follows the live session.</summary>
    private async Task StreamConsoleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        ctx.Response.SendChunked = true;
        var output = ctx.Response.OutputStream;
        var since = ParseSince(ctx);
        // SC2.4: the line counter and the byte offset advance together. Before, every poll called
        // File.ReadAllLinesAsync on the WHOLE session log and skipped the lines it had already sent —
        // the pane got quieter as the session grew and the reading got more expensive.
        var tail = new FileLineTail();
        var emitted = 0L;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var path = CurrentRawLogPath();
                if (tail.Follow(path))
                {
                    // A new session started — replay its log from the top, line numbers restart.
                    since = 0;
                    emitted = 0;
                }
                foreach (var text in tail.ReadAppended())
                {
                    var seq = ++emitted;
                    if (seq <= since) continue;
                    var json = JsonSerializer.Serialize(new ConsoleLineDto(seq, text),
                        ControlPlaneJsonContext.Default.ConsoleLineDto);
                    var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                    await output.WriteAsync(frame, ct).ConfigureAwait(false);
                    since = seq;
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
        {
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>The raw log of the session most recently written to — the live one while a session runs.
    /// Chosen by last-write time rather than by parsing a session number, so it needs no fold and is
    /// robust past 999 sessions (where the zero-padded filename stops sorting numerically).</summary>
    private string? CurrentRawLogPath()
    {
        var logsDir = Path.Combine(_plan.StateDir, "logs");
        if (!Directory.Exists(logsDir)) return null;
        try
        {
            return new DirectoryInfo(logsDir).EnumerateFiles("session-*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // GET /processes and POST /processes/kill live in ControlPlaneServer.Processes.cs.

    /// <summary>SC4.1: the third hand-rolled copy of this check, and the third one that let a Win32
    /// access-denied out of <c>HasExited</c> — here it would 500 the whole <c>/processes</c> endpoint.
    /// <see cref="PidLiveness"/> is the one implementation; it treats an un-openable id as alive.</summary>
    private static bool IsProcessAlive(int pid) => PidLiveness.LooksAlive(pid, DateTime.UtcNow);

    private static async Task<string?> TailBgLogAsync(string bgLogDir, int pid, Store.IRunStore? store, string? runId)
    {
        try
        {
            if (!Directory.Exists(bgLogDir)) return null;
            var match = BgLogs.Resolve(bgLogDir, pid, store, runId);
            if (match == null) return null;
            var fs = new FileStream(match, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
            await using (fs.ConfigureAwait(false))
            {
                using var reader = new StreamReader(fs);
                string? last = null, current;
                while ((current = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    if (current.Length > 0) last = current;
                return last;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task WriteSessionsAsync(HttpListenerContext ctx)
    {
        var rows = _store.QuerySessions(_state.RunId);
        var dtos = rows.Select(r => new SessionRowDto(
            Number: r.Number,
            StageId: r.StageId,
            Kind: r.Kind,
            StartedUtc: r.StartedUtc ?? "",
            EndedUtc: r.EndedUtc,
            Outcome: r.Outcome,
            Attempt: r.Attempt,
            ResumeCount: r.ResumeCount,
            GateSummary: r.GateSummary,
            ResultSummary: r.ResultSummary,
            CommitCount: r.CommitCount,
            CostUsd: r.CostUsd,
            TokensIn: r.TokensIn,
            TokensOut: r.TokensOut,
            TokensThink: r.TokensThink,
            TokensCache: r.TokensCache)).ToList();
        await WriteJsonAsync(ctx, new SessionsDto(dtos), ControlPlaneJsonContext.Default.SessionsDto).ConfigureAwait(false);
    }

    private async Task WriteQueryAsync(HttpListenerContext ctx)
    {
        var sql = ctx.Request.QueryString["sql"];
        if (string.IsNullOrWhiteSpace(sql))
        {
            await WriteJsonAsync(ctx, new QueryResultDto([], [], false, "missing 'sql' query parameter"),
                ControlPlaneJsonContext.Default.QueryResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        if (!sql.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(ctx, new QueryResultDto([], [], false, "only SELECT queries are allowed"),
                ControlPlaneJsonContext.Default.QueryResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        const int maxRows = 500;
        try
        {
            var rows = _store.Query(sql);
            var columns = rows.Count > 0 ? rows[0].Keys.ToList() : [];
            var truncated = rows.Count > maxRows;
            var dtoRows = rows.Take(maxRows)
                .Select(r => new QueryRowDto([.. columns.Select(c => Convert.ToString(r[c], System.Globalization.CultureInfo.InvariantCulture) ?? "")]))
                .ToList();
            await WriteJsonAsync(ctx, new QueryResultDto(columns, dtoRows, truncated, null),
                ControlPlaneJsonContext.Default.QueryResultDto).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            await WriteJsonAsync(ctx, new QueryResultDto([], [], false, ex.Message),
                ControlPlaneJsonContext.Default.QueryResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
        }
    }

    private async Task HandleControlPostAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        ControlCommand cmd;
        try
        {
            cmd = ControlFile.Parse(body);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(ctx, new ControlAcceptedDto(false, "malformed JSON body"),
                ControlPlaneJsonContext.Default.ControlAcceptedDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        if (cmd.Action == null)
        {
            await WriteJsonAsync(ctx, new ControlAcceptedDto(false, "unrecognised or missing 'command'"),
                ControlPlaneJsonContext.Default.ControlAcceptedDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        _inbox.Enqueue(cmd);
        await WriteJsonAsync(ctx, new ControlAcceptedDto(true, null),
            ControlPlaneJsonContext.Default.ControlAcceptedDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    private async Task HandleInjectPostAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        InjectRequestDto? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.InjectRequestDto);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(ctx, new InjectAcceptedDto(false, "malformed JSON body", null, null, null),
                ControlPlaneJsonContext.Default.InjectAcceptedDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req?.Content))
        {
            await WriteJsonAsync(ctx, new InjectAcceptedDto(false, "missing 'content'", null, null, null),
                ControlPlaneJsonContext.Default.InjectAcceptedDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        var runId = _state.RunId;
        var recordedUtc = DateTime.UtcNow;
        _store.WriteInjection(runId, "human", null, req.StageId, req.Content);
        await WriteJsonAsync(ctx, new InjectAcceptedDto(true, null, runId, req.StageId, recordedUtc.ToString("O")),
            ControlPlaneJsonContext.Default.InjectAcceptedDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    // ── M5.5: prompt preview ──

    private async Task WritePromptPreviewAsync(HttpListenerContext ctx)
    {
        var stageId = ctx.Request.QueryString["stage"];
        var kind = ctx.Request.QueryString["kind"] ?? "Deliver";
        if (string.IsNullOrWhiteSpace(stageId))
        {
            await WriteJsonAsync(ctx, new PromptPreviewDto("", "", ""),
                ControlPlaneJsonContext.Default.PromptPreviewDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        var stage = _plan.Stages.FirstOrDefault(s => string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase));
        if (stage == null)
        {
            await WriteJsonAsync(ctx, new PromptPreviewDto("", "", ""),
                ControlPlaneJsonContext.Default.PromptPreviewDto, HttpStatusCode.NotFound).ConfigureAwait(false);
            return;
        }
        var prompts = new PromptBuilder(_plan);
        var model = _plan.ResolveAgent(stage).Model ?? _plan.Agent.Model ?? "default";
        var prompt = kind.Equals("Fix", StringComparison.OrdinalIgnoreCase)
            ? prompts.Fix(stage, 0, 1, 2, new PendingFix { FromSession = 0, GateFailures = "(preview mode)", ProgressSummary = "(preview)" })
            : kind.Equals("Verify", StringComparison.OrdinalIgnoreCase)
            ? prompts.Verify(stage, 0, new PendingVerify { FromSession = 0, StageStartHead = "HEAD" })
            : kind.Equals("Audit", StringComparison.OrdinalIgnoreCase)
            ? prompts.Audit(stage, 0, new PendingAudit { StageId = stage.Id, StageStartHead = "HEAD" }, "HEAD")
            : prompts.Deliver(stage, 0, 1, 1);
        await WriteJsonAsync(ctx, new PromptPreviewDto(prompt, model, kind),
            ControlPlaneJsonContext.Default.PromptPreviewDto).ConfigureAwait(false);
    }

    // ── M5.1: timeline ──

    private async Task WriteTimelineAsync(HttpListenerContext ctx)
    {
        var events = ReadEvents();
        var entries = new List<TimelineEntryDto>();
        foreach (var evt in events)
        {
            string kind = "unknown", desc = "";
            string? stageId = null, outcome = null;
            int? sessionNum = null;
            decimal? cost = null;

            switch (evt)
            {
                case SessionStarted s:
                    kind = "session";
                    desc = $"session #{s.Number} {s.Kind} started";
                    stageId = s.StageId;
                    sessionNum = s.Number;
                    break;
                case SessionFinished f:
                    kind = "session";
                    desc = $"session #{f.Number} finished: {f.Outcome}";
                    stageId = f.StageId;
                    sessionNum = f.Number;
                    cost = f.CostUsd;
                    outcome = f.Outcome;
                    break;
                case GateFinished g:
                    kind = "gate";
                    desc = $"gate {g.Name}: {(g.Passed ? "pass" : "FAIL")} ({g.DurationMs}ms)";
                    stageId = g.Scope;
                    outcome = g.Passed ? "pass" : "fail";
                    break;
                case TokenDelta:
                    break; // skip — too noisy for timeline
                case AttentionRequested a:
                    kind = "attention";
                    desc = $"needs human: {a.Reason}";
                    break;
                case StageEntered se:
                    kind = "stage";
                    desc = $"stage {se.StageId} entered";
                    stageId = se.StageId;
                    break;
                case StageConfirmed sConfirmed:
                    kind = "stage";
                    desc = $"stage {sConfirmed.StageId} confirmed";
                    stageId = sConfirmed.StageId;
                    break;
                case PlanReloaded p:
                    kind = "run";
                    desc = $"plan reloaded — v{p.PlanVersion} · {p.Stages} stages · {p.Gates} gates";
                    break;
                default:
                    continue;
            }
            entries.Add(new TimelineEntryDto(
                Utc: evt.Ts.ToString("O"),
                Kind: kind,
                Description: desc ?? "",
                StageId: stageId,
                SessionNumber: sessionNum,
                CostUsd: cost,
                Outcome: outcome));
        }
        await WriteJsonAsync(ctx, new TimelineDto(entries),
            ControlPlaneJsonContext.Default.TimelineDto).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext ctx, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        ctx.Response.StatusCode = (int)status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }
}
