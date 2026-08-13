# CLI reference

`conductor --help` (or `conductor <verb> --help`) is always the authoritative, current list straight
from the binary. This page covers the verbs you reach for daily and what each is *for*; it does not
try to duplicate every flag.

## The front door

`conductor` with no arguments is not the verb list any more — it is the **hub**: this machine's state
home, the runs answering on the fleet ports (4317-4336), the runs the catalogue remembers, the plans
discoverable from where you are standing, and four things to do about any of it — **attach** a Face to
a run that is already going, **start** one from a plan here, **plan new**, or **history**.

**start** is the whole launch drill in one flow: the plan's `journey` itinerary first (read-only —
nothing is written and nothing spawns at preview time), then a yes/no, then the engine launches
*detached* through the same path as `run --detach` — child output captured to a per-launch
`logs/detach-<stamp>.log`, the bound URL read back from the child's discovery file — and the Face
attaches to that URL. Killing the Face afterwards leaves the engine running; that is the point.

Every run's status is the *reconciled* word, not the column: an engine that was killed never wrote the
correction, so a row that still says `running` with nothing holding its store lists as `orphaned`.

Zero plans here is a normal outcome and so is eleven — the hub lists what it finds and never asks
which one you meant. The front door of a CLI may not interrogate the person who just typed its name,
so it discovers plans rather than *resolving* one.

Redirected output is a different question: `conductor | cat` or `conductor > board.txt` prints the
same board and exits 0, with no picker and no prompt, so a script cannot hang on a keystroke nobody is
there to press.

Nothing else moved. `conductor --help` lists exactly the verbs it listed before, `conductor --version`
still answers the build, and `conductor <unknown-verb>` is still an error — the hub is reached by
rewriting an *empty* argv to a hidden `hub` verb, because Spectre's default-command mechanism would
have turned that last sentence into a lie (an unknown first token becomes the default command's
argument). `conductor hub` reaches the same screen when you have already typed something.

## Zero flags by default

Every command resolves the plan from `-p`, else a single `*.plan.json` in the directory you are
standing in (or in `./plans/`), else the `CONDUCTOR_PLAN` environment variable. So `cd` into a repo
that has one — which is what `conductor init` writes — and everything works with no `-p`.

The directory comes before the environment variable on purpose (bug #20): a session's environment
carries `CONDUCTOR_PLAN` pointing at the run that spawned it, so a scratch rig launched from inside
one used to drive that run's plan instead of its own. The override only happens when the directory
names exactly ONE plan — a tree with several (this repo has eleven under `plans/`) still resolves
through the variable — and it is never silent: the warning naming both files goes to stderr.

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
             --detach             launch the engine into its own process group and return: the
                                  child runs headless with its stdout+stderr captured to
                                  <stateDir>/logs/detach-<stamp>.log, and the URL printed is read
                                  back from the child's own discovery file (pid-checked, given a
                                  2s settle), never predicted from --port. Your shell can close;
                                  the run does not go with it. Attach later with `conductor face`.
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
| `status` | Plan, tracker, and session status from the database, in under a second. `--deep` adds an LLM narrative (slower, opt-in). In a directory that names no plan — none here, or several and nothing choosing between them — it widens instead of failing: the machine's board (live runs from the port probe, what the catalogue remembers, the plans found here), a note on stderr saying why, and exit 0. `-p <plan>` narrows it back to one run from anywhere. |
| `watch` | Block silently on a live run and return only when something needs judgment: a park, a churn loop, a phase gate red twice, the engine gone, the run ended. `--json` for the brief, `--timeout` for a heartbeat, `--hook` to hand it to a supervisor. |
| `watches` | What is armed on this machine: every live run beside the supervisor block watching it, how much of its hourly fuse is burnt, where a remote wake travels, and the park-push cap in force (`limits.maxPushesPerIncident`). Read-only — a loopback `GET /state` and two file reads, no token and no POST. `--json` for machines, `--ports` for a non-default window. Rows nothing would wake anybody for are called out. |
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
| `spend` | What this **whole machine** spent — today, this week, this month — across every catalogued store, with no repo and no plan argument. Billed rows only; each real run counted once even when the catalogue holds it twice. Flags: `--since`, `--runs`, `--home`, `--json`. |

```
conductor budget            # profile this repo's runs and prescribe the next cap
conductor money             # what every run of this repo cost
conductor money --run <ID>  # one run, per stage and per checkpoint
conductor spend             # what this machine spent today / this week / this month
conductor spend --since 1mo # one window instead of the ladder
```

All three read the machine-wide run catalogue rather than the current run's state, so they answer
after a run has ended and from any directory.

`spend` differs from `money --since` in the one way that matters. `--since` elsewhere filters **whole
runs** by last activity, so a run that started in June and closed a checkpoint this morning puts its
entire June bill inside "this week". `spend` windows at **session** granularity — the `costs` table
has no timestamp of its own, and `sessions.started_utc` is the only anchor it can be joined to — so a
run straddling the boundary contributes only the sessions inside it. Billed rows whose session has no
start time are reported as `undated`: counted in the lifetime total, counted in no window, never
silently dropped.

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
| `face --archive <run>` | Open a **finished** run in the Face. The engine serves that run's `run.db` through a read-only control plane — sessions, money, timeline, report, all the live tabs — with no engine process and no write token, so every write affordance hides itself and every POST is refused with "this run is finished". Takes the same selector `history` does. `--serve` prints the url and holds it open instead of launching a Face; `--port <n>` moves it off the default 4400, and a port inside the 4317-4336 fleet window is **refused** — anything answering there is listed by `ps` and by the hub as a live run, so an archive never shows up in `ps`. The run picker reaches the same place: enter on a past row opens it, and a row whose database this machine can no longer read is still listed and answers with the reason. |
| `face [--pick]` | Attach a Face to a run. With no flag the run in this directory wins without a prompt; `--pick` always shows the picker, which is the one list of everything on this machine — the runs answering on 4317-4336 and, under them, the ones the catalogue remembers, reconciled so a run whose engine is dead never reads as `running`. Enter on a live row attaches; enter on a past row opens it read-only (see `face --archive`). The history half is a screenful: when there is more, the heading says `N of M · conductor history for the rest` rather than presenting its first page as the machine. Once attached, `:` then `switch` shows the same list again and moves this Face to another run **without restarting it** — theme, tab and sidebar survive, and the write token is the new run's or none. Tokens travel in `CONDUCTOR_FLEET`, never in argv. |
| `ps` | Every conductor run on this machine — repo, plan, run id, stage, status, port, pid, uptime. The run in the current directory is marked `*`. Read-only; `--json` for machines. |
| `catalogue` | Every run store this machine has, and whether any of them hold the same run twice. `catalogue repair` says what it would collapse and writes nothing; `catalogue repair --apply` collapses it, after backing up every store it touches. It never writes a store a live engine is using, and it identifies a run by its run id — not by which store it happens to sit in. |
| `run close <id>` | Close the record of a run whose engine never got to close it — killed, rebooted, or reaped with the shell that started it. Writes a terminal status (`--status closed`, the default, or `completed`/`aborted`) and stamps the instant the run *actually* stopped, taken from its last recorded activity unless you pass `--ended`. `--reason` goes into the run's event spine, so the change says who made it and why. `--dry-run` shows what would change. |
| `run adopt <id>` | Annotate a run record without touching its lifecycle: `--reason` is journalled against the run, the status is left exactly where it was. For a record you mean to keep rather than close. |

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
