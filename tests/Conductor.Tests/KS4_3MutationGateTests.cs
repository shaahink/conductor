using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS4.3 — the mutation gate class. The property under test throughout is the one no exit code and no
/// coverage percentage can express: the suite RAN, everything in it passed, and it would still have
/// passed if the implementation were broken.
/// </summary>
/// <remarks>
/// <para>No Stryker process is started here. Running a real mutation pass takes minutes per file and
/// what these tests are about is the engine's half of the contract — the diff scoping, the
/// arithmetic, the fail-closed reading and the reporting — all of which take a report as input. The
/// reports below are the shape Stryker.NET 4.x actually writes; the real run that proves that claim
/// is recorded in this checkpoint's evidence file.</para>
/// </remarks>
public sealed class KS4_3MutationGateTests : IDisposable
{
    private readonly string _repo;

    public KS4_3MutationGateTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "ks43-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        Git.Exec(_repo, "init", "-b", "main");
        Git.Exec(_repo, "config", "user.email", "conductor@test.local");
        Git.Exec(_repo, "config", "user.name", "Conductor Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# ks43\n");
        Git.Exec(_repo, "add", "README.md");
        Git.Exec(_repo, "commit", "-m", "initial");
    }

    public void Dispose() => TestTemp.DeleteTree(_repo);

    // ── the arithmetic ──

    [Fact]
    public void TheScoreIsKilledPlusTimeoutOverEverythingThatCouldHaveBeenKilled()
    {
        var score = MutationReportReader.Read(Report(("src/A.cs", new[]
        {
            ("Killed", 1), ("Killed", 2), ("Timeout", 3), ("Survived", 4), ("Ignored", 5), ("CompileError", 6),
        })), null);

        Assert.NotNull(score);
        Assert.Equal(4, score!.Counted);                 // ignored and compile-error are not measurements
        Assert.Equal(75d, score.Percent);                // (2 killed + 1 timeout) / 4
        Assert.Equal(1, score.Survived);
    }

    /// <summary>The choice that makes this class un-gameable by not testing. A mutant nothing
    /// executed is counted in the denominator, so deleting the test that covered a file lowers the
    /// score rather than removing it from the sum.</summary>
    [Fact]
    public void AMutantNothingExecutedCountsAgainstTheScoreRatherThanVanishingFromIt()
    {
        var score = MutationReportReader.Read(Report(("src/A.cs", new[]
        {
            ("Killed", 1), ("NoCoverage", 2), ("NoCoverage", 3), ("NoCoverage", 4),
        })), null);

        Assert.Equal(25d, score!.Percent);
        Assert.Equal(3, score.NoCoverage);
        Assert.Equal(3, score.Survivors.Count);          // and every one of them is named for the fix brief
        Assert.All(score.Survivors, s => Assert.Equal("src/A.cs", s.File));
    }

    /// <summary>A status this reader has never heard of is not a kill. A later schema, or a
    /// half-finished run, must not be able to add mutants to the numerator by naming them something
    /// new.</summary>
    [Fact]
    public void AnUnknownStatusIsNeverCountedAsAKill()
    {
        var score = MutationReportReader.Read(Report(("src/A.cs", new[] { ("Killed", 1), ("Pending", 2) })), null);

        Assert.Equal(50d, score!.Percent);
        Assert.Contains(score.Survivors, s => s.Status == "Pending");
    }

    [Fact]
    public void TextThatIsNotAReportReadsAsNullRatherThanAsAPerfectScore()
    {
        Assert.Null(MutationReportReader.Read("", null));
        Assert.Null(MutationReportReader.Read("not json at all", null));
        Assert.Null(MutationReportReader.Read("""{"schemaVersion":"1"}""", null));   // no files element
    }

    // ── the diff scoping ──

    [Fact]
    public void OnlyTheFilesInScopeAreScored()
    {
        var json = Report(
            ("src/Changed.cs", new[] { ("Survived", 1), ("Survived", 2) }),
            ("src/Untouched.cs", new[] { ("Killed", 1), ("Killed", 2), ("Killed", 3), ("Killed", 4) }));

        var whole = MutationReportReader.Read(json, null);
        var scoped = MutationReportReader.Read(json, ["src/Changed.cs"]);

        // The whole-repository number says 66.67% and would clear a 60% bar. The number that is
        // actually about this session's work is zero. That gap IS the checkpoint.
        Assert.Equal(66.67d, whole!.Percent);
        Assert.Equal(0d, scoped!.Percent);
        Assert.Equal(["src/Changed.cs"], scoped.ScoredFiles);
    }

    /// <summary>Stryker has written both absolute and project-relative keys across versions, and the
    /// gate's working directory need not be the repo root — so the match is on trailing segments from
    /// whichever side is longer.</summary>
    [Theory]
    [InlineData(@"C:\code\conductor\src\Conductor.Core\GateRunner.cs", "src/Conductor.Core/GateRunner.cs")]
    [InlineData("src/Conductor.Core/GateRunner.cs", "src/Conductor.Core/GateRunner.cs")]
    [InlineData("GateRunner.cs", "src/Conductor.Core/GateRunner.cs")]
    [InlineData("./src/Conductor.Core/GateRunner.cs", "src/Conductor.Core/GateRunner.cs")]
    public void AReportKeyAndAChangedPathMatchOnTheirTrailingSegments(string reportKey, string changed)
        => Assert.True(MutationReportReader.SamePath(reportKey, changed.Replace('\\', '/')));

    [Fact]
    public void ASimilarlyNamedFileInAnotherDirectoryIsNotTheSameFile()
        => Assert.False(MutationReportReader.SamePath("src/other/GateRunner.cs", "src/core/GateRunner.cs"));

    [Fact]
    public void TheChangedSetSpansCommittedEditedAndBrandNewFiles()
    {
        Write("committed.cs", "class A;");
        Git.Exec(_repo, "add", "committed.cs");
        Git.Exec(_repo, "commit", "-m", "add committed.cs");
        Write("committed.cs", "class A { }");           // edited, not committed
        Write("untracked.cs", "class B;");              // never added — git has not been told it exists

        var changed = Git.ChangedFiles(_repo, "main~1");

        Assert.Contains("committed.cs", changed);
        Assert.Contains("untracked.cs", changed);
        Assert.DoesNotContain("README.md", changed);
    }

    /// <summary>A base rev that does not resolve returns EMPTY, never "everything". Returning the
    /// whole repository would silently promote a diff-scoped gate to a whole-repository one — which
    /// blows the budget rather than the verdict, and is therefore the harder failure to notice.</summary>
    [Fact]
    public void AnUnresolvableBaseRevYieldsNothingRatherThanEverything()
        => Assert.Empty(Git.ChangedFiles(_repo, "no-such-branch"));

    // ── the class, end to end through the battery ──

    /// <summary>The headline: the gate EXITS 0, the report says the changed file's mutants all
    /// survived, and the battery is red.</summary>
    [Fact]
    public async Task AGateThatExitsZeroIsRedWhenTheChangedFilesScoreBelowTheBar()
    {
        ChangeSource("src/Changed.cs");
        WriteReport(("src/Changed.cs", new[] { ("Killed", 1), ("Survived", 2), ("Survived", 3), ("Survived", 4) }));

        var r = await RunBatteryAsync(PlanWithMutationGate(threshold: 60));

        Assert.True(r.Passed);                        // the command really did succeed
        Assert.False(r.IsGreen);                      // and the battery is red anyway
        Assert.True(r.HasMutationShortfall);
        Assert.Equal(GateClass.MutationGlyph, r.Glyph);
        Assert.Equal(25d, r.Mutation!.Score);
        Assert.Equal(3, r.Mutation.Survivors.Count);
        Assert.False(GateRunner.AllRequiredPassed([r]));
    }

    [Fact]
    public async Task AGateThatClearsTheBarIsGreenAndStillRecordsItsScore()
    {
        ChangeSource("src/Changed.cs");
        WriteReport(("src/Changed.cs", new[] { ("Killed", 1), ("Killed", 2), ("Killed", 3), ("Survived", 4) }));

        var r = await RunBatteryAsync(PlanWithMutationGate(threshold: 60));

        Assert.True(r.IsGreen);
        Assert.False(r.HasMutationShortfall);
        Assert.Equal(75d, r.Mutation!.Score);          // the number survives a pass — the era-boundary run is made of these
    }

    /// <summary>The dilution the diff scoping exists to stop: a repository full of well-tested code
    /// and one new file nothing asserts on. Whole-report scoring calls this 90%.</summary>
    [Fact]
    public async Task TheRestOfTheRepositoryCannotCarryTheFileThisSessionChanged()
    {
        ChangeSource("src/Changed.cs");
        WriteReport(
            ("src/Changed.cs", new[] { ("Survived", 1), ("Survived", 2) }),
            ("src/Untouched.cs", Enumerable.Range(1, 18).Select(i => ("Killed", i)).ToArray()));

        var r = await RunBatteryAsync(PlanWithMutationGate(threshold: 60));

        Assert.False(r.IsGreen);
        Assert.Equal(0d, r.Mutation!.Score);
    }

    /// <summary>Fail closed. Every cheap way of switching this class off from inside the repo — a
    /// stale report, a mis-pointed path, a mutate glob narrowed until the changed file falls outside
    /// it — arrives here as "the report scores none of what changed", and none of them is
    /// distinguishable from a perfect score by exit code.</summary>
    [Fact]
    public async Task AReportThatScoresNoneOfTheChangedFilesIsRedNotPerfect()
    {
        ChangeSource("src/Changed.cs");
        WriteReport(("src/SomethingElse.cs", new[] { ("Killed", 1), ("Killed", 2) }));

        var r = await RunBatteryAsync(PlanWithMutationGate(threshold: 60));

        Assert.True(r.Passed);
        Assert.False(r.IsGreen);
        Assert.Null(r.Mutation!.Score);
        Assert.Contains("treated as red", r.Mutation.Note);
    }

    [Fact]
    public async Task AMissingReportIsRedForTheSameReason()
    {
        ChangeSource("src/Changed.cs");                 // and no report written at all

        var r = await RunBatteryAsync(PlanWithMutationGate(threshold: 60));

        Assert.True(r.Passed);
        Assert.False(r.IsGreen);
        Assert.NotNull(r.Mutation!.Note);
    }

    /// <summary>The other half of failing closed: a checkpoint that changed no mutable source has no
    /// mutation score to clear, and saying so is not the same as passing one. Pretending otherwise
    /// would put a red on every docs stage and teach every reader to ignore the class.</summary>
    [Fact]
    public async Task ABranchThatChangedNoMutableSourceIsGreenWithNoFindingAtAll()
    {
        Write("NOTES.md", "a docs-only change\n");
        WriteReport(("src/Untouched.cs", new[] { ("Survived", 1) }));

        var r = await RunBatteryAsync(PlanWithMutationGate(threshold: 90));

        Assert.True(r.IsGreen);
        Assert.Null(r.Mutation);
    }

    /// <summary>An optional mutation gate keeps the contract every optional gate has — it reports and
    /// never blocks — which is also the only honest way to declare a threshold you are still
    /// calibrating.</summary>
    [Fact]
    public async Task AnOptionalMutationGateReportsTheShortfallAndDoesNotBlock()
    {
        ChangeSource("src/Changed.cs");
        WriteReport(("src/Changed.cs", new[] { ("Survived", 1) }));
        var plan = PlanWithMutationGate(threshold: 60);
        plan.Gates[0].Optional = true;

        var r = await RunBatteryAsync(plan);

        Assert.True(r.IsGreen);
        Assert.True(r.HasMutationShortfall);
        Assert.Equal(GateClass.MutationGlyph + "-warn", r.Glyph);
    }

    /// <summary>A gate whose command FAILED is not scored. A Stryker run that fell over wrote no
    /// report, and reading "0% of nothing" out of that would report an unkilled-mutant problem where
    /// the real one is a broken runner.</summary>
    [Fact]
    public async Task AFailingGateIsReportedAsAFailingGateAndNotAsAMutationShortfall()
    {
        ChangeSource("src/Changed.cs");
        var plan = PlanWithMutationGate(threshold: 60);
        plan.Gates[0].Command = "exit 3";

        var r = await RunBatteryAsync(plan);

        Assert.False(r.Passed);
        Assert.Null(r.Mutation);
        Assert.Equal("FAIL-retry", r.Glyph);
    }

    // ── the reporting, at every surface a reader meets ──

    /// <summary>The KS4.2 lesson, applied before it could be paid for twice: this engine has TWO
    /// fix-brief renderers and a unit test can be green against one while a real fix session is
    /// handed "(no gate output captured)" by the other. Both are asserted here.</summary>
    [Fact]
    public async Task BothFixBriefRenderersCarryTheShortfallAndNameTheSurvivingMutants()
    {
        ChangeSource("src/Changed.cs");
        WriteReport(("src/Changed.cs", new[] { ("Killed", 7), ("Survived", 42) }));
        var r = await RunBatteryAsync(PlanWithMutationGate(threshold: 90));

        var details = GateRunner.FailureDetails([r]);
        var spill = GateFailureSpill.Render([r], Path.Combine(_repo, ".state"), 3);

        foreach (var rendered in new[] { details, spill })
        {
            Assert.Contains(GateClass.MutationGlyph, rendered, StringComparison.Ordinal);
            Assert.Contains("EXITED 0", rendered, StringComparison.Ordinal);
            Assert.Contains("src/Changed.cs:42", rendered, StringComparison.Ordinal);
            Assert.Contains("90", rendered, StringComparison.Ordinal);
        }
    }

    /// <summary>And the word is not FAIL. A reader told a gate FAILED goes looking for a failing
    /// assertion, finds a green suite, and concludes the engine is broken.</summary>
    [Fact]
    public async Task TheGlyphIsItsOwnWordAndNotTheRegressionOne()
    {
        ChangeSource("src/Changed.cs");
        WriteReport(("src/Changed.cs", new[] { ("Survived", 1) }));
        var r = await RunBatteryAsync(PlanWithMutationGate(threshold: 50));

        Assert.Equal("MUTANTS", r.Glyph);
        Assert.NotEqual(GateClass.Glyph, r.Glyph);
        Assert.False(r.HasRegressions);
        Assert.True(r.HasClassFailure);
    }

    /// <summary>The verdict says it in the class's own words, because "a gate failed" is wrong twice
    /// over here: the gate exited 0, and what needs fixing is the tests rather than the code.</summary>
    [Fact]
    public void TheVerdictNamesTheMutationClassRatherThanSayingAGateFailed()
    {
        var decision = SessionVerdict.Decide(new SessionEvidence
        {
            GatesRun = true,
            WorkEvidenceRead = true,
            GatesGreen = false,
            MutationShortfalls = [new MutationEvidence("mutation", 25d, 60d, 4, ["src/A.cs:9 — Arithmetic"], null)],
            WorkCommitCount = 1,
        });

        Assert.Equal(SessionOutcome.GatesRed, decision.Outcome);
        Assert.Contains("mutation class", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("src/A.cs:9", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>Both classes at once are two findings, not a ranking. A session that deleted a check
    /// AND left mutants alive has two things to fix, and choosing one for the fix brief loses the
    /// other.</summary>
    [Fact]
    public void AVerdictCarryingBothClassesReportsBoth()
    {
        var decision = SessionVerdict.Decide(new SessionEvidence
        {
            GatesRun = true,
            WorkEvidenceRead = true,
            GatesGreen = false,
            Regressions = [new RegressionEvidence("suite", ["TestOne"], null)],
            MutationShortfalls = [new MutationEvidence("mutation", 10d, 60d, 10, [], null)],
            WorkCommitCount = 1,
        });

        Assert.Contains("regression class", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("mutation class", decision.Reason, StringComparison.Ordinal);
    }

    // ── the plan refuses what it cannot enforce ──

    [Theory]
    [InlineData(null, "report.json", 60d, "mutation.format")]
    [InlineData("stryker-json", "", 60d, "no mutation.path")]
    [InlineData("stryker-json", "report.json", 0d, "above 0 and at most 100")]
    [InlineData("stryker-json", "report.json", 101d, "above 0 and at most 100")]
    public void APlanIsRefusedWhenTheMutationGateCannotBeEnforced(string? format, string path, double threshold, string expected)
    {
        var gate = new GateConfig
        {
            Name = "mutation",
            Command = "dotnet stryker",
            Class = GateClass.Mutation,
            Mutation = new MutationConfig { Format = format!, Path = path, Threshold = threshold },
        };

        var errors = GateRules.CollectErrors([gate]).ToList();

        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AWellFormedMutationGateIsAccepted()
        => Assert.Empty(GateRules.CollectErrors([MutationGate(60)]));

    /// <summary>Refused rather than half-supported, for the reason the regression/holdout pair is:
    /// this class's whole output is a list of files and line numbers, and that is exactly what a
    /// holdout may never say.</summary>
    [Fact]
    public void AMutationGateMayNotAlsoBeAHoldout()
    {
        var gate = MutationGate(60);
        gate.Visibility = GateVisibility.Holdout;

        Assert.Contains(GateRules.CollectErrors([gate]),
            e => e.Contains("exactly what a holdout may not do", StringComparison.Ordinal));
    }

    /// <summary>An unknown class must not project to "standard" in silence — a plan that asked for a
    /// mutation gate and got an exit-code gate has been downgraded without being told.</summary>
    [Fact]
    public void MutationIsAKnownClassAndAMisspellingIsNot()
    {
        Assert.True(GateClass.IsKnown("mutation"));
        Assert.False(GateClass.IsKnown("mutations"));
    }

    /// <summary>The report path may name the run directory with a wildcard, because Stryker stamps
    /// that directory with the clock — and the newest match is the run this battery just did.</summary>
    [Fact]
    public void AWildcardReportPathResolvesToTheNewestMatch()
    {
        var older = Path.Combine(_repo, "StrykerOutput", "2020-01-01", "reports");
        var newer = Path.Combine(_repo, "StrykerOutput", "2026-08-19", "reports");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(older, "mutation-report.json"), Report(("src/Old.cs", [("Killed", 1)])));
        File.WriteAllText(Path.Combine(newer, "mutation-report.json"), Report(("src/New.cs", [("Killed", 1)])));
        File.SetLastWriteTimeUtc(Path.Combine(older, "mutation-report.json"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var cfg = new MutationConfig { Path = "StrykerOutput/*/reports/mutation-report.json", Threshold = 60 };
        var located = MutationReportReader.Locate(cfg, _repo);

        Assert.NotNull(located);
        Assert.Contains("2026-08-19", located, StringComparison.Ordinal);
    }

    // ── fixtures ──

    private PlanConfig PlanWithMutationGate(double threshold) => new()
    {
        Name = "ks43",
        Repo = _repo,
        Gates = { MutationGate(threshold) },
    };

    private static GateConfig MutationGate(double threshold) => new()
    {
        Name = "mutation",
        Command = "exit 0",
        TimeoutMinutes = 1,
        Class = GateClass.Mutation,
        Mutation = new MutationConfig
        {
            Format = MutationConfig.StrykerJson,
            Path = "mutation-report.json",
            Threshold = threshold,
            DiffBase = "HEAD",
        },
    };

    private static async Task<GateResult> RunBatteryAsync(PlanConfig plan)
        => Assert.Single(await GateRunner.RunAllAsync(plan, null, CancellationToken.None));

    /// <summary>An uncommitted .cs file, which is what "this branch changed mutable source" is
    /// against the default <c>HEAD</c> base.</summary>
    private void ChangeSource(string relative)
        => Write(relative, "namespace Scratch; public static class Changed { public static int Add(int a, int b) => a + b; }");

    private void Write(string relative, string text)
    {
        var full = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private void WriteReport(params (string File, (string Status, int Line)[] Mutants)[] files)
        => File.WriteAllText(Path.Combine(_repo, "mutation-report.json"), Report(files));

    /// <summary>The shape Stryker.NET writes: a mutation-testing-elements report keyed by file, each
    /// with a mutants array carrying a status, a mutator name and a start line.</summary>
    private static string Report(params (string File, (string Status, int Line)[] Mutants)[] files)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("schemaVersion", "1");
            w.WriteStartObject("files");
            foreach (var (file, mutants) in files)
            {
                w.WriteStartObject(file);
                w.WriteString("language", "cs");
                w.WriteStartArray("mutants");
                var id = 0;
                foreach (var (status, line) in mutants)
                {
                    w.WriteStartObject();
                    w.WriteString("id", (id++).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteString("mutatorName", "Arithmetic operator");
                    w.WriteString("status", status);
                    w.WriteStartObject("location");
                    w.WriteStartObject("start");
                    w.WriteNumber("line", line);
                    w.WriteNumber("column", 1);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
