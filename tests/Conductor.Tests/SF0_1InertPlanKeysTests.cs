using System.Text.Json;

using Conductor.Core;
using Conductor.Hosting;
using Conductor.Core.Integrations;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Models;
using Conductor.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// SF0.1 — the core run's bugs 6, 11 and 2, plus FU-OWNER-12. One shape connects all four: a surface
/// that reads as a working feature while nothing behind it runs. Bug 6 is a plan key the engine
/// declares, round-trips and never reads; bug 11 is the same thing with one phantom reader nothing
/// calls; bug 2 is a startup line naming a service that started nothing; FU-OWNER-12 is the run
/// saying nothing at all about a notification path that cannot deliver.
///
/// <para>These assert on BEHAVIOUR, not on doc comments — the whole reason this stage exists is that
/// the comments were the part that was true.</para>
/// </summary>
public sealed class SF0_1InertPlanKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-sf01-{Guid.NewGuid():N}");

    public SF0_1InertPlanKeysTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "t.md"),
            "# T\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| S1.1 | first task | TODO | | |\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp dir */ }
    }

    // ───────────────────────────────── bug 6: inert model keys

    private static PlanConfig Deserialize(string body) =>
        JsonSerializer.Deserialize<PlanConfig>($$"""
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] },
          {{body}}
          "stages": [ { "id": "T0", "title": "t", "sessions": 1 } ]
        }
        """, PlanConfig.JsonOpts)!;

    /// <summary>The bug as filed: a model pinned on a workflow step was read by nothing, so the plan
    /// claimed one model answered while another one did. It is refused at load now, and the message
    /// carries the half that is actually useful — where the model really comes from.</summary>
    [Fact]
    public void AModelPinnedOnAWorkflowStepIsRefusedAtLoadAndNamesTheKeyThatWorks()
    {
        var plan = Deserialize("""
          "workflows": { "w": { "name": "w", "steps": [
            { "id": "deliver", "kind": "deliver", "model": "claude-opus-5" } ] } },
        """);

        var error = Assert.Single(plan.CollectErrors(), e => e.Contains("step 'deliver'.model", StringComparison.Ordinal));
        Assert.Contains("nothing reads it", error, StringComparison.Ordinal);
        Assert.Contains("pipeline.roles.<role>.model", error, StringComparison.Ordinal);
        Assert.Contains("stage.agent.model", error, StringComparison.Ordinal);
    }

    /// <summary>The other half of bug 6, and the sharper one: three of this block's four keys reach
    /// the run loop, so the fourth reads as working by association.</summary>
    [Fact]
    public void AModelPinnedOnStageOverridesIsRefusedAtLoadAndNamesTheKeyThatWorks()
    {
        var plan = Deserialize("""
          "workflows": { "w": { "name": "w", "steps": [ { "id": "d", "kind": "deliver" } ] } },
        """);
        plan.Stages[0].Overrides = new WorkflowOverrides
        {
            UnknownFields = new Dictionary<string, JsonElement>
            {
                ["model"] = JsonDocument.Parse("\"claude-opus-5\"").RootElement,
            },
        };

        var error = Assert.Single(plan.CollectErrors(), e => e.Contains("overrides.model", StringComparison.Ordinal));
        Assert.Contains("plan.agent.model", error, StringComparison.Ordinal);
    }

    /// <summary>Deleting the property is what makes the refusal total: <c>plan set</c> validates
    /// against <see cref="PlanKeySchema"/>, which reads the declared type graph. If the property
    /// survived, <c>plan set</c> would still cheerfully write the key that load now rejects — a plan
    /// you can edit into a state it cannot load from.</summary>
    [Fact]
    public void TheDeletedKeysAreGoneFromTheSchemaPlanSetValidatesAgainst()
    {
        Assert.False(PlanKeySchema.Resolve("stages.0.overrides.model").Known);
        Assert.DoesNotContain("model", WorkflowOverrides.KnownFields, StringComparer.Ordinal);
        Assert.DoesNotContain("model", WorkflowStep.KnownFields, StringComparer.Ordinal);

        // The siblings that DO reach the run loop are untouched — this deletes one key, not a feature.
        Assert.True(PlanKeySchema.Resolve("stages.0.overrides.skipVerification").Known);
        Assert.True(PlanKeySchema.Resolve("stages.0.agent.model").Known);
    }

    /// <summary>The regression guard that matters most: the shipped plans' workflow steps use only
    /// declared keys, so the new refusal cannot reject a plan that was fine yesterday.</summary>
    [Fact]
    public void AWorkflowUsingOnlyDeclaredKeysStillLoadsClean()
    {
        var plan = Deserialize("""
          "workflows": { "deliver": { "name": "deliver", "repeat": true, "steps": [
            { "id": "deliver", "kind": "Deliver", "deliver": true },
            { "id": "fix-if-red", "kind": "Fix", "deliver": true, "runIf": "!gatesGreen" } ] } },
          "defaultWorkflow": "deliver",
        """);

        Assert.DoesNotContain(plan.CollectErrors(), e => e.Contains("not a field here", StringComparison.Ordinal));
    }

    // ───────────────────────────────── bug 11: verifyEachDelivery

    private static readonly DefaultQaPolicy Policy = new();
    private static readonly WorkflowEngine Engine = new();

    /// <summary>The bug as filed: a plan setting <c>verifyEachDelivery: false</c> ran a verify after
    /// every delivery anyway. Its only reader was a private method nothing had called since M3.1, so
    /// the live decision — this expression — never saw the key.</summary>
    [Fact]
    public void VerifyEachDeliveryFalseNowReachesTheLiveSkipDecision()
    {
        var stage = new StageConfig { Id = "s" };

        Assert.False(Policy.EffectiveSkipVerification(new PlanConfig(), stage));                       // default: unchanged
        Assert.True(Policy.EffectiveSkipVerification(new PlanConfig { VerifyEachDelivery = false }, stage));
    }

    /// <summary>Lowest precedence, as declared: anything more specific outranks it in both
    /// directions. A dial that says verify wins over the key that says don't, and a stage override
    /// that says skip wins over the key that says verify.</summary>
    [Fact]
    public void TheQaDialAndTheStageOverrideBothOutrankIt()
    {
        var plainStage = new StageConfig { Id = "s" };
        var skipStage = new StageConfig { Id = "s", Overrides = new WorkflowOverrides { SkipVerification = true } };

        var dialOnPlusKeyOff = new PlanConfig
        {
            VerifyEachDelivery = false,
            Pipeline = new PipelineRules { Qa = new QaRule { Mode = "everySession" } },
        };
        Assert.False(Policy.EffectiveSkipVerification(dialOnPlusKeyOff, plainStage));

        Assert.True(Policy.EffectiveSkipVerification(new PlanConfig { VerifyEachDelivery = true }, skipStage));
    }

    /// <summary>The consequence, not just the expression: with the key set false, the workflow that
    /// exists to verify every delivery hands back a Deliver where it used to hand back a Verify. This
    /// is the assertion that would have been false before the fix.</summary>
    [Fact]
    public void WithTheKeyOffTheDeliverVerifyWorkflowStopsQueueingAVerify()
    {
        var stage = new StageConfig { Id = "s" };
        var workflow = Engine.Resolve("deliver-verify", null, null);
        var afterADeliveryThatWentFine = new WorkflowRuntimeVars { GatesGreen = true, HasCommits = true, NewlyDoneCount = 1 };

        SessionKind? NextKindWith(PlanConfig plan)
        {
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            var skip = Policy.EffectiveSkipVerification(plan, stage);
            Engine.ResolveStartKind(workflow, indices, stage.Id, skip);   // the delivery session itself
            return Engine.Advance(workflow, indices, stage.Id, afterADeliveryThatWentFine, skip).Next?.Kind;
        }

        Assert.Equal(SessionKind.Verify, NextKindWith(new PlanConfig()));                        // classic, unchanged
        Assert.NotEqual(SessionKind.Verify, NextKindWith(new PlanConfig { VerifyEachDelivery = false }));
    }

    // ───────────────────────────────── bug 2 + FU-OWNER-12: the notification path

    private PlanConfig HostablePlan() => new()
    {
        Name = "sf01", Repo = _dir, Tracker = "t.md",
        Agent = new AgentConfig { Command = "cmd", Args = ["/c", "echo {prompt}"] },
        Stages = [new StageConfig { Id = "S1", Title = "s", Sessions = 1 }],
    };

    /// <summary>Bug 2: the host named every service it had CALLED StartAsync on. Telegram's start is
    /// an early return whenever there is no telegram block — the ordinary case — so a run announced a
    /// notifier it had not started, in the same words as one it had.</summary>
    [Fact]
    public async Task ARunNeverAnnouncesAStartedServiceThatStartedNothing()
    {
        var plan = HostablePlan();
        Assert.Null(plan.Telegram);
        using var host = ConductorHost.Build(plan, new RunState { RunId = "run-sf01" }, new PlainSink(),
            new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false);

        var started = await ConductorHost.StartRunServicesAsync(host, CancellationToken.None);

        Assert.DoesNotContain(nameof(TelegramService), started, StringComparer.Ordinal);
    }

    /// <summary>…and the service says which half is missing, in the words doctor and
    /// <c>GET /telegram/status</c> already use, so the three surfaces cannot drift (SC1.2).</summary>
    [Fact]
    public void ADeclinedTelegramServiceSaysWhyInDoctorsOwnWords()
    {
        using var svc = new TelegramService(HostablePlan(), new RunState { RunId = "r" },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramService>.Instance, store: null);

        Assert.False(svc.IsStarted);
        Assert.Equal(TelegramReadiness.NoBlock, svc.NotStartedReason);
        Assert.Equal(TelegramReadiness.NoBlock, svc.DeliveryBlocker);
    }

    /// <summary>FU-OWNER-12, measured the way the followup measured it: on a live run with no telegram
    /// block, <c>grep -ci telegram .conductor/conductor.log</c> returned 0 — the verdict existed and
    /// was correct, but only ever when asked, so a silent chat could not be told from an undeliverable
    /// one. This drives the REAL orchestrator and reads the run's own log.</summary>
    [Fact]
    public async Task ARunSaysAtStartWhetherItsPushesCanBeDeliveredAtAll()
    {
        var plan = HostablePlan();
        using (var host = ConductorHost.Build(plan, new RunState { RunId = "run-sf01-log" }, new PlainSink(),
                   new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false))
        {
            await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
        }

        var log = await File.ReadAllTextAsync(Path.Combine(_dir, ".conductor", "conductor.log"));

        Assert.Contains("notifications: telegram will NOT deliver", log, StringComparison.Ordinal);
        Assert.Contains(TelegramReadiness.NoBlock, log, StringComparison.Ordinal);
    }
}
