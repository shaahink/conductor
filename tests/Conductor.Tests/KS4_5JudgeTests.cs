using System.Text.RegularExpressions;

using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS4.5 — the advisory judge: its parser, its arithmetic of agreement, the plan block that turns it
/// on, and the rules that keep it evidence.
/// <para>The negative half of this file is the point of the checkpoint. A second model reviewing the
/// work is only safe if there is no path from its opinion to a verdict, and "we didn't wire one" is
/// not a guarantee — so the absence is asserted three ways: over the source of the decision path, over
/// the ORDER of the engine's calls, and (in KS4_5JudgeHarnessTests) live, with a judge that condemns a
/// green session and one that blesses a red one.</para>
/// </summary>
public sealed class KS4_5JudgeTests
{
    // ── the parser ──

    [Fact]
    public void AWellFormedReviewParses()
    {
        var review = Judge.Parse(
            """
            Here is my review.
            {"verdict":"concerns","score":62,"findings":["the new test asserts nothing","claim is wider than the diff"],"summary":"thin"}
            """);

        Assert.NotNull(review);
        Assert.Equal("concerns", review!.Verdict);
        Assert.Equal(62, review.Score);
        Assert.Equal(2, review.Findings.Count);
        Assert.Equal("thin", review.Summary);
    }

    /// <summary>The lesson Verifier.Parse learned the hard way and JsonScan now carries for both: a
    /// finding routinely quotes a brace — a placeholder, a code fragment, a JSON example — and a
    /// single-level regex loses the whole review to it.</summary>
    [Fact]
    public void BracesInsideAFindingDoNotBreakTheParse()
    {
        var review = Judge.Parse(
            """{"verdict":"fail","score":10,"findings":["the template still says {model} where {planDoc} belongs"],"summary":"broken prompt"}""");

        Assert.NotNull(review);
        Assert.Equal("fail", review!.Verdict);
        Assert.Contains("{model}", review.Findings[0], StringComparison.Ordinal);
    }

    /// <summary>A model that shows its working writes the example first and the answer last.</summary>
    [Fact]
    public void TheLastParseableObjectWins()
    {
        var review = Judge.Parse(
            """
            The format I will use is {"verdict":"pass","score":100,"findings":[],"summary":"example"}
            My actual review: {"verdict":"fail","score":5,"findings":[],"summary":"real"}
            """);

        Assert.Equal("fail", review!.Verdict);
        Assert.Equal("real", review.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I could not review this.")]
    [InlineData("""{"score":90,"findings":[]}""")]          // no verdict at all
    [InlineData("""{"verdict":42}""")]                       // verdict is not a word
    [InlineData("""{"verdict":"  "}""")]                     // ...or is only whitespace
    [InlineData("""{"verdict":"pass",""")]                   // truncated mid-object
    public void AnUnreadableReviewIsNullAndNeverADefaultOpinion(string output)
        => Assert.Null(Judge.Parse(output));

    /// <summary>A score outside 0-100 is dropped and the review survives: the WORDS are the review,
    /// the number is a convenience. Silently clamping it would invent a figure the model never gave.</summary>
    [Theory]
    [InlineData(101)]
    [InlineData(-4)]
    public void AnOutOfRangeScoreIsDroppedAndTheReviewIsKept(int score)
    {
        var review = Judge.Parse($$"""{"verdict":"fail","score":{{score}},"findings":[]}""");

        Assert.NotNull(review);
        Assert.Null(review!.Score);
        Assert.Equal("fail", review.Verdict);
    }

    // ── agreement ──

    [Theory]
    [InlineData("pass", true, JudgeAgreement.Agrees)]
    [InlineData("PASS", true, JudgeAgreement.Agrees)]
    [InlineData("approve", true, JudgeAgreement.Agrees)]
    [InlineData("pass", false, JudgeAgreement.Disagrees)]
    [InlineData("fail", true, JudgeAgreement.Disagrees)]
    [InlineData("reject", true, JudgeAgreement.Disagrees)]
    [InlineData("fail", false, JudgeAgreement.Agrees)]
    [InlineData("concerns", true, JudgeAgreement.Inconclusive)]
    [InlineData("concerns", false, JudgeAgreement.Inconclusive)]
    [InlineData("mostly fine I suppose", true, JudgeAgreement.Inconclusive)]
    public void AgreementIsMeasuredAgainstTheDeterministicSignal(string verdict, bool green, JudgeAgreement expected)
        => Assert.Equal(expected, new JudgeReview(verdict, null, [], null).Against(green));

    // ── the spawn ──

    [Fact]
    public async Task NoJudgeBlockMeansNoSpawnAndNothingSpent()
    {
        var reply = await Judge.ReviewAsync(new PlanConfig { Repo = Path.GetTempPath() }, "review this");

        Assert.Same(JudgeReply.None, reply);
    }

    [Fact]
    public async Task ADisabledJudgeIsNeverSpawned()
    {
        var plan = new PlanConfig { Repo = Path.GetTempPath(), Judge = new JudgeConfig { Command = "cmd.exe" } };

        Assert.False(plan.Judge!.Enabled);   // off unless the plan says otherwise
        Assert.Same(JudgeReply.None, await Judge.ReviewAsync(plan, "review this"));
    }

    /// <summary>SC3.4's lesson, applied before it can repeat: a CLI spawned with no arguments waits on
    /// stdin for its whole timeout and answers nothing. Say so instead of burning six minutes.</summary>
    [Fact]
    public async Task AnArglessJudgeIsRefusedOutLoudRatherThanSpawned()
    {
        var log = new List<string>();
        var plan = new PlanConfig
        {
            Repo = Path.GetTempPath(),
            Judge = new JudgeConfig { Enabled = true, Command = "cmd.exe", Args = [] },
        };

        var reply = await Judge.ReviewAsync(plan, "review this", log.Add);

        Assert.Null(reply.Review);
        Assert.Contains(log, l => l.Contains("judge.args is empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConfiguredJudgeIsSpawnedAndItsReviewComesBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ks45-judge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "fake-judge.cmd");
            await File.WriteAllTextAsync(script, string.Join("\r\n",
                "@echo off",
                "echo {\"verdict\":\"fail\",\"score\":11,\"findings\":[\"the tests assert nothing\"],\"summary\":\"shallow\"}",
                "exit /b 0",
                ""));
            var plan = new PlanConfig
            {
                Repo = dir,
                Judge = new JudgeConfig { Enabled = true, Command = "cmd.exe", Args = ["/c", script, "{prompt}"] },
            };

            var reply = await Judge.ReviewAsync(plan, "review this");

            Assert.NotNull(reply.Review);
            Assert.Equal("fail", reply.Review!.Verdict);
            Assert.Equal(11, reply.Review.Score);
            Assert.Equal("the tests assert nothing", Assert.Single(reply.Review.Findings));
        }
        finally { TestTemp.DeleteTree(dir); }
    }

    // ── the plan block ──

    [Fact]
    public void AJudgeKeyThatDoesNothingIsRefusedByName()
    {
        var plan = Plan(new JudgeConfig { Enabled = true });
        plan.Judge!.UnknownFields = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["threshold"] = System.Text.Json.JsonDocument.Parse("70").RootElement,
        };

        var errors = plan.CollectErrors();

        var refusal = Assert.Single(errors, e => e.Contains("plan.judge.threshold", StringComparison.Ordinal));
        // The hint matters more than the refusal: a threshold is not a missing feature, it is the one
        // thing this checkpoint is built to make impossible.
        Assert.Contains("evidence, never verdict", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyIsRefusedEvenWhenTheJudgeIsOff()
    {
        var plan = Plan(new JudgeConfig());
        plan.Judge!.UnknownFields = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["model"] = System.Text.Json.JsonDocument.Parse("\"opus\"").RootElement,
        };

        Assert.Contains(plan.CollectErrors(), e => e.Contains("plan.judge.model", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("args-empty")]
    [InlineData("args-no-prompt")]
    [InlineData("output")]
    [InlineData("timeout")]
    public void AnUnanswerableJudgeIsRefusedAtPlanLoad(string flavour)
    {
        var cfg = new JudgeConfig { Enabled = true };
        switch (flavour)
        {
            case "command": cfg.Command = "   "; break;
            case "args-empty": cfg.Args = []; break;
            case "args-no-prompt": cfg.Args = ["-p", "review the diff"]; break;
            case "output": cfg.Output = "yaml"; break;
            default: cfg.TimeoutMinutes = 0; break;
        }

        Assert.Contains(Plan(cfg).CollectErrors(), e => e.Contains("plan.judge.", StringComparison.Ordinal));
    }

    /// <summary>The shipped default is a working invocation, and it is OFF. A judge costs a model spawn
    /// per delivery and buys no decision, so a plan that never asked for one never pays.</summary>
    [Fact]
    public void TheDefaultJudgeBlockIsValidAndDisabled()
    {
        var cfg = new JudgeConfig();

        Assert.False(cfg.Enabled);
        Assert.Contains("{prompt}", cfg.Args);
        Assert.DoesNotContain(Plan(cfg).CollectErrors(), e => e.Contains("plan.judge.", StringComparison.Ordinal));
    }

    /// <summary>A plan with no judge block at all is the normal case and must stay silent.</summary>
    [Fact]
    public void NoJudgeBlockIsNotAFinding()
    {
        var plan = Plan(null);

        Assert.Null(plan.Judge);
        Assert.DoesNotContain(plan.CollectErrors(), e => e.Contains("judge", StringComparison.OrdinalIgnoreCase));
    }

    // ── the rules that keep it evidence ──

    /// <summary>The decision path may not so much as NAME the judge. KS6.4 proves the advisory rows are
    /// declared once and read nowhere; this proves the same for the type that fills them, which is the
    /// half a later refactor could reintroduce by reaching for JudgeReview.Score directly.</summary>
    [Fact]
    public void TheDecisionPathNeverNamesTheJudge()
    {
        string[] surface = ["SessionVerdict.cs", "SessionEvidence.cs", "VerdictDecision.cs", "VerdictDisposition.cs"];
        var violations = surface
            .Where(f => CodeOnly(f).Contains("Judge", StringComparison.Ordinal))
            .ToList();

        Assert.True(violations.Count == 0,
            "KS4.5: the judge is named on the decision path — " + string.Join(", ", violations) +
            ". A judge that a verdict can see is a judge that can decide.");
    }

    /// <summary>The one-way flow, read off the engine's own source: every call that DECIDES happens
    /// before the call that consults the judge, and the judge limb never decides anything itself. This
    /// is what makes "the review cannot flip a verdict" a property of the control flow — the decision
    /// object already exists, fully formed, when the second model is spawned.</summary>
    [Fact]
    public void TheJudgeIsConsultedAfterEveryDecisionAndDecidesNothing()
    {
        var evaluate = CodeOnly("VerdictEngine.Evaluate.cs");
        var consult = evaluate.IndexOf("JudgeSessionAsync(", StringComparison.Ordinal);
        Assert.True(consult > 0, "KS4.5: the engine no longer consults the judge at all");
        Assert.Equal(consult, evaluate.LastIndexOf("JudgeSessionAsync(", StringComparison.Ordinal));

        var lastDecide = evaluate.LastIndexOf("SessionVerdict.Decide(", StringComparison.Ordinal);
        Assert.True(lastDecide > 0 && lastDecide < consult,
            "KS4.5: a verdict is decided AFTER the judge has spoken — that is exactly the wiring this " +
            "checkpoint forbids. The judge must be handed a decision, never consulted before one.");

        // And the limb itself: it may write files, log and bill, but it may not judge.
        Assert.DoesNotContain("SessionVerdict.Decide", CodeOnly("VerdictEngine.Judge.cs"), StringComparison.Ordinal);
    }

    /// <summary>The line a human reads. It names the rows as advisory and says what actually decided,
    /// because the failure mode here is social, not technical: a reader seeing "12/100" beside a green
    /// session and believing the run scored it.</summary>
    [Fact]
    public void TheAdvisoryLineSaysItIsNotTheVerdict()
    {
        var e = new SessionEvidence
        {
            AdvisoryRows = [new AdvisoryEvidence("judge:claude", "fail", 12, "disagrees")],
        };

        var line = VerdictEngine.AdvisoryNote(e);

        Assert.NotNull(line);
        Assert.Contains("NOT part of the verdict", line!, StringComparison.Ordinal);
        Assert.Contains("judge:claude: fail 12/100", line, StringComparison.Ordinal);
        // Silent when no judge ran, so every existing run's log is byte-identical.
        Assert.Null(VerdictEngine.AdvisoryNote(new SessionEvidence()));
    }

    // ── helpers ──

    private static PlanConfig Plan(JudgeConfig? judge) => new()
    {
        Name = "judge-test",
        Repo = Path.GetTempPath(),
        Tracker = "TRACKER.md",
        Stages = { new StageConfig { Id = "S1", Title = "Stage", Sessions = 1 } },
        Judge = judge,
    };

    private static readonly Regex Comments = new(
        @"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    private static string CodeOnly(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "Conductor.Core", "Orchestration", fileName);
        Assert.True(File.Exists(path), $"KS4.5: {path} has moved or been deleted");
        return Comments.Replace(File.ReadAllText(path), " ");
    }
}
