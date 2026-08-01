using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Models;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// FU-OWNER-11 — Telegram pushes carried no identity. A notification read as
/// <c>s2 NoProgress — P0</c> plus gates and cost, with nothing naming the repo, the plan or the
/// build: one chat receiving two machines' runs cannot attribute a line, and a message read hours
/// later cannot be dated to a binary. The corollary bit for real — a hand-typed operator message was
/// indistinguishable from an engine push and quoted an engine version the run had already
/// superseded.
///
/// <para>The fix has to hold at the CHOKE POINT, not at the call sites, or the next push added
/// anywhere in the engine is anonymous again. So the assertion here is universal and made on the
/// HTTP traffic that actually left the process: <b>every</b> message the run sent, whatever built
/// it, carries the plan and the session. Nothing is mocked — this is <c>conductor run</c> over a
/// real temp git repo, a fake agent, and a stub standing in for api.telegram.org.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class FuOwner11PushIdentityTests : IDisposable
{
    private const string ChatId = "515151";
    private const string PlanName = "FuOwner11Plan";

    private readonly string _repo;
    private readonly string _stateDir;
    private readonly ITestOutputHelper _out;

    public FuOwner11PushIdentityTests(ITestOutputHelper output)
    {
        _out = output;
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-fu11-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        _stateDir = Path.Combine(_repo, ".conductor");

        GitRun("init", "-b", "main");
        GitRun("config", "user.email", "fu11@test");
        GitRun("config", "user.name", "FU11 Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# FU-OWNER-11 Test Repo");
        GitRun("add", "README.md");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# FU11 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| T0.1 | identity checkpoint | TODO | | |\n");

        File.WriteAllText(Path.Combine(_repo, "fake-agent.cmd"), string.Join("\r\n",
            "@echo off",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"Delivered T0.1.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.00042,\"tokens\":{\"input\":10,\"output\":5}}}",
            "echo fu11 done> fu11-output.txt",
            "git add fu11-output.txt",
            "git commit -m \"feat: deliver fu11 checkpoint\"",
            "exit /b 0",
            ""));
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (Exception) { }
    }

    private void GitRun(params string[] args)
    {
        var r = ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
    }

    /// <summary>
    /// The whole of FU-OWNER-11 against one live run. Three claims, each of which was false before:
    /// every outbound message names its plan and session; the run announces which repo it is driving;
    /// and it names the engine build that is driving it, read off the binary's own stamp.
    /// </summary>
    [Fact]
    public async Task EveryOutboundMessage_CarriesPlanAndSession_AndRunStartNamesRepoAndEngine()
    {
        using var bot = new FakeBotApi();

        Directory.CreateDirectory(_stateDir);
        SecretsStore.WriteTelegramToken(_stateDir, "fu11-test-token");

        var planPath = Path.Combine(_repo, "fu11.plan.json");
        var plan = new PlanConfig
        {
            Name = PlanName,
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "T0", Title = "Identity", Sessions = 1 } },
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
                ApiBaseUrl = bot.Root,
            },
        };
        plan.Report.Commit = false;
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts));

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

        var sent = bot.Snapshot();
        Assert.True(sent.Count > 0, "the run sent nothing at all — there is no identity to check");

        // The evidence artifact wants what a human would actually see in the chat, not a green tick.
        _out.WriteLine("---- FU-OWNER-11 wire transcript (verbatim sendMessage text) ----");
        foreach (var m in sent) _out.WriteLine(m.Replace("\n", "\n    ", StringComparison.Ordinal));
        _out.WriteLine("---- end transcript ----");

        // 1. UNIVERSAL. Not "the session-end push carries it" — every message does, because the stamp
        //    is applied where the payload is built rather than at any call site. A push added to the
        //    engine tomorrow cannot opt out, and that is the actual checkpoint.
        var anonymous = sent.FindAll(m => !m.StartsWith($"<i>{PlanName} · s", StringComparison.Ordinal));
        Assert.True(anonymous.Count == 0,
            "these messages left the process with no plan/session identity: " + string.Join(" || ", anonymous));

        // 2. The repo. A chat serving two checkouts of the same plan is the case that motivated this;
        //    without this line the two are indistinguishable.
        var start = sent.Find(m => m.Contains("run started", StringComparison.Ordinal));
        Assert.True(start is not null,
            "no run-start message reached the wire. Sent: " + string.Join(" || ", sent));
        Assert.Contains(_repo, start!, StringComparison.Ordinal);

        // 3. The build, taken from the assembly's own stamp (FU-OWNER-10's field) rather than a
        //    constant someone maintains by hand — the hand-maintained version is precisely what was
        //    wrong in the message that opened this followup.
        Assert.Contains(BuildInfo.Current.Full, start!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The identity is read off the LIVE plan and state on every send, not snapshotted when the
    /// service was constructed. SC1.3 made both reloadable: a plan reload can rename the plan, and
    /// the session counter moves under every message. A snapshot would go quietly stale and mis-date
    /// every later push — the same class of lie the stamp exists to prevent.
    /// </summary>
    [Fact]
    public void IdentityLine_TracksTheLivePlanAndSessionCounter_NotAConstructionSnapshot()
    {
        var plan = new PlanConfig { Name = "First", Repo = _repo, Tracker = "TRACKER.md" };
        var state = new RunState { SessionCounter = 3 };
        using var svc = new TelegramService(plan, state,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramService>.Instance);

        Assert.Equal("<i>First · s3</i>", svc.IdentityLine);

        state.SessionCounter = 4;
        Assert.Equal("<i>First · s4</i>", svc.IdentityLine);
    }

    /// <summary>A plan name carrying markup must not be able to break the HTML parse mode of every
    /// message the run sends — the stamp is prepended to all of them, so an unescaped name would take
    /// the whole notification path down, not one message.</summary>
    [Fact]
    public void IdentityLine_EscapesAPlanNameThatLooksLikeMarkup()
    {
        var plan = new PlanConfig { Name = "a<b>&c", Repo = _repo, Tracker = "TRACKER.md" };
        using var svc = new TelegramService(plan, new RunState { SessionCounter = 1 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramService>.Instance);

        Assert.Equal("<i>a&lt;b&gt;&amp;c · s1</i>", svc.IdentityLine);
    }

    /// <summary>Records what the service actually POSTed to sendMessage. Answers getUpdates with an
    /// empty result: this test is about outbound identity, so nothing needs to come back.</summary>
    private sealed class FakeBotApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<string> _sent = new();

        public string Root { get; }

        public FakeBotApi()
        {
            var port = FreePort();
            Root = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
            _listener.Prefixes.Add(Root + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public List<string> Snapshot()
        {
            lock (_gate) return new List<string>(_sent);
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

                if (string.Equals(method, "sendMessage", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(payload);
                    var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    lock (_gate) _sent.Add(text);
                    body = """{"ok":true,"result":{"message_id":1}}""";
                }
                else if (string.Equals(method, "getUpdates", StringComparison.Ordinal))
                {
                    body = """{"ok":true,"result":[]}""";
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

        public void Dispose()
        {
            try { _listener.Stop(); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
        }
    }
}
