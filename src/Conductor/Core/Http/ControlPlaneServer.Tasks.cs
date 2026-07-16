using System.Net;
using System.Text.Json;
using Conductor.Core.Events;

namespace Conductor.Core.Http;

/// <summary>G2.1: task writes — the Kanban board's move/add. Validation and event shape are shared
/// with the MCP task tools via <see cref="TaskWrites"/>; the events land in the run.db event log
/// (the same log <c>GET /tasks</c> folds), and are flushed before the response so an immediate
/// re-fetch sees the change. Command/Query/Event layering holds: this writes events, never state.</summary>
public sealed partial class ControlPlaneServer
{
    private async Task HandleTaskUpdateAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        TaskUpdateRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.TaskUpdateRequestDto); }
        catch (JsonException) { await TaskErrorAsync(ctx, "malformed JSON body").ConfigureAwait(false); return; }

        var graph = FoldTaskGraph();
        var (evt, error) = TaskWrites.BuildStatusChange(graph, _state.RunId, req?.TaskId, req?.Status);
        if (evt is null) { await TaskErrorAsync(ctx, error ?? "invalid request").ConfigureAwait(false); return; }

        _store.AppendEvent(evt);
        _store.FlushEvents();

        // Report the task's actual post-fold status: transition legality lives in TaskGraph.Fold,
        // so an illegal move is a recorded no-op — exactly the MCP tool's contract.
        graph.Fold([evt]);
        var actual = graph.Find(evt.TaskId)?.Status ?? "";
        await WriteJsonAsync(ctx, new TaskWriteResultDto(true, null, evt.TaskId, actual, null, null, 0),
            ControlPlaneJsonContext.Default.TaskWriteResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    private async Task HandleTaskAddAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        TaskAddRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.TaskAddRequestDto); }
        catch (JsonException) { await TaskErrorAsync(ctx, "malformed JSON body").ConfigureAwait(false); return; }

        var graph = FoldTaskGraph();
        var (evt, error) = TaskWrites.BuildAdd(graph, _state.RunId, req?.CheckpointId, req?.Title, req?.Order ?? 0, source: "human");
        if (evt is null) { await TaskErrorAsync(ctx, error ?? "invalid request").ConfigureAwait(false); return; }

        _store.AppendEvent(evt);
        _store.FlushEvents();

        await WriteJsonAsync(ctx, new TaskWriteResultDto(true, null, evt.TaskId, "todo", evt.CheckpointId, evt.Title, evt.Order),
            ControlPlaneJsonContext.Default.TaskWriteResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    /// <summary>P3: edit a task's own data — title and/or extra context. The confirm step of both
    /// the card-detail editor and the advisor-refine flow; structured task data, never raw prompt.</summary>
    private async Task HandleTaskEditAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        TaskEditRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.TaskEditRequestDto); }
        catch (JsonException) { await TaskErrorAsync(ctx, "malformed JSON body").ConfigureAwait(false); return; }

        var graph = FoldTaskGraph();
        var (evt, error) = TaskWrites.BuildDetailEdit(graph, _state.RunId, req?.TaskId, req?.Title, req?.Context, req?.Paths);
        if (evt is null) { await TaskErrorAsync(ctx, error ?? "invalid request").ConfigureAwait(false); return; }

        _store.AppendEvent(evt);
        _store.FlushEvents();

        graph.Fold([evt]);
        var task = graph.Find(evt.TaskId);
        await WriteJsonAsync(ctx, new TaskWriteResultDto(true, null, evt.TaskId, task?.Status, task?.CheckpointId, task?.Title, task?.Order ?? 0),
            ControlPlaneJsonContext.Default.TaskWriteResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    private TaskGraph FoldTaskGraph()
    {
        var graph = new TaskGraph();
        graph.Fold(ReadEvents());
        return graph;
    }

    private static Task TaskErrorAsync(HttpListenerContext ctx, string reason) =>
        WriteJsonAsync(ctx, new TaskWriteResultDto(false, reason, null, null, null, null, 0),
            ControlPlaneJsonContext.Default.TaskWriteResultDto, HttpStatusCode.BadRequest);
}
