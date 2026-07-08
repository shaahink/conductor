# Conductor

A minimal, resilient orchestrator that works through a **mega plan** (Loom, Meridian, …)
autonomously, one agent session at a time, while you watch from the laptop or from your phone.

It mechanizes the session cycle you already run by hand:

```
pick next stage from the tracker
  → spawn a fresh headless agent session (claude -p)  [deliver | fix | resume]
  → watchdog it (stall / timeout / usage-limit detection)
  → when it exits, VERIFY INDEPENDENTLY: gate battery (real exit codes),
    new git commits, tracker checkpoint diff
  → record, report, commit+push REPORT.md, decide what runs next
  → repeat until every checkpoint is DONE and the gates confirm it
```

The plan docs stay the authority — conductor never re-plans your work. It only enforces
the rituals (pre/post-session, QA-previous-session, evidence-or-it-didn't-happen) and
keeps the loop moving without you.

## Trust model (why it's shaped like this)

- **Agents' claims are never trusted.** After every session conductor re-runs the gate
  battery itself and diffs the tracker + git log. A checkpoint only counts when the row
  flipped to DONE **and** commits exist **and** gates are green.
- **"All DONE" is confirmed, not believed.** When the tracker says the plan is complete,
  conductor runs the full battery one more time before declaring victory. An agent can
  flip rows to DONE; it cannot flip a red build green.
- **Failures loop back with evidence.** Red gates → the next session is a *fix session*
  whose prompt embeds the actual failing gate output.
- **Everything is resumable.** State is persisted on every transition
  (`.conductor/state.json`, atomic writes). Kill conductor, reboot, Ctrl+C — running
  `conductor run` again picks up where it left off, resuming the interrupted agent
  session via `claude --resume <session-id>`.

## Quick start (Loom)

```powershell
cd C:\code\conductor
dotnet build

# see what the first session prompt would be — spawns nothing
dotnet run --project src\Conductor -- run --dry-run -p examples\loom\loom.plan.json

# run ONE session and stop (recommended for the first supervised run)
dotnet run --project src\Conductor -- run --once -p examples\loom\loom.plan.json

# run the whole plan; Ctrl+C is always safe
dotnet run --project src\Conductor -- run -p examples\loom\loom.plan.json
```

Tip: `dotnet publish src\Conductor -c Release -o bin` gives you a standalone
`bin\conductor.exe`; put it on PATH and set `CONDUCTOR_PLAN` to skip `-p`.

## Watching it

**Behind the laptop** — the default is a live Spectre dashboard: plan/stage progress,
the agent's tool stream, gate results, cost, stall timer. Keys:

| Key | Effect |
|---|---|
| `P` | pause after the current session (loop idles, process stays up) |
| `R` | resume a paused / needs-attention run |
| `K` | kill the current agent session (conductor re-evaluates and continues) |
| `S` | skip the current stage — flagged loudly for human review |
| `Q` | quit after the current session (state saved; `run` continues later) |
| `A` | abort now (kills session, stops conductor) |

**AFK** — after every session conductor rewrites `.conductor/REPORT.md` in the target
repo and commits + pushes it (configurable). Open it on GitHub from your phone: status,
per-stage progress, session history with outcomes/costs, failing gate tails, the latest
tracker handoff. The agent's own per-checkpoint commits are pushed by the sessions
themselves, so `git log` is the second AFK view.

**From another terminal** — the same verbs work out-of-process via a control file:

```powershell
conductor status  -p examples\loom\loom.plan.json   # tables: stages, recent sessions
conductor pause   -p examples\loom\loom.plan.json
conductor resume  -p examples\loom\loom.plan.json
conductor kill    -p examples\loom\loom.plan.json
conductor skip    -p examples\loom\loom.plan.json
conductor abort   -p examples\loom\loom.plan.json
conductor report  -p examples\loom\loom.plan.json   # regenerate REPORT.md on demand
```

## How a session is judged

| Observation | Outcome | Next |
|---|---|---|
| gates green + new commits + ≥1 checkpoint newly DONE | `Advanced` | next checkpoint/stage, attempts reset |
| gates green + new commits, nothing flipped | `Progress` | another deliver session (stages are multi-session) |
| gates green, no commits | `NoProgress` | fix session, attempt burned |
| any required gate red | `GatesRed` | fix session with gate output embedded |
| no output for `stallMinutes` | `Stalled` | killed, then `claude --resume` of the same session (≤ `maxResumesPerSession`) |
| over `sessionTimeoutMinutes` | `TimedOut` | same as stalled |
| backend says usage/rate limit | `LimitBackoff` | wait `backoffMinutes`, resume same session — no attempt burned |
| tracker handoff contains `HUMAN:` or a row flips to BLOCKED | — | **NeedsHuman**: loop parks, report + notify fire |

Attempt budget per stage = `stage.sessions × limits.stageSlackFactor`. When exhausted,
the **advisor** (a cheap second model — deepseek via opencode by default) is asked to
choose retry / resume / skip / human; with no advisor (or an unparseable answer) conductor
parks as NeedsHuman. The advisor is deliberately marginal: deterministic rules first.

## Plan config

Everything lives in one JSON per mega plan — see `examples/loom/loom.plan.json` (commented).
The important parts:

- `agent` — command + args for headless sessions. `{prompt}`, `{sessionId}`,
  `{claudeSessionId}` are substituted. Swap in opencode by changing this block
  (set `"output": "text"` for non-stream-json backends).
- `gates` — the battery conductor runs itself. `skipIfMissing` lets you list gates that
  don't exist yet (e.g. `scripts/loom-guards.ps1` arrives in L1). `optional: true`
  reports but never blocks.
- `stages` — ids must match the tracker's checkpoint prefixes (`L0` ↔ `L0.1`, `L0.2`…).
  Progress is measured from the tracker table itself; the tracker stays the single
  source of truth.
- `limits` — watchdog + budget knobs.
- `templatesDir` — the four prompt templates (`session/fix/resume/advisor.md`) are
  editable text; built-in defaults are used if a file is missing.
- `notify` — optional command run on NeedsHuman/completion (`{message}` substituted),
  e.g. a BurntToast toast or a webhook curl.

## Runtime files (in the target repo)

```
.conductor/
  state.json          resumable run state (atomic writes; .corrupt quarantine)
  REPORT.md           the AFK report — the only file conductor commits
  conductor.log       orchestrator log
  control.json        transient control verbs from the CLI
  conductor.lock      PID lock (two conductors can't fight over one repo)
  logs/session-NNN.jsonl       raw agent stream per session
  logs/session-NNN.prompt.md   exact prompt each session got (audit trail)
```

A `.gitignore` inside keeps everything but `REPORT.md` out of the repo.

## Testing without burning tokens

`tools/fake-agent.ps1` impersonates claude's stream-json and can simulate
success / stall / red-gates / usage-limit. The smoke setup lives in the test suite's
approach: point a plan's `agent.command` at it. Unit tests:
`dotnet test` (tracker parsing, gate exit codes, state round-trip, prompt rendering).

## Design decisions

- **The tracker is the progress database.** No parallel bookkeeping to drift; if you
  hand-edit LOOM-START.md, conductor sees it next loop.
- **Sessions are processes, not threads.** A hung agent can always be killed as a tree;
  a killed conductor can always resume the agent by session id.
- **PowerShell gates with `; exit $LASTEXITCODE`.** Real exit codes — piping through
  anything that swallows them is how a non-building UI once shipped.
- **Deterministic first, model second.** The advisor is consulted only at genuine
  dead-ends and its answer is validated against a four-verb vocabulary.
