using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.History;
using Conductor.Core.Release;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// The 2026-08-27 bug sweep, round 1 — nine ledger bugs whose fix is one seam each. One class per
/// bug, each named for the ledger row it closes, so a reader of <c>conductor bug list --all</c> can
/// find the proof by number.
/// </summary>
public sealed class Bug58_ParseFailingGatesReadsTheGlyphsTheSummaryEmits
{
    /// <summary>The summary a session record carries is <c>name:glyph</c> joined by " · ", and the
    /// glyphs are <see cref="GateResult.Glyph"/>'s words — never the check-marks the parser used to
    /// look for. Every failing spelling is read; every passing, skipped and cached one is not.</summary>
    [Fact]
    public void EveryFailingGlyph_IsRead_AndNoPassingOneIs()
    {
        var failing = FailureCircuitBreaker.ParseFailingGates(
            "engine-fast:OK · face-fast:FAIL · engine-full:REGRESSION · face-full:warn · a:cached · b:- " +
            "· c:MUTANTS-warn · d:FAIL-retry · e:OK-retry · f:REGRESSION-warn · g:MUTANTS");

        Assert.Equal(["c", "d", "engine-full", "f", "face-fast", "face-full", "g"], failing.Order(StringComparer.Ordinal));
    }

    /// <summary>Round trip: the fingerprint rebuilt from <see cref="GateRunner.Summary"/> is the same
    /// set the live battery would produce, which is the comparison the breaker actually makes.</summary>
    [Fact]
    public void TheRecordedSummary_RoundTripsToTheLiveFingerprint()
    {
        var gates = new List<GateResult>
        {
            new("build", Passed: true, Skipped: false, Optional: false, 0, TimeSpan.Zero, ""),
            new("test", Passed: false, Skipped: false, Optional: false, 1, TimeSpan.Zero, ""),
            new("lint", Passed: false, Skipped: false, Optional: true, 1, TimeSpan.Zero, ""),
            new("skip", Passed: false, Skipped: true, Optional: false, 0, TimeSpan.Zero, ""),
            new("holdout", Passed: true, Skipped: false, Optional: false, 0, TimeSpan.Zero, "") { Regressions = ["X.Y"] },
        };

        var parsed = FailureCircuitBreaker.ParseFailingGates(GateRunner.Summary(gates));

        Assert.Equal(["holdout", "lint", "test"], parsed.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("✗", true)]
    [InlineData("FAIL", true)]
    [InlineData("FAIL-retry", true)]
    [InlineData("warn", true)]
    [InlineData("REGRESSION", true)]
    [InlineData("REGRESSION-warn", true)]
    [InlineData("MUTANTS", true)]
    [InlineData("OK", false)]
    [InlineData("OK-retry", false)]
    [InlineData("cached", false)]
    [InlineData("-", false)]
    public void TheGlyphTable(string glyph, bool failing) =>
        Assert.Equal(failing, FailureCircuitBreaker.IsFailingGlyph(glyph));
}

public sealed class Bug43_EvidenceReaderAcceptsFourDigitIds : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "bug43-" + Guid.NewGuid().ToString("N")[..8]);

    public Bug43_EvidenceReaderAcceptsFourDigitIds() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
    }

    /// <summary>A spec-kit board past task 999, or a Phase 1000, mints ids the progress provider
    /// accepts. The evidence reader accepted three digits and silently returned no id for four.</summary>
    [Theory]
    [InlineData("P1000.12-plan.md", "P1000.12")]
    [InlineData("T1.1234-result.txt", "T1.1234")]
    [InlineData("KS12.3-runbook.md", "KS12.3")]
    public async Task AFourDigitPhaseOrTask_IsRecoveredFromTheFileName(string name, string expected)
    {
        var dir = Path.Combine(_repo, "evidence");
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, name);
        await File.WriteAllTextAsync(full, "x");

        var artifact = await EvidenceReader.ReadAsync(full, _repo, null, null, "watcher");

        Assert.NotNull(artifact);
        Assert.Equal(expected, artifact.CheckpointId);
    }
}

public sealed class Bug70_AgentConfigMergeCarriesEnv
{
    /// <summary>The plan sets OPENCODE_CONFIG; a stage overrides the agent's model. The merged config
    /// used to have no Env at all — the override wiped it by omission.</summary>
    [Fact]
    public void AStageOverrideWithoutEnv_KeepsThePlanEnv()
    {
        var plan = new AgentConfig { Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_CONFIG"] = "a.json" } };
        var merged = plan.Merge(new AgentConfig { Model = "opus" });

        Assert.NotNull(merged.Env);
        Assert.Equal("a.json", merged.Env!["OPENCODE_CONFIG"]);
        Assert.Equal("opus", merged.Model);
    }

    /// <summary>Per key: the override's entries win, the base's survive, and neither input is mutated.</summary>
    [Fact]
    public void AStageOverrideWithEnv_MergesPerKey()
    {
        var plan = new AgentConfig { Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "plan", ["B"] = "plan" } };
        var stage = new AgentConfig { Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["B"] = "stage", ["C"] = "stage" } };

        var merged = plan.Merge(stage);

        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "plan", ["B"] = "stage", ["C"] = "stage" }, merged.Env);
        Assert.Equal(2, plan.Env!.Count);
        Assert.Equal(2, stage.Env!.Count);
    }

    [Fact]
    public void NoEnvAnywhere_StaysNull() => Assert.Null(new AgentConfig().Merge(new AgentConfig { Model = "x" }).Env);
}

public sealed class Bug87_CourierStatusVerdictConsultsPresence
{
    private static readonly CourierPresence Live = new(2, 33884, "0.5.0", null, "Conductor Courier",
        new DateTimeOffset(2026, 8, 26, 22, 4, 43, TimeSpan.Zero), 4400);

    /// <summary>The line that used to tell the owner to start what was already up.</summary>
    [Fact]
    public void ALiveInstance_IsReportedAsPolling_NotAsSomethingToStart()
    {
        var line = CourierCommand.VerdictLine(null, Live);

        Assert.Contains("polling", line, StringComparison.Ordinal);
        Assert.Contains("33884", line, StringComparison.Ordinal);
        Assert.DoesNotContain("`conductor courier run` starts polling", line, StringComparison.Ordinal);
    }

    /// <summary>A blocker seen from this shell does not un-run the daemon; it is named as a note.</summary>
    [Fact]
    public void ALiveInstanceWithABlockerHere_IsStillPolling_AndTheBlockerIsNamed()
    {
        var line = CourierCommand.VerdictLine("no bot token", Live);

        Assert.StartsWith("[green]polling[/]", line, StringComparison.Ordinal);
        Assert.Contains("no bot token", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingRunningAndNoBlocker_IsReady() =>
        Assert.Contains("`conductor courier run` starts polling", CourierCommand.VerdictLine(null, null), StringComparison.Ordinal);

    [Fact]
    public void NothingRunningWithABlocker_IsNotReady() =>
        Assert.StartsWith("[yellow]not ready:[/]", CourierCommand.VerdictLine("no bot token", null), StringComparison.Ordinal);
}

public sealed class Bug94_ADryRunIsNotRefusedUnderALiveRun
{
    [Fact]
    public void TheRehearsalProceeds() => Assert.False(ReleasePerform.LiveRunRefuses(dryRun: true));

    [Fact]
    public void TheRealRunIsStillRefused() => Assert.True(ReleasePerform.LiveRunRefuses(dryRun: false));
}

public sealed class Bug96_ThePreflightRemedyNamesTheActThatRenames
{
    /// <summary>The [Unreleased] heading is there and the script refuses 0.6.0: the remedy is the
    /// verb that performs the rename, not a hand edit of the very heading that verb rewrites.</summary>
    [Fact]
    public void WithAnUnreleasedHeading_TheRemedyIsReleasePerform()
    {
        var check = ReleasePreflight.Changelog(new ChangelogFacts("0.6.0", FileExists: true,
            ["## [Unreleased]", "## [0.5.0] - 2026-08-26"], ScriptRan: true, ScriptExit: 1, [], ""));

        Assert.Equal(ReleaseCheck.Fail, check.State);
        Assert.Contains(check.Detail, d => d.Contains("conductor release perform --tag 0.6.0", StringComparison.Ordinal));
        Assert.DoesNotContain(check.Detail, d => d.Contains("by hand", StringComparison.Ordinal));
    }

    /// <summary>No heading to rename: the hand edit is the honest answer and stays.</summary>
    [Fact]
    public void WithoutAnUnreleasedHeading_TheRemedyIsTheHandEdit()
    {
        var check = ReleasePreflight.Changelog(new ChangelogFacts("0.6.0", FileExists: true,
            ["## [0.5.0] - 2026-08-26"], ScriptRan: true, ScriptExit: 1, [], ""));

        Assert.Equal(ReleaseCheck.Fail, check.State);
        Assert.Contains(check.Detail, d => d.Contains("by hand", StringComparison.Ordinal));
        Assert.DoesNotContain(check.Detail, d => d.Contains("release perform", StringComparison.Ordinal));
    }
}

public sealed class Bug91_AParkedRunIsOwedItsRecord
{
    /// <summary>The Karvansara edge run: <c>needs_human</c> in a store the live Charkh engine holds.
    /// Still going, in the doctor's sense — an engine may resume it from the stale plan — but not
    /// working, in the corpus act's sense, so its backfill is owed now.</summary>
    [Theory]
    [InlineData("needs_human")]
    [InlineData("paused")]
    [InlineData("awaiting_owner")]
    public void APark_IsStillGoing_ButNotWorking(string status)
    {
        Assert.True(RunLiveness.IsStillGoing(status, storeLooksLive: true));
        Assert.False(RunLiveness.IsWorking(status, storeLooksLive: true));
        Assert.True(RunRecord.IsParked(status));
    }

    [Fact]
    public void ARunningRowInALiveStore_IsWorking()
    {
        Assert.True(RunLiveness.IsWorking("running", storeLooksLive: true));
        Assert.False(RunRecord.IsParked("running"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("aborted")]
    [InlineData("closed")]
    public void ATerminalRow_IsNeitherParkedNorWorking(string status)
    {
        Assert.False(RunRecord.IsParked(status));
        Assert.False(RunLiveness.IsWorking(status, storeLooksLive: true));
    }

    [Fact]
    public void ADeadStore_IsNotWorkingWhateverTheRowSays() =>
        Assert.False(RunLiveness.IsWorking("running", storeLooksLive: false));
}

public sealed class Bug61_TheMeasuringVerbsHonourTheRunDbOverride : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bug61-" + Guid.NewGuid().ToString("N")[..8]);

    public Bug61_TheMeasuringVerbsHonourTheRunDbOverride() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private string SeedDatabase(string runId, string plan)
    {
        var db = Path.Combine(_tmp, "override", "run.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        store.InitializeRun(runId, plan, _tmp, "master", EngineStamp.Parse("0.5.0+test"));
        return db;
    }

    /// <summary>The variable points at a database holding a run; the catalogue (an empty root) holds
    /// nothing. The verb used to answer from the catalogue and say "no runs to measure".</summary>
    [Fact]
    public void WithTheVariableSet_TheNamedDatabaseAnswers_NotTheCatalogue()
    {
        var db = SeedDatabase("run-bug61-aaaa", "edge");
        var emptyRoot = Path.Combine(_tmp, "empty-root");
        Directory.CreateDirectory(emptyRoot);

        var resolved = RunSources.Resolve(emptyRoot, RunHistoryFilter.All, selector: null, repo: _tmp, envOverride: db);

        Assert.NotNull(resolved);
        var one = Assert.Single(resolved);
        Assert.Equal("run-bug61-aaaa", one.Run.RunId);
        Assert.Equal(Path.GetFullPath(db), one.Db);
    }

    /// <summary>A selector is looked up INSIDE the named database — by id prefix or plan name — and a
    /// miss is a refusal that names the variable, never a silent fall-through to another run.</summary>
    [Fact]
    public void ASelector_IsResolvedInsideTheNamedDatabase()
    {
        var db = SeedDatabase("run-bug61-bbbb", "edge");
        var emptyRoot = Path.Combine(_tmp, "empty-root");
        Directory.CreateDirectory(emptyRoot);

        var byId = RunSources.Resolve(emptyRoot, RunHistoryFilter.All, "run-bug61-b", _tmp, envOverride: db);
        var byPlan = RunSources.Resolve(emptyRoot, RunHistoryFilter.All, "edge", _tmp, envOverride: db);
        var miss = RunSources.Resolve(emptyRoot, RunHistoryFilter.All, "nothing-like-this", _tmp, envOverride: db);

        Assert.Single(byId!);
        Assert.Single(byPlan!);
        Assert.Null(miss);
    }

    [Fact]
    public void AVariableNamingAMissingFile_IsIgnoredWithAWarning_NotObeyed() =>
        Assert.Null(RunSources.EnvDatabase(Path.Combine(_tmp, "does-not-exist", "run.db")));

    [Fact]
    public void AnUnsetVariable_IsNull() => Assert.Null(RunSources.EnvDatabase(""));
}

public sealed class Bug52_AFailedClaimAttemptIsNotAClaim : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bug52-" + Guid.NewGuid().ToString("N")[..8]);

    public Bug52_AFailedClaimAttemptIsNotAClaim() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private const string Bare = "{\"utc\":\"2026-08-18T22:00:00Z\",\"tool\":\"Bash\",\"id\":\"a\",\"f\":{\"command\":\"conductor task --done R1.1\"}}\n";
    private const string Mcp = "{\"utc\":\"2026-08-18T22:00:01Z\",\"tool\":\"mcp__conductor-tasks__task_update\",\"id\":\"b\",\"f\":{\"taskId\":\"R1.1\",\"status\":\"done\"}}\n";
    private const string Read = "{\"utc\":\"2026-08-18T22:00:02Z\",\"tool\":\"Read\",\"id\":\"c\",\"f\":{\"path\":\"x.txt\"}}\n";
    private const string OutcomeB = "{\"utc\":\"2026-08-18T22:00:03Z\",\"id\":\"b\",\"ms\":12}\n";
    private const string OutcomeC = "{\"utc\":\"2026-08-18T22:00:04Z\",\"id\":\"c\",\"ms\":3}\n";

    private SessionDigest Digest(string content)
    {
        var path = HookToolLog.PathFor(_tmp, 2);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return HookToolLog.BuildDigest(path, _tmp)!;
    }

    /// <summary>The KS7.2 rig, exactly: a bare <c>conductor task --done</c> that exited 127 (no
    /// outcome line ever arrived) and the MCP fallback that landed. One claim, and it is the one that
    /// came back — not because the dedupe happened to collapse two into one.</summary>
    [Fact]
    public void TheRigCase_CountsTheClaimThatLanded_Once()
    {
        var digest = Digest(Bare + Mcp + Read + OutcomeB + OutcomeC);

        Assert.Equal(["R1.1 -> done"], digest.Claims);
        Assert.Equal(1, digest.FailedCalls);
        Assert.Equal(3, digest.ToolCalls);
    }

    /// <summary>A session whose ONLY claim attempt failed used to show a claim it never made.</summary>
    [Fact]
    public void AnOnlyAttemptThatFailed_IsNoClaim()
    {
        var digest = Digest(Bare + Read + OutcomeC);

        Assert.Empty(digest.Claims);
        Assert.Equal(1, digest.FailedCalls);
    }

    /// <summary>No outcome line in the whole file means the outcome channel never spoke — a killed
    /// session, or a hook that fired PreToolUse only. Then the counter keeps attempt semantics rather
    /// than declaring a session that claimed nothing.</summary>
    [Fact]
    public void WithNoOutcomeChannelAtAll_AttemptsStillCount()
    {
        var digest = Digest(Bare + Read);

        Assert.Equal(["R1.1 -> done"], digest.Claims);
    }
}
