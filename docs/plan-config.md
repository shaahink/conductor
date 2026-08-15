# Plan configuration reference

Everything Conductor needs for one mega plan lives in a single JSON file. `conductor init` writes a
working one; this page is the full schema behind it.

You do not need most of this. A plan with `name`, `repo`, `tracker`, `agent`, `stages` and `gates`
runs. Everything else has a default that is chosen to be the right answer.

Comments (`//`) are allowed — the loader tolerates them, and the scaffold uses them. Every editor
keeps them: `plan set`, `plan add-stage`, an applied `plan import` and the Face's plan editor all
splice their change into the raw file instead of re-serialising it, so comments, key order and
formatting survive, nothing changes but the edited values and `planVersion`, and no default you
never wrote is materialised into the file. (Until KS3.2 the rewrite dropped every comment and kept
the annotated original as `<plan>.bak`; that apology path is gone because there is nothing to
apologise for.)

`plan set` only writes keys this page declares. An undeclared key — a typo like
`limits.maxRunCostUsdd`, or the right name in the wrong place like a bare `maxRunCostUsd` — is
refused with the path it thinks you meant, because nothing in the engine would ever read it.
`--create` writes one anyway. A key that is *declared* but absent from the file (any optional field:
it is null, so the serialiser omits it) sets normally, and so does a whole missing block —
`plan set telegram.pollIntervalSeconds 10` creates the `telegram` object.

An edit only reaches a **running** engine through the `reload-plan` control verb: `plan set` queues it
for you when a live engine holds the plan's `.conductor` lock (and says so, naming the pid), and
prints the exact `conductor plan reload` command when it does not.

## Root fields

| Field | Type | Description |
|---|---|---|
| `version` | string | Schema version (`"1.0"`). Rejects unsupported versions with a clear diagnostic. |
| `planVersion` | int | Monotonic edit counter, bumped on every `plan set/reload/add-stage`. |
| `name` | string | Plan name — appears in dashboard header + report. |
| `repo` | string | Absolute path to the repository directory. |
| `satelliteRepos` | string[] | Sibling repos this plan's work may also land in — absolute, or relative to `repo`. The session verdict diffs each of them for commits. See below. |
| `tracker` | string | Path (relative to repo) to the TRACKER.md file. |
| `planDoc` | string | Path (relative to repo) to the plan/design doc. |
| `branchPattern` | string | Regex — conductor warns if the current branch doesn't match. |
| `pauseOnBlocked` | bool | Park at NeedsHuman when a BLOCKED row is found. Default true. |
| `batteryCollapse` | bool | Skip agent's pre-session ritual, defer to conductor's battery. Saves tokens by not paying an agent to run gates the engine runs anyway; **the size of the saving has never been measured** (FU-B10-2 — it needs an A/B on the same checkpoints with only this flag flipped). |
| `verifyEachDelivery` | bool | Default true. Queue a Verify session after every delivery. Set false to rely on the audit and the gate battery instead. It is the **lowest-precedence** QA input there is: a `pipeline.qa` dial and a stage's `overrides.skipVerification` both outrank it, in either direction. |
| `promptExtra` | string | Prepended to every session prompt (high-level context). |

### `satelliteRepos` — when the work lands next door (SC4.3)

The verdict asks git whether the session produced anything. Asking only `repo` is wrong for any plan
whose deliverable lives partly in a sibling checkout: one such stage was delivered in full and scored
**NoProgress twice**, because the primary repo's log was empty exactly as intended.

```jsonc
"repo": "C:/code/app",
"satelliteRepos": ["../app-sdk", "C:/code/shared-protos"]
```

- Every declared repo is diffed for commits over the session window alongside `repo`, and a commit in
  any of them makes the session **Progress**, sets the workflow's `hasCommits`, and keeps the stall
  and circuit-breaker checks from calling the session empty. Conductor's own `chore(conductor):`
  commits are excluded there just as they are here.
- Satellite commits are reported **separately**, never folded into this repo's count: the verdict line
  reads `commits 2 (incl. 1 in satellite repo(s): …)`, and the report lists them tagged `[<label>]`.
  A checkpoint delivered next door records its commit as `<sha>@<label>`.
- The label is the satellite's directory name. Entries that are blank, duplicated, or point at `repo`
  itself are ignored — counting the primary twice would double every commit.
- **`conductor doctor` FAILS on a satellite path that is missing or is not a git repo**, naming it.
  A typo here is silent otherwise: the run keeps scoring, it just never counts the repo it was told
  about, which is the failure the setting exists to prevent.

## `agent` — Agent process config

| Field | Type | Description |
|---|---|---|
| `command` | string | CLI exe: `"opencode"`, `"claude"`, or any executable. |
| `args` | string[] | Arguments. `{prompt}`, `{sessionId}` and `{model}` are substituted. |
| `resumeArgs` | string[] | Arguments for resuming a session (`{prompt}`, `{claudeSessionId}`, `{model}`). |
| `provider` | string | Adapter: `"opencode"`, `"claude"`, `"text"`. Inferred from `output` if unset. |
| `output` | string | `"stream-json"` (claude) or `"text"` (opencode etc.). Legacy. |
| `systemPrompt` | string | System prompt injected before the base prompt (persona). |
| `model` | string | Model override (e.g. `"claude-sonnet-4-20250514"`). **Only reaches the CLI where the template says `{model}`** — see below. |
| `temperature` | double | Sampling temperature (0.0–2.0). |
| `env` | object | Extra environment variables for the agent process. |
| `inheritMcpServers` | bool | Default true. Whether a session also gets the MCP servers configured on this machine — see below. |

**`inheritMcpServers` — the session's tools are conductor's plus the operator's.** Every session is
launched against a config conductor writes itself, holding the `conductor-tasks` server that carries
`task`, `note`, `bug` and `bg`. That config now also carries the MCP servers the operator has already
configured: `mcpServers` from `~/.claude.json` (user scope), from the repo's `.mcp.json` (project
scope) and from `projects[<repo>].mcpServers` in `~/.claude.json` (local scope), with the later scope
winning a name collision — plus, for the opencode dialect, the `mcp` map from
`~/.config/opencode/opencode.json` and the repo's own `opencode.json`/`.jsonc`. It used to carry
conductor's server *only*, which is why a user-scope chrome-devtools server was invisible to every
spawned session. `conductor-tasks` is conductor's name and an operator entry using it is dropped, so
the claim path cannot be taken over; an unreadable or malformed operator config is skipped with a line
in the session log rather than failing the run. Set `inheritMcpServers: false` when a run must not
depend on the local machine's setup at all — conductor's own server is wired either way.

**`model` needs a `{model}` placeholder, in both templates.** The model is substituted, never appended:
a plan that sets `model` but whose `args` have no `{model}` runs the CLI's own default model while the
plan file, `journey` and `/state` all keep reporting the pinned one. `resumeArgs` is a separate template
that replaces `args` on resume, so a placeholder missing *there* switches model halfway through a stage.
`doctor` FAILS (not warns) on either gap, naming the stages (SC3.1) — it is the one config error nothing
downstream can detect. A stage `agent.model` override and a `pipeline.roles.*.model` rule count the same
way. When no model is set anywhere, a lone `{model}` token and the `--model`/`-m` flag before it are
dropped, so the CLI never receives an empty flag.

## `advisor` — Second brain for dead-ends

A model consulted only where you ask for one: turning prose into a plan, refining or splitting a
card, and judging a stuck stage. Never inside scheduling — the loop stays deterministic.

There is no default advisor: with no `advisor` block, an ambiguous session outcome takes conductor's
deterministic default and nothing is ever consulted. The defaults below apply once the block exists.

| Field | Type | Description |
|---|---|---|
| `enabled` | bool | Default true. |
| `command` | string | CLI exe. Default `"claude"`. Any executable that answers on stdout works — e.g. `"opencode"` for a cheap model. |
| `args` | string[] | Args with a `{prompt}` placeholder. Default `["-p", "{prompt}"]` — headless, one-shot, question on argv. A `{model}` placeholder is filled by `plan import --model`. |
| `output` | string | How to unwrap the answer: `"text"` (raw stdout, the default), `"json"` (claude `--output-format json`), or `"stream-json"` (NDJSON whose final result line carries it). |
| `timeoutMinutes` | int | Advisor timeout. Default 6. Minimum 1. |
| `remediationScript` | string | Shell command run when advisor returns `ApplyFix`. |

There is **no `advisor.provider`**. The advisor does not go through an agent adapter: `command` plus
its `args` pick the CLI and the model, and `output` only says how to unwrap the answer. Five shipped
plans carried the key anyway, copied from the `agent` block, where it really does select an adapter —
so the plan claimed one model was the second brain while another one answered. Any key this table
does not declare is now refused at plan load, naming it (SC3.4).

**An advisor that cannot answer is refused at load, not at 3am.** `args` that are explicitly empty (a
CLI spawned with no question sits on stdin until `timeoutMinutes` and answers nothing — the old
default), `args` with no `{prompt}`, an `output` kind nothing unwraps, and `timeoutMinutes` below 1
all fail the plan the way SC3.1's model gap does, and `doctor` reports them as the `plan` check.
`doctor` also carries an `advisor` line of its own: the invocation it would spawn and its timeout, or
a warn when the CLI named in `command` is not installed — that one is survivable (the consult fails
to spawn and the deterministic default takes over) but you should know before the run, not after.

## `statusAgent` — On-demand LLM status reporter

| Field | Type | Description |
|---|---|---|
| `enabled` | bool | Default true. |
| `command` | string | CLI exe. Default `"opencode"`. |
| `args` | string[] | Args with `{prompt}`. |
| `model` | string | Model override for status calls. |
| `timeoutMinutes` | int | How long a status call may run. Default 5. |
| `maxPerHour` | int | Rate limit. Default 12. |

## `stages[]` — Stage definitions

| Field | Type | Description |
|---|---|---|
| `id` | string | Short identifier, must match tracker checkpoint prefix (e.g. `"L0"`, `"P7.3"`). |
| `title` | string | Human-readable stage name. |
| `sessions` | int | Expected session count. Budget = `sessions × stageSlackFactor`. |
| `notes` | string | Stage-specific text appended to the session prompt. Braces: see below. |
| `ownerGate` | bool | Park at `AwaitingOwner` when stage goes green. Owner must approve to advance. |
| `persona` | string | Specialist persona: `architect`, `planner`, `qa`, `docs`, `reviewer`, `refactor`, `test-writer`, `git-cleanup`, `security-audit`. |
| `kind` | string | `"deliver"` (default) or `"review"` (advisory artifact, no mutations). |
| `dependsOn` | string[] | Stage IDs that must complete before this stage is ready. |
| `parentId` | string | Parent stage for hierarchical tree display. |
| `agent` | object | Per-stage agent override (merged over plan default). |
| `preHook` | object | Command run before the first session. Non-zero exit blocks the stage. |
| `postHook` | object | Command run after confirmation. Best-effort, never blocks. |
| `workflow` | string | Name of the workflow this stage runs (see `workflows` below). Falls back to `defaultWorkflow`, then to `deliver-verify`. |
| `overrides` | object | Per-stage workflow overrides. `skipVerification` (bool) is the one field, and it outranks the plan's `verifyEachDelivery` in both directions. A key this object does not declare is refused at load, naming the key that does the job. |
| `qa` | object | Per-stage QA dial — same shape as `pipeline.qa` (`mode`: `off` · `everySession` · `phaseGate`, plus `verifierThreshold`). Outranks the plan-level dial for this stage only. |
| `pathClaims` | string[] | Repo-relative paths this stage's work is expected to touch. Used to keep concurrent lanes off each other's files; on a single-session plan it is inert by construction, not by accident. |

### Braces in prose — `{word}` is refused, `{{word}}` is a literal

Stage `notes` and `promptExtra` are substituted into the prompt as **values**, so nothing in them is
ever expanded: a `{word}` there is not a variable, it is a broken instruction on its way to the
agent. The plan is refused at load — `doctor` reports it as the `plan` check — naming the stage and
the token. Write **doubled braces** when the prose really means a brace:

```json
"notes": "Serve it at GET /tasks/{{id}}/prompt, and add \"--model\", {{model}} to args."
```

renders as `GET /tasks/{id}/prompt` and `"--model", {model}`. Placeholders belong in **templates**
(`templatesDir/*.md`) and in `agent.args`, where they are resolved; a template that carries one
nothing resolves is caught by `doctor` (the `prompt` check composes every session kind for every
stage) and, at runtime, parks the run at NEEDS HUMAN with the refusal in `conductor.log` — fix the
template and `conductor resume` continues the same run.

Text the engine substitutes for you — a tracker handoff, gate output, an agent's transcript tail —
is data: braces in it are passed through verbatim and can never fail a run.

## `workflows` — Declarative session steps

| Field | Type | Description |
|---|---|---|
| `workflows` | object | Workflow definitions keyed by the name a stage refers to. Each is `{ "name", "repeat", "steps" }`. |
| `defaultWorkflow` | string | The workflow used by stages that name none. Unset = the built-in `deliver-verify`. |

Named workflows keyed by name; a stage picks one with `workflow`, the plan with `defaultWorkflow`.
Built-ins: `deliver-verify` (default), `big-dev-then-big-audit`, `docs-only`, `spike`. Each step is
`{ "id", "kind", "deliver", "runIf", "skipIf" }`, where `kind` ∈ `Deliver` · `Verify` · `Audit` · `Fix`.

`runIf` / `skipIf` speak one small vocabulary, **case-sensitive**:

| Kind | Tokens |
|---|---|
| boolean | `verifier.passed`, `circuit.broken`, `gatesGreen`, `hasCommits`, `stalled`, `stageComplete` |
| numeric | `verifier.score`, `stage.attempts`, `newlyDoneCount` — compared against a number with `>=` `<=` `>` `<` `==` `!=` |

A leading `!` negates: `"runIf": "!gatesGreen"`. An unrecognised token used to be permissive at runtime,
so `"!gatesgreen"` (wrong case) silently stopped depending on the run at all — bare junk always ran the
step, negated junk never did. The plan is now **refused at load** naming the vocabulary (SC3.1), so a
typo costs a startup message instead of a stage.

## `gates[]` — Gate battery

| Field | Type | Description |
|---|---|---|
| `name` | string | Gate name (appears in dashboard + logs). |
| `command` | string | Shell command. Exit code determines pass/fail. |
| `shell` | string | `"powershell"`, `"bash"` or `"sh"`. Defaults to the host shell — `powershell` on Windows, `bash` elsewhere. An explicit `"powershell"` runs `pwsh` on Linux/macOS. |
| `cwd` | string | Working dir relative to repo root. |
| `optional` | bool | Report but never block. |
| `skipIfMissing` | string | Skip gate while this file path doesn't exist. |
| `skipIfFresh` | string | Repo-relative output artifact. Skip the gate while it is newer than every change to the source — see below. |
| `watchPaths` | string[] | Extra inputs whose newest write time joins this gate's result-cache key. For inputs no git HEAD covers. |
| `tier` | string | `"fast"` (per-session under perPhase), `"full"` (phase end, and every session under perSession), or `"truth"` (phase confirmation only). Default `"full"`. |
| `parallel` | bool | Run concurrently with other parallel gates in the same batch. |
| `stages` | string[] | Only run when the current stage id is in this list. |
| `stageKinds` | string[] | Only run when the current stage's `kind` is in this list. Applies in addition to `stages`. |
| `timeoutMinutes` | int | Per-gate timeout. Default 20. |

**Leave `shell` unset for portable gates.** `conductor init` does, which is why a scaffolded plan
(`dotnet build`, `npm test`, `go test ./...`, `cargo test`, `pytest -q`) runs unchanged on any host.

**`"gates": []` (or the field omitted entirely) is a supported, deliberate choice** — not a
misconfiguration. Every verdict reads `"gates green (none configured)"` rather than failing or going
silently blank, and `conductor doctor` flags it as a warn-level notice, never a failure. Useful for a
docs-only or spike plan with no build/test surface.

### `gatePolicy` — Battery run strategy

- `"perSession"` (default): full battery after every session.
- `"perPhase"`: fast-tier gates per session; full battery only when a stage's checkpoints are all DONE.

## `limits` — Watchdog + budget

| Field | Type | Default | Description |
|---|---|---|---|
| `stallMinutes` | int | 12 | No agent output, no tool call and no live `bg` child for this long → the session is stalled and the grace window below starts. |
| `stallGraceMinutes` | int | 3 | How long a stalled session gets to recover before it is hard-killed. The stall is *detected* at `stallMinutes` and *acted on* this much later, so the shortest path from silence to a kill is the sum of the two. |
| `sessionTimeoutMinutes` | int | 240 | Hard session timeout. |
| `stallSeconds` | int | null | Seconds-precision override for `stallMinutes`. Minutes are the right unit for a real plan and useless for a rehearsal run whose sessions last seconds — that is the only reason these three exist. null = use the minutes field. |
| `stallGraceSeconds` | int | null | Seconds-precision override for `stallGraceMinutes`. |
| `sessionTimeoutSeconds` | int | null | Seconds-precision override for `sessionTimeoutMinutes`. |
| `maxResumesPerSession` | int | 2 | Max times a session can be resumed after stall/timeout. |
| `maxSessions` | int | null | Live session cap for the whole run: when the run's session count reaches it the loop **parks** at the next boundary (Paused, with the reason) instead of spawning another session, and raising or clearing it resumes the same run. null or 0 = no cap. Unlike the process-scoped `--max-sessions` flag — which stops the *process* — this is editable in flight (Face → Plan → Settings, or `plan set`, both of which queue a reload). |
| `authPreflight` | bool | true | Ask the agent CLI for one token (~$0.001) before the run's first session, so a run cannot start on a dead credential and discover it thirteen sessions in. Only recognised provider CLIs are probed; `doctor --no-auth-check` skips the same probe. |
| `sameFailureCircuitBreaker` | bool | true | When 2 consecutive sessions end with the same non-success outcome *and* matching symptoms (same failing gates, same stall shape), stop queuing another identical fix session and consult the advisor instead. This is what stops a retry loop from spending the whole budget re-running the failure. |
| `verifierThreshold` | int | 80 | Verifier score (0–100) a session must reach for its checkpoints to be marked DONE; below it the findings feed a retry. Overridable per stage (`stages[].qa.verifierThreshold`) and per plan (`pipeline.qa.verifierThreshold`); a value outside 1–100 is refused at load. |
| `stageSlackFactor` | int | 2 | Budget multiplier: `stage.sessions × this`. |
| `backoffMinutes` | int | 30 | Wait on usage/rate limit. |
| `maxBackoffs` | int | 10 | Hard cap on consecutive backoffs. |
| `maxRunCostUsd` | decimal | null | Total cost cap. Parks at AwaitingOwner when hit. |
| `maxRunTokens` | long | null | Total token cap. Same parking behaviour. |
| `maxSessionTokens` | long | null | Per-session token budget, counting cache reads. Enforced live: on cross the session is ended → RolledOver with handoff, next session fresh, no attempt burned. Also puts the budget in the session prompt. |
| `softBreakRatio` | double | 0.8 | Fraction of `maxSessionTokens` at which the agent is asked to land its sub-task and hand off. Delivered into the running session by a `PostToolUse` hook, so it arrives within one tool call. Leave room to act on it — 0.75 gives the agent the last quarter of the budget to finish and commit. |
| `approvalMode` | bool | false | Park at AwaitingOwner before every session. |
| `stallPatternTermination` | bool | true | 2× consecutive zero-output stall → NeedsHuman. |
| `stallBackoffMinutes` | int | 12 | Initial stall backoff, doubles each consecutive stall. |
| `maxConcurrentLanes` | int | 2 | Max concurrent Tier A analysis lanes. **Lane spend is not costed** — no token or dollar accounting exists in `LaneRunner`/`LaneWorkerPool`, so lanes bill against your account without moving `maxRunCostUsd`. |
| `dnsHealthCheck` | object | — | Pre-session DNS check (hosts, intervalSeconds). |
| `overheadCostPerSecond` | decimal | 0.0001 | Gate runtime cost estimate rate. |
| `batterySettleSeconds` | int | 120 | Ceiling on how long the gate battery waits for the session's own `bg:` children to exit before it judges. `0` disables the wait. |
| `maxPushesPerIncident` | int | 1 | How many notifications ONE park may emit. See below. `0` removes the cap. |

### A park notifies once (KS2.6)

An **incident** is keyed on `(status, attention reason)`. The first park on a given pair pushes; every
repeat of the same pair is silent, however many loop iterations pass — a NEEDS HUMAN park holds
quietly for as long as it takes. A **different** reason opens a new incident and does notify, and a
session that actually runs closes the open incident, so the same cause reached again after real work
buzzes again.

The cap exists because it was measured: on 2026-08-02 a tracker handoff that merely *mentioned* the
escalation token in prose was matched (the match is a plain case-insensitive substring, and stays
one), the run parked, and — because the park's idle delay was skipped under `--dry-run` — the loop
re-parked and re-notified at full speed for roughly two hundred phone notifications about one
unchanged fact.

Two related rules are not configurable:

- **`--dry-run` notifies nobody.** A preview spawns no agent, spends nothing, and sends no Telegram
  push, no webhook POST and no `notify.command` invocation — on the run-start, session-end,
  run-complete, blocked-until, owner-gate and NEEDS HUMAN paths alike. It also no longer spins: a dry
  run that walks into a park says what it found and stops.
- **A preflight backoff park says so.** The DNS/preflight branch used to only log, so a network blip
  could park a run for hours in silence. It now pushes once per backoff *escalation*, naming the
  window it is backing off for.

`conductor watches` lists the cap in force per live run, beside what would wake anybody for it.

### The battery settles, then retries once (SC4.1)

Two rules apply to every battery and are not configurable beyond the cap above, because both exist
to stop the verdict scoring the environment instead of the work:

- **Settle.** Before gates start, conductor waits for the background children *this session* started
  (`conductor bg start`, MCP `bg_start`) to actually exit, logging `battery settle: …`. If they are
  still running at `batterySettleSeconds` it starts anyway and says so — a child that never exits
  delays the verdict, it does not block the run.
- **One retry.** A **required** gate that fails is run again, once, before the battery can be called
  red. If the second run passes, the line reads `PASS on retry`; if it fails again the gate is red
  and the fix prompt says it failed twice. Optional gates are never retried — their failure blocks
  nothing. A failure line also carries the gate's duration against its last passing duration.

### What invalidates a cached gate pass (SC4.3)

A gate that already passed is not re-run. What "already" means is the whole question, and the key it
was decided on used to be the primary repo's HEAD alone — so a gate could be served a pass belonging
to a different tree, or to a different command. A gate's cached pass is now filed and looked up under:

- this repo's HEAD, **and**
- the gate's own `cwd` HEAD — for a gate that builds a sibling checkout, this is the only part of the
  key that moves when that checkout does, **and**
- the newest write time under any `watchPaths` the gate declares — the escape hatch for inputs that
  no git HEAD covers (a generated tree, a vendored drop), **and**
- a digest of the gate's `command` and `cwd`. **Editing a gate mid-run invalidates its cache**, and
  the phase gate's whole-battery reuse (`tree unchanged since last green battery`) reads the same
  digest, so an edited battery is never reused as if it were the one that passed.

Anything the key cannot read — a `cwd` that has been deleted, a directory outside git — becomes a
marker that matches no previous key. The cost is one gate run; it is never a false pass.

**`skipIfFresh` compares against uncommitted work too.** The artifact is fresh only when it is newer
than the last commit **and** newer than every uncommitted change in the tree (the artifact itself
excluded, so an untracked build output never dates itself fresh). Comparing against the last commit
alone made this useless where it mattered most: mid-session an agent's work is uncommitted by
definition, so a build output from before the edits still looked fresh and the gate skipped straight
over the changes it exists to check.

## `report` — AFK reporting

| Field | Type | Default |
|---|---|---|
| `commit` | bool | true |
| `push` | bool | true |
| `heartbeatMinutes` | int | 0 (disabled). Live-report during long sessions. |

## `notify` — Notifications

| Field | Type | Description |
|---|---|---|
| `command` | string | CLI command for needs-human / completion. |
| `args` | string[] | Arguments for `command`. `{message}` is the only placeholder substituted — an args list without it spawns the command with nothing to say. |
| `webhook`, `discord`, `slack` | object | Each has `url` + optional `headers`. |

## `telegram` — Telegram bot

| Field | Type | Description |
|---|---|---|
| `allowedChatIds` | string[] | Allowed chat IDs for commands. Empty = push-only **to nobody** — the bot has no chat to send to, and `doctor` warns. |
| `pollIntervalSeconds` | int | getUpdate polling interval. Default 4. |
| `enableTwoWay` | bool | Enable incoming commands via Telegram. |
| `apiBaseUrl` | string | Bot API root. Defaults to `https://api.telegram.org`; set it only to point at a test double. |
| `messageThreadId` | int | The forum topic every push of this run belongs to, when the chat is a forum supergroup. Unset — the ordinary case — means the run threads itself by replying to its own first message, which is the only way to group a run in a non-forum chat. |

Token read from the `CONDUCTOR_TELEGRAM_TOKEN` environment variable, or from
`<stateDir>/secrets.local.json` (written by the Face's Telegram tab / `POST /telegram/token`, and
never committed). The environment variable wins.

### Setup, end to end

1. **Create the bot.** Message [@BotFather](https://t.me/BotFather) → `/newbot` → it replies with the
   token. Either `setx CONDUCTOR_TELEGRAM_TOKEN <token>` (restart the shell) or paste it into the
   Face's Telegram tab, which saves it to the local secrets file for this plan.
2. **Find your chat id — the bootstrap that is easy to get stuck on.** The bot cannot message you
   first; Telegram only reveals a chat once *you* have written to it. So:
   - Open your new bot in Telegram and send it any message (`/start` will do).
   - Then read the chat id back from the Bot API:

     ```
     curl "https://api.telegram.org/bot<TOKEN>/getUpdates"
     ```

     The id is `result[0].message.chat.id` — a bare integer for a direct chat, negative for a group.
     If `result` is empty, the message did not arrive at the bot: check you messaged the right
     username, and note that a *running* Conductor consumes updates as it polls, so query while no
     run is polling (or just read the id from [@userinfobot](https://t.me/userinfobot) instead).
   - Put it in `allowedChatIds` (plan file, `plan set`, or the Face's Telegram tab).
3. **Confirm the whole path.** The Face's Telegram tab → "send test": it sends through the run's own
   push queue, so a green result is evidence about delivery, not just about reachability. `doctor`
   and `GET /telegram/status` report the same verdict (`willDeliver`) in the same words.

### Late configuration takes effect without a restart (SC1.3)

A token saved through `POST /telegram/token`, and a `telegram` block added or changed by a plan edit
(which queues a plan reload, applied at the next session boundary), are picked up by the **running**
engine: the service starts, restarts, or stops to match, and the log says which. The only case that
genuinely needs a restart is an engine process that holds no Telegram service at all — an older
build — and there `GET /telegram/status` answers `restartRequired: true` and the token endpoint says
so instead of reporting a silent success.

## `github` — Push the board to GitHub issues (KS9.1)

Absent by default, and absent means the mirror does not exist: a plan without this block behaves
exactly as it did before the block existed. Present with `enabled: false` is the same thing said out
loud.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | `false` | The master switch. False means the mirror never runs, even with a token present and a repo named. |
| `repo` | string | `""` | `owner/name` to mirror **into**. Empty and `enabled: true` derives it from the plan repo's `origin`; a scratch mirror does not have to live in the repo being worked on. |
| `board` | string | `"issues"` | `issues`, or `issues+project` for a Projects v2 board as well (needs `projectNumber` and a token with the `project` scope). Any other value is refused **by name** rather than read as the default — silently downgrading a typo to issues-only is indistinguishable from a project mirror that ran and did nothing. The project half refuses today whatever the scope: see [cli.md](cli.md#the-projects-v2-half-refuses-and-says-why). |
| `projectNumber` | int | `0` | The Projects v2 board number from the project URL. `0` with `board: "issues+project"` is refused by name, never a silent no-op — before a destination is resolved and before anything is dialled. |
| `liveMirror` | bool | `true` | Reconcile the board **as the run goes**, at the boundaries the engine already treats as boundaries, instead of only on a manual `github sync --backfill`. Only ever consulted under `enabled`, so a plan that never opted in has no mirror to switch off; set `false` to keep the backfill and nothing else. |
| `runHistoryIssue` | bool | `true` | Mirror the run's diary as one issue with one comment per finished session. |
| `reportAsPrComment` | bool | `false` | Post the run report as a PR comment when the run's branch has one open. Off by default — a comment on someone's PR is the loudest thing this integration can do. |
| `labelPrefix` | string | `"conductor"` | Prefix for every label conductor owns, so the mirror never fights the repository's own labels. `conductor` yields `conductor:status:done`. Labels *outside* this prefix are never removed. |

```jsonc
"github": {
  "enabled": true,
  "repo": "shaahink/conductor-board",
  "labelPrefix": "conductor"
}
```

The token is **not** in the plan. It comes from `$CONDUCTOR_GITHUB_TOKEN`, or from `githubToken` in
`<stateDir>/secrets.local.json` — the same file, and the same precedence, as the Telegram token.

### One way only

There is no inbound half and there will not be one. Nothing reads GitHub state back into run state,
the tracker, or the task graph; `Events/TaskWrites.cs` remains the only writer of task state, and an
architecture test fails the build if anything under the mirror names it. Moving a card on GitHub
therefore changes nothing in the run — documented behaviour, not a missing feature. Two-way sync was
considered and rejected (L6.3, D-7, ADR 0005): the tracker is the verified contract, and a board that
could write back would be a second one.

## `progress` — Tracker provider

| Field | Type | Description |
|---|---|---|
| `kind` | string | `"markdown-table"` (default), `"script"`, or `"plan-checkpoints"`. |
| `script` | object | Command + timeout for the `"script"` provider. |
| `checkpoints` | array | Inline checkpoints for `"plan-checkpoints"`. |

## `conventions` — Tracker format

| Field | Type | Default | Description |
|---|---|---|---|
| `stageIdPattern` | string | `(?<stage>[A-Za-z]+\d+)(?:\.\d+)?[a-z]?` | Regex with optional `stage` named group. |
| `handoffMarker` | string | `"## Handoff"` | Heading for the handoff block. |
| `humanToken` | string | `"HUMAN:"` | Token in handoff to request human decision. |
| `status` | object | — | Status vocabulary: `done`, `blocked`, `inProgress`, `todo` word lists. |

`examples/shamshir/` is the worked example of overriding `stageIdPattern` so a `P-0` / `P3.4b` style
tracker parses.

## `templatesDir` — Prompt template directory

Path (relative to plan file) to custom `session.md`, `fix.md`, `resume.md`, `audit.md`, `advisor.md`,
`review.md` templates. Falls back to built-in defaults when files are missing. `conductor init` drops
editable copies of `session.md` and `fix.md` so "templates as content" works the moment you run.

## `readOrder` — Session reading list

Array of file paths (relative to repo) that the agent reads in order at session start. Rendered as an
ordered list in the session prompt.

## `batteries` — Prompt context injection

| Field | Type | Default | Description |
|---|---|---|---|
| `lessons` | bool | true | Inject rolling lessons brief (`.conductor/lessons.md`). |
| `recentFailure` | bool | true | Inject compact failed-session summary when the last session didn't verify. |
| `ledger` | bool | true | Inject recent knowledge-ledger entries (`conductor note`). |
| `bugs` | bool | true | Inject the run's open tracked bugs (`conductor bug new`). |
| `lessonsMaxEntries` | int | 3 | Max lessons entries. |
| `ledgerMaxEntries` | int | 8 | Max ledger entries. |
| `maxBytes` | int | 2048 | Total byte cap for all battery sections. |

## `analysisLanes[]` — Tier A parallel analysis

Read-only lanes that run in a scratch directory concurrently with the primary session.

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

### There is no `mutatingLanes` block (KS3.3)

This page used to carry a `mutatingLanes[]` table with seven fields, above a blockquote admitting the
block was never scheduled. The property is **deleted** now, so the key resolves to nothing: `plan set`
refuses it and `doctor` names it as inert if it is still sitting in a file. Tier B lanes themselves are
untouched and still real — they are reached from follow-ups
(`LaneCoordinator.RunFollowupFixLanesAsync` builds them from `.conductor/followups.md` after a stage
confirms, sequentially), which is a different thing from a plan block and now reads as one.

## `supervisor` — The babysitter, named in the plan

The command `conductor watch` runs when a wake fires, with the ~30-line brief on stdin. Keeping it here
rather than in a shell loop means the supervision survives the terminal it was started from, ships with
the repo, and shows up in a diff. Costs nothing while quiet: the wait is a file-stat loop and the
command is invoked only on a wake. The wake set, the brief and the standing-orders contract are
documented in [`operating.md` §3](operating.md).

| Field | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool | true | Set false to keep the block and its orders in the plan while silencing the command — the reviewable way to turn a babysitter off for one night. |
| `command` | string | — | Run through the platform shell with the brief on stdin. Blank disables the block as surely as `enabled: false`. |
| `timeoutMinutes` | int | 10 | How long the command may run before it is killed. |
| `maxPerHour` | int | 6 | Invocations per rolling hour; 0 = unlimited. A cost fuse, not a nicety: a run that parks, is resumed by the supervisor and parks again on the same cause is a model invocation every few seconds until someone notices the bill. Fires are counted in `.conductor/supervisor-fires.log`, so the cap survives the fresh process every wake starts. |
| `standingOrders` | string | — | What the supervisor may decide alone and what it must escalate. Carried **into** the brief, so the agent reads its authority on the same stdin as the wake instead of being trusted to have been told separately. Unset = nothing stated, which a careful supervisor reads as "escalate everything". |
| `remote` | object | — | Send the wake off-box — see below. |

### `supervisor.remote` — when the supervisor is not on this machine

| Field | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool | true | Set false to keep the URL in the plan while silencing delivery. |
| `webhookUrl` | string | — | Receives the wake brief as the POST body, verbatim (`application/json`) — the same document the local supervisor gets on stdin, not a "something happened" ping. |
| `headers` | object | — | Headers for the webhook. Values expand `${NAME}` and `%NAME%` from the environment, so the plan can name a credential without ever containing one. |
| `telegram` | bool | false | Also push a compact wake line to `telegram.allowedChatIds`, sent by the `watch` process — so it still arrives when the engine is the thing that died. |
| `timeoutSeconds` | int | 20 | Ceiling on any one delivery. |
| `maxPerHour` | int | 12 | Dispatches per rolling hour; 0 = unlimited. Deliberately a separate fuse from `supervisor.maxPerHour`: a local supervisor that has burnt its budget is exactly when the human most needs the wake to reach them. |

Delivery is best-effort by design — a webhook that is down must not turn a parked run into a second
outage, so a failed send is reported on stderr and the watch still exits on its wake code.

## `packs` — Domain context, merged into every prompt

An array of pack **names**. Each resolves to `<templatesDir>/packs/<name>.md`, falling back to
`<planDir>/packs/<name>.md` so a pack written for one era is not stranded there, and the concatenation
is substituted into every prompt as `{packs}`. A name carrying a path separator or `..` is refused
rather than combined into a path — plan JSON can arrive over the control plane's import endpoint. A
name that resolves to no file is skipped silently.

```jsonc
"packs": ["dotnet-engineer", "modern-csharp", "agent-pitfalls"],
```

Use them for house style and the mistakes agents habitually make in this codebase's domain, instead of
restating those in every stage's notes.

**Packs are not free.** They land in the same composed prompt as `promptExtra` and the stage notes, and
on Windows the prompt reaches the agent as a command-line **argument**: through a `.cmd`/`.bat` shim the
ceiling is 8191 characters, and an argv over it is truncated rather than refused — the agent gets a
prompt that stops mid-sentence and nothing says so. `conductor doctor` measures the composed length per
stage and fails before a run rather than after; `conductor init` says the same thing where it offers the
block. Keep packs short, or move the long-form material into `readOrder`, which costs file reads instead
of argv.

## `pipeline` — Declarative pipeline rules

Absent (the default) means every classic behaviour is reproduced exactly: a plan with no `pipeline`
block behaves byte-for-byte as it did before the block existed.

| Field | Type | Description |
|---|---|---|
| `roles` | object | Maps a session role — `deliver`, `verify`, `audit`, `fix` — to the agent that runs it. A missing role uses the stage/plan default agent. A role's `model` is subject to the same `{model}`-placeholder rule as `agent.model`, and `doctor` fails on the gap. |
| `qa` | object | The QA frequency dial: `mode` ∈ `off` · `everySession` · `phaseGate`, plus an optional `verifierThreshold` (1–100). Outranks `verifyEachDelivery`; a stage's own `qa` outranks it. A typo'd mode is refused at load rather than silently projecting to classic behaviour. |
| `multiItem` | object | Whether one session may claim several conflict-free ready items. Absent or disabled = one active checkpoint per session. |

## `audit` — Phase-end audit

| Field | Type | Default |
|---|---|---|
| `enabled` | bool | true |
| `maxAttempts` | int | 1 |
| `enableParallel` | bool | true (audit runs as concurrent lane instead of sequential session). |

## `setup` / `teardown` — Lifecycle hooks

Optional commands run before/after every session and every gate battery.

| Field | Type | Description |
|---|---|---|
| `command` | string | Shell command. Best-effort: non-zero exit doesn't block. |
| `cwd` | string | Working dir relative to repo root. |
| `timeoutMinutes` | int | Default 3. |
