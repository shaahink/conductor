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
conductor doctor -p <plan>            # is the environment ready? (<2s, says what's missing)
conductor run    -p <plan>            # start the run (engine + control plane + TUI, one process tree)
conductor status -p <plan>            # where are we, from the database, in <1s
conductor log --query "stage=M5 and outcome=fail" -p <plan>   # why did something fail
conductor inject "focus on the failing test first" -p <plan>  # steer the NEXT session
conductor approve -p <plan>           # let it past an owner-gated stage
conductor pause  -p <plan>            # stop after the current session (safe)
```

`-p <plan>` resolves as: `--plan` flag → `CONDUCTOR_PLAN` env → `./conductor.plan.json` in the cwd. In
a repo scaffolded by `conductor init`, drop the flag entirely. In this repo the plans are under
`plans/`, so pass e.g. `-p plans\conductor-maestro.plan.json`.

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
| `doctor` | <2s health check: agent CLI, git, face-go binary, DNS/disk/API, budget, Telegram. Exit 1 if any `fail`. |
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

### Control a LIVE run (queue an intent the engine picks up at the next boundary)
These write `.conductor/control.json` (or POST `/control`) — they work from any terminal while a run is
going. Destructive ones need `--yes`.
| Command | Does |
|---|---|
| `inject "<instruction>"` | Prepend an instruction to the agent's NEXT session prompt. The steering wheel. |
| `approve` | Clear an owner-gated stage so the run advances (also `R` in the TUI). |
| `pause` | Stop after the current session. `resume` continues. |
| `pause-after-stage` | Park at Paused once the current stage completes. |
| `resume` | Resume a paused / needs-attention run. |
| `skip --yes` | Skip the current stage, flag it for human review. |
| `goto <stage>` | Jump to a different stage (clears the old stage's pending state). |
| `retry-stage` | Reset the attempt counter, re-queue a deliver session for the current stage. |
| `kill --yes` | Kill the current agent session; the loop re-evaluates. |
| `abort --yes` | Kill the session AND stop the conductor. |
| `rollback --yes [--force]` | Reset the working tree to the stage-start commit (`--force` if dirty). |
| `rollover <tokens>\|off\|clear` (queues the `set-rollover` control verb) | P5: session-token rollover for THIS RUN ONLY — a session past the cap ends `RolledOver` (handoff written, next session fresh, no attempt burned). `off` forces it off even if the plan sets a cap; `clear` hands back to `limits.maxSessionTokens`. Run-state only — never writes the plan file. The active override is on `GET /state` as `maxSessionTokensThisRun` (absent = none) and in the Face's Settings "rollover (run)" row. |

### Knowledge & plan authoring
| Command | Does |
|---|---|
| `note "<text>" [-k kind] [-s stage]` | Write to the knowledge ledger (`run.db`); injected into later prompts. |
| `bug new "<title>" [-d detail] [-s severity] [--stage S]` · `bug list [--all]` · `bug fix <id> [--wontfix]` | Tracked bugs that outlive the session that found them; open ones feed later prompts. |
| `plan set <key> <value> [--create]` · `plan reload` · `plan add-stage <json>` · `plan import <file> [--model M] [-y]` | Edit the plan from the CLI. `set` refuses a key the plan schema does not declare (suggesting the dotted path it thinks you meant — `--create` overrides), reports the comment lines its rewrite drops and keeps the original as `<plan>.bak`, and queues the reload itself when a live engine holds the plan. `import` parses a markdown mega-plan into stages and DIFFS against the current plan (never clobbers). `reload` validates the file and queues a live `reload-plan` — a running loop swaps the plan at its next session boundary. |

### Infra
`bg start\|status\|logs\|stop` (long-running commands, so they don't look like a stall) ·
`mcp-serve` (the MCP task server the engine wires into each session) · `completion <shell>` ·
`audit <stage> --replay` (post-hoc read-only audit of a completed stage).

---

## 3. Common operator workflows

**Start a supervised run.** `conductor doctor -p <plan>` → fix any `fail` → `conductor run --once -p
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
session's prompt. Use for "do X before Y", "the real bug is in file Z", "stop gold-plating".

**Stop safely.** `conductor pause` (finish the current session, then idle) is the graceful stop.
`abort --yes` is the hard stop. **Do not close the terminal window to stop a run** — see Gaps §5.

**It died / looks stuck.** Follow `docs/troubleshooting.md`'s procedure: is a `conductor.exe`
process alive? → log tail → `crash-*.log`? → `git status` (uncommitted work is real, not corruption)
→ `conductor run -p <plan>` to resume (it reads `run.db` alone and the next prompt tells the worker to
re-orient).

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
| `budget-park` | Cost or token cap hit; the run stopped rather than spend past it | `status` → `approve` re-opens the window |
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

## 7. Known gaps & missing features (as of 2026-08-01, end of the Sarban era)

Re-measured at the close of the Sarban era, not carried forward. Four of the ten items on the
2026-07-15 list are gone — closed by the two eras that ran since — and what is left is stated with
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
6. **`plan import` needs an existing valid plan to diff against** — there's no clean from-scratch
   bootstrap. Workaround: `conductor init` first, then `plan import`.
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
