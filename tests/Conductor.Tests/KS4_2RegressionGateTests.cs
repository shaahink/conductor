using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS4.2 — the regression gate class, PASS-TO-PASS. The property under test throughout is the one an
/// exit code cannot express: a gate that <b>exits 0</b> and is nevertheless red, because a check that
/// used to pass is not being reported as passing any more.
/// </summary>
/// <remarks>
/// <para>The gate commands here read their check names out of a file in the temp repo, so a "deleted
/// test" is one line removed between two batteries. That is the smallest thing that reproduces the
/// move this class exists to catch, and it keeps these tests to a second each — the live proof that a
/// whole session cannot game its way past it is <see cref="KS4_2RegressionHarnessTests"/>.</para>
/// </remarks>
public sealed class KS4_2RegressionGateTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteRunStore _store;
    private const string RunId = "ks42run";

    public KS4_2RegressionGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ks42-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.InitializeRun(RunId, "ks42", _dir, null, EngineStamp.Parse("test"));
    }

    public void Dispose()
    {
        _store.Dispose();
        TestTemp.DeleteTree(_dir);
    }

    // ── reading the pass set ──

    [Fact]
    public async Task TheLinesFormatReadsEveryNonBlankLineAsACheckName()
    {
        var set = await PassSetExtractor.ExtractAsync(
            new PassSetConfig { Format = PassSetConfig.Lines }, "alpha\r\n\r\n  beta  \ngamma\n", _dir);

        Assert.Equal(["alpha", "beta", "gamma"], set);
    }

    [Fact]
    public async Task TheGoTestFormatReadsPassLinesIncludingSubtests()
    {
        const string output = """
            === RUN   TestPaneWraps
            --- PASS: TestPaneWraps (0.00s)
                --- PASS: TestPaneWraps/narrow (0.00s)
                --- FAIL: TestPaneWraps/wide (0.01s)
            --- SKIP: TestSlow (0.00s)
            ok      conductor/internal/tui  0.312s
            """;

        var set = await PassSetExtractor.ExtractAsync(new PassSetConfig { Format = PassSetConfig.GoTest }, output, _dir);

        // The failing and skipped ones are absent by construction: this reads what PASSED, never
        // "everything minus the failures" — a check that is no longer run prints neither.
        Assert.Equal(["TestPaneWraps", "TestPaneWraps/narrow"], set);
    }

    /// <summary>The trx reader, against trx that this repo's own suite actually emitted. The two
    /// <c>Passed</c> elements below are verbatim from <c>dotnet test --logger trx</c> on
    /// <c>Conductor.Tests</c> (16 tests of KS4_1HoldoutGatesTests, 2026-08-19) — attribute order,
    /// self-closing form and all. The <c>Failed</c> one is the same element with its outcome changed,
    /// which is exactly what VSTest writes but for the child error nodes it also nests inside.</summary>
    [Fact]
    public async Task TheTrxFormatReadsOnlyThePassedResultsOfARealTrx()
    {
        var path = Path.Combine(_dir, "real.trx");
        await File.WriteAllTextAsync(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="8a1f" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult executionId="4a6fa398-03e3-421a-b88c-49a4676b229e" testId="f111a9bb-7252-9958-8fe2-8d8f24feec5a" testName="Conductor.Tests.KS4_1HoldoutGatesTests.AnUnknownVisibilityIsRefusedByName" computerName="MSI" duration="00:00:00.0004988" startTime="2026-08-19T14:59:41.5452908+01:00" endTime="2026-08-19T14:59:41.5452910+01:00" testType="13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b" outcome="Passed" testListId="8c84fa94-04c1-424b-9868-57a2d4851a1d" relativeResultsDirectory="4a6fa398-03e3-421a-b88c-49a4676b229e" />
                <UnitTestResult executionId="bc683d1b-d7bc-41fa-b938-937f6c532a0d" testId="3fe81409-2fb5-eda0-1b79-739cde98cfe7" testName="Conductor.Tests.KS4_1HoldoutGatesTests.TheDoctorGateLintNamesNoHoldoutAndQuotesNoneOfItsConfiguration" computerName="MSI" duration="00:00:00.0316059" startTime="2026-08-19T14:59:40.1825311+01:00" endTime="2026-08-19T14:59:40.1825313+01:00" testType="13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b" outcome="Passed" testListId="8c84fa94-04c1-424b-9868-57a2d4851a1d" relativeResultsDirectory="bc683d1b-d7bc-41fa-b938-937f6c532a0d" />
                <UnitTestResult executionId="3c290bd4-06d5-4a3b-97b5-bc5622d88549" testId="2bcef9f2-2520-2d5c-b620-85a1ceab8a12" testName="Conductor.Tests.KS4_1HoldoutGatesTests.AVisibleGateMayNotWearTheRedactedName" computerName="MSI" duration="00:00:00.0019147" startTime="2026-08-19T14:59:38.4090644+01:00" endTime="2026-08-19T14:59:38.4090646+01:00" testType="13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b" outcome="Failed" testListId="8c84fa94-04c1-424b-9868-57a2d4851a1d" relativeResultsDirectory="3c290bd4-06d5-4a3b-97b5-bc5622d88549">
                  <Output><ErrorInfo><Message>Assert.True() Failure</Message></ErrorInfo></Output>
                </UnitTestResult>
              </Results>
            </TestRun>
            """);

        var set = await PassSetExtractor.ExtractAsync(
            new PassSetConfig { Format = PassSetConfig.Trx, Path = "real.trx" }, "", _dir);

        Assert.Equal(
        [
            "Conductor.Tests.KS4_1HoldoutGatesTests.AnUnknownVisibilityIsRefusedByName",
            "Conductor.Tests.KS4_1HoldoutGatesTests.TheDoctorGateLintNamesNoHoldoutAndQuotesNoneOfItsConfiguration",
        ], set);
    }

    /// <summary><c>dotnet test</c> names its trx after the machine and the clock unless the plan pins
    /// LogFileName, so the declared path may glob — and it must resolve to the run that just happened,
    /// not to whichever file the filesystem lists first.</summary>
    [Fact]
    public async Task ATrxPathMayGlobAndResolvesToTheNewestMatch()
    {
        var results = Path.Combine(_dir, "TestResults");
        Directory.CreateDirectory(results);
        await File.WriteAllTextAsync(Path.Combine(results, "MSI_2026-08-01.trx"), Trx("old.check"));
        var newer = Path.Combine(results, "MSI_2026-08-19.trx");
        await File.WriteAllTextAsync(newer, Trx("new.check"));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(5));

        var set = await PassSetExtractor.ExtractAsync(
            new PassSetConfig { Format = PassSetConfig.Trx, Path = "TestResults/*.trx" }, "", _dir);

        Assert.Equal(["new.check"], set);
    }

    [Fact]
    public void LostChecksIsASetDifferenceSoAdditionsAreNeverLosses()
    {
        // Deleted one, added two: still a regression, and it names the one thing that went.
        Assert.Equal(["b"], GateRunner.LostChecks(["a", "b", "c"], ["a", "c", "d", "e"]));
        Assert.Empty(GateRunner.LostChecks(["a", "b"], ["b", "a", "c"]));
        Assert.Empty(GateRunner.LostChecks([], ["a"]));
        Assert.Equal(["a", "b"], GateRunner.LostChecks(["b", "a"], []));
    }

    // ── the class, through the real battery ──

    /// <summary>THE checkpoint. Battery one records what passes; the check file loses a line — the
    /// deleted test, in miniature — and battery two's gate still exits 0 and is red anyway, naming
    /// what it lost. Note what is NOT asserted: nothing about exit codes. The gate passed both times.</summary>
    [Fact]
    public async Task AGateThatExitsZeroIsRedWhenACheckThatUsedToPassHasGone()
    {
        var plan = PlanWithRegressionGate();
        WriteChecks("suite.alpha", "suite.beta", "suite.gamma");

        var first = await RunBatteryAsync(plan);
        Assert.True(first.IsGreen);
        Assert.Empty(first.Regressions);
        Assert.Equal(["suite.alpha", "suite.beta", "suite.gamma"], first.PassSet);

        WriteChecks("suite.alpha", "suite.gamma");     // beta is deleted, and the suite is "green"
        var second = await RunBatteryAsync(plan);

        Assert.True(second.Passed);                    // the gate itself is perfectly happy
        Assert.Equal(0, second.ExitCode);
        Assert.False(second.IsGreen);                  // …and the battery is not
        Assert.Equal(["suite.beta"], second.Regressions);
        Assert.Equal(GateClass.Glyph, second.Glyph);
        Assert.Equal("gates RED", GateRunner.Token([second]));
    }

    /// <summary>The anti-laundering property, and the reason the baseline is not simply overwritten
    /// every battery. If it were, deleting a check would cost exactly one red session and then be
    /// invisible forever — the cheapest possible way to launder a deletion through this engine.</summary>
    [Fact]
    public async Task ARegressingBatteryDoesNotAdvanceTheBaselineSoTheNextOneAsksAgain()
    {
        var plan = PlanWithRegressionGate();
        WriteChecks("suite.alpha", "suite.beta");
        Assert.True((await RunBatteryAsync(plan)).IsGreen);

        WriteChecks("suite.alpha");
        Assert.Equal(["suite.beta"], (await RunBatteryAsync(plan)).Regressions);
        Assert.Equal(["suite.beta"], (await RunBatteryAsync(plan)).Regressions);
        Assert.Equal(["suite.beta"], (await RunBatteryAsync(plan)).Regressions);

        // …and the record still remembers what the suite used to prove.
        Assert.Equal(["suite.alpha", "suite.beta"], _store.GetGatePassSet(RunId, "suite"));
    }

    [Fact]
    public async Task RestoringTheCheckClearsTheRegressionAndAdvancesTheBaseline()
    {
        var plan = PlanWithRegressionGate();
        WriteChecks("suite.alpha", "suite.beta");
        await RunBatteryAsync(plan);
        WriteChecks("suite.alpha");
        await RunBatteryAsync(plan);

        WriteChecks("suite.alpha", "suite.beta", "suite.delta");
        var restored = await RunBatteryAsync(plan);

        Assert.True(restored.IsGreen);
        Assert.Empty(restored.Regressions);
        Assert.Equal(["suite.alpha", "suite.beta", "suite.delta"], _store.GetGatePassSet(RunId, "suite"));
    }

    /// <summary>Fail closed. "The results file is not where the plan says" and "the suite ran and
    /// everything passed" are the same exit code, and the first is the cheapest way there is to turn
    /// this class off from inside the repo.</summary>
    [Fact]
    public async Task APassingRegressionGateThatReportsNoChecksAtAllIsRed()
    {
        var plan = PlanWithRegressionGate();
        WriteChecks("suite.alpha");
        await RunBatteryAsync(plan);

        WriteChecks();                                  // the file is there; it says nothing
        var empty = await RunBatteryAsync(plan);

        Assert.True(empty.Passed);
        Assert.False(empty.IsGreen);
        Assert.NotNull(empty.RegressionNote);
        Assert.True(empty.HasRegressions);
        Assert.Equal(["suite.alpha"], _store.GetGatePassSet(RunId, "suite"));   // baseline untouched
    }

    /// <summary>A regression gate is never served from the result cache. The cache key is built from
    /// HEAD, and a session's work is uncommitted for most of its length — so a cached pass here would
    /// mean nobody looked at the tree where the check went missing.</summary>
    [Fact]
    public async Task ARegressionGateIsNeverServedFromTheResultCache()
    {
        var plan = PlanWithRegressionGate();
        WriteChecks("suite.alpha", "suite.beta");
        await RunBatteryAsync(plan);

        // File a passing result under the exact key the cache would look up.
        var gate = plan.Gates[0];
        var head = "0123456789abcdef0123456789abcdef01234567";
        _store.RecordGate(RunId, 1, "S1", gate.Name, gate.Tier, "session",
            GateRunner.CacheKey(plan, gate, head), passed: true, skipped: false, optional: false,
            exitCode: 0, durationMs: 10, tail: "");

        WriteChecks("suite.alpha");
        var served = await RunBatteryAsync(plan, head);

        Assert.False(served.Cached);
        Assert.Equal(["suite.beta"], served.Regressions);
    }

    /// <summary>An optional gate's contract is unchanged: it reports and it never blocks. That is
    /// also the only honest way to land a bulk rename, which from here is indistinguishable from a
    /// bulk deletion.</summary>
    [Fact]
    public async Task AnOptionalRegressionGateReportsAndNeverBlocks()
    {
        var plan = PlanWithRegressionGate();
        plan.Gates[0].Optional = true;
        WriteChecks("suite.alpha", "suite.beta");
        await RunBatteryAsync(plan);

        WriteChecks("suite.alpha");
        var optional = await RunBatteryAsync(plan);

        Assert.Equal(["suite.beta"], optional.Regressions);
        Assert.True(optional.IsGreen);
        Assert.Equal(GateClass.Glyph + "-warn", optional.Glyph);
    }

    /// <summary>The fix brief a regression writes is a different brief: there is no failing assertion
    /// to read, so it has to say what the class found and name the checks.</summary>
    [Fact]
    public async Task TheFixBriefNamesTheClassAndTheCheckThatWentMissing()
    {
        var plan = PlanWithRegressionGate();
        WriteChecks("suite.alpha", "suite.beta");
        await RunBatteryAsync(plan);
        WriteChecks("suite.alpha");

        var details = GateRunner.FailureDetails([await RunBatteryAsync(plan)]);

        Assert.Contains(GateClass.Glyph, details, StringComparison.Ordinal);
        Assert.Contains("PASS-TO-PASS", details, StringComparison.Ordinal);
        Assert.Contains("suite.beta", details, StringComparison.Ordinal);
        Assert.Contains("EXITED 0", details, StringComparison.Ordinal);
    }

    // ── the verdict reads it as its own row, and says the word ──

    [Fact]
    public void ARegressionRowKeepsASessionOutOfDeliveryAndTheVerdictNamesTheClass()
    {
        var delivering = new SessionEvidence
        {
            Kind = SessionKind.Deliver,
            GatesRun = true,
            WorkEvidenceRead = true,
            GatesGreen = true,
            WorkCommitCount = 3,
            NewlyDoneCount = 1,
        };
        Assert.Equal(VerdictDisposition.Deliver, SessionVerdict.Decide(delivering).Disposition);

        // The same session, with the class's finding attached. Note GatesGreen is left TRUE: the
        // runner already turns it false, and this asserts the verdict would refuse the delivery even
        // if some later change let a regressing battery report itself green.
        var d = SessionVerdict.Decide(delivering with
        {
            Regressions = [new RegressionEvidence("tests", ["Suite.ThePartThatMattered"], null)],
        });

        Assert.Equal(VerdictDisposition.QueueFix, d.Disposition);
        Assert.Equal(SessionOutcome.GatesRed, d.Outcome);
        Assert.Equal(AttemptEffect.Increment, d.Attempts);
        Assert.Contains("regression class (PASS-TO-PASS)", d.Reason, StringComparison.Ordinal);
        Assert.Contains("Suite.ThePartThatMattered", d.Reason, StringComparison.Ordinal);
        Assert.Contains("tests", d.Reason, StringComparison.Ordinal);
    }

    // ── refused at plan load, not half-supported ──

    [Theory]
    [InlineData("regresion", null, null, false, "has class 'regresion'")]
    [InlineData("regression", null, null, false, "must say how to read the set of checks that passed")]
    [InlineData("regression", "junit", null, false, "must say how to read the set of checks that passed")]
    [InlineData("regression", "trx", null, false, "declares no passSet.path")]
    [InlineData("regression", "lines", null, true, "is both 'holdout' and 'regression'")]
    public void APlanThatAsksForTheClassWithoutTheMeansOfReadingItIsRefusedByName(
        string cls, string? format, string? path, bool holdout, string expected)
    {
        var gate = new GateConfig
        {
            Name = "tests",
            Command = "echo hi",
            Class = cls,
            Visibility = holdout ? GateVisibility.Holdout : GateVisibility.Visible,
            PassSet = format is null ? null : new PassSetConfig { Format = format, Path = path },
        };

        var errors = GateRules.CollectErrors([gate]).ToList();

        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AWellFormedRegressionGateIsAccepted()
        => Assert.Empty(GateRules.CollectErrors(
            [new GateConfig { Name = "tests", Command = "echo hi", Class = GateClass.Regression, PassSet = new PassSetConfig { Format = PassSetConfig.Lines } }]));

    // ── the rig ──

    /// <summary>A gate whose checks are the lines of a file: deleting a line IS deleting a test, and
    /// the gate exits 0 either way.</summary>
    private PlanConfig PlanWithRegressionGate() => new()
    {
        Name = "ks42",
        Repo = _dir,
        Gates =
        {
            new GateConfig
            {
                Name = "suite",
                Command = "if (Test-Path checks.txt) { Get-Content checks.txt; exit 0 } else { exit 1 }",
                TimeoutMinutes = 1,
                Class = GateClass.Regression,
                PassSet = new PassSetConfig { Format = PassSetConfig.Lines },
            },
        },
    };

    private async Task<GateResult> RunBatteryAsync(PlanConfig plan, string? head = null)
    {
        var results = await GateRunner.RunAllAsync(plan, null, CancellationToken.None,
            db: _store, runId: RunId, headSha: head ?? "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
        return Assert.Single(results);
    }

    private void WriteChecks(params string[] names)
        => File.WriteAllText(Path.Combine(_dir, "checks.txt"), string.Join("\n", names));

    private static string Trx(string testName) =>
        $"""
         <TestRun><Results>
           <UnitTestResult testName="{testName}" outcome="Passed" />
         </Results></TestRun>
         """;
}
