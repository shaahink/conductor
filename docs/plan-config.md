# Plan configuration reference

Everything Conductor needs for one mega plan lives in a single JSON file. `conductor init` writes a
working one; this page is the full schema behind it.

You do not need most of this. A plan with `name`, `repo`, `tracker`, `agent`, `stages` and `gates`
runs. Everything else has a default that is chosen to be the right answer.

Comments (`//`) are allowed — the loader tolerates them, and the scaffold uses them.

## Root fields

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

## `agent` — Agent process config

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

## `advisor` — Second brain for dead-ends

A model consulted only where you ask for one: turning prose into a plan, refining or splitting a
card, and judging a stuck stage. Never inside scheduling — the loop stays deterministic.

| Field | Type | Description |
|---|---|---|
| `enabled` | bool | Default true. |
| `command` | string | CLI exe (cheap model: deepseek via opencode by default). |
| `args` | string[] | Args with `{prompt}` placeholder. |
| `output` | string | `"text"` or `"json"` (claude `--output-format json`). |
| `timeoutMinutes` | int | Advisor timeout. Default 6. |
| `remediationScript` | string | Shell command run when advisor returns `ApplyFix`. |

## `statusAgent` — On-demand LLM status reporter

| Field | Type | Description |
|---|---|---|
| `enabled` | bool | Default true. |
| `command` | string | CLI exe. Default `"opencode"`. |
| `args` | string[] | Args with `{prompt}`. |
| `model` | string | Model override for status calls. |
| `maxPerHour` | int | Rate limit. Default 12. |

## `stages[]` — Stage definitions

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

## `gates[]` — Gate battery

| Field | Type | Description |
|---|---|---|
| `name` | string | Gate name (appears in dashboard + logs). |
| `command` | string | Shell command. Exit code determines pass/fail. |
| `shell` | string | `"powershell"`, `"bash"` or `"sh"`. Defaults to the host shell — `powershell` on Windows, `bash` elsewhere. An explicit `"powershell"` runs `pwsh` on Linux/macOS. |
| `cwd` | string | Working dir relative to repo root. |
| `optional` | bool | Report but never block. |
| `skipIfMissing` | string | Skip gate while this file path doesn't exist. |
| `tier` | string | `"fast"` (per-session under perPhase), `"full"` (phase end, and every session under perSession), or `"truth"` (phase confirmation only). Default `"full"`. |
| `parallel` | bool | Run concurrently with other parallel gates in the same batch. |
| `stages` | string[] | Only run when the current stage id is in this list. |
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
| `webhook`, `discord`, `slack` | object | Each has `url` + optional `headers`. |

## `telegram` — Telegram bot

| Field | Type | Description |
|---|---|---|
| `allowedChatIds` | string[] | Allowed chat IDs for commands. Empty = push-only **to nobody** — the bot has no chat to send to, and `doctor` warns. |
| `pollIntervalSeconds` | int | getUpdate polling interval. Default 4. |
| `enableTwoWay` | bool | Enable incoming commands via Telegram. |
| `apiBaseUrl` | string | Bot API root. Defaults to `https://api.telegram.org`; set it only to point at a test double. |

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
| `lessons` | bool | true | Inject rolling lessons brief. |
| `recentFailure` | bool | true | Inject compact failed-session summary. |
| `lessonsMaxEntries` | int | 3 | Max lessons entries. |
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

## `mutatingLanes[]` — Tier B isolated worktree lanes

Lanes that may write, isolated in a git worktree and merged behind a gate.

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
