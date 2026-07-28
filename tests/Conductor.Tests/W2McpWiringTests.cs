using System.Text;
using System.Text.Json;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Hosting;
using Conductor.Core.Orchestration;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// W2.1 truth gates — the claim path reaches a claude-shaped child. A live run with a
/// claude-provider agent must launch it with <c>--mcp-config</c>/<c>--strict-mcp-config</c> pointing at
/// a config the claude CLI can actually read, and with <c>CONDUCTOR_PLAN</c> in its environment so the
/// in-worker <c>conductor task/bug/note</c> verbs resolve THIS run's plan instead of dying on
/// "Multiple plan files found" (the four U-series crash-*.logs).
/// </summary>
public sealed class W2McpWiringTests
{
    // ── the CLI flags (pure) ──

    [Fact]
    public void ClaudeProvider_GetsMcpConfigAndStrictFlags()
    {
        var args = SessionRunner.McpArgsFor("claude", ["-p", "{prompt}"], @"C:\s\mcp-config.claude.json");
        Assert.Equal(["--mcp-config", @"C:\s\mcp-config.claude.json", "--strict-mcp-config"], args);
    }

    [Fact]
    public void OpencodeProvider_GetsNoExtraArgs_ItReadsTheEnvVarInstead()
    {
        Assert.Empty(SessionRunner.McpArgsFor("opencode", ["run", "{prompt}"], @"C:\s\mcp-config.claude.json"));
        Assert.Empty(SessionRunner.McpArgsFor("text", ["{prompt}"], @"C:\s\mcp-config.claude.json"));
    }

    [Fact]
    public void PlanThatWiresMcpItself_IsLeftAlone()
    {
        // A plan carrying its own --mcp-config keeps full control: a second, conflicting config
        // appended by the orchestrator would be worse than not wiring at all.
        Assert.Empty(SessionRunner.McpArgsFor("claude",
            ["-p", "{prompt}", "--mcp-config", "my-own.json"], @"C:\s\mcp-config.claude.json"));
    }

    // ── CONDUCTOR_PLAN resolution (the crash the env var fixes) ──

    [Fact]
    public void ConductorPlanEnv_ResolvesTheRunsPlan_WhereDiscoveryWouldHaveThrown()
    {
        // The U-series shape exactly: a cwd holding TWO *.plan.json files, output redirected, so the
        // picker cannot run. Discovery throws; CONDUCTOR_PLAN answers before discovery is consulted.
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-w21-plans-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var cwd = Directory.GetCurrentDirectory();
        var prior = Environment.GetEnvironmentVariable("CONDUCTOR_PLAN");
        try
        {
            var mine = Path.Combine(dir, "mine.plan.json");
            File.WriteAllText(mine, "{}");
            File.WriteAllText(Path.Combine(dir, "other.plan.json"), "{}");
            Directory.SetCurrentDirectory(dir);

            Environment.SetEnvironmentVariable("CONDUCTOR_PLAN", null);
            Assert.Throws<InvalidOperationException>(() => new PlanSettings().ResolvePlanPath());

            Environment.SetEnvironmentVariable("CONDUCTOR_PLAN", mine);
            Assert.Equal(mine, new PlanSettings().ResolvePlanPath());

            // An explicit --plan still outranks the injected env var.
            Assert.Equal("explicit.json", new PlanSettings { Plan = "explicit.json" }.ResolvePlanPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONDUCTOR_PLAN", prior);
            Directory.SetCurrentDirectory(cwd);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── the live wire ──

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveClaudeShapedSession_LaunchesWithMcpConfigAndConductorPlan()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w21-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(repo);
            ProcResult Git(string a) => ProcessRunner.Run("git",
                a.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo, TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email w21@test");
            Git("config user.name W21");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| W0.1 | item | TODO | | |\n");

            // A one-line session template. cmd.exe truncates a command line at the first newline, so a
            // normal multi-line prompt would eat every argument after it and the flags under test would
            // be invisible for a reason that has nothing to do with the wiring. Everything else about
            // the run stays real.
            await File.WriteAllTextAsync(Path.Combine(repo, "session.md"), "deliver {stage} now.");

            // A claude-SHAPED fake agent. The plan's own args end with {prompt}, so the orchestrator's
            // appended flags land at %2..%4. It records what it was launched with — the trailing argv,
            // the injected env, and a copy of the config file itself, which the engine deletes at
            // session end (as it should) and which is read from its known path rather than from %3, so
            // the copy does not depend on how cmd split the prompt argument.
            var script = Path.Combine(repo, "capture.cmd");
            await File.WriteAllTextAsync(script, string.Join("\r\n",
                "@echo off",
                "echo %2 %3 %4 > capture-argv.txt",
                "echo %CONDUCTOR_PLAN% > capture-plan.txt",
                "echo %OPENCODE_CONFIG% > capture-opencode.txt",
                "copy \".conductor\\mcp-config.claude.json\" capture-mcp.json > nul",
                "exit /b 0",
                ""));

            var planPath = Path.Combine(repo, "test.plan.json");
            var seed = new PlanConfig
            {
                Name = "w21-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "W0", Title = "Wire", Sessions = 1 }],
                Agent = new AgentConfig
                {
                    Command = "cmd.exe",
                    Args = ["/c", script, "{prompt}"],
                    Provider = "claude",
                    Output = "stream-json",
                },
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Limits.MaxSessions = 1;
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 1), consoleSink: false);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            // 1. The child was launched with the claude MCP flags.
            var argv = await File.ReadAllTextAsync(Path.Combine(repo, "capture-argv.txt"), CancellationToken.None);
            Assert.Contains("--mcp-config", argv, StringComparison.Ordinal);
            Assert.Contains("--strict-mcp-config", argv, StringComparison.Ordinal);
            Assert.Contains("mcp-config.claude.json", argv, StringComparison.Ordinal);

            // 2. CONDUCTOR_PLAN named THIS run's plan — in-worker `conductor task` can now resolve it.
            var seenPlan = (await File.ReadAllTextAsync(Path.Combine(repo, "capture-plan.txt"), CancellationToken.None)).Trim();
            Assert.Equal(plan.PlanFilePath, seenPlan);

            // 3. The opencode env var is still wired — the second dialect did not displace the first.
            var seenOpencode = (await File.ReadAllTextAsync(Path.Combine(repo, "capture-opencode.txt"), CancellationToken.None)).Trim();
            Assert.EndsWith("mcp-config.json", seenOpencode, StringComparison.Ordinal);

            // 4. The file the flag points at is a config the claude CLI can actually load: a
            //    `mcpServers` map with a stdio server whose command+args boot `conductor mcp-serve`
            //    against THIS run. The opencode `{mcp:{command:[exe,...]}}` shape would load as empty.
            var cfgJson = await File.ReadAllTextAsync(Path.Combine(repo, "capture-mcp.json"), CancellationToken.None);
            using var cfg = JsonDocument.Parse(cfgJson);
            var server = cfg.RootElement.GetProperty("mcpServers").GetProperty("conductor-tasks");
            Assert.Equal("stdio", server.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(server.GetProperty("command").GetString()));
            var serverArgs = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Equal("mcp-serve", serverArgs[0]);
            Assert.Contains(state.RunId, serverArgs);
            Assert.Contains(serverArgs, a => a?.EndsWith("events.jsonl", StringComparison.Ordinal) == true);
        }
        finally
        {
            // git marks pack files read-only, so a recursive delete can raise either flavour —
            // and a throwing finally would swallow the real assertion failure.
            try { Directory.Delete(repo, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
