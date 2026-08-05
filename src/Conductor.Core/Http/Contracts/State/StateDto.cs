using Conductor.Core;
using Conductor.Core.Events;

namespace Conductor.Core.Http;

public sealed record StateDto(
    string PlanName, string Status, string? AttentionReason, string StageId, string StageTitle,
    string? Persona, int DoneCount, int TotalCount, decimal TotalCostUsd, decimal OverheadCostUsd,
    long TokensInput, long TokensOutput, long TokensReasoning,
    string CurrentCheckpoint, string CurrentCheckpointTitle, string GateSummary,
    IReadOnlyList<StageDto> Stages,
    // F6: identity/location so the Face TUI needs no separate plan-file parsing â€” it can read/write
    // the same template + persona markdown files PromptBuilder/PersonaRegistry already hot-reload.
    string RunId, string Repo, string PlanDir,
    // F6: session-level ticker data (live cost/tokens/wall-time/gate battery) â€” DashboardSnapshot
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
    // legacy `output` mode â€” serving the raw field would send null for a run that is plainly Claude.
    // See AgentProviderFactory.ResolveName, which is the same decision the engine runs on.
    string Provider = "",
    // SC2.2: the instant AttentionReason was raised. A reason with no age reads the same after four
    // seconds and after four hours, and the Face had no way to tell them apart. Absent/null = no
    // attention raised, or a run whose state.json predates SC2.2.
    DateTime? AttentionSinceUtc = null,
    // â”€â”€ SC2.3: the budget block. Every one of these was computed OUTSIDE the engine before, by each
    // surface subtracting numbers it had to guess the meaning of â€” and after a budget approval the
    // guess was wrong, because the cap is measured against a window that the approval resets while
    // TotalCostUsd keeps counting the whole run. The engine answers all of it now, once.
    //
    // How the live session's cost is known: "measured" (the CLI's own recorded total, session over),
    // "streamed" (the provider put cost on the wire), "estimated-from-run-rate" (real tokens priced at
    // this run's observed dollars-per-token), "no-rate-yet" (real tokens, no rate to price them with),
    // "none" (nothing in flight). See LiveCostEstimator â€” the vocabulary is closed and lives there.
    string SessionCostBasis = LiveCostEstimator.BasisNone,
    // Spend against the cap: the CURRENT budget window, in-flight session included. This â€” not
    // LifetimeCostUsd â€” is what limits.maxRunCostUsd is compared with.
    decimal CostSpent = 0m,
    // limits.maxRunCostUsd, or null when the plan sets no cost cap. Null cap = null remaining, not
    // an infinite one: "no cap" and "loads left" are different facts and must not render the same.
    decimal? CostCap = null,
    decimal? CostRemaining = null,
    // Mean cost of the run's finished, priced sessions â€” the honest input to "how many more fit".
    decimal MeanSessionCost = 0m,
    int CheckpointsRemaining = 0,
    // Window vs lifetime. Equal until an owner approves past a budget park; after that the window
    // restarts at that instant and the lifetime keeps counting, so a takeover can no longer subtract
    // one from the other and call the difference spend.
    decimal WindowCostUsd = 0m,
    decimal LifetimeCostUsd = 0m,
    DateTime? BudgetWindowStartedUtc = null,
    int BudgetApprovals = 0,
    // â”€â”€ SC5.1: the declared wait. Null = the run is not waiting on anything. Both fields together or
    // neither: a timestamp with no reason is the knowledge loss the verb exists to prevent, and a
    // reason with no timestamp cannot be counted down.
    DateTime? BlockedUntilUtc = null,
    string? BlockedReason = null,
    // â”€â”€ SF3.3: git awareness. Branch, upstream, ahead/behind, dirtiness, HEAD and the last few
    // subjects, read once per GitSnapshotCache.Ttl rather than once per poll. Null = an engine that
    // predates the block; present-with-IsRepo-false = a workspace that is genuinely not a git repo.
    GitDto? Git = null,
    // â”€â”€ FU-OWNER-10: which build am I attached to? The engine's own version and commit (the same
    // stamp `conductor version` and GET /version print), plus the short identity of the Face binary
    // this engine would launch. Three fields so that "did my reinstall take?" is answerable from
    // inside the tool instead of from four out-of-band checks against the process list.
    string EngineVersion = "",
    string EngineCommit = "",
    string FaceBuild = "");
