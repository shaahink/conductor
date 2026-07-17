# Conductor UX (U-series) Phase Tracker

**Plan:** Conductor UX (U-series) | **Branch:** `feat/foreman` | **Design doc:** docs/CONDUCTOR-UX.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: session #5 (Deliver, U1) — **U1 claimed 2/2**. QA of session #4: its gate claims
reproduced exactly (build 0w/0e, 889/889, ratchet 38≤38) — verdict PASS, no findings, nothing
to fix. U1.1 Home tab (`db9244a`) + U1.2 top-bar repo chip (`abccde2`).
gate: green — dotnet build 0w/0e, dotnet test **890/890** (+1: the new /state wire test),
ratchet OK (pragmas 38≤38, nothing weakened), go build/vet/test green, gofmt clean.
note: `/state` gained `tracker`+`stateDir`. The spec's "state dir = `<planDir>/.conductor`" is
WRONG vs the engine (PlanConfig.cs:98 roots StateDir at **Repo**) — Home renders the engine's
truth; don't "fix" it back. See the ledger.
bug #2 filed (high, NOT fixed — engine work, out of U1's scope): `conductor bg start` CLI logs
are always empty for anything slower than ~300ms (it returns immediately, killing the read pump
it just attached), so every build/test-suite run through it yields a BOM-only log while LOOKING
healthy. Workaround in the ledger; this session's gates used it.
next: **U2.1** — palette groups (Run/Stage/Danger) + consequence-naming confirms (§U2.1).


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 11 |
| Done | 0 |
| Claimed (unconfirmed) | 5 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### U0 — Engine: start, resume, journey

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U0.1 | plan discovery: -p optional, cwd/plans scan, picker, friendly errors | DONE | fbdef79 | build:OK · face-build:OK |
| U0.2 | `conductor journey`: itinerary with stages, gates, human moments, resume state | DONE | fbdef79 | build:OK · face-build:OK |
| U0.3 | gateless plans proven + resume story documented (README) | DONE | fbdef79 | build:OK · face-build:OK |

### U1 — Face: landing page + workspace identity

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U1.1 | Home landing tab: Server / Run / Workspace / Next-steps panels, demo parity | DONE | db9244a | goldens home_demo/home_disconnected/default; tab_home_test.go (8); live /state wire test GetState_CarriesTheWorkspaceIdentity_...; demo_test.go parity |
| U1.2 | workspace identity in the top bar (repo basename, full path on Home) | DONE | abccde2 | goldens size_80x24/size_200x50/default (bar line); widgets/ticker_test.go TestRepoBase (9 cases) |

### U2 — Face: controls, visual report, dev stats

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U2.1 | palette groups (Run/Stage/Danger) + consequence-naming confirms | TODO |  |  |
| U2.2 | Report tab is a visual run report (progress, stages, sessions, gates, scores) | TODO |  |  |
| U2.3 | Dev tab: SQL console moved + run internals + per-session token/cost stats | TODO |  |  |

### U3 — Face: themes, agent-terminal vibe, glitch pass

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U3.1 | curated themes (mocha/latte/nord/gruvbox), --theme, live switch, persisted | TODO |  |  |
| U3.2 | golden glitch pass at 3 sizes, seeded from the spec's dogfood appendix | TODO |  |  |
| U3.3 | agent-terminal vibe: Claude Code-style transcript, provider-aware, footer strip | TODO |  |  |

## Dependencies

```
U0 → U1
U1 → U2
U2 → U3
```
