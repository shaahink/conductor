using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Lanes;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core;

public sealed record RunOptions(bool DryRun, bool Once, int MaxSessions, bool ControlPlane = false, int ControlPlanePort = 4317, bool StartPaused = false);

/// <summary>
/// Thin wiring hub: owns DI construction for RunContext + the five orchestration satellites.
/// The run loop, session lifecycle, and verdict decisions live in RunLoop / SessionRunner / VerdictEngine.
/// </summary>
public sealed class Orchestrator
{
    private readonly RunContext _ctx;
    private readonly GateOrchestrator _gates;
    private readonly LaneCoordinator _lanes;
    private readonly Action<PlanConfig>? _onPlanSwapped;
    private SessionRunner? _sessions;
    private VerdictEngine? _verdictEngine;
    private RunLoop? _loop;

    public Orchestrator(
        PlanConfig plan,
        RunState state,
        IProgressSink sink,
        IEventSink events,
        RunOptions opts,
        ILogger<Orchestrator> logger,
        IRunNotifier telegram,
        WebhookNotifier webhooks,
        IPlanner? planner = null,
        IRunStore? store = null,
        ProcessSupervisor? processSupervisor = null,
        ConcurrentQueue<ControlCommand>? controlInbox = null,
        IWorkflowResolver? workflowResolver = null,
        IAssignmentPolicy? assignmentPolicy = null,
        IQaPolicy? qaPolicy = null,
        Action<PlanConfig>? onPlanSwapped = null)
    {
        _onPlanSwapped = onPlanSwapped;
        var qa = qaPolicy ?? new DefaultQaPolicy();
        var prompts = BuildPromptBuilder(plan, qa);
        var lessons = new LessonsManager(plan.StateDir);
        var iPlanner = planner ?? new CheckpointPlanner();
        var progress = ProgressProviderFactory.Create(plan);
        var agentProvider = AgentProviderFactory.Create(plan.Agent);

        _ctx = new RunContext(
            plan, state, opts, sink, events, prompts, lessons, iPlanner, progress,
            agentProvider, store, processSupervisor, controlInbox, telegram, webhooks,
            workflowResolver: workflowResolver ?? new WorkflowEngine(), logger,
            assignmentPolicy: assignmentPolicy ?? new DefaultAssignmentPolicy(),
            qaPolicy: qa);

        _gates = new GateOrchestrator(plan, state, events, store);
        // KS5.2: the lanes get the run's ledger, so a lane's billed spend reaches the costs table and
        // the cap total instead of evaporating with the pool thread that produced it.
        _lanes = new LaneCoordinator(plan, state, sink, events, _ctx.Log,
            pathClaims: new PathClaimTracker(), ledger: _ctx.Ledger);
    }

    private SessionRunner Sessions => _sessions ??= CreateSessions();
    private VerdictEngine Verdicts => _verdictEngine ??= new VerdictEngine(
        _ctx, _gates, _lanes, _ctx.Messenger, _ctx.Webhooks, SaveAndReport, PushIdleSnapshot);
    private RunLoop Loop => _loop ??= new RunLoop(
        _ctx, Sessions, Verdicts, _gates, _lanes,
        dispatcher: null,
        saveAndReport: () =>
        {
            _ctx.Save();
            Reporter.WriteAndPublish(_ctx.Plan, _ctx.State, _ctx.ReadWork(), _ctx.LastGates, _ctx.Log, store: _ctx.Store,
                onNewOwnerItems: _ctx.NotifyNewOwnerQueueItems);
            PushIdleSnapshot();
        },
        onPlanSwapped: _onPlanSwapped);

    private SessionRunner CreateSessions()
    {
        var v = Verdicts;
        return new SessionRunner(_ctx, _lanes,
            handleControl: ct => Loop.HandleControlAsync(true, ct),
            pushSessionSnapshot: (a, r, s, at, m, t) => PushSessionSnapshot(a, r, s, at, m, t),
            saveAndReport: SaveAndReport,
            evaluateSession: v.EvaluateSessionAsync,
            recordRolloverFacts: v.RecordRolloverFacts,
            queueResume: v.QueueResume,
            needsHuman: v.NeedsHuman,
            reflectionStep: v.ReflectionStep,
            notify: v.NotifyOperator);
    }

    public Task<int> RunAsync(CancellationToken ct) => Loop.RunAsync(ct);

    // ── thin facade methods (delegated from RunLoop to avoid exposing it) ──

    private void SaveAndReport()
    {
        _ctx.Save();
        BestEffort.Run(() => Reporter.WriteAndPublish(_ctx.Plan, _ctx.State, _ctx.ReadWork(), _ctx.LastGates, _ctx.Log,
            store: _ctx.Store, onNewOwnerItems: _ctx.NotifyNewOwnerQueueItems));
        PushIdleSnapshot();
    }

    private void PushIdleSnapshot()
        => _ctx.Sink.Snapshot(SnapshotBuilder.Build(_ctx.Plan, _ctx.State, _ctx.ReadWork(),
            _ctx.LastGates != null ? GateRunner.Summary(_ctx.LastGates) : "", _ctx.BackoffUntil));

    private void PushSessionSnapshot(AgentSession agent, SessionRecord rec, StageConfig _, int attempt, int maxAttempts, TrackerSnapshot track)
        => _ctx.Sink.Snapshot(SnapshotBuilder.Build(_ctx.Plan, _ctx.State, track,
            _ctx.LastGates != null ? GateRunner.Summary(_ctx.LastGates) : "", _ctx.BackoffUntil) with
        {
            SessionNumber = rec.Number,
            SessionKind = rec.Kind.ToString(),
            Attempt = attempt,
            MaxAttempts = maxAttempts,
            ResumeCount = rec.ResumeCount,
            SessionCostUsd = agent.CostUsd ?? 0m,
            SessionTokensInput = agent.TokensInput ?? 0,
            SessionTokensOutput = agent.TokensOutput ?? 0,
            SessionTokensReasoning = agent.TokensReasoning ?? 0,
            SessionElapsed = DateTime.UtcNow - agent.StartedUtc,
            LastActivityAgoSec = (DateTime.UtcNow - agent.LastActivityUtc).TotalSeconds,
            AgentActive = true,
        });

    private static PromptBuilder BuildPromptBuilder(PlanConfig plan, IQaPolicy qa)
    {
        var registry = new PersonaRegistry(plan);
        var lessons = new LessonsManager(plan.StateDir);
        return new PromptBuilder(plan, registry, lessons, qa);
    }
}
