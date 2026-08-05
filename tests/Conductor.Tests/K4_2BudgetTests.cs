using System.Globalization;
using Conductor.Core.Budget;
using Conductor.Core.History;
using Conductor.Core.Store;

using Microsoft.Data.Sqlite;

namespace Conductor.Tests;

/// <summary>
/// K4.2 — the engine measures its own token budget and prescribes the next one.
///
/// <para>The fixture is not invented. <see cref="FaceRun"/> is the real session table of this repo's
/// <c>Sarban face</c> run (run <c>8cefa5de</c>), copied out of <c>run.db</c>, and
/// <see cref="FaceNudges"/> is the real <c>liveTokens</c>/<c>tokenBudget</c> pair the cooperative rail
/// stamped on each of its own events. The assertions are the figures published in
/// <c>docs/dev/NEXT-ERA-FINDINGS-2026-08-04.md</c> § <i>The 8M cap's real score</i> — which were
/// produced by hand-written SQL months before this analyzer existed. So this test says something
/// narrow and useful: given only what the run recorded, the analyzer independently arrives at the
/// diagnosis a human reached by hand.</para>
///
/// <para>Two of those published figures do NOT reproduce, and the test pins the measured values
/// rather than the printed ones: the capped window closed 14 checkpoints, not 17, and it lost 10
/// sessions to the ceiling, not 11 (the eleventh rollover is session 7, which ran to 16.5M before
/// any ceiling existed). Both are corrections owed to the doc, not bugs in the measurement.</para>
/// </summary>
public sealed class K4_2BudgetTests : IDisposable
{
    private readonly string _tmp;

    public K4_2BudgetTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-k42-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ the real run, as recorded

    /// <summary>number, outcome, agent tokens, checkpoints closed — run 8cefa5de, all 41 sessions.</summary>
    private const string FaceRun = """
        1,Advanced,14712289,SF0.1   2,Advanced,18506570,SF0.2   3,Advanced,25135555,SF0.3
        4,Advanced,21713655,SF0.4   5,Advanced,37322795,SF1.1   6,Advanced,25073184,SF1.2
        7,RolledOver,16467631,      8,KilledByUser,0,           9,RolledOver,8033821,
        10,Advanced,6996896,SF2.1   11,Advanced,7732648,SF2.2   12,Advanced,7476325,SF2.3
        13,RolledOver,8040782,      14,Advanced,7534336,SF3.1   15,Advanced,6899453,SF3.2
        16,RolledOver,8051776,      17,Progress,7490332,        18,RolledOver,8129686,
        19,Advanced,7297670,SF3.3   20,RolledOver,8036264,      21,Progress,7675022,
        22,Progress,7487866,        23,RolledOver,8010786,      24,Advanced,7752892,SF4.2
        25,Progress,4163638,        26,RolledOver,8114355,      27,RolledOver,8104536,
        28,Advanced,6878148,SF5.2   29,Advanced,7214913,SF5.3   30,Progress,7527796,
        31,Advanced,7469547,SF5.4   32,RolledOver,8044510,      33,Advanced,7553326,SF6.1
        34,Advanced,6515327,SF6.2   35,RolledOver,8068432,      36,Progress,7448988,
        37,Progress,7621725,        38,Progress,7307049,        39,Advanced,4655672,SF7.1
        40,Advanced,6459096,SF7.2   41,Progress,2480121,
        """;

    /// <summary>session, liveTokens at the nudge, the ceiling the event named — all 30 firings.</summary>
    private const string FaceNudges = """
        9,6068016    10,6067730   11,6107402   12,6013249   13,6125398   14,6137700
        15,6045059   16,6065478   17,6080323   18,6008098   19,6076392   20,6029398
        21,6128292   22,6019098   23,6112906   24,6056537   26,6068971   27,6038685
        28,6094241   29,6062190   30,6041360   31,6134352   32,6029351   33,6015283
        34,6096596   35,6031301   36,6113636   37,6012983   38,6076467   40,6031480
        """;

    private static List<ArchivedSession> Sessions(string table) =>
        [.. table.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(',', StringSplitOptions.TrimEntries))
            .Select(f => new ArchivedSession(
                Number: int.Parse(f[0], CultureInfo.InvariantCulture),
                StageId: "SF", Kind: "Deliver", StartedUtc: null, EndedUtc: null,
                Outcome: f[1], Attempt: 1, ResumeCount: 0, Commits: 0, CostUsd: 0m,
                Tokens: long.Parse(f[2], CultureInfo.InvariantCulture),
                ResultSummary: null, GateSummary: null,
                NewlyDone: f.Length > 3 ? f[3] : null,
                AgentTokens: long.Parse(f[2], CultureInfo.InvariantCulture)))];

    private static List<SoftBreakObservation> Nudges(string table, long ceiling = 8_000_000) =>
        [.. table.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(',', StringSplitOptions.TrimEntries))
            .Select(f => new SoftBreakObservation(
                int.Parse(f[0], CultureInfo.InvariantCulture),
                long.Parse(f[1], CultureInfo.InvariantCulture), ceiling, null))];

    private static BudgetProfile Face() =>
        BudgetAnalyzer.Analyze("8cefa5de", "Sarban face", Sessions(FaceRun), Nudges(FaceNudges));

    // ------------------------------------------------------------------ the reproduction

    [Fact]
    public void FaceRun_SplitsAtTheSessionTheCeilingBecameEffective()
    {
        var p = Face();

        Assert.Equal(2, p.Windows.Count);
        // Nothing told it where the cap arrived. Sessions 1-6 ran as far as 37.3M; session 9 is the
        // first to die on the ceiling, and it died before any session lived long enough to be nudged.
        Assert.Equal(1, p.Windows[0].FirstSession);
        Assert.Equal(8, p.Windows[0].LastSession);
        Assert.Null(p.Windows[0].CapTokens);
        Assert.Equal(9, p.Windows[1].FirstSession);
        Assert.Equal(41, p.Windows[1].LastSession);
        Assert.Equal(8_000_000, p.Windows[1].CapTokens);
        Assert.True(p.Windows[1].CapMeasured, "the ceiling was stamped on the rail's own events");
    }

    [Fact]
    public void FaceRun_ReproducesTheUncappedWindowsPublishedScore()
    {
        var before = Face().Windows[0];

        // "1-8, ceiling not yet effective | 7 costed | 158.9M | 6 | 26.5M"
        Assert.Equal(7, before.Costed);
        Assert.Equal(158_931_679, before.Tokens);
        Assert.Equal(6, before.Checkpoints);
        Assert.Equal(26.5, Math.Round(before.TokensPerCheckpoint!.Value / 1e6, 1));
    }

    [Fact]
    public void FaceRun_ReproducesTheCappedWindowsFloorAndClosers()
    {
        var after = Face().Windows[1];

        // "Sessions that did close a checkpoint post-cap ran 4.66M-7.75M, median ~7.3M"
        Assert.Equal(33, after.Costed);
        Assert.Equal(4_655_672, after.Floor);
        Assert.Equal(7_752_892, after.ClosingMax);
        Assert.Equal(7.26, Math.Round(after.ClosingMedian / 1e6, 2));
        Assert.Equal(238_273_734, after.Tokens);

        // The doc prints 17 checkpoints for this window; `newly_done` records 14, and this is what a
        // correction looks like when it is measured rather than argued.
        Assert.Equal(14, after.Checkpoints);
    }

    [Fact]
    public void FaceRun_ReproducesTheMeasuredNudgePointAndWrapUp()
    {
        var after = Face().Windows[1];

        // "At 0.75 x 8M it fires at 6.0M" - measured, the rail lands just past it because it rides a
        // tool call rather than interrupting one.
        Assert.Equal(6.07, Math.Round(after.NudgeTokens!.Value / 1e6, 2));
        Assert.Equal(0.76, Math.Round(after.NudgeRatio!.Value, 2));

        // "the headroom left afterwards (2.0M) was only ~1.4x the observed wrap-up (0.46-1.75M,
        // typically ~1.4M)" - measured against the real firing point rather than ratio x cap.
        var wrap = after.WrapUp!.Value;
        Assert.Equal(20, wrap.Samples);
        Assert.Equal(1.37, Math.Round(wrap.Median / 1e6, 2));
        Assert.InRange(wrap.Max / 1e6, 1.6, 1.8);
        Assert.Equal(1.4, Math.Round(after.Headroom!.Value / (double)wrap.Median, 1));
    }

    [Fact]
    public void FaceRun_ReachesTheSameDiagnosisTheResearchPassReachedByHand()
    {
        var p = Face();
        var after = p.Current;

        // "the nudge sat below the median session that finishes"
        Assert.True(after.NudgeTokens < after.ClosingMedian);
        Assert.Equal(0.84, Math.Round(after.NudgeTokens!.Value / (double)after.ClosingMedian, 2));
        Assert.Contains(p.Prescription.Findings, f => f.StartsWith("NUDGE BELOW THE MEDIAN CLOSER", StringComparison.Ordinal));
        Assert.Contains(p.Prescription.Findings, f => f.StartsWith("HEADROOM THIN", StringComparison.Ordinal));

        // "11 rollovers ... not one of them stopped at the 6.0M nudge" - 10 of them are in the capped
        // window; the eleventh (session 7, 16.5M) predates the ceiling entirely.
        Assert.Equal(10, after.Rollovers);
        Assert.Equal(10, after.RolloversNudgedFirst);
        Assert.Contains(p.Prescription.Findings, f => f.StartsWith("THE RAIL IS DELIVERED AND IGNORED", StringComparison.Ordinal));
        Assert.Equal(1, p.Windows[0].Rollovers);
    }

    [Fact]
    public void FaceRun_PrescribesACapThatClearsItsOwnLargestCloser()
    {
        var p = Face();
        var rx = p.Prescription;

        // The research pass wrote 12M / 0.7 by hand. The analyzer lands on the same band from the
        // measurements alone, and what is asserted here is the RULE, not the round number: the nudge
        // has to clear the largest session that ever closed a checkpoint, and the headroom has to be
        // at least 1.5x the measured wrap-up.
        Assert.True(rx.MaxSessionTokens >= 11_000_000 && rx.MaxSessionTokens <= 13_000_000,
            $"prescribed {rx.MaxSessionTokens}");
        Assert.True(rx.NudgeTokens > p.Current.ClosingMax,
            $"nudge {rx.NudgeTokens} must clear the {p.Current.ClosingMax} largest closer");
        Assert.True(rx.Headroom >= 1.5 * rx.WrapUpBasis,
            $"headroom {rx.Headroom} must be >= 1.5x wrap-up {rx.WrapUpBasis}");
        Assert.True(rx.WrapUpMeasured);
        Assert.Contains("clears the", rx.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void CapPayoff_ComparesTheWindowsRatherThanTheLifetime()
    {
        // 26.5M before against 17.0M after. The published 1.9x used the 17-checkpoint count; on the
        // 14 the ledger actually records, the cap paid 1.6x. Still the headline, one notch smaller.
        Assert.Equal(1.6, Math.Round(Face().CapPayoff!.Value, 1));
    }

    // ------------------------------------------------------------------ the fallbacks

    [Fact]
    public void ACeilingIsInferredFromWhereTheKillsClusterWhenNothingEverNudged()
    {
        // Same run, rail evidence removed - the case of every database written before the soft-break
        // event carried its budget. `OverSessionTokenBudget` fires at >= cap, so the kills pile up
        // against it and the smallest of them bounds it from above.
        var p = BudgetAnalyzer.Analyze("x", "no rail", Sessions(FaceRun), []);

        Assert.Equal(2, p.Windows.Count);
        Assert.Equal(8_000_000, p.Current.CapTokens);
        Assert.False(p.Current.CapMeasured);
        Assert.Equal(9, p.Current.FirstSession);
        Assert.Null(p.Current.NudgeTokens);
        Assert.False(p.Prescription.WrapUpMeasured);
        Assert.Contains(p.Prescription.Findings, f => f.Contains("ASSUMED", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunThatWasNeverCappedIsOneUncappedWindow()
    {
        var sessions = Sessions("""
            1,Advanced,14000000,A.1  2,Advanced,18000000,A.2  3,Progress,9000000,
            4,Advanced,21000000,A.3
            """);
        var p = BudgetAnalyzer.Analyze("x", "uncapped", sessions, []);

        var w = Assert.Single(p.Windows);
        Assert.Null(w.CapTokens);
        Assert.Equal(0, w.Rollovers);
        Assert.Equal(14_000_000, w.Floor);
        Assert.Contains(p.Prescription.Findings, f => f.Contains("no ceiling was in force", StringComparison.Ordinal));
    }

    [Fact]
    public void ACapUnderTheFloorIsTheHeadlineFinding()
    {
        // The sk-studio stage F shape: a 6M ceiling under a repo whose smallest closing session is
        // 6.5M. Everything rolls over, one thing lands, and the total rises.
        var sessions = Sessions("""
            1,RolledOver,6050000,   2,RolledOver,6040000,  3,RolledOver,6060000,
            4,RolledOver,6030000,   5,Advanced,6500000,F.1
            """);
        var nudges = Nudges("1,4200000 2,4210000 3,4190000 4,4205000 5,4200000", 6_000_000);
        var p = BudgetAnalyzer.Analyze("x", "under floor", sessions, nudges);

        Assert.Equal(6_000_000, p.Current.CapTokens);
        Assert.Equal(6_500_000, p.Current.Floor);
        Assert.Contains(p.Prescription.Findings, f => f.StartsWith("CAP BELOW FLOOR", StringComparison.Ordinal));
        Assert.Contains(p.Prescription.Findings, f => f.StartsWith("ROLLOVER RATE", StringComparison.Ordinal));
        Assert.True(p.Prescription.MaxSessionTokens > p.Current.Floor);
    }

    [Fact]
    public void ARunWithNothingDeliveredRefusesToPrescribe()
    {
        var p = BudgetAnalyzer.Analyze("x", "nothing landed", Sessions("1,Progress,5000000,  2,Progress,5000000,"), []);

        Assert.Equal(0, p.Current.Closers);
        Assert.Contains(p.Prescription.Findings, f => f.Contains("no floor to measure", StringComparison.Ordinal));
        Assert.Contains("not enough delivered work", p.Prescription.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonCarriesEveryFigureTheTableShows()
    {
        var json = BudgetJson.Serialize([Face()]);

        Assert.Contains("\"floorTokens\": 4655672", json, StringComparison.Ordinal);
        Assert.Contains("\"capTokens\": 8000000", json, StringComparison.Ordinal);
        Assert.Contains("\"wrapUp\"", json, StringComparison.Ordinal);
        Assert.Contains("\"capPayoff\"", json, StringComparison.Ordinal);
        Assert.Contains("\"prescription\"", json, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the archive end of it

    [Fact]
    public void SoftBreaksAreAttributedToTheSessionThatWasRunning()
    {
        // The event rows this repo has carry a NULL session_id, so attribution walks back to the
        // nearest preceding SessionStarted. Written here the way the engine writes it, then read back
        // through the read-only archive.
        var db = Path.Combine(_tmp, "run.db");
        SeedEvents(db);

        var archive = RunArchive.TryOpen(db);
        Assert.NotNull(archive);
        var breaks = archive!.SoftBreaks("R1");

        Assert.Equal(2, breaks.Count);
        Assert.Equal(1, breaks[0].Session);
        Assert.Equal(6_000_000, breaks[0].LiveTokens);
        Assert.Equal(8_000_000, breaks[0].TokenBudget);
        Assert.Equal("SF2.1", breaks[0].Checkpoint);
        Assert.Equal(2, breaks[1].Session);
        Assert.Equal(6_100_000, breaks[1].LiveTokens);
    }

    [Fact]
    public void AnArchiveWithNoEventLogReportsNoNudgesRatherThanThrowing()
    {
        var db = Path.Combine(_tmp, "bare.db");
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE runs (run_id TEXT PRIMARY KEY, plan_name TEXT NOT NULL, repo TEXT NOT NULL, " +
                              "branch TEXT, driver_ver TEXT, status TEXT NOT NULL, started_utc TEXT NOT NULL, ended_utc TEXT);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var archive = RunArchive.TryOpen(db);
        Assert.NotNull(archive);
        Assert.Empty(archive!.SoftBreaks("R1"));
    }

    private static void SeedEvents(string db)
    {
        using var c = new SqliteConnection($"Data Source={db}");
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE runs (run_id TEXT PRIMARY KEY, plan_name TEXT NOT NULL, repo TEXT NOT NULL, " +
                "  branch TEXT, driver_ver TEXT, status TEXT NOT NULL, started_utc TEXT NOT NULL, ended_utc TEXT);" +
                "INSERT INTO runs VALUES ('R1', 'p', 'r', NULL, NULL, 'completed', '2026-08-01T00:00:00Z', NULL);" +
                "CREATE TABLE events (seq INTEGER NOT NULL, ts TEXT NOT NULL, run_id TEXT NOT NULL, " +
                "  session_id TEXT, type TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (seq, run_id));";
            cmd.ExecuteNonQuery();
        }
        void Add(int seq, string? session, string type, string payload)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO events (seq, ts, run_id, session_id, type, payload) " +
                              "VALUES (@s, '2026-08-01T00:00:00Z', 'R1', @sid, @t, @p)";
            cmd.Parameters.AddWithValue("@s", seq);
            cmd.Parameters.AddWithValue("@sid", (object?)session ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@t", type);
            cmd.Parameters.AddWithValue("@p", payload);
            cmd.ExecuteNonQuery();
        }
        Add(1, "1", "SessionStarted", """{"type":"sessionStarted","number":1}""");
        Add(2, null, "SoftBreakRequested", """{"liveTokens":6000000,"tokenBudget":8000000,"currentCheckpointId":"SF2.1"}""");
        Add(3, "1", "SessionFinished", """{"type":"sessionFinished"}""");
        Add(4, "2", "SessionStarted", """{"type":"sessionStarted","number":2}""");
        Add(5, null, "SoftBreakRequested", """{"liveTokens":6100000,"tokenBudget":8000000}""");
        SqliteConnection.ClearAllPools();
    }
}
