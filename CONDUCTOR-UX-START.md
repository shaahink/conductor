# Conductor UX (U-series) Phase Tracker

**Plan:** Conductor UX (U-series) | **Branch:** `feat/foreman` | **Design doc:** docs/CONDUCTOR-UX.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: (none) — tracker authored 2026-07-16 from the owner's playground dogfood feedback; no
U-series session has run yet.
stage: **U0 NOT STARTED**.
gate: not run for this era (repo is green at authoring time: dotnet 849/849, go test ok, driver PASS).
next: **U0.1** — plan discovery in `PlanSettings.ResolvePlanPath` (see docs/CONDUCTOR-UX.md §U0.1).


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 10 |
| Done | 0 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### U0 — Engine: start, resume, journey

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U0.1 | plan discovery: -p optional, cwd/plans scan, picker, friendly errors | TODO |  |  |
| U0.2 | `conductor journey`: itinerary with stages, gates, human moments, resume state | TODO |  |  |
| U0.3 | gateless plans proven + resume story documented (README) | TODO |  |  |

### U1 — Face: landing page + workspace identity

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U1.1 | Home landing tab: Server / Run / Workspace / Next-steps panels, demo parity | TODO |  |  |
| U1.2 | workspace identity in the top bar (repo basename, full path on Home) | TODO |  |  |

### U2 — Face: controls, visual report, dev stats

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U2.1 | palette groups (Run/Stage/Danger) + consequence-naming confirms | TODO |  |  |
| U2.2 | Report tab is a visual run report (progress, stages, sessions, gates, scores) | TODO |  |  |
| U2.3 | Dev tab: SQL console moved + run internals + per-session token/cost stats | TODO |  |  |

### U3 — Face: curated themes + glitch pass

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U3.1 | curated themes (mocha/latte/nord/gruvbox), --theme, live switch, persisted | TODO |  |  |
| U3.2 | golden glitch pass at 3 sizes — each fix noted in evidence | TODO |  |  |

## Dependencies

```
U0 → U1
U1 → U2
U2 → U3
```
