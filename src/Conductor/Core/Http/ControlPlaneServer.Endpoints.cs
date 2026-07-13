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
        await WriteJsonAsync(ctx, dto, ControlPlaneJsonContext.Default.StateDto).ConfigureAwait(false);
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

    private async Task WriteProcessesAsync(HttpListenerContext ctx)
    {
        var pids = _store.GetAllPids(_state.RunId);
        var bgLogDir = Path.Combine(_plan.StateDir, "bg-logs");
        var dtos = new List<ProcessDto>(pids.Count);
        foreach (var p in pids)
        {
            var alive = p.ExitedUtc == null && IsProcessAlive(p.Pid);
            var lastLine = p.Purpose.StartsWith("bg:", StringComparison.Ordinal)
                ? await TailBgLogAsync(bgLogDir, p.Pid).ConfigureAwait(false)
                : null;
            dtos.Add(ControlPlaneDto.FromPid(p, alive, lastLine));
        }
        await WriteJsonAsync(ctx, new ProcessesDto(dtos), ControlPlaneJsonContext.Default.ProcessesDto).ConfigureAwait(false);
    }

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
