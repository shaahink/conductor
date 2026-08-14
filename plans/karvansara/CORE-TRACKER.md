# Karvansara core - the open door Phase Tracker

**Plan:** Karvansara core - the open door | **Branch:** `feat/karvansara` | **Design doc:** docs/dev/KARVANSARA-PLAN-2026-08-13.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: the KS5 lane landed whole (a157e40..2ba91d7): KS5.1-KS5.4 delivered and adversarially
  verified (KS5.4 through three rounds), rebased onto the KS2a merge, fast-forwarded; battery on
  the merged tree 2436/2436 + face build/vet/test green. KS2.0-KS2.6 landed just before (e6b80c2).
gaps for KS10.1's closure ledger: the face tokens-cap row still quotes the plan-file ceiling;
  approve lost CtlCommand's --yes/--force; one owner-gate-plus-lowered-cap path spends a session
  before parking.
measured: concurrent full suites on this machine kill each other's MSBuild nodes: run batteries
  serially with -nodeReuse:false -p:UseSharedCompilation=false, or MSB4166 lies about the tree.
in flight: lane/ks3 holds KS3.1-KS3.3 verified; KS3.4 refuted seven rounds, each a new face of one
  defect - the preflight compose leg diverges from the live launch (latest: three "no session
  composes" headlines that a red gate or audit re-decides into a real session). KS3.5 waits behind
  it; the lane is paused for an owner decision - fix round 8 or re-scope the clause.
next: settle KS3.4, KS3.5, merge lane/ks3; wave 3 = KS9, wave 4 = KS10.1/10.2, KS10.3 owner-only.


## Baseline numbers (tracker-counted - the engine is stopped, the tracker is the board)

| Metric | Value |
|---|---|
| Total checkpoints | 32 |
| Done | 21 |
| Claimed (unconfirmed) | 0 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### KS0 — Leftovers - the catalogue stops corrupting itself

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS0.1 | Legacy-db import dedups by run id - never plan slug - consults imported.json before importing, and a repair pass with a backup collapses the existing duplicates, leaving one row per real run and the payesh evidence path green on the deduped store | DONE | 4ae0cf5 | .conductor/evidence/KS0/ks0-1-catalogue-repair.md |
| KS0.2 | conductor run close and adopt verbs close or annotate a run record with provenance through the store, an honest status writer covers non-terminal parks, and the four phantom running rows are closed via the verb - the WATCH-HANDOFF hand-SQL procedure retired | DONE | 15627b9 | .conductor/evidence/KS0/ks0-2-run-close.md |
| KS0.3 | The sharp-small batch goes red to green by reproduction script: the gate battery builds to a shadow path and never rebuilds the running engine, CWD beats the CONDUCTOR_PLAN env var with a warning on override, the fresh-run.db first-write FK error dies, and lessons.md stops duplicate-appending with a pinned test | DONE | eb9778e | .conductor/evidence/KS0/ks0-3-sharp-small-batch.md |

### KS1 — Truth - every read surface reconciles

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS1.1 | A plan reload updates the run row, and limits provenance is labeled at-launch versus now - a mid-run limits edit shows both at the same boundary in history, with a test asserting the UPDATE | DONE | 6666431 | .conductor/evidence/KS1/ks1-1.md |
| KS1.2 | Stage rows derive from the event fold and stages side-table reads are retired, derived status matching the status surface for all archived runs, with an architecture test forbidding readers of stages.session_count | DONE | 98f8ba9 | .conductor/evidence/KS1/ks1-2.md |
| KS1.3 | history, the fleet list and json output reconcile liveness at render time - a killed engine's run never lists as running, and the json carries the reconciled status for the evidence pipeline to quote | DONE | f0d50ff | .conductor/evidence/KS1/ks1-3.md |
| KS1.4 | Doctor gains the plan-semantics lints - gate-command path probe, checkpoint-id versus tracker cross-check, hook dry-run, plan drift, composed-prompt argv-length, brace sweep, escalation-token sweep - and goes red on each of seven seeded trap plans | DONE | ff3bd86 | .conductor/evidence/KS1/ks1-4.md |
| KS1.5 | The ARCHITECTURE.md rollback paragraph matches ControlDispatcher's actual reset and force semantics, covered by a docs-match-reality test | DONE | d156895 | .conductor/evidence/KS1/ks1-5.md |
| KS1.6 | The invariant is an architecture test: readers outside the engine may not consume mutable snapshot columns that have a fold-derived equivalent - green on the tree, red on a seeded violation | DONE | 4798173 | .conductor/evidence/KS1/ks1-6.md |

### KS2 — The open door - bare conductor is the app, and every section reads

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS2.1 | Bare conductor on a TTY opens the hub - recent runs reconciled, plans discovered, attach start plan-new history - non-TTY prints a status board with exit 0, and every existing verb is unchanged | DONE | 8c137b4 | .conductor/evidence/KS2/ks2-1.md |
| KS2.2 | The archive serves: the Face attaches to finished runs read-only - sessions, money, timeline and report render with no engine process for that run | DONE | a56bae4 | .conductor/evidence/KS2/ks2-2.md |
| KS2.3 | A run starts from the hub: choose plan, journey preview, detached engine launch with stderr redirected, then attach - killing the Face leaves the engine alive | DONE | 90fda68 | .conductor/evidence/KS2/ks2-3.md |
| KS2.4 | One picker merges fleet probe and catalogue - live runs attach, past runs open read-only, across repos, write tokens never crossing runs | DONE | 2d05043 | .conductor/evidence/KS2/ks2-4.md |
| KS2.5 | conductor status with no resolvable plan prints a machine-level board - the multiple-plan-files error is unreachable | DONE | 7fe1376 | .conductor/evidence/KS2/ks2-5.md |
| KS2.6 | A park emits once: notifier rate-limited with a max per incident, dry-run never notifies, a monitor listing verb exists, and the 2026-08-02 incident replay produces exactly one notification | DONE | e6b80c2 | .conductor/evidence/KS2/ks2-6.md |
| KS2.7 | Long text scrolls everywhere: Agent console and transcript, Kanban detail, History, Telegram and Processes each own a pane viewport, the last hand-rolled scroll integers are deleted, and glitch-sweep proves a 500-line body scrolls to its end in every tab | DONE | 82e597f | .conductor/evidence/KS2/ks2-7.md |
| KS2.8 | The reader: one full-screen overlay opens any truncated cell or row with soft wrap, pager keys, percent readout and themed markdown - a 2000-line report and a 300-char kanban note both readable to the last line at 80x24 | DONE | d13184a | .conductor/evidence/KS2/ks2-8.md |

### KS3 — Authoring - no human writes JSON

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS3.1 | conductor plan new interviews from an idea, PRD or tracker and emits plan JSON, tracker and templates doctor-clean by construction - from an empty repo, one command, zero-fail, the JSON never opened in an editor | TODO | - | - |
| KS3.2 | The editor stops destroying: comment header preserved across plan set, add-stage and import, no silent progress-kind or gate-timeout rewrites - the add-a-stage replay diffs to only the stage | TODO | - | - |
| KS3.3 | Schema honesty: the eight undocumented keys documented, mutatingLanes removed or wired, doctor warns on inert keys, and plan-config.md matches PlanConfig under the docs-match-reality pin | TODO | - | - |
| KS3.4 | conductor preflight runs the launch drill as one verb - doctor, journey, dry-run compose, version-versus-release, rebuild check, escalation-block check - one verdict, each seeded drill failure caught | TODO | - | - |
| KS3.5 | Import bridges: a spec-kit tasks.md, a Task-Master tasks.json and a plain markdown checklist each convert to a plan, and the spec-kit sample drives conductor demo to completion | TODO | - | - |

### KS5 — Spend - every dollar the tool can spend is governed

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS5.1 | A machine-wide ledger verb answers what this machine spent this week and month, billed-only, across the catalogue, cross-checked against per-run money with no price table in the diff | DONE | a157e40 | .conductor/evidence/KS5/ks5-1.md |
| KS5.2 | Every spawned model process writes a costs row - lanes, advisor, supervisor - caps see them, and an architecture test holds the rule that any process-spawning path taking a model writes a costs row | DONE | 0e7e63a | .conductor/evidence/KS5/ks5-2.md |
| KS5.3 | BudgetAnalyzer prescriptions surface at plan-reload, logging any ceiling that contradicts the measured floor at the boundary | DONE | 1aa80cb | .conductor/evidence/KS5/ks5-3.md |
| KS5.4 | approve on a budget park raises the ceiling explicitly with the amount stated instead of resetting the counter, and the cap check runs after the queued reload applies - the 2026-07-29 replay shows no silent double-spend | DONE | f73fd68 | .conductor/evidence/KS5/ks5-4.md |

### KS9 — The far door - GitHub is the remotest view

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS9.1 | SecretsStore gains the GitHub token field with the env override, a raw-HttpClient client lands on the ReleaseClient pattern, and github sync --backfill posts a finished run's board and diary to a scratch repo - re-running mints zero duplicates, off by default, nothing inbound | TODO | - | - |
| KS9.2 | The live mirror reconciles over ReadEventsAfter - batched, network-failure-proof, cursor-resumable - a mid-run network kill leaves the run unharmed and the board converges on reconnect with zero duplicates | TODO | - | - |
| KS9.3 | Projects v2 board via GraphQL mirrors stage status - or, without the one-time project-scope grant, reports the precise refusal and stays SKIPPED rather than half-done | TODO | - | - |

### KS10 — Ship core

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS10.1 | The internal record reconciles: ARCHITECTURE.md and docs/dev match the engine for everything this plan changed, the closure ledger names every bug and followup row closed here or its living owner, and conductor budget's re-measure is written into TOKEN-BUDGET-TUNING for edge to compile against | TODO | - | - |
| KS10.2 | The published surface reconciles and is pinned: README, the docs user set and its index, .github templates where a verb changed, and the Unreleased CHANGELOG section written as the release body - conductor --help lists no verb absent from cli.md, every README command block executes as written, SF7_1DocsMatchRealityTests goes red on a seeded stale doc, and payesh's harvest is green on the deduped store with its PR open or its refusal recorded | TODO | - | - |
| KS10.3 | Owner-only: feat/karvansara merges to master, the release tags through the pipeline with KS10.2's section as its body, the reinstalled version matches the releases page, this run's own board backfills to GitHub - the first real use of KS9 - and the payesh PR merges | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
