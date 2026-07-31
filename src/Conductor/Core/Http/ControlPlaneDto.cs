using System.Text.Json.Serialization;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Http;

public sealed record StateDto(
    string PlanName, string Status, string? AttentionReason, string StageId, string StageTitle,
    string? Persona, int DoneCount, int TotalCount, decimal TotalCostUsd, decimal OverheadCostUsd,
    long TokensInput, long TokensOutput, long TokensReasoning,
    string CurrentCheckpoint, string CurrentCheckpointTitle, string GateSummary,
    IReadOnlyList<StageDto> Stages,
    // F6: identity/location so the Face TUI needs no separate plan-file parsing — it can read/write
    // the same template + persona markdown files PromptBuilder/PersonaRegistry already hot-reload.
    string RunId, string Repo, string PlanDir,
    // F6: session-level ticker data (live cost/tokens/wall-time/gate battery) — DashboardSnapshot
    // already computes all of this for the old Spectre dashboard; it just wasn't on the wire yet.
    int SessionNumber, string SessionKind, int Attempt, int MaxAttempts,
    double SessionElapsedSec, bool AgentActive,
    decimal SessionCostUsd, long SessionTokensInput, long SessionTokensOutput, long SessionTokensReasoning,
    IReadOnlyList<GateDto> Gates,
    // P5 follow-up: the set-rollover this-run override, read off the live RunState (it is run-state
    // only, never event-folded). Absent on the wire = no override (the plan's limits.maxSessionTokens
    // decides); 0 = rollover forced OFF this run; >0 = the per-session token cap this run.
    long? MaxSessionTokensThisRun = null,
    // The model the current/last session's resolved agent runs (stage + assignment overrides applied),
    // from the latest SessionStarted event; before any session, the stage/plan default. "" = unknown.
    string Model = "",
    // U1.1: the rest of the workspace identity the Face's Home panel names. Both are engine-computed
    // (PlanConfig.Tracker / PlanConfig.StateDir) rather than re-derived Face-side: StateDir is rooted at
    // Repo, NOT PlanDir, so a plan whose json lives outside the repo root cannot be guessed from PlanDir.
    string Tracker = "",
    string StateDir = "",
    // U3.3: the RESOLVED agent provider for the current stage ("claude" | "opencode" | "text"), so the
    // Face can adopt that CLI's transcript conventions. Resolved, never the raw AgentConfig.Provider:
    // that field is nullable and unset on most plans, where the real provider is inferred from the
    // legacy `output` mode — serving the raw field would send null for a run that is plainly Claude.
    // See AgentProviderFactory.ResolveName, which is the same decision the engine runs on.
    string Provider = "",
    // SC2.2: the instant AttentionReason was raised. A reason with no age reads the same after four
    // seconds and after four hours, and the Face had no way to tell them apart. Absent/null = no
    // attention raised, or a run whose state.json predates SC2.2.
    DateTime? AttentionSinceUtc = null,
    // ── SC2.3: the budget block. Every one of these was computed OUTSIDE the engine before, by each
    // surface subtracting numbers it had to guess the meaning of — and after a budget approval the
    // guess was wrong, because the cap is measured against a window that the approval resets while
    // TotalCostUsd keeps counting the whole run. The engine answers all of it now, once.
    //
    // How the live session's cost is known: "measured" (the CLI's own recorded total, session over),
    // "streamed" (the provider put cost on the wire), "estimated-from-run-rate" (real tokens priced at
    // this run's observed dollars-per-token), "no-rate-yet" (real tokens, no rate to price them with),
    // "none" (nothing in flight). See LiveCostEstimator — the vocabulary is closed and lives there.
    string SessionCostBasis = LiveCostEstimator.BasisNone,
    // Spend against the cap: the CURRENT budget window, in-flight session included. This — not
    // LifetimeCostUsd — is what limits.maxRunCostUsd is compared with.
    decimal CostSpent = 0m,
    // limits.maxRunCostUsd, or null when the plan sets no cost cap. Null cap = null remaining, not
    // an infinite one: "no cap" and "loads left" are different facts and must not render the same.
    decimal? CostCap = null,
    decimal? CostRemaining = null,
    // Mean cost of the run's finished, priced sessions — the honest input to "how many more fit".
    decimal MeanSessionCost = 0m,
    int CheckpointsRemaining = 0,
    // Window vs lifetime. Equal until an owner approves past a budget park; after that the window
    // restarts at that instant and the lifetime keeps counting, so a takeover can no longer subtract
    // one from the other and call the difference spend.
    decimal WindowCostUsd = 0m,
    decimal LifetimeCostUsd = 0m,
    DateTime? BudgetWindowStartedUtc = null,
    int BudgetApprovals = 0,
    // ── SC5.1: the declared wait. Null = the run is not waiting on anything. Both fields together or
    // neither: a timestamp with no reason is the knowledge loss the verb exists to prevent, and a
    // reason with no timestamp cannot be counted down.
    DateTime? BlockedUntilUtc = null,
    string? BlockedReason = null);

public static class ControlPlaneDto
{
    public static StateDto FromSnapshot(DashboardSnapshot snap, string runId, string repo, string planDir,
        long? maxSessionTokensThisRun = null, string tracker = "", string stateDir = "") => new(
        PlanName: snap.PlanName,
        Status: snap.Status,
        AttentionReason: snap.AttentionReason,
        StageId: snap.StageId,
        StageTitle: snap.StageTitle,
        Persona: snap.Persona,
        DoneCount: snap.DoneCount,
        TotalCount: snap.TotalCount,
        TotalCostUsd: snap.TotalCostUsd,
        OverheadCostUsd: snap.OverheadCostUsd,
        TokensInput: snap.TokensInput,
        TokensOutput: snap.TokensOutput,
        TokensReasoning: snap.TokensReasoning,
        CurrentCheckpoint: snap.CurrentCheckpoint,
        CurrentCheckpointTitle: snap.CurrentCheckpointTitle,
        GateSummary: snap.GateSummary,
        Stages: [.. snap.Stages.Select(FromStage)],
        RunId: runId,
        Repo: repo,
        PlanDir: planDir,
        SessionNumber: snap.SessionNumber,
        SessionKind: snap.SessionKind,
        Attempt: snap.Attempt,
        MaxAttempts: snap.MaxAttempts,
        SessionElapsedSec: snap.SessionElapsed.TotalSeconds,
        AgentActive: snap.AgentActive,
        SessionCostUsd: snap.SessionCostUsd,
        SessionTokensInput: snap.SessionTokensInput,
        SessionTokensOutput: snap.SessionTokensOutput,
        SessionTokensReasoning: snap.SessionTokensReasoning,
        Gates: [.. snap.Gates.Select(g => new GateDto(g.Name, g.State, g.LiveElapsed(DateTime.UtcNow).TotalSeconds))],
        MaxSessionTokensThisRun: maxSessionTokensThisRun,
        Tracker: tracker,
        StateDir: stateDir,
        AttentionSinceUtc: snap.AttentionSinceUtc,
        BlockedUntilUtc: snap.BlockedUntilUtc,
        BlockedReason: snap.BlockedReason);

    private static StageDto FromStage(StageProgress s) => new(
        Id: s.Id, Title: s.Title, Done: s.Done, Total: s.Total, State: s.State,
        Attempts: s.Attempts, LastOutcome: s.LastOutcome, CostUsd: s.CostUsd,
        ParentId: s.ParentId, Depth: s.Depth,
        Checkpoints: [.. s.Checkpoints.Select(c => new CheckpointDto(c.Id, c.Title, c.Status))]);

    public static TasksDto FromTasks(IReadOnlyList<TaskItem> tasks) => new(
        [.. tasks.Select(t => new TaskDto(t.TaskId, t.CheckpointId, t.Title, t.Status, t.Source, t.Order, t.Context, t.Paths,
            Kind: t.Kind, StageId: t.StageId, Confirmed: t.Confirmed, Qa: t.Qa))]);

    public static ProcessDto FromPid(PidRow p, bool alive, string? lastOutputLine) => new(
        Pid: p.Pid, Purpose: p.Purpose, StageId: p.StageId, SessionNumber: p.SessionNumber,
        StartedUtc: p.StartedUtc.ToString("O"), ExitedUtc: p.ExitedUtc?.ToString("O"), ExitCode: p.ExitCode,
        Alive: alive, LastOutputLine: lastOutputLine);
}

/// <summary>Source-generated (de)serialisation for the control plane's DTOs — camelCase, matching
/// <c>Events.EventJsonContext</c>'s convention for the rest of the wire spine.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(StateDto))]
[JsonSerializable(typeof(TasksDto))]
[JsonSerializable(typeof(TaskUpdateRequestDto))]
[JsonSerializable(typeof(TaskAddRequestDto))]
[JsonSerializable(typeof(TaskEditRequestDto))]
[JsonSerializable(typeof(TaskRefineRequestDto))]
[JsonSerializable(typeof(TaskRefineResultDto))]
[JsonSerializable(typeof(TaskSplitRequestDto))]
[JsonSerializable(typeof(TaskSplitResultDto))]
[JsonSerializable(typeof(TaskWriteResultDto))]
[JsonSerializable(typeof(PromptBlocksDto))]
[JsonSerializable(typeof(ControlRequestDto))]
[JsonSerializable(typeof(ControlAcceptedDto))]
[JsonSerializable(typeof(ProcessesDto))]
[JsonSerializable(typeof(ProcessKillRequestDto))]
[JsonSerializable(typeof(ProcessKillResultDto))]
[JsonSerializable(typeof(SessionsDto))]
[JsonSerializable(typeof(QueryResultDto))]
[JsonSerializable(typeof(InjectRequestDto))]
[JsonSerializable(typeof(InjectAcceptedDto))]
[JsonSerializable(typeof(PromptPreviewDto))]
[JsonSerializable(typeof(TimelineDto))]
[JsonSerializable(typeof(LedgerDto))]
[JsonSerializable(typeof(BugsDto))]
[JsonSerializable(typeof(NoteRequestDto))]
[JsonSerializable(typeof(BugNewRequestDto))]
[JsonSerializable(typeof(BugResolveRequestDto))]
[JsonSerializable(typeof(KnowledgeWriteResultDto))]
[JsonSerializable(typeof(ConsoleLineDto))]
[JsonSerializable(typeof(ControlPlaneInfo))]
[JsonSerializable(typeof(PlanDto))]
[JsonSerializable(typeof(PlanEditRequestDto))]
[JsonSerializable(typeof(PlanMutationResultDto))]
[JsonSerializable(typeof(PlanImportRequestDto))]
[JsonSerializable(typeof(PlanImportResultDto))]
[JsonSerializable(typeof(TelegramStatusDto))]
[JsonSerializable(typeof(TelegramTestResultDto))]
[JsonSerializable(typeof(TelegramSetTokenRequestDto))]
[JsonSerializable(typeof(TelegramSetTokenResultDto))]
public sealed partial class ControlPlaneJsonContext : JsonSerializerContext;
