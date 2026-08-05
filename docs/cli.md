# CLI reference

`conductor --help` (or `conductor <verb> --help`) is always the authoritative, current list straight
from the binary. This page covers the verbs you reach for daily and what each is *for*; it does not
try to duplicate every flag.

## Zero flags by default

Every command resolves the plan from `-p`, else the `CONDUCTOR_PLAN` environment variable, else
`./conductor.plan.json`. So `cd` into a repo that has one — which is what `conductor init` writes —
and everything works with no `-p`.

## Pre-flight

| Verb | What it does |
|---|---|
| `demo` | Run a complete plan end to end against a built-in fake agent, in a throwaway directory. No credentials, no spend, no PowerShell. The fastest honest answer to "does this work on my machine". |
| `journey` | Pre-flight itinerary: identity, stages, gates, human moments. No state written, no agent spawned. Run this before `run`. |
| `doctor` | <2s health check: agent CLI, git, face binary, DNS/disk/API, budget, Telegram. Says exactly what's missing. Not a resume preview — see `status` for that. |
| `init` | Scaffold a plan + TRACKER.md + editable templates, with gates chosen from the detected repo type (dotnet/node/go/rust/python). `--from-idea "…"` turns prose into stages in the same command. |
| `new-plan` | Bare-minimum scaffold: plan + tracker, no gate detection. |

## Run

```
run          Run the plan: engine + control plane + Face TUI, one command. Resumes from saved
             state; Ctrl+C is safe.
             --dry-run            print the next session's prompt, spawn nothing
             --once               run exactly one session then stop
             --max-sessions <N>   stop after N sessions this process
             --paused             start idle: dashboard + control plane up, no session spawns
                                  until you resume
             --headless           plain line output, no Face TUI (control plane still runs)
             --no-face            control plane runs, but nothing is spawned to view it
```

`conductor run` is **one process tree**: engine + localhost HTTP/SSE control plane, and it spawns the
Face automatically. You never launch the Face binary yourself. If the Face dies the run continues;
`conductor face` attaches a fresh one.

### The three things called "resume"

- **`conductor run -p <plan>`** resumes a run that is not currently a live process — you closed the
  terminal, the machine restarted, a previous run ended. It reads the latest persisted `RunState`
  (`.conductor/run.db`, with legacy `.conductor/state.json` as a fallback) and continues from the
  recorded session count, stage, and budget. No flag needed. `conductor journey` tells you what it
  will do before you run it.
- **`--paused`** is a flag on `run`, not a separate mechanism: dashboard and control plane come up
  with no session spawning until you resume. Useful for reviewing the plan in the Face first.
- **`conductor resume`** is a *control verb* for a run that is already live and paused or parked
  (after `conductor pause`, an owner gate, or a budget cap). It does not start a process. From the
  Face, same action via the command palette or `R`.

## Control

These work from any terminal, out-of-process, via a control-file drop — and from the Face's `:`
command palette, and from Telegram.

| Verb | What it does |
|---|---|
| `pause` | Pause after the current session |
| `resume` | Resume a paused / needs-attention conductor |
| `kill` | Kill the current agent session (loop re-evaluates) |
| `skip` | Skip the current stage (flagged for human review) |
| `abort` | Kill the session and stop the conductor |
| `approve` | Approve an owner-gated stage so the conductor advances |
| `retry-stage` | Reset attempt counter, re-queue deliver for the current stage |
| `rollback` | Reset working tree to the stage start commit (`--yes` to force) |
| `goto <ID>` | Jump to a different stage |
| `pause-after-stage` | Park after the current stage completes |
| `inject <txt>` | Queue an instruction for the agent's next session |
| `heartbeat` | Force a fresh `.conductor/REPORT.md` now (only meaningful mid-session) |
| `rollover <tokens\|off\|clear>` | Set/clear this run's session-token rollover (run-state only) |
| `plan set/reload/add-stage/import` | Plan management: hot-update fields, reload, add stages, import prose or markdown |

## Diagnostics

| Verb | What it does |
|---|---|
| `status` | Plan, tracker, and session status from the database, in under a second. `--deep` adds an LLM narrative (slower, opt-in). |
| `watch` | Block silently on a live run and return only when something needs judgment: a park, a churn loop, a phase gate red twice, the engine gone, the run ended. `--json` for the brief, `--timeout` for a heartbeat, `--hook` to hand it to a supervisor. |
| `gate` | Re-run the gate battery at HEAD, no agent spawned. `--full` for the full battery (default: fast tier). |
| `report` | Regenerate `.conductor/REPORT.md` from current state. |
| `log` | Query the structured JSON log: `-q "stage=P7 and gate=build and outcome=fail"`. |
| `tasks` | Sub-task graph per checkpoint from the event log. |
| `task` | Checkpoint CRUD from run.db: `--list`, `--done`, `--in-progress`. **This is the one claim path** — hand-editing a tracker row claims nothing. |
| `note` / `bug` | Knowledge ledger + tracked bugs that outlive the session that found them. |
| `audit <ID>` | Post-hoc audit replay (read-only, `--replay`). |
| `bg` | Background process management: `start\|status\|logs\|stop`. |
| `chat "…"` | Ask questions about a running plan (MCP access to run.db, ledger, control verbs). |
| `mcp-serve` | Run the MCP task server (JSON-RPC 2.0 over stdio). |
| `completion` | Generate shell completion scripts (`powershell` or `bash`). |
| `version` | What this binary is: semver, git sha, build date — stamped at build — and *which file answered*. `--json` for machines, `--short` for scripts. Takes no plan and works in any directory. |
| `update` | Check the latest release, and swap this binary for it. `--check` looks without installing. Verifies the download's checksum, then runs it and asks its version before replacing anything, and **refuses while a run is live**. |

## Token and money

| Verb | What it does |
|---|---|
| `budget` | Measure this repo's token budget **from its own runs** and prescribe the next one: session floor, wrap-up spend, cap, nudge-versus-floor, rollover rate. No argument profiles the current repo. Filters: `--repo`, `--plan`, `--since`, `--json`. |
| `money` | Price a run or a project from its own ledger: sessions, tokens, cache-read share, cost, checkpoints, tokens and dollars per checkpoint, plus the windows either side of a cap change, the per-stage split and the calendar month. Scopes: `--run`, `--project`, `--since`, `--plan`, `--json`. |

```
conductor budget            # profile this repo's runs and prescribe the next cap
conductor money             # what every run of this repo cost
conductor money --run <ID>  # one run, per stage and per checkpoint
```

Both read the machine-wide run catalogue rather than the current run's state, so they answer after a
run has ended and from any directory.

`budget` prescribes **two** numbers, not one — `limits.maxSessionTokens` (the ceiling) and
`limits.softBreakRatio` (where the wrap-up nudge lands) — and prints them as a `limits` block to paste
into the plan. The rule it checks them against: a cap only helps if the **nudge** clears the median
session that actually closed a checkpoint. A nudge below that converts nothing, because sessions keep
dying at the hard ceiling mid-work instead of wrapping up cooperatively. Derivation and the measured
numbers behind it: [`docs/dev/TOKEN-BUDGET-TUNING.md`](dev/TOKEN-BUDGET-TUNING.md).

## Across runs, on this machine

| Verb | What it does |
|---|---|
| `history` | Browse past runs from this machine's catalogue, read-only. No argument lists them; pass a run id, repo or slug to open one and replay its spine. Filters: `--repo`, `--plan`, `--since`, `--limit`, `--json`. |
| `ps` | Every conductor run on this machine — repo, plan, run id, stage, status, port, pid, uptime. The run in the current directory is marked `*`. Read-only; `--json` for machines. |

## Overriding the defaults

Conductor tries to need no configuration and then get out of the way. When you do want to steer:

| You want | Reach for |
|---|---|
| See what it *would* do, spend nothing | `run --dry-run`, or `journey` |
| Supervise the first session | `run --once` |
| Look before it moves | `run --paused` |
| No TUI (CI, redirected output) | `run --headless` |
| No dashboard at all | `run --no-face` |
| Approve every session by hand | `limits.approvalMode: true` in the plan |
| Approve at a specific stage | `stages[].ownerGate: true` |
| Cap the spend | `limits.maxRunCostUsd` / `maxRunTokens` |
| No gates at all (docs/spike plan) | `"gates": []` — supported, not a misconfiguration |
| A different shell for one gate | `gates[].shell` — see [platforms.md](platforms.md) |
