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

    /// <summary>SC7.1 — on a <c>tool</c> event, the call's extracted structure (name + fields, each
    /// value truncated on its own). Null on every other kind. This is what carries a real
    /// <c>file_path</c> or <c>command</c> to the transcript and to the out-of-repo write check;
    /// <see cref="Text"/> is a rendering of it and cannot be parsed back.</summary>
    public ToolCall? Tool { get; init; }
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
    private long _lastToolCallTicks = DateTime.UtcNow.Ticks;
    private IDisposable? _supervisorTrack; // F2.1: tracked handle if supervisor assigned, set after construction

    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
    public DateTime LastToolCallUtc => new(Interlocked.Read(ref _lastToolCallTicks), DateTimeKind.Utc);
    public string? ResultText => _stream.ResultText;
    public bool ResultIsError => _stream.ResultIsError;
    /// <summary>W3.2: non-null once the provider stream reported a dead credential.</summary>
    public string? AuthFailure => _stream.AuthFailure;
    public decimal? CostUsd => _stream.CostUsd;
    public int? NumTurns => _stream.NumTurns;
    public long? TokensInput => _stream.TokensInput;
    public long? TokensOutput => _stream.TokensOutput;
    public long? TokensReasoning => _stream.TokensReasoning;
    public long? TokensCacheRead => _stream.TokensCacheRead;

    /// <summary>KS7.3 — the cache-WRITE part of <see cref="TokensInput"/>. A subset of it; see
    /// <see cref="AgentStreamState.TokensCacheWrite"/>.</summary>
    public long? TokensCacheWrite => _stream.TokensCacheWrite;
    /// <summary>K4.1: per-turn context high water and mean for this session — how full the window ran,
    /// not how much the session spent in total.</summary>
    public Conductor.Core.Events.ContextWindowStats Context => _stream.Context;
    public bool WasKilled { get; private set; }

    /// <summary>KS7.1: every tool call the permission posture refused during this session, in order.
    /// Empty on a session with no posture, and empty on a posture whose rules never matched — the two
    /// are distinguished by the run log's posture line, which states the rules that were applied.</summary>
    public IReadOnlyList<ToolRefusal> Refusals => _stream.Refusals;

    private AgentSession(Process proc, StreamWriter raw, IAgentProvider provider, IEventSink? eventSink, string? conductorSessionId, IDisposable? supervisorTrack, string? stageId = null)
    {
        _proc = proc;
        _raw = raw;
        _provider = provider;
        _supervisorTrack = supervisorTrack;
        // Stamp the conductor session number on every TokenDelta so the LiveMetrics.ForSession fold
        // (B2.6) can attribute live burn to a session — EventLog only stamps Seq/Ts/RunId, not the
        // per-event SessionId, so it MUST be set here or ForSession folds nothing (B2.6 regression).
        TokenDeltaSink? tokenDelta = eventSink != null
            ? (i, o, r, c, cw, cost) => eventSink.Emit(new TokenDelta { SessionId = conductorSessionId, Input = i, Output = o, Reasoning = r, CacheRead = c, CacheWrite = cw, CostUsd = cost })
            : null;
        _stream = new AgentStreamState((kind, text) =>
        {
            _events.Enqueue(new AgentEvent { Kind = kind, Text = text });
            if (kind == "tool") Interlocked.Exchange(ref _lastToolCallTicks, DateTime.UtcNow.Ticks);
        }, tokenDelta,
        // SC7.1: the structured half of a tool call. Same queue, same watchdog stamp — the only
        // difference is that the event reaching the transcript still knows what path was written.
        onTool: (call, text) =>
        {
            _events.Enqueue(new AgentEvent { Kind = "tool", Text = text, Tool = call });
            Interlocked.Exchange(ref _lastToolCallTicks, DateTime.UtcNow.Ticks);
        },
        // KS7.1: a refusal reaches the event log the moment the wire reports it, stamped with the
        // session that hit it, so `run_query` can answer "did the deny list ever bite" without
        // re-parsing a transcript. It deliberately does NOT stamp the tool clock — see EmitRefusal.
        onRefusal: refusal =>
        {
            eventSink?.Emit(new ToolRefused
            {
                SessionId = conductorSessionId,
                StageId = stageId,
                ToolName = refusal.ToolName,
                Message = refusal.Message,
                ReasonType = refusal.ReasonType,
            });
        });
    }

    /// <summary>Substitutes the arg-template placeholders (<c>{prompt}</c>, <c>{sessionId}</c>,
    /// <c>{claudeSessionId}</c>, <c>{model}</c>). <c>{model}</c> lets a plan route per stage — the plan
    /// editor's model picker sets <see cref="AgentConfig.Model"/>, which lands here. When no model is set
    /// (neither plan-wide nor per-stage), a lone <c>{model}</c> token is dropped along with the model flag
    /// right before it (<c>--model</c>/<c>-m</c>), so the CLI never receives an empty <c>--model</c>.</summary>
    internal static List<string> ResolveArgs(IReadOnlyList<string> template, string prompt, string sessionId, string? resumeClaudeId, string? model)
    {
        var args = new List<string>(template.Count);
        foreach (var tok in template)
        {
            if (tok == "{model}" && string.IsNullOrWhiteSpace(model))
            {
                if (args.Count > 0 && IsModelFlag(args[^1])) args.RemoveAt(args.Count - 1);
                continue;
            }
            args.Add(tok
                .Replace("{prompt}", prompt)
                .Replace("{sessionId}", sessionId)
                .Replace("{claudeSessionId}", resumeClaudeId ?? sessionId)
                .Replace("{model}", model ?? ""));
        }
        return args;
    }

    /// <summary>One definition of "the flag a model name follows" — shared with the advisor's arg
    /// resolution (SC3.4), which drops an unfilled <c>{model}</c> the same way this does.</summary>
    internal static bool IsModelFlag(string s) => s is "--model" or "-m" or "--model=";

    public static AgentSession Start(AgentConfig cfg, string cwd, string prompt, string sessionId, string? resumeClaudeId, string rawLogPath, IEventSink? eventSink = null, string? conductorSessionId = null, Dictionary<string, string>? extraEnv = null, ProcessSupervisor? supervisor = null, IReadOnlyList<string>? extraArgs = null, string? stageId = null, string? forkBaseId = null)
    {
        // KS7.4: three templates, and the order matters. A FORK carries an earlier conversation into a
        // session that keeps its OWN new id ({claudeSessionId} is the base, {sessionId} the new one) —
        // measured to compose on claude 2.1.235, which is what makes it usable without surrendering id
        // control. A RESUME reuses the interrupted session's own id. Neither is reachable unless the
        // plan supplied that template, so an existing plan is byte-for-byte unchanged.
        var template = (forkBaseId is { Length: > 0 } && cfg.ForkArgs is { Count: > 0 }) ? cfg.ForkArgs
            : (resumeClaudeId != null && cfg.ResumeArgs is { Count: > 0 }) ? cfg.ResumeArgs
            : cfg.Args;
        var args = ResolveArgs(template, prompt, sessionId, forkBaseId ?? resumeClaudeId, cfg.Model);
        // W2.1: orchestrator-supplied flags (claude's --mcp-config) go AFTER the plan's own template so
        // a plan can never accidentally position {prompt} behind them.
        if (extraArgs is { Count: > 0 }) args.AddRange(extraArgs);
        // KS7.1: and the posture gets the LAST word on the bypass flag. Stripping here rather than at
        // the plan level covers every session kind through one seam — work, fix, audit, advisor — so a
        // plan cannot declare a restricted posture and still hand one class of session the escape
        // hatch. A posture that names no mode strips nothing: an existing plan is untouched.
        args = PermissionPosture.StripBypass(args, cfg.Permissions);

        // DV2.2, bug #15 — the last chance to refuse, and the FIRST place the whole argv exists.
        // Everything above adds to it: the plan's template, the orchestrator's own --mcp-config, the
        // posture's edits. An argv over the ceiling does not fail loudly — a .cmd/.bat shim, which is
        // what an npm-installed agent CLI is on Windows, truncates or refuses the command line, the
        // agent does nothing at all, and the run scores the session as a short but successful one.
        // Doctor and preflight both warn about this before a run; neither is consulted at spawn, so
        // until now the warning and the launch were two different opinions and only one of them ran.
        // PromptCompositionException is deliberate: RunLoop already parks a run on it (NeedsHuman
        // with the reason), which is the honest outcome for a session that cannot be started.
        var (ceiling, why) = ArgvLimits.CeilingFor(cfg.Command, cwd);
        var argvLength = ArgvLimits.CommandLineLength(cfg.Command, args);
        if (argvLength > ceiling)
            throw new PromptCompositionException(
                $"the composed argv is {argvLength} chars against the {ceiling}-char ceiling ({why}) — refusing to " +
                "spawn, because over that ceiling the agent is truncated or refused and the run would score the " +
                "session as if it had read everything. Shorten promptExtra/packs/stage notes, lower " +
                "batteries.maxBytes, or point agent.command at the real executable rather than a .cmd/.bat shim.");

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
        var session = new AgentSession(proc, raw, AgentProviderFactory.Create(cfg), eventSink, conductorSessionId, supervisorTrack: null, stageId: stageId);
        proc.OutputDataReceived += (_, e) => session.OnLine(e.Data, stderr: false);
        proc.ErrorDataReceived += (_, e) => session.OnLine(e.Data, stderr: true);
        proc.Start();
        session._job.Assign(proc);
        // SC5.4: stamp the row with the stage and session it belongs to. Both columns were NULL for
        // every agent row ever written, and the purpose read `agent:stage:14:session#14` — where the
        // "stage" was the session number a second time, not a stage. That is why `bg logs <agent pid>`
        // had nothing to resolve against: `bg status` could list the session but the row did not say
        // which session it was. The `session#N` tail stays in the purpose as the fallback for rows
        // already in run.db (BgLogs.SessionNumberFor).
        var sessionNumber = int.TryParse(conductorSessionId, System.Globalization.CultureInfo.InvariantCulture, out var sn)
            ? (int?)sn : null;
        session._supervisorTrack = supervisor?.Track(
            proc,
            $"agent:{stageId ?? "stage"}:session#{conductorSessionId ?? sessionId}",
            stageId,
            sessionNumber);
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

        // This runs on the Process stdout callback thread: an exception escaping here is unhandled and
        // takes the whole conductor process down mid-run (killing every other stage with it). Agent
        // output is untrusted — a provider that chokes on one malformed line must cost us that line,
        // not the run. The raw tee above already persisted it, so nothing is lost for forensics.
        try
        {
            _provider.ParseLine(line, _stream);
        }
        catch (Exception ex)
        {
            _events.Enqueue(new AgentEvent { Kind = "raw", Text = Trunc(line, 220) });
            _events.Enqueue(new AgentEvent { Kind = "stderr", Text = $"[parse error: {ex.GetType().Name}] {Trunc(line, 160)}" });
        }
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
