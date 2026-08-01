using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Hosting;
using Conductor.Core.Integrations;
using Conductor.Core.Orchestration;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SF0.3 — pids and background work tell the truth (core-run bugs #9, #5, #12, #13).
///
/// <para>Every assertion here was written against the engine BEFORE the fix and observed failing;
/// the four bugs are independent defects that share one cause — a surface that answered the
/// liveness/sharing question with its own private copy of the rule instead of the one the project
/// already settled on.</para>
///
/// <list type="bullet">
/// <item><b>#9</b> <c>McpTaskServer.IsProcessAliveMcp</c> was <c>!Process.HasExited</c> with a bare
/// <c>catch { return false; }</c> — the exact inversion of <see cref="PidLiveness.LooksAlive"/> for a
/// pid it cannot inspect, and blind to a RECYCLED id it can. So MCP <c>bg_status</c> buried live
/// children and resurrected dead ones, while the CLI's table said the opposite about the same row.</item>
/// <item><b>#5</b> the same question asked of a pid the OS refuses to open used to take
/// <c>conductor bg status</c> down with a Win32 access-denied. SC4.1 routed the CLI through
/// <see cref="PidLiveness"/>; this pins it so it cannot regress, and so the ledger row closes on a
/// measurement rather than on a doc comment.</item>
/// <item><b>#12</b> <c>bg start</c> handed the caller's own stdout handle to a detached grandchild,
/// so <c>conductor bg start ... | anything</c> blocked until that child exited — the opposite of what
/// the verb is for.</item>
/// <item><b>#13</b> MCP <c>bg_logs</c> read a bg child's log with bare <c>File.ReadAllLines</c>, whose
/// <c>FileShare.Read</c> cannot open a file a writer holds — i.e. it failed at the one case the verb
/// exists for. SC2.4 fixed the CLI path and left this one.</item>
/// </list>
/// </summary>
public sealed class SF0_3PidsAndBackgroundWorkTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"conductor-sf03-{Guid.NewGuid():N}");
    private readonly string _stateDir;
    private readonly string _planPath;
    private readonly SqliteRunStore _store;
    private const string RunId = "run-sf03";
    private const string PlanName = "sf03";

    /// <summary>Windows' System process. It exists for the whole life of the machine and cannot be
    /// opened even from an elevated process, which makes it the only genuinely uninspectable pid
    /// available to a test. The tests that rely on that ASSERT it rather than guarding on it, so a
    /// machine where it stops holding fails loudly instead of passing vacuously.</summary>
    private const int SystemPid = 4;

    public SF0_3PidsAndBackgroundWorkTests()
    {
        _stateDir = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(Path.Combine(_stateDir, "bg-logs"));
        Directory.CreateDirectory(Path.Combine(_stateDir, "logs"));
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# T");

        _planPath = Path.Combine(_repo, "test.plan.json");
        var seed = new PlanConfig
        {
            Name = PlanName,
            Repo = _repo.Replace("\\", "/", StringComparison.Ordinal),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "SF0", Title = "Ledger", Sessions = 1 }],
        };
        File.WriteAllText(_planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        _store = new SqliteRunStore(Path.Combine(_stateDir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.InitializeRun(RunId, PlanName, _repo, "feat/sarban", "1.0");
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- bug #9: one liveness policy

    /// <summary>The deterministic half of bug #9, and the one that needs no special pid: a tracked id
    /// the OS has since handed to somebody else. <see cref="PidLiveness"/> settles that with the start
    /// time — a process that started long after we recorded ours is not ours — and MCP had no such
    /// notion at all, so it reported a stranger's process as this run's live background job.</summary>
    [Fact]
    public async Task McpBgStatus_CallsARecycledPidDead_LikeTheCliDoes()
    {
        using var self = Process.GetCurrentProcess();
        // Tracked three hours ago; the OS says this pid started minutes ago. Same id, different process.
        var tracked = DateTime.UtcNow.AddHours(-3);
        _store.TrackPid(self.Id, RunId, "bg:recycled", "SF0", 3, tracked);

        Assert.Equal(PidState.Recycled, PidLiveness.Check(self.Id, tracked));
        Assert.Equal("dead", await McpStatusFor(self.Id).ConfigureAwait(true));
    }

    /// <summary>The half the ledger row names: a pid this process may not open. The policy SC4.1 set is
    /// that cannot-inspect means ALIVE — <see cref="PidLiveness.Sweep"/> refuses to bury such a row, so
    /// no reporting surface may bury it either. MCP answered <c>false</c>, so an agent's own
    /// <c>bg_status</c> could call its running build dead and start a second one.</summary>
    [Fact]
    public async Task McpBgStatus_CallsAnUninspectablePidRunning_NotDead()
    {
        if (!OperatingSystem.IsWindows()) return; // pid 4 is the Windows System process
        var tracked = DateTime.UtcNow.AddMinutes(-5);
        // Stated as an assertion, not a silent guard: if this ever stops holding, the two tests below
        // would pass for the wrong reason and bug #9's real case would go untested again.
        Assert.Equal(PidState.Unverifiable, PidLiveness.Check(SystemPid, tracked));

        _store.TrackPid(SystemPid, RunId, "bg:uninspectable", "SF0", 3, tracked);

        Assert.True(PidLiveness.LooksAlive(SystemPid, tracked));
        Assert.Equal("running", await McpStatusFor(SystemPid).ConfigureAwait(true));
    }

    /// <summary>The property the checkpoint actually asks for, stated as one assertion: for every row
    /// in the store, the word MCP prints and the word the CLI's table prints come from the same
    /// decision. Parity is what stops the two surfaces disagreeing about the same pid again.</summary>
    [Fact]
    public async Task McpBgStatus_AgreesWithTheCliOnEveryRow()
    {
        using var self = Process.GetCurrentProcess();
        _store.TrackPid(self.Id, RunId, "bg:ours", "SF0", 3, DateTime.UtcNow);
        _store.TrackPid(self.Id + 1_000_000, RunId, "bg:gone", "SF0", 3, DateTime.UtcNow);
        _store.TrackPid(SystemPid, RunId, "bg:system", "SF0", 3, DateTime.UtcNow.AddMinutes(-5));

        var mcp = await McpBgStatusRows().ConfigureAwait(true);

        foreach (var row in _store.GetAllPids(RunId))
        {
            var cli = PidLiveness.LooksAlive(row.Pid, row.StartedUtc) ? "running" : "dead";
            Assert.Equal(cli, mcp[row.Pid]);
        }
    }

    // ---------------------------------------------------------------- bug #5: bg status survives

    /// <summary>Bug #5 as the operator met it: `conductor bg status` with an unopenable pid in run.db
    /// died with a Win32 stack trace instead of printing a table. This drives the real handler over a
    /// real run.db holding one, and asserts the verb still completes.</summary>
    [Fact]
    public void BgStatus_OverAnUninspectablePid_PrintsATableInsteadOfThrowing()
    {
        if (!OperatingSystem.IsWindows()) return; // pid 4 is the Windows System process
        var tracked = DateTime.UtcNow.AddMinutes(-5);
        _store.TrackPid(SystemPid, RunId, "bg:uninspectable", "SF0", 3, tracked);
        _store.Dispose(); // the CLI opens its own connection, as a separate process would

        var settings = new BgCommand.Settings { Plan = _planPath, Verb = "status" };

        Assert.Equal(0, BgStatusHandler.ExecuteStatus(settings));
    }

    // ---------------------------------------------------------------- bug #12: the leaked stdout

    /// <summary>Bug #12's mechanism. <c>UseShellExecute=false</c> with nothing redirected hands the
    /// launcher's own stdout/stderr/stdin down to the detached shell, which holds them for as long as
    /// the child lives — so a pipe on `conductor bg start` sees no EOF until the background job it was
    /// supposed to detach from finishes. Redirecting the three streams to handles this process owns is
    /// what breaks the inheritance; the log keeps filling because the SHELL writes it (W3.3 bug #2),
    /// not an in-process pump, and that is unchanged.</summary>
    [Fact]
    public void RedirectedSpawn_DoesNotInheritTheCallersConsoleHandles()
    {
        var psi = BgLogs.RedirectedSpawn("dotnet", ["build"], _repo, Path.Combine(_stateDir, "bg-logs", "b.log"));

        Assert.True(psi.RedirectStandardOutput, "the caller's stdout must not reach the detached child");
        Assert.True(psi.RedirectStandardError, "the caller's stderr must not reach the detached child");
        Assert.True(psi.RedirectStandardInput, "a detached child must not be able to eat the caller's stdin");
        // The redirect that carries the output is still the shell's, so nothing depends on this
        // process staying alive to pump it.
        var line = psi.Arguments.Length > 0 ? psi.Arguments : string.Join(" ", psi.ArgumentList);
        Assert.Contains("2>&1", line, StringComparison.Ordinal);
        Assert.Contains("b.log", line, StringComparison.Ordinal);
    }

    /// <summary>Bug #12 end to end, in the shape an operator hits it: a piped `bg start` whose child
    /// outlives the launcher. The test reads the pipe to EOF with a deadline far shorter than the
    /// child's lifetime — before the fix, EOF waited for the child and this timed out.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PipedBgStart_ReturnsWhileTheChildIsStillRunning()
    {
        if (!OperatingSystem.IsWindows()) return; // uses cmd.exe for the long-lived child
        var exe = Path.Combine(AppContext.BaseDirectory, "conductor.exe");
        Assert.True(File.Exists(exe), $"conductor.exe is not beside the test assembly ({exe})");

        // 60s of child, and a 30s patience for the pipe. Before the fix the read blocked for the full
        // 60; after it, EOF arrives as soon as the launcher exits.
        var work = Path.Combine(_repo, "slow.cmd");
        await File.WriteAllTextAsync(work, "@echo off\r\necho started\r\nping -n 61 127.0.0.1 >nul\r\n").ConfigureAwait(true);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = _repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "bg", "start", "-p", _planPath, "--purpose", "sf03slow", "--", "cmd.exe", "/c", work })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(true);
        sw.Stop();

        Assert.Contains("bg started", stdout, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"the pipe stayed open for {sw.Elapsed.TotalSeconds:F1}s — the child's stdout handle is still the caller's");

        // …and the child really did outlive the launcher, so the timing above means detachment and
        // not a child that simply died early.
        var row = _store.GetAllPids(RunId).SingleOrDefault(p => p.Purpose == "bg:sf03slow");
        Assert.NotNull(row);
        Assert.True(PidLiveness.LooksAlive(row!.Pid, row.StartedUtc), "the background child exited early — the timing proves nothing");
        try { using var child = Process.GetProcessById(row.Pid); child.Kill(entireProcessTree: true); } catch (ArgumentException) { }
    }

    // ---------------------------------------------------------------- bug #13: a LIVE log

    /// <summary>Bug #13. The writer is the shell cmd.exe uses for its `&gt;` redirect: it holds the
    /// file for Write and permits readers. A reader that declares <c>FileShare.Read</c> is still
    /// refused, because on Windows the share mode must also permit what the EXISTING handle holds — so
    /// MCP <c>bg_logs</c> answered "Cannot read log … being used by another process" for exactly the
    /// live build the agent wanted to watch.</summary>
    [Fact]
    public async Task McpBgLogs_ReadsABgLogThatIsStillBeingWritten()
    {
        var started = DateTime.UtcNow.AddMinutes(-2);
        _store.TrackPid(5150, RunId, "bg:tests", "SF0", 3, started);
        var log = Path.Combine(_stateDir, "bg-logs", BgLogs.NameFor("tests", started));
        await File.WriteAllLinesAsync(log, ["Determining projects to restore...", "Build succeeded."]).ConfigureAwait(true);

        await using var writer = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var payload = await McpCall("bg_logs", new { pid = 5150, tail = 30 }).ConfigureAwait(true);

        Assert.True(payload.GetProperty("ok").GetBoolean(),
            payload.TryGetProperty("error", out var err) ? err.GetString() : "bg_logs failed");
        var lines = payload.GetProperty("lines").EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Contains("Build succeeded.", lines);
    }

    /// <summary>The CLI half of the same question, kept beside it so the two paths cannot drift apart
    /// again — SC2.4 fixed this one and left the MCP one, which is how bug #13 survived.</summary>
    [Fact]
    public async Task BgLogs_CliReadsTheSameLiveLog()
    {
        var started = DateTime.UtcNow.AddMinutes(-2);
        _store.TrackPid(5151, RunId, "bg:tests", "SF0", 3, started);
        var log = Path.Combine(_stateDir, "bg-logs", BgLogs.NameFor("tests", started));
        await File.WriteAllLinesAsync(log, ["Build succeeded."]).ConfigureAwait(true);
        _store.Dispose();

        await using var writer = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var settings = new BgCommand.Settings { Plan = _planPath, Verb = "logs", PidOrPurpose = "5151" };

        Assert.Equal(0, BgLogsHandler.ExecuteLogs(settings));
    }

    // ---------------------------------------------------------------- FU-OWNER-9: the self-PID guard

    /// <summary>FU-OWNER-9, the most consequential row in <c>followups.md</c>. A fix session read a build
    /// error saying <c>locked by: conductor (15300)</c>, inferred a stale orphan, and ran
    /// <c>Stop-Process -Id 15300</c> on the conductor that was running it. The agent runs unsandboxed by
    /// design, so the guard cannot be a permission — it has to be knowledge the agent did not have:
    /// the supervising pid, as a number it can compare against, in the block every session prompt
    /// carries. Guessing is what killed the run; there is now nothing to guess.</summary>
    [Fact]
    public void ToolContract_NamesTheSupervisingPid_SoStalenessIsNeverInferred()
    {
        var plan = PlanConfig.Load(_planPath);

        var tools = ToolContract.Render(plan);

        Assert.Contains($"PID {Environment.ProcessId}", tools, StringComparison.Ordinal);
        Assert.Contains("CONDUCTOR_PID", tools, StringComparison.Ordinal);
        Assert.Contains("locked by: conductor (PID)", tools, StringComparison.Ordinal);
        // The hazard that grew after the row was filed: a second run on the same machine.
        Assert.Contains("Another repo's run may share this machine", tools, StringComparison.Ordinal);
        // Bug #15: this block rides in a prompt that a cmd.exe-based agent receives as a command-line
        // ARGUMENT, and cmd caps that at 8191 chars. Adding this guard at its first (740-char) length
        // pushed the built-in session prompt from ~7.3k to 8058 and six live harness tests stopped
        // running their agent at all — while the run still reported success. Until #15 lands, the
        // ceiling is the gate: keep the whole contract well inside it.
        Assert.True(tools.Length < 6_000, $"tool contract is {tools.Length} chars — see bug #15 before growing it");
    }

    /// <summary>The warning has to reach the session that actually meets the message — a FIX session is
    /// the one handed gate output, and gate output is where `locked by: conductor (PID)` appears.</summary>
    [Fact]
    public void FixPrompt_WarnsThatLockedByConductorIsTheRunYouAreInside()
    {
        var plan = PlanConfig.Load(_planPath);
        var prompts = new PromptBuilder(plan);

        var fix = prompts.Fix(plan.Stages[0], 4, 2, 3,
            new PendingFix { FromSession = 3, GateFailures = "MSB3027: cannot copy conductor.exe — locked by: conductor (15300)", ProgressSummary = "no commits" });

        Assert.Contains("locked by: conductor (PID)", fix, StringComparison.Ordinal);
        Assert.Contains("CONDUCTOR_PID", fix, StringComparison.Ordinal);
        Assert.Contains($"PID {Environment.ProcessId}", fix, StringComparison.Ordinal);
    }

    /// <summary>The half a prompt assertion cannot prove: the number is in the CHILD's environment, so
    /// an agent can verify a pid rather than take the prompt's word for it. Driven through a real run
    /// with a fake agent that echoes what it was launched with — the same rig W2.1 uses for
    /// <c>CONDUCTOR_PLAN</c>, because the failure this guards against is precisely a variable everyone
    /// believed was being set.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveSession_CarriesConductorPidInTheAgentsEnvironment_AndNamesItInThePrompt()
    {
        if (!OperatingSystem.IsWindows()) return; // the capture agent is a .cmd
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-sf03-live-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(repo);
            ProcResult Git(params string[] a) => ProcessRunner.Run("git", a, repo, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(0, Git("init", "-b", "main").ExitCode);
            Git("config", "user.email", "sf03@test");
            Git("config", "user.name", "SF03");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r").ConfigureAwait(true);
            Git("add", "README.md");
            Assert.Equal(0, Git("commit", "-m", "init", "--no-gpg-sign").ExitCode);
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| SF0.1 | item | TODO | | |\n").ConfigureAwait(true);
            // The tools block is what carries the guard, so the template has to ask for it. cmd.exe
            // truncates a command line at the first newline (W2.1's lesson), which is harmless here:
            // the capture script reads the environment, not its argv, and the COMPOSED prompt is
            // written to .conductor/logs/ in full regardless of what the child was handed.
            await File.WriteAllTextAsync(Path.Combine(repo, "session.md"), "deliver {stage} now.\n\n{tools}\n").ConfigureAwait(true);

            var script = Path.Combine(repo, "capture.cmd");
            await File.WriteAllTextAsync(script, string.Join("\r\n",
                "@echo off", "echo %CONDUCTOR_PID% > capture-pid.txt", "exit /b 0", "")).ConfigureAwait(true);

            var planPath = Path.Combine(repo, "test.plan.json");
            var seed = new PlanConfig
            {
                Name = "sf03-live",
                Repo = repo.Replace("\\", "/", StringComparison.Ordinal),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "SF0", Title = "Ledger", Sessions = 1 }],
                Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", script, "{prompt}"], Provider = "claude", Output = "stream-json" },
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Limits.MaxSessions = 1;
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(true);
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 1), consoleSink: false);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token).ConfigureAwait(true);

            // The agent saw the supervising conductor's real pid — this test host, which IS the engine.
            var seen = (await File.ReadAllTextAsync(Path.Combine(repo, "capture-pid.txt"), CancellationToken.None).ConfigureAwait(true)).Trim();
            Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), seen);

            // …and the prompt it was handed says the same number, so the two cannot disagree.
            var promptPath = Path.Combine(repo, ".conductor", "logs", BgLogs.PromptName(1));
            Assert.True(File.Exists(promptPath), $"no composed prompt at {promptPath}");
            var prompt = await File.ReadAllTextAsync(promptPath, CancellationToken.None).ConfigureAwait(true);
            Assert.Contains($"PID {Environment.ProcessId}", prompt, StringComparison.Ordinal);
            Assert.Contains("locked by: conductor (PID)", prompt, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<string> McpStatusFor(int pid) => (await McpBgStatusRows().ConfigureAwait(false))[pid];

    private async Task<Dictionary<int, string>> McpBgStatusRows()
    {
        var payload = await McpCall("bg_status", new { }).ConfigureAwait(false);
        return payload.GetProperty("processes").EnumerateArray()
            .ToDictionary(p => p.GetProperty("pid").GetInt32(), p => p.GetProperty("status").GetString()!);
    }

    private async Task<JsonElement> McpCall(string tool, object arguments)
    {
        var server = new McpTaskServer(Path.Combine(_stateDir, "events.jsonl"),
            Path.Combine(_stateDir, "mcp-journal.jsonl"), RunId, _store, _stateDir, _repo, 3);
        var request = JsonSerializer.Serialize(
            new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var stdin = new StringReader(request);
        await using var stdout = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.RunAsync(stdin, stdout, cts.Token).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonElement>(
            stdout.ToString().Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)[0]);
        var text = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        return JsonSerializer.Deserialize<JsonElement>(text!);
    }
}
