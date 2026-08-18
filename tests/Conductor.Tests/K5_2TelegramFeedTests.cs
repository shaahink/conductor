using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K5.2 — the five defects that made the owner's own feed unreadable, taken from a transcribed
/// client-site run (15 sessions, $97.46):
///
/// <list type="number">
/// <item>the session number printed TWICE, from two sources — the identity line's live
///   <c>_state.SessionCounter</c> and the body's own record number — which a late push can disagree
///   with;</item>
/// <item>the stage rendered as a bare letter (<c>— G</c>), because the id was passed and the title
///   never looked up;</item>
/// <item><c>result:</c> as 700 characters of prose cut blind mid-word;</item>
/// <item>a rollover pushing <c>gates: (not recorded)</c> and nothing about what it landed — while
///   K1.1 had been recording its commits and claims all along;</item>
/// <item>no progress line anywhere in fifteen messages.</item>
/// </list>
///
/// <para>Every assertion here is made on the bytes that left the process: a real
/// <see cref="TelegramService"/>, its real send queue and send loop, POSTing over a real loopback
/// socket to a stub standing in for api.telegram.org. The last test is the whole chain — a real
/// <c>conductor run</c>, so the record-to-message leg in <c>RunLoop.Plumbing</c> is proved too.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class K5_2TelegramFeedTests : IDisposable
{
    private const string ChatId = "424242";
    private const string PlanName = "K52Plan";

    private readonly string _repo;
    private readonly string _stateDir;
    private readonly ITestOutputHelper _out;

    public K5_2TelegramFeedTests(ITestOutputHelper output)
    {
        _out = output;
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-k52-{Guid.NewGuid():N}");
        _stateDir = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(_stateDir);

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# K5.2 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| K9.1 | first checkpoint | DONE | abc1234 | e.md |\n" +
            "| K9.2 | second checkpoint | TODO | | |\n" +
            "| K9.3 | third checkpoint | TODO | | |\n");

        SecretsStore.WriteTelegramToken(_stateDir, "k52-test-token");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (Exception) { }
    }

    private PlanConfig Plan(string apiRoot) => new()
    {
        Name = PlanName,
        Repo = _repo,
        Tracker = "TRACKER.md",
        Stages = { new StageConfig { Id = "K9", Title = "The result contract and the channels", Sessions = 1 } },
        Telegram = new TelegramConfig
        {
            AllowedChatIds = { ChatId },
            PollIntervalSeconds = 60,
            ApiBaseUrl = apiRoot,
        },
    };

    /// <summary>Pushes through the REAL queue and send loop, then stops the service — StopAsync
    /// drains the backlog, which is what makes the final push observable.</summary>
    private async Task<List<string>> SendAsync(FakeBotApi bot, RunState state, Func<TelegramService, Task> push)
    {
        var plan = Plan(bot.Root);
        using var svc = new TelegramService(plan, state, NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        await push(svc);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var sent = bot.Snapshot();
        _out.WriteLine("---- verbatim sendMessage text ----");
        foreach (var m in sent) _out.WriteLine(m.Replace("\n", "\n    ", StringComparison.Ordinal));
        _out.WriteLine("---- end ----");
        return sent;
    }

    private static SessionEndPush Push(int number = 7, string outcome = "Advanced",
        string? gates = "engine-fast:OK", string? result = null, bool rollover = false,
        int commits = 0, params string[] newlyDone) =>
        new(number, "K9", outcome, gates, result, 0.4242m, null, commits, newlyDone, rollover);

    // ── defect 1: the session number, twice, from two sources ──

    [Fact]
    public async Task The_session_number_is_printed_once_and_it_is_the_records()
    {
        using var bot = new FakeBotApi();

        // The live counter has already moved on — exactly the case where the two sources disagreed.
        var sent = await SendAsync(bot, new RunState { SessionCounter = 99 },
            svc => svc.PushSessionEndAsync(Push(number: 7)));

        var msg = Assert.Single(sent);
        Assert.StartsWith($"<i>{PlanName} · s7</i>\n", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("s99", msg, StringComparison.Ordinal);

        // Once, not twice: the body no longer opens with a second copy of the number.
        var occurrences = msg.Split("s7", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, occurrences);
        Assert.DoesNotContain("<b>s7", msg, StringComparison.Ordinal);
    }

    // ── defect 2: the stage as a bare letter ──

    [Fact]
    public async Task The_stage_carries_its_title_not_a_bare_id()
    {
        using var bot = new FakeBotApi();

        var sent = await SendAsync(bot, new RunState(), svc => svc.PushSessionEndAsync(Push()));

        Assert.Contains("K9 — The result contract and the channels", Assert.Single(sent), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_stage_id_degrades_to_the_id_rather_than_throwing()
    {
        using var svc = new TelegramService(Plan("http://127.0.0.1:1"), new RunState(),
            NullLogger<TelegramService>.Instance);

        Assert.Equal("Z9", svc.StageLabel("Z9"));
        Assert.Equal("-", svc.StageLabel(""));
    }

    // ── defect 3: 700 characters of prose, cut blind ──

    [Fact]
    public async Task The_structured_result_is_rendered_as_fields_not_cut_mid_word()
    {
        using var bot = new FakeBotApi();
        var result =
            "SESSION-RESULT: K5.2 made the feed readable again\n" +
            "- one bullet long enough to bury what follows it: " + new string('p', 300) + "\n" +
            "- a second, ordinary outcome bullet\n" +
            "evidence: .conductor/evidence/K5/K5.2-telegram.md\n" +
            "gaps: the 4096-character chunking is K5.4";
        Assert.True(result.IndexOf("gaps:", StringComparison.Ordinal) > 400);

        var sent = await SendAsync(bot, new RunState(),
            svc => svc.PushSessionEndAsync(Push(result: result)));

        var msg = Assert.Single(sent);
        Assert.Contains("result: <b>K5.2 made the feed readable again</b>", msg, StringComparison.Ordinal);
        Assert.Contains("• a second, ordinary outcome bullet", msg, StringComparison.Ordinal);
        Assert.Contains("gaps: the 4096-character chunking is K5.4", msg, StringComparison.Ordinal);
        // KS11.3 / CH-5: the artifact is half the PROOF line now, beside the gate verdict, rather
        // than the last line of the result block below the gaps.
        Assert.Contains("proof: gates ", msg, StringComparison.Ordinal);
        Assert.Contains("evidence .conductor/evidence/K5/K5.2-telegram.md", msg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verifier_payload_is_bounded_but_not_re_cut_by_the_caller()
    {
        using var bot = new FakeBotApi();
        var json = "{\"score\":66,\"findings\":[\"" + new string('f', 4000) + "\"],\"verdict\":\"WARN\"}";

        var sent = await SendAsync(bot, new RunState(),
            svc => svc.PushSessionEndAsync(Push(result: json)));

        var msg = Assert.Single(sent);
        Assert.Contains("result: ", msg, StringComparison.Ordinal);
        Assert.True(msg.Length < 1500, $"the notifier bounds its own message: {msg.Length}");
    }

    // ── defect 4: a rollover that reports nothing ──

    [Fact]
    public async Task A_rollover_reports_what_it_landed_and_says_its_gates_are_deferred()
    {
        using var bot = new FakeBotApi();

        var sent = await SendAsync(bot, new RunState(), svc => svc.PushSessionEndAsync(
            Push(outcome: "RolledOver", gates: null, rollover: true, commits: 3, newlyDone: "K9.1")));

        var msg = Assert.Single(sent);
        Assert.DoesNotContain("(not recorded)", msg, StringComparison.Ordinal);
        Assert.Contains("proof: gates deferred", msg, StringComparison.Ordinal);
        Assert.Contains("landed: 3 commits · claimed K9.1", msg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_rollover_with_no_gate_summary_still_says_not_recorded()
    {
        using var bot = new FakeBotApi();

        var sent = await SendAsync(bot, new RunState(),
            svc => svc.PushSessionEndAsync(Push(outcome: "AgentError", gates: null)));

        Assert.Contains("proof: gates (not recorded)", Assert.Single(sent), StringComparison.Ordinal);
    }

    // ── defect 5: no progress, ever ──

    [Fact]
    public async Task Every_push_carries_a_progress_line()
    {
        using var bot = new FakeBotApi();

        var sent = await SendAsync(bot, new RunState { CurrentStage = "K9" }, async svc =>
        {
            await svc.PushSessionEndAsync(Push());
            await svc.PushAsync("an ordinary engine push");
        });

        Assert.Equal(2, sent.Count);
        foreach (var m in sent)
            Assert.Contains("progress: 1/3 checkpoints · K9 1/3", m, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_with_no_readable_tracker_pushes_without_a_progress_line()
    {
        File.Delete(Path.Combine(_repo, "TRACKER.md"));
        using var bot = new FakeBotApi();

        var sent = await SendAsync(bot, new RunState(), svc => svc.PushAsync("still delivered"));

        var msg = Assert.Single(sent);
        Assert.DoesNotContain("progress:", msg, StringComparison.Ordinal);
        Assert.Contains("still delivered", msg, StringComparison.Ordinal);
    }

    // ── the whole chain, on a real run ──

    /// <summary>The composition tests above start at <see cref="SessionEndPush"/>. This one starts at
    /// a real session: <c>conductor run --once</c> over a real git repo with a fake agent, so the leg
    /// that builds the push FROM THE RECORD (<c>RunLoop.Plumbing</c>) is proved as well.</summary>
    [Fact]
    public async Task A_real_run_pushes_one_session_number_a_stage_title_and_a_progress_line()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-k52run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        try
        {
            GitRun(repo, "init", "-b", "main");
            GitRun(repo, "config", "user.email", "k52@test");
            GitRun(repo, "config", "user.name", "K52 Test");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# K5.2");
            GitRun(repo, "add", "README.md");
            GitRun(repo, "commit", "-m", "chore: initial commit", "--no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# K5.2 Run\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
                "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
                "| K9.1 | run checkpoint | TODO | | |\n| K9.2 | second | TODO | | |\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "fake-agent.cmd"), string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"SESSION-RESULT: delivered K9.1 on a real run\"}}",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"- committed the deliverable\"}}",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"gaps: none\"}}",
                "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.00042,\"tokens\":{\"input\":10,\"output\":5}}}",
                "echo k52 done> k52-output.txt",
                "git add k52-output.txt",
                "git commit -m \"feat: deliver k52 checkpoint\"",
                "exit /b 0",
                ""));

            var stateDir = Path.Combine(repo, ".conductor");
            Directory.CreateDirectory(stateDir);
            SecretsStore.WriteTelegramToken(stateDir, "k52-run-token");

            using var bot = new FakeBotApi();
            var plan = new PlanConfig
            {
                Name = PlanName,
                Repo = repo,
                Tracker = "TRACKER.md",
                Stages = { new StageConfig { Id = "K9", Title = "The result contract and the channels", Sessions = 1 } },
                Agent = new AgentConfig
                {
                    Command = "cmd.exe",
                    Args = { "/c", Path.Combine(repo, "fake-agent.cmd"), "{prompt}" },
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
            var planPath = Path.Combine(repo, "k52.plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts));

            var exit = await new RunCommand().ExecuteAsync(null!, new RunCommand.Settings
            {
                Plan = planPath,
                Once = true,
                Headless = true,
                NoFace = true,
                NoControlPlane = true,
            });
            Assert.Equal(0, exit);

            var sent = bot.Snapshot();
            _out.WriteLine("---- K5.2 wire transcript from a real run ----");
            foreach (var m in sent) _out.WriteLine(m.Replace("\n", "\n    ", StringComparison.Ordinal));
            _out.WriteLine("---- end transcript ----");

            // KS11.3: the run's onboarding message also carries money, so the session-end push is
            // found by the fact only IT has — the result block.
            var end = sent.Find(m => m.Contains("result: ", StringComparison.Ordinal));
            Assert.True(end is not null, "no session-end push reached the wire: " + string.Join(" || ", sent));

            Assert.StartsWith($"<i>{PlanName} · s1</i>\n", end!, StringComparison.Ordinal);
            Assert.DoesNotContain("<b>s1", end!, StringComparison.Ordinal);
            Assert.Contains("K9 — The result contract and the channels", end!, StringComparison.Ordinal);
            Assert.Contains("progress: 0/2 checkpoints · K9 0/2", end!, StringComparison.Ordinal);
            Assert.Contains("result: <b>delivered K9.1 on a real run</b>", end!, StringComparison.Ordinal);
        }
        finally
        {
            try { TestTemp.DeleteTree(repo); } catch (Exception) { }
        }
    }

    private static void GitRun(string repo, params string[] args)
    {
        var r = ProcessRunner.Run("git", args, repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
    }

    /// <summary>Records what the service actually POSTed to sendMessage; answers everything else with
    /// an empty ok. The path carries the bot token, which on a developer machine may be a real one —
    /// match on the method name and never record the URL.</summary>
    private sealed class FakeBotApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<string> _sent = new();

        public string Root { get; }

        public FakeBotApi()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Root = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
            _listener.Prefixes.Add(Root + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public List<string> Snapshot()
        {
            lock (_gate) return new List<string>(_sent);
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; }

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
