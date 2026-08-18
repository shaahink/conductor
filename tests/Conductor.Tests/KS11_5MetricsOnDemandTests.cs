using System.Globalization;
using System.Text.RegularExpressions;

using Conductor.Core;
using Conductor.Core.Budget;
using Conductor.Core.History;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Money;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Extensions.Logging.Abstractions;

using Money = Conductor.Core.Integrations.Messaging.MoneyLine;

namespace Conductor.Tests;

/// <summary>
/// KS11.5 / CHAPAR CH-6 — the figures tier: <c>/progress</c>, <c>/money</c>, <c>/tokens</c>, and the
/// daily digest recomposed in the grammar.
///
/// <para>The exit that matters is not "the verbs answer" — it is that what they answer is the SAME
/// number the terminal answers. So the rig writes a real <c>run.db</c> with real <c>costs</c> rows,
/// and the assertions compute the expected figures the way <c>conductor money</c> computes them
/// (<c>RunArchive</c> → <see cref="MoneyAnalyzer"/>, <c>MoneyCommand.cs:95-107</c>) rather than by
/// restating what the composer does. Two surfaces quoting one run must not have two arithmetics.</para>
///
/// <para>The database is isolated by a state POINTER in the rig's own working tree, not by an
/// environment variable: xUnit runs test classes in parallel, and KS11.4 has already paid for a
/// process-wide variable set by one rig and read by another.</para>
/// </summary>
public sealed class KS11_5MetricsOnDemandTests : IDisposable
{
    private const string RunId = "ks11-5-metrics";

    /// <summary>Every pattern here runs over a message body a few hundred characters long; the
    /// timeout is the analyzer's rule rather than a real risk, and one second is orders of magnitude
    /// above anything these can take.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly string _repo;
    private readonly SqliteRunStore _store;
    private readonly PlanConfig _plan;
    private readonly RunState _state;
    private readonly MessageComposer _composer;
    private readonly CommandRouter _router;

    public KS11_5MetricsOnDemandTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-ks11m-{Guid.NewGuid():N}", "metrics-rig");
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# Metrics rig\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + "| KS11.1 | the seam | DONE | abc1234 | seam.md |\n"
            + "| KS11.2 | the profiles | DONE | bcd2345 | profiles.md |\n"
            + "| KS11.5 | metrics on demand | IN PROGRESS | | |\n"
            + "| KS12.1 | the record | TODO | | |\n");

        var db = Path.Combine(_repo, ".conductor", "rig.db");
        _store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
        // The pointer is what makes plan.RunDbPath resolve to THIS database instead of deriving a
        // path under the machine's state home — a test must not write into the operator's catalogue.
        StatePointer.TryWrite(StateHome.PointerPathFor(_repo), db, "Metrics rig");
        Seed();

        _plan = new PlanConfig
        {
            Name = "Metrics rig",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages =
            {
                new StageConfig { Id = "KS11", Title = "Chapar — the remote surface", Sessions = 1 },
                new StageConfig { Id = "KS12", Title = "The record", Sessions = 1 },
            },
        };
        _plan.Limits.MaxRunCostUsd = 50m;

        _state = new RunState
        {
            RunId = RunId,
            SessionCounter = 3,
            CurrentStage = "KS11",
            Status = RunStatus.Running,
            History =
            {
                new SessionRecord { Number = 1, CostUsd = 12.50m, TokensInput = 20_000, TokensOutput = 30_000, TokensCacheRead = 2_000_000 },
                new SessionRecord { Number = 2, CostUsd = 6.25m, TokensInput = 10_000, TokensOutput = 15_000, TokensCacheRead = 1_000_000 },
                new SessionRecord { Number = 3, CostUsd = 2.00m, TokensInput = 5_000, TokensOutput = 5_000, TokensCacheRead = 400_000 },
            },
        };

        _composer = new MessageComposer(_plan, _state, ProgressProviderFactory.Create(_plan), _store, _ => { });
        _router = new CommandRouter(_composer, _plan);
    }

    /// <summary>Three sessions, five billed rows across three lanes, two checkpoints closed. The
    /// figures are deliberately awkward — $12.50 / $6.25 / $2.00 plus lane and advisor rows — so a
    /// composer that rounded or dropped a lane could not accidentally agree with the archive.</summary>
    private void Seed()
    {
        _store.InitializeRun(RunId, "Metrics rig", _repo, "feat/ks11", EngineStamp.Parse("test"));
        _store.RecordSession(RunId, "KS11", 1, "delivery", new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 17, 10, 0, 0, DateTimeKind.Utc), "Advanced", null, 0, 1,
            "build:OK", "SESSION-RESULT: the seam", 2, "KS11.1");
        _store.RecordSession(RunId, "KS11", 2, "delivery", new DateTime(2026, 8, 17, 11, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc), "Advanced", null, 0, 1,
            "build:OK", "SESSION-RESULT: the profiles", 1, "KS11.2");
        _store.RecordSession(RunId, "KS11", 3, "delivery", new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc),
            null, null, null, 0, 1, null, null, 0, null);

        _store.RecordCost(RunId, 1, "agent", 20_000, 30_000, 0, 2_000_000, 12.00m, 3_600_000);
        _store.RecordCost(RunId, 1, "advisor", 1_000, 500, 0, 20_000, 0.50m, 4_000);
        _store.RecordCost(RunId, 2, "agent", 10_000, 15_000, 0, 1_000_000, 6.00m, 3_600_000);
        _store.RecordCost(RunId, 2, "lane", 2_000, 1_000, 0, 40_000, 0.25m, 60_000);
        _store.RecordCost(RunId, 3, "agent", 5_000, 5_000, 0, 400_000, 2.00m, 1_800_000);

        _store.RecordGate(RunId, 1, "KS11", "build", "fast", "repo", "abc1234", true, false, false, 0, 900, null);
        _store.RecordGate(RunId, 2, "KS11", "tests", "full", "repo", "bcd2345", true, false, false, 0, 90_000, null);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    /// <summary>What <c>conductor money</c> would print for this database, computed its way: through
    /// <see cref="RunArchive"/> (read-only) and <see cref="MoneyAnalyzer"/>. Nothing in here restates
    /// the composer — if the composer's arithmetic ever drifts from the verb's, this is what
    /// notices.</summary>
    private MoneyRun Expected()
    {
        var archive = RunArchive.TryOpen(_plan.RunDbPath);
        Assert.NotNull(archive);
        var sessions = archive!.Sessions(RunId);
        var costs = archive.Costs(RunId);
        var windows = BudgetAnalyzer.Analyze(RunId, _plan.Name, sessions, archive.SoftBreaks(RunId)).Windows;
        return MoneyAnalyzer.AnalyzeRun(RunId, _plan.Name, _plan.Repo, null, null, sessions, costs, windows);
    }

    // ────────────────────────── the cross-checks ──────────────────────────

    /// <summary>CH-6's exit, to the cent: the billed total a phone is told is the billed total the
    /// terminal reports for the same database.</summary>
    [Fact]
    public void The_billed_total_is_the_one_conductor_money_reports_for_the_same_database()
    {
        var expected = Expected();
        var text = _composer.MoneyText();

        Assert.Equal(20.75m, expected.Total.Cost);          // the rig's five rows, summed by the analyzer
        Assert.Equal(Math.Round(expected.Total.Cost, 2), FirstDollarAmount(text));
        Assert.Contains("Billed " + Money.Spend(expected.Total.Cost, 50m), text, StringComparison.Ordinal);
    }

    /// <summary>A third path to the same number, and the only one that is not C#: SQLite's own SUM
    /// over the rows. The analyzer and the composer could in principle drift together — this cannot
    /// drift with either.</summary>
    [Fact]
    public void The_database_itself_sums_to_the_figure_the_answer_quotes()
    {
        var row = _store.Query("SELECT COALESCE(SUM(cost_usd), 0) AS total FROM costs WHERE run_id = @runId",
            ("@runId", RunId)).Single();
        var billed = Convert.ToDecimal(row["total"], CultureInfo.InvariantCulture);

        Assert.Equal(20.75m, billed);
        Assert.Equal(billed, FirstDollarAmount(_composer.MoneyText()));
    }

    /// <summary>Every lane, to the cent — a total that agrees while the split does not is a total
    /// that will stop agreeing the moment a lane is added.</summary>
    [Fact]
    public void Every_spending_lane_is_quoted_at_the_archives_figure()
    {
        var expected = Expected();
        var text = _composer.MoneyText();

        Assert.Equal(3, expected.Categories.Count);
        foreach (var lane in expected.Categories)
            Assert.Contains($"{lane.Label} {Money.Usd(lane.Cost)}", text, StringComparison.Ordinal);
    }

    /// <summary>The productivity figure the owner keeps asking for, from the analyzer's own division
    /// rather than a second one: dollars per checkpoint the sessions actually closed.</summary>
    [Fact]
    public void Dollars_per_checkpoint_come_from_the_analyzer()
    {
        var expected = Expected();
        Assert.Equal(2, expected.Total.Checkpoints);        // KS11.1 and KS11.2, from newly_done
        Assert.Contains($"{Money.Usd(expected.Total.CostPerCheckpoint!.Value)} per delivered checkpoint",
            _composer.MoneyText(), StringComparison.Ordinal);
    }

    /// <summary>And the tokens, from the same <see cref="MoneyRun"/> — so <c>/money</c> and
    /// <c>/tokens</c> cannot quote two different runs.</summary>
    [Fact]
    public void The_token_total_and_the_cache_share_are_the_archives()
    {
        var expected = Expected();
        var text = _composer.TokensText();

        Assert.Equal(3_549_500, expected.Total.Tokens);
        Assert.Contains("3.5M tokens over 3 sessions", text, StringComparison.Ordinal);
        Assert.Contains($"({(expected.Total.CacheReadShare * 100).ToString("0.#", CultureInfo.InvariantCulture)}%)",
            text, StringComparison.Ordinal);
        Assert.Contains($"Input {(expected.Total.InputTokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)}k",
            text, StringComparison.Ordinal);
    }

    /// <summary>The run's own counter and the database are two records of one truth. The rig makes
    /// them differ on purpose — the state carries $20.75 of history against $20.75 billed plus a
    /// session in flight — and the answer says which is which rather than letting a reader find the
    /// difference themselves.</summary>
    [Fact]
    public void A_counter_that_disagrees_with_the_record_is_named_not_hidden()
    {
        _state.History.Add(new SessionRecord { Number = 4, CostUsd = 3.00m });
        var text = _composer.MoneyText();

        Assert.Contains("The run's own counter says $23.75", text, StringComparison.Ordinal);
        Assert.Contains("not billed to the record yet", text, StringComparison.Ordinal);
    }

    /// <summary>CH-6 again, on the other axis: <c>/progress</c> counts what <c>/status</c> counts.
    /// Both read one snapshot through <see cref="IProgressProvider"/>, and the test compares the
    /// rendered figures rather than the call, because the rendering is what a reader compares.</summary>
    [Fact]
    public void Progress_counts_are_the_counts_status_answers()
    {
        var status = _composer.StatusText();
        var progress = _composer.ProgressText();

        var fromStatus = Regex.Match(status, @"Checkpoints: (?<done>\d+)/(?<total>\d+) done", RegexOptions.ExplicitCapture, RegexTimeout);
        var fromProgress = Regex.Match(progress, @"progress: (?<done>\d+)/(?<total>\d+) checkpoints", RegexOptions.ExplicitCapture, RegexTimeout);
        Assert.True(fromStatus.Success && fromProgress.Success, "both views must state a checkpoint count");
        Assert.Equal(fromStatus.Groups["done"].Value, fromProgress.Groups["done"].Value);
        Assert.Equal(fromStatus.Groups["total"].Value, fromProgress.Groups["total"].Value);

        // And the stage rows add up to the same total, which is what makes the road view honest.
        var perStage = Regex.Matches(progress, @"\[(?:done|here| {4})\] \w+\s+(?<done>\d+)/(?<total>\d+)", RegexOptions.ExplicitCapture, RegexTimeout);
        Assert.Equal(2, perStage.Count);
        Assert.Equal(int.Parse(fromProgress.Groups["total"].Value, CultureInfo.InvariantCulture),
            perStage.Sum(m => int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture)));
    }

    /// <summary>The stage a session is working is marked as the reader's "you are here", and a stage
    /// with every row settled reads as done — a progress view that marks nothing is a list.</summary>
    [Fact]
    public void The_road_says_where_the_run_is()
    {
        var progress = _composer.ProgressText();
        Assert.Contains("[here] KS11", progress, StringComparison.Ordinal);
        Assert.Contains("KS11.5 — metrics on demand", progress, StringComparison.Ordinal);
    }

    // ────────────────────────── the grammar ──────────────────────────

    /// <summary>CH-5: every pulled answer ends in the same monospace telemetry line, carrying the
    /// same three facts the pushes carry. A reader who compares an answer with the last push must
    /// find the same numbers written the same way.</summary>
    [Theory]
    [InlineData("/progress")]
    [InlineData("/money")]
    [InlineData("/tokens")]
    [InlineData("/daily")]
    public void Every_pulled_answer_ends_in_the_grammars_telemetry_line(string verb)
    {
        var outcome = _router.Route(verb, ChatProfile.Observer, twoWay: false, injectionArmed: false);
        Assert.Equal(SurfaceAction.Reply, outcome.Action);

        var last = outcome.Text!.Split('\n')[^1];
        Assert.StartsWith("<code>", last, StringComparison.Ordinal);
        Assert.Contains("progress: 2/4 checkpoints", last, StringComparison.Ordinal);
        Assert.Contains("cost: $20.75 of $50.00", last, StringComparison.Ordinal);
        Assert.Contains("tokens 3.5M", last, StringComparison.Ordinal);
    }

    /// <summary>The digest was the one message a day a reader is guaranteed to see, and it carried no
    /// progress, no cap and no tokens. It carries the gate verdict as a proof line now, like every
    /// other message.</summary>
    [Fact]
    public void The_digest_reads_in_the_same_grammar()
    {
        var digest = _composer.DailyDigestText();

        Assert.StartsWith("<b>Metrics rig — daily digest</b>", digest, StringComparison.Ordinal);
        Assert.Contains("proof: gates all recent gates passed", digest, StringComparison.Ordinal);
        Assert.Contains("<b>Sessions by stage</b>", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("$0.0000", digest, StringComparison.Ordinal);
    }

    // ────────────────────────── the profile, and the rule ──────────────────────────

    /// <summary>CH-3: the three verbs are browse verbs, so an observer gets the figures — the whole
    /// point of the observer profile is a reader who can see the run without being able to move it.</summary>
    [Theory]
    [InlineData("/progress")]
    [InlineData("/money")]
    [InlineData("/tokens")]
    public void An_observer_may_ask_for_figures(string verb)
    {
        var observer = _router.Route(verb, ChatProfile.Observer, twoWay: true, injectionArmed: false);
        var admin = _router.Route(verb, ChatProfile.Admin, twoWay: true, injectionArmed: false);

        Assert.Equal(SurfaceAction.Reply, observer.Action);
        Assert.Equal(admin.Text, observer.Text);        // same run, same answer, no filtered version
        Assert.Contains(verb, SurfaceCommands.BrowseList, StringComparison.Ordinal);
    }

    /// <summary>CH-6's money rule, enforced against the source rather than promised in a comment: the
    /// engine has no price table and this file must not become one. Every dollar it prints came from
    /// the <c>costs</c> table; the only decimal literal it is allowed is the one-cent threshold that
    /// decides whether the counter and the record disagree.</summary>
    [Fact]
    public void The_metrics_composer_holds_no_price_table()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(),
            "src", "Conductor.Core", "Integrations", "Messaging", "MessageComposer.Metrics.cs"));

        var literals = Regex.Matches(source, @"\b\d+\.\d+m\b", RegexOptions.ExplicitCapture, RegexTimeout).Select(m => m.Value).Distinct().ToList();
        Assert.Equal(["0.01m"], literals);
    }

    // ────────────────────────── the goldens ──────────────────────────

    /// <summary>What each answer LOOKS like, pinned. The assertions above prove the figures are the
    /// archive's; these prove the shape a reader actually receives, which is the half that drifts
    /// silently — a line moved, a label reworded, the telemetry line lost off the end.
    ///
    /// <para>Strict, like KS11.3's: a missing golden fails rather than writing itself, because a
    /// golden that appears on first run pins whatever the code happened to do that day.</para></summary>
    [Theory]
    [InlineData("/progress", "answer-progress")]
    [InlineData("/money", "answer-money")]
    [InlineData("/tokens", "answer-tokens")]
    [InlineData("/daily", "answer-daily")]
    public void Each_answer_is_pinned(string verb, string golden)
    {
        var outcome = _router.Route(verb, ChatProfile.Observer, twoWay: false, injectionArmed: false);
        AssertGolden(golden, outcome.Text!);
    }

    private static void AssertGolden(string name, string actual)
    {
        var path = Path.Combine(RepoRoot(), "tests", "Conductor.Tests", "testdata", "ks11-5", name + ".txt");
        var normalised = actual.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

        if (string.Equals(Environment.GetEnvironmentVariable("CONDUCTOR_GOLDEN_REBASELINE"), "1",
                StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, normalised);
            return;
        }

        Assert.True(File.Exists(path),
            $"golden {name}.txt is missing — regenerate with CONDUCTOR_GOLDEN_REBASELINE=1 and READ the diff");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), normalised,
            StringComparer.Ordinal);
    }

    // ────────────────────────── the rig's helpers ──────────────────────────

    /// <summary>The first dollar figure in a body, as a decimal — what a reader's eye lands on, read
    /// back the way a reader reads it.</summary>
    private static decimal FirstDollarAmount(string text)
    {
        var m = Regex.Match(text, @"\$(?<amount>\d+\.\d{2})\b", RegexOptions.ExplicitCapture, RegexTimeout);
        Assert.True(m.Success, "the money answer stated no dollar figure at all");
        return decimal.Parse(m.Groups["amount"].Value, CultureInfo.InvariantCulture);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
