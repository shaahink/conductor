# Next-Era Direction — Verified Audit + Plan

**2026-08-07.** This document does three things: (A) verifies every falsifiable claim in the
synthesized audit report (`CONDUCTORNEXTERAAUDITREPORT.md`, 2026-08-06) against the code at
`C:\code\conductor` (branch `feat/karvan`, read-only — the live run was not touched); (B) records
the corrections, several of which change what the next era should build; (C) states the next-era
plan — direction and strength — ready to be turned into a conductor plan in the next step.
Naming is deferred by owner decision and appears here only as a parked item.

External landscape claims (Melty Labs, microsoft/conductor, star counts, product shutdowns,
spec-kit internals) were **not** re-researched per instruction; they are carried as reported and
flagged as such. Everything code-shaped was verified directly.

---

## Part A — Verification verdicts

| # | Report claim | Verdict | Evidence |
|---|---|---|---|
| 1 | `runs.limits_json` written only by `InitializeRun`; `ApplyPlanReload` never updates the run row | **CONFIRMED, with a correction that narrows it** | Writer: `SqliteRunStore.Sessions.cs:19-35`. Reload path swaps plan into engine, gates, lanes, dispatcher, work graph — no store UPDATE anywhere in `RunLoop.Reload.cs:49-111`. **Correction:** `InitializeRun` is `ON CONFLICT DO UPDATE` and runs on *every* process start including resume, deliberately refreshing limits (K3.3 comment, `Sessions.cs:13-18`). So the run row lies only for the remaining lifetime of the current engine process after a mid-run edit; the next resume heals it. Real bug, smaller blast radius than the report implies. Per-session `limits` snapshots exist (`Sessions.cs:70,95`), so the "now" line can already be derived. |
| 2 | Stages side-table diverges; `session_count` has no writer | **CONFIRMED, worse than reported** | Column declared `v1_init.sql:24`, default 0. Only writers of `stages` are `InitializeStage` (`INSERT OR REPLACE`, status `in_progress` — `Sessions.cs:47-53`) and `ConfirmStage` (status `done` — `Sessions.cs:55-60`). No writer of `session_count` exists anywhere. `RunArchive.Stages()` reads the dead column raw (`RunArchive.cs:285-292`). **Additional finding:** `INSERT OR REPLACE` in `InitializeStage` re-zeroes `session_count`/`confirmed_utc` and rewrites `started_utc` on stage re-entry — the same snapshot-lie class, one more instance. Note the runs listing already applies the correct medicine — `session_count` computed live via `COUNT(*)` (`RunArchive.cs:132`) — so the fix pattern is proven in the same file. |
| 3 | Immortal "running" record; "read side never consults `EngineLock.IsHeldByLiveEngine`" | **CONFIRMED for `history`; the "never consults" claim is WRONG** | `RecordRunEnd` has exactly two call sites: abort (`RunLoop.cs:108`) and completion (`VerdictEngine.Completion.cs:59`). But `conductor status` already reconciles: pid liveness via `RunHasLiveProcess` + `EngineLock.IsHeldByLiveEngine` (`StatusReportBuilder.cs:98-113`), and the watch loop uses it too (`WatchLoop.cs:88`). The unreconciled surface is the **archive listing**: `RunArchive.Runs()` returns raw `r.status` (`RunArchive.cs:129-137`) and `HistoryCommand` just colors it (`HistoryCommand.cs:348-353`). Also: this is a *known, deliberate* design — "run outlives engine" (SC24 tests), and the 8th-audit handover ruled every non-terminal exit path correct and deferred the status gap as **D11 / FU-F1-06, low priority** (`.conductor/handovers/F1.md:512-519,572`). The next era should treat it as "close a deferred item on the listing surfaces", not "discover a bug". |
| 4 | ARCHITECTURE.md rollback claim contradicts code | **CONFIRMED verbatim** | Doc: "`rollback` squashes bookkeeping commits; it does **not** revert code" (`ARCHITECTURE.md:113`). Code: `Git.Exec(_plan.Repo, "reset", "--hard", sha)` to the stage-start head, gated by dirty-tree check + `--force`, emits `RollbackExecuted` (`ControlDispatcher.cs:173-195`). The doc is the thing that's wrong. |
| 5 | Doctor validates "~4 of the 32 rules"; surface is thin | **MISLEADING — reframed** | Doctor is broad, not thin: 62 `Check` constructions across three partials, including per-stage gate coverage (`DoctorCommand.cs:331-343`), checkpoint declarations, token floor vs configured ceiling (`DoctorCommand.State.cs:77`), auth ping, update feed. The *genuine* gap is narrower and specific: no gate-command dry-run/path probe, no checkpoint-id pattern lint against the tracker, no hook dry-run, no plan-editor drift lint. The "32-rule inventory" is an artifact of the interrupted session and was not recoverable; the concrete lint list stands on its own. |
| 6 | Budget machinery claims (BudgetAnalyzer pure/static, measured-not-configured; MoneyAnalyzer billed-only; no price table; 98%+ cache reads) | **ALL CONFIRMED** | `BudgetAnalyzer.cs:6-18` (K4.2, pure/static, ceilings from `SoftBreakRequested` events, floor from smallest checkpoint-closing session); `MoneyAnalyzer.cs:11-13` ("Money comes from what the provider billed, never from a price table"); `LiveCostEstimator.cs:22-25` (deliberately no price table, three named bases); 98.3% cache reads at `TOKEN-BUDGET-TUNING.md:282`. **Correction to the report's P1:** "surface BudgetAnalyzer into doctor" is *already done* — doctor, the verb, and the tests call the same function by design (`BudgetAnalyzer.cs:15-16`). What remains is surfacing prescriptions at **plan reload** time. |
| 7 | Ratchet + architecture tests + warnings-as-errors | **CONFIRMED** | `tools/gates/ratchet.ps1` + `ratchet-baseline.json`; `tests/Conductor.Tests/architecture-baseline.json`; `TreatWarningsAsErrors` + `CodeAnalysisTreatWarningsAsErrors` (`Directory.Build.props:22-23`). |
| 8 | 13 control verbs | **CONFIRMED** | `Progress.Control.cs:3-21` — exactly 13 enum members. |
| 9 | Partial-class counts | **MINOR CORRECTIONS** | VerdictEngine 8 ✓, SessionRunner 6 ✓, SqliteRunStore 7 ✓; RunLoop is **6** (not 5), ControlPlaneServer is **12** (not 11). |
| 10 | CV/budget evidence alignment | **CONFIRMED, one new risk found** | The evidence base is `C:\code\cv\career\token-budget-evidence.md` (2026-08-06), built from `conductor history --json` across all 18 stores. Because `history --json` exports the unreconciled `runs.status` (claim 3), a dead-engine run exports as `running` — the truthfulness bug **leaks directly into the CV evidence pipeline**. This tightens the report's "fix these first" argument from principle to necessity. |
| 11 | Competitive landscape, spec-kit externals, name collision | **UNVERIFIED-EXTERNAL** | Carried as reported. Nothing in the next plan depends on their precision; the naming decision is parked anyway. |

**Net assessment of the synthesized report:** directionally sound and safe to build on. Its three
"bugs" are one real class (snapshot/side-table lies vs. event-fold truth) — that framing survives
verification fully. Its errors are errors of *novelty and size*: bug 3 was already found, ruled on,
and deferred in-repo; bug 1 self-heals on resume; doctor is broader than credited. None of the
corrections reverse a recommendation; several re-rank them.

---

## Part B — Corrections that change the plan

1. **Bug 3 is a listing-surface fix, not an engine fix.** `status` and `watch` already reconcile.
   Scope the work to `RunArchive.Runs()` / `history` (and any Face fleet list that reads the same
   rows): render-time reconciliation (`stale (engine gone)`) using the existing `EngineLock` +
   pid-probe pattern. The DB stays immutable; D11/FU-F1-06 gets closed rather than re-litigated.
   The `--json` export must carry the reconciled status too — that is what the CV evidence reads.
2. **Bug 1's fix is one UPDATE plus one label.** The reload path gains a store write (same row
   `ON CONFLICT` update `InitializeRun` already does), and the history/status surfaces label
   provenance "at launch" vs "now" (now = last session snapshot, already recorded). Small.
3. **Bug 2's fix has an in-file template.** Derive stage rows the way `Checkpoints()` already
   derives checkpoint rows from the event fold (`RunArchive.cs:294-302`) and the way `Runs()`
   already counts sessions. Retire reads of the `stages` side-table; the table can stay for old
   DBs, unread. This also fixes skip/goto/retry stages showing stale `in_progress` forever.
4. **Doctor work is a lint list, not a rewrite.** Gate-command path probe/dry-run, checkpoint-id
   pattern vs tracker, hook dry-run, and a plan-drift lint (the plan editor silently rewriting
   progress kind / gate timeouts / comment headers is an already-paid-for burn). Drop the
   "4 of 32" framing.
5. **Budget epic shrinks.** Doctor already prescribes. Remaining: machine-wide/multi-run spend
   ledger, lane spend inside the cap, and prescriptions surfaced at plan-reload time.

---

## Part C — The next-era plan (direction and strength)

### Thesis

> **The era of the truthful read side.** The engine's write side never believes the agent; this
> era makes every read surface equally sceptical. Then it extends the cap to every dollar the tool
> can spend, and converts code hygiene into permanent design assets. No new feature surface until
> the listing surfaces cannot lie — because the CV, the site articles, and the money verbs all
> quote them.

The ordering rule the report proposed survives verification and is adopted: **credibility before
capability.** The one addition from verification: the truthfulness epic is now also a *pipeline*
fix (history --json → cv evidence → articles), which is why it is first and indivisible.

### Epic 1 — Truth (P0)

The architectural invariant, stated once and enforced: **the event fold is truth; every
side-table and snapshot column is a view; every external reader folds or reconciles.**

| Checkpoint | Work | Falsifiable exit |
|---|---|---|
| T1 | Reload updates the run row; provenance labeled "at launch / now" | Edit limits mid-run → `history` shows both, same boundary; test asserts the UPDATE |
| T2 | Stage rows derived from the fold; side-table reads retired | For all 18 archived runs, derived stage status matches the status surface; no reader of `stages.session_count` remains (architecture test forbids it) |
| T3 | Render-time liveness reconciliation in `history` + fleet list + `--json` | A killed engine's run never lists as `running`; `--json` carries the reconciled status; D11/FU-F1-06 closed |
| T4 | Doctor plan-semantics lints: gate path probe, checkpoint-id pattern, hook dry-run, plan-drift | Doctor fails/warns on a plan with an unresolvable gate cmd, a checkpoint id absent from the tracker, a drifted plan file |
| T5 | ARCHITECTURE.md rollback paragraph rewritten to match `ControlDispatcher` (danger stated, `--force` semantics documented) | Doc and code agree; SF7-class docs-match-reality test covers the claim |
| T6 | The invariant as a test | New ArchitectureBoundaryTests rule: readers outside the engine may not consume mutable snapshot columns that have a fold-derived equivalent |

Estimated 6 checkpoints, ~2 stages. This epic is deliberately boring; that is its value.

### Epic 2 — Spend governance (P1)

Close the last gap between "the run has a cap" and "the machine has a cap".

| Checkpoint | Work | Falsifiable exit |
|---|---|---|
| S1 | Machine-wide spend ledger (all runs, all repos, billed-only — same one-rule as MoneyAnalyzer) | One verb answers "what did this machine spend this week", cross-checked against per-run `money` totals |
| S2 | Lane spend inside the cap | A parallel/mutating lane's tokens count against a ceiling somewhere; no spend path is uncounted |
| S3 | BudgetAnalyzer prescriptions at plan-reload | Reloading a plan whose ceiling contradicts the measured floor/prescription logs the disagreement at the boundary |

Estimated 3–4 checkpoints, 1 stage. Keep the no-price-table rule absolute; the ledger sums billed
rows, it never prices.

### Epic 3 — Quality lane: hygiene that buys design (P2)

Thicken the existing referee; do not add a parallel one. The governing rule (adopted verbatim from
the report — it survived scrutiny and is the marketable idea):

> Every hygiene checkpoint must buy one permanent design asset — a new architecture-test rule, a
> complexity-budget decrement, or a mutant-killing test. Diagnostic count is the fuel gauge; the
> growing architecture suite is the odometer.

| Checkpoint | Work |
|---|---|
| Q1 | Curated Roslynator set (~25 design-shaped rules) as errors; everything else off — compiler as baseline, no SARIF diffing |
| Q2 | Analyzer-debt count ratchet, extending `ratchet.ps1` semantics (referee not editable by the agent) |
| Q3 | Complexity budgets (CA1502/1505/1506) with ratchets; first targets are the largest partial surfaces (VerdictEngine 8 files, ControlPlaneServer 12) |
| Q4 | Extract the pure "evidence → verdict" function out of VerdictEngine — the one deep refactor this era funds, because it makes the taxonomy testable without the loop and is the showpiece refactor for the CV |
| Q5 | Era-boundary Stryker.NET, git-diff-scoped only |

Estimated 5 checkpoints, 1–2 stages. Q4 is the only structural change; Q1–Q3 are the fence built
around it before it happens.

### Epic 4 — Spec-kit bridge (P3, optional, cut first)

Importer only: tasks.md phases → stages, story checkpoints → checkpoints, constitution →
promptExtra. The pitch is "the SDD execution layer — import a spec-kit project and run it under
real gates, budgets, and resumability." 3 checkpoints. Own planning craft unchanged — two rituals
adopted without tooling: a named clarify step before freezing an era plan, and a plan-lint
cross-consistency pass (T4 partially delivers this anyway).

### Parked (explicitly not this era)

- **Naming / public rename** — owner-deferred to next stage. The Sarban/Karvan lineage remains
  the leading candidate; nothing in this era hard-codes the public name deeper.
- HMAC-signed run receipts, cross-vendor review gate — steal-list items; revisit only if capacity
  remains after Epic 3.
- Any new feature surface not listed above.

### Strength — budget prescription (measured, not guessed)

Grounded in `TOKEN-BUDGET-TUNING.md` + `cv/career/token-budget-evidence.md`:

- **Ceiling 32M / nudge 0.7** — the karvan-core configuration: 0 rollovers in 26 sessions and the
  cheapest per-checkpoint of any conductor run (15.5M/checkpoint). Nothing in this era's work is
  heavier than Karvan's; keep it.
- **Scale estimate:** 17–20 checkpoints ≈ 260–310M tokens at the measured 15.5M/checkpoint,
  ≈ **$195–$230** at the measured blended $0.74/M (98.3% cache reads). Epics 1+2 alone
  (the must-do core): 9–10 checkpoints, ≈ $105–$115.
- **Sequencing:** Epic 1 → Epic 2 → Epic 3, single run, phase gates between epics; Epic 4 only if
  the era is ahead of budget at the Epic 3 boundary. Epic 1 is indivisible — no partial credit,
  because the CV pipeline quotes its surfaces.
- **Era gate battery:** existing gates + ratchet; Q2's analyzer ratchet arms mid-era and applies
  to the era's own remaining sessions — the tool tightening its own referee while running is
  itself CV material.

### What this era proves (the CV sentence for each epic)

1. *Truth:* "Every surface the tool prints is reconciled against process liveness or derived from
   an event fold — the orchestrator that never believes the agent now never believes a cache."
2. *Spend:* "Hard dollar governance at machine scope, derived from billed reality, with no price
   table anywhere in the codebase."
3. *Quality:* "Anti-cheat quality ratchets where every hygiene pass must purchase a permanent
   design asset — enforced on the tool, by the tool, while it builds itself."

---

*Verification method: targeted code inspection of the live checkout (read-only), branch
`feat/karvan`. All file:line references resolve at time of writing. External-landscape claims
carried unverified by instruction. Next step when ready: fold Part C into a conductor plan +
tracker via the normal era-planning craft — clarify step first.*
