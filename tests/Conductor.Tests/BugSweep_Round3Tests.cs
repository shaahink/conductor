using System.Diagnostics;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Core.Events;
using Conductor.Core.Fleet;
using Conductor.Core.Providers;
using Conductor.Core.Release;
using Conductor.Core.Store;
using Conductor.Core.Telemetry;
using Conductor.Models;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>The 2026-08-27 bug sweep, round 3 — #75 #45 #48 #40 #93 #95 #53 #73. One class per bug.</summary>
public sealed class Bug75_AMultiLineNoteBodyArrivesWhole : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bug75-" + Guid.NewGuid().ToString("N")[..8]);

    public Bug75_AMultiLineNoteBodyArrivesWhole() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>The diagnosis, reproduced: an argument handed through a <c>.cmd</c> file ends at its
    /// first newline. This is the installed binary's whole path — scoop's shim is
    /// <c>@"…\conductor.exe" %*</c> — and it is why three DV3 acceptance records are one line each.</summary>
    [Fact]
    public async Task TheCmdShimKeepsOnlyTheFirstLineOfAnArgument()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shim = Path.Combine(_dir, "echoargs.cmd");
        await File.WriteAllTextAsync(shim, "@echo off\r\necho [%*]\r\n").ConfigureAwait(true);

        var psi = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(shim);
        psi.ArgumentList.Add("line1\nline2\nline3");
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
        await p.WaitForExitAsync().ConfigureAwait(true);

        Assert.Contains("line1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("line2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ADashReadsTheBodyFromStdin_AndNothingElseDoes()
    {
        using var piped = new StringReader("DV3.3 ACCEPTANCE, declared before editing. Done means:\r\n- one\r\n- two\r\n");
        var body = LedgerBody.Resolve("-", piped, stdinRedirected: true);
        Assert.Equal("DV3.3 ACCEPTANCE, declared before editing. Done means:\n- one\n- two", body);

        using var untouched = new StringReader("must not be read");
        Assert.Equal("a plain argument", LedgerBody.Resolve("a plain argument", untouched, stdinRedirected: true));
        Assert.Equal("must not be read", untouched.ReadToEnd());
    }

    /// <summary>A verb waiting on an interactive terminal for EOF is a session hung on a prompt.</summary>
    [Fact]
    public void ADashWithNothingPipedIsRefused_NotWaitedOn()
    {
        using var tty = new StringReader("");
        var ex = Assert.Throws<InvalidOperationException>(() => LedgerBody.Resolve("-", tty, stdinRedirected: false));
        Assert.Contains("conductor note - <", ex.Message, StringComparison.Ordinal);
        using var empty = new StringReader("   \n");
        Assert.Throws<InvalidOperationException>(() => LedgerBody.Resolve("-", empty, stdinRedirected: true));
    }

    /// <summary>The shape the shim leaves behind is warned about; ordinary one-liners are not.</summary>
    [Fact]
    public void TheHeaderOfAListWhoseItemsNeverArrived_LooksCut()
    {
        Assert.True(LedgerBody.LooksCut("DV3.3 ACCEPTANCE, declared before editing. Done means:"));
        Assert.True(LedgerBody.LooksCut("Four things the next session should not re-derive -"));
        Assert.False(LedgerBody.LooksCut("DV3.3 ACCEPTANCE, declared before editing. Done means:\n- the bar"));
        Assert.False(LedgerBody.LooksCut("write every conductor note as ONE line."));
        Assert.False(LedgerBody.LooksCut(""));
        Assert.False(LedgerBody.LooksCut(null));
    }
}

public sealed class Bug45_ANewerBuildDoesNotMigrateAStoreALiveEngineHolds : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bug45-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _repo;
    private readonly string _stateDir;
    private readonly string _db;

    public Bug45_ANewerBuildDoesNotMigrateAStoreALiveEngineHolds()
    {
        _repo = Path.Combine(_dir, "repo");
        _stateDir = Path.Combine(_repo, StateHome.ScratchDirName);
        _db = Path.Combine(_dir, "store", "run.db");
        Directory.CreateDirectory(_stateDir);
    }

    public void Dispose()
    {
        EngineLock.Delete(_stateDir);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A store at the current version with an unfinished run in this repo, then wound back
    /// one version so the next open would migrate it — the KS10.1 shape, in miniature.</summary>
    private void SeedBehindStore()
    {
        using (var store = new SqliteRunStore(_db, NullLogger<SqliteRunStore>.Instance))
            store.InitializeRun("run-45", "plan", _repo, "master", EngineStamp.Parse("1.0"));
        SqliteConnection.ClearAllPools();
        SetVersion(SqliteRunStore.CurrentSchemaVersion - 1);
    }

    private void SetVersion(int v)
    {
        using var c = new SqliteConnection($"Data Source={_db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE schema_version SET version = @v";
        cmd.Parameters.AddWithValue("@v", v);
        cmd.ExecuteNonQuery();
        c.Close();
        SqliteConnection.ClearAllPools();
    }

    private int ReadVersion()
    {
        using var c = new SqliteConnection($"Data Source={_db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version";
        var v = (int)(long)cmd.ExecuteScalar()!;
        c.Close();
        SqliteConnection.ClearAllPools();
        return v;
    }

    [Fact]
    public void TheOpenIsRefusedWithThePidAndTheWayOut_AndTheFileIsLeftAsItWas()
    {
        SeedBehindStore();
        EngineLock.Write(_stateDir);   // this process, with its real start time: a live engine

        var ex = Assert.Throws<InvalidOperationException>(() => new SqliteRunStore(_db, NullLogger<SqliteRunStore>.Instance));

        Assert.Contains("bug #45", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"pid {Environment.ProcessId}", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"v{SqliteRunStore.CurrentSchemaVersion - 1}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Stop the run first", ex.Message, StringComparison.Ordinal);
        Assert.Equal(SqliteRunStore.CurrentSchemaVersion - 1, ReadVersion());
    }

    /// <summary>The refusal is about the LIVE engine, not the lock file: a lock left behind by a dead
    /// engine migrates as before, and so does a store nobody holds.</summary>
    [Fact]
    public void AStoreNobodyHolds_StillMigrates()
    {
        SeedBehindStore();
        EngineLock.Write(_stateDir);
        EngineLock.Delete(_stateDir);

        using var store = new SqliteRunStore(_db, NullLogger<SqliteRunStore>.Instance);
        store.Dispose();
        Assert.Equal(SqliteRunStore.CurrentSchemaVersion, ReadVersion());
    }

    /// <summary>The negative control: a store already AT this version is never refused, whoever holds
    /// it — the engine's own verbs open their own store all day.</summary>
    [Fact]
    public void AStoreAtThisVersion_OpensUnderALiveEngine()
    {
        using (var store = new SqliteRunStore(_db, NullLogger<SqliteRunStore>.Instance))
            store.InitializeRun("run-45", "plan", _repo, "master", EngineStamp.Parse("1.0"));
        SqliteConnection.ClearAllPools();
        EngineLock.Write(_stateDir);

        using var again = new SqliteRunStore(_db, NullLogger<SqliteRunStore>.Instance);
        Assert.NotNull(again.GetLatestRunId("plan"));
    }

    [Fact]
    public void AFinishedRunDoesNotHoldItsStore()
    {
        SeedBehindStore();
        using (var c = new SqliteConnection($"Data Source={_db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE runs SET status = 'completed'";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        EngineLock.Write(_stateDir);

        using var store = new SqliteRunStore(_db, NullLogger<SqliteRunStore>.Instance);
        store.Dispose();
        Assert.Equal(SqliteRunStore.CurrentSchemaVersion, ReadVersion());
    }
}

public sealed class Bug48_TheFaceSaysWhenItAttachesPastThisDirectorysPlan
{
    private static FleetRun Run(int port, string repo) =>
        new(Port: port, BaseUrl: $"http://127.0.0.1:{port}", PlanName: $"{repo} plan",
            RunId: "7951c3ca149a4c12a5a7fb973bbea1bf", Repo: repo, StateDir: $"{repo}/.conductor",
            Status: "Running", StageId: "KS10", StageTitle: "t", AttentionReason: null, Done: 1, Total: 2, CostUsd: 0m);

    [Fact]
    public void TheOnlyLiveRunStillWins_ButTheDecisionSaysItWidened()
    {
        var d = FaceTarget.Choose([Run(4318, "C:/code/conductor")], "C:/code/scratch-rig/.conductor", pick: false);

        Assert.Equal(FaceTarget.Kind.Single, d.Kind);
        Assert.True(d.Widened);
    }

    [Fact]
    public void NoPlanHere_IsNotAWidening()
    {
        var d = FaceTarget.Choose([Run(4318, "C:/code/conductor")], localStateDir: null, pick: false);
        Assert.Equal(FaceTarget.Kind.Single, d.Kind);
        Assert.False(d.Widened);
    }

    [Fact]
    public void ThisDirectorysOwnRun_IsNotAWidening()
    {
        var d = FaceTarget.Choose([Run(4317, "C:/code/conductor"), Run(4318, "C:/code/sk-studio")], "C:/code/conductor/.conductor", pick: false);
        Assert.Equal(FaceTarget.Kind.Single, d.Kind);
        Assert.False(d.Widened);
    }

    [Fact]
    public void ThePickerIsWidenedToo_WhenThisDirectoryHadAPlan()
    {
        var d = FaceTarget.Choose([Run(4317, "C:/code/conductor"), Run(4318, "C:/code/sk-studio")], "C:/code/elsewhere/.conductor", pick: false);
        Assert.Equal(FaceTarget.Kind.Picker, d.Kind);
        Assert.True(d.Widened);
    }

    /// <summary>The one line before the TUI takes the terminal names the plan that has no run, the
    /// run being shown instead, and the flag that chooses explicitly.</summary>
    [Fact]
    public void TheNoticeNamesThePlanTheRunAndTheWayToChoose()
    {
        var notice = FaceCommand.WidenedNotice("scratch rig", Run(4318, "C:/code/conductor"));
        Assert.StartsWith("notice:", notice, StringComparison.Ordinal);
        Assert.Contains("'scratch rig'", notice, StringComparison.Ordinal);
        Assert.Contains("has no live run", notice, StringComparison.Ordinal);
        Assert.Contains("C:/code/conductor", notice, StringComparison.Ordinal);
        Assert.Contains("not this directory's run", notice, StringComparison.Ordinal);
        Assert.Contains("--pick", notice, StringComparison.Ordinal);

        Assert.Contains("picker", FaceCommand.WidenedNotice("", null), StringComparison.Ordinal);
    }
}

public sealed class Bug40_SatelliteCommitsAreCreditedOnlyToASessionThatTouchedTheSatellite : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bug40-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly PlanConfig _plan;

    public Bug40_SatelliteCommitsAreCreditedOnlyToASessionThatTouchedTheSatellite()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "primary"));
        Directory.CreateDirectory(Path.Combine(_dir, "site"));
        _plan = new PlanConfig
        {
            Name = "bug40",
            Repo = Path.Combine(_dir, "primary"),
            SatelliteRepos = [Path.Combine(_dir, "site")],
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ToolCall Call(string name, string key, string value) =>
        new(name, new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

    [Fact]
    public void AWriteInsideTheSatellite_Touches()
    {
        Assert.Equal("site", SatelliteRepos.Touched(_plan, Call("Write", "path", Path.Combine(_dir, "site", "src", "index.astro"))));
        Assert.Equal("site", SatelliteRepos.Touched(_plan, Call("Edit", "path", Path.Combine(_dir, "site", "README.md").Replace('\\', '/'))));
    }

    /// <summary>The KS0 session's whole history with the field guide: listing, reading, grepping.</summary>
    [Fact]
    public void ReadingTheSatellite_DoesNotTouch()
    {
        Assert.Null(SatelliteRepos.Touched(_plan, Call("Read", "path", Path.Combine(_dir, "site", "README.md"))));
        Assert.Null(SatelliteRepos.Touched(_plan, Call("Grep", "path", Path.Combine(_dir, "site"))));
        Assert.Null(SatelliteRepos.Touched(_plan, Call("Bash", "command", "git status --short")));
    }

    [Fact]
    public void ARelativeWritePathResolvesAgainstThePrimary_NotTheSatellite()
    {
        Assert.Null(SatelliteRepos.Touched(_plan, Call("Write", "path", "src/App.cs")));
        Assert.Equal("site", SatelliteRepos.Touched(_plan, Call("Write", "path", "../site/src/App.cs")));
    }

    [Fact]
    public void AShellCommandThatNamesTheSatellitesDirectory_Touches()
    {
        var abs = Path.Combine(_dir, "site");
        Assert.Equal("site", SatelliteRepos.Touched(_plan, Call("Bash", "command", $"git -C {abs} commit -m \"feat: page\"")));
        Assert.Equal("site", SatelliteRepos.Touched(_plan, Call("Bash", "command", $"cd {abs.Replace('\\', '/')} && npm run build")));
        Assert.Equal("site", SatelliteRepos.Touched(_plan, Call("Bash", "command", "git -C ../site push")));
        // The bare label is a word, not a directory.
        Assert.Null(SatelliteRepos.Touched(_plan, Call("Bash", "command", "echo the site is fine")));
    }

    [Fact]
    public void AttributionSplitsByTheLabelSuffix_AndKeepsOrder()
    {
        var (own, foreign) = SatelliteRepos.Attribute(
            ["8f9ea3b feat: harvest [site]", "bc3f071 chore: bump [site]", "39c3214 feat: x [other]"],
            ["SITE"]);

        Assert.Equal(["8f9ea3b feat: harvest [site]", "bc3f071 chore: bump [site]"], own);
        Assert.Equal(["39c3214 feat: x [other]"], foreign);
    }

    /// <summary>Run 9647f1b8, session #4: four commits in a satellite the session never wrote to.</summary>
    [Fact]
    public void ASessionThatTouchedNothing_IsCreditedWithNothing()
    {
        var (own, foreign) = SatelliteRepos.Attribute(
            ["8f9ea3b a [site]", "bc3f071 b [site]", "39c3214 c [site]", "236eb82 d [site]"], []);
        Assert.Empty(own);
        Assert.Equal(4, foreign.Count);
    }
}

public sealed class Bug93_TheCourierLeavesARecord : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bug93-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheLogLivesBesideTheSettings()
    {
        Assert.Equal(Path.Combine(_root, "courier", "courier.log"), CourierHome.LogPathFor(_root));
        Assert.Equal(CourierHome.LogPathFor(_root), CourierLog.At(_root).Path);
    }

    [Fact]
    public void LinesAreStampedAppendedAndTailed()
    {
        var log = CourierLog.At(_root);
        Assert.Empty(log.Tail());
        Assert.Null(log.LastWriteUtc());

        log.Append("courier run starting: pid 1");
        log.Append("refused to start: no token\nsecond line folded");
        log.Append("courier run stopped: exit 1");

        var tail = log.Tail(2);
        Assert.Equal(2, tail.Count);
        Assert.EndsWith("courier run stopped: exit 1", tail[1], StringComparison.Ordinal);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z ", tail[0]);
        Assert.Contains("second line folded", tail[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\n", tail[0], StringComparison.Ordinal);
        Assert.NotNull(log.LastWriteUtc());
    }

    [Fact]
    public void TheFileRotates_AndKeepsOneGeneration()
    {
        var log = CourierLog.At(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(log.Path)!);
        File.WriteAllText(log.Path, new string('x', (int)CourierLog.RotateAtBytes + 10));

        log.Append("after the rotation");

        Assert.True(File.Exists(log.Path + ".1"));
        Assert.True(new FileInfo(log.Path).Length < 200);
        Assert.Single(log.Tail());
    }

    /// <summary>The daemon's own logger lines reach the file without a second phrasing.</summary>
    [Fact]
    public void TheLoggerProviderMirrorsWarningsAndInfo_NotDebug()
    {
        var log = CourierLog.At(_root);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(log.AsProvider()));
        var logger = factory.CreateLogger("courier");

        logger.LogInformation("Courier listening on {Url}", "http://127.0.0.1:4330");
        logger.LogWarning("Courier has no loopback listener: {Why}", "port taken");
        logger.LogDebug("noise");

        var tail = log.Tail();
        Assert.Equal(2, tail.Count);
        Assert.Contains("[info] courier: Courier listening on http://127.0.0.1:4330", tail[0], StringComparison.Ordinal);
        Assert.Contains("[warn] courier: Courier has no loopback listener: port taken", tail[1], StringComparison.Ordinal);
    }
}

public sealed class Bug95_TheDocsRowsTheTagMakesFalse : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "bug95-" + Guid.NewGuid().ToString("N")[..8]);

    private const string Row =
        "| `release preflight` | **New since `v0.5.0`; not in the released binary yet — the era-close, measured instead of written.** Six preconditions. |";

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheClauseIsRewrittenToNameTheRelease_AndTheRestOfTheRowSurvives()
    {
        var after = DocsFacts.Rewrite(Row, "0.6.0");
        Assert.Equal("| `release preflight` | **New in `v0.6.0` — the era-close, measured instead of written.** Six preconditions. |", after);
        Assert.Equal("**New in `v0.6.0`.**", DocsFacts.Rewrite("**New since `v0.5.0`; not in the released binary yet.**", "v0.6.0"));
        Assert.Equal("no clause here", DocsFacts.Rewrite("no clause here", "0.6.0"));
    }

    [Fact]
    public void ThePreflightLineIsGreenWithNoRows_AndRedWithAny_NamingTheAct()
    {
        Assert.Equal(ReleaseCheck.Ok, ReleasePreflight.Docs(new DocsFacts([], 7), "0.6.0").State);

        var red = ReleasePreflight.Docs(new DocsFacts([new DocsRow("docs/cli.md", 150, Row), new DocsRow("docs/operating.md", 76, Row)], 7), "0.6.0");
        Assert.Equal(ReleaseCheck.Fail, red.State);
        Assert.Contains("2 docs row(s)", red.Headline, StringComparison.Ordinal);
        Assert.Contains("docs/cli.md:150", red.Detail[0], StringComparison.Ordinal);
        Assert.Contains("release perform --tag 0.6.0", red.Detail[^1], StringComparison.Ordinal);

        Assert.Equal(1, ReleasePreflight.ExitCode([red]));
    }

    [Fact]
    public void TheCheckAndTheActAreNamedAndOrdered()
    {
        Assert.Equal(["merge", "changelog", "docs", "processes", "migration", "courier", "backfill"], ReleasePreflight.CheckNames);
        Assert.Equal(["changelog", "docs", "merge", "tag", "docmove"], ReleasePerform.MechanicalOrder);
    }

    [Fact]
    public void TheActIsRefusedWithoutAVersion_ReadyWithOne_AndNothingWhenClean()
    {
        var rows = new DocsFacts([new DocsRow("docs/cli.md", 150, Row)], 7);
        Assert.Equal(ReleaseAct.Refused, ReleasePerform.Docs(null, rows).State);
        Assert.Equal(ReleaseAct.Ready, ReleasePerform.Docs("0.6.0", rows).State);
        Assert.Contains("v0.6.0", ReleasePerform.Docs("v0.6.0", rows).Headline, StringComparison.Ordinal);
        Assert.Equal(ReleaseAct.Nothing, ReleasePerform.Docs("0.6.0", new DocsFacts([], 7)).State);
    }

    /// <summary>The probe reads the top level of docs/ and nothing under it: the record and the dev
    /// notes may quote the phrase for ever.</summary>
    [Fact]
    public void TheProbeScansTopLevelDocsOnly()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "docs", "history"));
        File.WriteAllText(Path.Combine(_repo, "docs", "cli.md"), "# cli\n\nplain\n" + Row + "\n");
        File.WriteAllText(Path.Combine(_repo, "docs", "quickstart.md"), "nothing here\n");
        File.WriteAllText(Path.Combine(_repo, "docs", "history", "old.md"), Row + "\n");

        var facts = ReleaseCommand.ProbeDocs(_repo);

        Assert.Equal(2, facts.FilesScanned);
        var row = Assert.Single(facts.Rows);
        Assert.Equal("docs/cli.md", row.File);
        Assert.Equal(4, row.Line);
    }

    [Fact]
    public void ARepoWithNoDocsMeasuresZeroFiles()
    {
        Directory.CreateDirectory(_repo);
        var facts = ReleaseCommand.ProbeDocs(_repo);
        Assert.Equal(0, facts.FilesScanned);
        Assert.Empty(facts.Rows);
    }
}

public sealed class Bug53_TheCacheWriteTtlSplitIsNamed
{
    private const string TurnWithSplit =
        """{"type":"assistant","message":{"id":"msg_53","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":9306,"cache_creation":{"ephemeral_5m_input_tokens":300,"ephemeral_1h_input_tokens":9006},"cache_read_input_tokens":20673,"output_tokens":4}}}""";

    private const string TurnWithoutSplit =
        """{"type":"assistant","message":{"id":"msg_53b","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":9306,"cache_read_input_tokens":20673,"output_tokens":4}}}""";

    [Fact]
    public void TheOneHourHalfIsASubsetOfTheWrite_OnTheDeltaAndTheState()
    {
        var deltas = new List<(long Input, long CacheWrite, long CacheWrite1h)>();
        var state = new AgentStreamState((_, _) => { }, (i, _, _, _, cw, _, cw1h) => deltas.Add((i, cw, cw1h)));

        new ClaudeProvider().ParseLine(TurnWithSplit, state);

        var d = Assert.Single(deltas);
        Assert.Equal(9006, d.CacheWrite1h);
        Assert.Equal(9306, d.CacheWrite);
        Assert.Equal(2 + 9306, d.Input);
        Assert.True(d.CacheWrite1h <= d.CacheWrite, "the 1h part is contained in the write half");
        Assert.Equal(9006, state.TokensCacheWrite1h);
        Assert.Equal(9306, state.TokensCacheWrite);
    }

    /// <summary>A wire that reports no split leaves the field null — "not reported" is not "all 5m".</summary>
    [Fact]
    public void NoSplitOnTheWire_IsNull_NotZero()
    {
        var deltas = new List<long>();
        var state = new AgentStreamState((_, _) => { }, (_, _, _, _, _, _, cw1h) => deltas.Add(cw1h));

        new ClaudeProvider().ParseLine(TurnWithoutSplit, state);

        Assert.Equal(0, Assert.Single(deltas));
        Assert.Null(state.TokensCacheWrite1h);
        Assert.Equal(9306, state.TokensCacheWrite);
    }

    [Fact]
    public void TheResultEnvelopeNamesItToo()
    {
        var state = new AgentStreamState((_, _) => { });
        new ClaudeProvider().ParseLine(
            """{"type":"result","subtype":"success","usage":{"input_tokens":2,"cache_creation_input_tokens":11374,"cache_creation":{"ephemeral_5m_input_tokens":1374,"ephemeral_1h_input_tokens":10000},"cache_read_input_tokens":12528,"output_tokens":1}}""",
            state);
        Assert.Equal(10000, state.TokensCacheWrite1h);
        Assert.Equal(11374, state.TokensCacheWrite);
    }

    [Fact]
    public void TheTraceCarriesItBesideTheWriteTotal_AtRunSessionAndTurn()
    {
        const string runId = "run53";
        static DateTimeOffset At(int m) => new(2026, 8, 27, 12, m, 0, TimeSpan.Zero);
        var spans = OtelTrace.Build(
        [
            new RunStarted { Seq = 1, Ts = At(0), RunId = runId, Plan = "p", Repo = "C:/code/x", Branch = "master" },
            new StageEntered { Seq = 2, Ts = At(0), RunId = runId, StageId = "S1", Title = "s" },
            new SessionStarted { Seq = 3, Ts = At(0), RunId = runId, SessionId = "1", Number = 1, StageId = "S1", Kind = "work", Attempt = 1, Model = "claude-opus-5" },
            new TokenDelta { Seq = 4, Ts = At(1), RunId = runId, SessionId = "1", Input = 9308, Output = 4, CacheRead = 20673, CacheWrite = 9306, CacheWrite1h = 9006 },
            new TokenDelta { Seq = 5, Ts = At(2), RunId = runId, SessionId = "1", Input = 900, Output = 30, CacheRead = 50000, CacheWrite = 100, CacheWrite1h = 0 },
            new SessionFinished { Seq = 6, Ts = At(3), RunId = runId, SessionId = "1", Number = 1, StageId = "S1", Outcome = "success", CostUsd = 1m },
            new RunFinished { Seq = 7, Ts = At(4), RunId = runId, Status = "completed", Sessions = 1 },
        ]);

        static long Attr(OtelSpan s, string key) => (long)s.Attributes.Single(a => a.Key == key).Value;

        var run = Assert.Single(spans, s => s.Name == "conductor.run");
        Assert.Equal(9406, Attr(run, "gen_ai.usage.cache_creation_input_tokens"));
        Assert.Equal(9006, Attr(run, OtelTrace.CacheWrite1hAttribute));

        var session = Assert.Single(spans, s => s.Name.StartsWith("chat ", StringComparison.Ordinal));
        Assert.Equal(9406, Attr(session, "gen_ai.usage.cache_creation_input_tokens"));
        Assert.Equal(9006, Attr(session, OtelTrace.CacheWrite1hAttribute));

        var turn = session.Events.First(e => e.Name == "gen_ai.turn");
        Assert.Equal(9006L, turn.Attributes.Single(a => a.Key == OtelTrace.CacheWrite1hAttribute).Value);
    }
}

/// <summary>Bug #73 is two PowerShell rigs; the pin is that both now name the scratch's own run.db
/// (precedence rule 1 of StateHome.Resolve) and that no query in the window-close rig is aimed at a
/// plan FILE as if it were a directory.</summary>
public sealed class Bug73_TheToolsRigsWriteNoRunIntoTheOperatorsStateHome
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Conductor.slnx not found above " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("tools/sf1/sf1-2-live-proof.ps1")]
    [InlineData("tools/w3/window-close.ps1")]
    public void TheRigNamesItsOwnRunDb(string rel)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("$env:CONDUCTOR_RUN_DB", text, StringComparison.Ordinal);
        Assert.Contains("-RunDb", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowCloseQueriesAreAimedAtDirectories_NotPlanFiles()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "w3", "window-close.ps1"));
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("& $q ", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain(".Plan", line, StringComparison.Ordinal);
            Assert.Contains(".Dir", line, StringComparison.Ordinal);
        }
    }
}
