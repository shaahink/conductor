# Conductor

A minimal, resilient orchestrator that drives **mega plans** autonomously — one agent session at a time,
while you watch from the laptop or your phone.

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
works with no `-p`. In *this* repo the plans live under `plans/`, so pass
`-p plans\conductor-maestro.plan.json` (or `dotnet run -- run -p ...` to drive with a fresh branch build,
which the self-referential Maestro plan wants).

## CLI commands (23 verbs)

```
RUN
  run          Run the plan loop (resumes from saved state)
               --dry-run            print the next session's prompt, spawn nothing
               --once               run exactly one session then stop
               --max-sessions <N>   stop after N sessions
               --no-dashboard       plain line output (for CI / redirected)

CONTROL
  pause        Pause after the current session
  resume       Resume a paused / needs-attention run
  kill         Kill the current agent session (loop re-evaluates)
  skip         Skip the current stage (flagged for human review)
  abort        Kill the session and stop the conductor
  approve      Approve an owner-gated stage so the conductor advances
  retry-stage  Reset attempt counter, re-queue deliver for the current stage
  rollback     Reset working tree to the stage start commit (--yes to force)
  goto <ID>    Jump to a different stage
  pause-after-stage  Park after the current stage completes
  inject <txt> Queue an instruction for the agent's next session
  heartbeat on|off   Toggle heartbeat at runtime without restarting
  plan set/reload/add-stage   Plan management (hot-update fields, reload, add stages)

DIAGNOSTICS
  status       Show plan, tracker, and session status (+ optional LLM analysis)
               --since <DATETIME>   show delta since a point in time
               --no-llm             skip the LLM analysis (fast, offline)
  gate         Re-run the gate battery at HEAD (no agent spawned)
               --full               full battery (default: fast-tier only)
  report       Regenerate .conductor/REPORT.md from current state
  log          Query the structured JSON log
               -q "stage=P7 and gate=build and outcome=fail"
               --since <DATETIME>   filter by time
               --tail <N>           show last N matching entries
  tasks        Show sub-task graph per checkpoint from the event log
  replay       Replay / time-travel through a past run's events.jsonl
  preview      Render the dashboard offline from current state (+ synthetic data)
  doctor       Print exactly what will happen on resume
  audit <ID>   Post-hoc audit replay (read-only, --replay flag)
  mcp-serve    Run the MCP task server (JSON-RPC 2.0 over stdio)
  new-plan     Scaffold a new plan + TRACKER.md from a built-in template
               --template (minimal|dotnet|node|shamshir) -o <dir>
  completion   Generate shell completion scripts (powershell or bash)
```

## Dashboard TUI

The default is a live Spectre dashboard with 5 zones:

```
┌─ Conductor — Loom ● Running ────────────────────────── checkpoints 12/24 (50%) ─┐
│ stage L4 Refactor → S3              cost $0.1245 agent $0.1123 gates $0.0122    │
│ ▸ L4.1 Extract TfmScore             tokens 45.3k in · 12.1k out · 58.1k total   │
│ ⠋ agent working · deliver · elapsed 12m34s · last output 3s ago                 │
├─────────────────────────┬─────────────────────────────┬──────────────────────────┤
│ plan (F/↑↓/D)          │ agent (O)    (C fold)      │ thinking (T)              │
│ all/todo/active/failed │ 12:34:56 » read TRACKER.md  │ 12:34:57 ◎ goal Extract… │
│                        │ 12:34:58 ◆ DONE verifying   │          ? hyp The name…  │
│ ┌── L0 Truth harness   │ 12:35:02 » edit TfmScore.cs │          ✎ evidence…     │
│ │ L0.1 ✅ Truth rst    │ 12:35:45 ◀ exit code 0     │          → action rename  │
│ │ L0.2 ✅ Stub A       │                             │                          │
│ ├── L1 Architecture    │                             ├──────────────────────────┤
│ │ L1.1 ✅ Contracts    │                             │ gates                    │
│ └── L2 …              │                             │ build  ✓ pass  2m12s     │
│ ┌── L3 Database        │                             │ tests  … running 3m01s   │
│ │ L3.1 TODO Schema    │                             │ lint   - skip            │
└─────────────────────────┴─────────────────────────────┴──────────────────────────┘
└─ [P] pause [K] kill [S] skip [I] inject [G] status [Q] quit [A] abort [H] hb ─┘
```

### Key bindings

| Key | Effect |
|---|---|
| `P` | Pause after the current session (loop idles, process stays up) |
| `R` | Resume a paused / needs-attention run / approve owner gate |
| `K` (double-tap) | Kill the current agent session (conductor re-evaluates) |
| `S` (double-tap) | Skip the current stage — flagged loudly for human review |
| `A` (double-tap) | Abort now (kills session, stops conductor) |
| `Q` | Quit after the current session (state saved; `run` continues later) |
| `I` | Inject an instruction for the agent's next session |
| `E` | Edit the selected stage's config |
| `G` | Run the LLM status agent |
| `H` | Toggle heartbeats on/off |
| `T` | Open thinking panel |
| `O` | Open agent output history |
| `L` | Open timeline modal |
| `F8` | Open replay / time-travel modal |
| `F1` | Open health metrics modal |
| `N` | Open confidence modal |
| `B` | Open repo info modal |
| `C` | Toggle agent fold expand/collapse |
| `D` | Open the stage's plan doc section |
| `V` | Open git diff view |
| `X` | Open current session prompt |
| `F` | Cycle plan tree filter (All/Todo/Active/Failed) |
| `/` | Search focus |
| `↑` / `↓` | Navigate plan tree |
| `Enter` | Toggle stage expand/collapse |
| `Esc` / `q` | Close modal |

## Face — companion TUI (optional)

The dashboard above runs *inside* `conductor run`'s own process. **Face** is a separate,
optional companion app that attaches to a run over HTTP/SSE instead — useful for watching
from a second terminal, or once you've set `--no-dashboard`. It needs the control plane
enabled on the conductor side:

```powershell
conductor run -p <plan> --control-plane [--control-plane-port <n>]
```

The Face is **`face-go`** (Go + Bubble Tea) — a single ~22MB binary, no runtime dependency,
~5ms startup, wired to the control plane's HTTP+SSE endpoints. `conductor run` spawns it
automatically; `conductor face` attaches a second one to a live run.

- **`face-go`** (Go + Bubble Tea)
  ```powershell
  cd face-go
  go build -o bin/conductor-face.exe ./cmd/conductor-face/
  .\bin\conductor-face.exe --demo    # offline synthetic data, no conductor process needed
  .\bin\conductor-face.exe            # live, default http://127.0.0.1:4317
  ```

> The original TypeScript + Ink face (`face/`) was retired in M7 once `face-go` reached
> day-to-day usability. Its history is in git.

`face-go` (ten tabs, `a h t s c e g r k l` / `1`–`9`/`0`): `:` command palette
(pause/resume/abort/kill/skip/goto/…, destructive verbs confirm first) · `p` plan sidebar (self-scrolls
to the active stage) · `i` inject a note for the next session · `e` templates — edit prompt/persona
files (full cursor editor) and preview the compiled prompt per session kind · `h` session history ·
`t` timeline (drill into any event) · `r` report/query console (ad-hoc SQL against run.db, history +
wide-table scroll) · `k` knowledge — ledger + tracked bugs, and file a note / file a bug / resolve one
in place (`n`/`b`/`x`) · `l` Telegram guided setup · `s` supervised-processes view · `/` inline
transcript search · `T` fold thinking — press `?` for the authoritative, up-to-date list.

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
| `tier` | string | `"fast"` (per-session under perPhase) or `"full"` (phase end only). Default `"full"`. |
| `parallel` | bool | Run concurrently with other parallel gates in the same batch. |
| `stages` | string[] | Only run when the current stage id is in this list. |
| `timeoutMinutes` | int | Per-gate timeout. Default 20. |

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

The tracker is a Markdown file (typically `TRACKER.md` or `CONDUCTOR-START.md`)
that serves as both the human-readable progress document AND the machine-parsable
state that Conductor reads.

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
  state.json            Resumable run state (atomic writes; .corrupt quarantine)
  events.jsonl          Append-only event log (event-sourced backbone — 22 event types)
  REPORT.md             The AFK report — the only file conductor commits
  conductor.log         Orchestrator text log
  control.json          Transient control verbs from the CLI/TUI/Telegram
  conductor.lock        PID lock (two conductors can't fight over one repo)
  lessons.md            Rolling lessons brief (bounded, rotating)
  followups.md          Tracked follow-up items from audits
  queue/                Injected instruction chain for the next session
  logs/
    session-NNN.jsonl           Raw agent stream per session
    session-NNN.prompt.md       Exact prompt each session got
    conductor-YYYY-MM-DD.json   Structured JSON log (for conductor log --query)
  lanes/                Analysis lane artifacts
  handovers/            Phase-end audit handover documents
  audits/               Post-hoc audit replay outputs
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
- **Everything is resumable.** State is persisted on every transition (atomic
  `state.json` + event log). Kill conductor, reboot, Ctrl+C — running `conductor run`
  again picks up where it left off.

## Testing without burning tokens

`tools/fake-agent.ps1` impersonates opencode's stream-json and can simulate
success / stall / red-gates / usage-limit. Unit tests: `dotnet test`.

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
