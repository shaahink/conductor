using System.Globalization;

using Conductor.Core;
using Conductor.Core.Evidence;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K5.4, composition half — what a push SAYS.
///
/// <para>The identity stamp (FU-OWNER-11) answered which plan and which session. It could not
/// answer which checkout: one chat can carry two clones of the same plan on two branches, and every
/// message from both read identically. Nothing in a push was ever a link, though
/// <c>Reporter</c> has built remote URLs from a commit sha for as long as the report has existed —
/// so a sha in a chat was a string the owner had to carry back to a machine. And money rendered as
/// four decimal places with nothing to compare them to: a run at $97 of a $100 cap and a run at $97
/// of no cap were the same message.</para>
///
/// <para>Assertions are on the bytes that left the process, against a real git repo with a real
/// remote, because the links are built from what <c>git remote get-url</c> actually answers.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class K5_4CompositionTests : IDisposable
{
    private const string ChatId = "838383";
    private const string PlanName = "K54Comp";
    private const string Remote = "https://github.com/acme/widgets";

    private readonly string _repo;
    private readonly ITestOutputHelper _out;

    public K5_4CompositionTests(ITestOutputHelper output)
    {
        _out = output;
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-k54c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);

        Git("init", "-b", "feat/karvan");
        Git("config", "user.email", "k54@test");
        Git("config", "user.name", "K54 Test");
        Git("remote", "add", "origin", Remote + ".git");
        // An unborn HEAD has no branch to name — `rev-parse --abbrev-ref HEAD` fails outright — so a
        // repo with no commit is not the case under test here, and would silently drop the branch
        // and the report link from every assertion below.
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# K5.4");
        Git("add", "README.md");
        Git("commit", "-m", "chore: initial commit", "--no-gpg-sign");

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# K5.4 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| K9.1 | first checkpoint | DONE | abc1234 | e.md |\n" +
            "| K9.2 | second checkpoint | IN PROGRESS | | |\n" +
            "| K9.3 | third checkpoint | TODO | | |\n");

        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
        SecretsStore.WriteTelegramToken(Path.Combine(_repo, ".conductor"), "k54c-test-token");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (Exception) { }
    }

    private void Git(params string[] args)
    {
        var r = ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed: {r.Output} {r.StdErr}");
    }

    private PlanConfig Plan(string apiRoot, decimal? costCap = null)
    {
        var plan = new PlanConfig
        {
            Name = PlanName,
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "K9", Title = "The result contract and the channels", Sessions = 1 } },
            Telegram = new TelegramConfig
            {
                AllowedChatIds = { ChatId }, PollIntervalSeconds = 60, ApiBaseUrl = apiRoot,
            },
        };
        plan.Limits.MaxRunCostUsd = costCap;
        return plan;
    }

    private async Task<List<BotCall>> SendAsync(RecordingBotApi bot, RunState state,
        Func<TelegramService, Task> push, decimal? costCap = null)
    {
        using var svc = new TelegramService(Plan(bot.Root, costCap), state, NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        await push(svc);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var calls = bot.Snapshot();
        _out.WriteLine("---- verbatim Bot API calls ----");
        foreach (var c in calls) _out.WriteLine(c.Describe());
        _out.WriteLine("---- end ----");
        return calls;
    }

    // ── repo, branch, stage title and checkpoint, in every push ──

    [Fact]
    public async Task Every_push_names_the_checkout_the_branch_the_stage_and_the_checkpoint()
    {
        using var bot = new RecordingBotApi();
        var state = new RunState { SessionCounter = 12, CurrentStage = "K9" };

        var calls = await SendAsync(bot, state, async svc =>
        {
            await svc.PushAsync("a plain engine push");
            await svc.PushSessionEndAsync(Push());
        });

        Assert.Equal(2, calls.Count);
        foreach (var c in calls)
        {
            var text = c.Text!;
            Assert.Contains(Path.GetFileName(_repo), text, StringComparison.Ordinal);
            Assert.Contains("@feat/karvan", text, StringComparison.Ordinal);
            Assert.Contains("K9 — The result contract and the channels", text, StringComparison.Ordinal);
            // The checkpoint the board says is in flight — not the next TODO, and not a guess.
            Assert.Contains("K9.2", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_session_end_push_is_stamped_with_its_own_stage_not_the_runs()
    {
        using var bot = new RecordingBotApi();

        // The run has moved on; the push is about K9 and must say K9.
        var calls = await SendAsync(bot, new RunState { CurrentStage = "K7" },
            svc => svc.PushSessionEndAsync(Push()));

        Assert.Contains("K9 — The result contract and the channels",
            Assert.Single(calls).Text!, StringComparison.Ordinal);
    }

    // ── money with headroom ──

    [Fact]
    public async Task Cost_is_rendered_against_the_cap_with_what_is_left()
    {
        using var bot = new RecordingBotApi();
        var state = new RunState();
        state.History.Add(new SessionRecord { Number = 1, CostUsd = 60m });

        var calls = await SendAsync(bot, state, svc => svc.PushSessionEndAsync(Push(cost: 1.25m)), costCap: 100m);

        var text = Assert.Single(calls).Text!;
        Assert.Contains("cost: $1.25 · run $60.00 of $100.00 (60%, $40.00 left)", text, StringComparison.Ordinal);
        // The old rendering: four decimals, no cap, nothing to compare it to.
        Assert.DoesNotContain("$60.0000", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(97.46, 100.0, "$97.46 of $100.00 (97%, $2.54 left)")]
    [InlineData(120.0, 100.0, "$120.00 of $100.00 — cap reached, the run parks for approval")]
    public void The_money_line_says_how_much_headroom_is_left(double spent, double cap, string expected)
        => Assert.Equal("cost: " + expected, MoneyLine.ForRun((decimal)spent, (decimal)cap));

    [Fact]
    public void A_plan_with_no_cap_says_so_rather_than_implying_one()
        => Assert.Equal("cost: $12.00 (no cap set)", MoneyLine.ForRun(12m, null));

    /// <summary>Two decimals above a dollar is what a statement reads like; four below it is a real
    /// session that would otherwise render as $0.00.</summary>
    [Theory]
    [InlineData(0.0042, "$0.0042")]
    [InlineData(97.4567, "$97.46")]
    public void Small_money_keeps_its_precision_and_large_money_loses_it(double amount, string expected)
        => Assert.Equal(expected, MoneyLine.Usd((decimal)amount));

    // ── links ──

    [Fact]
    public async Task Commits_arrive_as_links_to_the_runs_own_remote()
    {
        using var bot = new RecordingBotApi();
        var shas = new[] { "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678 first subject", "0f1e2d3c4b5a69788796a5b4c3d2e1f012345678" };

        var calls = await SendAsync(bot, new RunState(),
            svc => svc.PushSessionEndAsync(Push(commits: 2, shas: shas)));

        var text = Assert.Single(calls).Text!;
        Assert.Contains($"<a href=\"{Remote}/commit/a1b2c3d4e5f60718293a4b5c6d7e8f9012345678\">a1b2c3d</a>",
            text, StringComparison.Ordinal);
        Assert.Contains("landed: 2 commits (", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_is_a_link_on_the_runs_own_branch()
    {
        using var bot = new RecordingBotApi();

        var calls = await SendAsync(bot, new RunState(), svc => svc.PushSessionEndAsync(Push()));

        Assert.Contains($"{Remote}/blob/feat%2Fkarvan/.conductor/REPORT.md",
            Assert.Single(calls).Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_request_an_agent_mentioned_becomes_a_link()
    {
        using var bot = new RecordingBotApi();
        var result = "SESSION-RESULT: opened the PR\n- raised #412 against master\ngaps: none";

        var calls = await SendAsync(bot, new RunState(), svc => svc.PushSessionEndAsync(Push(result: result)));

        Assert.Contains($"<a href=\"{Remote}/pull/412\">#412</a>",
            Assert.Single(calls).Text!, StringComparison.Ordinal);
    }

    /// <summary>A repo with no remote must degrade to plain text, not to a broken link — and an
    /// escaped apostrophe (<c>&amp;#39;</c>) must not be mistaken for a pull-request reference.</summary>
    [Fact]
    public void Without_a_remote_a_sha_is_plain_text_and_an_escaped_entity_is_left_alone()
    {
        Assert.Equal("a1b2c3d", RemoteLinks.Commit(null, "a1b2c3d4e5f6 subject"));
        Assert.Null(RemoteLinks.Report(null, "main"));
        Assert.Equal("it&#39;s fine", RemoteLinks.LinkifyPullRequests("it&#39;s fine", Remote));
    }

    // ── the completion push ──

    [Fact]
    public async Task The_completion_push_leads_with_the_outcome_then_the_work_the_cost_and_the_report()
    {
        using var bot = new RecordingBotApi();
        var state = new RunState { SessionCounter = 20 };
        state.History.Add(new SessionRecord { Number = 1, CostUsd = 42m });

        var calls = await SendAsync(bot, state, svc => svc.PushRunCompleteAsync(
            new RunCompletePush(20, 38, 40, TimeSpan.FromHours(9.5), [])), costCap: 50m);

        var call = Assert.Single(calls);
        var body = call.Text![(call.Text!.LastIndexOf("</i>\n", StringComparison.Ordinal) + 5)..];
        Assert.StartsWith("<b>run complete</b> · 9h 30m", body, StringComparison.Ordinal);
        Assert.Contains("38/40 checkpoints · 20 sessions", body, StringComparison.Ordinal);
        Assert.Contains("cost: $42.00 of $50.00 (84%, $8.00 left)", body, StringComparison.Ordinal);
        Assert.Contains("/blob/feat%2Fkarvan/.conductor/REPORT.md", body, StringComparison.Ordinal);
        // A finished run is one of the two pushes that has earned a buzz.
        Assert.False(call.DisableNotification);
        // The engine build no longer gets more room than what the run did.
        Assert.DoesNotContain("engine ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_completion_over_skipped_stages_says_so()
    {
        using var bot = new RecordingBotApi();

        var calls = await SendAsync(bot, new RunState(), svc => svc.PushRunCompleteAsync(
            new RunCompletePush(4, 6, 9, null, ["K3", "K6"])));

        var text = Assert.Single(calls).Text!;
        Assert.Contains("run complete, with stages skipped", text, StringComparison.Ordinal);
        Assert.Contains("skipped: K3, K6", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duration_reads_the_way_a_human_reads_one()
    {
        Assert.Equal("9h 30m", TelegramService.Elapsed(TimeSpan.FromMinutes(570)));
        Assert.Equal("14m", TelegramService.Elapsed(TimeSpan.FromSeconds(880)));
        Assert.Equal("12s", TelegramService.Elapsed(TimeSpan.FromSeconds(12)));
    }

    private static SessionEndPush Push(decimal? cost = 0.4242m, string? result = null,
        int commits = 0, string[]? shas = null) =>
        new(7, "K9", "Advanced", "engine-fast:OK", result, cost, null, commits, [], false,
            shas, TimeSpan.FromMinutes(23));
}
