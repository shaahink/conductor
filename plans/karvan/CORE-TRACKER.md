# Karvan core - the engine knows what it did and what it cost Phase Tracker

**Plan:** Karvan core - the engine knows what it did and what it cost | **Branch:** `feat/karvan` | **Design doc:** docs/history/CONDUCTOR-KARVAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: **K2.1, K2.2 and K2.3 all claimed** (`b05efef`, `3bc5a2a`, this commit). `src/Conductor.Core` is
  its own assembly (domain/orchestration/store/events/providers); `Conductor` is Program + Commands +
  `Http/ControlPlaneServer*` + `Hosting/`. Namespaces already matched folders, so it was a rename. Two
  reverse deps died: `HumanDuration.Format`, `ControlPlaneDiscovery.PathFor`. `ArchitectureBoundaryTests`
  = 8 rules that name the offender. K2.3: `ControlPlaneDto` was never a type — 29 files of independent
  records wearing a prefix — now `Http/Contracts/<feature>/`, mapper renamed `ControlPlaneMapper`;
  events in `Events/Kinds/`; Telegram's API records in `Integrations/TelegramApi/`. `ARCHITECTURE.md`
  is new and holds the convention plus the split/left table. Suite **1814/1814** twice.
next: **K2.4, half done — back on TODO, four things left.** DONE: `AGENTS.md` 978→368 lines, the nine
  superseded handoffs archived verbatim with an index at `docs/history/handoffs/AGENTS-resume-log-`
  `2026-07.md`. LEFT, in order: (1) `ARCHITECTURE.md` has only the layering diagram + K2.3's file
  convention — it still owes the session lifecycle, the seams, the two surfaces and a where-do-I-add-X
  table; (2) `SARBAN-{CORE,FACE}-TRACKER.md` → `docs/history/trackers/`; (3) root
  `CONDUCTOR-WORKGRAPH.md` (252 lines) vs `docs/dev/` copy (197) — different hashes, resolve to one;
  (4) `docs/README.md` + `docs/dev/README.md` indexes. None of it touches this plan's own files.
watch: **clear `CONDUCTOR_PLAN` before running your own build's `doctor`/`run`** — the env the
  orchestrator hands you points at THIS repo, a scratch cwd is not isolation, and your build migrates
  `run.db` 9→10 and kills every claim verb. Fixed here; recipe is in the ledger.
red: none. Open, not blocking: **#27** fresh-db FK error on first `run_state` write, **#24**
  `AgentConfig.Merge` drops `Env`.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 32 |
| Done | 1 |
| Claimed (unconfirmed) | 6 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### K1 — The ledger stops lying

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K1.1 | A rolled-over session records the commits and claims it actually made, proven by a harness session driven to its ceiling, with a rollover still consuming no attempt and still not running the phase gate | DONE | 93bbae5 | .conductor/evidence/K1/K1.1-rollover-records-facts.md |
| K1.2 | The soft break is re-stated until it is obeyed, names the actual remaining budget, states the wrap-up order (claim first, handoff second), and the session record says whether it was delivered, re-delivered and obeyed | DONE | 93bbae5 | .conductor/evidence/K1/K1.2-soft-break-restated-and-measured.md |
| K1.3 | Three small untruths die as a class — the thinking-token column that is zero on all 125 rows, the lessons file that is a diary and repeats one entry twice, and a go.mod that calls a directly-imported package indirect while carrying two lipgloss majors | DONE | 890ac38 | .conductor/evidence/K1/K1.3-three-untruths.md |
| K1.4 | A spawned session sees conductor's task tools and the operator's own MCP servers, because the config merges instead of replacing, with the prompt-side deferred-tool fallback kept | DONE ✓ | 36314be | .conductor/evidence/K1/K1.4-mcp-merge.md |

### K2 — The architecture becomes navigable

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K2.1 | Conductor.Core holds the domain, orchestration and store with no Spectre and no HTTP hosting, Conductor is CLI plus hosting, the reference direction only points one way, and publish plus doctor plus the Face's discovery still work | DONE | b05efef | .conductor/evidence/K2/K2.1-K2.2-core-extraction.md |
| K2.2 | Architecture tests in the ordinary suite fail the build when a boundary is crossed, each naming the offending type and the rule, landed together with K2.1 so the extraction is verified rather than asserted | DONE | b05efef | .conductor/evidence/K2/K2.1-K2.2-core-extraction.md |
| K2.3 | The worst partial-file piles are split by responsibility — the thirty-file DTO pile becomes per-feature endpoint contracts — and one written file-organisation convention says where a new endpoint, event or partial belongs | DONE | b05efef | .conductor/evidence/K2/K2.3-partial-piles-and-the-convention.md |
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

### F0

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F0.1 | Map scroll behavior, animation, and UI components in face-go | TODO | - | - |

### F1

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F1.1 | Implement fixed scroll container component | TODO | - | - |
| F1.2 | Add exit mechanism after list completion | TODO | - | - |

### F2

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F2.1 | Filter shamshir from showcase list display | TODO | - | - |

### F3

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F3.1 | Move narration component to bottom placement | TODO | - | - |

### R0

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| R0.1 | Test scroll behavior and exit mechanism | TODO | - | - |
| R0.2 | Verify shamshir is hidden and narration repositioned | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
