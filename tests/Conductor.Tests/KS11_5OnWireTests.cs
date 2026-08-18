using System.Globalization;

using Conductor.Core;
using Conductor.Core.Budget;
using Conductor.Core.History;
using Conductor.Core.Integrations;
using Conductor.Core.Money;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS11.5 on the WIRE — an observer asks for the figures and the real <see cref="TelegramService"/>
/// carries them back, through a Bot API stub standing at <c>TelegramConfig.ApiBaseUrl</c>.
///
/// <para>The unit tests next door prove the composer's arithmetic is the archive's. This proves the
/// figures survive the transport a stakeholder actually reads them through: profile resolution from
/// the plan, the long poll, the send queue, the chunker. A number that is right in the composer and
/// clipped on the wire is still a wrong answer on a phone.</para>
///
/// <para>Scratch bot token written into the rig's own <c>.conductor</c> — never the process
/// environment, never a real chat.</para>
/// </summary>
public sealed class KS11_5OnWireTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ObserverChat = "-100123456";
    private const string RunId = "ks11-5-wire";

    private readonly string _repo;
    private readonly SqliteRunStore _store;

    public KS11_5OnWireTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-ks11w5-{Guid.NewGuid():N}", "wire-rig");
        var scratch = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# Wire rig\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + "| KS11.4 | evidence on demand | DONE | df5048e | note-5.md |\n"
            + "| KS11.5 | metrics on demand | IN PROGRESS | | |\n");
        SecretsStore.WriteTelegramToken(scratch, "111111:scratch-bot-token");

        var db = Path.Combine(scratch, "rig.db");
        _store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
        StatePointer.TryWrite(StateHome.PointerPathFor(_repo), db, "Wire rig");

        _store.InitializeRun(RunId, "Wire rig", _repo, "feat/ks11", EngineStamp.Parse("test"));
        _store.RecordSession(RunId, "KS11", 1, "delivery", new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc), "Advanced", null, 0, 1,
            "build:OK", "SESSION-RESULT: evidence on demand", 2, "KS11.4");
        _store.RecordCost(RunId, 1, "agent", 40_000, 60_000, 0, 6_000_000, 31.40m, 3_600_000);
        _store.RecordCost(RunId, 1, "advisor", 2_000, 1_000, 0, 50_000, 1.35m, 9_000);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    /// <summary>The stage's headline exit, end to end: the observer chat asks what the run has cost
    /// and the dollars that LEAVE are the dollars <c>conductor money</c> reports for that database.</summary>
    [Fact]
    public async Task An_observer_asks_what_it_cost_and_the_wire_carries_the_archives_figure()
    {
        using var bot = new RecordingBotApi();
        bot.QueueCommand(ObserverChat, "/money");

        var plan = Plan(bot.Root, pollSeconds: 1);
        using var svc = new TelegramService(plan, State(), NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var calls = await WaitForCallsAsync(bot, 1);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var reply = Assert.Single(calls);
        Assert.Equal(ObserverChat, reply.ChatId);

        var expected = Expected(plan);
        Assert.Equal(32.75m, expected.Total.Cost);
        Assert.Contains("Billed $32.75 of $60.00", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("agent $31.40", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("advisor $1.35", reply.Text!, StringComparison.Ordinal);
        Assert.Contains($"{Conductor.Core.Integrations.Messaging.MoneyLine.Usd(expected.Total.CostPerCheckpoint!.Value)} per delivered checkpoint",
            reply.Text!, StringComparison.Ordinal);
    }

    /// <summary>And the tokens, which is the figure that makes the bill make sense: 98% of an era
    /// like this one is the prompt being re-sent, and a reader told only the total concludes the run
    /// wrote six million tokens of code.</summary>
    [Fact]
    public async Task The_token_answer_carries_the_cache_share_on_the_wire()
    {
        using var bot = new RecordingBotApi();
        bot.QueueCommand(ObserverChat, "/tokens");

        var plan = Plan(bot.Root, pollSeconds: 1);
        using var svc = new TelegramService(plan, State(), NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var calls = await WaitForCallsAsync(bot, 1);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var expected = Expected(plan);
        var reply = Assert.Single(calls);
        Assert.Equal(6_153_000, expected.Total.Tokens);
        Assert.Contains("6.2M tokens over 1 session", reply.Text!, StringComparison.Ordinal);
        Assert.Contains($"({(expected.Total.CacheReadShare * 100).ToString("0.#", CultureInfo.InvariantCulture)}%)",
            reply.Text!, StringComparison.Ordinal);
    }

    /// <summary>The other half of CH-3, unchanged by this checkpoint: the figures are browse verbs,
    /// so an observer gets them — and a control verb from the same chat is still refused by name.</summary>
    [Fact]
    public async Task The_figures_are_open_to_an_observer_and_control_still_is_not()
    {
        using var bot = new RecordingBotApi();
        bot.QueueCommand(ObserverChat, "/progress");
        bot.QueueCommand(ObserverChat, "/pause");

        using var svc = new TelegramService(Plan(bot.Root, pollSeconds: 1), State(),
            NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var calls = await WaitForCallsAsync(bot, 2);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Contains("Wire rig — progress", calls[0].Text!, StringComparison.Ordinal);
        Assert.Contains("[here] KS11", calls[0].Text!, StringComparison.Ordinal);
        Assert.Contains("/pause is a control command and this chat is an observer",
            calls[1].Text!, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_repo, ".conductor", "control.json")));
    }

    // ── the rig ──

    /// <summary>The same reading <c>conductor money</c> would take of this database.</summary>
    private static MoneyRun Expected(PlanConfig plan)
    {
        var archive = RunArchive.TryOpen(plan.RunDbPath);
        Assert.NotNull(archive);
        var sessions = archive!.Sessions(RunId);
        var costs = archive.Costs(RunId);
        var windows = BudgetAnalyzer.Analyze(RunId, plan.Name, sessions, archive.SoftBreaks(RunId)).Windows;
        return MoneyAnalyzer.AnalyzeRun(RunId, plan.Name, plan.Repo, null, null, sessions, costs, windows);
    }

    private PlanConfig Plan(string apiRoot, int pollSeconds = 30)
    {
        var plan = new PlanConfig
        {
            Name = "Wire rig",
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
        plan.Limits.MaxRunCostUsd = 60m;
        return plan;
    }

    private static RunState State() => new()
    {
        RunId = RunId,
        SessionCounter = 1,
        CurrentStage = "KS11",
        Status = RunStatus.Running,
        History =
        {
            new SessionRecord
            {
                Number = 1, CostUsd = 32.75m,
                TokensInput = 42_000, TokensOutput = 61_000, TokensCacheRead = 6_050_000,
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
