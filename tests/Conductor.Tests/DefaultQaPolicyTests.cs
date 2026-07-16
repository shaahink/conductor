using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>P2: the QA dial is a PROJECTION onto the existing workflows — resolving a dial value
/// must produce exactly the same run as selecting the corresponding workflow by hand. The pin tests
/// compare the resolved definitions themselves (same instance from the same resolver), which is the
/// design brief's code-quality gate for this stage.</summary>
public sealed class DefaultQaPolicyTests
{
    private readonly DefaultQaPolicy _policy = new();
    private readonly WorkflowEngine _engine = new();

    private static QaRule Rule(string mode, int? threshold = null) =>
        new() { Mode = mode, VerifierThreshold = threshold };

    // ── the projection pin: dial value ≡ hand-picked workflow ──

    [Theory]
    [InlineData("everySession", "deliver-verify")]
    [InlineData("phaseGate", "big-dev-then-big-audit")]
    [InlineData("off", "deliver-verify")] // off = deliver-verify with verification skipped (the M3.2 override machinery)
    public void DialValue_ResolvesTheSameDefinition_AsPickingTheWorkflowByHand(string mode, string handPicked)
    {
        var projected = _policy.Project(Rule(mode), stageRule: null);
        var viaDial = _engine.Resolve(projected.WorkflowName, null, null);
        var byHand = _engine.Resolve(handPicked, null, null);
        Assert.Same(byHand, viaDial);
    }

    [Fact]
    public void DialValue_WinsOverTheStageWorkflow_ThroughTheEngineExtension()
    {
        // A stage pinned to spike + a phaseGate dial: the dial owns QA frequency, so the resolved
        // definition is exactly the hand-picked big-dev-then-big-audit.
        var plan = new PlanConfig { Pipeline = new PipelineRules { Qa = Rule("phaseGate") } };
        var stage = new StageConfig { Id = "s", Workflow = "spike" };
        Assert.Same(_engine.Resolve("big-dev-then-big-audit", null, null), _engine.Resolve(plan, stage, _policy));
    }

    [Fact]
    public void NoDial_LeavesTheClassicResolution_ByteForByte()
    {
        var plan = new PlanConfig();
        var stage = new StageConfig { Id = "s", Workflow = "spike" };
        Assert.Same(_engine.Resolve("spike", null, null), _engine.Resolve(plan, stage, _policy));
        Assert.Same(QaProjection.Classic, _policy.Project(null, null));
    }

    // ── projection semantics ──

    [Fact]
    public void Off_SkipsVerification_VerifyingModesForceItBackOn()
    {
        Assert.True(_policy.Project(Rule("off"), null).SkipVerification);
        Assert.False(_policy.Project(Rule("everySession"), null).SkipVerification);
        Assert.False(_policy.Project(Rule("phaseGate"), null).SkipVerification);
        Assert.Null(_policy.Project(null, null).SkipVerification);
    }

    [Fact]
    public void Modes_AreCaseInsensitive()
    {
        Assert.Equal("big-dev-then-big-audit", _policy.Project(Rule("PHASEGATE"), null).WorkflowName);
        Assert.True(_policy.Project(Rule("OFF"), null).SkipVerification);
    }

    [Fact]
    public void StageRule_ReplacesThePlanRuleWhole()
    {
        var projected = _policy.Project(Rule("everySession", threshold: 90), Rule("off"));
        Assert.True(projected.SkipVerification);
        Assert.Null(projected.VerifierThreshold); // whole-rule precedence: no field merge
    }

    [Fact]
    public void UnknownMode_ProjectsToClassic_AndValidationRejectsIt()
    {
        Assert.Same(QaProjection.Classic, _policy.Project(Rule("sometimes"), null));
        Assert.False(DefaultQaPolicy.IsValidMode("sometimes"));
        Assert.True(DefaultQaPolicy.IsValidMode("everysession"));
    }

    // ── engine-side effective values ──

    [Fact]
    public void EffectiveSkipVerification_DialOwnsTheAnswerWhenSet()
    {
        var overriddenStage = new StageConfig { Id = "s", Overrides = new WorkflowOverrides { SkipVerification = true } };
        var plainStage = new StageConfig { Id = "s" };

        // Dial absent → the classic per-stage override decides.
        Assert.True(_policy.EffectiveSkipVerification(new PlanConfig(), overriddenStage));
        Assert.False(_policy.EffectiveSkipVerification(new PlanConfig(), plainStage));

        // Dial set → it supersedes a stale override in BOTH directions.
        var qaOff = new PlanConfig { Pipeline = new PipelineRules { Qa = Rule("off") } };
        var qaEvery = new PlanConfig { Pipeline = new PipelineRules { Qa = Rule("everySession") } };
        Assert.True(_policy.EffectiveSkipVerification(qaOff, plainStage));
        Assert.False(_policy.EffectiveSkipVerification(qaEvery, overriddenStage));
    }

    [Fact]
    public void EffectiveVerifierThreshold_DialThenLimits()
    {
        var plan = new PlanConfig { Pipeline = new PipelineRules { Qa = Rule("everySession", threshold: 95) } };
        plan.Limits.VerifierThreshold = 80;
        var stage = new StageConfig { Id = "s" };

        Assert.Equal(95, _policy.EffectiveVerifierThreshold(plan, stage));

        plan.Pipeline.Qa!.VerifierThreshold = null;
        Assert.Equal(80, _policy.EffectiveVerifierThreshold(plan, stage));

        stage.Qa = Rule("phaseGate", threshold: 70);
        Assert.Equal(70, _policy.EffectiveVerifierThreshold(plan, stage));
    }

    [Fact]
    public void AuditCoversPriorSessions_DefaultsTrue_AndRidesTheEffectiveRule()
    {
        Assert.True(_policy.Project(Rule("phaseGate"), null).AuditCoversPriorSessions);
        var narrowed = new QaRule { Mode = "phaseGate", AuditCoversPriorSessions = false };
        Assert.False(_policy.Project(null, narrowed).AuditCoversPriorSessions);
    }

    // ── plan validation: a typo'd dial can never silently no-op ──

    [Fact]
    public void PlanValidation_RejectsUnknownModeAndBadThreshold()
    {
        var plan = new PlanConfig
        {
            Pipeline = new PipelineRules { Qa = Rule("sometimes") },
            Stages = { new StageConfig { Id = "s1", Title = "s", Qa = Rule("off", threshold: 250) } },
        };
        var errors = plan.CollectErrors();
        Assert.Contains(errors, e => e.Contains("plan.pipeline.qa.mode"));
        Assert.Contains(errors, e => e.Contains("verifierThreshold must be 1–100"));
    }
}
