# Karvan core - the engine knows what it did and what it cost Phase Tracker

**Plan:** Karvan core - the engine knows what it did and what it cost | **Branch:** `feat/karvan` | **Design doc:** docs/history/CONDUCTOR-KARVAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: **K1.3 claimed** (`890ac38`, `58df0bc`, `44c17a0`, `6acea2c`). (1) `tokens_think` was LABELLED,
  not dropped — measured why: `OpencodeProvider.cs:104` really folds `tokens.reasoning`, so
  `IAgentProvider.ReportsReasoningTokens` decides, `/sessions` serves explicit `null` for a provider
  that has none, and the Face renders `n/a` + a cause note. (2) `lessons.md` is deduped one-line
  rules; the SF7-38 duplicate came from `TrimToCap` re-emitting the entry it had just prepended.
  (3) `go mod tidy` + `module_intent_test.go`, which fails the build if go.mod misdescribes imports.
next: **K1.4** — the MCP config must MERGE the operator's own servers instead of replacing them
  (engine-side half of the bug filed in SF7.1); keep the prompt-side deferred-tool fallback.
notes: **never point the fresh build at this repo's `.conductor`** — schema v10 here, v9 published.
  A `/sessions` test whose plan declares no `Agent` now resolves to the text provider, so
  `tokensThink` serves null: declare an opencode agent if you need the number.
red: **push feat/karvan first** — DNS died on this machine at session end ("could not resolve
  host: github.com", 4 tries); all 5 commits through `07cdfe2` are local, tree clean, claim in
  run.db. Suites green: 231/231 engine (scoped), 6/6 face packages.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 25 |
| Done | 0 |
| Claimed (unconfirmed) | 2 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### K1 — The ledger stops lying

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K1.1 | A rolled-over session records the commits and claims it actually made, proven by a harness session driven to its ceiling, with a rollover still consuming no attempt and still not running the phase gate | DONE | 93bbae5 | .conductor/evidence/K1/K1.1-rollover-records-facts.md |
| K1.2 | The soft break is re-stated until it is obeyed, names the actual remaining budget, states the wrap-up order (claim first, handoff second), and the session record says whether it was delivered, re-delivered and obeyed | DONE | 93bbae5 | .conductor/evidence/K1/K1.2-soft-break-restated-and-measured.md |
| K1.3 | Three small untruths die as a class — the thinking-token column that is zero on all 125 rows, the lessons file that is a diary and repeats one entry twice, and a go.mod that calls a directly-imported package indirect while carrying two lipgloss majors | TODO | - | - |
| K1.4 | A spawned session sees conductor's task tools and the operator's own MCP servers, because the config merges instead of replacing, with the prompt-side deferred-tool fallback kept | TODO | - | - |

### K2 — The architecture becomes navigable

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K2.1 | Conductor.Core holds the domain, orchestration and store with no Spectre and no HTTP hosting, Conductor is CLI plus hosting, the reference direction only points one way, and publish plus doctor plus the Face's discovery still work | TODO | - | - |
| K2.2 | Architecture tests in the ordinary suite fail the build when a boundary is crossed, each naming the offending type and the rule, landed together with K2.1 so the extraction is verified rather than asserted | TODO | - | - |
| K2.3 | The worst partial-file piles are split by responsibility — the thirty-file DTO pile becomes per-feature endpoint contracts — and one written file-organisation convention says where a new endpoint, event or partial belongs | TODO | - | - |
| K2.4 | The front door reads: a real ARCHITECTURE.md map, an AGENTS.md cut to current state with superseded handoffs archived and indexed, closed-era trackers out of the repo root, the divergent duplicate workgraph doc resolved to one file, and the docs indexes updated | TODO | - | - |

### K3 — Conductor remembers

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K3.1 | State has a machine-level home with one catalogue keyed by repo and plan, an environment override, an idempotent migration that imports existing run.db files rather than orphaning them, and a per-run scratch dir that keeps the repo's tracked deliverables | TODO | - | - |
| K3.2 | conductor history lists and opens past runs read-only from the catalogue, and the Face's existing run picker offers them | TODO | - | - |
| K3.3 | Every run records the engine version, its commit, its dirty flag and a snapshot of the limits that governed it, and a dirty build warns at launch | TODO | - | - |

### K4 — Token truth - measure it before shrinking it

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K4.1 | The engine records context size per turn — a high-water and a mean per session — derived from the stream, with the derivation checked against a session that can be estimated independently | TODO | - | - |
| K4.2 | conductor budget prints floor, wrap-up, cap, nudge-versus-floor and rollover rate and prescribes a correction, and it reproduces this repo's own two runs without being told the answers | TODO | - | - |
| K4.3 | conductor money answers what a project cost per checkpoint, per stage and per month, with cache-read share and the before-and-after windows that say what the cap bought, cross-checked against a hand-written query | TODO | - | - |
| K4.4 | Live session tokens, the distance to the nudge, a burn rate and a projection sit beside live money in the Face and on the wire, honest when no cap is set | TODO | - | - |

### K5 — The result contract and the channels

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K5.1 | The session result has one format conductor owns — short headline, at most three outcome bullets, artefacts as links, evidence paths, explicit gaps — with the followup parser, the verdict parse and the session template moving in the same checkpoint and a legacy result degrading rather than throwing | TODO | - | - |
| K5.2 | The five Telegram defects that make the feed unreadable are gone: one identity block from one source, the stage title beside the id, the structured result rendered instead of cut mid-word, a rollover that reports what it landed, and a progress line in every push | TODO | - | - |
| K5.3 | Evidence is a first-class artifact — path, kind, checkpoint, session, sha, created-at — written as an event when an agent registers one or a watched directory gains a file, with non-text kinds first-class, a Face surface, and the existing free-text evidence field still working | TODO | - | - |
| K5.4 | The message-composition layer ships owner-editable per-event templates, repo and branch and stage title and checkpoint in every push, commits and PRs as links, money with headroom, photo and document sending so evidence arrives, a thread per run, severity mapped to notify or silent, 4096-character chunking, and an ADR recording the push-only remote posture | TODO | - | - |

### K6 — The surfaces read

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K6.1 | An ADR fixes the TUI conventions — pager keys, focus model, help, one scroll idiom, viewport versus list versus table — after an actual read of glow, soft-serve, gh-dash and lazygit | TODO | - | - |
| K6.2 | bubbles v2 is a declared dependency and Report and Knowledge scroll through a viewport, with the golden, frame-invariant and glitch-sweep tests green, any baseline regenerated in a separate rebaseline commit, and a captured frame of a long document scrolled to its end as evidence | TODO | - | - |
| K6.3 | Each tab owns its own model, state, update and view, the root update becomes a dispatch instead of 826 lines and 80 cases, and the mnemonic map and the hand-maintained help legend change together | TODO | - | - |
| K6.4 | One markdown renderer honours the active theme everywhere markdown belongs, the remaining primitive swaps the ADR calls for are done as far as the goldens allow, and anything deliberately left is named | TODO | - | - |

### K7 — Ship the plan

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K7.1 | The docs match the engine and this era's own measurements are written back — the cap's real score, the corrected nudge rule, and this run's figures produced by conductor budget rather than by hand — with every wrong claim corrected in place and a closure ledger naming an owner for everything still open | TODO | - | - |
| K7.2 | feat/karvan is merged to master by the owner, the release is tagged through the existing pipeline, and the installed version matches the releases page | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
