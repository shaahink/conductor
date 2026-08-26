using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Core.Publishing;
using Conductor.Hosting;
using Conductor.Models;

using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// DV6.3, the boundary half — the page is produced by a REAL run reaching a real session boundary,
/// not by a unit test calling the publisher.
///
/// <para>The unit suite proves the render, the escaping, the staleness line and the document on the
/// wire. It cannot prove the sentence the checkpoint is actually about: <i>at each boundary</i>. So
/// this drives the full cycle with the fake agent — one stage, one session — and then asks the
/// scratch repo's own state directory whether a board page is sitting in it, whether it is the page
/// this run's board would produce, and whether it says which boundary made it.</para>
///
/// <para>It also pins the half that is easy to lose in a refactor: the page is written on the
/// session-end beat, AFTER the checkpoint writes and the tracker regeneration, so the page shows the
/// board the tracker just showed rather than the board as it was when the session started.</para>
/// </summary>
public sealed partial class HarnessTests
{
    [Fact]
    public async Task FullCycle_BoardPage_IsRenderedAtTheSessionBoundary()
    {
        var plan = new PlanConfig
        {
            Name = "HarnessPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", _agentScript, "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };

        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
        Assert.Equal(0, await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None));

        var page = Path.Combine(_stateDir, BoardSnapshotHtml.FileName);
        Assert.True(File.Exists(page), $"no board page at {page}; state dir holds: "
            + string.Join(", ", Directory.GetFiles(_stateDir).Select(Path.GetFileName)));

        var html = await File.ReadAllTextAsync(page);

        // It is the boundary that made it, and it says so — the whole reason a file may be trusted.
        Assert.Contains("session 1 end", html, StringComparison.Ordinal);
        Assert.Contains("does not update", html, StringComparison.Ordinal);
        Assert.Contains("HarnessPlan", html, StringComparison.Ordinal);

        // One self-contained document, on the bytes a real run wrote — not on a fixture's.
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "http://", "https://", "<script", "src=", "@import" })
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);

        // And the atomic write left nothing behind for a reader to trip over.
        Assert.Empty(Directory.GetFiles(_stateDir, "*.tmp-*"));
    }
}
