# Conductor UX (U-series) Phase Tracker

**Plan:** Conductor UX (U-series) | **Branch:** `feat/foreman` | **Design doc:** docs/CONDUCTOR-UX.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: session #4 (FIX) — session #3 (Verify) crashed AgentError; root cause was
SessionRunner.ExtractSessionResult cropping a real, valid verifier JSON to 700 chars before
Verifier.Parse ever saw it (plus a regex too fragile for a quoted `{model}`-style brace in a
finding). Both fixed; see the ledger note on this run. U0.1-U0.3's code was already correct
(session #3's own analysis confirmed it before crashing) but never claimed — claimed now via
`conductor task --done` with commit+evidence.
stage: **U0 claimed 3/3 (unconfirmed — awaiting the next live verify session)**.
gate: green — dotnet build 0w/0e, dotnet test 889/889, ratchet OK (pragmas 38≤38, nothing
weakened), face-go build+vet+test green.
next: **U1** — Face landing page + workspace identity (docs/CONDUCTOR-UX.md §U1), once U0 is
confirmed by a live verify session.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 11 |
| Done | 0 |
| Claimed (unconfirmed) | 3 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### U0 — Engine: start, resume, journey

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U0.1 | plan discovery: -p optional, cwd/plans scan, picker, friendly errors | DONE | 199f2c8 | 9 resolution-order unit tests; matches CONDUCTOR-UX.md §U0.1 exactly |
| U0.2 | `conductor journey`: itinerary with stages, gates, human moments, resume state | DONE | 66e6f57 | conductor journey verb, 10 unit tests; matches CONDUCTOR-UX.md §U0.2 |
| U0.3 | gateless plans proven + resume story documented (README) | DONE | 84fe84f | gateless verdicts + README resume story; U03GatelessLiveTests live proof |

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
