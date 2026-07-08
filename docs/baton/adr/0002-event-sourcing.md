# ADR-0002 — Event-sourced backbone (RunState becomes a projection)

- **Status:** Accepted (decision) — implementation lands in B2. Authored in B0.6.
- **Date:** 2026-07-08
- **Deciders:** Baton self-plan, session #1 (stage B0)
- **Context source:** `docs/baton/BATON-BRIEF.md` §3.2 (the event log) + §0.1 (trust model) + D-5;
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

## Alternatives considered

- **Keep the mutable snapshot, bolt on a side history file.** Rejected: re-introduces the F-9 split
  brain (two sources of truth) and cannot support replay/time-travel cleanly.
- **Big-bang cutover to event sourcing.** Rejected: violates the additive-first / no-resumability-
  regression rule; a projection bug would corrupt a live days-long run with no fallback.
