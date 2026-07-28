# Conductor Quick Start

Get a mega-plan running autonomously in 10 minutes.

```
# Prerequisites
dotnet --version          # 10.x  (the engine targets net10.0)
go version                # 1.26  (the Face)
git --version             # any modern git
opencode --version        # or claude — the headless agent backend, already authenticated
```

Windows, Linux and macOS all work — see [platforms.md](platforms.md) for the two rails that are
Windows-only. Paths below use Windows separators; substitute yours.

**In a hurry?** `conductor demo` drives a complete run against a built-in fake agent with no
credentials and no spend. Do that first — it answers "does this work here" in seconds, and the rest
of this guide is about pointing it at *your* work.

## 1. Build conductor

```bash
# From your clone of this repo — macOS / Linux:
./tools/install.sh
```
```powershell
# Windows:
powershell -File tools\install.ps1
```

That builds the engine *and* the Go face and puts a global `conductor` on your PATH, which is what
the rest of this guide assumes. To work against a branch build instead:

```powershell
dotnet build Conductor.slnx
dotnet run --project src\Conductor -- run -p <plan>
```

## 2. Scaffold a new plan

```powershell
# Preferred: detects the repo type (dotnet/node/go/rust/python) and writes matching build+test
# gates, plus editable copies of the prompt templates.
cd C:\MyProject
conductor init

# Creates:
#   C:\MyProject\conductor.plan.json
#   C:\MyProject\TRACKER.md
#   C:\MyProject\templates\{session.md,fix.md}

# Have only an idea? Route it through the advisor and get a drivable plan out:
conductor init --from-idea "port the ingest pipeline off the legacy scheduler"

# Bare-minimum scaffold with no gate detection:
conductor new-plan -o C:\MyProject --name MyProject
```

## 3. Edit the plan

Open `conductor.plan.json` and set the real paths:

```json
{
  "name": "MyProject",
  "repo": "C:/MyProject",                    ← absolute path
  "tracker": "TRACKER.md",                   ← relative to repo
  "stages": [
    { "id": "S1", "title": "Set up CI", "sessions": 1 },
    { "id": "S2", "title": "Add tests", "sessions": 2 }
  ],
  "agent": {
    "command": "opencode",
    "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "{prompt}"],
    "resumeArgs": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "--continue", "{prompt}"],
    "provider": "opencode"
  },
  "gates": [
    { "name": "build", "command": "dotnet build", "tier": "fast", "timeoutMinutes": 10 },
    { "name": "tests", "command": "dotnet test",  "timeoutMinutes": 20 }
  ],
  "limits": {
    "stallMinutes": 12,
    "sessionTimeoutMinutes": 180,
    "stageSlackFactor": 2
  },
  "report": { "commit": true, "push": true }
}
```

## 4. Edit the tracker

Open `TRACKER.md` — the handoff block and checkpoint rows define what the agent
delivers:

```markdown
# MyProject — Tracker

## Handoff  (overwrite this block, ≤12 lines, no history)
last: (none) — first run.
stage: **S1 NOT STARTED**.
gate: not yet run.
next: **S1.1** — first checkpoint.

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| S1.1 | Set up CI pipeline | TODO | | |
| S1.2 | Configure build scripts | TODO | | |
| S2.1 | Write unit tests | TODO | | |
| S2.2 | Write integration tests | TODO | | |
```

## 5. Dry-run (see what the agent will do)

```powershell
conductor run --dry-run -p conductor.plan.json
```

Output:
```
--- DRY RUN: would start session #1 (Deliver, stage S1) with prompt: ---
You are autonomous session #1, stage S1 ...
```

This prints the entire prompt the agent would receive — template rendered,
batteries injected, instructions queued. **No agent spawned, no tokens spent,
no git changes.** Run this first, every time.

## 6. Run one session (supervised)

```powershell
conductor run --once -p conductor.plan.json
```

Launches the dashboard:

```
┌─ Conductor — MyProject ● Running ─────────────────── checkpoints 0/4 (0%) ──────┐
│ stage S1 Set up CI                        cost $0.0000 agent $0.0000 gates $0.00│
│ ▸ S1.1 Set up CI pipeline                tokens —                              │
│ ⠋ agent working · deliver · elapsed 0m12s · last output 2s ago                 │
├────────────────────┬─────────────────────────────────┬──────────────────────────┤
│ plan (F/↑↓/D)     │ agent (O)    (C fold)          │ thinking (T)              │
│ all/todo/active    │ 12:01:02 » read TRACKER.md     │ 12:01:03 ◎ goal Set up… │
│ ┌── S1 Set up CI  │ 12:01:05 » edit .github/…      │                          │
│ │ S1.1 ▶ ACTV     │ 12:01:30 ◀ exit code 0        │                          │
│ │ S1.2 TODO       │ 12:01:32 » git add…            │                          │
│ └── S2 Add tests  │                                │                          │
├────────────────────┴─────────────────────────────────┴──────────────────────────┤
│ [P] pause [K] kill [S] skip [I] inject [G] status [Q] quit [A] abort [H] hb    │
│ log                                                                            │
│ ✓ 12:01:02 session #1 (Deliver, S1) started                                   │
│ ✓ 12:01:30 session #1 exited — running gates                                   │
│ ✓ 12:01:33 gates: build OK (2.1s)  tests OK (4.2s)                            │
│ ✓ 12:01:34 outcome: Advanced — 1 checkpoint newly DONE, 2 commits              │
└─────────────────────────────────────────────────────────────────────────────────┘
```

When the session exits, conductor:
1. Runs the gate battery
2. Checks git for new commits
3. Diffs the tracker checkpoint status
4. Reports the outcome (`Advanced`, `Progress`, `GatesRed`, etc.)
5. Writes `.conductor/REPORT.md`

With `--once`, conductor stops here. You review the result, then decide.

## 7. Run the full plan (autonomous)

```powershell
conductor run -p conductor.plan.json
# or: set CONDUCTOR_PLAN=conductor.plan.json and just run: conductor run
```

Conductor loops continuously:
- Picks the next un-done stage from the tracker
- Spawns an agent session (deliver / fix / resume / audit)
- Watchdogs it (stall, timeout, token budget)
- Verifies independently (gates + git + tracker diff)
- Records, reports, commits REPORT.md
- Advances to the next checkpoint/stage

**Safe at any point:** Ctrl+C, kill the process, reboot — `conductor run` resumes.

## 8. Control from any terminal

While conductor runs (or is paused), open another terminal:

```powershell
# Inspect
conductor status -p conductor.plan.json          # tables + LLM analysis
conductor doctor -p conductor.plan.json          # what happens on resume
conductor report -p conductor.plan.json          # regenerate REPORT.md
conductor log --query "stage=S1 and outcome=fail" -p conductor.plan.json

# Control
conductor pause   -p conductor.plan.json   # pause after current session
conductor resume  -p conductor.plan.json   # resume paused/needs-human
conductor kill    -p conductor.plan.json   # kill current agent (--yes)
conductor abort   -p conductor.plan.json   # stop conductor (--yes)
conductor skip    -p conductor.plan.json   # skip stage (--yes)
conductor approve -p conductor.plan.json   # approve owner gate
conductor inject "remember to check the config" -p conductor.plan.json   # queue instruction

# Navigation
conductor retry-stage     -p conductor.plan.json   # reset attempts, re-queue deliver
conductor rollback --yes  -p conductor.plan.json   # reset to stage start commit
conductor goto S2         -p conductor.plan.json   # jump to stage S2
conductor pause-after-stage -p conductor.plan.json # park after this stage
conductor heartbeat off   -p conductor.plan.json   # stop living REPORT.md updates

# Plan management
conductor plan set limits.stallMinutes 15 -p conductor.plan.json   # hot-update
conductor plan reload  -p conductor.plan.json   # re-read + validate
conductor plan add-stage '{"id":"S3","title":"Deploy","sessions":1}' -p conductor.plan.json
```

## 9. Outcome → What happens next

| You see | Meaning | What conductor does next |
|---|---|---|
| `Advanced` | Checkpoint flipped DONE, gates green | Advances to next checkpoint. Attempts reset. |
| `Progress` | Work done, gates green, nothing flipped | Another deliver session. Same attempt budget. |
| `GatesRed` | Gates failed — fix needed | A fix session with the gate failure output embedded in the prompt. |
| `NoProgress` | Session ran but no commits | Fix session. **Attempt budget burned.** |
| `Stalled` | No output for 12 min | Kills the session, resumes it (`--continue`). Up to 2 resumes. |
| `RolledOver` | Hit token budget mid-session | Clean handoff, fresh session starts. No attempt burned. |
| `NeedsHuman` | Needs a human decision | Loops parks. Check REPORT.md + conductor.log, fix, `conductor resume`. |

## 10. Full plan run example (from Shamshir)

A real 57-session run on the Shamshir trading engine:

```
                        cost
     P7.1  s33-s36      $0.21   Verify P4.1 live (stalled → advanced → skipped)
     P7.2  s37-s52      $2.10   Prove cTrader works (16 sessions, lots of stalls)
     P7.3  s50-s57      $0.54   Trap sweep (8 sessions, 4 advanced)
     P7.4  delivered    $0.31   Trap 4+5+6
     P7.5  delivered    $0.47   Compare-both headline
     P7.6  delivered    $0.45   F6-R economics
     P7.7  delivered    $0.38   cTrader test audit
     P7.8  delivered    $0.37   Final audit
                    ─────────
     Total           $4.83    57 sessions, 8 checkpoints
```

Key patterns that made it work:
- **`--once` for the first session** of every stage — verify the agent understood
  the brief before going autonomous.
- **`conductor inject`** to guide the agent mid-stage without editing the plan.
  Queued instructions appear in the next session's prompt.
- **`conductor skip`** for a stalled stage that already completed its goal
  (state.json lagged the tracker).
- **`conductor kill`** when the agent goes in circles.
- **OWNER-PENDING protocol**: `DONE (OWNER-PENDING: need cTrader creds)` in the
  tracker — the checkpoint auto-promotes, the human handles the creds step.
- **Phase-end audits**: after every phase, an audit session produces a handover
  document (`.conductor/handovers/P*.md`) with bugs found, risks, and follow-ups.

## 11. Reading the AFK report

After every session, `.conductor/REPORT.md` is committed and pushed. Open it
from your phone:

```
# Conductor — MyProject run report

_Updated 2026-07-09 14:22 UTC · branch feat/my-feature · HEAD abc1234_

**Status:** Running
**Stage:** S1 — Set up CI · attempts used 1
**Checkpoints:** 1/4 done · **Sessions run:** 1 · **Cost:** $0.0083 (agent $0.0080 + gates $0.0003)

## Stage progress
| Stage | Title | Progress | State |
|---|---|---|---|
| S1 | Set up CI | ██░░░░░░░░ 1/4 | **← active** |
| S2 | Add tests | ░░░░░░░░░░ 0/2 | todo |

## Sessions
| # | Stage | Kind | Started | Dur | Outcome | New DONE | Commits | Gates | Cost |
|---|---|---|---|---|---|---|---|---|---|
| 1 | S1 | deliver | 07-09 14:20 | 0:12 | Advanced | S1.1 | 2 | build OK | $0.0083 |

## Timeline
_Transitions with duration, from the event log (.conductor/events.jsonl)._
...
```

## 12. Dashboard key bindings

The full, current reference lives with the Face itself — [`face-go/STYLE.md`](../face-go/STYLE.md) —
so it cannot drift from the binary. `?` inside the dashboard shows the same thing.

The handful you need on day one:

```
  h a s t o c e p r k g b d   switch tab (Home, Agent, Sessions, Timeline, …, Kanban)
  :                           command palette — every control verb, destructive ones confirm
  \                           collapse / restore the plan sidebar
  ?                           help
  q                           quit the Face (the run keeps going)
```

Closing the Face never stops the run: it is a viewer over the control plane, and
`conductor face` attaches a fresh one.

## 13. Quick reference: creating a new iteration

```powershell
# 1. Scaffold (detects repo type, writes matching gates)
conductor init

# 2. Edit plan JSON (set repo, stages, gates)
notepad conductor.plan.json

# 3. Edit tracker (write checkpoint rows)
notepad TRACKER.md

# 4. Dry-run — verify the prompt looks right
conductor run --dry-run -p conductor.plan.json

# 5. First session — supervised
conductor run --once -p conductor.plan.json

# 6. Full autonomous run
conductor run -p conductor.plan.json
```

## 14. What to do when...

### ...a gate fails
```powershell
# Re-run the battery without spawning an agent:
conductor gate -p conductor.plan.json

# If it's a genuine infra failure (not your code):
conductor gate --full -p conductor.plan.json
# All green → clears pendingFix, conductor continues

# If it's a real bug, let conductor handle it:
# The next session will be a fix session with the failure output embedded
```

### ...the agent stalls repeatedly
```powershell
# Kill it, let conductor decide what to do (resume/fix/advise)
conductor kill --yes -p conductor.plan.json

# If 2 consecutive stalls with zero output, conductor auto-parks
# at NeedsHuman. Run `conductor doctor` to see what's pending.
```

### ...you want to re-run a stage
```powershell
conductor retry-stage -p conductor.plan.json    # reset attempts
conductor run -p conductor.plan.json            # re-launch
```

### ...the stage went wrong and you want a clean slate
```powershell
conductor rollback --yes -p conductor.plan.json  # git reset --hard to stage start head
conductor retry-stage -p conductor.plan.json      # reset attempts
conductor run -p conductor.plan.json              # fresh start
```

### ...you want to skip forward
```powershell
conductor skip --yes -p conductor.plan.json    # flag for human review
conductor goto S2 -p conductor.plan.json       # jump directly
```

### ...conductor exits mid-run
```powershell
# State is persisted. Just run again:
conductor run -p conductor.plan.json
# It recovers from events.jsonl + state.json, resumes any interrupted session
```

### ...you want to see what would happen before committing
```powershell
conductor doctor -p conductor.plan.json
# Prints: pending fix? resume? phase gate? audit? remaining stages?
```

## 15. Architecture overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                        ORCHESTRATOR LOOP                            │
│                                                                     │
│  ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌────────────────┐  │
│  │ Progress │──▶│  Select  │──▶│  Build   │──▶│  AgentSession   │  │
│  │ Provider │   │  Stage   │   │  Prompt  │   │  (agent process) │  │
│  └──────────┘   └──────────┘   └──────────┘   └────────┬───────┘  │
│       ▲                                                 │          │
│       │                    ┌────────────────────────────┘          │
│       │                    ▼                                       │
│       │           ┌────────────────┐                               │
│       │           │   GATE RUNNER  │  runs gate battery            │
│       │           │  (independent) │  diffs tracker + git          │
│       │           └───────┬────────┘                               │
│       │                   ▼                                        │
│       │           ┌────────────────┐                               │
│       └───────────│   REPORTER    │  writes REPORT.md,             │
│                   │               │  commits + pushes              │
│                   └────────────────┘                               │
│                                                                     │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────────────────┐   │
│  │ EventLog     │  │  Telegram    │  │  MCP Task Server       │   │
│  │ (events.jsonl)│  │  (AFK push   │  │  (agent task mgmt)     │   │
│  │              │  │   + control) │  │                        │   │
│  └──────────────┘  └──────────────┘  └────────────────────────┘   │
│                                                                     │
│  ┌─────────────────┐  ┌──────────────────────┐                     │
│  │ Analysis Lanes   │  │  Mutating Lanes      │                     │
│  │ (Tier A, read-  │  │  (Tier B, worktree)  │                     │
│  │  only, scratch)  │  │  + merge gate        │                     │
│  └─────────────────┘  └──────────────────────┘                     │
└─────────────────────────────────────────────────────────────────────┘
```

## 16. CLI option reference

```
conductor journey [options]
  -p, --plan <PATH>          Path to plan JSON (or CONDUCTOR_PLAN env var)
                             Pre-flight itinerary — no state written, no agent spawned

conductor run [options]
  -p, --plan <PATH>          Path to plan JSON (or CONDUCTOR_PLAN env var)
  --dry-run                  Print the prompt, spawn nothing
  --once                     Run one session, then stop
  --max-sessions <N>         Stop after N sessions this process
  --paused                   Start idle: dashboard + control plane up, no session until resumed
  --headless                 Plain line output, no Face TUI (for CI / redirected stdout)

conductor status [options]
  -p, --plan <PATH>
  --since <DATETIME>         Show delta since ISO 8601 timestamp (with --deep)
  --deep                     Add an LLM narrative on top of the fast database verdict

conductor gate [options]
  -p, --plan <PATH>
  --full                     Run full battery, not just fast-tier

conductor log [options]
  -p, --plan <PATH>
  -q, --query <EXPR>         Filter: key=value pairs separated by " and "
  --since <DATETIME>         Only entries on or after this time
  --tail <N>                 Last N matching entries

conductor plan <verb> [options]
  -p, --plan <PATH>
  set <key> <value>          Hot-update a plan field (limits.stallMinutes 15)
  reload                     Re-read + validate plan JSON
  add-stage <json>           Append a new stage

conductor audit <STAGE> [options]
  -p, --plan <PATH>
  --replay                   Read-only diagnostic audit (required)

conductor new-plan [options]        # bare scaffold; prefer `init`
  -o, --output <DIR>         Output directory (default: cwd)
  --name <NAME>              Plan name (default: directory name)
  --repo <PATH>              Repo path (default: output dir)

conductor init [options]
  --from-idea <TEXT|FILE>    Route prose (or a structured doc) through the advisor into a
                             drivable plan. A structured doc is parsed for free.

conductor completion <SHELL>
  powershell                 Generate PowerShell tab completion
  bash                       Generate bash completion

conductor inject <INSTRUCTION>
  -p, --plan <PATH>          Queue instruction for next session

conductor mcp-serve [options]
  --events <PATH>            Path to events.jsonl
  --journal <PATH>           Path to MCP journal
  --run-id <ID>              Run identifier

conductor pause | resume | approve | kill | skip | abort | retry-stage |
           pause-after-stage | heartbeat [options]
  -p, --plan <PATH>
  --yes                      Skip confirmation (kill/skip/abort/rollback)
  --force                    rollback only: discard dirty working tree
