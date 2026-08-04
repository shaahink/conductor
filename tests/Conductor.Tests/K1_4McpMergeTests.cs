using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Hosting;
using Conductor.Core.Integrations;
using Conductor.Core.Orchestration;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// K1.4 truth gates â€” a spawned session sees conductor's task tools AND the operator's own MCP servers.
/// <para>Before this, <c>WireMcpServer</c> wrote a config holding only <c>conductor-tasks</c> and launched
/// the child with <c>--strict-mcp-config</c>: a user-scope chrome-devtools server was invisible to every
/// spawned session, which is the field report the checkpoint answers. The live test at the bottom is the
/// one that matters â€” it runs the real orchestrator against a scratch repo that ALREADY has an MCP config
/// in it, and reads the file the child was actually pointed at.</para>
/// </summary>
public sealed class K1_4McpMergeTests
{
    private static string NewDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-k14-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static void Nuke(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Built by concatenation rather than a raw string: these fragments nest, and a raw interpolation
    // whose content ends in a brace needs more '$' than it is worth reading.
    private static string Server(string cmd) =>
        "{\"type\":\"stdio\",\"command\":\"" + cmd + "\",\"args\":[\"--x\"]}";

    private static string Map(params (string Name, string Cmd)[] servers) =>
        "{" + string.Join(",", servers.Select(s => "\"" + s.Name + "\":" + Server(s.Cmd))) + "}";

    // â”€â”€ the claude dialect: three scopes, in the CLI's own precedence order â”€â”€

    [Fact]
    public async Task UserScopeServers_AreFound_SoASessionCanSeeTheOperatorsTools()
    {
        var home = NewDir("home");
        var repo = NewDir("repo");
        try
        {
            Write(Path.Combine(home, ".claude.json"),
                "{\"numStartups\":41,\"mcpServers\":" + Map(("chrome-devtools", "chrome")) + "}");

            var merged = await OperatorMcpServers.ForClaudeAsync(repo, home, CancellationToken.None);

            Assert.Equal(["chrome-devtools"], merged.Servers.Keys);
            Assert.Equal("chrome", merged.Servers["chrome-devtools"].GetProperty("command").GetString());
            Assert.Contains(merged.Sources, s => s.Contains(".claude.json (1)", StringComparison.Ordinal));
        }
        finally { Nuke(home); Nuke(repo); }
    }

    [Fact]
    public async Task ScopePrecedence_IsUserThenProjectThenLocal_TheCliesOwnOrder()
    {
        var home = NewDir("home");
        var repo = NewDir("repo");
        try
        {
            // Same name in all three scopes, plus one server unique to each, so the test proves both
            // "the later scope wins" and "nothing is lost on the way".
            Write(Path.Combine(home, ".claude.json"),
                "{\"mcpServers\":" + Map(("shared", "from-user"), ("only-user", "u")) + ","
                + "\"projects\":{\"" + repo.Replace("\\", "\\\\") + "\":{\"mcpServers\":"
                + Map(("shared", "from-local"), ("only-local", "l")) + "}}}");
            Write(Path.Combine(repo, ".mcp.json"),
                "{\"mcpServers\":" + Map(("shared", "from-project"), ("only-project", "p")) + "}");

            var merged = await OperatorMcpServers.ForClaudeAsync(repo, home, CancellationToken.None);

            Assert.Equal("from-local", merged.Servers["shared"].GetProperty("command").GetString());
            Assert.Equal(["only-local", "only-project", "only-user", "shared"],
                merged.Servers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }
        finally { Nuke(home); Nuke(repo); }
    }

    [Fact]
    public async Task LocalScope_MatchesTheProjectKeyAcrossSeparatorAndCaseDifferences()
    {
        var home = NewDir("home");
        var repo = NewDir("repo");
        try
        {
            // The claude CLI writes the key exactly as it saw the path. A run whose plan carries forward
            // slashes (every plan in this repo does) must still find it.
            Write(Path.Combine(home, ".claude.json"),
                "{\"projects\":{\"" + repo.Replace("\\", "/").ToUpperInvariant() + "\":{\"mcpServers\":"
                + Map(("local-only", "x")) + "}}}");

            var merged = await OperatorMcpServers.ForClaudeAsync(repo.Replace("\\", "/"), home, CancellationToken.None);

            Assert.Contains("local-only", merged.Servers.Keys, StringComparer.Ordinal);
        }
        finally { Nuke(home); Nuke(repo); }
    }

    [Fact]
    public async Task ConductorsOwnName_CannotBeTakenByAnOperatorEntry()
    {
        var home = NewDir("home");
        var repo = NewDir("repo");
        try
        {
            // Handing this name away would point the claim path at someone else's process â€” a run that
            // looks like it simply never claimed anything.
            Write(Path.Combine(home, ".claude.json"),
                "{\"mcpServers\":" + Map(("conductor-tasks", "imposter"), ("fine", "ok")) + "}");

            var merged = await OperatorMcpServers.ForClaudeAsync(repo, home, CancellationToken.None);

            Assert.Equal(["fine"], merged.Servers.Keys);
            Assert.Contains(merged.Notes, n => n.Contains("conductor-tasks", StringComparison.Ordinal)
                                            && n.Contains("conductor's wins", StringComparison.Ordinal));
        }
        finally { Nuke(home); Nuke(repo); }
    }

    [Fact]
    public async Task ABrokenOperatorConfig_DegradesToTheOtherScopes_InsteadOfFailingTheRun()
    {
        var home = NewDir("home");
        var repo = NewDir("repo");
        try
        {
            // A typo in someone's home directory must not stop every session on the machine.
            Write(Path.Combine(home, ".claude.json"), "{ this is not json");
            Write(Path.Combine(repo, ".mcp.json"), "{\"mcpServers\":" + Map(("survivor", "s")) + "}");

            var merged = await OperatorMcpServers.ForClaudeAsync(repo, home, CancellationToken.None);

            Assert.Equal(["survivor"], merged.Servers.Keys);
            Assert.Contains(merged.Notes, n => n.Contains(".claude.json", StringComparison.Ordinal)
                                            && n.Contains("skipped", StringComparison.Ordinal));
        }
        finally { Nuke(home); Nuke(repo); }
    }

    [Fact]
    public async Task NonObjectEntriesAndRoots_AreSkippedWithANote_NotCopiedThrough()
    {
        var home = NewDir("home");
        var repo = NewDir("repo");
        try
        {
            Write(Path.Combine(home, ".claude.json"),
                "{\"mcpServers\":{\"bogus\":\"just-a-string\",\"good\":" + Server("g") + "}}");
            Write(Path.Combine(repo, ".mcp.json"), "[1,2,3]");

            var merged = await OperatorMcpServers.ForClaudeAsync(repo, home, CancellationToken.None);

            Assert.Equal(["good"], merged.Servers.Keys);
            Assert.Contains(merged.Notes, n => n.Contains("'bogus' is String", StringComparison.Ordinal));
            Assert.Contains(merged.Notes, n => n.Contains("root is Array", StringComparison.Ordinal));
        }
        finally { Nuke(home); Nuke(repo); }
    }

    [Fact]
    public async Task NoOperatorConfigAnywhere_IsSilent_NotAnError()
    {
        var home = NewDir("home");
        var repo = NewDir("repo");
        try
        {
            var claude = await OperatorMcpServers.ForClaudeAsync(repo, home, CancellationToken.None);
            var opencode = await OperatorMcpServers.ForOpencodeAsync(repo, home, CancellationToken.None);

            Assert.Empty(claude.Servers);
            Assert.Empty(claude.Notes);
            Assert.Empty(opencode.Servers);
            Assert.Empty(opencode.Notes);
        }
        finally { Nuke(home); Nuke(repo); }
    }

    // â”€â”€ the opencode dialect: a different key, a different file, comments allowed â”€â”€

    [Fact]
    public async Task OpencodeConfigs_AreReadFromTheMcpKey_GlobalThenRepo()
    {
        var home = NewDir("home");
        var repo = NewDir("repo");
        try
        {
            Write(Path.Combine(home, ".config", "opencode", "opencode.json"),
                """{"mcp":{"shared":{"type":"local","command":["global"]},"only-global":{"type":"local","command":["g"]}}}""");
            // .jsonc, with the comments and trailing comma an operator actually writes.
            Write(Path.Combine(repo, "opencode.jsonc"), """
                {
                  // the repo's own server wins the name
                  "mcp": {"shared": {"type": "local", "command": ["repo"]},},
                }
                """);

            var merged = await OperatorMcpServers.ForOpencodeAsync(repo, home, CancellationToken.None);

            Assert.Equal("repo", merged.Servers["shared"].GetProperty("command")[0].GetString());
            Assert.Contains("only-global", merged.Servers.Keys, StringComparer.Ordinal);
        }
        finally { Nuke(home); Nuke(repo); }
    }

    [Fact]
    public async Task TheClaudeKeyIsNotReadForOpencode_AndViceVersa()
    {
        var home = NewDir("home");
        var repo = NewDir("repo");
        try
        {
            // Each dialect reads its own operator config. Cross-wiring them would hand an opencode
            // session a stdio spec it cannot launch.
            Write(Path.Combine(repo, ".mcp.json"), "{\"mcpServers\":" + Map(("claude-only", "c")) + "}");
            Write(Path.Combine(repo, "opencode.json"), """{"mcp":{"opencode-only":{"type":"local","command":["o"]}}}""");

            var claude = await OperatorMcpServers.ForClaudeAsync(repo, home, CancellationToken.None);
            var opencode = await OperatorMcpServers.ForOpencodeAsync(repo, home, CancellationToken.None);

            Assert.Equal(["claude-only"], claude.Servers.Keys);
            Assert.Equal(["opencode-only"], opencode.Servers.Keys);
        }
        finally { Nuke(home); Nuke(repo); }
    }

    // â”€â”€ the live wire: a real run, a repo that already has servers, the file the child was handed â”€â”€

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Integration")]
    public async Task LiveSession_GetsConductorsToolsAndTheOperatorsServers(bool inherit)
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-k14-live-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(repo);
            ProcResult Git(string a) => ProcessRunner.Run("git",
                a.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo, TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email k14@test");
            Git("config user.name K14");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r", CancellationToken.None);
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| K0.1 | item | TODO | | |\n",
                CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(repo, "session.md"), "deliver {stage} now.", CancellationToken.None);

            // THE POINT: the repo already has MCP servers configured, in both dialects, before conductor
            // writes anything. Project scope rather than user scope so the assertion does not depend on
            // whose machine runs the suite.
            await File.WriteAllTextAsync(Path.Combine(repo, ".mcp.json"),
                """{"mcpServers":{"chrome-devtools":{"type":"stdio","command":"npx","args":["chrome-devtools-mcp@latest"]}}}""",
                CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(repo, "opencode.json"),
                """{"mcp":{"operators-own":{"type":"local","command":["some-server"],"enabled":true}}}""",
                CancellationToken.None);

            var script = Path.Combine(repo, "capture.cmd");
            await File.WriteAllTextAsync(script, string.Join("\r\n",
                "@echo off",
                "echo %2 %3 %4 > capture-argv.txt",
                // Both configs are copied before the engine deletes them at session end.
                "copy \".conductor\\mcp-config.claude.json\" capture-mcp.json > nul",
                "copy \".conductor\\mcp-config.json\" capture-mcp-opencode.json > nul",
                "exit /b 0",
                ""), CancellationToken.None);

            var planPath = Path.Combine(repo, "test.plan.json");
            var seed = new PlanConfig
            {
                Name = "k14-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "K0", Title = "Wire", Sessions = 1 }],
                Agent = new AgentConfig
                {
                    Command = "cmd.exe",
                    Args = ["/c", script, "{prompt}"],
                    Provider = "claude",
                    Output = "stream-json",
                    // false is the opt-out a plan sets when it must not depend on the local machine.
                    InheritMcpServers = inherit ? null : false,
                },
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Limits.MaxSessions = 1;
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 1), consoleSink: false);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            var claudeJson = await File.ReadAllTextAsync(Path.Combine(repo, "capture-mcp.json"), CancellationToken.None);
            var opencodeJson = await File.ReadAllTextAsync(Path.Combine(repo, "capture-mcp-opencode.json"), CancellationToken.None);
            Console.WriteLine($"[K1.4 inherit={inherit}] mcp-config.claude.json as the child received it:\n{claudeJson}");
            Console.WriteLine($"[K1.4 inherit={inherit}] mcp-config.json (opencode dialect):\n{opencodeJson}");

            using var claudeCfg = JsonDocument.Parse(claudeJson);
            using var opencodeCfg = JsonDocument.Parse(opencodeJson);
            var claudeServers = claudeCfg.RootElement.GetProperty("mcpServers");
            var opencodeServers = opencodeCfg.RootElement.GetProperty("mcp");

            // 1. conductor's own server is there either way â€” the claim path is never traded away.
            var conductor = claudeServers.GetProperty("conductor-tasks");
            Assert.Equal("stdio", conductor.GetProperty("type").GetString());
            Assert.Contains(state.RunId, conductor.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
            Assert.True(opencodeServers.TryGetProperty("conductor-tasks", out _));

            if (inherit)
            {
                // 2. and so is the operator's, in both dialects, carried through verbatim.
                var chrome = claudeServers.GetProperty("chrome-devtools");
                Assert.Equal("npx", chrome.GetProperty("command").GetString());
                Assert.Equal("chrome-devtools-mcp@latest", chrome.GetProperty("args")[0].GetString());
                Assert.Equal("some-server", opencodeServers.GetProperty("operators-own").GetProperty("command")[0].GetString());
            }
            else
            {
                // 3. the opt-out really opts out: conductor-tasks and nothing else, on this machine
                //    whatever the operator has configured globally.
                Assert.Equal(["conductor-tasks"], claudeServers.EnumerateObject().Select(p => p.Name).ToArray());
                Assert.Equal(["conductor-tasks"], opencodeServers.EnumerateObject().Select(p => p.Name).ToArray());
            }

            // 4. --strict-mcp-config is still passed. It now means "read exactly this merged file",
            //    which is why the merge had to happen engine-side rather than by dropping the flag.
            var argv = await File.ReadAllTextAsync(Path.Combine(repo, "capture-argv.txt"), CancellationToken.None);
            Assert.Contains("--strict-mcp-config", argv, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
