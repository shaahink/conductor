# Maestro — Final Audit (M9.2)

**Written:** 2026-07-15 · **Branch:** `feat/foreman` · **Design authority:** `docs/history/MAESTRO-PLAN.md`

Every checkpoint in the design doc, rated **CONFORMS** / **DEVIATES**, with the evidence used to
decide. Per the design doc's own rule for M9.2, truth gates were **re-run this session** wherever
that was possible without the owner's paid credentials — not trusted from the tracker. Live re-runs
are marked **(live)**; checkpoints resting on the test suite are marked **(test)** and name the test.

The dogfood vehicle was a real end-to-end `conductor run` of a toy plan through the binary built
from this branch, driven by the token-free `tools/fake-agent.ps1`. That exercises the whole engine
path (spawn → parse → gate → verdict → confirm → history → report → escalation) without spending on
the real model. Four defects bled out of it and were fixed this session (see M9.1).

## Verdict summary

| Milestone | CONFORMS | DEVIATES |
|---|---|---|
| M1 Deconstruction | 4/4 | — |
| M2 One truth | 5/5 | — (M2.4 was deviating; fixed this session) |
| M3 Workflows | 3/3 | — |
| M4 Gates | 3/3 | — |
| M5 Observability | 6/6 | — |
| M6 Plan authoring | 3/3 | — |
| M7 Knowledge | 3/3 | — |
| M8 AFK | 2/3 | M8.3 Telegram live phone dogfood (needs owner's bot token) |
| M9 Dogfood close | 2/2 | — (M9.1 real-model run remains the owner's paid dogfood; see note) |

**30/31 design-doc feature checkpoints CONFORM.** The single open DEVIATE (M8.3's live phone dogfood)
and the one caveat (M9.1's real-model run) share one root cause: they require the owner's real
credentials — a bot token and paid model spend — which a `HUMAN:` gate reserves for the owner.
Everything reproducible without credentials was reproduced and conforms.

---

## M1 — Deconstruction

| # | Verdict | Evidence |
|---|---|---|
| M1.1 delete `Ui/**`, PreviewCommand | **CONFORMS** (live) | `grep -rn "Conductor.Ui" src/ tests/` → nothing. Toy `conductor run` drives a plan to completion. |
| M1.2 split `Commands.cs`, none >250L | **CONFORMS** (live) | No file in `src/Conductor/Commands/` exceeds 250 lines (checked all). |
| M1.3 split `Orchestrator.cs` ≤400L | **CONFORMS** (live) | `Orchestrator.cs` is 145 lines (thin DI wiring); RunLoop/SessionRunner/VerdictEngine carry the logic. |
| M1.4 `architecture-baseline.json` == `{}` | **CONFORMS** (live) | `filesOverLineCeiling` and `filesOverTypeCeiling` are both empty; `ArchitectureTests` hold the line. |

## M2 — One truth: the database

| # | Verdict | Evidence |
|---|---|---|
| M2.1 schema defined once | **CONFORMS** (test) | Versioned `.sql` migrations under `Core/Store/Migrations`; `MigrationRunner`; fresh-vs-migrated schema-identity test in suite. |
| M2.2 `IRunStore`, rows land, loud failures | **CONFORMS** (live) | `conductor log --query "stage=T0"` returned real event rows from the toy `run.db` (110 KB on disk). All SQL behind the store (fitness test). |
| M2.3 `run.db` authoritative, no `state.json` | **CONFORMS** (live) | The toy run wrote **no** `state.json` — only `run.db`. Resume path reads `FindInterruptedSession` from the store. |
| M2.4 session history dir + `prompt.md` byte-match | **CONFORMS** (live, **after fix**) | `sessions/NNN/` holds prompt/transcript/verdict/handover/cost + `INDEX.md`; `sessions/001/prompt.md` is **byte-identical** to the compiled `logs/session-001.prompt.md`. **`transcript.md` was missing** (design deviation) — added this session (see M9.1). |
| M2.5 accurate cost/tokens incl. overhead split | **CONFORMS** (live) | `cost.json` carries `costUsd`, `overheadCostUsd` (gate/advisor split), token breakdown, `commits`. |

## M3 — Workflows that bend

| # | Verdict | Evidence |
|---|---|---|
| M3.1 declarative steps + 4 built-ins | **CONFORMS** (live) | Toy run under the default `deliver-verify` logged `workflow 'deliver-verify': step 0 → 1 (verify, kind=Verify)` and progressed Deliver→Verify→Fix. `WorkflowEngine` ships the 4 built-ins. |
| M3.2 per-stage/session overrides | **CONFORMS** (test) | `WorkflowOverrides` + `ApplyStageOverrides`; model shows in the spawned command line (`pids`) — tests pass. |
| M3.3 safe parallelism by path claims | **CONFORMS** (test) | `PathClaimTracker` + `LaneCoordinator` conflict check; 5 path-claim tests. |

## M4 — Gates that cannot be escaped

| # | Verdict | Evidence |
|---|---|---|
| M4.1 claims vs confirmations | **CONFORMS** (live) | **Headline result.** The fake agent hand-edited the tracker row `T0.1` to DONE; the engine logged `WARNING: 1 checkpoint(s) marked DONE via direct tracker edit … discarded` and confirmed `newly DONE []` — **zero** checkpoints advanced. This is the exact M4.1 truth gate, met on a live run. |
| M4.2 gate caching that hits | **CONFORMS** (live) | On the fix session, `gate smoke: CACHED (0s)` — the `(gate, sha, tier)` cache hit live. |
| M4.3 verifier findings → retry prompt | **CONFORMS** (test) | `Verifier.Parse`: bad delivery <80 with findings, good passes, malformed → null; 6 tests. |

## M5 — Observability and the Face

| # | Verdict | Evidence |
|---|---|---|
| M5.1 timeline pane | **CONFORMS** (test) | `GET /timeline` wire test + face-go `timeline_modal` golden. |
| M5.2 live plan pane, no truncation | **CONFORMS** (test) | Per-stage score/attempts/cost in sidebar; `sidebar_open` golden. |
| M5.3 native console (raw stdout SSE) | **CONFORMS** (test) | `GET /console/current` SSE + `console_modal` golden; previously dogfooded against a real stream. |
| M5.4 live ticker from `tokenDelta` | **CONFORMS** (test) | `WithLiveSessionMetrics` fold; 2 wire tests (live fold + no double-count). |
| M5.5 compiled-prompt preview | **CONFORMS** (test) | `GET /prompt/preview` wire tests + `prompt_preview` golden. |
| M5.6 one-verdict `conductor status` <1s | **CONFORMS** (live) | Ran live against the real `run.db`: **514 ms** total (69 ms DB read), verdict + per-stage table, no `state.json` read. |

## M6 — Plan authoring

| # | Verdict | Evidence |
|---|---|---|
| M6.1 `plan import` with model choice | **CONFORMS** (live) | `conductor plan import docs/history/MAESTRO-PLAN.md` parsed **structurally (no model call) → 9 stages, ids exactly M1…M9** with the correct dependency chain and session counts. |
| M6.2 re-import diffs, never clobbers | **CONFORMS** (live) | Same import reported `9 stage(s) added, 0 changed` in a diff table, then bumped the plan to v2. |
| M6.3 edit plan from the TUI | **CONFORMS** (test) | `POST /plan/edit` + face-go plan editor; 5 wire tests + goldens. |

## M7 — Knowledge that compounds

| # | Verdict | Evidence |
|---|---|---|
| M7.1 ledger injected + surfaced + queryable | **CONFORMS** (live) | The toy `prompt.md` on disk contains the injected Conductor tools contract (`conductor note`, `bg`, `task`). `LedgerBattery` adds ledger rows first so the byte cap never drops them; `GET /ledger` + face-go `k` tab; `ledger_list` MCP. |
| M7.2 `bug new/list/fix` + MCP, bugs outlive session | **CONFORMS** (test + live) | `bugs` table + `BugsBattery`; the M8 workflow-index bug was filed for real as bug #1. Wire + store tests. |
| M7.3 structured handovers | **CONFORMS** (live) | `handover.md` written per session from `GetLatestHandover`; present in every toy session dir. (Rich content depends on the real agent; the mechanism conforms.) |

## M8 — AFK and smart setup

| # | Verdict | Evidence |
|---|---|---|
| M8.1 `conductor doctor` <2s | **CONFORMS** (live) | Ran live repeatedly: **296–922 ms**, always <2 s. Correctly reports agent CLI, git branch/dirty, face-go binary, DNS/disk/API, budget, Telegram — and exactly what is missing. |
| M8.2 `conductor init` scaffold + repo detection | **CONFORMS** (live, **built this session**) | Was the audit's clearest DEVIATE — M8 shipped Telegram under M8.2 instead. `conductor init` now detects dotnet/go/rust/node/python from a root marker, wires matching build+test gates, drops editable `session.md`/`fix.md`, self-checks the scaffold loads. Verified live: correct detection across three repo types; the dotnet scaffold passes `conductor doctor` (7 ok). |
| M8.3 Telegram v2 driven end-to-end from the phone | **DEVIATES** | Backend (`SecretsStore`, `TestConnectionAsync`, `/telegram/*`) and the face-go guided-setup tab are built and wire-tested. The truth gate — *a toy run driven to completion from the phone, lid closed* — is **not met**: it needs the owner's real bot token (a `HUMAN:` item). No credential-free way to reproduce it. |

## M9 — Dogfood close

| # | Verdict | Evidence |
|---|---|---|
| M9.1 run a real plan end-to-end; fix what bleeds | **CONFORMS** (live) | A toy plan was driven end-to-end through the branch binary and **four real defects bled out and were fixed** (below). *Caveat:* driven by `fake-agent.ps1`, not the real DeepSeek/opencode model — a full real-model run is the owner's paid dogfood and remains theirs to run, as the design doc always intended. The engine path itself was fully exercised. |
| M9.2 final audit, every checkpoint rated | **CONFORMS** (live) | This document. Truth gates re-run live where credential-free. |

### What bled in M9.1 (all fixed this session)

1. **Ratchet gate was RED** at the M8 close-out that reported it green — analyzer suppressions sat at
   40 vs the ceiling of 38. Fixed honestly (the gate forbids raising the ceiling) by removing two: a
   dead class-level `MA0045` on `Orchestrator.cs` (zero blocking calls remain post-split) and
   converting `DoctorCommand` to a Spectre `AsyncCommand`. Commit `4b1e2e7`.
2. **`tools/fake-agent.ps1` failed to PARSE under Windows PowerShell 5.1** — two em-dashes made the
   BOM-less UTF-8 script decode as ANSI and tear a string literal, so the smoke harness never ran a
   single session. Now ASCII-only (matching `ratchet.ps1`'s documented discipline). Commit `4b1e2e7`.
   *The engine handled the broken agent correctly* — gate battery ran, cache hit, circuit breaker
   fired on the identical ×2 failure, escalated to NEEDS HUMAN, honoured `--max-sessions`.
3. **M2.4 deviation:** `transcript.md` — listed in the design doc but never written to the session
   history dir. `RunLoop.RenderTranscript` now folds the raw agent NDJSON into readable markdown
   there; unparseable lines are preserved verbatim. Commit `4b1e2e7`.
4. **Prompt glitch:** the session template rendered `exactly as `` prescribes` (empty backticks) for
   any plan without a `planDoc`. `{planDoc}` now falls back to the tracker. Commit `fba0fe2`.

Plus a stale `doctor` `--help` description (still described the pre-M8.1 resume preview) — fixed with
the `init` commit `baceb4a`.

---

## Open items for the owner (both credential-gated `HUMAN:`)

1. **M8.3 — live Telegram phone dogfood.** Paste a real bot token into the Face's Telegram tab, add a
   real chat id, hit Test, confirm a message arrives, then drive a toy run from the phone watching
   session-end pushes / NeedsHuman buttons / reply-to-inject / `/status`. Everything up to the real
   token is built and tested.
2. **M9.1 — full real-model run.** `conductor run -p plans/conductor-maestro.plan.json` with the real
   DeepSeek/opencode agent, to completion. The engine, gates, escalation, history, and cost accounting
   are all exercised and green under the fake agent; this is the paid confirmation on the real model.

Neither blocks release of the engine itself: the tool builds clean (0w/0e), the full C# suite is green
(704 tests), the anti-cheat ratchet is green, face-go is green, and the dogfood loop drives a plan
end-to-end with correct claims-vs-confirmations, gate caching, and human escalation.
