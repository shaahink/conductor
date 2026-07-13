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

public sealed record RunOptions(bool DryRun, bool Once, int MaxSessions, bool ControlPlane = false, int ControlPlanePort = 4317);

/// <summary>
/// Thin wiring hub: owns DI construction for RunContext + the five orchestration satellites.
/// The run loop, session lifecycle, and verdict decisions live in RunLoop / SessionRunner / VerdictEngine.
/// </summary>
#pragma warning disable MA0045
public sealed class Orchestrator
{
    private readonly RunContext _ctx;
    private readonly GateOrchestrator _gates;
    private readonly LaneCoordinator _lanes;
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
        ITelegramService telegram,
        WebhookNotifier webhooks,
        IPlanner? planner = null,
        IRunStore? store = null,
        ProcessSupervisor? processSupervisor = null,
        ControlDispatcher? dispatcher = null,
        ConcurrentQueue<ControlCommand>? controlInbox = null)
    {
        var prompts = BuildPromptBuilder(plan);
        var lessons = new LessonsManager(plan.StateDir);
        var iPlanner = planner ?? new CheckpointPlanner();
        var progress = ProgressProviderFactory.Create(plan);
        var agentProvider = AgentProviderFactory.Create(plan.Agent);

        _ctx = new RunContext(
            plan, state, opts, sink, events, prompts, lessons, iPlanner, progress,
            agentProvider, store, processSupervisor, controlInbox, telegram, webhooks,
            workflowEngine: new WorkflowEngine(), logger);

        _gates = new GateOrchestrator(plan, state, events, store);
        _lanes = new LaneCoordinator(plan, state, sink, events, _ctx.Log, pathClaims: new PathClaimTracker());
    }

    private SessionRunner Sessions => _sessions ??= CreateSessions();
    private VerdictEngine Verdicts => _verdictEngine ??= new VerdictEngine(
        _ctx, _gates, _lanes, _ctx.Telegram, _ctx.Webhooks, SaveAndReport, PushIdleSnapshot);
    private RunLoop Loop => _loop ??= new RunLoop(
        _ctx, Sessions, Verdicts, _gates, _lanes,
        dispatcher: null,
        saveAndReport: () =>
        {
            _ctx.Save();
            var track = ReadTrackerSafe();
            Reporter.WriteAndPublish(_ctx.Plan, _ctx.State, track, _ctx.LastGates, _ctx.Log, store: _ctx.Store);
            PushIdleSnapshot();
        });

    private SessionRunner CreateSessions()
    {
        var v = Verdicts;
        return new SessionRunner(_ctx, _lanes,
            handleControl: ct => Loop.HandleControlAsync(true, ct),
            pushSessionSnapshot: (a, r, s, at, m, t) => PushSessionSnapshot(a, r, s, at, m, t),
            saveAndReport: SaveAndReport,
            evaluateSession: v.EvaluateSessionAsync,
            queueResume: v.QueueResume,
            needsHuman: v.NeedsHuman,
            reflectionStep: v.ReflectionStep);
    }

    public Task<int> RunAsync(CancellationToken ct) => Loop.RunAsync(ct);

    // ── thin facade methods (delegated from RunLoop to avoid exposing it) ──

    private void Save() => _ctx.Save();
    private void SaveAndReport()
    {
        _ctx.Save();
        try
        {
            var track = _ctx.Progress.Read(_ctx.Plan, CancellationToken.None);
            Reporter.WriteAndPublish(_ctx.Plan, _ctx.State, track, _ctx.LastGates, _ctx.Log, store: _ctx.Store);
        }
        catch (Exception) { }
        PushIdleSnapshot();
    }

    private void PushIdleSnapshot()
    {
        TrackerSnapshot track;
        try { track = _ctx.Progress.Read(_ctx.Plan, CancellationToken.None); }
        catch (Exception) { track = new TrackerSnapshot(); }
        _ctx.Sink.Snapshot(SnapshotBuilder.Build(_ctx.Plan, _ctx.State, track,
            _ctx.LastGates != null ? GateRunner.Summary(_ctx.LastGates) : "", _ctx.BackoffUntil));
    }

    private void PushSessionSnapshot(AgentSession agent, SessionRecord rec, StageConfig stage, int attempt, int maxAttempts, TrackerSnapshot track)
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

    private TrackerSnapshot ReadTrackerSafe()
    {
        try { return _ctx.Progress.Read(_ctx.Plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

    private static PromptBuilder BuildPromptBuilder(PlanConfig plan)
    {
        var registry = new PersonaRegistry(plan);
        var lessons = new LessonsManager(plan.StateDir);
        return new PromptBuilder(plan, registry, lessons);
    }
}
