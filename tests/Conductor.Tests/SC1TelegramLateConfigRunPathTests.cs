using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SC1.3 on the real run path. <see cref="SC1TelegramLateConfigTests"/> proves the live service picks
/// up a late token and a late telegram block; this proves the ENGINE hands them to it, through the
/// path an operator actually uses: edit the plan, drop a reload, and the running run starts notifying.
///
/// Nothing here is invoked directly on the service. The run is a real <see cref="RunCommand"/>
/// execution over a real git repo with a fake agent, started from a plan that has NO telegram block
/// and no token at all — the state in which the old engine pinned a <c>NoOpTelegramService</c> for
/// the life of the process. Mid-run the agent writes the telegram block into the plan file, saves a
/// token, and asks for a plan reload; the assertion is that a later session's push reaches the wire.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SC1TelegramLateConfigRunPathTests : IDisposable
{
    private const string ChatId = "838383";

    private readonly string _repo;
    private readonly string _stateDir;

    public SC1TelegramLateConfigRunPathTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-sc13run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        _stateDir = Path.Combine(_repo, ".conductor");

        GitRun("init", "-b", "main");
        GitRun("config", "user.email", "sc13@test");
        GitRun("config", "user.name", "SC13 Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# SC1.3 late-config repo");
        GitRun("add", "README.md");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# SC1.3 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| T0.1 | late telegram config | TODO | | |\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (Exception) { }
    }

    /// <summary>SF0.2 (bug #8): one argument per parameter, exit code asserted — see
    /// <c>HarnessTests.GitRun</c> for what the space-splitting version cost.</summary>
    private void GitRun(params string[] args)
    {
        var r = Conductor.Core.ProcessRunner.Run("git", args,
            _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
    }

    /// <summary>
    /// The checkpoint's live claim: configuration that arrives after the engine started takes effect
    /// on THIS run. Session 1 runs with Telegram unconfigured (and delivers nothing, correctly);
    /// between sessions the engine reloads the plan and hands the new block plus the newly saved token
    /// to the live service; session 2's end-of-session push leaves the process. Before SC1.3 the run
    /// would have finished in silence with every surface reporting the setup as saved.
    /// </summary>
    [Fact]
    public async Task ATelegramBlockAndTokenSavedMidRun_MakeTheCurrentRunDeliver_WithNoRestart()
    {
        using var bot = new StubBotApi();
        var planPath = Path.Combine(_repo, "sc13.plan.json");

        // Plan A — how the run starts: no telegram block anywhere, no token in the secrets store.
        var planA = BuildPlan(withTelegram: false, botRoot: null);
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(planA, PlanConfig.JsonOpts));

        // Plan B — what the operator would save from the Face's Telegram tab (or `plan set`), staged
        // for the fake agent to copy over the plan file mid-run.
        var planB = BuildPlan(withTelegram: true, botRoot: bot.Root);
        var stagedPlan = Path.Combine(_repo, "staged-plan.json");
        await File.WriteAllTextAsync(stagedPlan, JsonSerializer.Serialize(planB, PlanConfig.JsonOpts));

        // The token, staged the same way — it lives outside the plan (secrets store / env var), which
        // is exactly why a plan reload alone was never going to be enough to pick it up. Written by
        // SecretsStore itself rather than by hand, so the staged file is byte-for-byte what the Face's
        // token endpoint would have left there.
        var stagedDir = Path.Combine(_repo, "staged");
        SecretsStore.WriteTelegramToken(stagedDir, "sc13-runpath-token");
        var stagedSecrets = Path.Combine(stagedDir, "secrets.local.json");

        var stagedControl = Path.Combine(_repo, "staged-control.json");
        await File.WriteAllTextAsync(stagedControl, "{\"command\":\"reload-plan\",\"confirmed\":true}");

        // The agent configures Telegram during session 1 and does ordinary work in session 2. The
        // marker file is what makes it happen exactly once.
        await File.WriteAllTextAsync(Path.Combine(_repo, "fake-agent.cmd"), string.Join("\r\n",
            "@echo off",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"working\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.00042,\"tokens\":{\"input\":10,\"output\":5}}}",
            "if exist \"" + Path.Combine(_repo, "configured.marker") + "\" goto :work",
            "copy /Y \"" + stagedPlan + "\" \"" + planPath + "\" >nul",
            "copy /Y \"" + stagedSecrets + "\" \"" + Path.Combine(_stateDir, "secrets.local.json") + "\" >nul",
            "copy /Y \"" + stagedControl + "\" \"" + Path.Combine(_stateDir, "control.json") + "\" >nul",
            "echo done> \"" + Path.Combine(_repo, "configured.marker") + "\"",
            ":work",
            "echo sc13 %RANDOM%> sc13-output.txt",
            "git add sc13-output.txt",
            "git commit -m \"feat: sc13 session work\"",
            "exit /b 0",
            ""));

        var exit = await new RunCommand().ExecuteAsync(
            null!,
            new RunCommand.Settings
            {
                Plan = planPath,
                MaxSessions = 2,
                Headless = true,
                NoFace = true,
                NoControlPlane = true,
            });

        // 1. The service that started life with no telegram block is now polling: the reloaded block
        //    reached it. Before SC1.3 the container held a NoOpTelegramService and this stays 0
        //    forever, whatever the plan file says.
        Assert.True(bot.GetUpdatesCalls > 0,
            $"getUpdates was never called — the telegram block added mid-run never reached a live service (exit {exit}). Sent: {bot.Describe()}");

        // 2. And it delivered: the session-end push for the session that ran AFTER the reload left the
        //    process, with the token that did not exist when the engine started.
        var push = bot.Sent.Find(m => m.Contains("s2", StringComparison.Ordinal));
        Assert.True(push is not null,
            $"no session-end push reached the wire after the late configuration (exit {exit}). Sent: {bot.Describe()}");
        Assert.Contains("T0", push!, StringComparison.Ordinal);
    }

    private PlanConfig BuildPlan(bool withTelegram, string? botRoot)
    {
        var plan = new PlanConfig
        {
            Name = "SC13LatePlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "T0", Title = "Telegram", Sessions = 2 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", Path.Combine(_repo, "fake-agent.cmd"), "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
        };
        plan.Report.Commit = false;
        if (withTelegram)
            plan.Telegram = new TelegramConfig
            {
                AllowedChatIds = { ChatId },
                PollIntervalSeconds = 1,
                EnableTwoWay = true,
                ApiBaseUrl = botRoot,
            };
        return plan;
    }

    /// <summary>A stand-in for api.telegram.org that records what actually left the process.</summary>
    private sealed class StubBotApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly Lock _gate = new();

        public string Root { get; }
        public int GetUpdatesCalls { get; private set; }
        public List<string> Sent { get; } = new();

        public StubBotApi()
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Root = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}";
                probe.Stop();
            }
            _listener.Prefixes.Add(Root + "/");
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public string Describe()
        {
            lock (_gate) return Sent.Count == 0 ? "(nothing)" : string.Join(" || ", Sent);
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            // The path carries the bot token; match on the method name only and never record the URL.
            var method = (ctx.Request.Url?.AbsolutePath ?? "").Split('/')[^1];
            var body = """{"ok":true,"result":[]}""";

            try
            {
                if (string.Equals(method, "getUpdates", StringComparison.Ordinal))
                {
                    lock (_gate) GetUpdatesCalls++;
                }
                else if (string.Equals(method, "getMe", StringComparison.Ordinal))
                {
                    body = """{"ok":true,"result":{"id":1,"username":"sc13_run_bot"}}""";
                }
                else if (string.Equals(method, "sendMessage", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(payload);
                    var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    lock (_gate) Sent.Add(text);
                    body = """{"ok":true,"result":{"message_id":1}}""";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (Exception) { }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
        }
    }
}
