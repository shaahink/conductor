# Conductor UX (U-series) Phase Tracker

**Plan:** Conductor UX (U-series) | **Branch:** `feat/foreman` | **Design doc:** docs/CONDUCTOR-UX.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: session #7 (Fix, U2). s6 did NOT fail: it returned a valid score-90 PASS. **The engine
threw it away — the live orchestrator (PID 2148) is the INSTALLED binary, built 01:11 and
started 01:28; s4's verifier-truncation fix (`fbdef79`) landed 03:15, so the running process
predates its own fix and still applied the 700-char crop** (run.db result_summary = 701 chars vs
the real 4093). Nothing to fix in code: `Verifier.Parse`/`ExtractSessionResult` are correct.
**HUMAN: every Verify in this run keeps failing until the owner re-runs `tools/install.ps1` and
RESTARTS** — no in-run session can (the restart kills it). Bug #4 filed (stale-engine detection).
Record was already correct (nothing over-claimed): U0+U1 DONE, U2 was 3× TODO.
done: **U2.1** (`26a4194`) palette Run/Stage/Danger + confirms that name consequences
(`abort — kill session + stop conductor. y/N`); reading frames caught 3 real glitches (17-char
`pause-after-stage` skewed its column; selected row off by one; help card hit 25 rows and clipped
its own border at 80×24 — new guard test, not eye). Plus (`71fa214`) **bug #5**: ClaudeProvider
never read `usage`, so ALL claude runs recorded 0 tokens — which silently disabled
`limits.maxSessionTokens` (TokensTotal always 0). Fixed + closed.
gate: green — build 0w/0e, ratchet OK (38≤38, archdebt 0), go build/vet/test green, gofmt clean.
The 3 C# fails seen mid-session were a competing `dotnet test` in C:\code\DevContext2 saturating
the box; all 31 pass isolated once quiet. **Do NOT run `Get-Process dotnet | Stop-Process` as
AGENTS.md suggests — it would have killed that other repo's suite AND a live web server.**
next: **U2.2** (visual Report) then **U2.3** (Dev tab). **U2.3's spec premise is FALSE**: the
sessions table has NO token/cost columns — they live in a separate `costs` table, keyed by
session_number with a `category` (agent|gate|advisor), so a session has MANY rows: SUM/GROUP BY,
never a naive JOIN. See the ledger before starting.


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
| U1.1 | Home landing tab: Server / Run / Workspace / Next-steps panels, demo parity | DONE | db9244a | build:OK · face-build:OK |
| U1.2 | workspace identity in the top bar (repo basename, full path on Home) | DONE | db9244a | build:OK · face-build:OK |

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
