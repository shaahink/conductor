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
| `demo` | Run a complete plan end to end against a built-in fake agent, in a throwaway directory. No credentials, no spend, no PowerShell. The fastest honest answer to "does this work on my machine". `--from <file>` drives *your* board instead of the built-in one — a spec-kit `tasks.md`, a Task-Master `tasks.json`, a plain markdown checklist or a conductor plan/tracker, converted with no model call — which answers the next question, "will it drive *mine*", before you point it at a real agent. `--keep` leaves the directory behind to poke at, and `-o|--output <DIR>` builds it where you say instead of in a temp directory that is removed when done. |
| `preflight` | **The whole launch drill, one verdict, one exit code.** Six legs: `doctor` (0 fail), journey resolution (workflow + model per stage), the next session's prompt composed and measured, running engine versus the latest release, a stale-engine check (are the sources that build this binary newer than the binary?), and the tracker handoff block (an escalation request left in it parks session one before anything spawns). The drill decides from the same inputs the run loop schedules on — as the loop PREPARES them at startup: the declared plan projected over the work graph in an existing `run.db` (the loop syncs the declaration into the graph before its first read; the drill models the same sync, read-only) and the saved state after both halves of the loop's crash recovery (state.json's, and the event log's orphan scan of the same `run.db`). No agent spawns and preflight creates nothing under the plan's `.conductor/` — not even SQLite's `-shm`/`-wal` sidecars: a cleanly-closed store is opened `immutable`; resolving where the plan's `run.db` lives does register the (repo, plan) pair in the machine-level `catalogue.json`, exactly as plain `doctor` does. `--no-auth-check` and `--no-update-check` mean exactly what they mean on `doctor`. |
| `journey` | Pre-flight itinerary: identity, stages, gates, human moments. No state written, no agent spawned. Run this before `run`. |
| `doctor` | <2s health check: agent CLI, git, face binary, DNS/disk/API, budget, Telegram. Says exactly what's missing. Not a resume preview — see `status` for that. |
| `plan new` | Authoring from nothing: one command from an empty repo to a plan, a tracker and the editable templates, **doctor-clean by construction** — the agent block names a CLI this machine actually has, and no scaffolded template spells the escalation token. `--from-idea` takes free prose, a PRD path or an existing tracker; a structured document is parsed for free, prose needs a model you name with `--advisor`. `--agent <COMMAND>` writes a specific agent CLI into the scaffold instead of whichever of claude/opencode this machine has, and `-o|--output <DIR>` / `--name <NAME>` place and name it. The JSON never has to be opened. |
| `init` | Scaffold a plan + TRACKER.md + editable templates, with gates chosen from the detected repo type (dotnet/node/go/rust/python). `--from-idea "…"` turns prose into stages in the same command; `--model <MODEL>` names the model the advisor uses to read that prose (ignored for a structured document, which needs none). `-o|--output <DIR>` scaffolds somewhere other than the cwd and `--name <NAME>` overrides the plan name, which otherwise comes from the directory. |
| `new-plan` | Bare-minimum scaffold: plan + tracker, no gate detection. `-o|--output <DIR>`, `--name <NAME>` as on `init`, and `--repo <PATH>` for an absolute repo root other than the output directory. |

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
             --no-control-plane   no localhost HTTP/SSE control plane at all. Implies
                                  --headless, because the Face needs it
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
| `approve` | Approve whatever the run is parked on. An owner gate advances; a budget park has its spend ceiling **raised** — by `--amount <usd>` / `--tokens <n>`, or by one more of the plan's own cap when you give neither. The run states the new ceiling; nothing already spent is forgiven. A raise that would leave any reached half of the ceiling (cost or tokens) at or under the spend is refused whole, naming the number to type — the run resumes only when both halves clear. An amount on a non-budget park is refused, not ignored. |
| `retry-stage` | Reset attempt counter, re-queue deliver for the current stage |
| `rollback` | Reset working tree to the stage start commit — a real `git reset --hard`, so it **destroys uncommitted work and drops every commit made since that head**. `--yes` confirms the destructive action; `--force` is the separate one, and it means *proceed even though the tree is dirty* — it does not stash that tree, it discards it. Refused when no stage-start head has been recorded, and it applies only outside a session: arriving mid-session it takes effect when the session ends. |
| `goto <ID>` | Jump to a different stage |
| `pause-after-stage` | Park after the current stage completes |
| `inject <txt>` | Queue an instruction for the agent's next session |
| `heartbeat` | Force a fresh `.conductor/REPORT.md` now (only meaningful mid-session) |
| `rollover <tokens\|off\|clear>` | Set/clear this run's session-token rollover (run-state only) |
| `plan new/set/reload/add-stage/import` | Plan management: scaffold one, hot-update fields, reload, add stages, import prose or markdown. `import` reads four shapes deterministically — a conductor plan/tracker document, a spec-kit `tasks.md`, a Task-Master `tasks.json`, a plain markdown checklist — and says which one it read; only a document none of them recognises falls through to the advisor and costs a model call. Detection is by content, not filename. A sub-command the dispatcher does not know is refused and names the ones it does — it no longer falls through to the plan summary. `set` refuses a key the plan schema does not declare unless you pass `--create`, because nothing reads an undeclared key; `import` takes `--model <MODEL>` for the one shape that needs an advisor call. |

## Diagnostics

| Verb | What it does |
|---|---|
| `status` | Plan, tracker, and session status from the database, in under a second. `--deep` adds an LLM narrative (slower, opt-in). In a directory that names no plan — none here, or several and nothing choosing between them — it widens instead of failing: the machine's board (live runs from the port probe, what the catalogue remembers, the plans found here), a note on stderr saying why, and exit 0. `-p <plan>` narrows it back to one run from anywhere. |
| `watch` | Block silently on a live run and return only when something needs judgment: a park, a churn loop, a phase gate red twice, the engine gone, the run ended. `--json` for the brief, `--timeout` for a heartbeat, `--hook` to hand it to a supervisor with `--hook-timeout <MINUTES>` bounding how long that command may run (default 10). `--notify <URL>` POSTs the brief on wake instead of the plan's `supervisor.remote` block, and is not bound by its hourly fuse. `--poll <SECONDS>` sets how often the event log is checked (default 2). |
| `watches` | What is armed on this machine: every live run beside the supervisor block watching it, how much of its hourly fuse is burnt, where a remote wake travels, and the park-push cap in force (`limits.maxPushesPerIncident`). Read-only — a loopback `GET /state` and two file reads, no token and no POST. `--json` for machines, `--ports` for a non-default window, `--timeout <MS>` for the per-port probe budget (default 2500). Rows nothing would wake anybody for are called out. |
| `gate` | Re-run the gate battery at HEAD, no agent spawned. `--full` for the full battery (default: fast tier). |
| `report` | Regenerate `.conductor/REPORT.md` from current state. |
| `log` | Query the structured JSON log: `-q`/`--query "stage=P7 and gate=build and outcome=fail"` — key=value pairs joined by ` and `, case-insensitive. `--tail <N>` shows only the last N matches. |
| `tasks` | Sub-task graph per checkpoint from the event log. |
| `task` | Checkpoint CRUD from run.db: `--list`, `--done`, `--in-progress`. **This is the one claim path** — hand-editing a tracker row claims nothing. A claim carries its receipts: `-c`/`--commit <SHA>` and `-e`/`--evidence <TEXT>`. The other moves put a card back or park it: `--todo <CP>` reopens a done/skipped/blocked card, `--blocked <CP>` marks work that cannot proceed, `--skipped <CP>` marks work deliberately not delivered, and `--blocked-until <ISO8601>` (with `--reason`) is the *timed* wait, which is a different thing — conductor sleeps until that instant and spawns one more session. `--amend <CP> --note "<TEXT>"` records an acceptance correction on a card without moving it. Every move prints the card's post-fold status and exits non-zero if the transition is refused, which is why the output is trustworthy and the intent is not. |
| `note` / `bug` | Knowledge ledger + tracked bugs that outlive the session that found them. `note` takes `-k`/`--kind <finding\|observation\|trap\|decision>` (default `note`) and `-s`/`--stage <STAGE>`. `bug new` takes `-d`/`--detail <TEXT>` for the repro, `-s`/`--severity <low\|medium\|high>` (default medium) and `--stage <STAGE>`; `bug list` prints the open ones (`--all` includes the closed), and `bug fix <id> --wontfix` closes one as wontfix rather than fixed. |
| `audit <ID>` | Post-hoc audit replay (read-only, `--replay`). |
| `bg` | Background process management: `start\|status\|logs\|stop`. On `start`: `--purpose <LABEL>` names the child (it defaults to the executable name) and `--cwd <DIR>` sets its working directory (default: the plan's repo root). On `logs`: `-t`/`--tail <N>` lines, default 30. |
| `worktree` | What attempt worktrees conductor has on disk: which run made each, which are orphans from a run that died, and which belong to a live run. Read-only; `--reap` removes the orphans, and it never touches a worktree you made or a live run's. The engine runs the same sweep at startup, so this verb is mostly for the one case that survives it: a tree whose build output is still locked by a process that outlived the run. |
| `chat "…"` | Ask questions about a running plan (MCP access to run.db, ledger, control verbs). |
| `mcp-serve` | Run the MCP task server (JSON-RPC 2.0 over stdio). The engine wires this up itself for a session; the paths exist for driving it by hand — `--events <path>` the events.jsonl, `--journal <path>` the MCP side-journal, `--run-db <path>` the store (default: beside the events file, the pre-K3.1 layout), `--run-id <id>` for event authorship, `--state-dir <path>` for the bg tools, `--repo <PATH>` the repo root the `bg_start` tool launches children in, and `--session <number>`, which is stamped on every bg child the server starts. |
| `mcp-observe` | KS8.1: the **read-only** MCP surface (JSON-RPC 2.0 over stdio). Serves this machine's run catalogue as MCP *resources* — `conductor://history`, `conductor://runs/{run}/status`, `conductor://runs/{run}/money` — and serves **no tools at all**: `tools/list` is empty and `tools/call` is refused. Control operations are excluded by design, not by a flag (ADR-0007). Reads through `Mode=ReadOnly` SQLite, so it cannot write even by mistake. `--home` serves a state home other than this machine's. |
| `completion` | Generate shell completion scripts (`powershell` or `bash`). |
| `version` | What this binary is: semver, git sha, build date — stamped at build — and *which file answered*. `--json` for machines, `--short` for scripts. Takes no plan and works in any directory. |
| `update` | Check the latest release, and swap this binary for it. `--check` looks without installing. Verifies the download's checksum, then runs it and asks its version before replacing anything, and **refuses while a run is live**. |

## Token and money

| Verb | What it does |
|---|---|
| `budget` | Measure this repo's token budget **from its own runs** and prescribe the next one: session floor, wrap-up spend, cap, nudge-versus-floor, rollover rate. No argument profiles the current repo. Filters: `--repo`, `--plan`, `--since`, `--json`. |
| `money` | Price a run or a project from its own ledger: sessions, tokens, cache-read share, cost, checkpoints, tokens and dollars per checkpoint, plus the windows either side of a cap change, the per-stage split and the calendar month. Scopes: `--run`, `--project`, `--since`, `--plan`, `--json`. |
| `spend` | What this **whole machine** spent — today, this week, this month — across every catalogued store, with no repo and no plan argument. Billed rows only; each real run counted once even when the catalogue holds it twice. Flags: `--since`, `--runs`, `--home`, `--json`. |
| `otel` | Export a run's event log to an **OTLP/HTTP collector** as one trace: run → stage → session → gate and tool spans, `gen_ai.*` usage attributes with the cache split, and the per-turn context curve as span events. Read-only — it reads the event log and posts, it never writes the run. Flags: `--endpoint`, `--run`, `--service`, `--dry-run`, `--out`. |

```
conductor budget            # profile this repo's runs and prescribe the next cap
conductor money             # what every run of this repo cost
conductor money --run <ID>  # one run, per stage and per checkpoint
conductor spend             # what this machine spent today / this week / this month
conductor spend --since 1mo # one window instead of the ladder
conductor otel --dry-run    # render the trace and print it, post nothing
conductor otel --endpoint http://localhost:4318  # ship it to a collector
```

`budget`, `money` and `spend` read the machine-wide run catalogue rather than the current run's
state, so they answer after a run has ended and from any directory. `otel` takes a run the same way
`money --run` does, and `--dry-run`/`--out` render the exact OTLP payload without needing a
collector to be up — which is also how the span shape is pinned in tests.

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

Every verb on this page that reads the machine's *catalogue* rather than one plan's `run.db`
takes `--home <PATH>` and reads a state home other than this one's: `history`, `budget`,
`money`, `spend`, `catalogue`, `github`, `mcp-observe` and `run` itself. It is how a measurement
is pointed at a copy of a store instead of at the one a live engine is writing.

| Verb | What it does |
|---|---|
| `history` | Browse past runs from this machine's catalogue, read-only. No argument lists them; pass a run id, repo or slug to open one and replay its spine. Filters: `--repo`, `--plan`, `--since`, `--limit`, `--json`. |
| `history export` | KS8.2: a finished run as an **ATIF trajectory** (Agent Trajectory Interchange Format — the Harbor / Terminal-Bench interchange spec, `ATIF-v1.7`). `history export <run> --atif` writes to stdout, `-o`/`--output <FILE>` to a file, and `--all -o <DIR>` writes the whole catalogue, one `<shortRunId>.atif.json` per run. Each session is one agent step; the gate battery, the checkpoints it confirmed and the commits it landed are that step's observation. **Billed dollars only** — conductor has no price table, so ATIF's own cost derivation is not applied, and `prompt_tokens` includes `cached_tokens` per that same spec. Read-only. |
| `face --archive <run>` | Open a **finished** run in the Face. The engine serves that run's `run.db` through a read-only control plane — sessions, money, timeline, report, all the live tabs — with no engine process and no write token, so every write affordance hides itself and every POST is refused with "this run is finished". Takes the same selector `history` does. `--serve` prints the url and holds it open instead of launching a Face; `--port <n>` moves it off the default 4400, and a port inside the 4317-4336 fleet window is **refused** — anything answering there is listed by `ps` and by the hub as a live run, so an archive never shows up in `ps`. The run picker reaches the same place: enter on a past row opens it, and a row whose database this machine can no longer read is still listed and answers with the reason. |
| `face [--pick]` | Attach a Face to a run. With no flag the run in this directory wins without a prompt; `--pick` always shows the picker, which is the one list of everything on this machine — the runs answering on 4317-4336 and, under them, the ones the catalogue remembers, reconciled so a run whose engine is dead never reads as `running`. Enter on a live row attaches; enter on a past row opens it read-only (see `face --archive`). The history half is a screenful: when there is more, the heading says `N of M · conductor history for the rest` rather than presenting its first page as the machine. Once attached, `:` then `switch` shows the same list again and moves this Face to another run **without restarting it** — theme, tab and sidebar survive, and the write token is the new run's or none. Tokens travel in `CONDUCTOR_FLEET`, never in argv. `--demo` runs the TUI against synthetic data with no conductor process at all, and `--timeout <MS>` bounds the per-port probe the picker runs (default 2500). |
| `ps` | Every conductor run on this machine — repo, plan, run id, stage, status, port, pid, uptime. The run in the current directory is marked `*`. Read-only; `--json` for machines, `--ports <FIRST-LAST>` for a window other than 4317-4336 and `--timeout <MS>` for the per-port probe budget (default 2500). |
| `catalogue` | Every run store this machine has, and whether any of them hold the same run twice. `catalogue repair` says what it would collapse and writes nothing; `catalogue repair --apply` collapses it, after backing up every store it touches. It never writes a store a live engine is using, and it identifies a run by its run id — not by which store it happens to sit in. |
| `run close <id>` | Close the record of a run whose engine never got to close it — killed, rebooted, or reaped with the shell that started it. Writes a terminal status (`--status closed`, the default, or `completed`/`aborted`) and stamps the instant the run *actually* stopped, taken from its last recorded activity unless you pass `--ended`. `--reason` goes into the run's event spine, so the change says who made it and why. `--dry-run` shows what would change. |
| `run adopt <id>` | Annotate a run record without touching its lifecycle: `--reason` is journalled against the run, the status is left exactly where it was. For a record you mean to keep rather than close. |

## The inbox — what the owner said about this project

Notes arrive from the bot (a voice note, a document, a caption) and live under `.conductor/inbox`,
outside git, surviving the run that received them. A session reads the unread ones at its next
boundary; these verbs are how a person reads the same inbox.

| Verb | What it does |
|---|---|
| `inbox list` | Every note: id, when it arrived, kind, whether a session has read it, whether audio is sitting there untranscribed. `--unseen` for only the unread, `--full` for whole texts rather than summaries, `--json` for machines. |
| `inbox show --id N` | One note, whole — what the prompt's `CLIPPED` marker points at. Names the audio file and the transcript sidecar on disk. |
| `inbox add --file <PATH> [--text ...]` | File a note from this machine: an exported voice message, a meeting recording, a document. The file is copied into the inbox and the note goes through the same store the bot writes to. |
| `inbox transcribe --id N \| --all` | Run the configured `courier.transcribe.command` over notes whose audio has no transcript yet — the verb behind "the audio is kept and can be read out later". Low-confidence stretches come back marked `[?: like this]`. |
| `inbox parked` | The machine-level **dead-letter box** — notes the courier accepted but could not file, because the project they were about had moved or gone. They are not in any repo's inbox and they were not dropped; they wait under the state home until you move them or the project comes back. A bot that loses a message is the failure this whole surface exists to prevent, so nothing is discarded on a routing miss. |
| `inbox prune --seen \| --older-than DAYS \| --id N [--yes]` | **The only deletion path in conductor.** Nothing else removes a note, its audio or its transcript — not reading one, not marking it seen, not a new run. It needs a filter, it prints what it would take, and it deletes nothing without `--yes`. |

**Which project a note is about** (DV3.4). Notes arrive in a chat, not in a repo, so conductor works
it out in this order and says which rung answered:

1. **Reply to a push — zero typing.** Every message conductor sends opens with `<plan> · s<n>`, so
   replying to last night's checkpoint push files the note against *that* project.
2. **`/project <name>`** in the chat — sticky, stored under the machine's state home, and it survives
   a restart. Sent inside a supergroup **topic** it selects for that topic only, so one topic per
   project routes with no command at all. Bare `/project` shows what is selected and what this
   machine has.
3. **The run that received it**, when nothing else said otherwise.

An unknown or ambiguous name is refused **by name**, listing what this machine actually has. A
project whose checkout has moved or vanished cannot be filed against — that note is **parked** in
`<state home>/dead-letter/` with its audio and the sender is told where it is. Nothing is dropped.

## The courier — one bot, always awake

`conductor courier` is the only verb in this CLI that is about the **machine** rather than a project.
It owns `CONDUCTOR_TELEGRAM_TOKEN`, polls whether or not a run is live, and files each note into
whichever project it is about. That is the whole point: feedback should be possible when you *have*
it, at midnight with nothing running, not only while a run happens to be up.

**It holds the token, so a run must not.** Telegram allows exactly one `getUpdates` consumer per bot
token — two pollers steal each other's updates and inbound goes unreliable for both. The courier is
therefore the *one* consumer on a machine that has one.

| Verb | What it does |
|---|---|
| `courier status` | Whether it can run at all: is the token set, how far the poll offset has got, which projects it may file into, which chats it answers, and what is missing if anything is. `--json` for machines. |
| `courier run` | Poll until stopped. Ctrl-C is a stop, not a kill: the delivery in flight is finished and its offset written before the process exits. `--once` polls a single time and prints what happened — the shape a rig and a scheduled task both use. |
| `courier install [--task-name <NAME>] [--exe <PATH>] [--no-start]` | Register the courier as a **per-user Scheduled Task**: starts at your logon, restarts on failure every minute, `LeastPrivilege` — no admin rights and no elevation prompt. `--exe` names the binary the task runs (this one, by default); `--no-start` registers without starting it now; `--task-name` is for a rig that must not touch yours. |
| `courier uninstall [--task-name <NAME>]` | Stop it and remove the registration. Nothing polls for this machine afterwards. |
| `courier restart [--task-name <NAME>]` | Stop and start it again — the fix for a courier still running the engine it was installed with. |
| `courier stop [--task-name <NAME>]` | End the running instance. It comes back at your next logon. |
| `courier allow --repo <PATH> [--plan <NAME>]` | Add a project to the allowlist. `--plan` is the name a push's identity line carries; with it omitted the plan file in the repo is read, and failing that the folder name is used. A path that is not a directory is refused rather than stored. |
| `courier deny --repo <PATH>` | Take a project off the allowlist. Notes for it are parked from then on — never filed somewhere close. |
| `courier chat --id <CHAT_ID> [--profile admin\|observer]` | Answer this chat. `admin` may file notes; `observer` may not, and nothing is downloaded on an observer's behalf. Defaults to admin. |
| `courier unchat --id <CHAT_ID>` | Stop answering it. |

**The allowlist is explicit, and it is deliberately not the state catalogue.** A run can only file
against itself or a project you named in that chat. A daemon holding the bot token could write into
every checkout this machine has ever run, so it files only into projects written down with
`courier allow`. Anything else is **parked** in `<state home>/dead-letter/` with the reason, and the
sender is told where it is.

**Which project a note is about** is DV3.4's ladder, unchanged: a reply to a push files against that
push's project, else this chat's (or this topic's) `/project` selection. The selection lives in the
machine's state home, so the courier and a live run read the same one. A courier has no local run, so
there is no bottom rung — a note that names nothing routable is parked rather than guessed at.

> **Telegram keeps an undelivered message for 24 hours, and nothing on this machine can change
> that.** The courier answers *"no run live"*, not *"machine off"*: a voice note sent on Friday night
> to a laptop that sleeps until Monday was never handed over by Telegram at all. This is the honest
> limit of a courier that runs on your own machine, and `courier status` prints it.

**It outlives a reinstall, and that is the one thing to know about upgrading it.** A running courier
holds the published `conductor.exe` open, so `tools/install.ps1` stops it before publishing and
starts it again afterwards — otherwise the publish fails on a file lock, and, worse, a courier that is
never restarted keeps running yesterday's engine for as long as the machine stays up, precisely
because it is built to survive everything else. It states the protocol it speaks in
`courier.run.json`; a newer run refuses a stale courier **by name**, with its pid and the engine it
is still running, and names `conductor courier restart` as the fix. A NEWER courier than the run is
not an error — that is the ordinary state of a machine between a reinstall and the next logon.

**Promotion: a note becomes work by one tap, and never by itself.** Every acknowledgement the
courier sends for a filed note carries a **Promote to followup** button. Press it and that note
becomes a row in that project's `.conductor/followups.md` — which is what `LaneCoordinator` turns
into a Tier-B fix lane at the next stage confirmation. Three things about it are deliberate:

- **The courier has no run, so it writes `next` in the owning-stage column.** Whichever stage that
  project confirms first picks the row up, rewrites the cell to its own id, and runs the lane once.
  A promotion made at midnight is work on Monday morning without anybody editing a table.
- **Pressing twice writes one row.** The keyboard stays on the message forever and nothing about it
  says "already used", so the second press answers with the id the first one made.
- **A note can never become an injection.** That is the third tier and it stays a deliberate verb —
  a misheard word in a transcript plus an agent running unattended is the one compound failure this
  whole path is shaped to avoid. Promotion moves a note exactly one rung, and a test asserts that no
  code path from an inbound note reaches the injection API at all.

A live run offers the same button and behaves the same way, with one difference: it knows what stage
it is on, so its row is owned by that stage rather than by `next`. Observers are refused the button
as they are refused every other callback.

Its state lives at `<state home>/courier/` — `courier.json` (what you configured), `offset.json` (how
far it has acknowledged, written *after* each delivery is handled so a crash replays rather than
loses), `courier.run.json` (what the running daemon says about itself: pid, protocol, engine, the exe
it holds open, the task that started it — written at startup, cleared on the way out), and `media/`
(where bytes land before they are adopted into a project's inbox).

## The cloud — `/cloud`, and why there is no `conductor cloud`

**There is deliberately no CLI verb here.** `/cloud` is an **owner-only chat verb** (admin profile,
refused for an observer like any other control verb), and the cloud *lane* is a plan-config block —
[`cloud`](plan-config.md), **off unless you turn it on**, with no environment override to switch it
on by accident.

The verb has two directions and the installed `claude` gives conductor only one of them:

- **Follow up on a session that already exists** — headless, so conductor drives it: `/cloud <id>
  <message>` sends the message and brings the answer back to the chat, truncated to a phone screen
  with the truncation announced.
- **Create a session** — interactive-only on the measured CLI version. It is **refused with the
  platform's own words plus the exact command to run on a terminal**. Conductor does not fake a TTY
  to get around a refusal a research-preview surface makes on purpose.

**The create direction preflights git first**, because that is the direction that clones from the
remote — an agent out there reads what the remote has, not what your working tree has. Six verdicts,
each quoting the state that produced it rather than saying "not ready": nothing to clone, detached
head, a dirty tree (with the count and the files), no upstream, the branch missing on the remote, and
the remote tip differing from local `HEAD`. A follow-up is **not** gated this way — it messages a
workspace cloned when the session was made, so refusing it for a dirty tree would be a false gate —
but the reply states the local git state anyway, so you know what that session cannot see.

Cost always reads as **a word, never a number**: conductor has no per-turn telemetry out there, and a
made-up figure is worse than an honest "unknown".

## The board, on GitHub

One way out, off by default, nothing ever read back. Conductor **pushes** a run's board to GitHub
issues; it never reads GitHub state into the run. Drag a card on GitHub and the run does not notice —
that is correct, not a gap. The tracker and the event log stay the contract.

| Verb | What it does |
|---|---|
| `github sync --backfill <run>` | Push a finished run's whole board: one issue per checkpoint (title `<id> — <title>`, `status:*` and `source:*` labels, a `confirmed` label only when the engine confirmed the claim, and the stage as a milestone), plus a run issue carrying plan/repo/branch/engine with one comment per finished session. Takes the same selector `history`, `budget` and `money` take — a run id, a prefix, a catalogue slug, a repo name, or a path to a `run.db`. The run is opened **read-only**. |
| `github backfill <run>` | The same thing, spelled the way you would say it — `backfill` is an accepted alias for `sync`, so `github backfill <run>` and `github sync --backfill <run>` are one verb. |
| `github sync --backfill <run> --repo owner/name` | Mirror into a repository other than the plan's. Point it at a scratch repo the first time; a backfill will *not* derive its destination from your working repo's `origin` unless the plan opted in with `github.enabled`. |
| `github sync --backfill <run> --dry-run` | Reconcile and report what would change, writing nothing. |
| `github sync --backfill <run> --no-diary` | Board only — skip the run issue and its per-session comments. |
| `github sync … --project <n>` | Mirror the **columns** to a Projects v2 board as well, without editing the plan (the same thing as `board: "issues+project"`). Needs a token carrying the classic `project` scope; without it the verb refuses by name and writes nothing — see below. |
| `github ci [--repo owner/name] [--branch <branch>]` | **New since `v0.5.0`; not in the released binary yet.** Ask what CI actually says about the commit this checkout is on, and record it. It reads every **active** workflow the repository has and then that workflow's latest run on the branch — *not* the commit's check-runs, which only list the workflows that commit triggered, so a schedule-only or dispatch-only workflow is invisible there and a branch reads CLEAN while a broken one sits red. A workflow that has never run on the branch is a row saying so. Writes the dated observation to `<stateDir>/ci-status.json`; the `REPORT.md` header, the owner queue and `doctor` derive `ci-battery` / `ci-verdict` from it and from `.github/workflows`, so a green cannot outlive the commit it was for. Exits **1** when CI is red on this commit or nothing on the server re-runs these gates. |
| `github sarif --backfill <run>` | Push every **open bug that names a file and a line** to GitHub **code scanning** as one SARIF 2.1.0 run. `--out <path>` also writes the document to disk, `--sha` / `--gitref` anchor the alerts to a commit that must exist in the destination, `--dry-run` renders and sends nothing. Free on a public repository; a private one needs Advanced Security — see below. |

Identity is a marker in the issue body (`<!-- conductor:task KS9.1 -->`), not the title, so re-running
a backfill mints **zero** duplicates and a reworded checkpoint updates its issue instead of growing a
second one. A patch carries only the fields that differ, labels outside the plan's `labelPrefix` are
left alone, and a checkpoint that leaves the plan is closed and labelled `retired` — never deleted.

The token comes from `$CONDUCTOR_GITHUB_TOKEN`, or from `githubToken` in
`<stateDir>/secrets.local.json` — the same file and the same precedence as the Telegram token. With
neither, the verb refuses **before dialling anything** and names both places it looked. A classic or
fine-grained token with `repo` scope is enough for issues; `project` is only needed for a Projects v2
board. `CONDUCTOR_GITHUB_API` repoints the API root (for a loopback fake, or an enterprise host) and
every surface that writes announces the override — a destination that writes issues is never
redirected silently. Plan-side configuration lives in the `github` block: see
[plan-config.md](plan-config.md).

### The Projects v2 board, and the one scope it needs

`--project <n>`, or `board: "issues+project"`, mirrors the **columns** on top of the issue board:
each checkpoint's issue is added to the board and its `Status` field set from the checkpoint's own
status. Projects v2 exists **only** in GitHub's GraphQL API — REST cannot move a board item — and a
mutation there needs the classic `project` scope, which `repo` does not imply. Conductor checks that
scope with a `GET /user` **before** anything is written, and refuses by name if it is missing:

```
a Projects v2 board needs the 'project' scope and this token does not carry it. nothing was written.
  scopes observed: delete_repo, gist, read:org, repo, user, workflow
  scope required: project — Projects v2 is GraphQL-only, and the REST api cannot move a board item.
  token source: CONDUCTOR_GITHUB_TOKEN
  the owner grants it once, interactively: gh auth refresh -s project
  conductor will not run that: it is interactive and it rewrites this machine's stored credential.
  until then set github.board to 'issues' — the issue board mirrors in full without it.
```

Granting the scope is yours to do: `gh auth refresh -s project` is interactive and it rewrites the
machine's stored credential, so conductor names it and never runs it. The issue board is unaffected
either way; inside a run the refusal is one log line and the issue mirror carries on.

**Which column a card lands in.** GitHub's default board template offers three options — Todo, In
Progress, Done — and conductor has five statuses, so each status names the options it would like,
best first, and the first one *your* board offers wins:

| Status | Column, best first |
|---|---|
| **todo** | Todo · To do · Backlog · New |
| **in_progress** | In Progress · Doing · Started |
| **blocked** | Blocked · On hold · Paused · *In Progress* |
| **done** | Done · Complete · Completed · Shipped |
| **skipped** | Skipped · Won't do · Cancelled · *Done* |

Every fallback is announced — *"no 'Blocked' option on this board, so 'blocked' cards are placed in
'In Progress'"* — and a status your board has no word for at all leaves the card **on** the board
with no status set, reported by name along with the options the board did offer. Nothing is guessed
silently. A second pass over an unchanged board issues **zero** mutations; a board whose item
listing is stale (GitHub's replica lag) costs redundant writes and can never mint a second item,
because `addProjectV2ItemById` returns the item that is already there.

### Bugs as code-scanning alerts, and the one thing that is not free

`github sarif --backfill <run>` renders the bug ledger as a single SARIF 2.1.0 run and uploads it to
the repository's **code scanning** tab: filterable, dismissable, shown on the PR diff, visible in the
GitHub mobile app, and — unlike an issue — not something the issue list has to carry.

Only bugs that **name a place in the tree** become alerts. The `bugs` table has no file column, so
the citation is lifted out of the prose a session wrote, and it is refused rather than guessed:

- a citation with **no line number** is not a location (SARIF would default the region to line 1,
  which hangs every bug that merely *mentions* a file at the top of it, and that reads as a fact);
- a **bare file name** — `VerdictEngine.cs:370`, which is how most sessions write it — resolves only
  when exactly **one** tracked file bears that name; two `agent.go`s and it is dropped;
- a path **no tracked file matches** is dropped, because an alert anchored to nothing is worse than
  no alert.

The verb reports both halves — `sarif: 6 located, 26 without a file and line` — so the reach is never
overstated. Bugs with no citation are still mirrored as issues by `github sync`.

Three properties of the document are load-bearing. It declares its own **analysis category**
(`conductor-bugs/`), so an upload cannot close alerts another tool raised. Every result is
**fingerprinted by the bug's row id**, so re-uploading from a later commit *updates* an alert instead
of raising a second one. And **only open bugs are rendered** — which is the closing mechanism, not an
omission: code scanning resolves an alert whose result stops appearing in a later analysis of the same
category, so `conductor bug fix <id>` closes the alert at the next upload with no second call.

A 202 from GitHub is a **receipt, not an ingestion**. GitHub validates the document afterwards, so
conductor polls `GET /code-scanning/sarifs/<id>` until it stops saying `pending` and reports a
rejected document by the reason GitHub gives. A pass that stopped at the 202 would call a rejected
SARIF a success.

**Public repositories get code scanning free. Private repositories need GitHub Advanced Security
(GitHub Code Security).** That is GitHub's rule, not conductor's, and conductor cannot work around
it — what it does is say so before the call and translate the 403 into the sentence that names the
cause. Measured on 2026-08-26 against a private scratch repository with a token carrying `repo`:

```
note shaahink/dv61-ledger-scratch is PRIVATE — code scanning is free on PUBLIC repositories; a
     PRIVATE repository needs GitHub Advanced Security (GitHub Code Security) and refuses the
     upload with 403 without it.
403 Forbidden from https://api.github.com/repos/…/code-scanning/sarifs [token scopes: delete_repo,
     gist, read:org, repo, user, workflow] — {"message":"Code scanning is not enabled for this
     repository. Please enable code scanning in the repository settings."}
```

Note what that refusal does **not** say: anything about the token. GitHub documents the
`security_events` scope for this endpoint, but the observed wall on a private repository is the
repository's entitlement, not the credential — so conductor **notes** a missing `security_events`
(with `gh auth refresh -s security_events`, which is yours to run) and makes the call anyway. An
organisation that *has* Advanced Security must not be refused on a scope requirement nobody here has
been able to observe.

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
