# Conductor

[![CI](https://github.com/shaahink/conductor/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/shaahink/conductor/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Go 1.26](https://img.shields.io/badge/Go-1.26-00ADD8.svg)](https://go.dev/)

A minimal, resilient orchestrator that drives **mega plans** autonomously — one agent session at a time,
while you watch from the laptop or your phone.

![The conductor dashboard: home, agent transcript, work board, card detail, timeline, plan editor, command palette](docs/assets/demo.gif)

<sub>Seven screens of the Face, recorded live from `conductor-face --demo` — a real terminal session
against synthetic data, no engine and no credentials. Regenerate with
`powershell -File tools/demo/make-demo-gif.ps1` (Docker + Go; it cross-compiles the Face and runs
`docs/assets/demo.tape` in the VHS container), or `vhs docs/assets/demo.tape` directly where ttyd
is available.</sub>

It mechanises the session cycle you already run by hand:

```
pick next stage from the tracker
  → spawn a fresh headless agent session  [deliver | fix | resume | audit]
  → watchdog it (stall / timeout / budget detection)
  → when it exits, VERIFY INDEPENDENTLY: gate battery (real exit codes),
    new git commits, tracker checkpoint diff
  → record, report, commit+push REPORT.md, decide what runs next
  → repeat until every checkpoint is DONE and the gates confirm it
```

The plan docs stay the authority — conductor never re-plans your work. It only enforces
the rituals (pre/post-session, QA-previous-session, evidence-or-it-didn't-happen) and
keeps the loop moving without you.

## Requirements

| | Version | Why |
|---|---|---|
| **.NET SDK** | 10.0 | The engine (`conductor`). `dotnet --version` must report `10.*`. |
| **Go** | 1.26 | The Face (`face-go`), a single ~22 MB Bubble Tea binary with no runtime dependency. |
| **Git** | any modern | Conductor verifies work by diffing commits; a run without git has no independent evidence. |
| **An agent CLI** | — | `claude` or `opencode` on PATH, already authenticated. This is what actually writes code. |
| **PowerShell** | 5.1+ / pwsh | The default gate shell on Windows, and how the install script runs. |

Optional: `ffmpeg` (only to regenerate the demo GIF), a Telegram bot token (`CONDUCTOR_TELEGRAM_TOKEN`)
for phone control.

## Platform

**Windows is the supported host.** That is a statement about what is tested, not a licence
restriction — the engine targets `net10.0` and the Face is plain Go, so both *compile* on Linux and
macOS (CI proves it on every push), but the parts that make a run survive unattended are
Windows-specific today:

- gates default to the PowerShell shell, and the shipped plans use PowerShell commands;
- the process rails (graceful stop on window close, pid-identity checks that stop conductor killing
  a recycled pid) call Win32 directly;
- the Face is spawned as `conductor-face.exe`.

So the full gate battery runs on `windows-latest` in CI and the cross-platform job is a compile +
Go-test check. Running the engine on Linux/macOS is not blocked, but it is not yet proven — if you
try it, expect the shell and process-rail edges first, and please open an issue with what broke.

## Quick start

**Install once** — builds the engine *and* the Go face, then puts a global `conductor` on your PATH:

```powershell
powershell -File tools\install.ps1
```

Re-run that any time to update the installed command (it's a local release snapshot, independent of
the repo's Debug build). After it, `conductor` works from any terminal:

```powershell
conductor init                 # scaffold a new plan in the current repo (detects dotnet/go/rust/node/python)
conductor doctor               # <2s health check — says exactly what's missing before a run
conductor journey              # pre-flight itinerary: stages, gates, human moments — no state written, no spend
conductor run --dry-run        # show the first session's prompt, spawn nothing
conductor run --once           # run ONE session and stop (good for the first supervised run)
conductor run                  # run the whole plan; Ctrl+C is always safe
conductor status               # "where are we", from the database, in under a second
```

**You run `conductor`, not the Go app.** `conductor run` is one process tree: it starts the engine +
control plane and **automatically spawns the Go face (TUI)** as a child. If the face dies the run
continues; `conductor face` attaches a fresh one. You never launch the Go binary yourself.

**Zero flags:** commands resolve the plan from `-p`, else `CONDUCTOR_PLAN`, else `./conductor.plan.json`.
So `cd` into a repo that has a `conductor.plan.json` (what `conductor init` writes) and every command
works with no `-p`. In *this* repo the plans live under `plans/`, so pass one explicitly —
`-p plans\conductor-workgraph.plan.json` is the current era's — or `dotnet run -- run -p ...` to drive
with a fresh branch build, which the self-referential plans want. Note their `repo` field is an
absolute path to the author's checkout; point it at yours before driving one.

## CLI commands

`conductor --help` (or `conductor <verb> --help`) is always the authoritative, current list straight
from the binary — the table below covers the ones you'll reach for daily; it does not try to
duplicate every flag of every verb.

```
PRE-FLIGHT
  journey      Pre-flight itinerary: identity, stages, gates, human moments — no state written,
               no agent spawned. Run this before `run`.
  doctor       <2s health check: agent CLI, git, face-go binary, DNS/disk/API, budget, Telegram —
               says exactly what's missing (not a resume preview — see `status` for that)

RUN
  run          Run the plan: engine + control plane + Face TUI, one command. Resumes from saved
               state; Ctrl+C is safe.
               --dry-run            print the next session's prompt, spawn nothing
               --once               run exactly one session then stop
               --max-sessions <N>   stop after N sessions this process
               --paused             start idle: dashboard + control plane up, no session spawns
                                    until you resume
               --headless           plain line output, no Face TUI (control plane still runs)

CONTROL
  pause        Pause after the current session
  resume       Resume a paused / needs-attention conductor (control verb — different from `run`
               re-attaching to a live process; see "How resume actually works" below)
  kill         Kill the current agent session (loop re-evaluates)
  skip         Skip the current stage (flagged for human review)
  abort        Kill the session and stop the conductor
  approve      Approve an owner-gated stage so the conductor advances
  retry-stage  Reset attempt counter, re-queue deliver for the current stage
  rollback     Reset working tree to the stage start commit (--yes to force)
  goto <ID>    Jump to a different stage
  pause-after-stage  Park after the current stage completes
  inject <txt> Queue an instruction for the agent's next session
  heartbeat    Force a fresh .conductor/REPORT.md now (only meaningful mid-session)
  rollover <tokens|off|clear>  Set/clear this run's session-token rollover (run-state only)
  plan set/reload/add-stage/import   Plan management (hot-update fields, reload, add stages, import prose/markdown)

DIAGNOSTICS
  status       Show plan, tracker, and session status, from the database, in under a second
               --deep               add an LLM narrative on top (slower, opt-in)
  gate         Re-run the gate battery at HEAD (no agent spawned)
               --full               full battery (default: fast-tier only)
  report       Regenerate .conductor/REPORT.md from current state
  log          Query the structured JSON log
               -q "stage=P7 and gate=build and outcome=fail"
  tasks        Show sub-task graph per checkpoint from the event log
  task         Checkpoint CRUD from run.db: --list, --done, --in-progress
  note / bug   Knowledge ledger + tracked bugs that outlive the session that found them
  audit <ID>   Post-hoc audit replay (read-only, --replay flag)
  bg           Background process management: start|status|logs|stop
  chat "..."   Ask questions about a running plan (MCP access to run.db, ledger, control verbs)
  mcp-serve    Run the MCP task server (JSON-RPC 2.0 over stdio)
  new-plan / init   Scaffold a new plan + TRACKER.md (init also detects the repo type + gates)
  completion   Generate shell completion scripts (powershell or bash)
```

## Dashboard — face-go

`conductor run` is **one process tree**: it starts the engine + a localhost HTTP+SSE control
plane, then **automatically spawns the Go face (`face-go`)** as the dashboard — a single ~22MB
Bubble Tea binary, no runtime dependency. You never launch the Go binary yourself; if it dies the
run continues headless, and `conductor face` attaches a fresh one to a live run.

```powershell
conductor run -p <plan>              # spawns face-go automatically
conductor face -p <plan>             # attach another face to an already-running conductor
conductor run -p <plan> --headless   # no Face — plain line output (CI / redirected output)
conductor run -p <plan> --no-face    # control plane runs, but nothing is spawned to view it
```

`face-go`'s full keybinding and layout reference lives in `face-go/STYLE.md` (kept current there,
not duplicated here) — eleven tabs (Agent · Sessions · Timeline · Procs · Console · Templates ·
Plan · Report · Knowledge · Telegram · Kanban), an always-visible plan sidebar, and a `:` command
palette for every control verb (pause/resume/abort/kill/skip/goto/…, destructive ones confirm
first). `--demo` runs it fully offline against synthetic data:

```powershell
cd face-go
go build -o bin/conductor-face.exe ./cmd/conductor-face/
.\bin\conductor-face.exe --demo    # offline synthetic data, no conductor process needed
```

> The original TypeScript + Ink face (`face/`) was retired in M7 once `face-go` reached
> day-to-day usability. Its history is in git.

### How resume actually works

Three different things are all called "resume" — they are not interchangeable:

- **`conductor run -p <plan>`** is how you resume a run that isn't currently a live process
  (you closed the terminal, the machine restarted, a previous run ended or was interrupted). It
  reads the latest persisted `RunState` for that plan — `.conductor/state.json` if present and
  non-empty, falling back to the `run_state` table in `.conductor/run.db` (the live source of
  truth since M2; `state.json` is a legacy carrier kept for back-compat) — and continues from
  exactly the recorded session count, stage, and budget. No flag needed; this is the default
  behaviour of plain `run`. `conductor journey` shows you what it will do (`"resumes session #N,
  stage X"` vs `"fresh run"`) before you run it.
- **`--paused`** is a flag on `run`, not a separate resume mechanism: `conductor run -p <plan>
  --paused` brings the dashboard + control plane up with no session spawning until you explicitly
  resume — useful for reviewing/editing the plan in the Face before the first (or next) session
  fires.
- **`conductor resume`** is a *control verb* for a run that is already live and currently paused
  or parked awaiting attention (e.g. after `conductor pause`, an owner-gate, or a budget cap trip)
  — it does not start a process, it un-pauses one that's already running. From the Face, the same
  action is available from the command palette or the `R` key.

Ctrl+C during a live run is always safe: it saves state and prints an epilogue (status, exit
meaning, attention reason, the exact resume command) rather than leaving you guessing.

## AFK awareness

After every session, conductor rewrites `.conductor/REPORT.md` in the target repo and
commits + pushes it (configurable). Open it on GitHub from your phone: status,
per-stage progress, session history, gate results, timeline, health, confidence.

**Telegram integration** (optional): push notifications + two-way control
(`/status`, `/tasks`, `/pause`, `/resume`, `/approve`, `/skip`) via inline keyboard.

**Webhooks**: generic HTTP POST, Discord, or Slack notification on
NeedsHuman/completion events.

**From another terminal** — the same control verbs work out-of-process via a
control file drop. See CLI commands above.

## Session lifecycle

```
 Orchestrator loop                  Agent session
 ──────────────                     ─────────────
   Read tracker
   Select next stage
   Pre-hook (stage, blocks if fail)
   Check handoff for HUMAN:
   Check DNS preflight (O2)
   Launch analysis lanes (B12.1) ──────────────▶ (read-only, scratch temp)
   Build prompt (template + persona + batteries + injected instructions)
   Spawn agent (deliver / fix / resume / audit)
                               ─────────────────▶ Agent works
   Watchdog: stall, timeout, ─────────────────▶ (streaming output)
     soft-break (B9.4)
   Poll lanes ─────────────────────────────────▶ (collect artifacts)
   On exit:
     Run gate battery (fast or full)
     Git: check new commits
     Tracker: diff checkpoint status
     Determine outcome (Advanced / Progress / GatesRed / Stalled / ...)
     If stalled → resume session (≤ maxResumesPerSession)
     If GatesRed → fix session
     If exhausted budget → consult advisor (cheap LLM)
     Emit events, record session, save state
     Write + push REPORT.md
     If phase done → schedule audit / phase gate
     If plan done → confirm with full battery
```

### Outcome model

| Observation | Outcome | Next |
|---|---|---|
| gates green + new commits + ≥1 checkpoint newly DONE | `Advanced` | next checkpoint/stage, attempts reset |
| gates green + new commits, nothing flipped | `Progress` | another deliver session |
| gates green, no commits | `NoProgress` | fix session, attempt burned |
| any required gate red | `GatesRed` | fix session with gate output embedded |
| no output for `stallMinutes` | `Stalled` | killed, then resume same session (≤ maxResumes) |
| over `sessionTimeoutMinutes` | `TimedOut` | same as stalled |
| backend says usage/rate limit | `LimitBackoff` | wait `backoffMinutes`, resume — no attempt burned |
| session exceeded token budget | `RolledOver` | clean handoff, fresh session — no attempt burned |
| tracker handoff contains `HUMAN:` or row flips to BLOCKED | — | **NeedsHuman**: loop parks, report + notification |
| 2× consecutive zero-output stall | — | **NeedsHuman** (O2 identical-stall detection) |

Attempt budget per stage = `stage.sessions × limits.stageSlackFactor`. When exhausted,
the **advisor** (a cheap second model — deepseek via opencode by default) is asked to
choose retry / resume / skip / human. The advisor can also `ApplyFix` (run a
remediation script) or `RerunGates` (P3 stronger verdicts).

## Plan config

Everything lives in one JSON file per mega plan. Below is the full schema reference.

### Root fields

| Field | Type | Description |
|---|---|---|
| `version` | string | Schema version (`"1.0"`). Rejects unsupported versions with a clear diagnostic. |
| `planVersion` | int | Monotonic edit counter, bumped on every `plan set/reload/add-stage`. |
| `name` | string | Plan name — appears in dashboard header + report. |
| `repo` | string | Absolute path to the repository directory. |
| `tracker` | string | Path (relative to repo) to the TRACKER.md file. |
| `planDoc` | string | Path (relative to repo) to the plan/design doc. |
| `branchPattern` | string | Regex — conductor warns if the current branch doesn't match. |
| `pauseOnBlocked` | bool | Park at NeedsHuman when a BLOCKED row is found. Default true. |
| `batteryCollapse` | bool | Skip agent's pre-session ritual, defer to conductor's battery. Saves ~30-50% tokens. |
| `promptExtra` | string | Prepended to every session prompt (high-level context). |

### `agent` — Agent process config

| Field | Type | Description |
|---|---|---|
| `command` | string | CLI exe: `"opencode"`, `"claude"`, or any executable. |
| `args` | string[] | Arguments. `{prompt}` and `{sessionId}` are substituted. |
| `resumeArgs` | string[] | Arguments for resuming a session (`{prompt}`, `{claudeSessionId}`). |
| `provider` | string | Adapter: `"opencode"`, `"claude"`, `"text"`. Inferred from `output` if unset. |
| `output` | string | `"stream-json"` (claude) or `"text"` (opencode etc.). Legacy. |
| `systemPrompt` | string | System prompt injected before the base prompt (persona). |
| `model` | string | Model override (e.g. `"claude-sonnet-4-20250514"`). |
| `temperature` | double | Sampling temperature (0.0–2.0). |
| `tokenCeiling` | int | Per-session output token ceiling. |
| `env` | object | Extra environment variables for the agent process. |

### `advisor` — Second brain for dead-ends

| Field | Type | Description |
|---|---|---|
| `enabled` | bool | Default true. |
| `command` | string | CLI exe (cheap model: deepseek via opencode by default). |
| `args` | string[] | Args with `{prompt}` placeholder. |
| `output` | string | `"text"` or `"json"` (claude `--output-format json`). |
| `timeoutMinutes` | int | Advisor timeout. Default 6. |
| `remediationScript` | string | Shell command run when advisor returns `ApplyFix`. |

### `statusAgent` — On-demand LLM status reporter

| Field | Type | Description |
|---|---|---|
| `enabled` | bool | Default true. |
| `command` | string | CLI exe. Default `"opencode"`. |
| `args` | string[] | Args with `{prompt}`. |
| `model` | string | Model override for status calls. |
| `maxPerHour` | int | Rate limit. Default 12. |

### `stages[]` — Stage definitions

| Field | Type | Description |
|---|---|---|
| `id` | string | Short identifier, must match tracker checkpoint prefix (e.g. `"L0"`, `"P7.3"`). |
| `title` | string | Human-readable stage name. |
| `sessions` | int | Expected session count. Budget = `sessions × stageSlackFactor`. |
| `notes` | string | Stage-specific text appended to the session prompt. |
| `ownerGate` | bool | Park at `AwaitingOwner` when stage goes green. Owner must approve to advance. |
| `persona` | string | Specialist persona: `architect`, `planner`, `qa`, `docs`, `reviewer`, `refactor`, `test-writer`, `git-cleanup`, `security-audit`. |
| `kind` | string | `"deliver"` (default) or `"review"` (advisory artifact, no mutations). |
| `dependsOn` | string[] | Stage IDs that must complete before this stage is ready. |
| `parentId` | string | Parent stage for hierarchical tree display. |
| `agent` | object | Per-stage agent override (merged over plan default). |
| `preHook` | object | Command run before the first session. Non-zero exit blocks the stage. |
| `postHook` | object | Command run after confirmation. Best-effort, never blocks. |

### `gates[]` — Gate battery

| Field | Type | Description |
|---|---|---|
| `name` | string | Gate name (appears in dashboard + logs). |
| `command` | string | Shell command. Exit code determines pass/fail. |
| `shell` | string | `"powershell"` (Win default) or `"bash"` / `"sh"`. |
| `cwd` | string | Working dir relative to repo root. |
| `optional` | bool | Report but never block. |
| `skipIfMissing` | string | Skip gate while this file path doesn't exist. |
| `tier` | string | `"fast"` (per-session under perPhase), `"full"` (phase end, and every session under perSession), or `"truth"` (phase confirmation only). Default `"full"`. |
| `parallel` | bool | Run concurrently with other parallel gates in the same batch. |
| `stages` | string[] | Only run when the current stage id is in this list. |
| `timeoutMinutes` | int | Per-gate timeout. Default 20. |

**`"gates": []` (or the field omitted entirely) is a supported, deliberate choice** — not a
misconfiguration. Every verdict reads `"gates green (none configured)"` rather than failing or
going silently blank, and `conductor doctor` flags it as a warn-level notice ("none configured —
every session verdict will trust commits + tracker only"), never a failure. Useful for a docs-only
or spike plan with no build/test surface.

### `limits` — Watchdog + budget

| Field | Type | Default | Description |
|---|---|---|---|
| `stallMinutes` | int | 12 | No output for this long → kill + resume. |
| `sessionTimeoutMinutes` | int | 240 | Hard session timeout. |
| `maxResumesPerSession` | int | 2 | Max times a session can be resumed after stall/timeout. |
| `stageSlackFactor` | int | 2 | Budget multiplier: `stage.sessions × this`. |
| `backoffMinutes` | int | 30 | Wait on usage/rate limit. |
| `maxBackoffs` | int | 10 | Hard cap on consecutive backoffs. |
| `maxRunCostUsd` | decimal | null | Total cost cap. Parks at AwaitingOwner when hit. |
| `maxRunTokens` | long | null | Total token cap. Same parking behaviour. |
| `maxSessionTokens` | long | null | Per-session token budget → RolledOver with handoff. |
| `softBreakRatio` | double | 0.8 | Fraction of `maxSessionTokens` at which agent gets a cooperative "wrap up" nudge. |
| `approvalMode` | bool | false | Park at AwaitingOwner before every session. |
| `stallPatternTermination` | bool | true | 2× consecutive zero-output stall → NeedsHuman. |
| `stallBackoffMinutes` | int | 12 | Initial stall backoff, doubles each consecutive stall. |
| `maxConcurrentLanes` | int | 2 | Max concurrent Tier A analysis lanes. |
| `dnsHealthCheck` | object | — | Pre-session DNS check (hosts, intervalSeconds). |
| `overheadCostPerSecond` | decimal | 0.0001 | Gate runtime cost estimate rate. |

### `gatePolicy` — Battery run strategy

- `"perSession"` (default): full battery after every session.
- `"perPhase"`: fast-tier gates per session; full battery only when a stage's checkpoints are all DONE.

### `report` — AFK reporting

| Field | Type | Default |
|---|---|---|
| `commit` | bool | true |
| `push` | bool | true |
| `heartbeatMinutes` | int | 0 (disabled). Live-report during long sessions. |

### `notify` — Notifications

| Field | Type | Description |
|---|---|---|
| `command` | string | CLI command for needs-human / completion. |
| `webhook`, `discord`, `slack` | object | Each has `url` + optional `headers`. |

### `telegram` — Telegram bot

| Field | Type | Description |
|---|---|---|
| `allowedChatIds` | string[] | Allowed chat IDs for commands. Empty = push-only. |
| `pollIntervalSeconds` | int | getUpdate polling interval. Default 4. |
| `enableTwoWay` | bool | Enable incoming commands via Telegram. |

Token read from `CONDUCTOR_TELEGRAM_TOKEN` env var.

### `progress` — Tracker provider

| Field | Type | Description |
|---|---|---|
| `kind` | string | `"markdown-table"` (default), `"script"`, or `"plan-checkpoints"`. |
| `script` | object | Command + timeout for the `"script"` provider. |
| `checkpoints` | array | Inline checkpoints for `"plan-checkpoints"`. |

### `conventions` — Tracker format

| Field | Type | Default | Description |
|---|---|---|---|
| `stageIdPattern` | string | `(?<stage>[A-Za-z]+\d+)(?:\.\d+)?[a-z]?` | Regex with optional `stage` named group. |
| `handoffMarker` | string | `"## Handoff"` | Heading for the handoff block. |
| `humanToken` | string | `"HUMAN:"` | Token in handoff to request human decision. |
| `status` | object | — | Status vocabulary: `done`, `blocked`, `inProgress`, `todo` word lists. |

### `templatesDir` — Prompt template directory

Path (relative to plan file) to custom `session.md`, `fix.md`, `resume.md`,
`audit.md`, `advisor.md`, `review.md` templates. Falls back to built-in defaults
when files are missing.

### `readOrder` — Session reading list

Array of file paths (relative to repo) that the agent reads in order at session
start. Rendered as an ordered list in the session prompt.

### `batteries` — Prompt context injection

| Field | Type | Default | Description |
|---|---|---|---|
| `lessons` | bool | true | Inject rolling lessons brief. |
| `recentFailure` | bool | true | Inject compact failed-session summary. |
| `lessonsMaxEntries` | int | 3 | Max lessons entries. |
| `maxBytes` | int | 2048 | Total byte cap for all battery sections. |

### `analysisLanes[]` — Tier A parallel analysis

| Field | Type | Description |
|---|---|---|
| `id` | string | Lane identifier. |
| `kind` | string | `"architecture"`, `"design"`, `"qa"`, `"research"`, `"analysis"`. |
| `name` | string | Human-readable name. |
| `prompt` | string | Analysis question — embedded in the lane's session prompt. |
| `stageTrigger` | string | Only run when this stage becomes active. null = every stage. |
| `timeoutMinutes` | int | Default 15. |
| `enabled` | bool | Default true. |
| `maxOutputLines` | int | Default 200. |

### `mutatingLanes[]` — Tier B isolated worktree lanes

| Field | Type | Description |
|---|---|---|
| `id` | string | Lane identifier. |
| `kind` | string | `"delivery"`, `"fix"`, `"refactor"`. |
| `name` | string | Human-readable name. |
| `prompt` | string | Work prompt for the agent. |
| `stageTrigger` | string | Only run for this stage. |
| `timeoutMinutes` | int | Default 30. |
| `enabled` | bool | Default true. |
| `agent` | object | Per-lane agent override. |
| `mergeGates` | array | Gates to verify the merge. null = use plan-level gates. |

### `audit` — Phase-end audit

| Field | Type | Default |
|---|---|---|
| `enabled` | bool | true |
| `maxAttempts` | int | 1 |
| `enableParallel` | bool | true (audit runs as concurrent lane instead of sequential session). |

### `setup` / `teardown` — Lifecycle hooks

Optional commands run before/after every session and every gate battery.

| Field | Type | Description |
|---|---|---|
| `command` | string | Shell command. Best-effort: non-zero exit doesn't block. |
| `cwd` | string | Working dir relative to repo root. |
| `timeoutMinutes` | int | Default 3. |

## Tracker format

The tracker is a Markdown file (`TRACKER.md` is what `conductor init` writes; this repo's own is
`CONDUCTOR-WORKGRAPH.md`) that serves as both the human-readable progress document AND the
machine-parsable state that Conductor reads.

Since W1 the tracker is a **generated view**: the work graph in `run.db` is the runtime truth, and
the tracker is re-rendered from it after every session. Hand-editing a checkpoint row no longer
claims anything — `conductor task --done <id>` (or the Face's board) is the one claim path. The
handoff block stays yours to write.

### Handoff block

```markdown
## Handoff  (overwrite this block, ≤12 lines, no history)
last: s12 L3 refactor DONE — all 3 checkpoints flipped. Gate battery: build OK,
  tests OK. Evidence: docs/evidence/L3/.
stage: **L4 Delivery — IN PROGRESS** (attempt 1/4).
gate: PASS.
next: **L4.1 Extract TfmScore** — rename + extract + wire.
trap: L4 depends on L3 being confirmed; pre-hook runs before first session.
```

### Checkpoint table

```markdown
Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| L0.1 | Stub A | DONE | abc1234 | docs/evidence/L0.1-test.md |
| L0.2 | Stub B | IN PROGRESS | | |
| L0.3 | Stub C | TODO | | |
```

### OWNER-PENDING marker

A `DONE (OWNER-PENDING: need creds to verify)` cell means the agent marked it done
but left a note for the human. The checkpoint is auto-promoted.

### QA-previous block

```markdown
> QA-previous (s12 QA of s11/L3): **confirmed.** Full gate battery re-run: build OK,
> tests OK. Verified 2 claims: TfmScore exists, interface extracted.
```

## Runtime files (`.conductor/`)

```
.conductor/
  run.db                 THE live run state (SQLite, run_state table) — every transition persists
                          here; this is what `conductor run` resumes from, not state.json
  state.json             Legacy resumable-state carrier (pre-M2). Only a couple of standalone
                          verbs (e.g. `conductor gate`) still write it; the live run loop never
                          does. Kept as a fallback `RunState.LoadOrNew` reads first; harmless if
                          stale or absent — resume falls back to run.db either way
  events.jsonl           Append-only event log (event-sourced backbone — 22 event types)
  REPORT.md              The AFK report — the only file conductor commits
  conductor.log          Orchestrator text log
  control.json           Transient control verbs from the CLI/TUI/Telegram
  conductor.lock         PID lock (two conductors can't fight over one repo)
  lessons.md             Rolling lessons brief (bounded, rotating)
  followups.md           Tracked follow-up items from audits
  queue/                 Injected instruction chain for the next session
  logs/
    session-NNN.jsonl           Raw agent stream per session
    session-NNN.prompt.md       Exact prompt each session got
    conductor-YYYY-MM-DD.json   Structured JSON log (for conductor log --query)
  lanes/                 Analysis lane artifacts
  handovers/              Phase-end audit handover documents
  audits/                Post-hoc audit replay outputs
```

A `.gitignore` inside keeps everything but `REPORT.md` out of the repo.

## Trust model

- **Agents' claims are never trusted.** After every session conductor re-runs the
  gate battery itself and diffs the tracker + git log. A checkpoint only counts
  when the row flipped to DONE **and** commits exist **and** gates are green.
- **"All DONE" is confirmed, not believed.** When the tracker says the plan is
  complete, conductor runs the full battery one more time before declaring victory.
- **Failures loop back with evidence.** Red gates → the next session is a *fix
  session* whose prompt embeds the actual failing gate output.
- **Everything is resumable.** State is persisted to `run.db` on every transition.
  Kill conductor, reboot, Ctrl+C — running `conductor run` again picks up where it
  left off (see "How resume actually works" above).

## Testing without burning tokens

`tools/fake-agent.ps1` impersonates opencode's stream-json and can simulate
success / stall / red-gates / usage-limit. Unit tests: `dotnet test`.

For a full end-to-end proof with **no credentials and no spend**, run the dress rehearsal — it takes
a markdown document to a finished run through the real `conductor.exe`, driving every lever over the
live HTTP control plane:

```powershell
powershell -File tools/w5/rehearsal.ps1 -Keep    # ~90s, 27 checks, no API key needed
```

The write-up (including the three engine defects it found) is
[`docs/workgraph/W5-REHEARSAL.md`](docs/workgraph/W5-REHEARSAL.md).

### The gate battery

What CI runs, and what every checkpoint in this repo must pass:

```powershell
dotnet build Conductor.slnx
dotnet test  Conductor.slnx
cd face-go; go build ./...; go vet ./...; go test ./...
powershell -File tools/gates/ratchet.ps1     # exact path — a wrong path exits 0
```

The ratchet is an anti-cheat gate: it fails if the *bar* moved down (tests deleted, analyzer
suppressions added, gate commands softened) rather than if the code is wrong.

## Design decisions

- **The tracker is the progress database.** No parallel bookkeeping to drift.
- **Sessions are processes, not threads.** A hung agent can always be killed as a
  tree; a killed conductor can always resume the agent by session id.
- **Event-sourced backbone.** `events.jsonl` is the append-only truth; `state.json`
  is a projection. Enables replay, timeline, health metrics, task graph.
- **Provider abstraction.** `IAgentProvider` separates the engine from backend wire
  formats (opencode, claude, text).
- **Personas.** Per-stage specialist agents (architect, qa, docs, etc.) with
  dedicated system prompts and prompt templates.
- **Parallel lanes.** Tier A read-only analysis lanes (scratch dir) and Tier B
  isolated worktree lanes (git worktree + merge gate) run concurrently with the
  primary session.
- **Deterministic first, model second.** The advisor is consulted only at genuine
  dead-ends and its answer is validated against a vocabulary of actions.
- **Pluggable progress providers.** Default Markdown-table tracker; `script`
  and `plan-checkpoints` escape hatches for non-standard workflows.
- **Battery collapse.** Opt-in to skip the agent's redundant pre-session ritual,
  saving 30-50% of output tokens.

## Documentation

[`docs/README.md`](docs/README.md) is the index. The short path:

- [`docs/quickstart.md`](docs/quickstart.md) — plan → tracker → dry run → first supervised session.
- [`docs/OPERATING-CONDUCTOR.md`](docs/OPERATING-CONDUCTOR.md) — every control verb and when to reach for it.
- [`docs/DOGFOOD-RUNBOOK.md`](docs/DOGFOOD-RUNBOOK.md) — triage when a run looks stuck, dead, or wrong.
- [`face-go/STYLE.md`](face-go/STYLE.md) — the Face's live keybinding + layout reference.
- [`docs/CONDUCTOR-WORKGRAPH.md`](docs/CONDUCTOR-WORKGRAPH.md) — the current design authority;
  [`CONDUCTOR-WORKGRAPH.md`](CONDUCTOR-WORKGRAPH.md) at the root is its live tracker.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — the short version is that the gate battery above is the
review, and the ratchet means a PR may not make the bar smaller. Security reports:
[SECURITY.md](SECURITY.md).

## License

MIT — see [LICENSE](LICENSE).
