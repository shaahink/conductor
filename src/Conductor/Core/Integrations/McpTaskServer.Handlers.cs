using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Conductor.Core.Events;

namespace Conductor.Core.Integrations;

public partial class McpTaskServer
{
    private JsonElement HandleTaskList(JsonElement? args)
    {
        var cpId = "";
        if (args is { } a && a.TryGetProperty("checkpointId", out var cp))
            cpId = cp.GetString() ?? "";

        RefreshGraph(); // W2.2: cards the owner added mid-session are part of the answer
        var tasks = _graph.ForCheckpoint(cpId);
        var list = tasks.Select(t => new
        {
            taskId = t.TaskId,
            checkpointId = t.CheckpointId,
            title = t.Title,
            status = t.Status,
            source = t.Source,
            order = t.Order,
            // P3: owner-provided extra context rides the task, so the agent sees it too.
            context = t.Context,
            // PF3: declared paths ride along — the agent sees what each card claims to touch.
            paths = t.Paths,
        }).ToArray();

        return JsonSerializer.SerializeToElement(new { tasks = list, count = list.Length });
    }

    private JsonElement HandleTaskUpdate(JsonElement? args)
    {
        var taskId = "";
        var status = "";
        if (args is { } a)
        {
            if (a.TryGetProperty("taskId", out var t)) taskId = t.GetString() ?? "";
            if (a.TryGetProperty("status", out var s)) status = s.GetString() ?? "";
        }

        // Shared write semantics (G2.1): validation + event shape live in TaskWrites so the HTTP
        // control plane's /tasks/update can't drift from this tool.
        RefreshGraph();
        var (evt, error) = TaskWrites.BuildStatusChange(_graph, _runId, taskId, status, source: "agent");
        if (evt is null)
            return JsonSerializer.SerializeToElement(new { ok = false, error });

        WriteEvent(evt);
        _graph.Fold([evt]);

        var actualStatus = _graph.Find(taskId)?.Status ?? "";
        return JsonSerializer.SerializeToElement(new { ok = true, taskId, status = actualStatus });
    }

    private JsonElement HandleTaskAdd(JsonElement? args)
    {
        var cpId = "";
        var title = "";
        var order = 0;
        if (args is { } a)
        {
            if (a.TryGetProperty("checkpointId", out var c)) cpId = c.GetString() ?? "";
            if (a.TryGetProperty("title", out var t)) title = t.GetString() ?? "";
            if (a.TryGetProperty("order", out var o) && o.TryGetInt32(out var ov)) order = ov;
        }

        // W2.2: refresh first — the id is allocated against every writer's events, not a start-of-
        // process snapshot, so two allocators can no longer mint the same id for different cards.
        RefreshGraph();
        var (evt, error) = TaskWrites.BuildAdd(_graph, _runId, cpId, title, order, source: "agent");
        if (evt is null)
            return JsonSerializer.SerializeToElement(new { ok = false, error });

        WriteEvent(evt);
        _graph.Fold([evt]);

        return JsonSerializer.SerializeToElement(new { ok = true, taskId = evt.TaskId, checkpointId = cpId, title, order = evt.Order });
    }

    /// <summary>SC5.1: the MCP half of `conductor task --blocked-until`. Same parse, same ceiling, same
    /// event — <see cref="BlockedUntilRequest"/> owns the rules so the two ingresses cannot drift.
    /// A refusal comes back as an error the agent can act on, never as a silently dropped wait.</summary>
    private JsonElement HandleBlockedUntil(JsonElement? args)
    {
        var until = "";
        var reason = "";
        if (args is { } a)
        {
            if (a.TryGetProperty("until", out var u)) until = u.GetString() ?? "";
            if (a.TryGetProperty("reason", out var r)) reason = r.GetString() ?? "";
        }

        var (untilUtc, error) = BlockedUntilRequest.Parse(until, reason);
        if (untilUtc is not { } resolved)
            return JsonSerializer.SerializeToElement(new { ok = false, error });

        var evt = new BlockedUntilRequested
        {
            RunId = _runId, UntilUtc = resolved, Reason = reason.Trim(), Source = "agent",
        };
        WriteEvent(evt);

        // The reason is knowledge the waking session needs, so it lands in the ledger too — the same
        // pairing the CLI verb makes.
        if (_store != null)
        {
            try { _store.WriteLedger(_runId, _sessionNumber, null, "blocked-until", $"Blocked until {resolved:yyyy-MM-dd HH:mm:ss}Z: {reason.Trim()}"); }
#pragma warning disable CA1031 // best-effort: the event above is the control signal, the ledger is the note
            catch { }
#pragma warning restore CA1031
        }

        return JsonSerializer.SerializeToElement(new
        {
            ok = true,
            until = resolved.ToString("O"),
            reason = reason.Trim(),
            note = "the run loop will sleep until then and spawn one more session; no attempt is burned. End your session now.",
        });
    }

    /// <summary>F1.3: Write a finding/observation to the knowledge ledger.
    /// If run.db is available, writes directly to the ledger table (immediate persistence).
    /// Always emits a <see cref="NoteAdded"/> journal event as a fallback so the note survives
    /// regardless; <see cref="TaskGraph"/> ignores these events by design (notes are not tasks).</summary>
    private JsonElement HandleNote(JsonElement? args)
    {
        var kind = "note";
        var content = "";
        var stageId = "";
        if (args is { } a)
        {
            if (a.TryGetProperty("kind", out var k)) kind = k.GetString() ?? "note";
            if (a.TryGetProperty("content", out var c)) content = c.GetString() ?? "";
            if (a.TryGetProperty("stage_id", out var s)) stageId = s.GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(content))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "content is required" });

        // Best-effort direct write to run.db ledger table (F1.3)
        if (_store != null)
        {
            try
            {
                _store.WriteLedger(_runId, null, string.IsNullOrWhiteSpace(stageId) ? null : stageId, kind, content);
            }
#pragma warning disable CA1031 // catch is best-effort here — journal write is the fallback
            catch { }
#pragma warning restore CA1031
        }

        // Also emit an event so the note appears in event-log projections. W2.2 lands it in run.db
        // immediately when a store is wired: a note journaled and never folded dies with a killed
        // session, which is the exact knowledge loss the ledger exists to prevent.
        var evt = new NoteAdded
        {
            RunId = _runId,
            Kind = kind,
            Content = content,
            StageId = string.IsNullOrWhiteSpace(stageId) ? null : stageId,
        };
        WriteEvent(evt);

        return JsonSerializer.SerializeToElement(new { ok = true, kind, stageId = string.IsNullOrWhiteSpace(stageId) ? null : stageId });
    }

    // ---------------------------------------------------------------- bg tools (F2.4)

    private JsonElement HandleBgStart(JsonElement? args)
    {
        if (_stateDir == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "bg_start requires plan state directory (run via conductor, not standalone MCP)." });

        var command = new List<string>();
        var purpose = "";
        var cwd = _repoPath ?? "";

        if (args is { } a)
        {
            if (a.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cmd.EnumerateArray())
                    command.Add(item.GetString() ?? "");
            }
            if (a.TryGetProperty("purpose", out var p)) purpose = p.GetString() ?? "";
            if (a.TryGetProperty("cwd", out var c)) cwd = c.GetString() ?? cwd;
        }

        if (command.Count == 0)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "command array is required and must not be empty." });

        var exe = command[0];
        var exeArgs = command.Skip(1).ToList();
        if (string.IsNullOrWhiteSpace(purpose))
            purpose = Path.GetFileNameWithoutExtension(exe);

        // W3.3 (bug #2): the OS writes the log, not an in-process pump. The CLI launcher's pump died
        // with the launcher; this one used to die at session end, orphaning the tail of a long build.
        var logDir = Path.Combine(_stateDir, "bg-logs");
        Directory.CreateDirectory(logDir);
        var startedUtc = DateTime.UtcNow;
        var logPath = Path.Combine(logDir, BgLogs.NameFor(purpose, startedUtc));
        var psi = BgLogs.RedirectedSpawn(exe, exeArgs,
            string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd, logPath);

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"Failed to start '{exe}': {ex.Message}" });
        }

        if (proc == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "Process.Start returned null." });

        // The exit watcher survives only as long as this session; the pids row is closed by the lazy
        // PidLiveness sweep every reader runs, which does not care whether we are still here.
        _ = Task.Run(async () =>
        {
            try
            {
                await proc.WaitForExitAsync().ConfigureAwait(false);
                var exitCode = 0;
                try { exitCode = proc.ExitCode; } catch { }
                _store?.MarkPidExited(proc.Id, exitCode);
            }
            catch { }
        });

        if (_store != null)
        {
            try
            {
                // SC4.1: stamp the session — the battery settle needs to know whose child this is.
                _store.TrackPid(proc.Id, _runId, $"bg:{purpose}", null, _sessionNumber, startedUtc);
            }
            catch { }
        }

        return JsonSerializer.SerializeToElement(new { ok = true, pid = proc.Id, purpose, log = logPath });
    }

    private JsonElement HandleBgStatus(JsonElement? args)
    {
        if (_store == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "bg_status requires store (no plan state available)." });

        var rows = _store.GetAllPids(_runId);
        var list = rows.Select(r =>
        {
            var alive = IsProcessAliveMcp(r.Pid);
            var status = r.ExitedUtc != null ? "exited"
                : alive ? "running" : "dead";
            // SC5.4: both operands UTC — `started` is now honestly UTC (SqliteRunStore.ParseUtc), so
            // this stopped subtracting a local clock from a UTC one and reporting a negative age.
            var runtime = r.ExitedUtc != null
                ? Conductor.Commands.BgStatusHandler.FormatDuration(r.ExitedUtc.Value - r.StartedUtc)
                : alive
                    ? Conductor.Commands.BgStatusHandler.FormatDuration(DateTime.UtcNow - r.StartedUtc)
                    : "";
            return new
            {
                pid = r.Pid,
                purpose = r.Purpose,
                status,
                started = r.StartedUtc.ToString("O"),
                runtime,
                // The file `bg_logs` will read for this row — an agent's stream is not under bg-logs/.
                log = BgLogs.IsAgentRow(r) && BgLogs.SessionNumberFor(r) is { } n
                    ? $"logs/{BgLogs.StreamName(n)}"
                    : null,
            };
        }).ToArray();

        return JsonSerializer.SerializeToElement(new { processes = list, count = list.Length });
    }

    private JsonElement HandleBgLogs(JsonElement? args)
    {
        if (_stateDir == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "bg_logs requires plan state directory." });

        var pid = 0;
        var tail = 30;
        if (args is { } a)
        {
            if (a.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pv)) pid = pv;
            if (a.TryGetProperty("tail", out var t) && t.TryGetInt32(out var tv) && tv > 0) tail = tv;
        }
        if (pid <= 0)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "pid is required." });

        // SC5.4 (round-four #4): an agent's output is not under bg-logs/ and never was — its stream is
        // logs/session-NNN.jsonl. `bg_status` lists the agent, so this is a pid an agent WILL ask
        // about; answering "no log file found" sent the operator hunting through 67 unrelated names.
        // The CLI folds this stream through the plan's provider; this server has no plan, so it
        // returns the path (the useful half) with a bounded raw tail beside it.
        var agentRow = BgLogs.FindRow(_store, _runId, pid);
        if (agentRow != null && BgLogs.IsAgentRow(agentRow))
        {
            var number = BgLogs.SessionNumberFor(agentRow);
            var stream = BgLogs.ResolveAgentStream(_stateDir, agentRow);
            if (stream == null)
                return JsonSerializer.SerializeToElement(new
                {
                    ok = false,
                    error = $"pid {pid} is agent session #{number?.ToString() ?? "?"}, but its stream is not on disk.",
                });
            return AgentStreamTail(pid, number, stream, tail);
        }

        var logDir = Path.Combine(_stateDir, "bg-logs");
        if (!Directory.Exists(logDir))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "No bg-logs directory found." });

        var logFile = BgLogs.Resolve(logDir, pid, _store, _runId);
        if (logFile == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"No log file found for PID {pid}." });

        try
        {
#pragma warning disable MA0045 // sync read is deliberate — called from sync MCP handlers
            var allLines = File.ReadAllLines(logFile);
#pragma warning restore MA0045
            var lines = allLines.Length <= tail ? allLines : allLines[^tail..];
            return JsonSerializer.SerializeToElement(new { ok = true, pid, tail, totalLines = allLines.Length, lines });
        }
        catch (IOException ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"Cannot read log: {ex.Message}" });
        }
    }

    /// <summary>SC5.4: the tail of an agent's raw stream. One envelope carries a whole assistant
    /// message, so lines are clipped — an MCP client that wants the full text has the path.</summary>
    private static JsonElement AgentStreamTail(int pid, int? sessionNumber, string stream, int tail)
    {
        try
        {
            var window = new Queue<string>(tail);
            foreach (var line in SharedFileRead.ReadLines(stream))
            {
                window.Enqueue(line.Length <= 400 ? line : line[..399] + "…");
                if (window.Count > tail) window.Dequeue();
            }
            return JsonSerializer.SerializeToElement(new
            {
                ok = true,
                pid,
                sessionNumber,
                stream,
                prompt = sessionNumber is { } n
                    ? Path.Combine(Path.GetDirectoryName(stream)!, BgLogs.PromptName(n))
                    : null,
                tail,
                lines = window.ToArray(),
            });
        }
        catch (IOException ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"Cannot read stream: {ex.Message}" });
        }
    }

    private JsonElement HandleBgStop(JsonElement? args)
    {
        var pid = 0;
        if (args is { } a && a.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pv))
            pid = pv;
        if (pid <= 0)
            return JsonSerializer.SerializeToElement(new { ok = false, error = "pid is required." });

        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
            _store?.MarkPidExited(pid, -1);
            return JsonSerializer.SerializeToElement(new { ok = true, pid, killed = true });
        }
        catch (ArgumentException)
        {
            _store?.MarkPidExited(pid, null);
            return JsonSerializer.SerializeToElement(new { ok = true, pid, killed = false, reason = "Process not found (already exited)." });
        }
        catch (InvalidOperationException)
        {
            _store?.MarkPidExited(pid, null);
            return JsonSerializer.SerializeToElement(new { ok = true, pid, killed = false, reason = "Process already exited." });
        }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"Failed to kill PID {pid}: {ex.Message}" });
        }
    }

    private static bool IsProcessAliveMcp(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch { return false; }
    }
}
