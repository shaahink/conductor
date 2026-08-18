# Operating Conductor — an agent's control guide

**Audience:** an AI agent (a Claude Code session or similar) driving Conductor **on the owner's
behalf** — starting runs, watching them, answering "where are we", responding when the run needs a
human, and recovering when it dies. If you are that agent: read this, then act. You control Conductor
entirely through the `conductor` CLI and its localhost control plane; nothing here needs the GUI.

Companion docs: `README.md` (what it is), `docs/troubleshooting.md` (diagnose a stuck/dead run),
`docs/history/maestro/M9-FINAL-AUDIT.md` (what conforms/deviates), `AGENTS.md` (session handover).

---

## 0. TL;DR — the commands you will actually type

```powershell
conductor preflight -p <plan>         # the whole launch drill: doctor + five more legs, one verdict
conductor doctor -p <plan>            # is the environment ready? (<2s, says what's missing)
conductor run    -p <plan>            # start the run (engine + control plane + TUI, one process tree)
conductor status -p <plan>            # where are we, from the database, in <1s
conductor log --query "stage=M5 and outcome=fail" -p <plan>   # why did something fail
conductor inject "focus on the failing test first" -p <plan>  # steer the NEXT session
conductor approve -p <plan>           # let it past an owner-gated stage
conductor pause  -p <plan>            # stop after the current session (safe)
```

`-p <plan>` resolves as: `--plan` flag → a single `*.plan.json` in the cwd (or `./plans/`) →
`CONDUCTOR_PLAN` env. In a repo scaffolded by `conductor init`, drop the flag entirely. In this repo
the plans are under `plans/` and there are several, so the cwd cannot decide and the variable still
wins — pass e.g. `-p plans\conductor-maestro.plan.json` to be sure.

Install the global command once with `powershell -File tools\install.ps1` (see README). Everything
below assumes `conductor` is on PATH.

---

## 1. Mental model (read once)

- **You run the engine, never the Go face.** `conductor run` is ONE process tree: the C# engine + an
  HTTP+SSE control plane (`http://127.0.0.1:<auto-port>`) + the Go TUI (`face-go`), spawned as a child.
  Kill the face, the run continues; `conductor face` reattaches. You never launch the Go binary.
- **The coding agent is a separate process** the engine spawns per session (opencode/DeepSeek by the
  plan's `agent` block) — not you. You are the *operator*; that process is the *worker*.
- **`run.db` is the only truth.** `.conductor/run.db` (SQLite) holds sessions, gates, ledger, bugs,
  checkpoints, events. `status`/`log` read it. `state.json` is gone. The tracker markdown is a
  generated *view* — hand-editing it does nothing (the engine confirms checkpoints from the DB).
- **The engine is authoritative and disposable-UI.** If your control command and the TUI disagree,
  the engine wins.

Where to look when something's off (full table in `DOGFOOD-RUNBOOK.md`):

| File (under `.conductor/`) | Tells you |
|---|---|
| `run.db` | Everything structured. Query via `conductor log` or the MCP `run_query` tool (behind `conductor chat`). SF1.2 deleted `report --query` with the rest of the SQL console; `conductor report` writes a report. |
| `logs/conductor-YYYYMMDD.log` | The engine's structured log. Tail it first. |
| `logs/crash-*.log` | A forensic dump if it crashed. Newest near the time it went quiet = root cause. |
| `logs/session-NNN.jsonl` / `.prompt.md` | Raw agent I/O + the exact compiled prompt. |
| `sessions/NNN/` | Per-session `prompt.md` · `transcript.md` · `verdict.md` · `handover.md` · `cost.json` + `INDEX.md`. |
| `control-plane.json` | `{ port, url, pid, planName, startedUtc, token }` — how to reach a LIVE run over HTTP; `token` is the write token every POST must send as `X-Conductor-Token`. |
| `REPORT.md` | Human-readable progress snapshot, regenerated each session. |

---

## 2. Full command reference

`-p <plan>` applies to every command that reads a plan. Add `--help` to any command for its options.

### Lifecycle & setup
| Command | Does |
|---|---|
| `init [-o <dir>] [--name N] [--repo P]` | Scaffold a runnable plan + editable `templates/` + `TRACKER.md`, gates chosen from the detected repo type (dotnet/go/rust/node/python). Self-checks it loads. |
| `new-plan [-o <dir>]` | Minimal scaffold (plan + tracker only), no templates/gates. `init` supersedes it. |
| `demo [--from <file>] [--keep] [-o <dir>]` | Drive a complete plan end to end against the built-in fake agent, in a throwaway directory. No credentials, no spend, no PowerShell. `--from` drives *your* board instead — a spec-kit `tasks.md`, a Task-Master `tasks.json`, a plain markdown checklist or a conductor plan/tracker, converted with no model call. `--keep` leaves the directory to poke at. See §6. |
| `doctor` | <2s health check: agent CLI, git, face-go binary, DNS/disk/API, budget, Telegram. Exit 1 if any `fail`. |
| `journey` | The pre-flight itinerary: identity, stages, gates, human moments — what the run will do, stage by stage, before it does any of it. Writes no state and spawns no agent. `preflight` runs this as one of its legs; reach for `journey` alone when the itinerary is the only thing you want to read. |
| `preflight [--no-auth-check] [--no-update-check]` | The launch drill as one verb: doctor (0 fail), journey resolution (workflow + model per stage), the next session's prompt composed and measured, running engine versus the latest release, a stale-engine check, and the tracker handoff block. One verdict, one exit code, and the verdict line names the legs that failed. Decides from the loop's own inputs as the loop prepares them — the declared plan projected over the work graph in an existing `run.db` (read-only, modelling the startup sync) and the saved state after both halves of crash recovery (state.json's and the event log's) — so what it names is what `conductor run` does. No agent spawns and preflight creates nothing under the plan's `.conductor/`, WAL sidecars included — resolving the plan's `run.db` location registers it in the machine catalogue, as plain `doctor` does. |
| `run [--dry-run] [--once] [--max-sessions N] [--headless] [--no-face] [--no-control-plane] [--port P] [--paused]` | Drive the plan. `--dry-run` = print the first prompt, spawn nothing. `--once` = one session. `--headless` = plain line output, no TUI (use this when driving from a non-interactive shell). `--no-face` = control plane up, no TUI. `--paused` = come up idle (author the plan / pre-seed the kanban first); `resume` starts session 1. |
| `face [--demo]` | Attach a TUI to an already-running engine (`--demo` = offline synthetic data). |

### Monitoring (read-only, safe any time)
| Command | Does |
|---|---|
| `status [--since D] [--deep]` | One-verdict "where are we" from `run.db`, per-stage table. Default is instant; `--deep` adds an LLM narrative. |
| `log --query "<k=v and k=v>" [--since D] [--tail N]` | Query the structured log. Keys: `stage`, `gate`, `outcome`, etc. |
| `report [--query <SQL>]` | Regenerate `REPORT.md`; `--query` runs read-only SQL over `run.db`. |
| `tasks` | Task graph: sub-tasks per checkpoint from the event log. |
| `task --list` | Checkpoint status from `run.db`. |
| `gate [--full]` | Re-run the gate battery at HEAD (no agent). `--full` = whole battery, else fast tier. Clears `pendingFix` if green. |
| `chat "<question>"` | Ask a model about the run; it has MCP access to `run.db`, the ledger, control verbs. |
| `watch [--json] [--timeout M] [--hook "<cmd>"] [--notify <url>]` | Block silently until the run needs judgment, then emit a ~30-line brief (exit 0) — or exit 10 on the `--timeout` heartbeat. Runs the plan's `supervisor` command with the brief on stdin, and POSTs it to the `supervisor.remote` webhook / phone. See §3 "Unattended supervision". |
| `watches [--json] [--ports <a-b>]` | The other half of `watch`: what is **armed** on this machine right now — every live run beside the supervisor block watching it, how much of its hourly fuse is burnt, where a remote wake travels, and the park-push cap in force (`limits.maxPushesPerIncident`). A run nothing would wake anybody for is called out as such. Read-only: a loopback `GET /state` and two file reads, no token and no POST. |

### Control a LIVE run (queue an intent the engine picks up at the next boundary)
These write `.conductor/control.json` (or POST `/control`) — they work from any terminal while a run is
going. Destructive ones need `--yes`.
| Command | Does |
|---|---|
| `inject "<instruction>"` | Prepend an instruction to the agent's NEXT session prompt. The steering wheel. |
| `approve [--amount <usd>] [--tokens <n>]` | Clear whatever the run is parked on (also `R` in the TUI). On an owner gate it advances the stage. On a **budget park it raises the run's spend ceiling** by the amount you name — or, with neither flag, by one more of the plan's own `limits.maxRunCostUsd` / `maxRunTokens`. The log line and the toast state the ceiling before and after and the spend it forgives nothing of. The run resumes only when **both** halves of the ceiling clear the spend: a raise that would leave either half still reached — too small, or aimed at the wrong half — is refused whole, naming the number to type. An amount on a non-budget park is refused. |
| `pause` | Stop after the current session. `resume` continues. |
| `pause-after-stage` | Park at Paused once the current stage completes. |
| `resume` | Resume a paused / needs-attention run. |
| `skip --yes` | Skip the current stage, flag it for human review. |
| `goto <stage>` | Jump to a different stage (clears the old stage's pending state). |
| `retry-stage` | Reset the attempt counter, re-queue a deliver session for the current stage. |
| `kill --yes` | Kill the current agent session; the loop re-evaluates. |
| `abort --yes` | Kill the session AND stop the conductor. |
| `rollback --yes [--force]` | Reset the working tree to the stage-start commit (`--force` if dirty). |
| `plan reload` | Queue the `reload-plan` control action: a running loop validates the file and swaps the plan at its next session boundary. Listed here as well as under authoring because it is a *live* control intent — `plan set` queues it for you when an engine holds the plan, and raising `limits.maxRunCostUsd` then reloading is the other way past a budget park. |
| `heartbeat` | Force a fresh `REPORT.md` right now. Only meaningful mid-session — it snapshots the live agent, so it is the one control verb that takes effect *during* a session rather than at the next boundary. |
| `rollover <tokens>\|off\|clear` (queues the `set-rollover` control verb) | P5: session-token rollover for THIS RUN ONLY — a session past the cap ends `RolledOver` (handoff written, next session fresh, no attempt burned). `off` forces it off even if the plan sets a cap; `clear` hands back to `limits.maxSessionTokens`. Run-state only — never writes the plan file. The active override is on `GET /state` as `maxSessionTokensThisRun` (absent = none) and in the Face's Settings "rollover (run)" row. |

### Knowledge & plan authoring
| Command | Does |
|---|---|
| `note "<text>" [-k kind] [-s stage]` | Write to the knowledge ledger (`run.db`); injected into later prompts. |
| `bug new "<title>" [-d detail] [-s severity] [--stage S]` · `bug list [--all]` · `bug fix <id> [--wontfix]` | Tracked bugs that outlive the session that found them; open ones feed later prompts. |
| `plan new [--from-idea "<prose\|path>"] [--advisor M] [-o <dir>]` | Author from nothing: one command from an empty repo to a plan, a tracker and the editable templates, **doctor-clean by construction** — the agent block names a CLI this machine actually has, and no scaffolded template spells the escalation token. `--from-idea` takes free prose, a PRD path or an existing tracker; a structured document is parsed with no model call, prose needs the advisor you name. The JSON never has to be opened. |
| `plan set <key> <value> [--create]` · `plan reload` · `plan add-stage <json>` · `plan import <file> [--model M] [-y]` | Edit the plan from the CLI. `set` refuses a key the plan schema does not declare (suggesting the dotted path it thinks you meant — `--create` overrides), and queues the reload itself when a live engine holds the plan. Every edit is spliced into the raw file (KS3.2): `//` comments, key order and formatting survive, and nothing changes but the edited values and `planVersion`. `import` parses a markdown mega-plan into stages and DIFFS against the current plan (never clobbers). `reload` validates the file and queues a live `reload-plan` — a running loop swaps the plan at its next session boundary. |

### Across runs, and this whole machine (read-only unless it says otherwise)

State outlives the repo it was produced in: every run this machine has driven is in a machine-level
catalogue keyed by repo and plan, and these verbs read *that* rather than the run you are standing in.
They answer after a run has ended, and from any directory.

| Command | Purpose |
|---|---|
| `ps [--json]` | Every conductor run on this machine — repo, plan, run id, stage, status, port, pid, uptime. The run in the current directory is marked `*`. |
| `history [<selector>] [--repo R] [--plan P] [--since D] [--limit N] [--json]` | Browse past runs from the catalogue. No argument lists them; a run id, prefix, slug, repo name or a path to a `run.db` opens one and replays its spine. Liveness is **reconciled at render time** — a run whose engine was killed never lists as `running`, in the table or in the JSON. |
| `face --archive <selector> [--serve] [--port N]` | Open a **finished** run in the TUI: the engine serves that run's `run.db` through a read-only control plane, so every write affordance hides itself and every POST is refused. A port inside the 4317-4336 fleet window is refused — an archive must never show up in `ps` as a live run. |
| `catalogue` · `catalogue repair [--apply]` | Every run store this machine has, and whether any of them hold the same run twice. `repair` says what it would collapse and writes nothing; `--apply` collapses it after backing up every store it touches, never writes a store a live engine is using, and identifies a run by its run id rather than by which store it sits in. |
| `run close <id> [--status S] [--ended T] [--reason "<why>"] [--dry-run]` | Close the record of a run whose engine never got to close it — killed, rebooted, or reaped with its shell. Writes a terminal status (`closed`, the default, or `completed`/`aborted`) and stamps when the run *actually* stopped, taken from its last recorded activity unless you pass `--ended`. The reason is journalled into the run's event spine. **This is the supported way to clear a stale `running` row; hand-editing SQL is not.** |
| `run adopt <id> --reason "<why>"` | Annotate a run record without touching its lifecycle — for a record you mean to keep rather than close. |
| `budget [--repo R] [--plan P] [--since D] [--json]` | Measure a repo's token budget **from its own runs** and prescribe the next one: session floor, wrap-up spend, cap, nudge-versus-floor, nudge-versus-median-closer, rollover rate, and a `limits` block to paste. |
| `money [--run R] [--project P] [--plan P] [--since D] [--json]` | Price a run or a project from its own ledger: sessions, tokens, cache-read share, cost, and tokens and dollars per checkpoint, with the windows either side of a cap change. |
| `spend [--since D] [--runs] [--home H] [--json]` | What this **whole machine** spent — today, this week, this month — across every catalogued store, with no repo and no plan argument. Billed rows only, each real run counted once even when the catalogue holds it twice, and rows whose session has no start time reported as `undated` rather than silently dropped. |
| `github sync --backfill <selector> [--repo owner/name] [--dry-run] [--no-diary] [--project N]` | Push a finished run's board to GitHub issues: one issue per checkpoint (`status:*` / `source:*` labels, `confirmed` only when the engine confirmed the claim, the stage as a milestone) plus a run issue with a comment per finished session. **One way out, off by default** — nothing is ever read back from GitHub into the run. Identity is a marker in the issue body, so re-running mints zero duplicates. The token comes from `$CONDUCTOR_GITHUB_TOKEN` or `githubToken` in `<stateDir>/secrets.local.json`; with neither it refuses before dialling anything. `--project` refuses by design — see [cli.md](cli.md). |

### Infra
`bg start\|status\|logs\|stop` (long-running commands, so they don't look like a stall) ·
`mcp-serve` (the MCP task server the engine wires into each session) · `completion <shell>` ·
`audit <stage> --replay` (post-hoc read-only audit of a completed stage) ·
`version [--json] [--short]` (semver, git sha, build date — stamped at build — and *which file
answered*; takes no plan and works in any directory) ·
`update [--check] [-y]` (check the latest release and swap this binary for it; verifies the
download's checksum, runs it and asks its version before replacing anything, and **refuses while a
run is live**).

---

## 3. Common operator workflows

**Start a supervised run.** `conductor preflight -p <plan>` (the whole drill; `conductor doctor -p
<plan>` is the health leg alone) → fix every leg the verdict names → `conductor run --once -p
<plan>` (watch one session) → if healthy, `conductor run -p <plan>` for the whole plan. From a
non-interactive shell add `--headless`.

**"How's it going?"** `conductor status -p <plan>` (instant). For detail on a specific failure,
`conductor log --query "stage=<S> and outcome=fail" -p <plan>`, then read `sessions/<NNN>/verdict.md`.

**It stopped and says NEEDS HUMAN.** The engine escalates when it can't make progress (repeated
identical failures, an owner-gated stage, budget/backoff exhaustion, or an explicit `HUMAN:` line in
the tracker handoff). Steps: read the last `verdict.md` + the log tail to learn WHY → decide → then
either `inject "<guidance>"` and `resume`, or `approve` (if it's an owner gate), or `skip --yes` /
`goto <stage>` to move on. Never edit the tracker to fake a checkpoint DONE — the engine ignores it.

**Steer without stopping.** `conductor inject "<instruction>"` — lands at the top of the next
session's prompt. Use for "do X before Y", "the real bug is in file Z", "stop gold-plating". The
whole argument is stored, newlines included, and the success line states how much of it arrived —
`queued 001-… (2,919 chars)`. **Read that number.** On Windows the `conductor` on PATH is a `.cmd`
shim and cmd.exe ends a command line at the first newline, so a multi-line instruction can be cut
before the engine ever sees it; a count far below what you typed is that cut, and the fix is to pass
one physical line or call `conductor.exe` by path.

**Stop safely.** `conductor pause` (finish the current session, then idle) is the graceful stop.
`abort --yes` is the hard stop. **Do not close the terminal window to stop a run** — see Gaps §5.

**It died / looks stuck.** Follow `docs/troubleshooting.md`'s procedure: is a `conductor.exe`
process alive? → log tail → `crash-*.log`? → `git status` (uncommitted work is real, not corruption)
→ `conductor run -p <plan>` to resume (it reads `run.db` alone and the next prompt tells the worker to
re-orient).

### Letting someone else watch — a group chat with an observer profile (KS11.2)

A stakeholder who wants to follow a run does not need the steering wheel, and until KS11.2 there was
no way to give them one without the other: `allowedChatIds` gated pushes and commands together, so
any chat the bot served could `/inject` and, with `enableTwoWay`, `/abort`.

```jsonc
"telegram": {
  "chats": [
    { "chatId": "99205495",   "profile": "admin"    },
    { "chatId": "-100123456", "profile": "observer" }
  ],
  "enableTwoWay": true
}
```

Both chats get every push. The observer chat may ask `/status`, `/tasks`, `/daily` and `/start`; a
control verb, `/inject`, `/chat` or a tap on a confirmation button gets one named line saying so and
nothing happens. An unknown command is met with silence, as it always has been.

Each chat is introduced before the run's first word: what this run is (plan, stage map, budget
ceiling), what will arrive here and when, and exactly what this chat may ask. A chat added by a
plan reload mid-run gets the same introduction at the reload, and `/start` re-sends it on request.
The "what you can ask" list is built from the same catalogue that enforces the permission, so it
cannot promise a verb the bot would refuse.

**Three things about group chats specifically:**

1. **Privacy mode.** A bot added to a group sees only messages addressed to it — commands like
   `/status@yourbot`, and replies to its own messages — unless you turn privacy mode off in
   @BotFather (*Bot Settings → Group Privacy*). Leave it ON: the run's own commands still work, and
   the bot is not reading the group's conversation. Note that with privacy on, plain `/status` in a
   group may not reach the bot at all; `/status@yourbot` always does.
2. **The chat id is negative,** and a supergroup's has a `-100` prefix. Get it from @userinfobot
   inside the group, and quote it as a string in the plan.
3. **Evidence is served as-is.** Granting `observer` to a group is a decision about what that group
   may see; there is no redaction layer between the artifact and the chat.

An unknown profile string fails plan load by name — `conductor doctor` will not paper over it, and
the run refuses to start rather than guessing which side of the line a chat belongs on.

### Unattended supervision — the night watch (SF5)

**The problem with babysitting by polling.** An agent tailing the log every 30s spends its budget on
*accumulation*, not on the polls: over ten hours ~95% of its ticks say "still running", and each of
those ticks is paid for again inside every later tick's context. `conductor watch` inverts it — the
waiting is a file-stat loop that costs nothing, and the expensive reader is invoked once, at the
moment that actually needed judgment.

```powershell
conductor watch --json --timeout 60      # block; print a brief on wake (exit 0) or heartbeat (exit 10)
while ($true) { conductor watch --json } # the night watch: the plan's supervisor block runs on each wake
```

**Wake set — `watch` returns (and runs the supervisor) on exactly these:**

| Wake | Means | First moves |
|---|---|---|
| `needs-human` | Agent escalated a `HUMAN:` item, or `pauseOnBlocked` parked the run | `status` → `inject` / `resume` / `skip` |
| `owner-gate` · `approval-park` | A stage wants owner approval before it advances | `status` → `approve` |
| `budget-park` | Cost or token cap hit; the run stopped rather than spend past it | `status` → `approve --amount <usd>` raises the ceiling (or raise `limits.maxRunCostUsd` and `plan reload`, which un-parks it too) |
| `circuit-breaker` | Repeated failures on one stage tripped the breaker | `status` → `inject "<what to try instead>"` → `pause` |
| `phase-red-twice` | A phase gate went RED twice on the same stage — the agent is not converging | `gate --full` → `inject` → `pause` |
| `engine-gone` | The conductor process vanished (crash, closed terminal, reboot) | `status` → `run` (resumes from `run.db`) |
| `run-ended` | The plan finished, or the run stopped for good | `status` → `report` |

**Don't-wake set — `watch` stays silent through these, and that is the point:**

| Quiet event | Why it must not wake anyone |
|---|---|
| Usage-limit backoff | Self-resumes. On a real run these were 2 of the last 3 events — waking on them *is* the polling babysitter |
| Stall backoff / session retry | The watchdog already handles it; a second supervisor is noise |
| Session start / exit / rollover / `blocked-until` | Churn, not a decision |
| Gate PASS, checkpoint confirmed, stage advance | Good news needs no night call |
| A single phase RED | One red is work in progress; the *second* on the same stage is the signal |

The `--timeout <min>` heartbeat is the long fallback for "did the watch itself die" — it returns exit
**10** with `reason=timeout` and, deliberately, does **not** run the supervisor. A heartbeat that
invoked the model would put back exactly the per-tick cost this verb exists to remove.

**The supervisor block.** Keep the babysitter in the plan, not in a shell history — it then survives
the terminal it was started from and gets reviewed in a diff like everything else:

```json
"supervisor": {
  "command": "claude -p \"You are the night watch for this run. The wake brief is on stdin; your standing orders are in it. Act, then say in one line what you did.\"",
  "timeoutMinutes": 10,
  "maxPerHour": 6,
  "standingOrders": "You MAY: approve an owner gate whose checkpoint has an evidence path; inject a hint on a circuit breaker; resume after a self-resolved park. You MUST escalate (notify and stop): anything that spends money, any merge or push to master, any plan edit, any second circuit breaker on the same stage."
}
```

- It costs nothing while quiet — the command runs only when the wake set fires.
- `--hook '<command>'` overrides the block for a one-off, and is not bound by the hourly fuse.
- `maxPerHour` (default 6, `0` = unlimited) is a **cost fuse**, counted in
  `.conductor/supervisor-fires.log` so it survives across the fresh process every wake starts. A
  supervisor that hits the cap is usually a run stuck on one cause, not a busy night — read the brief
  yourself before raising it. Whenever the supervisor does not run, `watch` says so on stderr.

**The standing-order pattern.** Write the orders in the plan, not in the prompt that starts the loop:
`standingOrders` is copied into the brief, so the agent reads its authority on the same stdin as the
wake. An agent that cannot see its limits has none. Two rules that keep this honest: name the
*escalation* half explicitly (silence about a limit reads as permission), and keep everything that
spends money, merges, or edits the plan on the human's side of the line.

**When the supervisor is not on this machine (SF5.3).** The two supervisors an owner actually has at
3am are a phone and a cloud session, and neither can be a local `command`. A `remote` block inside the
supervisor block sends the wake off-box, on the same wake set and nothing else:

```json
"supervisor": {
  "command": "claude -p \"night watch; brief on stdin\"",
  "remote": {
    "webhookUrl": "https://api.github.com/repos/me/site/dispatches",
    "headers": { "Authorization": "Bearer ${GH_WAKE_TOKEN}", "Accept": "application/vnd.github+json" },
    "telegram": true,
    "maxPerHour": 12
  }
}
```

- **The webhook body IS the brief** — byte for byte the document the local supervisor reads on stdin,
  `standingOrders` included. A ping that only says "something happened" makes the remote reader go and
  look, which is the polling cost this verb exists to delete, paid over a network instead of a pipe.
- **Header values expand `${NAME}` and `%NAME%` from the environment**, so the plan names a credential
  and never contains one. A variable that is not set **drops that header and says which one** on
  stderr — posting a literal `${TOKEN}` earns a 401 whose cause is invisible from the far end.
- **`telegram: true`** sends a phone-sized wake line (reason, stage, spend vs cap, first verbs) to
  `telegram.allowedChatIds`. It is sent by the **watch** process, not the engine, so it still arrives
  when the engine is the thing that died — the one failure the run's own push path can never report.
- **The remote goes out first, and goes anyway.** It is dispatched before the local `command` and does
  not care whether that command is absent, disabled, or rate limited: the hour a local babysitter has
  burnt its fuse is exactly the hour a human off the box needs to hear. Its own fuse
  (`maxPerHour`, default 12, in `.conductor/supervisor-remote-fires.log`) is separate from the
  supervisor's for the same reason — one shared ledger would have each quietly spending the other.
- **`--notify <URL>`** replaces the whole block, phone included, for a deliberate one-off, and is not
  bound by the plan's fuse — the same bargain `--hook` makes.
- **A dead endpoint is reported, not thrown.** Failure prints a stderr line and the watch still exits
  on its wake code; a watch that crashed because a webhook was down would turn one parked run into two
  outages. Delivery lines name the **host**, never the URL: a webhook path routinely carries its own
  secret and a Telegram one always carries the bot token.

**The cloud pattern**, end to end: `conductor watch` POSTs the brief to a small always-on relay (a
`repository_dispatch` on GitHub, a Worker, any endpoint you control); the relay holds the credential
and starts a cloud Claude Code session with repo access, handing it the brief as its prompt. The
session reads what fired, what it cost, what the board looks like and what its standing orders permit,
without a single polling tick having been paid for.

**What stays manual — say it out loud, because the gap is where the 3am surprise lives:**

1. **Conductor does not host the relay.** `watch` delivers to something on the internet; it does not
   become that something. No relay, no cloud session — the webhook just 404s into the evidence log.
2. **The control plane is localhost-only.** A cloud session can read the repo and push commits, but it
   cannot `conductor approve` your run from a datacentre. Until you give it a path back to the box (a
   tailnet, an SSH hop, an agent already running there), remote supervision is **read-and-advise**:
   the acting is still the local supervisor's or yours.
3. **Nothing retries.** One POST inside one timeout window. If the far end was down, that fact is on
   stderr and in the fires ledger — it is not queued for later.
4. **The brief is not signed.** Anything that can reach your webhook URL can post something shaped
   like a wake; authenticating what arrives is the receiving end's job, not this one's.
5. **The brief leaves the machine.** It carries the repo path, plan name, stage, spend and your
   standing orders. Point it only at a channel you would paste `conductor status` into.

---

## 4. Programmatic control (when you'd rather use HTTP than the CLI)

Discover a live run's URL from `.conductor/control-plane.json` → `url` (e.g. `http://127.0.0.1:4317`).
All endpoints are localhost-only.

**Read:** `GET /state` (current stage/session/cost/live metrics) · `/timeline` · `/tasks` · `/ledger`
· `/bugs` · `/sessions` · `/scores` (SF1.1: the verifier's verdicts, each with the per-stage bar it
was judged against and whether it passed) · `/plan` · `/prompt/preview?stage=&kind=` ·
`/prompt/blocks?task=` (P3: a task's prompt as labeled building blocks) · `/console/current` and
`/transcript/current` (SSE streams of the live agent). Reads need no token.

There is no ad-hoc SQL endpoint: SF1.2 deleted `GET /report/query?sql=` along with the Face's Dev
console, so every read above is a typed DTO. SQL against `run.db` lives in the MCP `run_query` tool,
which is what `conductor chat` asks questions through.

**Write:** `POST /control` (same verbs as §2's control commands) · `POST /inject` · `POST /tasks/update`
· `POST /tasks/add` · `POST /tasks/edit` (P3: title/extra-context as structured task data; PF3 adds
`paths` — a card's declared repo-relative claims, which gate multi-item session claims) ·
`POST /tasks/refine` (P3: the advisor PROPOSES a title/context — nothing mutates until you confirm by
posting `/tasks/edit`) · `POST /plan/edit` · `POST /plan/import` · `POST /telegram/test|token`.

**Every write must send the per-run token** as `X-Conductor-Token`, read from the `token` field of
`.conductor/control-plane.json`. Without it a POST is `401`. This is a CSRF guard: a browser can POST
to `127.0.0.1` but can't read the token file, and `/inject` feeds text straight into the next agent's
prompt (a prompt-injection vector) while `/plan/edit` and a prompt-driven `/plan/import` can plant a
gate shell command. Example:

```bash
TOKEN=$(jq -r .token .conductor/control-plane.json)
curl -s -X POST "$url/tasks/add" -H "X-Conductor-Token: $TOKEN" \
  -H 'Content-Type: application/json' -d '{"checkpointId":"G2.1","title":"Wire the endpoints"}'
```

A **freeform `/plan/import`** (prose the advisor model interprets) must be **previewed before it can
apply**: POST with `"apply":false` to get the diff, review it, then POST the same source with
`"apply":true`. A blind `apply:true` with no prior preview is refused — the reviewable diff is the
defence against a model-shaped gate command landing unseen.

**Plan edits are live (G3.2).** A saved `/plan/edit` or applied `/plan/import` auto-queues a
`reload-plan` control verb: the running loop re-reads the plan file at its **next session boundary**
(never mid-session) and swaps the live plan — stages, gates, and limits changes take effect in the
current run, no restart. The same verb is available directly (`POST /control
{"command":"reload-plan"}`, the Face palette, or `conductor plan reload`), and the reload shows up in
the timeline as `plan reloaded — vN`. An invalid or missing plan file makes the reload a loud no-op —
the old plan stays.

**From inside a session, the worker agent uses MCP tools** (not you): `conductor_note`, `ledger_list`,
`task_add|list|update`, `bug_new|list|fix`, `bg_start|status|logs|stop`, `run_query`. You mostly won't
call these directly; they're how the spawned worker records knowledge that survives it.

---

## 5. Safety rules (these are enforced by machines, not trust)

1. **Never weaken the measurement to get green.** Deleting a test, suppressing an analyzer, raising an
   architecture ceiling, softening a gate command, or editing a gate script are all mechanically
   detected by `tools/gates/ratchet.ps1` and fail the session. If a bar is genuinely wrong, put a
   `HUMAN:` line in the tracker handoff and stop — do not route around it.
2. **Evidence or it didn't happen.** A passing self-written test is weak; a truth gate the engine ran
   is strong. When you claim something works, name the command whose output proves it.
3. **Hand-editing the tracker does nothing.** Checkpoints are confirmed from `run.db` after gates pass
   and the verifier scores ≥ threshold. Use `conductor task --done <id> --evidence <path>` to CLAIM;
   the engine CONFIRMS.
4. **PowerShell helper scripts under `tools/` must stay ASCII** (Windows PowerShell 5.1 reads BOM-less
   UTF-8 as ANSI and a non-ASCII char tears the parse — this silently broke `fake-agent.ps1` before).

---

## 6. Credential-free dogfood (drive a full run with zero model spend)

To exercise the whole engine path without the owner's paid model, point a toy plan's `agent` at
`tools/fake-agent.ps1` and drive it headless. Recipe: scratch git repo + a `*-START.md` tracker with
`| T0.1 | ... | TODO |  |  |` rows + a minimal plan whose `agent.command` is
`powershell -NoProfile -ExecutionPolicy Bypass -File <abs>/tools/fake-agent.ps1 -Repo <repo> -Mode
success -Prompt {prompt}`, then `conductor run -p toy.plan.json --headless --max-sessions N`. The fake
agent hand-edits the tracker (never calls `task --done`), so it's exactly the "rigged agent" case: the
engine discards the edit and advances zero checkpoints — good proof of claims-vs-confirmations, gate
caching, and NEEDS-HUMAN escalation. Full detail: `docs/history/maestro/M9-FINAL-AUDIT.md` §M9.1.

---

## 7. Known gaps & missing features (as of 2026-08-15, end of the Karvansara era)

Re-measured at the close of the Sarban era and re-checked at the close of Karvansara, not carried
forward. Four of the ten items on the 2026-07-15 list were gone by Sarban's close; item 6 went with
Karvansara's `plan new`, and item 3 was re-read against the tree rather than assumed —
`src/Conductor.Core/PersonaRegistry.cs` is still there, so the row stays. What is left is stated with
what still owns it. The open engineering rows also live in
[`docs/dev/NEXT-FEATURES.md`](dev/NEXT-FEATURES.md), which is where new ones should be filed.

**Owner-only (credential-gated `HUMAN:` — neither blocks engine use):**
1. **Live Telegram phone dogfood.** The backend is done and then some: SC1 made the readiness verdict
   the same sentence everywhere, SF0.1 made a run *volunteer* it at startup, and SF4.2 gave the pushes
   an identity (repo, plan, run) and a `reloadPending` answer. The phone-driven run still needs the
   owner's real bot token — on this repo's own run the startup line reads *"telegram will NOT
   deliver — configured but no bot token"*, which is the gap saying its own name.
2. ~~**Full real-model run.**~~ **CLOSED by the Sarban era itself** — two self-hosted plans, 8 + 8
   stages, driven end to end by a real model against this repo, with claims, gates, escalation and
   cost accounting all exercised on live sessions rather than a fake agent.

**Incomplete design-doc items:**
3. **Persona kill-list residue.** The design doc's kill-list wants the 9-persona system gone; the heavy
   system was removed but a slim `PersonaRegistry` + scattered `persona` references remain (`RunContext`,
   `RunLoop.Snapshot`, the control-plane DTO). Harmless — no failing test, and SF6.2 pruned the stale
   persona *content* out of the prompt bank — but not the clean deletion the doc asked for.
4. ~~**`conductor init` is intentionally minimal.**~~ **CLOSED by SF6.3.** It now scaffolds the whole
   template set rather than two of them, and writes commented `advisor` / `telegram` blocks into the
   plan so the two settings most runs want are one uncomment away instead of one doc-read away.

**Operational limitations:**
5. **The crash-log net reports a crash but doesn't recover in-flight work** beyond what git already
   has. Closing the terminal window is *not* in this category any more — `ConsoleCtrlRails` wires
   `CTRL_CLOSE_EVENT`/logoff/shutdown into the same graceful stop as Ctrl+C and blocks inside the OS
   handler until the run has saved, proven from outside the process by `tools/w3/window-close.ps1`
   (W3.3, `c8f9b56`; `docs/dev/workgraph/W3-WINDOW-CLOSE.md`).

**Ergonomics / polish (minor):**
6. ~~**`plan import` needs an existing valid plan to diff against**~~ — **CLOSED by KS3.1.**
   `conductor plan new` is the from-scratch bootstrap: one command from an empty repo to a plan, a
   tracker and the templates, doctor-clean by construction, with `--from-idea` taking prose, a PRD or
   an existing tracker. `plan import` still diffs against a plan, which is now the point rather than
   the gap.
7. **A fully-done stage in a `perPhase` plan renders `gating` indefinitely** (`SnapshotBuilder`), which
   can read as "stuck" long after the phase gate passed. By-design, but easy to misread.
8. **`status` can show `sessions 0` against a re-seeded `run.db`** — the checkpoint count (seeded from
   the tracker on startup) and the recorded-session count can diverge.
9. ~~**No CI / release automation.**~~ **CLOSED by SC8.** `.github/workflows/ci.yml` builds and tests
   the tree (including a `ubuntu-latest` leg) on every push, and `release.yml` publishes a tagged
   build whose notes are the matching `CHANGELOG.md` section — it refuses a tag that has none. The
   installer still publishes framework-dependent (needs the .NET 10 runtime present).

None of items 3–8 blocks day-to-day operation, and the era closed with the engine building clean
(0w/0e), the C# suite green, the anti-cheat ratchet green, face-go green, and a real `conductor run`
driving two full plans end to end with correct claims-vs-confirmations, gate caching and human
escalation.
