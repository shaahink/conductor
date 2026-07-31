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
| `run.db` | Everything structured. Query via `conductor log`/`report --query` or MCP `run_query`. |
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

---

## 4. Programmatic control (when you'd rather use HTTP than the CLI)

Discover a live run's URL from `.conductor/control-plane.json` → `url` (e.g. `http://127.0.0.1:4317`).
All endpoints are localhost-only.

**Read:** `GET /state` (current stage/session/cost/live metrics) · `/timeline` · `/tasks` · `/ledger`
· `/bugs` · `/sessions` · `/scores` (SF1.1: the verifier's verdicts, each with the per-stage bar it
was judged against and whether it passed) · `/plan` · `/prompt/preview?stage=&kind=` ·
`/prompt/blocks?task=` (P3: a task's prompt as labeled building blocks) · `/console/current` and
`/transcript/current` (SSE streams of the live agent) · `GET /report/query?sql=` (read-only SQL).
Reads need no token.

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

## 7. Known gaps & missing features (as of 2026-07-15, Maestro closed 30/30)

**Owner-only (credential-gated `HUMAN:` — neither blocks engine use):**
1. **Live Telegram phone dogfood (M8.3).** Backend (`SecretsStore`, `/telegram/*`, `TestConnectionAsync`)
   and the Face's guided-setup tab are built and tested; the actual phone-driven run needs the owner's
   real bot token pasted into the Face. Truth gate ("toy run driven from the phone, lid closed") unmet.
2. **Full real-model run (M9.1).** The engine, gates, escalation, history, and cost accounting are all
   green under the token-free fake agent; the confirmation on the real DeepSeek/opencode model is a
   paid run only the owner can start.

**Incomplete design-doc items:**
3. **Persona kill-list residue.** The design doc's kill-list wants the 9-persona system gone; the heavy
   system was removed but a slim `PersonaRegistry` (~83 lines) + scattered `persona` references remain.
   Harmless (no failing test), but not the clean deletion the doc asked for.
4. **`conductor init` is intentionally minimal.** It scaffolds plan + templates + gates + repo
   detection, but NOT domain "packs" (the design's M8.2 mentioned packs), and leaves `advisor` /
   `statusAgent` / `telegram` unset for the user to fill in.

**Operational limitations (see `DOGFOOD-RUNBOOK.md`):**
5. ~~**Closing the terminal window kills the run ungracefully.**~~ **FIXED (W3.3, `c8f9b56`).**
   `ConsoleCtrlRails` wires `CTRL_CLOSE_EVENT`/logoff/shutdown into the same graceful stop as Ctrl+C
   and blocks inside the OS handler until the run has saved. Closing the window parks the run and
   leaves it resumable — proven from outside the process by `tools/w3/window-close.ps1`
   (`docs/dev/workgraph/W3-WINDOW-CLOSE.md`). Ctrl+C is still the tidiest exit; the ✕ is no longer a
   data-loss risk.
6. **The crash-log net reports a crash but doesn't recover in-flight work** beyond what git already has.

**Ergonomics / polish (minor):**
7. **`plan import` needs an existing valid plan to diff against** — there's no clean from-scratch
   bootstrap. Workaround: `conductor init` first, then `plan import`.
8. **A fully-done stage in a `perPhase` plan renders `gating` indefinitely** (`SnapshotBuilder`), which
   can read as "stuck" long after the phase gate passed. By-design, but easy to misread.
9. **`status` can show `sessions 0` against a re-seeded `run.db`** — the checkpoint count (seeded from
   the tracker on startup) and the recorded-session count can diverge.
10. **No CI / release automation** (`.github/workflows` absent) and the installer publishes
    framework-dependent (needs the .NET 10 runtime present, which it is here). Expected for a one-user
    tool; noted for completeness.

None of items 3–10 blocks day-to-day operation. The engine builds clean (0w/0e), the full C# suite is
green (704), the anti-cheat ratchet is green, face-go is green, and a real `conductor run` drives a
plan end-to-end with correct claims-vs-confirmations, gate caching, and human escalation.
