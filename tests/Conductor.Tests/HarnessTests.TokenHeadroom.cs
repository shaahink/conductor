using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Hosting;
using Conductor.Http;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K4.4 - the token gauge driven through the WHOLE engine, then read back over HTTP.
///
/// <para>The unit tests prove the arithmetic and the wire tests prove the JSON. Neither proves the
/// leg that actually carries the number: an agent's own usage output, parsed by a real provider,
/// folded into real events by a real run, and only then projected onto <c>/state</c>. That leg is
/// where this repo has lost a token field before - B13.6 left live cache-read NULL for a whole
/// session and every rail downstream read zero.</para>
///
/// <para>So this runs a real orchestrator against a real temp repo with an agent that reports the
/// spend profile this project actually has: 150,000 tokens anyone can see and 9,850,000 of cache
/// reads. Against a 12M ceiling that is a session five-sixths of the way to being killed, already
/// past its 9.6M nudge - while the three token fields a surface had before this checkpoint still add
/// up to 150k, which is 1.25% of the same ceiling.</para>
/// </summary>
public sealed partial class HarnessTests
{
    /// <summary>One turn, opencode's own usage shape, dominated by cache reads exactly as a real
    /// long-context session is.</summary>
    /// <remarks>PowerShell rather than the sibling harnesses' <c>cmd.exe</c>, and the reason is worth
    /// writing down: setting a session ceiling adds the wrap-up section to the prompt, the prompt is
    /// one argument, and cmd.exe truncates a command line at 8191 characters. The same script under
    /// cmd fails as <c>AgentError</c> the moment a ceiling exists - nothing to do with the ceiling
    /// itself, everything to do with which shell carries the prompt. K1.2's rig made the same
    /// choice.</remarks>
    private static string CacheHeavyAgentScript() => string.Join("\r\n",
        "param([string]$Prompt = \"\")",
        "Write-Output '{\"type\":\"step_start\"}'",
        "Write-Output '{\"type\":\"text\",\"part\":{\"text\":\"Delivering harness checkpoint H0.1.\"}}'",
        "Write-Output '{\"type\":\"step_finish\",\"part\":{\"cost\":0.0004,\"tokens\":" +
        "{\"input\":100000,\"output\":50000,\"cache\":{\"read\":9850000}}}}'",
        "Write-Output '{\"type\":\"text\",\"part\":{\"text\":\"Session complete.\"}}'",
        "Set-Content -Path headroom-output.txt -Value 'harness done'",
        "git add headroom-output.txt",
        "git commit -m 'feat: deliver headroom checkpoint'",
        "exit 0",
        "");

    [Fact]
    public async Task FullCycle_ServesTheTokenHeadroomTheSessionActuallyBurned()
    {
        var script = Path.Combine(_repo, "headroom-agent.ps1");
        await File.WriteAllTextAsync(script, CacheHeavyAgentScript());

        var plan = new PlanConfig
        {
            Name = "HeadroomPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "powershell",
                Args = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Prompt", "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;
        plan.Limits.MaxSessionTokens = 12_000_000; // nudge lands at 9.6M on the rail's default ratio

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        Assert.Equal(0, await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None));

        // 1. The run recorded what the agent reported, cache read included.
        var session = Assert.Single(state.History);
        Assert.True(session.TokensInput == 100_000,
            $"outcome={session.Outcome} kind={session.Kind} in={session.TokensInput} out={session.TokensOutput} " +
            $"cache={session.TokensCacheRead} cost={session.CostUsd} summary={session.ResultSummary}");
        Assert.Equal(50_000, session.TokensOutput);
        Assert.Equal(9_850_000, session.TokensCacheRead);

        // 2. And /state - served by a real listener over a real socket, from the events this run
        //    wrote - reports the ceiling against the number the rail would have acted on.
        var store = host.Services.GetRequiredService<IRunStore>();
        var server = new ControlPlaneServer(plan, state, store, new ConcurrentQueue<ControlCommand>(),
            new NoOpTelegramService(), NullLogger.Instance, FreePort());
        Assert.True(server.Start(), "control plane failed to bind");
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
            var body = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/state");
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var h = root.GetProperty("tokenHeadroom");

            // Verbatim, from this test on 2026-08-05:
            // {"tokens":10000000,"cap":12000000,"nudgeAt":9600000,"toNudge":-400000,
            //  "toCap":2000000,"usedRatio":0.8333333333333334,"live":false}
            Assert.Equal(10_000_000, h.GetProperty("tokens").GetInt64());
            Assert.Equal(12_000_000, h.GetProperty("cap").GetInt64());
            Assert.Equal(9_600_000, h.GetProperty("nudgeAt").GetInt64());
            Assert.Equal(-400_000, h.GetProperty("toNudge").GetInt64());
            Assert.Equal(2_000_000, h.GetProperty("toCap").GetInt64());
            // Deliberately nothing asserted about `live` or the rate here. The store persists events
            // through an async drain, so whether SessionFinished has landed by the time this frame is
            // served depends on how loaded the machine is - it read false alone and true under the
            // full suite. The liveness rules are measured against a controlled clock in
            // K4_4TokenHeadroomTests; what this test is for is the number, and the number is the same
            // either way.

            // 3. The gap this checkpoint exists to close, measured on a real run rather than argued:
            //    what a surface could see before is 1.25% of the ceiling; the truth is 83%.
            var visible = root.GetProperty("sessionTokensInput").GetInt64()
                          + root.GetProperty("sessionTokensOutput").GetInt64()
                          + root.GetProperty("sessionTokensReasoning").GetInt64();
            Assert.Equal(150_000, visible);
            Assert.InRange(h.GetProperty("usedRatio").GetDouble(), 0.83, 0.84);
            Assert.True(visible * 66 < h.GetProperty("tokens").GetInt64(),
                "the visible fields must be shown to be nowhere near the number the rail acts on");
        }
        finally { server.Dispose(); }
    }

    private static int FreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }
}
