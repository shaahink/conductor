using System.Net;
using System.Text.Json;
using Conductor.Core;

namespace Conductor.Core.Http;

/// <summary>Supervised-process endpoints: <c>GET /processes</c> lists this run's tracked PIDs with
/// liveness (read from run.db), and <c>POST /processes/kill</c> terminates one from the Face's Procs
/// tab. The kill is delegated to <see cref="ProcessKiller"/>, which only touches a PID this run tracked
/// and still alive — never an arbitrary process, never conductor itself.</summary>
public sealed partial class ControlPlaneServer
{
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

    private async Task HandleProcessKillAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        ProcessKillRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.ProcessKillRequestDto); }
        catch (JsonException)
        {
            await WriteJsonAsync(ctx, new ProcessKillResultDto(false, "malformed JSON body", 0),
                ControlPlaneJsonContext.Default.ProcessKillResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        if (req is null || req.Pid <= 0)
        {
            await WriteJsonAsync(ctx, new ProcessKillResultDto(false, "a positive 'pid' is required", 0),
                ControlPlaneJsonContext.Default.ProcessKillResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }

        var result = ProcessKiller.Kill(_store, _state.RunId, req.Pid);
        await WriteJsonAsync(ctx, new ProcessKillResultDto(result.Ok, result.Error, req.Pid),
            ControlPlaneJsonContext.Default.ProcessKillResultDto,
            result.Ok ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest).ConfigureAwait(false);
    }
}
