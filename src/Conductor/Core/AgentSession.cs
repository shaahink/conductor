using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Conductor.Core.Events;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Core;

public sealed class AgentEvent
{
    public DateTime Utc { get; } = DateTime.UtcNow;
    public string Kind { get; init; } = "raw"; // system | text | thinking | tool | result | stderr | raw
    public string Text { get; init; } = "";
}

/// <summary>
/// One headless agent run (claude -p / opencode run). Owns the process, the raw-stream tee, and the
/// stall watchdog clock; delegates all wire-format parsing to an <see cref="IAgentProvider"/> (B2.4),
/// so the session core no longer knows any backend's JSON shape. The provider folds text/thinking/
/// tool/cost/token events into a shared <see cref="AgentStreamState"/> the Orchestrator reads back.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _raw;
    private readonly JobObject _job = new();
    private readonly ConcurrentQueue<AgentEvent> _events = new();
    private readonly IAgentProvider _provider;
    private readonly AgentStreamState _stream;
    private readonly Lock _gate = new();
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private IDisposable? _supervisorTrack; // F2.1: tracked handle if supervisor assigned, set after construction

    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
    public string? ResultText => _stream.ResultText;
    public bool ResultIsError => _stream.ResultIsError;
    public decimal? CostUsd => _stream.CostUsd;
    public int? NumTurns => _stream.NumTurns;
    public long? TokensInput => _stream.TokensInput;
    public long? TokensOutput => _stream.TokensOutput;
    public long? TokensReasoning => _stream.TokensReasoning;
    public long? TokensCacheRead => _stream.TokensCacheRead;
    public bool WasKilled { get; private set; }

    private AgentSession(Process proc, StreamWriter raw, IAgentProvider provider, IEventSink? eventSink, string? conductorSessionId, IDisposable? supervisorTrack)
    {
        _proc = proc;
        _raw = raw;
        _provider = provider;
        _supervisorTrack = supervisorTrack;
        // Stamp the conductor session number on every TokenDelta so the LiveMetrics.ForSession fold
        // (B2.6) can attribute live burn to a session — EventLog only stamps Seq/Ts/RunId, not the
        // per-event SessionId, so it MUST be set here or ForSession folds nothing (B2.6 regression).
        Action<long, long, long, long, decimal>? tokenDelta = eventSink != null
            ? (i, o, r, c, cost) => eventSink.Emit(new TokenDelta { SessionId = conductorSessionId, Input = i, Output = o, Reasoning = r, CacheRead = c, CostUsd = cost })
            : null;
        _stream = new AgentStreamState((kind, text) => _events.Enqueue(new AgentEvent { Kind = kind, Text = text }), tokenDelta);
    }

    public static AgentSession Start(AgentConfig cfg, string cwd, string prompt, string sessionId, string? resumeClaudeId, string rawLogPath, IEventSink? eventSink = null, string? conductorSessionId = null, Dictionary<string, string>? extraEnv = null, ProcessSupervisor? supervisor = null)
    {
        var template = (resumeClaudeId != null && cfg.ResumeArgs is { Count: > 0 }) ? cfg.ResumeArgs : cfg.Args;
        var args = template.Select(a => a
            .Replace("{prompt}", prompt)
            .Replace("{sessionId}", sessionId)
            .Replace("{claudeSessionId}", resumeClaudeId ?? sessionId)).ToList();

        var psi = new ProcessStartInfo(cfg.Command)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Apply plan-level env vars first, then any session-level extra env vars (extra wins).
        if (cfg.Env != null)
            foreach (var kv in cfg.Env) psi.Environment[kv.Key] = kv.Value;
        if (extraEnv != null)
            foreach (var kv in extraEnv) psi.Environment[kv.Key] = kv.Value;

        Directory.CreateDirectory(Path.GetDirectoryName(rawLogPath)!);
        var raw = new StreamWriter(rawLogPath, append: false, Encoding.UTF8) { AutoFlush = true };

        var proc = new Process { StartInfo = psi };
        var session = new AgentSession(proc, raw, AgentProviderFactory.Create(cfg), eventSink, conductorSessionId, supervisorTrack: null);
        proc.OutputDataReceived += (_, e) => session.OnLine(e.Data, stderr: false);
        proc.ErrorDataReceived += (_, e) => session.OnLine(e.Data, stderr: true);
        proc.Start();
        session._job.Assign(proc);
        session._supervisorTrack = supervisor?.Track(proc, $"agent:stage:{conductorSessionId ?? sessionId}:session#{conductorSessionId ?? sessionId}");
        try { proc.StandardInput.Close(); } catch { /* agent may not read stdin */ }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return session;
    }

    private void OnLine(string? line, bool stderr)
    {
        if (line == null) return;
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        lock (_gate) { try { _raw.WriteLine((stderr ? "[stderr] " : "") + line); } catch (IOException) { /* raw tee is best-effort; a full/locked disk must not drop the live event below */ } catch (ObjectDisposedException) { /* session tearing down */ } }

        if (stderr) { _events.Enqueue(new AgentEvent { Kind = "stderr", Text = Trunc(line, 220) }); return; }
        _provider.ParseLine(line, _stream);
    }


    private static string Trunc(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }

    public bool HasExited
    {
        // A disposed/never-started process reports as exited so the watchdog stops waiting on it.
        get { try { return _proc.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    public bool TryDequeue(out AgentEvent ev) => _events.TryDequeue(out ev!);

    public void Kill()
    {
        WasKilled = true;
        try { _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    /// <summary>Close the job object, terminating any process the session spawned that is still
    /// running (e.g. a dev server it started and left behind). Call once the agent process has
    /// exited, before independent verification, so strays can't lock build outputs.</summary>
    public void ReapStrays() => _job.Dispose();

    public int WaitForExitCode()
    {
        // A process that never started / was already reaped has no exit code — report -1 (treated as
        // a failed session by the verdict logic) rather than throwing.
        try { _proc.WaitForExit(); return _proc.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    public void Dispose()
    {
        // Teardown is best-effort across three unmanaged-ish handles; each is guarded independently so
        // one already-gone handle can't leak the others. ObjectDisposed/InvalidOperation mean "already
        // released"; nothing here hides a real fault.
        lock (_gate) { try { _raw.Dispose(); } catch (ObjectDisposedException) { } }
        try { _supervisorTrack?.Dispose(); } catch (ObjectDisposedException) { }
        try { _job.Dispose(); } catch (ObjectDisposedException) { }
        try { _proc.Dispose(); } catch (InvalidOperationException) { }
    }
}
