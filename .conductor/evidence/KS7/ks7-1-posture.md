# KS7.1 — Permission posture: what bounds an unattended session, measured

**Checkpoint:** KS7.1 — *Permission posture: an allowlist/deny settings profile replaces
`--dangerously-skip-permissions` for unattended runs if the installed CLI sustains it — a karvan-class
stage must run green under the restricted profile with refusals telemetered, OR a filed finding says
precisely why not; the blast-radius story lands in ARCHITECTURE.md honestly either way.*

**Date:** 2026-08-18 · **Session:** #6 of `karvansara-edge` · **Branch:** `feat/karvansara-edge`

The short version: **the CLI sustains it, and the exit is met — a stage ran green under the restricted
profile with six refusals telemetered** (rig run `380c587c`). Getting there took a correction to my own
first conclusion, and the correction is the most useful thing in this document: dropping the bypass
flag turns the CLI's permission gate back on, that gate refuses `conductor task --done` and every
un-allowlisted MCP tool, and a run in that state delivers the work, passes its gates, and reports
`newly DONE []`. Filed as bug #51 and fixed in the engine. Nothing below is read off a settings doc.

---

## 1 · Flags verified against the installed CLI (trap 16)

`claude --version` → **2.1.235 (Claude Code)**. From that binary's own `--help`:

| flag | as the installed CLI states it |
|---|---|
| `--permission-mode <mode>` | choices: `acceptEdits`, `auto`, `bypassPermissions`, `manual`, `dontAsk`, `plan` |
| `--allowedTools, --allowed-tools <tools...>` | comma/space-separated allow list |
| `--disallowedTools, --disallowed-tools <tools...>` | comma/space-separated deny list |
| `--settings <file-or-json>` | "additional settings" — additive, not exclusive |
| `--setting-sources <sources>` | `user`, `project`, `local` |
| `--dangerously-skip-permissions` | "Bypass all permission checks." |
| `--allow-dangerously-skip-permissions` | *offers* bypass without enabling it by default |

**A trap the help text states outright**, in the `-p` entry: *"Settings files that fail validation are
silently ignored in this mode (no error dialog is shown)."* A malformed restricted profile does not
fail the run — it disappears. Every claim below is therefore made against an observed effect, never
against "we passed the file".

## 2 · Six live probes — what the profile actually does in print mode

Rig: `C:/Users/shahi/AppData/Local/Temp/ks71-probe`, model `claude-haiku-4-5-20251001`, each probe
asking for two Bash calls (`git status --short`, then `echo ok`). Raw folded streams:
[`ks7-1-cli-probes.txt`](ks7-1-cli-probes.txt).

| # | profile | flag | result |
|---|---|---|---|
| 1 | `deny: ["Bash"]` | — | **Bash absent from the init `tools` array.** A bare tool name removes the tool; no event fires because no call is possible. |
| 2 | `allow: [Bash(echo:*), …]`, `deny: [WebFetch, WebSearch]`, `defaultMode: acceptEdits` | — | `git status --short` **ran** (exit 128, not-a-repo). WebFetch/WebSearch absent. |
| 3 | same | `--permission-mode manual` | init reports `permissionMode: default`. `git status --short` **ran**. |
| 4 | same | `--permission-mode dontAsk` | `git status --short` **ran**. |
| 5 | `deny: ["Bash(git:*)", …]` | — | `git status --short` **refused**; `echo ok` ran. |
| 6 | same as 5 | `--dangerously-skip-permissions` | init reports `bypassPermissions` — and `git status --short` was **still refused**. |

Two conclusions hold from these probes alone, and a third that looked solid did not survive the rig --
recorded here because the correction is the point:

1. **`permissions.deny` is a real, enforced boundary**, in two flavours: a bare tool name deletes the
   tool from the advertised set (probe 1), a specifier intercepts the call (probe 5) and emits
   `{"type":"system","subtype":"permission_denied","tool_name":...,"decision_reason_type":...,"message":...}`
   followed by a `tool_result` with `is_error: true`.
2. **Deny is orthogonal to the bypass flag** (probe 6). The rules bite with
   `--dangerously-skip-permissions` on.
3. ~~*Print mode runs anything not denied, so `allow` gates nothing.*~~ **Wrong, and the rig proved
   it.** Probes 2-4 only ever asked for *read-only* shell commands (`git status --short`, `echo ok`).
   Those auto-approve; a mutating command does not. The conclusion was an artefact of the sample, and
   §4 has the measurement that replaces it.

### What that does to ND-5

ND-5 recommended dropping `--dangerously-skip-permissions`, *gated on verification* that an
allowlist/deny profile sustains a karvan-class run. **The gate passes -- with a correction to the
reasoning.** An allow/deny profile does replace the flag, but not because the allow list narrows what
is reachable. It works because dropping the flag restores the CLI's own permission gate; `allow` is
then what lets the run's own work back through. The finding first filed against ND-5 (bug #50) rested
on conclusion 3 and is closed as superseded; **bug #51** carries what actually matters.

## 3 · What shipped

`agent.permissions` — `{ mode, allow, deny }`, plan-wide or per stage
(`src/Conductor.Core/Models/PermissionsConfig.cs`).

- **The settings file conductor already owned now carries the posture.**
  `SessionRunner.Mcp.cs` → `WriteSessionSettings` composes the budget hook *and* `permissions`, and is
  written when **either** is due — the old writer returned null unless a token ceiling existed, so a
  posture-only plan would have gotten no file at all. Renamed `settings.budget.json` →
  `settings.session.json`, because it stopped being about the budget.
- **`--permission-mode` is appended** when the plan's own args do not name one — the same
  deference rule `--mcp-config` and `--settings` already followed.
- **Bypass flags are stripped** when the mode is anything but `bypassPermissions`
  (`PermissionPosture.StripBypass`), in `AgentSession.Start` — the one seam every session kind passes
  through, so a plan cannot declare a restricted posture and still hand the fix or audit session the
  escape hatch. A deny-only posture (no mode) leaves the command line untouched: conductor changes
  only what it was asked to change.
- **An unknown mode is refused BY NAME at plan load** (`PermissionsConfig.CollectPostureErrors`, called
  from `PlanConfig.CollectErrors`), plan-wide and per stage. The CLI ignores a mode it does not know,
  so a typo would otherwise produce a run *reporting* a posture it is not under.
- **Conductor's own claim path is folded into the allow list** under any non-bypass mode
  (`PermissionPosture.AllowRulesFor` → `mcp__conductor-tasks`, `Bash(conductor:*)`). §4b is why. Deny
  still resolves first, so a plan that explicitly denies one keeps its refusal — what it cannot do is
  forget to grant them.
- **Refusals are telemetered on three surfaces**: a `refusal` transcript line, a `toolRefused` event on
  the run's event log stamped with session and stage, and a counted line in the run log at session end.

## 4 · The rig: a stage green under the restricted profile, driven by the fresh build

Scratch repo `C:/Users/shahi/AppData/Local/Temp/ks71-rig`, its own plan, its own state dir, run with
`dotnet run --project src/Conductor -- run -p <rig plan> --once --headless --no-control-plane`. The rig
plan's `agent.args` deliberately **still carry `--dangerously-skip-permissions`**, so the strip has
something to remove; its posture is `mode: acceptEdits`, `allow: ["Read","Write"]`, `deny:
["Bash(curl:*)", "Bash(git push:*)", "WebFetch", "WebSearch"]`. The settings file conductor wrote for a
live session: [`ks7-1-rig-settings.session.json`](ks7-1-rig-settings.session.json).

### 4a · The profile reached the process

- run log -- `KS7.1: permission posture -- mode acceptEdits, 4 deny rule(s), 4 allow rule(s) (incl. 2
  for the claim path), 1 bypass flag(s) stripped`
- the session's own `init` event (`.conductor/logs/session-001.jsonl`) -- **`permissionMode:
  acceptEdits`**, despite `--dangerously-skip-permissions` sitting in the plan's args. The strip
  reached the process.
- the same init event's `tools` array -- `Bash`, `Read`, `Write` present; **`WebFetch` and `WebSearch`
  absent.** The tool-level deny removed them from what the model was told exists.

### 4b · Run 1 (b1b90d5c, session #2) -- the posture shut the run's own claim path

Refusals, each paired with the call that caused it, from the rig's transcript:

| tool call | refusal |
|---|---|
| `Bash conductor task --in-progress R1.1` | This command requires approval |
| `mcp__conductor-tasks__task_update` | ...but you haven't granted it yet |
| `Bash curl https://example.com` | Permission to use Bash with command curl ... has been denied |
| `mcp__conductor-tasks__conductor_note` | ...but you haven't granted it yet |
| `Bash ... Set-Content -Path ...hello.txt` | multiple operations; the following parts require approval |
| `Bash conductor task --done R1.1 --evidence hello.txt` | This command requires approval |

Outcome: `verdict inputs: gates GREEN · commits 0 · newly DONE [] · dirty YES` -> **NoProgress, fix
session queued** -- for a stage that had in fact been delivered. Both claim channels were shut, and the
board would have shown a stall rather than a permissions problem. Filed as **bug #51 (high)** and fixed
in the engine: `PermissionPosture.AllowRulesFor` folds `mcp__conductor-tasks` and `Bash(conductor:*)`
into the profile whenever a non-bypass mode is set.

### 4c · Run 2 (380c587c, session #1) -- green, with refusals telemetered

Same plan, same `allow: ["Read","Write"]`, nothing added by hand -- only the folded claim path:

```
session #1 exited (code 0, 2m, $0.14)
session #1 posture refused 6 tool call(s): ...
gate hello-exists: PASS (2s)
verdict inputs: gates GREEN · commits 0 · newly DONE [R1.1] · dirty YES
session #1 Advanced -- R1.1 done
```

**A stage ran green under the restricted profile with its refusals telemetered.** The six refusals in
that run, from the transcript:

| tool call | refusal |
|---|---|
| `Bash curl https://example.com` | Permission to use Bash with command curl ... has been denied |
| `Bash ... git add hello.txt && git commit -m ...` | multiple operations; parts require approval |
| `Bash ... git add hello.txt && git status` | multiple operations; `git add hello.txt` requires approval |
| `Bash ... git add hello.txt` | multiple operations; `git add hello.txt` requires approval |
| `Bash ... git commit -am ...` | multiple operations; requires approval |
| `Bash git -C ... add hello.txt` | This command requires approval |

Telemetry, all three surfaces, on that run:

- **transcript** -- 6 rows of kind `refusal` in `.conductor/transcript.jsonl`
- **event log** -- `select count(*) from events where type='ToolRefused'` -> **6**, each stamped
  `session_id 1`, `stageId R1`, carrying the CLI's verbatim message and its `reasonType`
- **run log** -- one counted line naming every distinct rule that fired

And the residual hazard, stated rather than papered over: **`git add` and `git commit` are refused
under a restricted posture unless the plan allows them.** The rig's gate was file existence, so the
stage still went green; a plan whose delivery is measured in commits must allow `Bash(git add:*)` and
`Bash(git commit:*)`. That is the "everything else your stage needs is still yours to allow" line in
`docs/plan-config.md`, with a concrete list behind it.

### 4d · A defect the rig caught that no unit test could

`SessionRunner.Activity.TrackActivity` is an *allowlist* of event kinds
(`tool`/`text`/`result`/`thinking`/`stderr`). A `refusal` reached no transcript and no Face pane --
silently. Fixed by naming `refusal` there; run 2's six transcript rows are the proof. The general
lesson is in the ledger: any new `AgentEvent` kind must be added to `TrackActivity` or it is invisible
everywhere except the run log.

## 5 · The blast-radius story, stated honestly

`ARCHITECTURE.md` §3b. The parts that were tempting to leave implicit and are now written down:

- There is **no OS sandbox**. The job object is a *lifetime* boundary, not a security boundary: the
  child runs as the operator with the operator's token. Codex CLI shipped a native Windows sandbox on
  restricted tokens/AppContainer/ACLs, so the primitives exist — building one is parked (plan §8).
- The boundary that does exist is the CLI's own permission gate: `deny` always, plus everything
  the gate refuses once the bypass flag is off. `allow` re-opens; it does not narrow.
- Refusal telemetry is what makes a deny list falsifiable: a profile that failed to load and a profile
  whose rules never matched are indistinguishable from outside the process, and print mode hides the
  first case by design.

## 6 · Gates

Scoped suites for everything touched (`KS7_1PermissionPosture`, `BudgetRail`, `Provider`, `PlanConfig`,
`ResolveArgs`, `AgentConfig`, `SF7_1Docs`) — see §7 for the run. `SF7_1DocsMatchRealityTests` failed
first, correctly, because `agent.permissions` was undocumented; `docs/plan-config.md` now carries the
key, the three fields and the honest note about what `allow` is worth.

## 7 · Runs

| what | result |
|---|---|
| `dotnet build Conductor.slnx -clp:ErrorsOnly` | Build succeeded, 0 warnings, 0 errors |
| `dotnet test --filter KS7_1PermissionPosture` | **44 / 44** |
| scoped: `KS7_1`, `Architecture`, `SF7_1Docs`, `PlanConfig`, `BudgetRail`, `Provider`, `AgentConfig`, `ResolveArgs`, `Transcript` | **213 / 213** |
| rig run 1 (b1b90d5c) | posture applied; claim path shut -> NoProgress (the finding) |
| rig run 2 (380c587c) | gates GREEN, `newly DONE [R1.1]`, `Advanced -- R1.1 done`, 6 refusals telemetered |

Two suites failed first and were fixed rather than relaxed: `SF7_1DocsMatchRealityTests` (because
`agent.permissions` was undocumented) and `ArchitectureTests` (PlanConfig.cs crossed its 500-line
ceiling -- the validation moved into `PermissionsConfig.CollectPostureErrors`; the ceiling did not move).
