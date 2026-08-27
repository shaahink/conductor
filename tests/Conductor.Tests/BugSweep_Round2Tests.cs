using System.Text.Json;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.History;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Orchestration;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>The 2026-08-27 bug sweep, round 2 — #37 #39 #79 #81 #86 #92. One class per bug.</summary>
public sealed class Bug37_HistoryJsonIsNotCutByTheTableDefault
{
    [Fact]
    public void ThePayloadNamesTheCut_WhenACallerLimitedIt()
    {
        var cut = RunHistoryPayload.List([], totalRows: 34);
        Assert.Equal(34, cut.Total);
        Assert.True(cut.Truncated);
    }

    [Fact]
    public void ThePayloadIsWhole_ByDefault()
    {
        var whole = RunHistoryPayload.List([]);
        Assert.Equal(0, whole.Total);
        Assert.False(whole.Truncated);
    }

    /// <summary>The table keeps its twenty; the JSON must never inherit them.</summary>
    [Fact]
    public void TheTableDefaultIsTwenty() => Assert.Equal(20, HistoryCommand.DefaultTableLimit);

    [Fact]
    public void TotalAndTruncated_ReachTheWire()
    {
        var json = JsonSerializer.Serialize(RunHistoryPayload.List([], totalRows: 3), RunHistoryJsonContext.Default.RunHistoryListJson);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }
}

public sealed class Bug81_FollowupsAreReadAsALedger : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bug81-" + Guid.NewGuid().ToString("N")[..8]);

    public Bug81_FollowupsAreReadAsALedger() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>The real file's shapes, cut down: the declaring table with a status column, then two
    /// closing-pass scoreboards with composite cells, a parenthetical, "unchanged", and a re-home.</summary>
    private const string Fixture = """
        # followups

        | id | rule | sites | why deferred | owning stage | status |
        |----|------|-------|--------------|--------------|--------|
        | FU-B11-3 | Real-credential cTrader owner-gated path | B11.4 | needs real credentials | `HUMAN:` | **OPEN, owner-gated** |
        | FU-OWNER-9 | Agent kills its own parent conductor process | SF0 | a fix session ran Stop-Process | SF0.3 | OPEN |
        | FU-OWNER-10 | Rig one | x | y | SF3.3 | OPEN |
        | FU-OWNER-11 | Rig two | x | y | SF4.2 | OPEN |
        | FU-OWNER-13 | Rig three | x | y | SF4.2 | OPEN |
        | FU-OWNER-14 | The reinstall | x | y | SF7.2 | OPEN |
        | FU-B4-1 | Rule four | x | y | B4 | OPEN |
        | FU-F0-2 | Rule five | x | y | F0 | OPEN |

        ## Sarban's end

        | row | disposition | why |
        |---|---|---|
        | FU-B4-1 · FU-F0-2 | **CLOSED, accept-by-design** | each re-verified in the tree |
        | FU-OWNER-9 | **CLOSED**; remainder filed as bug **#16** | suggestions (1) and (3) landed in SF0.3 |
        | FU-B11-3 | **`HUMAN:`, stated** | real credentials and real money |
        | FU-OWNER-10 · 11 · 13 | unchanged | already owned by SF3.3 / SF4.2 |
        | the 11 carried bug rows | **CLOSED** in the ledger itself | SF0.1-SF0.3 fixed them |

        ## Karvan's end

        | row | state at 2026-08-05 | owner from here |
        |---|---|---|
        | FU-OWNER-10, FU-OWNER-11, FU-OWNER-13 | **CLOSED** by SF7.1, unchanged | — |
        | FU-B11-3 | **HUMAN**, unchanged — real cTrader credentials | the owner |
        | FU-OWNER-14 (the reinstall) | **re-homed once more, KS10.3 → KS12.3.** | KS12.3 |

        ## Charkh's end

        | id | disposition | owner |
        |---|---|---|
        | FU-B11-3 | **OPEN, owner-gated**, unchanged | the owner |
        | FU-OWNER-14 (the reinstall) | **CLOSED as a ledger row at CH5.1 (`c0dcad5`)** | — |

        """;

    private string Write()
    {
        var path = Path.Combine(_dir, "followups.md");
        File.WriteAllText(path, Fixture);
        return path;
    }

    [Theory]
    [InlineData("FU-B11-3", new[] { "FU-B11-3" })]
    [InlineData("FU-B4-1 · FU-F0-2 · FU-F0-3 · FU-F1-03", new[] { "FU-B4-1", "FU-F0-2", "FU-F0-3", "FU-F1-03" })]
    [InlineData("FU-OWNER-10 · 11 · 13", new[] { "FU-OWNER-10", "FU-OWNER-11", "FU-OWNER-13" })]
    [InlineData("FU-OWNER-10, FU-OWNER-11, FU-OWNER-13", new[] { "FU-OWNER-10", "FU-OWNER-11", "FU-OWNER-13" })]
    [InlineData("FU-OWNER-14 (the reinstall)", new[] { "FU-OWNER-14" })]
    [InlineData("the 43 rows closed by the 2026-07-28 triage", new string[0])]
    public void EveryIdACellNames(string cell, string[] expected) =>
        Assert.Equal(expected, FollowupLedger.SplitIds(cell));

    /// <summary>One entry per id — 8 for a file whose raw reading gives 15 rows over 10 spellings.</summary>
    [Fact]
    public void OneEntryPerId_WithTheNewestVerdict_AndTheDeclaredRule()
    {
        var path = Write();
        var raw = FollowupParser.Read(path);
        var ledger = FollowupLedger.Read(path);

        Assert.True(raw.Count > ledger.Count, $"raw {raw.Count} vs ledger {ledger.Count}");
        Assert.Equal(8, ledger.Count);
        Assert.Equal(ledger.Count, ledger.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count());

        var owner9 = ledger.Single(e => e.Id == "FU-OWNER-9");
        Assert.False(FollowupParser.IsOpen(owner9));
        Assert.Equal("Agent kills its own parent conductor process", owner9.Item);

        // Composite cells apply to every id they name; "unchanged" a section later does not reopen them.
        foreach (var id in new[] { "FU-OWNER-10", "FU-OWNER-11", "FU-OWNER-13", "FU-B4-1", "FU-F0-2" })
            Assert.False(FollowupParser.IsOpen(ledger.Single(e => e.Id == id)), id);

        // A re-home is not a verdict; the CLOSED that follows it is.
        Assert.False(FollowupParser.IsOpen(ledger.Single(e => e.Id == "FU-OWNER-14")));

        // The one genuinely open row stays open through three restatements of the same fact.
        var b113 = ledger.Single(e => e.Id == "FU-B11-3");
        Assert.True(FollowupParser.IsOpen(b113));
        Assert.Equal("Real-credential cTrader owner-gated path", b113.Item);
    }

    /// <summary>"unchanged" alone keeps whatever the file last decided — including OPEN.</summary>
    [Fact]
    public void AnUndecidedRow_KeepsThePreviousVerdict()
    {
        var rows = new List<FollowupParser.Row>
        {
            new("FU-X-1", "rule", null, "S1", "OPEN", ["rule", "S1", "OPEN"]),
            new("FU-X-1", "unchanged", null, "", null, ["unchanged", "still owned by S1"]),
        };
        var one = Assert.Single(FollowupLedger.Fold(rows));
        Assert.True(FollowupParser.IsOpen(one));
        Assert.Equal("rule", one.Item);
    }

    /// <summary>The fix-lane reader is untouched: it still sees rows the way it always did.</summary>
    [Fact]
    public void TheRawReader_StillReadsEveryRow()
    {
        var path = Write();
        Assert.Contains(FollowupParser.Read(path), e => e.Id == "FU-OWNER-10 · 11 · 13");
    }

    [Fact]
    public void Cards_CarryOneCardPerKey()
    {
        var twice = new[]
        {
            new FollowupEntry { Id = "FU-Z-1", Item = "first", Status = "OPEN" },
            new FollowupEntry { Id = "FU-Z-1", Item = "second", Status = "CLOSED" },
        };
        var cards = GithubLedgerPlan.Cards([], twice, "conductor");
        var one = Assert.Single(cards);
        Assert.True(one.Closed);
    }
}

public sealed class Bug79_TheBackfillRemembersWhatItCreated : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bug79-" + Guid.NewGuid().ToString("N")[..8]);

    public Bug79_TheBackfillRemembersWhatItCreated() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ASecondPass_StartsFromWhatTheFirstCreated()
    {
        var path = GithubMapFile.PathFor(_root, "run-79", "owner/scratch");
        Assert.StartsWith(Path.Combine(_root, GithubMapFile.DirName), path, StringComparison.Ordinal);

        var first = GithubMapFile.Load(path);
        Assert.Equal(0, first.IssueCount);
        first.RecordIssue("bug:1", 5);
        first.RecordIssue("bug:2", 4);
        first.RecordComment("run:diary:3", 8);
        Assert.True(await GithubMapFile.SaveAsync(path, first));

        var second = GithubMapFile.Load(path);
        Assert.Equal(2, second.IssueCount);
        Assert.Equal(5, second.IssueFor("bug:1"));
        Assert.Equal(4, second.IssueFor("bug:2"));
        Assert.True(second.CommentPosted("run:diary:3"));
        Assert.Null(second.IssueFor("bug:3"));
    }

    /// <summary>The archive's own github_map rows seed the map, and the file's word wins for a key
    /// both name — the file is what the last backfill actually saw GitHub answer.</summary>
    [Fact]
    public async Task TheArchiveSeedsIt_AndTheFileWins()
    {
        var path = GithubMapFile.PathFor(_root, "run-79b", "owner/scratch");
        var written = GithubMapFile.Load(path);
        written.RecordIssue("bug:1", 10);
        Assert.True(await GithubMapFile.SaveAsync(path, written));

        var seeded = GithubMapFile.Load(path, seed: [new GithubMapFileEntry("bug:1", "issue", 3), new GithubMapFileEntry("bug:9", "issue", 7)]);
        Assert.Equal(10, seeded.IssueFor("bug:1"));
        Assert.Equal(7, seeded.IssueFor("bug:9"));
    }

    [Fact]
    public void ATornFile_IsAnEmptyMap_NotAnError()
    {
        var path = GithubMapFile.PathFor(_root, "run-79c", "owner/scratch");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"key\":\"bug:1\",\"kind\":\"issue\",\"issue\":2}\n{not json\n");
        var map = GithubMapFile.Load(path);
        Assert.Equal(1, map.IssueCount);
    }
}

public sealed class Bug86_AFailedFirstAttemptIsKept : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "bug86-" + Guid.NewGuid().ToString("N")[..8]);

    public Bug86_AFailedFirstAttemptIsKept() => Directory.CreateDirectory(_state);

    public void Dispose()
    {
        try { Directory.Delete(_state, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task TheOutputLandsUnderGateOutput_NamedByStageGateAndAttempt()
    {
        var first = new GateResult("engine-full", Passed: false, Skipped: false, Optional: false, 1,
            TimeSpan.FromSeconds(293), "Failed KS1_2StagesFromFoldTests.DerivedStatus... Actual: active\n");

        var path = await GateFailureSpill.SpillAttemptAsync(_state, "CH2", first, attempt: 1);

        Assert.NotNull(path);
        Assert.StartsWith(Path.Combine(_state, GateFailureSpill.DirName), path, StringComparison.Ordinal);
        var name = Path.GetFileName(path);
        Assert.StartsWith("CH2-engine-full-", name, StringComparison.Ordinal);
        Assert.EndsWith("-attempt1.log", name, StringComparison.Ordinal);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("Actual: active", text, StringComparison.Ordinal);
        Assert.Contains("exit 1 after 293s", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoStateDir_KeepsNothing_AndSaysSo()
    {
        var first = new GateResult("g", false, false, false, 1, TimeSpan.Zero, "x");
        Assert.Null(await GateFailureSpill.SpillAttemptAsync(null, "S1", first, 1));
    }

    [Fact]
    public void TheEventRecordsTheRetry()
    {
        var e = new Conductor.Core.Events.GateFinished { Name = "g", Passed = true, Retried = true, FirstAttemptOutputPath = "x.log" };
        Assert.True(e.Retried);
        Assert.Equal("x.log", e.FirstAttemptOutputPath);
    }
}

public sealed class Bug39_ADanglingSessionRecordIsClosed : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bug39-" + Guid.NewGuid().ToString("N")[..8]);

    public Bug39_ADanglingSessionRecordIsClosed() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private static RunState State() => new()
    {
        PlanName = "p",
        RunId = "run-39",
        SessionCounter = 2,
        History =
        {
            new SessionRecord { Number = 1, Stage = "KS0", StartedUtc = new DateTime(2026, 8, 13, 17, 0, 0, DateTimeKind.Utc) },
            new SessionRecord { Number = 2, Stage = "KS0", StartedUtc = new DateTime(2026, 8, 13, 18, 0, 0, DateTimeKind.Utc),
                EndedUtc = new DateTime(2026, 8, 13, 19, 0, 0, DateTimeKind.Utc), Outcome = SessionOutcome.Advanced },
        },
    };

    /// <summary>The KS0 case: session #1's engine was killed mid-session, #2 resumed and finished.</summary>
    [Fact]
    public void OnResume_TheOpenRecordIsClosedAsInterrupted()
    {
        var state = State();
        var now = new DateTime(2026, 8, 13, 20, 0, 0, DateTimeKind.Utc);

        var closed = SessionReconcile.CloseDangling(state, now);

        Assert.Equal([1], closed);
        Assert.Equal(SessionOutcome.Interrupted, state.History[0].Outcome);
        Assert.Equal(now, state.History[0].EndedUtc);
        Assert.Equal(SessionOutcome.Advanced, state.History[1].Outcome);
        Assert.Empty(SessionReconcile.CloseDangling(state, now));
    }

    [Fact]
    public void TheStatusWord_IsRunningOnlyForTheLatestUnderALiveEngine()
    {
        var open = new SessionRecord { Number = 1, Stage = "S" };
        Assert.Equal("running", SessionReconcile.OutcomeWord(open, isLatest: true, engineLive: true));
        Assert.Equal(SessionReconcile.Orphaned, SessionReconcile.OutcomeWord(open, isLatest: true, engineLive: false));
        Assert.Equal(SessionReconcile.Orphaned, SessionReconcile.OutcomeWord(open, isLatest: false, engineLive: true));
        var done = new SessionRecord { Number = 1, Stage = "S", Outcome = SessionOutcome.TimedOut };
        Assert.Equal("TimedOut", SessionReconcile.OutcomeWord(done, isLatest: true, engineLive: true));
    }

    /// <summary>The write half, as <c>conductor run close</c> uses it: the persisted state is patched.</summary>
    [Fact]
    public void RunClose_ClosesTheRecordInThePersistedState()
    {
        var db = Path.Combine(_tmp, "run.db");
        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        store.InitializeRun("run-39", "p", _tmp, "master", EngineStamp.Parse("0.5.0+test"));
        store.SaveRunState("run-39", "p", JsonSerializer.Serialize(State(), PlanConfig.JsonOpts));

        var closed = store.CloseDanglingSessions("run-39", "p", new DateTime(2026, 8, 13, 20, 0, 0, DateTimeKind.Utc));

        Assert.Equal([1], closed);
        var reloaded = JsonSerializer.Deserialize<RunState>(store.LoadRunStateJson("run-39")!, PlanConfig.JsonOpts)!;
        Assert.Equal(SessionOutcome.Interrupted, reloaded.History[0].Outcome);
        Assert.Empty(store.CloseDanglingSessions("run-39", "p", DateTime.UtcNow));
    }
}

/// <summary>Bug #92: a session the watchdog killed never sends the result envelope, so its cost was null
/// and no cost row was written — `conductor budget` prescribed the era from six of seven sessions. The
/// rule is pure so it can be pinned beside the contract it must not break (KS5.2: an envelope that
/// carries no figure is the CLI's answer, not a missing envelope).</summary>
public sealed class Bug92_AnEnvelopelessExitStillReachesTheLedger
{
    [Fact]
    public void TheCliFigure_IsNeverSecondGuessed()
    {
        var (cost, estimated) = SessionRunner.PriceExit(0.5m, resultReceived: true, liveTokens: 1_000, observedRatePerToken: 0.001m);
        Assert.Equal(0.5m, cost);
        Assert.False(estimated);
    }

    /// <summary>KS5.2's contract, untouched: the CLI answered, and its answer was "no figure".</summary>
    [Fact]
    public void AnEnvelopeWithNoFigure_StaysUnpriced()
    {
        var (cost, estimated) = SessionRunner.PriceExit(null, resultReceived: true, liveTokens: 1_000, observedRatePerToken: 0.001m);
        Assert.Null(cost);
        Assert.False(estimated);
    }

    [Fact]
    public void NoEnvelope_IsPricedAtTheObservedRate()
    {
        var (cost, estimated) = SessionRunner.PriceExit(null, resultReceived: false, liveTokens: 1_000, observedRatePerToken: 0.001m);
        Assert.Equal(1.0m, cost);
        Assert.True(estimated);
    }

    /// <summary>The row is written at $0 rather than not at all: a 0 can be re-priced, an absence cannot.</summary>
    [Fact]
    public void NoEnvelope_NoRate_IsRecordedAtZero_NotDropped()
    {
        var (cost, estimated) = SessionRunner.PriceExit(null, resultReceived: false, liveTokens: 1_000, observedRatePerToken: null);
        Assert.Equal(0m, cost);
        Assert.True(estimated);
    }

    [Fact]
    public void NothingSpent_IsNothingToPrice()
    {
        var (cost, estimated) = SessionRunner.PriceExit(null, resultReceived: false, liveTokens: 0, observedRatePerToken: null);
        Assert.Null(cost);
        Assert.False(estimated);
    }

    /// <summary>The fact the rule keys on comes from the wire: the envelope's arrival, not its contents.</summary>
    [Fact]
    public void TheProvider_RecordsTheEnvelopesArrival_EvenWithoutAFigure()
    {
        var state = new AgentStreamState((_, _) => { });
        var provider = new ClaudeProvider();
        provider.ParseLine("""{"type":"assistant","message":{"id":"m1","usage":{"input_tokens":40,"output_tokens":12},"content":[{"type":"text","text":"working"}]}}""", state);
        Assert.False(state.ResultReceived);
        provider.ParseLine("""{"type":"result","subtype":"success","is_error":false,"result":"done","num_turns":1,"usage":{"input_tokens":40,"output_tokens":12}}""", state);
        Assert.True(state.ResultReceived);
        Assert.Null(state.CostUsd);
    }
}
