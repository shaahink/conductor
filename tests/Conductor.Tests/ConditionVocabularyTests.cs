using System.Text.Json;
using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Tests;

/// <summary>SC3.1 — <c>runIf</c>/<c>skipIf</c> typos used to mean "always run": the evaluator ends in
/// <c>_ =&gt; true</c>, so <c>"!gatesgreen"</c> (wrong case) inverted a step's control flow with no
/// diagnostic anywhere (devcontext #4). These tests measure the two halves that make the fix real —
/// that <see cref="ConditionVocabulary"/> is a faithful mirror of the evaluator (not a hand-kept
/// list that can drift), and that a plan carrying an unknown token is refused at load.</summary>
public sealed class ConditionVocabularyTests
{
    private static readonly WorkflowEngine Engine = new();

    private static WorkflowRuntimeVars AllFalse() => new()
    {
        VerifierScore = 0, VerifierPassed = false, CircuitBroken = false, StageAttempts = 0,
        GatesGreen = false, HasCommits = false, Stalled = false, NewlyDoneCount = 0, StageComplete = false,
    };

    private static WorkflowRuntimeVars AllTrue() => new()
    {
        VerifierScore = 100, VerifierPassed = true, CircuitBroken = true, StageAttempts = 9,
        GatesGreen = true, HasCommits = true, Stalled = true, NewlyDoneCount = 9, StageComplete = true,
    };

    /// <summary>The mirror, measured rather than asserted by inspection: every token the vocabulary
    /// accepts must reach a REAL branch of the evaluator. A token that had fallen out of the switch
    /// would hit the permissive default and read true against all-false vars — so "false here" is
    /// the only evidence that the evaluator actually knows it.</summary>
    [Fact]
    public void EveryBooleanToken_ReachesARealEvaluatorBranch()
    {
        foreach (var token in ConditionVocabulary.BooleanTokens)
        {
            Assert.Null(ConditionVocabulary.Validate(token));
            Assert.False(Engine.EvaluateCondition(token, AllFalse()), $"'{token}' read true against all-false vars — it is hitting the permissive default, not a real branch");
            Assert.True(Engine.EvaluateCondition(token, AllTrue()), $"'{token}' read false against all-true vars");
        }
    }

    /// <summary>Same measurement for the numeric half: an unknown left-hand side makes
    /// <c>TryEvalNumericCompare</c> bail out to the same permissive default, so a comparison that
    /// cannot possibly hold must read false.</summary>
    [Fact]
    public void EveryNumericToken_ReachesARealEvaluatorBranch()
    {
        var vars = AllTrue(); // every numeric var is 9 or 100 here — all well below 100000
        foreach (var token in ConditionVocabulary.NumericTokens)
        {
            Assert.Null(ConditionVocabulary.Validate($"{token} >= 100000"));
            Assert.False(Engine.EvaluateCondition($"{token} >= 100000", vars), $"'{token} >= 100000' read true — unknown left-hand side falls through to the permissive default");
            Assert.True(Engine.EvaluateCondition($"{token} < 100000", vars));
        }
    }

    [Theory]
    [InlineData("!verifier.passed")]
    [InlineData("verifier.score >= 80")]
    [InlineData("newlyDoneCount == 0")]
    [InlineData("stage.attempts > 2")]
    [InlineData("  gatesGreen  ")]
    [InlineData("!newlyDoneCount > 0")]
    public void ValidExpressions_Pass(string expr) => Assert.Null(ConditionVocabulary.Validate(expr));

    [Theory]
    [InlineData("!gatesgreen")]      // wrong case — the field-observed typo
    [InlineData("gates.green")]      // wrong shape
    [InlineData("verifierPassed")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gatesGreen == true")]   // boolean token on the left of a comparison
    [InlineData("verifier.score >= high")]
    [InlineData("score > 5")]
    public void UnknownExpressions_AreRejected_AndTheyAreExactlyWhatTheEvaluatorWouldWave(string expr)
    {
        var why = ConditionVocabulary.Validate(expr);
        Assert.NotNull(why);
        // The counterpart to the mirror test above: each of these DOES hit the permissive default,
        // so its runtime answer is invariant — identical against all-false and all-true vars, i.e.
        // it ignores reality completely. (Measured, correcting the field note's framing: bare junk
        // is stuck TRUE, so a conditional step always runs, while "!junk" is stuck FALSE, so the
        // step never runs. Both invert the author's intent in silence; only the sign differs.)
        var onFalse = Engine.EvaluateCondition(expr, AllFalse());
        var onTrue = Engine.EvaluateCondition(expr, AllTrue());
        Assert.Equal(onFalse, onTrue);
    }

    [Fact]
    public void Describe_NamesEveryToken_SoTheRefusalIsActionable()
    {
        var described = ConditionVocabulary.Describe();
        foreach (var token in ConditionVocabulary.BooleanTokens.Concat(ConditionVocabulary.NumericTokens))
            Assert.Contains(token, described, StringComparison.Ordinal);
    }

    /// <summary>The shipped workflows are the examples authors copy — if one of them failed the
    /// vocabulary, plan load would reject the engine's own defaults.</summary>
    [Theory]
    [InlineData("deliver-verify")]
    [InlineData("big-dev-then-big-audit")]
    [InlineData("docs-only")]
    [InlineData("spike")]
    public void BuiltInWorkflows_UseOnlyValidConditions(string name)
    {
        var wf = Engine.Resolve(name, null, null);
        Assert.Equal(name, wf.Name);
        foreach (var step in wf.Steps)
        {
            Assert.Null(step.RunIf is null ? null : ConditionVocabulary.Validate(step.RunIf));
            Assert.Null(step.SkipIf is null ? null : ConditionVocabulary.Validate(step.SkipIf));
        }
    }

    // --- plan load ---

    private const string PlanTemplate = """
    {
      "name": "T", "repo": "{repo}", "tracker": "TRACKER.md",
      "agent": { "command": "cmd", "args": ["/c", "echo", "{prompt}"] },
      "stages": [ { "id": "S1", "title": "one", "sessions": 1 } ],
      "workflows": {
        "custom": { "name": "custom", "steps": [
          { "id": "deliver", "kind": "Deliver" },
          { "id": "fix", "kind": "Fix", "{field}": "{expr}" }
        ] }
      }
    }
    """;

    private static PlanConfig LoadWith(string field, string expr, string dir)
    {
        var repo = Path.Combine(dir, "repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"), "# tracker\n");
        var json = PlanTemplate
            .Replace("{repo}", repo.Replace('\\', '/'), StringComparison.Ordinal)
            .Replace("{field}", field, StringComparison.Ordinal)
            .Replace("{expr}", expr, StringComparison.Ordinal);
        var path = Path.Combine(dir, "p.plan.json");
        File.WriteAllText(path, json);
        return PlanConfig.Load(path);
    }

    [Theory]
    [InlineData("runIf", "!gatesgreen")]
    [InlineData("skipIf", "gates.green")]
    public void PlanLoad_RefusesUnknownConditionToken_NamingTheVocabulary(string field, string expr)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-cond-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => LoadWith(field, expr, dir));
            Assert.Contains($"step 'fix' {field} '{expr}'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("gatesGreen", ex.Message, StringComparison.Ordinal);      // the vocabulary is named
            Assert.Contains("verifier.score", ex.Message, StringComparison.Ordinal);
        }
        finally { try { TestTemp.DeleteTree(dir); } catch (Exception) { /* best effort */ } }
    }

    [Fact]
    public void PlanLoad_AcceptsAValidCondition()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-cond-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var plan = LoadWith("runIf", "!gatesGreen", dir);
            Assert.Equal("!gatesGreen", plan.Workflows!["custom"].Steps[1].RunIf);
        }
        finally { try { TestTemp.DeleteTree(dir); } catch (Exception) { /* best effort */ } }
    }

    /// <summary>Guards the blast radius of the new refusal: every plan shipped in this repo must
    /// still load. A validation rule that fails the project's own plans is a broken rule.</summary>
    [Fact]
    public void ShippedPlans_StillValidateTheirConditions()
    {
        string? plansDir = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "plans");
            if (Directory.Exists(candidate)) { plansDir = candidate; break; }
        }
        if (plansDir == null) return; // not in a full checkout — soft skip

        var checkedSteps = 0;
        foreach (var file in Directory.EnumerateFiles(plansDir, "*.plan.json"))
        {
            var cfg = JsonSerializer.Deserialize<PlanConfig>(File.ReadAllText(file), PlanConfig.JsonOpts);
            if (cfg?.Workflows is not { } workflows) continue;
            foreach (var step in workflows.Values.SelectMany(wf => wf.Steps))
            {
                Assert.Null(step.RunIf is null ? null : ConditionVocabulary.Validate(step.RunIf));
                Assert.Null(step.SkipIf is null ? null : ConditionVocabulary.Validate(step.SkipIf));
                if (step.RunIf is not null || step.SkipIf is not null) checkedSteps++;
            }
        }
        Assert.True(checkedSteps > 0, "no shipped plan carries a conditional step — this guard is measuring nothing");
    }
}
