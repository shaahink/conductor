using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Commands;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV2.2, cluster A — prompt composition. The defect these pin was silent and expensive: the whole
/// battery section was joined and then cut at <c>batteries.maxBytes</c>, so a knowledge ledger that
/// grew over a long run deleted the open-bugs battery behind it. Measured on a 45-session run:
/// <c>### open bugs</c> was in prompts 026 and 032 and gone from 038 onward, with eleven bugs open
/// the whole time and no line anywhere saying a section had been dropped.
///
/// <para>Bug #62 is the group-level fix (per-battery shares + a notice); bug #63 is the second,
/// independent truncation found while triaging it — <see cref="BugsBattery"/>'s own row cap, which
/// hid 16 of this repo's 28 open bugs from every prompt without saying so.</para>
/// </summary>
public sealed class DV2_2PromptCompositionTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteRunStore _db;
    private const string RunId = "run-dv2";

    /// <summary>The value this plan actually ships in its <c>batteries</c> block, so these run
    /// against the real budget rather than a number chosen to make them pass.</summary>
    private const int PlanMaxBytes = 6144;

    public DV2_2PromptCompositionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"conductor-dv22-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _db = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _db.InitializeRun(RunId, "toy", @"C:\repo", "feat/toy", EngineStamp.Parse("test"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { TestTemp.DeleteTree(_dir); } catch { }
    }

    // ------------------------------------------------------------------ #62, the headline

    [Fact]
    public void AGrownKnowledgeLedgerCanNoLongerStarveTheOpenBugsBattery()
    {
        GrowTheLedger(40);
        var ids = FileBugs("the courier drops a photo caption", "getUpdates 409 is unhandled", "budget restarts at zero");

        var ledger = new LedgerBattery(_db, RunId, maxEntries: 40);
        var bugs = new BugsBattery(_db, RunId);

        // The premise, asserted so this test can never pass vacuously: the ledger ALONE overruns the
        // budget. Under the old concatenation cap the bugs battery behind it could not have survived.
        Assert.True(ledger.Section.Length > PlanMaxBytes,
            $"ledger is only {ledger.Section.Length} chars — grow it or this test proves nothing");

        var rendered = new BatteryGroup([ledger, bugs], PlanMaxBytes).Render();

        Assert.Contains("### open bugs", rendered, StringComparison.Ordinal);
        foreach (var id in ids)
            Assert.Contains($"#{id}", rendered, StringComparison.Ordinal);
        Assert.Contains("getUpdates 409 is unhandled", rendered, StringComparison.Ordinal);
        Assert.True(rendered.Length <= PlanMaxBytes, $"rendered {rendered.Length} > budget {PlanMaxBytes}");
    }

    [Fact]
    public void TheBatteryThatLostRoomIsNamedInTheRenderedText_NotCutInSilence()
    {
        GrowTheLedger(40);
        FileBugs("a bug that must still be visible");

        var rendered = new BatteryGroup(
            [new LedgerBattery(_db, RunId, maxEntries: 40), new BugsBattery(_db, RunId)], PlanMaxBytes).Render();

        Assert.Contains("batteries.maxBytes", rendered, StringComparison.Ordinal);
        Assert.Contains("trimmed: knowledge ledger", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ABatteryWithNoRoomLeftIsDroppedByName_NotJustAbsent()
    {
        // Nine batteries into a budget that can seat one: the share left for each of the eight big
        // ones is under a line of prose, which is worth nothing as context, so they are dropped and
        // named rather than rendered as a heading over an ellipsis. The small one still fits its
        // share exactly, which is the half of this that matters — a squeeze does not become a wipe.
        var batteries = new List<IPromptBattery> { new FakeBattery("tiny", "one short line that survives") };
        for (var i = 1; i <= 8; i++) batteries.Add(new FakeBattery($"b{i}", new string('h', 4000)));

        var rendered = new BatteryGroup(batteries, 880).Render();

        Assert.Contains("DROPPED ENTIRELY", rendered, StringComparison.Ordinal);
        Assert.Contains("b1", rendered, StringComparison.Ordinal);
        Assert.Contains("b8", rendered, StringComparison.Ordinal);
        Assert.Contains("one short line that survives", rendered, StringComparison.Ordinal);
        Assert.True(rendered.Length <= 880, $"rendered {rendered.Length} > budget 880");
    }

    [Fact]
    public void ASmallBatteryKeepsItsWholeSectionAndTheSurplusGoesToTheBigOne()
    {
        var big = new FakeBattery("huge", new string('h', 8000));
        var small = new FakeBattery("tiny", "one short line that must survive intact");

        var rendered = new BatteryGroup([big, small], 2048).Render();

        // Equal shares would give "tiny" ~900 chars and cap "huge" at ~900 too. Fair share instead
        // lets "tiny" take only what it needs and hands the rest to "huge", so both are as long as
        // the budget allows — the point being that neither can eat the other's allocation.
        Assert.Contains("one short line that must survive intact", rendered, StringComparison.Ordinal);
        Assert.Contains("### huge", rendered, StringComparison.Ordinal);
        Assert.True(rendered.Length <= 2048, $"rendered {rendered.Length} > budget 2048");
        Assert.True(rendered.Length > 1500, $"rendered only {rendered.Length} — the surplus was not redistributed");
    }

    [Fact]
    public void NothingIsTrimmedAndNoNoticeAppearsWhenEverythingFits()
    {
        var a = new FakeBattery("alpha", "short");
        var b = new FakeBattery("beta", "also short");

        var rendered = new BatteryGroup([a, b], 2048).Render();

        Assert.DoesNotContain("batteries.maxBytes", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("…", rendered, StringComparison.Ordinal);
        Assert.Contains("### alpha", rendered, StringComparison.Ordinal);
        Assert.Contains("### beta", rendered, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ #63, the second truncation

    [Fact]
    public void BugsBatterySaysHowManyOpenBugsItDidNotShow()
    {
        for (var i = 0; i < 40; i++)
            _db.WriteBug(RunId, $"open defect number {i}", null, "medium", "DV2", 1);

        var section = new BugsBattery(_db, RunId).Section;

        Assert.Contains("THIS LIST IS PARTIAL", section, StringComparison.Ordinal);
        Assert.Contains("28 more open bug(s) are not shown", section, StringComparison.Ordinal);
        Assert.Contains("conductor bug list", section, StringComparison.Ordinal);
    }

    [Fact]
    public void BugsBatterySaysNothingAboutHiddenRowsWhenTheWholeLedgerFits()
    {
        _db.WriteBug(RunId, "the only open defect", null, "medium", "DV2", 1);

        Assert.DoesNotContain("THIS LIST IS PARTIAL", new BugsBattery(_db, RunId).Section, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePartialNoticeSitsAboveTheRows_SoTrimmingThisSectionCannotRemoveIt()
    {
        for (var i = 0; i < 40; i++)
            _db.WriteBug(RunId, $"open defect number {i}", null, "medium", "DV2", 1);

        var section = new BugsBattery(_db, RunId).Section;
        var notice = section.IndexOf("THIS LIST IS PARTIAL", StringComparison.Ordinal);
        var firstRow = section.IndexOf("- #", StringComparison.Ordinal);

        Assert.True(notice >= 0 && firstRow > notice,
            $"the partial notice must precede the first row (notice at {notice}, first row at {firstRow})");
    }

    [Fact]
    public void TheHiddenCountSpansThisRunsRowsAndTheRowsCarriedFromEarlierRuns()
    {
        _db.InitializeRun("run-older", "toy", @"C:\repo", "feat/toy", EngineStamp.Parse("test"));
        for (var i = 0; i < 10; i++)
            _db.WriteBug("run-older", $"carried defect {i}", null, "medium", "KS4", 1);
        for (var i = 0; i < 8; i++)
            _db.WriteBug(RunId, $"this run's defect {i}", null, "medium", "DV2", 1);

        // 18 open in total, 12 slots: this run's 8 take priority, 4 carried rows fill the rest, 6 hide.
        Assert.Contains("6 more open bug(s) are not shown", new BugsBattery(_db, RunId).Section, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ #15, the wall at spawn

    /// <summary>Bug #15's real shape. Doctor and preflight have warned about this ceiling for two
    /// eras, and neither is consulted at spawn — so the engine composed an argv it had already been
    /// told was fatal, the shim truncated or refused it, the agent did nothing, and the run recorded
    /// a short successful session. The refusal has to live where the argv is finally assembled.</summary>
    [Fact]
    public void AnArgvOverTheCeilingIsRefusedAtSpawn_NotSpawnedAndScoredAsSuccess()
    {
        if (!OperatingSystem.IsWindows()) return;

        var shim = Path.Combine(_dir, "agent-shim.cmd");
        File.WriteAllText(shim, "@echo off\r\n");
        var cfg = new AgentConfig { Command = shim, Args = ["-p", "{prompt}"] };
        var prompt = new string('x', ArgvLimits.CmdExeCommandLine + 1000);

        var ex = Assert.Throws<PromptCompositionException>(() =>
            AgentSession.Start(cfg, _dir, prompt, "sid", null, Path.Combine(_dir, "raw.log")));

        Assert.Contains("refusing to spawn", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ArgvLimits.CmdExeCommandLine.ToString(Invariant), ex.Message, StringComparison.Ordinal);
        // PromptCompositionException specifically: RunLoop already parks a run on it (NeedsHuman with
        // the reason), which is the outcome a session that cannot start deserves.
        Assert.IsType<PromptCompositionException>(ex);
    }

    /// <summary>The other half of #15, and the reason the guard is not simply "8191 everywhere": which
    /// ceiling applies is a property of how somebody installed the agent CLI. The same argv that is
    /// fatal through an npm shim is comfortable through a real executable, and refusing both would
    /// break every plan that is fine.</summary>
    [Fact]
    public void TheCeilingFollowsHowTheAgentWasInstalled_ShimOrRealExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var shim = Path.Combine(_dir, "npm-installed.cmd");
        var exe = Path.Combine(_dir, "real-agent.exe");
        File.WriteAllText(shim, "@echo off\r\n");
        File.WriteAllText(exe, "not really an executable, but the extension is what decides");

        Assert.Equal(ArgvLimits.CmdExeCommandLine, ArgvLimits.CeilingFor(shim, _dir).Ceiling);
        Assert.Equal(ArgvLimits.CreateProcessCommandLine, ArgvLimits.CeilingFor(exe, _dir).Ceiling);
        Assert.Contains("shim", ArgvLimits.CeilingFor(shim, _dir).Why, StringComparison.Ordinal);
    }

    /// <summary>A command nobody can resolve gets the HIGHER ceiling. Guessing the shim's for a
    /// program we cannot find would refuse launches that are fine, which is a worse failure than the
    /// one being fixed.</summary>
    [Fact]
    public void AnUnresolvableCommandGetsTheGenerousCeiling_NotTheStrictOne()
        => Assert.Equal(ArgvLimits.CreateProcessCommandLine,
            ArgvLimits.CeilingFor("no-such-program-anywhere-on-this-machine", _dir).Ceiling);

    // ------------------------------------------------------------------ #55, the number doctor quoted

    /// <summary>Bug #55: doctor renders through <c>PromptBuilder</c> with no store, so the knowledge
    /// batteries contributed nothing to its measurement while contributing up to
    /// <c>batteries.maxBytes</c> at spawn — measured 350-500 chars light against the real thing. The
    /// cap is a true upper bound, so it is now counted and the warning says what it counted.</summary>
    [Fact]
    public void DoctorsArgvLintCountsTheBatteriesItCannotRender()
    {
        if (!OperatingSystem.IsWindows()) return;

        var shim = Path.Combine(_dir, "agent-shim.cmd");
        File.WriteAllText(shim, "@echo off\r\n");
        var plan = LintPlan(p =>
        {
            p.Agent = new AgentConfig { Command = shim, Args = ["-p", "{prompt}"] };
            // A plan that raises the battery budget on a shim-installed agent — which is exactly what
            // this repo's own plan does at 6144. The composed prompt clears 8191 comfortably on its
            // own; it is the room reserved for batteries doctor cannot render that takes it over.
            p.Batteries = new BatteriesConfig { MaxBytes = 8000 };
        });

        var check = DoctorCommand.CheckArgvLength(plan);

        Assert.Equal("warn", check.State);
        Assert.Contains("knowledge batteries", check.Message, StringComparison.Ordinal);
        Assert.Contains("live store at spawn", check.Message, StringComparison.Ordinal);
    }

    /// <summary>And it stays quiet when the remainder changes nothing: a lint that warned on every
    /// plan would be read as noise and the one that mattered would go with it.</summary>
    [Fact]
    public void DoctorsArgvLintIsSilentWhenTheBatteryCapChangesNothing()
    {
        var check = DoctorCommand.CheckArgvLength(LintPlan(p => p.Batteries = new BatteriesConfig { MaxBytes = 2048 }));

        Assert.Equal("ok", check.State);
        Assert.DoesNotContain("knowledge batteries", check.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    private PlanConfig LintPlan(Action<PlanConfig> tweak)
    {
        var repo = Path.Combine(_dir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"),
            "# t\n\n## Handoff\n\nnothing pending.\n\n## Checkpoints\n\n"
          + "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
          + "| S1.1 | the only row | TODO | - | - |\n");

        var plan = new PlanConfig
        {
            Name = "dv22-fixture",
            Repo = repo,
            Tracker = "TRACKER.md",
            PlanFilePath = Path.Combine(repo, "fixture.plan.json"),
            Agent = new AgentConfig { Command = "git", Args = ["-p", "{prompt}"] },
        };
        plan.Stages.Add(new StageConfig { Id = "S1", Title = "the only stage", Sessions = 1 });
        tweak(plan);
        File.WriteAllText(plan.PlanFilePath, "{}");
        return plan;
    }

    private void GrowTheLedger(int entries)
    {
        for (var i = 0; i < entries; i++)
            _db.WriteLedger(RunId, 1, "DV2", "finding", $"note {i}: " + new string('x', 300));
    }

    private List<long> FileBugs(params string[] titles)
    {
        var ids = new List<long>();
        foreach (var t in titles) ids.Add(_db.WriteBug(RunId, t, null, "high", "DV2", 1));
        return ids;
    }

    private sealed class FakeBattery(string name, string section) : IPromptBattery
    {
        public string Name => name;
        public string Section => section;
        public bool IsEmpty => section.Length == 0;
    }
}
