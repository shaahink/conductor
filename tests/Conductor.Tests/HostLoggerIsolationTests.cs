using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conductor.Tests;

/// <summary>
/// SC1 (fix session): a host's log sink belongs to that host and to nothing else.
///
/// <para>Two hosts can exist in one process — the test suite composes many, and a nested/embedded
/// run could too. <c>ConductorHost.Build</c> registered Serilog with the default
/// <c>preserveStaticLogger: false</c>, which does two global things: it assigns the process-wide
/// <c>Serilog.Log.Logger</c>, and it builds the logger factory with a <c>null</c> registered logger,
/// whose disposal path is <c>Log.CloseAndFlush()</c> — "dispose whatever the static logger happens
/// to be right now". So the second host to be built owned the static slot, and the FIRST host to be
/// disposed closed the second host's logger and its file sink out from under a live run. The run
/// kept going and its narration went nowhere: no exception, no warning, a log file holding only the
/// lines written before the collision.</para>
///
/// <para>That is what made <see cref="HostLoggingTests.DryRunWritesStructuredLogWithRunIdCorrelation"/>
/// red under the full battery and green alone — its log had the "conductor start" line and nothing
/// after it. The tests below reproduce the collision deterministically, with no parallelism and no
/// timing: build, dispose, and check that each host's file has its own lines and only its own.</para>
/// </summary>
public sealed class HostLoggerIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"conductor-logiso-{Guid.NewGuid():N}");

    public HostLoggerIsolationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_root); } catch (IOException) { /* sink handle may linger; temp dir */ }
    }

    [Fact]
    public async Task DisposingOneHostDoesNotCloseAnotherHostsLogSink()
    {
        var (repoOld, planOld) = MakeRepo("Old");
        var (repoLive, planLive) = MakeRepo("Live");

        // The production ordering, exactly: an older host is still undisposed when the run of
        // interest composes its own. The newer host owns the static slot, so the OLDER host's
        // disposal is the one that closes the newer host's logger.
        var hostOld = Build(planOld, "run-iso-old");
        var hostLive = Build(planLive, "run-iso-live");

        var logLive = hostLive.Services.GetRequiredService<ILogger<HostLoggerIsolationTests>>();
        logLive.LogInformation("live-before-unrelated-dispose");

        hostOld.Dispose();

        // A live run narrating after some unrelated host went away. This is the line that vanished:
        // no throw, no warning, just a log file that stops mid-run.
        logLive.LogInformation("live-after-unrelated-dispose");
        hostLive.Dispose();

        var textLive = await ReadLogAsync(planLive);
        Assert.Contains("live-before-unrelated-dispose", textLive, StringComparison.Ordinal);
        Assert.Contains("live-after-unrelated-dispose", textLive, StringComparison.Ordinal);

        var oldLog = SingleLogOrNull(planOld);
        if (oldLog is not null)
            Assert.DoesNotContain("live-", await File.ReadAllTextAsync(oldLog), StringComparison.Ordinal);

        Assert.NotEqual(repoOld, repoLive); // the two runs really did write to two different state dirs
    }

    [Fact]
    public void BuildingASecondHostDoesNotStealTheFirstHostsLogger()
    {
        // The other half of the same defect: when the static slot is the delivery target, a logger
        // resolved AFTER a second host was built writes into the second host's file. Ordering inside
        // the run path (build, then resolve the Orchestrator) made this the narrower window of the
        // two, but it is the same global and it fails the same way.
        var (_, planA) = MakeRepo("A");
        var (_, planB) = MakeRepo("B");

        using var hostA = Build(planA, "run-steal-a");
        using var hostB = Build(planB, "run-steal-b");

        var loggerA = hostA.Services.GetRequiredService<ILoggerFactory>();
        var loggerB = hostB.Services.GetRequiredService<ILoggerFactory>();
        Assert.NotSame(loggerA, loggerB);

        loggerA.CreateLogger("iso").LogInformation("late-resolved-A");
        // Flush A only — B stays live, which is exactly the state the old code could not survive.
        hostA.Dispose();

        var textA = File.ReadAllText(SingleLog(planA));
        Assert.Contains("late-resolved-A", textA, StringComparison.Ordinal);
        Assert.False(File.Exists(SingleLogOrNull(planB) ?? "") &&
                     File.ReadAllText(SingleLogOrNull(planB)!).Contains("late-resolved-A", StringComparison.Ordinal),
            "host A's line was delivered to host B's log file — the logger is still process-global");
    }

    // ------------------------------------------------------------------ helpers

    private (string Repo, PlanConfig Plan) MakeRepo(string tag)
    {
        var repo = Path.Combine(_root, tag);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "t.md"),
            "# T\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| S1.1 | first task | TODO | | |\n");
        return (repo, new PlanConfig
        {
            Name = $"logiso-{tag}",
            Repo = repo,
            Tracker = "t.md",
            Stages = { new StageConfig { Id = "S1", Title = "First", Sessions = 1 } },
            Agent = new AgentConfig { Command = "opencode", Args = { "run", "{prompt}" }, Output = "opencode-json" },
        });
    }

    private static Microsoft.Extensions.Hosting.IHost Build(PlanConfig plan, string runId) =>
        ConductorHost.Build(plan, new RunState { RunId = runId }, new PlainSink(),
            new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false);

    private static string SingleLog(PlanConfig plan) =>
        Directory.EnumerateFiles(Path.Combine(plan.StateDir, "logs"), "conductor-*.log").Single();

    private static string? SingleLogOrNull(PlanConfig plan)
    {
        var dir = Path.Combine(plan.StateDir, "logs");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "conductor-*.log").FirstOrDefault()
            : null;
    }

    /// <summary>Reads after the host is disposed, so the flush has already happened; the short retry
    /// only covers the file handle still being released on Windows.</summary>
    private static async Task<string> ReadLogAsync(PlanConfig plan)
    {
        var file = SingleLog(plan);
        for (var i = 0; i < 20; i++)
        {
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (IOException) { await Task.Delay(50); }
        }
        return await File.ReadAllTextAsync(file);
    }
}
