using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Cli;

namespace Conductor.Tests;

/// <summary>
/// W3.3 truth gates — the process rails.
///
/// Three independent defects: closing the console window killed a run mid-session with nothing
/// saved (only Ctrl+C was hooked), <c>ReapOrphans</c> tree-killed any live pid recorded in run.db
/// without checking it was still the same process, and <c>conductor bg start</c> attached a log
/// pump and then returned — killing it — so every bg log for a command slower than ~300ms was an
/// empty file that looked healthy.
/// </summary>
public sealed class W3ProcessRailsTests
{
    // ---------------------------------------------------------------- the console close rail

    [Fact]
    public void CloseEvents_StopTheRun_AndBlockUntilItHasSaved()
    {
        foreach (var ctrlType in new[] { ConsoleCtrlRails.CtrlCloseEvent, ConsoleCtrlRails.CtrlLogoffEvent, ConsoleCtrlRails.CtrlShutdownEvent })
        {
            var stopCalled = false;
            var waitedFor = TimeSpan.Zero;
            using (ConsoleCtrlRails.Install(
                       gracefulStop: () => stopCalled = true,
                       waitForStop: grace => { waitedFor = grace; return true; },
                       grace: TimeSpan.FromSeconds(7)))
            {
                Assert.True(ConsoleCtrlRails.Handle(ctrlType));
            }
            Assert.True(stopCalled, $"ctrl type {ctrlType} did not stop the run");
            Assert.Equal(TimeSpan.FromSeconds(7), waitedFor);
        }
    }

    [Fact]
    public async Task CloseHandler_DoesNotReturnBeforeTheSaveCompletes()
    {
        // Windows kills the process the moment this handler returns. If it returns first, the
        // "graceful" stop is decoration — that is the whole risk §7.5 describes.
        using var saving = new ManualResetEventSlim(false);
        var handlerReturned = false;
        using (ConsoleCtrlRails.Install(
                   gracefulStop: () => { },
                   waitForStop: saving.Wait,
                   grace: TimeSpan.FromSeconds(5)))
        {
            var handler = Task.Run(() => { ConsoleCtrlRails.Handle(ConsoleCtrlRails.CtrlCloseEvent); handlerReturned = true; });
            var raced = await Task.WhenAny(handler, Task.Delay(300, CancellationToken.None));
            Assert.NotSame(handler, raced);
            Assert.False(handlerReturned);

            saving.Set();
            await handler.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(handlerReturned);
        }
    }

    [Fact]
    public void CtrlC_IsLeftToCancelKeyPress()
    {
        var stopCalled = false;
        using (ConsoleCtrlRails.Install(gracefulStop: () => stopCalled = true, waitForStop: _ => true))
        {
            Assert.False(ConsoleCtrlRails.Handle(ConsoleCtrlRails.CtrlCEvent));
            Assert.False(ConsoleCtrlRails.Handle(ConsoleCtrlRails.CtrlBreakEvent));
        }
        Assert.False(stopCalled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellingMidSession_LeavesAResumableRun()
    {
        // The other half of the close rail: what the graceful stop must actually leave behind.
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w33-cancel-{Guid.NewGuid():N}");
        try
        {
            var plan = await ScaffoldAsync(repo, string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"working...\"}}",
                "ping -n 30 127.0.0.1 >nul",
                "exit /b 0",
                ""), _ => { });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var run = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
            var code = await run.WaitAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

            Assert.Equal(130, code);
            var rec = Assert.Single(state.History);
            Assert.Equal(SessionOutcome.Interrupted, rec.Outcome);
            Assert.NotNull(rec.EndedUtc);
            // Resumable: the interrupted session is queued to continue, and run.db reopens.
            Assert.NotNull(state.PendingResume);
            using var reopened = new SqliteRunStore(plan.RunDbPath,
                NullLogger<SqliteRunStore>.Instance);
            Assert.NotEmpty(reopened.ReadAllEvents(state.RunId));
        }
        finally { try { TestTemp.DeleteTree(repo); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- the pid-reuse guard

    [Fact]
    public void PidLiveness_TellsOurProcessApartFromARecycledId()
    {
        var self = Environment.ProcessId;
        var actualStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        Assert.Equal(PidState.Ours, PidLiveness.Check(self, actualStart));
        // Recorded long BEFORE this process started → whoever holds the id now is not ours.
        Assert.Equal(PidState.Recycled, PidLiveness.Check(self, actualStart.AddHours(-3)));
        Assert.Equal(PidState.Gone, PidLiveness.Check(pid: 9_999_991, DateTime.UtcNow));
    }

    [Fact]
    public void ReapOrphans_DoesNotKillAProcessThatMerelyReusedThePid()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w33-reap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".conductor"));
        Process? victim = null;
        try
        {
            using var db = new SqliteRunStore(Path.Combine(dir, ".conductor", "run.db"),
                NullLogger<SqliteRunStore>.Instance);
            const string runId = "w33-reap";
            db.InitializeRun(runId, "p", dir, "main", Conductor.Core.EngineStamp.Parse("1.0.0"));

            // A real, live process, recorded as if a PREVIOUS run had spawned it hours ago — i.e.
            // exactly what a recycled pid looks like. The old reaper tree-killed this on sight.
            victim = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;
            db.TrackPid(victim.Id, runId, "bg:someone-elses-work", null, null, DateTime.UtcNow.AddHours(-3));

            using var supervisor = new ProcessSupervisor(NullLogger<ProcessSupervisor>.Instance, runId, db);
            supervisor.ReapOrphans();

            Assert.False(victim.HasExited, "ReapOrphans killed a process that had merely reused the pid");
            // …and the row stops claiming to be live work of this run.
            Assert.DoesNotContain(db.GetOrphanPids(runId), p => p.Pid == victim.Id);
        }
        finally
        {
            try { victim?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            victim?.Dispose();
            try { TestTemp.DeleteTree(dir); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- bug #2: the dead log pump

    [Fact]
    public void RedirectedSpawn_QuotesPathsWithSpaces()
    {
        var psi = BgLogs.RedirectedSpawn(@"C:\tools dir\my tool.exe", ["--flag", "a b"],
            @"C:\repo", @"C:\logs dir\out.log");
        var line = psi.Arguments.Length > 0 ? psi.Arguments : string.Join(" ", psi.ArgumentList);
        Assert.Contains("my tool.exe", line, StringComparison.Ordinal);
        Assert.Contains("out.log", line, StringComparison.Ordinal);
        Assert.Contains("2>&1", line, StringComparison.Ordinal);
        // W3.3's property, restated as what it actually is. This used to assert
        // `!RedirectStandardOutput`, which was a PROXY for "no in-process pump to die" and stopped
        // being one at SF0.3: bug #12 needed those flags set so the launcher's console handles are not
        // inherited by a child that outlives it. The invariant bug #2 cares about is unchanged and is
        // right here on the command line — the SHELL writes the log, so no handle this process owns
        // has to survive for the log to fill. The load-bearing gate on that is the integration test
        // below (BgStart_LogKeepsFilling_AfterTheLauncherHasReturned), which drives a real child.
        Assert.Contains("> \"C:\\logs dir\\out.log\"", line, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BgStart_LogKeepsFilling_AfterTheLauncherHasReturned()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w33-bg-{Guid.NewGuid():N}");
        try
        {
            var plan = await ScaffoldAsync(repo, "@echo off\r\nexit /b 0\r\n", _ => { });
            Directory.CreateDirectory(Path.Combine(repo, ".conductor"));
            // bg start tracks pids against the plan's existing run — give it one.
            using (var seed = new SqliteRunStore(plan.RunDbPath, NullLogger<SqliteRunStore>.Instance))
                seed.InitializeRun(Guid.NewGuid().ToString("N"), plan.Name, repo, "main", Conductor.Core.EngineStamp.Parse("1.0.0"));

            // Emits over ~4 seconds — a hundred times longer than the launcher lives.
            var work = Path.Combine(repo, "slow.cmd");
            await File.WriteAllTextAsync(work, string.Join("\r\n",
                "@echo off",
                "echo line-one",
                "ping -n 3 127.0.0.1 >nul",
                "echo line-two",
                "ping -n 3 127.0.0.1 >nul",
                "echo line-three",
                ""));

            var settings = new BgCommand.Settings { Plan = Path.Combine(repo, "test.plan.json"), Verb = "start", Purpose = "slow" };
            var exit = BgStartHandler.ExecuteStart(settings, new FakeRemainingArgs(["cmd.exe", "/c", work]));
            Assert.Equal(0, exit);

            // ExecuteStart has returned — under the old pump, the log stopped here at 3 bytes.
            // The shell creates the redirect target as it starts, which is a few ms after the
            // launcher hands back; on a loaded machine that is not instant.
            var logDir = Path.Combine(repo, ".conductor", "bg-logs");
            string? logFile = null;
            var appear = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < appear && logFile == null)
            {
                logFile = Directory.GetFiles(logDir, "slow-*.log").FirstOrDefault();
                if (logFile == null) await Task.Delay(100, CancellationToken.None);
            }
            Assert.NotNull(logFile);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            string content = "";
            while (DateTime.UtcNow < deadline)
            {
                content = ReadShared(logFile);
                if (content.Contains("line-three", StringComparison.Ordinal)) break;
                await Task.Delay(200, CancellationToken.None);
            }

            Assert.Contains("line-one", content, StringComparison.Ordinal);
            Assert.Contains("line-three", content, StringComparison.Ordinal);

            // …and the pid recorded in run.db still finds that log.
            using var db = new SqliteRunStore(plan.RunDbPath, NullLogger<SqliteRunStore>.Instance);
            var runId = db.GetLatestRunId(plan.Name);
            var row = Assert.Single(db.GetAllPids(runId!), p => p.Purpose == "bg:slow");
            Assert.Equal(logFile, BgLogs.Resolve(logDir, row.Pid, db, runId));
        }
        finally { try { TestTemp.DeleteTree(repo); } catch (IOException) { } }
    }

    [Fact]
    public void BgLogs_ResolvesLegacyPidNamedFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w33-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var legacy = Path.Combine(dir, "backtest-4242.log");
            File.WriteAllText(legacy, "old world\n");
            Assert.Equal(legacy, BgLogs.Resolve(dir, 4242, null, null));
            Assert.Null(BgLogs.Resolve(dir, 111, null, null));
        }
        finally { try { TestTemp.DeleteTree(dir); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- unbounded spend

    [Fact]
    public void Doctor_WarnsWhenNothingCapsTheSpend()
    {
        var uncapped = new PlanConfig { Name = "p", Repo = "." };
        Assert.Equal("warn", DoctorCommand.CheckBudget(uncapped, 0m, hasRun: false, budgetGrantUsd: 0m, budgetGrantTokens: 0L).State);

        var capped = new PlanConfig { Name = "p", Repo = "." };
        capped.Limits.MaxRunCostUsd = 50m;
        Assert.Equal("ok", DoctorCommand.CheckBudget(capped, 0m, hasRun: false, budgetGrantUsd: 0m, budgetGrantTokens: 0L).State);

        var tokenCapped = new PlanConfig { Name = "p", Repo = "." };
        tokenCapped.Limits.MaxRunTokens = 5_000_000;
        Assert.Equal("ok", DoctorCommand.CheckBudget(tokenCapped, 0m, hasRun: false, budgetGrantUsd: 0m, budgetGrantTokens: 0L).State);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveRun_SaysSoWhenNothingCapsTheSpend()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w33-spend-{Guid.NewGuid():N}");
        try
        {
            var plan = await ScaffoldAsync(repo, string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"hi\"}}",
                "exit /b 0",
                ""), _ => { });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
            await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

            var log = await File.ReadAllTextAsync(Path.Combine(repo, ".conductor", "conductor.log"), CancellationToken.None);
            Assert.Contains("no spend cap", log, StringComparison.Ordinal);
        }
        finally { try { TestTemp.DeleteTree(repo); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- helpers

    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    private static async Task<PlanConfig> ScaffoldAsync(string repo, string agentBody, Action<PlanConfig> tweak)
    {
        Directory.CreateDirectory(repo);
        ProcResult Git(string args) => ProcessRunner.Run("git",
            args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Git("init -b main");
        Git("config user.email w33@test");
        Git("config user.name W33");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
        Git("add README.md");
        Git("commit -m init --no-gpg-sign");
        await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
            "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | the item | TODO | | |\n");

        var agentScript = Path.Combine(repo, "fake-agent.cmd");
        await File.WriteAllTextAsync(agentScript, agentBody);

        var planPath = Path.Combine(repo, "test.plan.json");
        var seed = new PlanConfig
        {
            Name = "w33-live",
            Repo = repo.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "H0", Title = "Rails", Sessions = 1 }],
            Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" },
            Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
        };
        seed.Report.Commit = false;
        tweak(seed);
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return PlanConfig.Load(planPath);
    }

    private sealed class FakeRemainingArgs : IRemainingArguments
    {
        public FakeRemainingArgs(IReadOnlyList<string> raw) => Raw = raw;
        public IReadOnlyList<string> Raw { get; }
        public ILookup<string, string?> Parsed { get; } = Array.Empty<string>().ToLookup(x => x, _ => (string?)null, StringComparer.Ordinal);
    }
}
