using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Http;

public sealed partial class ControlPlaneServer
{
    private IReadOnlyList<ConductorEvent> ReadEvents()
    {
        return _store.ReadAllEvents(_state.RunId);
    }

    private async Task WriteStateAsync(HttpListenerContext ctx)
    {
        var events = ReadEvents();
        var runState = RunStateProjection.Fold(events);
        var track = ReadTrackerSafe();
        var snap = SnapshotBuilder.Build(_plan, runState, track);
        var dto = ControlPlaneDto.FromSnapshot(snap, runState.RunId, _plan.Repo, _plan.PlanDir);
        dto = WithLiveSessionMetrics(dto, events, runState);
        await WriteJsonAsync(ctx, dto, ControlPlaneJsonContext.Default.StateDto).ConfigureAwait(false);
    }

    /// <summary>M5.4: fold <see cref="TokenDelta"/> for the current session so the ticker's cost/tokens
    /// accrue DURING a session, not only when <c>SessionFinished</c> lands. The 3-arg
    /// <see cref="SnapshotBuilder"/> can't see the event log, so it always reports zero live spend; here
    /// we add the in-flight session's folded deltas on top of the (finished-session) totals it produced.
    /// Once the session finishes its cost is in <see cref="RunState.History"/>, so we stop adding the
    /// live estimate to avoid double-counting.</summary>
    internal static StateDto WithLiveSessionMetrics(StateDto dto, IReadOnlyList<ConductorEvent> events, RunState runState)
    {
        if (runState.SessionCounter <= 0) return dto;

        var current = runState.History.LastOrDefault(h => h.Number == runState.SessionCounter);
        var live = LiveMetrics.ForSession(events, runState.SessionCounter);
        var sessionLive = current is { EndedUtc: null };
        var elapsed = sessionLive && current != null
            ? Math.Max(0, (DateTime.UtcNow - current.StartedUtc).TotalSeconds)
            : dto.SessionElapsedSec;

        return dto with
        {
            AgentActive = sessionLive,
            SessionElapsedSec = elapsed,
            SessionCostUsd = live.CostUsd,
            SessionTokensInput = live.Input,
            SessionTokensOutput = live.Output,
            SessionTokensReasoning = live.Reasoning,
            TotalCostUsd = sessionLive ? dto.TotalCostUsd + live.CostUsd : dto.TotalCostUsd,
            TokensInput = sessionLive ? dto.TokensInput + live.Input : dto.TokensInput,
            TokensOutput = sessionLive ? dto.TokensOutput + live.Output : dto.TokensOutput,
            TokensReasoning = sessionLive ? dto.TokensReasoning + live.Reasoning : dto.TokensReasoning,
        };
    }

    private TrackerSnapshot ReadTrackerSafe()
    {
        try { return ProgressProviderFactory.Create(_plan).Read(_plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

    private async Task WriteTasksAsync(HttpListenerContext ctx)
    {
        var events = ReadEvents();
        var graph = new TaskGraph();
        graph.Fold(events);
        await WriteJsonAsync(ctx, ControlPlaneDto.FromTasks(graph.Tasks), ControlPlaneJsonContext.Default.TasksDto).ConfigureAwait(false);
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
                var events = ReadEvents();
                foreach (var evt in events.Where(e => e.Seq > lastSeq).OrderBy(e => e.Seq))
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
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (File.Exists(transcriptPath))
                {
                    var lines = TranscriptLog.ReadAll(transcriptPath);
                    foreach (var line in lines.Where(l => l.Seq > lastSeq).OrderBy(l => l.Seq))
                    {
                        var json = JsonSerializer.Serialize(line, TranscriptJsonContext.Default.TranscriptLine);
                        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                        await output.WriteAsync(frame, ct).ConfigureAwait(false);
                        lastSeq = line.Seq;
                    }
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
        string? followingPath = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var path = CurrentRawLogPath();
                if (path != followingPath)
                {
                    followingPath = path; // a new session started — replay its log from the top
                    since = 0;
                }
                if (path != null && File.Exists(path))
                {
                    string[] lines;
                    try { lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false); }
                    catch (IOException) { lines = []; }
                    for (var i = 0; i < lines.Length; i++)
                    {
                        var seq = i + 1;
                        if (seq <= since) continue;
                        var json = JsonSerializer.Serialize(new ConsoleLineDto(seq, lines[i]),
                            ControlPlaneJsonContext.Default.ConsoleLineDto);
                        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                        await output.WriteAsync(frame, ct).ConfigureAwait(false);
                        since = seq;
                    }
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

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<string?> TailBgLogAsync(string bgLogDir, int pid)
    {
        try
        {
            if (!Directory.Exists(bgLogDir)) return null;
            var match = Directory.EnumerateFiles(bgLogDir, $"*-{pid}.log").FirstOrDefault();
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
            CommitCount: r.CommitCount)).ToList();
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
