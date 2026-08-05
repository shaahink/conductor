# ADR-0002 — Event-sourced backbone (RunState becomes a projection)

- **Status:** Accepted & implemented — B2 event log is live (B2.1); projections (RunState/TaskGraph/Timeline/Health/Confidence/McpMetrics) fold the log (B2.2–B5.4); crash recovery replays events (B2.3); parity tests green (StateCompatTests, RunStateProjectionTests). Additive discipline: events.jsonl is emitted ALONGSIDE state.json; no cutover is attempted until parity is proven. **Amended by W1.1 (2026-07-28) — see the amendment below.**
- **Date:** 2026-07-08 · **Last updated:** 2026-07-28 (W1.1 — checkpoints join the fold; the mutable checkpoints table is dropped)
- **Deciders:** Baton self-plan, session #1 (stage B0)
- **Context source:** `docs/history/baton/BATON-BRIEF.md` §3.2 (the event log) + §0.1 (trust model) + D-5;
  findings F-3 (live token lag), F-9 (no history/replay/health).

## Context

Today Conductor's durable state is a single mutable `RunState` serialised to
`.conductor/state.json` (see `src/Conductor/Models/RunState.cs`, written by the Orchestrator at
session end). Consequences observed on the live runs:

- **F-3:** token/cost only reach `state.json` at session *end*, so the live dashboard token line lags
  a whole session; AFK you cannot see current burn.
- **F-9:** observability is "the terminal now" — there is no timeline, no replay/time-travel, and no
  execution-health signal (retry loops, command repetition, tool oscillation). `RunState.History`
  is a coarse per-session summary that cannot answer "what happened at 01:12?".
- The report, metrics, and (future) Telegram all re-derive from the same mutable snapshot, so any new
  view means widening the mutable model — the opposite of SOLID.

The whole product rests on the trust model (§0.1): Conductor independently re-verifies gates,
commits, and tracker diffs. That model wants an **append-only audit trail**, not an overwritten blob.

## Decision

Adopt a **full event-sourced backbone**. Every meaningful transition is appended to an
append-only NDJSON log at `.conductor/events.jsonl`. `RunState`, `TaskGraph`, `Timeline`, `Metrics`,
`Health`, and `REPORT.md` all become **projections** rebuilt by folding the log.

Event vocabulary (schema owned by B2; see BRIEF §3.2):
`RunStarted, StageEntered, SessionStarted, TaskAdded, TaskStatusChanged, Thought, ToolCalled,
CommandStarted/Finished, GateStarted/Finished, TokenDelta, CheckpointConfirmed, Retry, Resume,
RolledOver, HumanInput, OwnerApprovalRequested/Granted, StageFinished, RunFinished`.

Rules: **append-only, never mutate**; each event carries `runId`, `sessionId`, `ts`. Crash recovery
= replay the log. `TokenDelta` is emitted per `step_finish` (directly fixes F-3).

## Migration strategy — additive first, cutover only after proven parity

This is the non-negotiable constraint (BRIEF §0.1, D-5, and the session rule "additive-first for
anything touching state/resumability — resumability must never regress"):

1. **Emit alongside.** B2 writes `events.jsonl` *in addition to* today's `state.json`. Nothing that
   currently reads `state.json` changes yet. Resumability keeps running off `state.json`.
2. **Project + compare.** A `RunState` projection is rebuilt by folding the log and checked against
   the live `state.json` under `StateCompatTests` (the existing `tests/.../StateCompatTests.cs` is the
   seed). Parity must hold across a real multi-session run, including crash-recovery replay.
3. **Cutover.** Only once parity is proven does `state.json` demote to a *cache/optimisation* and the
   projection become authoritative. The event log never becomes optional.

If parity cannot be shown, we do **not** cut over — we keep emitting additively and treat it as a
tracked followup, never a silent regression.

## Consequences

- Unlocks B5 (timeline, replay/time-travel, AI-health), B9 (event-sourced task graph), and clean
  live token/cost (F-3) — all as projections, no further mutable-model widening.
- New durable dependency: `.conductor/events.jsonl` (append-only; kept out of git like other
  `.conductor/` runtime state).
- Cost: dual-write during the additive window and a real parity test battery before cutover — an
  accepted, bounded cost paid once in B2.

## Amendment — W1.1 (2026-07-28): checkpoints join the fold

The F1-era `checkpoints` table (migration v2, `confirmed` in v6) was mutable state written in
place by `SeedCheckpoints`/`UpdateCheckpoint` — this ADR's own violation, and the root of gap
G4 (two seeds disagreeing on restart) and the `newly DONE []` claim-path split
(`docs/dev/GAP-ANALYSIS.md` §1). As of W1.1:

- **Checkpoints and Kanban tasks are ONE event-sourced work graph.** `TaskAdded` carries
  `kind` (`checkpoint` | `subtask`) and `stageId`; `Source` is the provenance vocabulary
  (`plan` | `tracker` | `import` | `human` | `agent`). `TaskStatusChanged` carries the claim's
  `commit`/`evidence`/`source`. `CheckpointConfirmed` folds into `TaskGraph` (sets
  `Confirmed`) and is emitted by the M4.1 confirm path (`IRunStore.ConfirmCheckpoints`,
  post-verify) — no longer at session-Advanced, where it overstated confirmation.
- **The `checkpoints` table is dropped** (migration v8). `IRunStore.GetCheckpoints` folds the
  event log; the write methods (`SeedCheckpoints`/`UpdateCheckpoint`/`MarkCheckpointInProgress`/
  `ConfirmCheckpoints`) are adapters that emit graph events — signatures unchanged, so every
  consumer (TrackerGenerator, verdict engine, `conductor task`) moved onto the graph without
  edits. Replaying the log reproduces checkpoint state byte-for-byte (`W1WorkGraphTests`).
- **Seq is allocated at persist time, from the database, inside the write transaction** —
  the events PK is `(seq, run_id)` and two processes share run.db (engine + the
  `conductor task` claim path); Emit-time stamps are provisional queue ordinals only.
- Re-seeding is upsert-never-clobber: new items land with full tracker state; existing items
  refresh their declared title only — runtime status is never overwritten by a re-sync
  (the W-series design principle; `docs/history/CONDUCTOR-WORKGRAPH.md`).

## Alternatives considered

- **Keep the mutable snapshot, bolt on a side history file.** Rejected: re-introduces the F-9 split
  brain (two sources of truth) and cannot support replay/time-travel cleanly.
- **Big-bang cutover to event sourcing.** Rejected: violates the additive-first / no-resumability-
  regression rule; a projection bug would corrupt a live days-long run with no fallback.
