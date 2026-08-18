using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS11.3 on the WIRE — the real <see cref="TelegramService"/>, a real Bot API stub, two chats with
/// different profiles.
///
/// <para>The goldens next door pin what the composer writes; this pins what actually leaves. Between
/// the two sits the whole adapter — profile resolution from the plan, the send queue, the identity
/// stamp, the per-chat fan-out — and every one of those is a place a per-profile message could be
/// delivered to the wrong chat, or to both, or to neither.</para>
///
/// <para>Scratch token, scratch chat ids, and a stub standing at
/// <see cref="TelegramConfig.ApiBaseUrl"/>: no proof in this stage touches a real bot.</para>
/// </summary>
public sealed class KS11_3OnWireTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ObserverChat = "-100123456";

    private readonly string _repo;

    public KS11_3OnWireTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-ks11w-{Guid.NewGuid():N}", "wire-rig");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# Wire rig\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + "| KS11.1 | the seam | DONE | abc1234 | seam.md |\n"
            + "| KS11.3 | the grammar | IN PROGRESS | | |\n");
        // KS11.4: this rig's token lives in its OWN secrets file, not in the process environment.
        // xUnit runs test classes in parallel and an env var set here is read by every other test in
        // the assembly - including the three that assert what a run with NO token says, which failed
        // exactly this way when a second wire rig widened the window.
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
        SecretsStore.WriteTelegramToken(Path.Combine(_repo, ".conductor"), "111111:scratch-bot-token");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    /// <summary>CH-4 at run start: each chat is introduced, once, in its own voice — and the two
    /// messages go to the two chats, not one message to both.</summary>
    [Fact]
    public async Task Each_chat_is_introduced_in_its_own_voice_and_only_once()
    {
        using var bot = new RecordingBotApi();
        using var svc = new TelegramService(Plan(bot.Root), State(), NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);

        await ((IRunNotifier)svc).PushOnboardingAsync(CancellationToken.None);
        await ((IRunNotifier)svc).PushOnboardingAsync(CancellationToken.None);   // idempotent per chat

        await ((IHostedService)svc).StopAsync(CancellationToken.None);
        var calls = bot.Snapshot();

        Assert.Equal(2, calls.Count);

        var admin = Assert.Single(calls, c => c.ChatId == AdminChat);
        Assert.Contains("the control surface for a conductor run", admin.Text!, StringComparison.Ordinal);
        Assert.Contains("/inject", admin.Text!, StringComparison.Ordinal);

        var observer = Assert.Single(calls, c => c.ChatId == ObserverChat);
        Assert.Contains("following a conductor run", observer.Text!, StringComparison.Ordinal);
        Assert.Contains("reading only", observer.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("/inject", observer.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("/abort", observer.Text!, StringComparison.Ordinal);
    }

    /// <summary>CH-3 through the transport, end to end: an observer types the most destructive verb
    /// there is and the wire carries one named refusal back to THEIR chat. Driven through the stub's
    /// own long-poll, so the profile is resolved from the plan by the adapter rather than handed in
    /// by the test.</summary>
    [Fact]
    public async Task An_observer_control_attempt_is_refused_on_the_wire()
    {
        using var bot = new RecordingBotApi();
        bot.QueueCommand(ObserverChat, "/abort");

        using var svc = new TelegramService(Plan(bot.Root, pollSeconds: 1), State(),
            NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var calls = await WaitForCallsAsync(bot, 1);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var reply = Assert.Single(calls);
        Assert.Equal(ObserverChat, reply.ChatId);
        Assert.Contains("/abort is a control command and this chat is an observer",
            reply.Text!, StringComparison.Ordinal);

        // The control file is what the engine obeys, and it was never written.
        Assert.False(File.Exists(Path.Combine(_repo, ".conductor", "control.json")));
    }

    /// <summary>And the admin chat, on the same plan, still gets the confirmation keyboard — the
    /// refusal above is a profile decision, not the control surface going away.</summary>
    [Fact]
    public async Task The_admin_chat_on_the_same_plan_still_gets_the_keyboard()
    {
        using var bot = new RecordingBotApi();
        bot.QueueCommand(AdminChat, "/abort");

        using var svc = new TelegramService(Plan(bot.Root, pollSeconds: 1), State(),
            NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var calls = await WaitForCallsAsync(bot, 1);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var reply = Assert.Single(calls);
        Assert.Equal(AdminChat, reply.ChatId);
        Assert.Contains("Confirm abort?", reply.Text!, StringComparison.Ordinal);
    }

    /// <summary>CH-5 on the wire: the telemetry line is the last thing a session-end push carries,
    /// it is in monospace, and it holds the money and the tokens.</summary>
    [Fact]
    public async Task A_session_end_push_carries_its_telemetry_in_monospace()
    {
        using var bot = new RecordingBotApi();
        using var svc = new TelegramService(Plan(bot.Root), State(), NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);

        await ((IRunNotifier)svc).PushSessionEndAsync(new SessionEndPush(
            Number: 7, Outcome: "Advanced", Stage: "KS11", GateSummary: "build:OK gates:9/9",
            ResultSummary: "SESSION-RESULT: the grammar\nevidence: .conductor/evidence/KS11/x.md",
            CostUsd: 1.25m, Score: null, Duration: TimeSpan.FromMinutes(30),
            Commits: 1, CommitShas: null, NewlyDone: ["KS11.3"], IsRollover: false),
            CancellationToken.None);

        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var text = Assert.Single(bot.Snapshot(), c => c.ChatId == AdminChat).Text!;
        var telemetry = Assert.Single(text.Split('\n'), l => l.StartsWith("<code>", StringComparison.Ordinal));
        Assert.Contains("cost: $1.25 · run $42.00 of $50.00", telemetry, StringComparison.Ordinal);
        Assert.Contains("tokens 3.5M", telemetry, StringComparison.Ordinal);
        Assert.Contains("proof: gates build:OK gates:9/9 · evidence .conductor/evidence/KS11/x.md",
            text, StringComparison.Ordinal);
    }

    // ── the rig ──

    private PlanConfig Plan(string apiRoot, int pollSeconds = 30)
    {
        var plan = new PlanConfig
        {
            Name = "Karvansara edge",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "KS11", Title = "Chapar — the remote surface", Sessions = 1 } },
            Telegram = new TelegramConfig
            {
                Chats =
                {
                    new TelegramChatEntry { ChatId = AdminChat, Profile = "admin" },
                    new TelegramChatEntry { ChatId = ObserverChat, Profile = "observer" },
                },
                PollIntervalSeconds = pollSeconds,
                ApiBaseUrl = apiRoot,
                EnableTwoWay = true,
            },
        };
        plan.Limits.MaxRunCostUsd = 50m;
        return plan;
    }

    private static RunState State() => new()
    {
        RunId = "ks11-3-wire",
        SessionCounter = 7,
        CurrentStage = "KS11",
        History =
        {
            new SessionRecord
            {
                Number = 1, CostUsd = 42m,
                TokensInput = 20_000, TokensOutput = 30_000, TokensReasoning = 10_000,
                TokensCacheRead = 3_440_000,
            },
        },
    };

    private static async Task<List<BotCall>> WaitForCallsAsync(RecordingBotApi bot, int atLeast)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var calls = bot.Snapshot();
            if (calls.Count >= atLeast) return calls;
            await Task.Delay(50);
        }
        return bot.Snapshot();
    }
}
