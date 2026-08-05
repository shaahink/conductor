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
/// SC1.1 regression. Telegram was dead for the entire life of the feature and no test caught it,
/// because no test ever started the host: <c>TelegramService</c> is registered as an
/// <c>IHostedService</c>, nothing called <c>StartAsync</c>, so <c>_started</c> stayed false and every
/// <c>PushAsync</c>/<c>PushSessionEndAsync</c> returned early in silence. Unit-testing the service in
/// isolation cannot catch that class of bug — the service was always fine, the WIRING was missing.
///
/// So this drives the real thing: <see cref="RunCommand"/>.<c>ExecuteAsync</c>, the actual
/// <c>conductor run</c> entry point, over a real temp git repo and a fake agent, with a stub Bot API
/// standing in for api.telegram.org. Nothing about Conductor is mocked. The assertions are made on
/// the HTTP traffic that left the process, which is the only evidence that would have been false
/// before the fix and is true after it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SC1TelegramStartsOnRunPathTests : IDisposable
{
    private const string ChatId = "424242";

    private readonly string _repo;
    private readonly string _stateDir;

    public SC1TelegramStartsOnRunPathTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-sc1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        _stateDir = Path.Combine(_repo, ".conductor");

        GitRun("init", "-b", "main");
        GitRun("config", "user.email", "sc1@test");
        GitRun("config", "user.name", "SC1 Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# SC1 Telegram Test Repo");
        GitRun("add", "README.md");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# SC1 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| T0.1 | telegram checkpoint | TODO | | |\n");

        File.WriteAllText(Path.Combine(_repo, "fake-agent.cmd"), string.Join("\r\n",
            "@echo off",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"Delivered T0.1.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.00042,\"tokens\":{\"input\":10,\"output\":5}}}",
            "echo sc1 done> sc1-output.txt",
            "git add sc1-output.txt",
            "git commit -m \"feat: deliver sc1 checkpoint\"",
            "exit /b 0",
            ""));
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (Exception) { }
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
    /// The whole checkpoint in one live run: the service is started by the run path (proved by
    /// getUpdates being polled at all), two-way control answers (a <c>/status</c> message from an
    /// allowed chat comes back as a real reply), and the session-end push is actually delivered
    /// rather than dropped on the floor at shutdown.
    /// </summary>
    [Fact]
    public async Task ConductorRun_StartsTelegram_DeliversSessionEndPush_AndAnswersStatus()
    {
        using var bot = new FakeBotApi(incomingCommand: "/status");

        // The token normally comes from CONDUCTOR_TELEGRAM_TOKEN. Writing it to the per-run secrets
        // store instead keeps the test off process-global state — and off the developer's real token,
        // which is present in this very environment.
        Directory.CreateDirectory(_stateDir);
        SecretsStore.WriteTelegramToken(_stateDir, "sc1-test-token");

        var planPath = Path.Combine(_repo, "sc1.plan.json");
        var plan = new PlanConfig
        {
            Name = "SC1TelegramPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "T0", Title = "Telegram", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", Path.Combine(_repo, "fake-agent.cmd"), "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Telegram = new TelegramConfig
            {
                AllowedChatIds = { ChatId },
                PollIntervalSeconds = 1,
                EnableTwoWay = true,
                ApiBaseUrl = bot.Root,
            },
        };
        plan.Report.Commit = false;
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts));

        // The REAL entry point. `context` is unused by the command, so a null is honest here: this is
        // the same call Spectre makes for `conductor run`, with the same settings object.
        var exit = await new RunCommand().ExecuteAsync(
            null!,
            new RunCommand.Settings
            {
                Plan = planPath,
                Once = true,
                Headless = true,
                NoFace = true,
                NoControlPlane = true,
            });

        Assert.Equal(0, exit);

        // 1. The poll loop ran at all. Before the fix `_started` was false, PollLoopAsync was never
        //    scheduled, and this count was 0 for every run conductor has ever done.
        Assert.True(bot.GetUpdatesCalls > 0,
            "getUpdates was never called — the Telegram hosted service was not started by the run path");

        // 2. Two-way: the /status message from the allowed chat produced a real outbound reply.
        var status = bot.Sent.Find(m => m.Contains("Status:", StringComparison.Ordinal));
        Assert.True(status is not null,
            "no /status reply was sent — two-way control did not answer. Sent: " + bot.Describe());
        Assert.Contains("SC1TelegramPlan", status!, StringComparison.Ordinal);

        // 3. The session-end push was delivered — not merely queued. It is enqueued fire-and-forget as
        //    the run loop's last act, so this also pins the shutdown drain: stop the send loop without
        //    flushing and this message never leaves the process.
        var sessionEnd = bot.Sent.Find(m => m.Contains("s1", StringComparison.Ordinal)
                                            && m.Contains("T0", StringComparison.Ordinal));
        Assert.True(sessionEnd is not null,
            "the session-end push never reached the wire. Sent: " + bot.Describe());
        Assert.Contains("cost:", sessionEnd!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The checkpoint is "every run path", not "the run path I happened to fix". Composing a host and
    /// then never starting its hosted services is precisely the mistake that killed Telegram for the
    /// life of the feature, and it is invisible at the call site — the run works perfectly, it just
    /// says nothing. So it is checked mechanically: build the host, start the services.
    /// </summary>
    [Fact]
    public void EveryProductionRunPath_ThatBuildsTheHost_AlsoStartsItsHostedServices()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var offenders = new List<string>();
        foreach (var file in new DirectoryInfo(Path.Combine(dir!.FullName, "src"))
                     .EnumerateFiles("*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                              && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file.FullName);
            if (!text.Contains("ConductorHost.Build(", StringComparison.Ordinal)) continue;
            if (!text.Contains("StartRunServicesAsync", StringComparison.Ordinal))
                offenders.Add(file.Name);
        }

        Assert.True(offenders.Count == 0,
            "these run paths build the host but never start its hosted services, so Telegram is dead on them: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A stand-in for api.telegram.org that records what the service actually sent. It answers
    /// getUpdates once with a command from an allowed chat, then goes quiet.
    /// </summary>
    private sealed class FakeBotApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly string _incomingCommand;
        private readonly Lock _gate = new();
        private int _served;

        public string Root { get; }
        public int GetUpdatesCalls { get; private set; }

        /// <summary>Message texts the service POSTed to sendMessage, in order.</summary>
        public List<string> Sent { get; } = new();

        public FakeBotApi(string incomingCommand)
        {
            _incomingCommand = incomingCommand;
            var port = FreePort();
            Root = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
            _listener.Prefixes.Add(Root + "/");
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public string Describe()
        {
            lock (_gate) return Sent.Count == 0 ? "(nothing)" : string.Join(" || ", Sent);
        }

        private static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; } // listener stopped — that is the exit condition

                // The path carries the bot token, which on this machine may be the developer's real
                // one; match on the method name only and never record the URL.
                var method = (ctx.Request.Url?.AbsolutePath ?? "").Split('/')[^1];
                string body;

                if (string.Equals(method, "getUpdates", StringComparison.Ordinal))
                {
                    body = NextUpdates();
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
                else
                {
                    body = """{"ok":true,"result":{}}""";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.Close();
            }
        }

        private string NextUpdates()
        {
            lock (_gate)
            {
                GetUpdatesCalls++;
                if (_served > 0) return """{"ok":true,"result":[]}""";
                _served++;
                var msg = new
                {
                    update_id = 1,
                    message = new
                    {
                        message_id = 1,
                        text = _incomingCommand,
                        chat = new { id = long.Parse(ChatId, CultureInfo.InvariantCulture) },
                    },
                };
                return JsonSerializer.Serialize(new { ok = true, result = new[] { msg } });
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (Exception) { }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
        }
    }
}
