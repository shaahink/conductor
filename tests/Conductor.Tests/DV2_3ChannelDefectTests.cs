using System.Globalization;

using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV2.3, cluster B — the three channel defects the field sweep found, each pinned at the stub seam
/// against a real <see cref="TelegramService"/> talking to a loopback stand-in for api.telegram.org.
/// No real token is reachable from here: the ambient one is cleared for the whole test process
/// (<see cref="TestEnvironmentIsolation"/>) and each fixture writes its own scratch token into its
/// own temp state dir.
///
/// <list type="bullet">
/// <item>bug #64 — the startup line counted the RAW <c>allowedChatIds</c> list, so a plan that
///   declares its chats the KS11.2 way was told at startup that it would deliver nothing while it
///   delivered perfectly, flatly contradicting <c>/telegram/status</c>;</item>
/// <item>bug #65 — the same raw-versus-resolved read one endpoint along, which made the test
///   endpoint report "there is no chat to send it to" for a bot that could reach its owner, and so
///   made the Face's guided setup uncompletable on a correct plan;</item>
/// <item>bug #38 — Telegram allows exactly one <c>getUpdates</c> consumer per token and terminates
///   the loser with <c>409 Conflict</c>. <c>EnsureSuccessStatusCode</c> discarded the body that says
///   so, and the loop logged a generic transport warning every interval, forever.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DV2_3ChannelDefectTests : IDisposable
{
    private const string AdminChat = "-1002220001";
    private const string ObserverChat = "-1002220002";

    private readonly string _repo;
    private readonly string _stateDir;
    private readonly ITestOutputHelper _out;
    private readonly List<TelegramService> _services = new();

    public DV2_3ChannelDefectTests(ITestOutputHelper output)
    {
        _out = output;
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-dv23-{Guid.NewGuid():N}");
        _stateDir = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(_stateDir);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# DV2.3\n");
        // A scratch token, never a real one: it only has to be non-null, since every call it appears
        // in goes to the loopback stub.
        SecretsStore.WriteTelegramToken(_stateDir, "dv23-scratch-token");
    }

    public void Dispose()
    {
        foreach (var s in _services)
        {
            try { s.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(15)); } catch (Exception) { }
            try { s.Dispose(); } catch (Exception) { }
        }
        try { TestTemp.DeleteTree(_repo); } catch (Exception) { }
    }

    /// <summary>A plan that declares its chats the KS11.2 way and ONLY that way — the shape both
    /// #64 and #65 misread. <c>allowedChatIds</c> stays empty on purpose; that emptiness is the
    /// whole defect.</summary>
    private PlanConfig ChatsBlockPlan(string apiRoot) => new()
    {
        Name = "DV23Plan",
        Repo = _repo,
        Tracker = "TRACKER.md",
        Stages = { new StageConfig { Id = "DV2", Title = "The sweep", Sessions = 1 } },
        Telegram = new TelegramConfig
        {
            PollIntervalSeconds = 60,
            ApiBaseUrl = apiRoot,
            Chats =
            {
                // Observer FIRST, so "admin first" is a real choice and not the order falling out.
                new TelegramChatEntry { ChatId = ObserverChat, Profile = "observer" },
                new TelegramChatEntry { ChatId = AdminChat, Profile = "admin" },
            },
        },
    };

    private TelegramService Service(PlanConfig plan, ILogger<TelegramService> log)
    {
        var svc = new TelegramService(plan, new RunState { SessionCounter = 1 }, log);
        _services.Add(svc);
        return svc;
    }

    private void Dump(string what, IEnumerable<string> lines)
    {
        _out.WriteLine($"---- {what} ----");
        foreach (var l in lines) _out.WriteLine(l);
        _out.WriteLine("---- end ----");
    }

    // ── bug #64: the started line reports the resolved chat set ──

    [Fact]
    public async Task Started_line_counts_the_resolved_chats_not_the_raw_allow_list()
    {
        using var bot = new RecordingBotApi();
        var log = new CapturingLogger();
        var svc = Service(ChatsBlockPlan(bot.Root), log);

        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var lines = log.Lines;
        Dump("start log, chats-block plan", lines);

        // The defect, exactly: AllowedChatIds is empty and the resolved set is not.
        Assert.Empty(svc.LiveConfig!.AllowedChatIds);
        Assert.Equal(2, svc.LiveConfig!.ChatCount);

        Assert.DoesNotContain(lines, l => l.Contains("will deliver nothing", StringComparison.Ordinal));
        Assert.Contains(lines, l =>
            l.StartsWith("Information|", StringComparison.Ordinal)
            && l.Contains("Telegram bot started", StringComparison.Ordinal)
            && l.Contains("2 allowed chat id(s)", StringComparison.Ordinal));
    }

    /// <summary>The negative control. The warning branch is not dead — a plan with a token, a block
    /// and no chat at all still says so, and it must keep saying so, or #64's fix would just be the
    /// warning being deleted.</summary>
    [Fact]
    public async Task A_plan_with_no_chats_at_all_still_says_it_will_deliver_nothing()
    {
        using var bot = new RecordingBotApi();
        var plan = ChatsBlockPlan(bot.Root);
        plan.Telegram!.Chats.Clear();
        var log = new CapturingLogger();
        var svc = Service(plan, log);

        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var lines = log.Lines;
        Dump("start log, no chats", lines);

        Assert.Contains(lines, l =>
            l.StartsWith("Warning|", StringComparison.Ordinal)
            && l.Contains("will deliver nothing", StringComparison.Ordinal));
    }

    // ── bug #65: the test endpoint on a chats-only plan ──

    [Fact]
    public async Task Test_connection_sends_to_the_resolved_admin_chat_on_a_chats_only_plan()
    {
        using var bot = new RecordingBotApi();
        var svc = Service(ChatsBlockPlan(bot.Root), new CapturingLogger());

        var outcome = await svc.TestConnectionAsync(CancellationToken.None);
        var calls = bot.Snapshot();
        Dump("bot API calls", calls.Select(c => $"{c.Method} chat_id={c.ChatId}"));
        _out.WriteLine($"outcome: ok={outcome.Ok} bot={outcome.BotUsername} error={outcome.Error}");

        // Before the fix this was false, with "there is no chat to send it to" — on a plan whose bot
        // reaches two chats.
        Assert.True(outcome.Ok, $"test connection failed: {outcome.Error}");
        Assert.Equal("dv23_stub_bot", outcome.BotUsername);

        var send = Assert.Single(calls, c => string.Equals(c.Method, "sendMessage", StringComparison.Ordinal));
        // Admin first: a test message is an admin's proof, not something posted into an observer chat.
        Assert.Equal(AdminChat, send.ChatId);
    }

    /// <summary>The other half of the same guard: with no chat to reach, the endpoint must still
    /// refuse rather than tick a green box. This is SC1.1's fix and #65's must not undo it.</summary>
    [Fact]
    public async Task Test_connection_still_refuses_when_there_is_no_chat_to_reach()
    {
        using var bot = new RecordingBotApi();
        var plan = ChatsBlockPlan(bot.Root);
        plan.Telegram!.Chats.Clear();
        var svc = Service(plan, new CapturingLogger());

        var outcome = await svc.TestConnectionAsync(CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.DoesNotContain(bot.Snapshot(), c => string.Equals(c.Method, "sendMessage", StringComparison.Ordinal));
    }

    // ── bug #38: getUpdates 409, named and backed off ──

    /// <summary>Telegram's own words, verbatim from the wire — the sentence that was in the response
    /// body the whole time and that nothing deserialised.</summary>
    private const string ConflictDescription =
        "Conflict: terminated by other getUpdates request; make sure that only one bot instance is running";

    private const string ConflictBody =
        """{"ok":false,"error_code":409,"description":"Conflict: terminated by other getUpdates request; make sure that only one bot instance is running"}""";

    [Fact]
    public async Task A_getUpdates_conflict_names_the_other_consumer_and_is_loud_exactly_once()
    {
        using var bot = new RecordingBotApi { ConflictBody = ConflictBody };
        var log = new CapturingLogger();
        var plan = ChatsBlockPlan(bot.Root);
        // Long enough that a SECOND poll inside the test window can only be the backoff bringing the
        // loop back, never the ordinary interval.
        plan.Telegram!.PollIntervalSeconds = 3600;
        var svc = Service(plan, log);

        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        Assert.True(await bot.WaitForPollsAsync(2, TimeSpan.FromSeconds(30)),
            $"the poll loop did not come back after the conflict (polls={bot.PollCount})");
        // Let the second conflict finish being logged before reading the lines.
        await WaitForAsync(() => log.Lines.Count(IsConflictLine) >= 2, TimeSpan.FromSeconds(10));

        var lines = log.Lines;
        Dump("poll log under a 409", lines);

        var loud = lines.Where(l => l.StartsWith("Error|", StringComparison.Ordinal)).ToList();
        var quiet = lines.Where(l => l.StartsWith("Debug|", StringComparison.Ordinal)
                                     && l.Contains("still conflicted", StringComparison.Ordinal)).ToList();

        // Loud ONCE — a conflict lasts as long as the other engine does, and an error line every
        // poll is how a diagnosis becomes wallpaper.
        var one = Assert.Single(loud);
        Assert.Contains(ConflictDescription, one, StringComparison.Ordinal);
        Assert.Contains("another process is polling getUpdates with this same bot token", one, StringComparison.Ordinal);
        Assert.Contains("Backing off 5s", one, StringComparison.Ordinal);
        Assert.NotEmpty(quiet);

        // And it is a BACKOFF, not a hang: the loop came back and the wait it announces grows.
        Assert.Contains(quiet, l => l.Contains("backing off 10s", StringComparison.Ordinal));

        // The old behaviour, for the record: EnsureSuccessStatusCode's message, at warning level,
        // saying nothing about who else holds the token.
        Assert.DoesNotContain(lines, l => l.StartsWith("Warning|", StringComparison.Ordinal)
                                          && l.Contains("Telegram poll error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conflict_with_an_unreadable_body_still_says_what_a_409_means()
    {
        using var bot = new RecordingBotApi { ConflictBody = "<html>502 from a proxy in the way</html>" };
        var log = new CapturingLogger();
        var plan = ChatsBlockPlan(bot.Root);
        plan.Telegram!.PollIntervalSeconds = 3600;
        var svc = Service(plan, log);

        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        Assert.True(await WaitForAsync(() => log.Lines.Any(IsConflictLine), TimeSpan.FromSeconds(30)),
            "no conflict line was logged");
        var lines = log.Lines;
        Dump("poll log under an unreadable 409", lines);

        var one = Assert.Single(lines, l => l.StartsWith("Error|", StringComparison.Ordinal));
        Assert.Contains("409 Conflict", one, StringComparison.Ordinal);
        Assert.Contains("another process is polling getUpdates with this same bot token", one, StringComparison.Ordinal);
    }

    /// <summary>Linear, capped and deterministic on purpose: an operator watching the log can tell a
    /// backoff from a hang, and a test can state the delay instead of measuring it.</summary>
    [Theory]
    [InlineData(0, 5)]     // defensive: a streak is at least one conflict
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(11, 55)]
    [InlineData(12, 60)]
    [InlineData(13, 60)]   // capped
    [InlineData(1000, 60)]
    public void Conflict_backoff_is_five_seconds_per_streak_capped_at_a_minute(int streak, int expectedSeconds)
        => Assert.Equal(expectedSeconds, (int)TelegramService.ConflictBackoff(streak).TotalSeconds);

    private static bool IsConflictLine(string line) =>
        line.Contains("conflict", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return condition();
    }

    /// <summary>The log IS the surface under test for #64 and #38, so it is captured rather than
    /// nulled. Level is kept: "loud once, quiet after" is a claim about levels.</summary>
    private sealed class CapturingLogger : ILogger<TelegramService>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = new();

        public List<string> Lines { get { lock (_gate) return new List<string>(_lines); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_gate) _lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{logLevel}|{formatter(state, exception)}"));
        }
    }
}
