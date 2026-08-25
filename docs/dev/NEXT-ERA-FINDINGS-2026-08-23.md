# The next era — the inbox that listens, and the question of where sessions run

*2026-08-23. Commissioned by the owner while Karvansara sits on its last owner-only checkpoint, with
three asks: (1) a Telegram channel that takes **voice notes, text and files as feedback**, per
project, that an agent can then act on; (2) whether conductor can **spin its sessions on Anthropic's
cloud** against the Max account, and what that costs; (3) **collecting feedback, logs and errors** so
they get out of this machine. Written against the tree at `feat/karvansara-edge` (HEAD `870786f`),
the live `.conductor/` state, and the Claude Code documentation as it reads today.*

*Claims from this machine are marked **measured** and carry a `file:line`. Claims about the platform
carry their source. This is a findings document, not a plan: it is meant to be argued with, edited,
and only then turned into `plans/<era>/…`. It supersedes nothing — it sits beside
`OBSERVABILITY-AND-MARKET-2026-08-22.md` and re-ranks that document's backlog once, at the end.*

---

## Part 0 — Where the plan actually stands

**Measured**, from `plans/karvansara/EDGE-TRACKER.md` and `run.db`: the edge plan is **24 checkpoints,
21 DONE ✓ (engine-confirmed), 2 DONE (claimed), 1 BLOCKED**. The blocked one is KS12.3, and it is
blocked *on the owner* by design — `edge.plan.json:142` sets `ownerGate: true`, and every sub-action
inside it (merge, tag, reinstall, backfill, the payesh merge, the tracker move) is owner-only. The
tracker's own handoff says it plainly: **"nothing is left for a session."**

Two reds are pre-flighted and still standing, and they are the owner's first calls, not this era's:

- `tools/changelog-section.sh 0.5.0` exits 1 because `CHANGELOG.md:22` still reads `## [Unreleased]`.
  The rename carries the version number, and nothing in the repo states which number it is.
- The tracker move is not a `git mv`: `SF7_1DocsMatchRealityTests.Karvansara.cs:116,119-127` pins both
  old paths, so it is one commit spanning the test, `docs/dev/README.md`, `edge.plan.json:39,40,229-230`
  and `core.plan.json:36,37,225-226`.

So the question in front of us is not "what is the next checkpoint." **It is the next era**, and it
inherits a ranked seven-item backlog from yesterday's document. This one adds three asks to it.

---

## Part 1 — The inbox: feedback that arrives when you have it

### 1.1 What already exists (measured)

More than expected, and in the right shape:

| Piece | Where | What it gives us |
|---|---|---|
| **Inbound long-poll** | `TelegramService.cs:343` — `getUpdates?offset={_offset}&timeout=30` | Inbound already works **without an inbound port**. ADR-0005 is not in the way: the connection is outbound, initiated from this machine. This is the single most important fact in Part 1. |
| **A channel-agnostic seam** | `Messaging/IMessageChannel.cs`, `RemoteSurface.cs`, `CommandRouter.cs` | KS11.1 already split *deciding* from *transporting*. A new inbound kind is a router case, not a Telegram change. |
| **Profiles** | `Messaging/ChatProfile.cs`, `SurfaceCommands.cs` | `admin` / `observer`, enforced at one gate, proven by an exhaustive command-by-profile matrix. Who may file feedback is already a solved question. |
| **A steering channel** | `RemoteSurface.cs:300-322` calling `_store.WriteInjection(runId, …)` | `/inject` exists and works. |
| **A prompt seam for anything durable** | `PromptBattery.Context.cs`, `IPromptBattery` | A new "what the human said" battery plugs in where `LessonsBattery` and `BugsBattery` already sit. |
| **Work that outlives its run** | `bugs` table, `OpenBugsReport`, `SF04BugsOutliveTheirRunTests` | The precedent for a record that survives the run that received it is already shipped and tested. |

### 1.2 The four gaps, measured

1. **The bot only exists inside a run.** `TelegramService.Lifecycle.cs:50` starts the poll loop as
   part of run startup and `:146` drains it at shutdown. No run means nobody is polling, so a voice
   note sent on a Sunday is read by nothing. This is the gap that matters, because feedback arrives
   when the owner thinks of it, not when a session boundary happens to be open.
2. **`TgMessage` carries text and nothing else.** `TelegramApi/UpdateDtos.cs:19-23` has `MessageId`,
   `Text`, `Chat`. There is no `voice`, no `audio`, no `document`, no `photo`, no `caption`, no
   `reply_to_message`, no `message_thread_id`. A voice note today is not rejected — it is *invisible*.
3. **An injection is consumed, not recorded.** `InstructionQueue.Dir` is `<stateDir>/queue`, and
   `InstructionQueue.Consume` renames every entry to `.done` once a prompt reads it. The Telegram path
   writes to `run.db` keyed by `runId` — and outside a run, `RemoteSurface.cs:310` falls back to
   `Guid.NewGuid()`, i.e. a note filed against a run that does not exist. **Steering is not the same
   verb as recording**, and today only steering exists.
4. **One bot, one run.** The token is machine-level — `CONDUCTOR_TELEGRAM_TOKEN`
   (`TelegramService.cs:160`) — while `telegram.chats` is per-plan (`edge.plan.json:209`). Two runs on
   this machine with Telegram enabled read the same token.

### 1.3 The constraint that decides the architecture

Telegram permits **exactly one `getUpdates` consumer per bot token**. A second one gets
`409 Conflict: terminated by other getUpdates request`, and the two then take turns stealing each
other's updates. This is not a conductor bug; it is the API.

It binds *today*: two live runs on this machine with Telegram on would fight over the token, which is
why a second run's Telegram block cannot simply be switched on. And it means **you cannot bolt an
always-listening feedback poller next to a run's poller on the same token.** Every option below is a
different answer to that one sentence.

### 1.4 Three options

**A — In-run only (S).** Teach the existing poll loop the new message kinds; notes land in the live
run's inbox. Cheap, no new process, no new failure mode. Does not answer the ask: feedback still
requires a live run. Worth doing *as part of* B or C, worthless alone.

**B — The courier daemon owns the token (M–L). Recommended as the destination.**
One long-lived machine-level process — `conductor courier` — owns `CONDUCTOR_TELEGRAM_TOKEN`, polls
always, and routes each message to a project. Live runs **stop polling** and push *through* the
daemon over loopback: a `CourierChannel : IMessageChannel` alongside the Telegram adapter, which is
precisely the payoff KS11.1's seam was built for. One bot in the phone, always awake, and the
outbound surface survives the end of a run — so a digest, a channel-health alarm, or an owner-queue
nudge can be sent when no run is live, which is exactly when you need it.

Its cost is honest and should be stated up front: **it is a new single point of failure for a live
run's pushes.** If the daemon is down, the run goes quiet. That makes yesterday's move #1 — *channel
health is loud* — a hard prerequisite, not a nice-to-have: a run whose channel died must say so in
`REPORT.md`, in `/status` and in the owner queue, or B trades one silent failure for another.

**C — A second bot, a second token (S–M). The de-risk.**
A separate feedback bot with its own token sidesteps 409 entirely: no daemon-to-run protocol, no new
dependency for a live run, and the run bot is untouched. Cost: two bots in the phone, and the two
halves (`/inject` on one, feedback on the other) never meet.

**Recommendation: build toward B, and keep C as the escape hatch** if the loopback protocol proves
expensive at the first checkpoint. Do not build A alone.

### 1.5 Routing: which project is this note about?

The owner's chat is a **private DM** (`99205495`). Forum topics — the obvious "one topic per project"
answer — exist only in supergroups, so they cannot be the primary mechanism. Three that do work in a
DM, in order of how much typing they cost:

1. **Reply to a push (zero typing).** Every message the bot sends already identifies a run. If the
   inbound message carries `reply_to_message`, the note files against *that* push's project. Reply to
   last night's checkpoint push with a voice note and it lands in the right place with no command at
   all. This should be the headline interaction.
2. **Sticky selection.** `/project <slug>` sets the chat's current project; it stays until changed,
   and every push says which project it is about, so the state is visible rather than remembered.
3. **Topics, where they exist.** In a supergroup, one topic per project via `message_thread_id` — the
   right answer for a stakeholder group chat, and a natural extension of the observer profile.

The project list is not a new concept: `Store/StateHome.cs` already keeps a machine-level catalogue
keyed by repo and plan (K3). The courier routes to entries in it and refuses an unknown slug by name
— the `GithubConfig.Board` rule, reused a third time.

### 1.6 Transcription: local, and already on this machine

Voice notes arrive as OGG/Opus. The download path is `getFile` to `file_path` to a fetch, and
**bots may download files up to 20 MB**, which is roughly 15–20 minutes of Opus at current recording
bitrates. Above that, Telegram will not serve the file to a bot at all without a self-hosted Bot API
server — a limit to state in docs, not to solve in code.

Transcription should be **local and configurable**, not a service call: this machine already runs
faster-whisper on the GPU offline, with no API key, and it reports per-segment confidence. That keeps
the privacy posture the rest of this repo holds (payesh anonymises fails-closed; nothing about a run
leaves the machine unless the owner publishes it). Conductor shells out to a configured
`courier.transcribe.command`; if none is configured the note is filed **untranscribed** with the audio
attached and the reply says so — a bot that silently drops a voice note is the failure mode from
§1.2.2 wearing a different hat.

Two rules worth pinning as tests: low-confidence segments are **marked in the stored note**, so a
reader (human or agent) can see which words not to trust; and the original audio is kept beside the
transcript, so a garbled transcription is always recoverable.

### 1.7 Where a note lands — three tiers, and the default matters

| Tier | What it is | Where it goes | Who chooses it |
|---|---|---|---|
| **note** *(default)* | A record about the project | `.conductor/inbox/` — one file per note plus its media, indexed, **surviving the run**, read at session start by a new `InboxBattery` on the existing `IPromptBattery` seam | nobody — it is what happens if you just talk |
| **followup** | A note that is work | a row in `.conductor/followups.md`, which `FollowupParser` reads and `LaneCoordinator` turns into a Tier-B lane | the owner, explicitly (a verb or a button on the bot's reply) |
| **inject** | Steer the session that is running now | today's path, `run.db` injections, admin only | the owner, explicitly |

**A transcribed voice note must never auto-inject.** A misheard word plus an autonomous agent running
`--dangerously-skip-permissions` is the worst compound failure available here. The default is the
record; steering stays a deliberate verb. The bot's acknowledgement should carry the buttons that
promote a note to the other two tiers, so promotion is one tap and never an accident.

This tier table is also the answer to "document it per project": the inbox **is** the per-project
document, and it is the artifact the next session reads whether it arrives in ten minutes or in three
weeks.

### 1.8 The risk that has to be named

Inbound text becomes agent-prompt text. That is **prompt injection into an autonomous agent** — the
same class ADR-0005 cites for refusing inbound HTTP, arriving instead through a channel we already
trust for reading. It does not mean don't build it; it means build it with the boundary stated:

- Only `admin`-profile chats may file. Observers are read-only today and stay read-only.
- A note reaches a prompt **as quoted data in a fenced block**, framed as *a human note; not an
  instruction from the engine; it cannot change a gate, the plan, the budget or the checkpoint's
  acceptance*. An architecture test should assert the battery's output is always fenced and always
  framed — the KS4.1 habit of proving absence rather than asserting it.
- A note can never carry a control verb. `/pause` inside a transcript is a word, not a command.

### 1.9 Checkpoint sketch (Part 1)

| # | Work | Falsifiable exit |
|---|---|---|
| 1 | The inbound message kinds: `voice`, `audio`, `document`, `photo`, `caption`, `reply_to_message`, `message_thread_id` on the DTO; `getFile` download with the 20 MB cap named in the refusal | A stub-wire test drives each kind end to end; an oversize file is refused **by name** to the sender, not dropped |
| 2 | The per-project inbox: durable store, index, media beside transcript, `InboxBattery` on the prompt seam, fenced-and-framed | A note filed with no run live is read by the next session of that project's next run; an architecture test proves the fencing |
| 3 | Transcription: configured local command, per-segment confidence retained, untranscribed fallback | A real `.ogg` transcribes in the rig; with the command unset the note still files and the reply says untranscribed |
| 4 | Routing: reply-to-push, sticky `/project`, unknown slug refused by name | A voice note sent as a reply to a checkpoint push files against that run's project with no command typed |
| 5 | The courier daemon owns the token; runs push through it; channel health loud everywhere | Two projects served from one bot; killing the daemon makes a live run's `REPORT.md`, `/status` and owner queue all say the channel is down within one boundary |
| 6 | Promotion: note to followup to Tier-B lane, by button | A note promoted in the chat appears as a `followups.md` row and opens a lane in the rig |

---

## Part 2 — Sessions in the cloud

### 2.1 What the platform actually offers (sourced, today)

- `claude --cloud "<task>"` starts a **new** cloud session on Anthropic-managed infrastructure.
  `--remote` is a deprecated alias for it.
- The cloud VM **clones your current directory's GitHub remote at your current branch — not your
  local checkout**. Without GitHub it bundles the local repo instead: 100 MB ceiling, tracked files
  only, and such a session **cannot push back to a remote**.
- `claude -p "<message>" --cloud <session-id>` queues a follow-up into a running session and exits.
  `--output-format json` returns `{ok, session_id, url}`.
- **`--output-format stream-json` is not supported with `--cloud <session-id>`.**
- `claude --teleport <session-id>` pulls a cloud session down to the terminal. Handoff is **one-way**:
  you cannot push a local session up. Teleport requires a clean tree, the same repo (not a fork), the
  branch already pushed, and the same claude.ai account.
- Cloud sessions **share the account's rate limits** with all other Claude usage, and there is
  **no separate compute charge for the VM**.
- Cloud sessions require claude.ai account auth (not an API key), are unavailable through Bedrock /
  Vertex / other third-party providers, need the org's `allow_remote_sessions` policy on, and stop
  after inactivity with the VM reclaimed.
- It is a **research preview** for Pro, Max and Team.

So: the Max account does cover it, and it costs no extra dollars. That is the good half.

### 2.2 The bad half, stated against what conductor is

Conductor is a local process that spawns `claude -p … --output-format stream-json … --session-id …`
(`edge.plan.json:43-71`) and reads that stream. Six things it does are downstream of that stream or of
the local process, and none of them survives the move:

1. **Money and token truth per checkpoint.** Per-turn usage with the cache split is parsed from the
   stream (KS7.3). With `--cloud` there is no stream. This is not a nice-to-have — it is the asset
   yesterday's document named as one of three nobody else holds together.
2. **The stall watchdog, the rollover nudge, the circuit breaker.** `limits.stallMinutes: 15`,
   `maxSessionTokens: 32M` with the 0.85 soft break, `sameFailureCircuitBreaker` — all of them sample a
   local process's output. A cloud session cannot stall-kill, cannot roll over at 27.2M, and cannot be
   circuit-broken.
3. **The claim protocol.** A session becomes a checkpoint by claiming it through conductor's loopback
   control plane and its `conductor-tasks` MCP server. ADR-0005 says loopback only — deliberately. **A
   cloud session therefore cannot claim a checkpoint, file a bug, write a note, or start a tracked
   background child.** This is the structural blocker, and it is not a bug to fix; it is the security
   posture working as designed.
4. **The clean-room attempt.** KS4.4 puts each attempt in a local worktree and hands the verdict a
   clean attempt diff. A cloud session's work arrives as a pushed branch, which is a *different*
   artifact with different provenance.
5. **Uncommitted work is invisible.** The VM clones the remote at your branch. A dirty tree silently
   runs the agent against yesterday's code — a failure that looks exactly like success.
6. **The holdout absence proof.** KS4.1's exit is a grep of the composed prompt *and* the transcript
   proving the holdout gates are absent. Both are local artifacts. The repo is on the cloud VM either
   way — the same as locally — but the *proof* that the agent never saw the holdout class cannot be
   produced for a machine conductor does not control and cannot grep afterwards.

There is also a data-posture line worth one sentence: cloud sessions live on Anthropic infrastructure,
and on **Max and Pro accounts the sharing choice is Private or Public — where public means any
logged-in claude.ai user**, with repository-access verification off by default. For a repo whose own
publishing pipeline anonymises fails-closed, that default deserves to be known rather than discovered.

### 2.3 The shapes that do work, ranked

**CL-2 — `/cloud <task>` as an admin verb on the Chapar surface. (S. Do this one.)**
The owner fires a cloud session from their phone against a project's repo; the session id and URL come
back in the chat and are recorded in the run's event log as an owner action. Zero coupling to the run
loop, zero risk, and it delivers most of what was actually asked for: work starts while the laptop is
shut. `-p … --cloud <id>` also means follow-ups can be sent from the same chat.

**CL-1 — cloud as a lane, local as the referee. (M. Behind a flag, as an experiment.)**
Conductor spawns `claude --cloud` for work that needs no conductor tools and produces no verdict — a
Tier-B fix lane, a docs sweep, a research brief, a second-opinion review. It records the session id and
URL, pushes them to the chat, and consumes the **branch** the session pushes. Every gate still re-runs
locally; the referee never moves. One rule makes it honest: **the cost of a cloud lane is `unknown`, and
it must be reported as unknown, never as zero.** A run that quietly prices a checkpoint at $0 because it
could not see the meter is exactly the class of lie this repo built KS4 to catch.

**CL-3 — move conductor, not the sessions. (Not code. An owner decision.)**
The thing actually wanted is "my laptop should not be the run." Putting the *engine* on an always-on
host answers that completely and gives up nothing: stream, gates, worktrees, money truth, holdout proof
and control plane all keep working, and Chapar already makes it operable from a phone. Costs: an
interactive claude.ai login on that host, one machine's quota is still one machine's quota, and a
Windows-shaped repo needs a Windows host.

**CL-4 — Managed Agents. (L. Named, priced, deferred.)**
The API's Managed Agents surface hosts the loop *and* a per-session sandbox, and it gives back exactly
what `--cloud` takes away: a server-side event stream, hard **dollar-denominated session budgets**,
MCP, and scheduled deployments. It is also API-billed rather than covered by Max — this era's $324 of
Max-covered work would have been metered at Opus 5 rates — and it is not the Claude Code harness, so
CLAUDE.md, skills, hooks and the prompt batteries would all need re-homing. That is an era, not a
checkpoint. Worth revisiting only if elasticity starts to matter more than per-run cost.

### 2.4 The downsides, in one list

Because they were asked for directly:

1. No per-turn telemetry, so **per-checkpoint money and token truth dies** for cloud work.
2. No stall watchdog, no rollover, no circuit breaker — the mechanisms that stop a run burning budget
   in a loop do not apply.
3. No control plane reach, so **a cloud session cannot claim a checkpoint** or file anything.
4. Clone-from-remote, so uncommitted work is invisible and a dirty tree fails silently.
5. Same quota: it buys **no extra capacity**. Parallel cloud sessions drain the Max pool faster,
   including the pool the local run needs.
6. The holdout absence proof cannot be produced for a machine we do not control.
7. Research preview: the surface can change under a plan that depends on it, and org policy can
   disable it.
8. Sharing defaults on Max are Private-or-Public, with repo-access verification off by default.
9. One-way handoff: a local session can never be pushed up, and the only bridge back is `--teleport`,
   which needs a clean tree and a pushed branch.

**Verdict: do not move the session loop to the cloud this era.** Take CL-2, try CL-1 behind a flag with
the unknown-cost rule enforced, and treat CL-3 as the real answer to the real complaint.

---

## Part 3 — Feedback, logs and errors, going out

Yesterday's document measured this half and its findings stand; the courier changes only *where they
land*. Three things fold in cleanly:

- **Channel health is loud** (yesterday's #1) becomes a **prerequisite** of the courier rather than a
  peer of it — §1.4 B. The edge run's entire GitHub record was lost to two log lines nobody read; the
  courier would add a second channel able to fail the same way.
- **Owner queue to Telegram on change** (yesterday's #2) is the courier's outbound half, and it is what
  makes the daemon worth its own process: the queue is regenerated at every boundary and today reaches
  nobody, and the moment the bot outlives the run it can be pushed when it changes rather than when
  someone looks.
- **Bugs and followups as a long-lived issue class** (yesterday's #4) is the same shape as §1.7's note
  tier, from the other direction: the inbox is what comes *in* and survives the run; the bug/followup
  issue class is what goes *out* and survives the run. Building them in one era means one lifecycle
  rule, written once.

The inbound half of "collect logs and errors" is free once §1.9's checkpoint 1 lands: a forwarded stack
trace, a screenshot of a broken screen, a log file — all of them are just message kinds landing in the
per-project inbox with the note that explains them.

---

## Part 4 — The era, ranked

Proposed name: **Divan** (دیوان) — the chancellery of the Persian administration: the bureau that
*received* petitions, *recorded* them, and *routed* them to the office that owed an answer. It is the
same three verbs, and it sits naturally beside Sarban, Karvan, Karvansara, Chapar and Payesh.

| # | Move | From | Size | Why here |
|---|---|---|---|---|
| 1 | Channel health is loud | 08-22 #1 | S | Prerequisite for everything below; the cheapest fix in either document |
| 2 | Owner queue to chat on change, one tap per clearing command | 08-22 #2 | S | Kills "I lose track" without building a dashboard |
| 3 | Inbound message kinds + per-project inbox + prompt battery | §1.9 1–2 | M | The ask, and the half that works even with no daemon |
| 4 | Local transcription with confidence retained | §1.9 3 | S | Already on this machine; nothing leaves it |
| 5 | Routing by reply / sticky project | §1.9 4 | S | Zero-typing capture is what makes it get used |
| 6 | `/cloud <task>` admin verb | CL-2 | S | Work starts while the laptop is shut, at no risk to the run loop |
| 7 | The courier daemon owns the token | §1.9 5 | L | One bot, always awake, outbound survives the run |
| 8 | Note to followup to Tier-B lane promotion | §1.9 6 | M | Feedback becomes work through machinery that already exists |
| 9 | Bugs + followups as a long-lived issue class | 08-22 #4 | M | Found bugs get out; the board stops being a graveyard |
| 10 | `gh auth refresh -s project`, then finish KS9.3 | 08-22 #3 | S | One command unblocks the columns that were asked for |
| 11 | Cloud lane behind a flag, cost reported as unknown | CL-1 | M | The experiment, with the honesty rule pinned by a test |
| 12 | Board snapshot as one self-contained HTML file | 08-22 #5 | M | The page that was wanted, with no inbound port |
| 13 | SARIF to code scanning for file/line bugs | 08-22 #6 | M | Free on this public repo; permanent, filterable, mobile-visible |
| 14 | `conductor verify` — the Reward-Hacking Gap report | 08-22 #7 | L | The strategic one. It may deserve its own era rather than this one's tail |

**Rough cost, derived not guessed:** the edge era measured **$324.01 across 24 checkpoints, about
$13.5 per checkpoint** on `claude-opus-5` with a $420 cap. Fourteen checkpoints of comparable shape is
**roughly $190**, which argues for a cap around **$280**. Items 12–14 are the ones to cut first if the
era needs to be smaller; 1–5 are the ones that answer what was actually asked.

---

## Part 5 — What not to build

Inherited from 08-22 and still correct, plus two new ones:

- **A second messenger adapter.** A fake channel proves the seam for free.
- **A remote-control TUI, or anything that steers a session from a phone.** Claude Code Remote Control
  and Agent HQ mission control own this, bundled, and are better.
- **A reverse proxy or tunnel to the control plane.** ADR-0005 holds; publish instead of serve.
- **Two-way GitHub sync.** Already refused three times.
- **NEW — the session loop in the cloud.** §2.2. Not a deferral: a design refusal, for as long as
  `--cloud` has no stream and no route to the control plane.
- **NEW — a cloud-session cost estimate.** If the meter is not readable, the number is `unknown`.
  Do not model it, do not infer it from token counts, do not print a zero.

---

## Open questions — the ones I could not decide for you

1. **One bot or two?** §1.4 B (courier owns the token) versus C (a separate feedback bot). B is the
   better end state; C ships sooner and cannot break a live run. This is the era's biggest fork.
2. **May a note ever auto-inject?** My recommendation is a hard no (§1.7). If you want a fast path,
   the safe version is a button on the bot's acknowledgement, not a default.
3. **Which projects does the courier serve** — everything in the `StateHome` catalogue, or an explicit
   allowlist? Allowlist is the safer default; the catalogue is the convenient one.
4. **Is `/cloud` owner-only, or may the engine spawn cloud lanes?** CL-2 versus CL-1. They can ship in
   that order, and CL-1 can be dropped without losing CL-2.
5. **Retention.** Voice notes and screenshots on disk — kept forever, gitignored, or pruned after N
   days? My default: transcripts committed with the inbox, media gitignored and kept on disk, pruned
   on an explicit verb only.
6. **Still owed from KS12.3, and not this era's to take:** the version number for the CHANGELOG rename,
   and whether the two Karvansara runs join the published payesh corpus.

---

## Sources

Platform facts in Part 2:

- Claude Code, *Use Claude Code on the web* — https://code.claude.com/docs/en/claude-code-on-the-web
- Claude Code, *CLI reference* — https://code.claude.com/docs/en/cli-reference
- Claude Code, *Run Claude Code programmatically (headless)* — https://code.claude.com/docs/en/headless
- Claude Code, *Cloud environments* — https://code.claude.com/docs/en/cloud-environments
- Claude Code, *Remote Control* — https://code.claude.com/docs/en/remote-control

Telegram limits in Part 1:

- Telegram, *Bot API* — https://core.telegram.org/bots/api (`getFile`: bots download up to 20 MB)
- tdlib, *telegram-bot-api* — https://github.com/tdlib/telegram-bot-api (a local server removes the
  download limit)

Market and observability context is sourced in `docs/dev/OBSERVABILITY-AND-MARKET-2026-08-22.md`.

---

## Part 6 — Gap-fill: what the findings did not say (amendment, 2026-08-25)

*A read-through pass before plan authoring. Each item is measured here or sourced; together they
amend Parts 1–4, and the checkpoint sketch and era table should be read with them folded in.*

### 6.1 The inbox must not be committed — and the repo already decided this

**Measured:** `.conductor/.gitignore` is deny-by-default (`*`) with an explicit allowlist —
`!REPORT.md`, `!followups.md`, `!handovers/`. Open question 5's proposed default ("transcripts
committed with the inbox") would require adding `!inbox/`, and this repo is **public**: the owner's
voice-note transcripts would ship to the world on the next push. The privacy-correct default is the
repo's existing default: no allowlist entry. The inbox lives on disk, survives the run, and never
enters git. A plan that wants committed transcripts opts in per-plan, and the docs say what that
means on a public repo. This also answers retention: transcripts and media both stay local, pruned
only by an explicit `conductor inbox prune` verb — resolving open question 5.

### 6.2 The poll offset dies with the process

**Measured:** `TelegramService.cs:96` — `private int _offset;` — advanced in memory at `:351`.
Today that is fine: the offset lives exactly as long as the run that owns the poll loop. A courier
restart, however, replays every update Telegram still holds and files every note twice. The courier
needs a durable offset in the state home plus dedup by `update_id` in the inbox index. Falsifiable:
kill the courier between receive and acknowledge, restart it, and the note files exactly once.

### 6.3 The courier answers "no run live," not "machine off"

**Sourced:** Telegram keeps undelivered updates for **24 hours** (Bot API, `getUpdates`). A voice
note sent Friday night to a laptop that sleeps until Monday is gone — not dropped by conductor;
never handed over by Telegram. The courier narrows the gap from "no run live" to "machine on"; it
cannot do better from this machine. That limit belongs in the courier's docs, stated plainly — and
it is the honest long-term argument for CL-3, the always-on host.

### 6.4 The courier's lifecycle is unstated, and it collides with the install discipline

Nobody starts the daemon in §1.4-B. Who runs it at boot, restarts it on crash, stops it for a
reinstall? Proposal: `conductor courier install` registers a per-user Scheduled Task (logon
trigger, restart-on-failure) — no admin rights, no Windows Service ceremony. And the install rule
"reinstall only when no run is live" gains a clause: a running courier holds the published exe
open, so `tools/install.ps1` must stop and restart it, or the reinstall fails on a file lock —
worse, a courier that is not restarted keeps running yesterday's engine indefinitely, precisely
because it is designed to outlive everything else. Version skew is therefore real: the courier
states its version at the loopback hello, and a run that speaks a newer protocol refuses the stale
courier **by name**, naming `conductor courier restart` as the fix.

### 6.5 The run–courier loopback needs the control plane's posture, spelled out

ADR-0005's argument does not stop applying because the port is new. A loopback listener with no
auth means any local process can push to the owner's chat as the run, or read the inbound notes.
The courier listens loopback-only with a per-install shared secret kept in the state home,
file-permission-protected — the same shape the control plane already carries — and per trap-3
discipline it gets its own named port, never a scan.

### 6.6 Two writers, one inbox

The courier writes a note while a session reads the battery. Atomic write (temp file + rename),
append-only index — and, the bigger miss, a **read cursor**. §1.7 says where a note lands and never
says when it stops being surfaced: without a "seen" mark, the battery grows without bound for any
long-lived project. Notes gain a seen-by-session mark at the boundary; the battery carries unseen
notes verbatim (capped) plus one line counting the rest. Nothing is deleted.

### 6.7 Admin-only filing shuts out the era's best consumer

§1.8 restricts filing to admin chats. The BookToCourse run's entire feedback loop is a stakeholder
group chat — voice notes from the client are exactly the payload this era is built for, and the
client is an observer. Proposal, for the owner to accept or refuse: a third profile, **`reporter`**
— may file tier-1 notes only, no promotion, no steering, no inject — enforced in the same
exhaustive command-by-profile matrix KS11 already proved. Observer stays read-only; admin keeps
the verbs.

### 6.8 `/cloud` needs the same preflight §2.2 demands of lanes

The clone-from-remote trap (§2.2 item 5) binds the owner verb too: `/cloud` fired against a dirty
tree or an unpushed branch silently runs the agent on yesterday's code. The verb preflights —
clean tree, branch pushed and current on the remote — and refuses **by name, in the chat**, quoting
the exact git state that blocked it.

### 6.9 The token handover has a transition, and 409 is waiting inside it

The day the courier owns `CONDUCTOR_TELEGRAM_TOKEN`, any plan whose telegram block still polls
in-run fights it for updates (§1.3). Precedence rule, stated and tested: when a courier is
configured on the machine, in-run polling refuses to start and says the courier's name; the run
pushes through the `CourierChannel` or not at all. Old-shape plans on a courier-less machine keep
today's behaviour byte-identically — the KS11.1 golden-replay standard, reused.

### 6.10 A note for a project that is not there

The courier routes on the StateHome catalogue (§1.5). A catalogue entry whose repo has moved or
vanished: refuse by name to the sender, and park the note in a machine-level dead-letter directory
under the state home rather than dropping it — a bot that loses a message is §1.2 gap 2 wearing
yet another hat.

### What this changes

None of it reorders the Part 4 table. It changes item 7's true size — the courier is L *with 6.2,
6.4 and 6.5 folded in*, not L-optimistic — it resolves open questions 2 and 5, and it adds one
candidate feature (6.7's reporter profile) to the owner's keep/cut list.
