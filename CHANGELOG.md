# Changelog

All notable changes to Conductor are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**This file is not decoration — it is load-bearing.** The version a binary reports is derived from
the `v*` git tags by MinVer (`src/BuildStamp.targets`, `MinVerTagPrefix` = `v`), and
`.github/workflows/release.yml` refuses to publish a tag whose section is missing from this file,
using the section as the release body (`tools/changelog-section.sh`). So: add to `[Unreleased]` as
you work, and rename it to the version when you tag.

A section that quotes a run's own score is quoting a **measurement with a date on it**, not a
constant: every further session moves it. Re-run `conductor budget` and `conductor money` when you
rename the section — the section is what the world reads on the releases page.

Between releases, `conductor version` answers with a tag-height prerelease such as
`0.1.1-alpha.0.54+1c2330f5a47e` — patch bumped, `alpha.0.<commits since the tag>`, plus the commit
it was built from. It orders above `0.1.0` and below `0.1.1`, and it is unique per commit.

## [Unreleased]

_Nothing yet — entries for the next era go here, and this heading is renamed to the version when it
is tagged._

## [0.5.0] - 2026-08-26

**Two eras — gates that cannot be gamed, and the chancellery.** 0.4.1 opened the door; this release
is about what happens once someone walks through it and finds they need to *trust* what the run tells
them, and to be *reachable* while it works. It carries two eras built back to back and tagged together
because they are one story. **Karvansara edge** went after the sentence "the gates are green", which
until now meant only *this command exited 0*: a `holdout` gate runs at the phase gate with its name
redacted everywhere a session can see, a `regression` gate fails when a check that used to pass has
quietly stopped existing, a `mutation` gate fails a suite that runs and asserts nothing — and a second
model may now review the work, but no code path lets its score flip a gate verdict. **Divan** — the
chancellery — gave the run a mouth and an ear. One bot that outlives the run owns the Telegram token
and files what you say into the project it is about, from an explicit allowlist; a filed note arrives
as context a session actually reads rather than as a message it missed; and what the run learned
finally leaves the machine as durable GitHub issues, a Projects v2 board, code-scanning alerts and a
single page you can open on a phone. Built the way the last four were: conductor driving itself
against this repository, unattended, every checkpoint confirmed by an independent gate battery rather
than by the agent that claimed it.

Their own score, produced by the tools this project ships (`conductor money` and `conductor budget`,
measured at the tag): **karvansara-edge — 23 checkpoints, 414.3M tokens, $324.01; 18.0M and $14.09
each**; **Divan — 23 checkpoints, 417.0M tokens, $320.38; 18.1M and $13.93 each**; both at 98.4–98.5%
cache reads. Divan's measured window took one rollover in eighteen costed sessions, and its
cooperative wrap-up rail converted six of the seven sessions it nudged. The per-checkpoint estimate
has now come in **low two eras running** — edge +4.0% on tokens, Divan +7.7% — which is why
`docs/dev/TOKEN-BUDGET-TUNING.md` §13 tells the next plan to compile against **19M / $14.50 per
checkpoint** and **42M / 0.9**, rather than round the last measurement down.

### Added

- **The courier — one bot, always awake.** Until now the Telegram poll loop lived exactly as long as
  the run that owned it, so a note sent to a machine with nothing running reached nobody. `conductor
  courier install` registers a per-user scheduled task (logon trigger, restart-on-failure, no admin
  rights) for a machine-level daemon that owns the bot token, polls whether or not a run is live, and
  files each note into the project it is about — from an **explicit allowlist**, never the whole
  state catalogue. `status`, `run`, `restart`, `stop`, `uninstall`, `allow`, `deny`, `chat`,
  `unchat` complete the lifecycle. The poll offset is durable, so a courier killed between receiving
  and acknowledging files the note exactly once on restart instead of replaying everything Telegram
  still holds. Its handover port is loopback-only on a fixed named port with a per-install shared
  secret, and it accepts notes — never run state. A run that speaks a newer protocol refuses a stale
  courier **by name**, naming `conductor courier restart`. The honest limit is documented rather than
  hidden: Telegram discards an undelivered update after 24 hours, so the courier turns "no run is
  live" into "the machine is on" and cannot do better from one machine. See ADR-0008.
- **The inbox — what the owner said, as context a session reads.** A filed note lands in that
  project's `.conductor/inbox/` and reaches the next session as the **last** block of its prompt:
  the engine's own knowledge first, the human's words last and framed. Only what actually fit is
  marked seen — the remainder is counted in one line and reaches the session after that, so a
  long-lived project's battery cannot grow without bound and nothing is skipped. `conductor inbox`
  reads it (`list`, `show`, `add`, `transcribe`, `parked`, `prune`); `prune` is the only thing in
  conductor that deletes a note, and it refuses to run without being told which. **Nothing in the
  inbox moves a run** — promotion into a followup or a task is a deliberate act, one button or one
  verb. Transcripts are never committed: `.conductor/.gitignore` is deny-by-default and the inbox
  gets no allowlist entry, which on a public repo is the whole difference.
- **Voice notes become words, locally.** `courier.transcribe.command` (or
  `CONDUCTOR_TRANSCRIBE_COMMAND` for a machine with no plan in front of it) shells out to a command
  you choose — `tools/transcribe/whisper-json.py` is a local faster-whisper wrapper this repo ships,
  so audio never leaves the machine. Per-segment confidence below `confidenceFloor` (default 0.45)
  is marked in the stored note. With no command configured the note still files and the audio is
  still kept; the reply says it was not transcribed rather than dropping it silently.
- **A dead-letter box.** A note for a project that has moved, been deleted or was never allowed is
  parked under the state home and the sender is told by name. `conductor inbox parked` lists them.
  Nothing is discarded on a routing miss.
- **`/cloud` — a session on Anthropic's infrastructure, owner-only.** Following up on an existing
  cloud session is headless, so conductor drives it and brings the answer back to the chat; creating
  one is interactive-only on today's CLI and is refused with the platform's own words plus the exact
  command to type, rather than by faking a TTY. Creating preflights git first — a cloud agent clones
  from the **remote** — with six verdicts (nothing to clone, detached head, dirty tree with the
  count and the files, no upstream, branch missing on the remote, remote tip differing from local
  `HEAD`), each quoting the state that produced it, in the chat. Cost is always reported as a word
  and never as a number, because there is no per-turn telemetry out there.
- **An opt-in cloud review lane.** The `cloud` plan block runs a per-session review out there
  alongside the local analysis lanes. **Off by default, with deliberately no environment override**
  to switch it on by accident, bounded by `timeoutMinutes` (default 30, 1–240). The referee never
  moves: every gate still runs on your machine and nothing the cloud says confirms a checkpoint.
- **Tracked bugs and followups become durable GitHub issues.** They get their own labels and markers,
  are created only while open, and are closed by the ledger with a comment rather than by the run
  ending. The daily digest gains the ledger line.
- **Projects v2 columns.** `conductor github sync --project <n>` drives a Projects v2 board's columns
  from the same fold that writes the issues. It needs a token carrying the `project` scope and says
  so by name when it does not have one — `gh auth refresh -s project` is the one-command fix.
- **`conductor github sarif` — bugs as code-scanning alerts.** Every open tracked bug that names a
  file and a line becomes one SARIF run uploaded to GitHub code scanning, so a defect the run already
  knows about appears where a reviewer is looking. Free on a public repository; a private one needs
  GitHub Advanced Security, and without it the upload is refused **by name**, quoting the repository
  it read, rather than failing blind. `--out` writes the SARIF and uploads nothing.
- **The board as one page for a phone.** `board.html` is a self-contained snapshot rendered from the
  control plane's own contracts at each boundary and pushed as a Telegram document. It states its own
  staleness at the top. Nothing inbound.
- **Chat profiles — `admin` and `observer`.** `telegram.chats` gives each chat a profile, so a
  stakeholder can be put in a chat the bot serves without also being handed `/inject` and the
  control verbs. A plan carrying only `allowedChatIds` behaves exactly as before; an unknown profile
  string fails plan load by name rather than defaulting to admin. The observer surface is a closed
  list enforced at one gate, and every verb is checked against both profiles by an exhaustive matrix
  test rather than a sample.
- **Onboarding.** Every configured chat is told what this run is (plan, stage map, budget ceiling),
  what will arrive and when, and exactly what it may ask — before the run's first word, again after
  a plan reload adds a chat mid-run, and on `/start`, which until now answered one static sentence.
  The message is composed per profile, and the "what you can ask" list is derived from the same
  catalogue the gate enforces, so the promise cannot drift from the permission.
- **Gates that cannot be gamed — three new gate classes.** A gate that exits 0 has said one thing:
  *this command succeeded*. Three failure modes hid inside that, and each is now a `class` or a
  `visibility` you declare on the gate in the plan, not a new kind of code.
  - `visibility: "holdout"` — the gate runs at the **phase gate only**, and its name is redacted
    everywhere a session can see: the progress line, the fix brief, the failure tail. A session
    cannot tune to a bar whose name it never learns.
  - `class: "regression"` — reads what still *passes* rather than what failed. A check that passed
    earlier in the run and no longer does fails the gate **even though the command exited 0**, so
    deleting a test to get green is a gate failure rather than a smaller test count.
  - `class: "mutation"` — reads a mutation report the gate produced and fails on a score shortfall:
    the suite that runs and asserts nothing. An unreadable report is reported as unreadable, never
    as a pass.
  All three say their verdict in the class's own words. "A gate failed" is wrong twice over for a
  classed failure — the gate exited 0, and what is broken is the checks rather than the code under
  them — and a fix session told the wrong thing goes looking for an assertion that does not exist.
- **A second model may review the work; it may not score it.** An optional review command runs after
  a session and its verdict joins the evidence set as an advisory row, beside the gates and the
  claims, reaching the fix prompt and the record. No code path lets a judge's score flip a gate
  verdict, and a test asserts that rather than a comment promising it.
- **`conductor mcp-observe` — a read-only MCP surface.** Serves this machine's run catalogue to any
  MCP client as *resources* (`conductor://history`, `conductor://runs/{run}/status`,
  `conductor://runs/{run}/money`) and **no tools at all**: `tools/list` is empty and `tools/call` is
  refused. Control operations are excluded by design rather than by a flag, and the store is opened
  `Mode=ReadOnly`, so SQLite refuses a write before any policy check would. The reasoning, including
  MCP's 2026 attack record, is in ADR-0007.
- **`conductor history export <run> --atif`.** A finished run leaves as an ATIF-v1.7 trajectory — the
  Harbor / Terminal-Bench interchange format — with `-o <FILE>` for one and `--all -o <DIR>` for the
  whole catalogue. Each session is one agent step; the gate battery, the checkpoints it confirmed
  and the commits it landed are that step's observation. Billed dollars only: conductor has no price
  table, so ATIF's own cost derivation is not applied.
- **`conductor worktree`.** What attempt worktrees are on disk, which run made each, and which are
  orphans from a run that died. `--reap` removes the orphans and never touches a live run's or one
  you made yourself. The engine runs the same sweep at startup.
- **`conductor otel`.** A run's spans in OpenTelemetry's own vocabulary, mirroring the `gen_ai.*`
  names, rendered from the event log — so a run can be read in a collector rather than only in the
  Face.
- **`conductor init` writes `AGENTS.md`.** Plus a `CLAUDE.md` that imports it, clobbering neither if
  either already exists — one file of guidance, honoured by every agent that reads either name.

### Changed

- **The bot token moves to the courier, where one is installed.** Telegram allows exactly one
  `getUpdates` consumer per token, so a run polling alongside a courier would fight it for updates.
  Where a courier is configured, in-run polling refuses to start and names the courier; the run
  pushes through it instead. **A machine with no courier behaves byte-identically to before**, pinned
  by golden replay rather than asserted.
- **`tools/install.ps1` stops the courier and puts it back.** A running courier holds the published
  exe open, so the publish would fail on a file lock — and a courier left down is a bot that stops
  answering, while a courier not restarted keeps running the old engine indefinitely, precisely
  because it is built to outlive everything else. The installer now owns both halves and warns
  loudly if the restart did not take.
- **The prompt batteries are bounded, and say so.** `batteries.ledgerMaxEntries` and
  `batteries.maxBytes` cap what the knowledge ledger contributes to a prompt. An unbounded ledger was
  measured starving the open-bugs battery out of the prompt entirely, which is a run whose sessions
  cannot see the defects they are meant to avoid.
- **The messenger seam.** Message composition, chat profiles and the command surface are now defined
  without knowing which messenger will carry them (`Conductor.Core.Integrations.Messaging`:
  `IMessageChannel`, `MessageComposer`, `CommandRouter`, `RemoteSurface`); `TelegramService` is the
  transport adapter behind that seam. Nothing a chat receives changed — fifteen goldens generated by
  the previous engine pass byte-identical through the new one. Internally, `ITelegramService` is now
  `IRunNotifier` and `RunContext.Telegram` is `RunContext.Messenger`, so the run loop no longer names
  a messenger it does not depend on.
- **Every push now reads headline / proof / telemetry.** What landed, then what proves it (the gate
  verdict and the evidence artifact, together on one line), then the numbers — progress, money
  against the cap and tokens — in monospace. A session-end push reads standalone: previously it was
  a status line plus clipped result text, with the artifact buried under the gaps and the cost below
  the prose where a phone cuts it off. Owner `notify/` templates written against the old fact names
  (`progress`, `gates`, `cost`) still render; the new facts are `proof` and `telemetry`.
- **A confirmation keyboard is only sent to admin chats.** An observer still gets the news that the
  run is asking for a decision — it is the text half of the same push — but is not offered a button
  it would be refused for pressing.
- **Telegram readiness counts every configured chat.** `doctor`, `/telegram/status` and the reload
  message counted `allowedChatIds` alone, so a plan configuring its chats the new way reported
  "push-only to nobody" while delivering perfectly.
- **Hooks are the record of what a session did, not the transcript.** Tool events arrive by hook and
  the transcript is the fallback, so the digest counts the call the agent *made* rather than the one
  it printed. A hook-less agent still works, on the fallback path.
- **Per-turn usage carries the cache split.** The cache-read half used to vanish from the per-turn
  view even though it is ~98% of what a session costs; it is now parsed from the stream and
  reconciles with the context curve.
- **A fix or audit session forks the session it is fixing** where the agent CLI supports it, instead
  of resuming cold. Measured, because "forking is cheaper" deserved a number rather than a belief:
  the carried context lands as a cache **read** instead of a fresh input, at 0.15% more tokens and
  fractionally less money than a resume — so the saving is not the fork, it is not re-discovering
  the context at all.
- **Gate output no longer floods the prompt.** A failure's full text goes to an evidence file and
  the prompt carries a bounded tail plus the path. Two new prompt batteries — a repo map, and a
  definition-of-done recap — cost less than what the spill saves.
- **The analyzer ruleset is curated, and the debt only ratchets down.** Roughly twenty-five
  design-shaped rules are errors, everything else is explicitly off, and each adoption carries a
  one-line reason. A separate bar counts analyzer *debt* in every spelling it takes — pragmas,
  `SuppressMessage`, `NoWarn`, severity downgrades — against the minimum this branch's own history
  has achieved, so no single commit can move its own bar. Complexity budgets (CA1502/1505/1506) are
  enforced per project on the same terms.
- **The verdict left the loop.** The evidence-to-verdict taxonomy is now a total, deterministic
  function: same evidence in, same decision out, on any machine, with no run in progress. Every
  branch of it used to be reachable only by standing up a run context, a store, a git repository and
  an agent process, which is why it was the least-tested part of the engine.
- **`docs/cli.md` now names every long option a shipped verb declares**, enforced by a test that
  reflects the options off the commands rather than trusting the page. Forty-one were missing when
  it was first run, including `task --evidence` and `task --blocked-until`.

### Fixed

- **A prompt battery could be truncated to nothing.** `BatteryGroup.Render` clipped the
  *concatenation* rather than each block, so whichever battery happened to sort last vanished
  silently; and the open-bugs battery dropped every bug past the twelfth with no line saying it had.
  Both are the same failure — a session that cannot see what it was given, and no signal that
  anything was withheld.
- **Budget counters restarted at zero on every engine process start**, so a resumed run measured
  itself against a ceiling it had already spent.
- **A stage-boundary squash could silently rewind the branch.**
- **A rate-limit storm could burn a stage's whole attempt budget in minutes**, turning a transient
  429 into an exhausted stage.
- **Two engines on one bot token looped on 409 conflicts.** Closed by the courier owning the token —
  one consumer per token, by construction.
- **A composed prompt over roughly 8,191 characters silently stopped a `cmd.exe` agent**, and nothing
  warned when a plan's packs pushed it over the argv ceiling; `doctor`'s argv lint under-measured the
  real spawn because it did not count the batteries.
- **Telegram diagnostics on an empty chat list.** The startup line counted the wrong collection, and
  `POST /telegram/test` indexed the first configured chat without checking there was one.
- **`report push failed:` logged with an empty reason**, so a repeated failure said nothing about
  itself.
- **A brand-new `run.db` logged a foreign-key constraint failure on first write.**
- **An unattended run under a restricted permission posture could silently lose its own claim path.**
  Probed against the shipped agent CLI rather than assumed: an allowlist profile cannot replace
  `--dangerously-skip-permissions` for this workload, and the finding is filed with the exact refusal
  rather than worked around with a guessed flag. Refusals are now telemetered, so the next attempt
  starts from evidence.
- **The attempt diff no longer counts the engine's own commits** — tracker regeneration and report
  writes — as the session's work.
- **Orphaned attempt worktrees are swept at startup**, so a run that died does not leave a branch
  that the next `git` operation trips over.

## [0.4.1] - 2026-08-15

**The Karvansara era — the open door.** 0.4.0 made a run accountable; this era makes it *approachable*
without giving any of that up. Typing `conductor` used to answer with a wall of help text, which is the
one moment a tool has to say what it can do for you. It now opens a **hub**: the runs live on this
machine, the runs it remembers, the plans it can see from where you are standing, and something to do
about any of them. Everything behind that door got the same treatment — a finished run opens in the
dashboard instead of being a database, a plan can be authored from an idea instead of from JSON, the
launch drill is one verb instead of six, and what a run cost is a question you can ask about the whole
machine rather than one repo at a time. Built the way the last three were: conductor driving itself
against this repository, unattended, every checkpoint confirmed by an independent gate battery rather
than by the agent that claimed it.

Its own budget, measured at the close by the tool this project ships (`conductor budget`, against this
run's own ledger): **a 32M-token session ceiling with the wrap-up nudge at 0.85** — 27.2M, clearing the
25.6M largest session that ever closed a checkpoint, with 4.8M of headroom against a measured 2.28M
wrap-up cost, and zero rollovers. The per-checkpoint figure this run reports is *not* comparable with
0.4.0's: most of its checkpoints were confirmed in a single wave-lane session, which is a scheduling
artefact rather than a cost improvement.

### Added

- **`conductor` with no arguments is the hub, not the help.** This machine's state home, the runs
  answering on the fleet ports, the runs the catalogue remembers, the plans discoverable from here —
  and four things to do about any of it: **attach** a Face to a run already going, **start** one from
  a plan here, **plan new**, or **history**. Starting from the hub is the whole launch drill in one
  flow: the plan's itinerary previewed read-only, a yes/no, then a *detached* engine you can close
  your terminal on, with the Face attached to it. Zero plans here is a normal answer and so is eleven
  — the front door of a CLI never interrogates the person who just typed its name. Redirected output
  (`conductor | cat`) prints the same board and exits 0, so a script cannot hang on a keystroke.
  `conductor --help` still lists exactly what it listed before, and an unknown verb is still an error.
- **The archive — a finished run opens in the dashboard.** `conductor face --archive <run>` serves any
  past run's database through a read-only control plane: sessions, money, timeline, report, every live
  tab, with no engine process and no write token, so every write affordance hides itself and every POST
  is refused by name. One run picker now covers the machine — the live runs and, under them, the ones
  the catalogue remembers — and `:switch` moves a Face between runs without restarting it.
- **`conductor preflight` — the launch drill as one verb, one verdict, one exit code.** Six legs:
  `doctor`, journey resolution, the next session's prompt actually composed and measured, the running
  engine versus the latest release, a stale-build check, and the tracker handoff block (an escalation
  request left sitting in it parks session one before anything spawns). It decides from the same inputs
  the run loop schedules on, prepared the same way, and it writes nothing under the plan's `.conductor/`.
- **`conductor plan new` — authoring from nothing.** One command from an empty repo to a plan, a tracker
  and the editable templates, **doctor-clean by construction**: the agent block names a CLI this machine
  actually has, and no scaffolded template spells the escalation token into existence. `--from-idea`
  takes free prose, a PRD path, or an existing tracker. The JSON never has to be opened.
- **Import bridges — bring the board you already have.** A spec-kit `tasks.md`, a Task-Master
  `tasks.json` and a plain markdown checklist each convert to a plan deterministically, detected by
  content rather than filename, with **no model call**; only a shape none of them recognises falls
  through to the advisor. `conductor demo --from <file>` drives *your* board against the fake agent, so
  "will it drive mine?" is answered before you point it at a real one.
- **`conductor spend` — what this whole machine cost.** Today, this week, this month, across every
  catalogued store, with no repo and no plan argument. Billed rows only; each real run counted once even
  when two stores hold it; sessions with no start time reported as `undated` rather than dropped. It
  windows at session granularity, so a run straddling a boundary contributes only the sessions inside it.
- **`conductor github sync --backfill <run>` — the board, on GitHub.** One issue per checkpoint (status
  and source labels, a `confirmed` label only when the engine confirmed the claim, the stage as a
  milestone), plus a run issue carrying plan, repo, branch and engine with one comment per finished
  session — and, under `github.enabled`, a live mirror that reconciles as the run goes and resumes from
  a cursor after a network outage. **One way out, off by default, nothing ever read back**: drag a card
  on GitHub and the run does not notice, which is correct rather than a gap. Identity is a marker in the
  issue body, so re-running a backfill mints zero duplicates and a reworded checkpoint updates its issue.
  The Projects v2 half **refuses and says why** — it needs a `project` scope conductor will not grant
  itself — rather than falling silent, which from outside would look exactly like a board being mirrored.
- **`conductor run close` and `run adopt` — a stale run record has a supported fix.** A run whose engine
  was killed, rebooted or reaped with its shell never got to close itself. `run close` writes a terminal
  status and stamps when it *actually* stopped, taken from its last recorded activity, with the reason
  journalled into the run's event spine; `run adopt` annotates a record you mean to keep. Hand-edited SQL
  was the previous answer and is no longer needed.
- **`conductor catalogue` — and `catalogue repair` for stores that hold a run twice.** `repair` says what
  it would collapse and writes nothing; `--apply` collapses it after backing up every store it touches,
  never writes a store a live engine is using, and identifies a run by its run id rather than by which
  store it happens to sit in.
- **`conductor watches` — what is armed on this machine.** Every live run beside the supervisor block
  watching it, how much of its hourly fuse is burnt, where a remote wake travels, and the park-push cap
  in force. Rows nothing would wake anybody for are called out. Read-only: no token, no POST.
- **The reader, and scrolling everywhere.** Every long surface in the dashboard owns a scrolling pane,
  and `enter` on any clipped cell or row opens a full-screen overlay with soft wrap, pager keys, a
  percent readout and themed markdown — so a 2000-line report and a 300-character kanban note are both
  readable in place instead of ending in an ellipsis.
- **`conductor doctor` gains plan-semantics lints.** Beyond "is the environment ready": does every gate
  command resolve to something that exists, do the tracker's checkpoint ids match the plan's, does the
  hook survive a dry run, has the plan drifted from the run it is driving, will the composed prompt fit
  in an argv, does any template carry an unresolved brace, and is the escalation token sitting in a
  handoff block where it will park session one.

### Changed

- **Licence is MIT.** Conductor is now plain MIT — free for any use, commercial included. The
  PolyForm Noncommercial 1.0.0 text that briefly sat in `LICENSE` was a mistake and is gone; no
  version of this software is under a noncommercial grant.
- **The one-line description says what Conductor is.** It is an engineering tool that turns a plan
  into verified, committed work; running unattended is a consequence of the verification, not the
  pitch. README badge, tagline and licence section updated to match.
- **`approve` on a budget park raises the ceiling, and says by how much.** It used to reset the counter,
  which forgave the spend silently. It now raises the run's ceiling by `--amount <usd>` / `--tokens <n>`,
  or by one more of the plan's own cap when you give neither, and states the ceiling before and after.
  A raise that would leave either half of the ceiling still at or under the spend is refused whole,
  naming the number to type; an amount on a non-budget park is refused rather than ignored.
- **A run's status is derived from its event spine, not read off a column.** Stage status folds from the
  events, so what `status`, `history`, the JSON and the dashboard say about an archived run is the same
  answer computed the same way — including for runs recorded before this release.
- **The plan editor stops destroying the file it edits.** `plan set`, `plan add-stage` and `plan import`
  splice into the raw JSON: the `//` comment header survives, key order and formatting survive, and
  nothing changes but the values you edited. Adding a stage no longer silently rewrites the progress kind
  or a gate timeout on its way past.
- **The plan schema is honest about itself.** Eight keys that were settable and undocumented are
  documented, a key that was read by nothing is gone, and `doctor` warns when a plan sets something inert
  instead of leaving you to wonder why it had no effect.
- **Every model process the engine spawns is billed.** Lanes, the advisor and the supervisor each write a
  cost row now, so the caps see the spend they are supposed to cap and `money` prices what actually ran.
- **A prescription that contradicts your plan says so at the boundary.** When a plan reloads, a
  `limits` ceiling that disagrees with the floor measured from that repo's own sessions is logged where
  the decision is being made, rather than waiting for someone to run `budget` and notice.
- **`conductor status` in a directory that names no plan widens instead of failing.** It prints the
  machine's board — live runs, what the catalogue remembers, the plans found here — with the reason on
  stderr and exit 0. The "several plan files and nothing choosing between them" error is unreachable.

### Fixed

- **The working directory beats `CONDUCTOR_PLAN`.** A session inherits the environment of the run that
  spawned it, so a plan launched from inside another run used to drive *that* run's plan instead of its
  own. A directory naming exactly one plan now wins, and the override is never silent.
- **The catalogue stops minting duplicate runs.** Importing a legacy `run.db` keyed on the plan slug, so a
  run could be added again under a new name; it keys on the run id and consults its own import record
  first. Existing duplicates collapse with `catalogue repair --apply`, which backs up every store it
  touches.
- **A killed engine's run never lists as `running`.** Liveness is reconciled at render time — in
  `history`, in the fleet list and in the JSON that feeds downstream tooling — so a run whose engine
  vanished reads as orphaned rather than as work in progress.
- **A park notifies once.** A parked run could re-emit the same push in a loop; notifications are now
  rate-limited per incident with a cap you can set, and a dry run never notifies at all.
- **The gate battery no longer rebuilds the running engine.** A gate that published over the binary
  driving the run could pull the floor out from under it mid-session; gates build to a shadow path.

## [0.4.0] - 2026-08-05

**The Karvan core era** — the engine knows what it did and what it cost. 0.3.0 made a run visible;
this era makes it *accountable*. Every claim conductor makes about itself — what a session landed,
what it spent, where its state lives, what its own docs say — is now measured by the engine and
checkable against the ledger, because in the run that produced 0.3.0 several of those claims were
wrong and nothing caught them. Built the same way as the last two: conductor driving itself against
this repo, unattended, with every checkpoint confirmed by an independent gate battery rather than by
the agent that claimed it.

Its own score, produced by the tools this era shipped (`conductor budget` and `conductor money`, measured
at the tag): **24 checkpoints at 16.8M tokens and $13.24 each, zero rollovers in 30 costed sessions**
— against the previous era's 17.0M, $14.86 and 30%. The run cost $317.84 for 403.9M tokens, 98.3% of
them cache reads.

### Added

- **`conductor budget` — the token budget stops being folklore.** It reads a run's own ledger, splits
  it at the session where a ceiling took effect, and prints floor, wrap-up, cap, nudge-versus-floor,
  nudge-versus-median-closer and rollover rate for each window — then *prescribes* a `limits` block
  to paste. Run against this repo it found four wrong numbers in `docs/dev/TOKEN-BUDGET-TUNING.md`
  and one rule that was too weak; all five are corrected in place.
- **`conductor money` — what a project cost.** Per checkpoint, per stage and per month, with the
  cache-read share and the before-and-after windows that say what a cap bought.
- **Context size per turn.** A high-water and a mean per session, derived from the stream, so the
  thing everyone tunes by feel has a number. Live session tokens, the distance to the nudge, a burn
  rate and a projection sit beside live money in the Face and on the wire — honest when no cap is set.
- **`conductor history`, and state that outlives a repo.** State has a machine-level home with a
  catalogue keyed by repo and plan, an environment override, and a migration that *imports* existing
  `run.db` files rather than orphaning them. Past runs open read-only from the catalogue, and the
  Face's run picker offers them.
- **Run provenance.** Every run records the engine version, its commit, its dirty flag and a snapshot
  of the limits that governed it. A dirty build warns at launch.
- **Evidence as a first-class artifact.** Path, kind, checkpoint, session, sha, created-at — written
  as an event when an agent registers one or a watched directory gains a file, with non-text kinds
  first-class and a Face surface. The existing free-text evidence field still works.
- **A message-composition layer for Telegram.** Owner-editable per-event templates, repo and branch
  and stage title and checkpoint in every push, commits and PRs as links, money with headroom, photo
  and document sending so evidence arrives, a thread per run, severity mapped to notify-or-silent,
  and 4096-character chunking. ADR 0005 records the push-only remote posture.
- **`ARCHITECTURE.md`, and tests that keep it true.** A real map of the tree with a file-organisation
  convention, backed by architecture tests in the ordinary suite that fail the build when a boundary
  is crossed, each naming the offending type and the rule.
- **ADR 0006 — TUI conventions**, written after an actual read of glow, soft-serve, gh-dash and
  lazygit: pager keys, focus model, help, one scroll idiom, viewport versus list versus table.

### Changed

- **`Conductor.Core` is a library.** The domain, orchestration and store moved out of the CLI with no
  Spectre and no HTTP hosting left in them; `Conductor` is CLI plus hosting. The reference direction
  points one way and a test says so. The thirty-file DTO pile became per-feature endpoint contracts.
- **The session result has one format conductor owns** — short headline, at most three outcome
  bullets, artefacts, evidence paths, explicit gaps. Five consumers used to cut the same paragraph at
  five different lengths, mid-word; they now read fields. A legacy result degrades rather than throws.
- **The Face's tabs own themselves.** Each tab has its own model, state, update and view, so the root
  update is a dispatch instead of 826 lines and 80 cases; the mnemonic map and the help legend are
  derived from one source. `bubbles` v2 is a declared dependency and four panes scroll through a real
  viewport. One theme-aware markdown renderer serves everywhere markdown belongs, and it memoises —
  200 frames of unchanged prose invoke glamour once.
- **The front door reads.** `AGENTS.md` cut to current state with superseded handoffs archived and
  indexed, closed-era trackers out of the repo root, the divergent duplicate workgraph doc resolved
  to one file, and the docs indexes updated.

### Fixed

- **A rolled-over session records what it actually did.** `SessionRunner` used to set the outcome and
  return *before* the pass that populates commit count and closed checkpoints, so every rollover
  reported nothing landed — on this repo's own history, ten of eleven rollovers had in fact committed.
- **The soft break is re-stated until it is obeyed**, names the actual remaining budget, and states
  the wrap-up order (claim first, handoff second). The session record now says whether it was
  delivered, re-delivered and obeyed. In the 0.3.0 run the rail converted **zero of ten** kills.
- **The five Telegram defects that made the feed unreadable**: two identity blocks from two sources,
  a stage id with no title, the structured result cut mid-word, a rollover that reported nothing, and
  pushes with no progress line.
- **A spawned session sees the operator's own MCP servers.** The per-session config is now a merge
  with whatever the machine already has, not a replacement that named only `conductor-tasks`.
- **Three small untruths died as a class**: a thinking-token column that was zero on all 125 rows, a
  lessons file that was a diary and repeated one entry twice, and a `go.mod` that called a
  directly-imported package indirect while carrying two lipgloss majors.
- **Face scroll offsets no longer run away past the end of a document** — 389 keystrokes into the
  Report pane used to leave it blank. Four panes, one clamp, one idiom.
- **`conductor demo` no longer leaves anything on your machine.** It has always deleted its throwaway
  repo; since this era moved `run.db` to a machine-level store it had been leaving the database, and a
  permanent `conductor history` row pointing at the directory it had just deleted, behind on every
  run. The demo's state now lives and dies inside the throwaway directory. (Windows also used to be
  told to "delete it by hand" — a pooled SQLite handle held the file open past cleanup.)
- **The rehearsal the README points contributors at is green again.** Moving `run.db` broke the shared
  helper the live-control-plane rigs read their evidence through, so five of its checks had been
  failing on a perfectly healthy engine, with a message that blamed the engine. Those rigs also wrote
  their throwaway runs into the operator's real store; they now keep them in their own scratch.
- **The front page describes the Face that ships.** It advertised eleven tabs and named three that had
  been merged away an era earlier, while omitting the one the Face opens on. Its session-outcome table
  was missing `AuthFailed` — a dead credential parks the run for good — along with `BlockedUntil` and
  `AgentError`. Both lists are now checked against the source by a test.

## [0.3.0] - 2026-08-01

**The Sarban face era** — the watcher and the surfaces. 0.2.0 shipped an engine that could be trusted
to run unattended; this era is about being able to *see* what it did, and to be told when it needs
you. It was built the way the last one was: conductor driving itself against this repo, unattended,
with every claim verified by an independent gate battery rather than by the agent that made it.

The 0.2.1 and 0.2.2 releases were cut mid-era for the token-budget rails and are not repeated here.

### Added

- **`conductor watch` — supervision that belongs in the plan.** A babysitter blocks on the run's own
  file rather than polling a model, so watching a run overnight costs nothing. The wake conditions
  are plan config, not a line of shell history, and the wake can leave the machine: it reaches a
  remote listener over HTTP, proven end to end against a listener that is not conductor. What stays
  manual is written down rather than implied.
- **`conductor ps` — the fleet is visible.** Every run on the machine, read from the control-plane
  discovery files. Engine processes now carry the repo and run id in their process title, so a stray
  `conductor.exe` in Task Manager can be identified before it is killed. When more than one control
  plane answers, the Face probes, leads with the likely one, and asks rather than guessing.
- **An owner queue — the things only you can do.** Decisions that need a human are collected, served
  on the wire, and rendered as a surface in the Face instead of being buried in a log line. A queue
  item that arrives while you are away pushes to Telegram.
- **A session digest, and cards that say who moved them.** What a session actually did — tool mix,
  files touched, what it landed — rendered from the wire. The board's cards carry who moved them,
  when, and how many times.
- **Build identity on the wire.** `GET /state` carries the engine version, commit and Face build, and
  the Face shows them in the top bar and on Home. "Did my reinstall take?" is now answerable from the
  screen (`FU-OWNER-10`).
- **A real endpoint for verifier scores**, so a rendered report no longer needs SQL to exist.
- **`conductor init` scaffolds the whole prompt bank**, with commented advisor and telegram blocks,
  and its output passes `doctor` clean.

### Changed

- **Twelve Face tabs became ten, and the SQL console is gone.** The Dev SQL console, `/report/query`
  and `report --query` are removed; MCP `run_query` stays for chat, and the two Dev panels that were
  never the problem were re-homed rather than deleted. Console folded into Agent as a raw toggle;
  Sessions and Timeline merged into one History surface.
- **One clock vocabulary.** Local time with a relative age and a date when it is not today, from a
  single shared formatter. The Timeline's UTC mislabel is fixed, and three timestamps that were on
  the wire and on no screen now render.
- **Money is honest.** Over-budget renders as `OVER` in dollars rather than as zero-percent headroom,
  window spend is distinguished from lifetime spend, and the top bar shows in-flight session cost
  live.
- **Telegram pushes carry identity.** Every push, digest, command reply and test message is stamped
  with the plan name and session number at the single point they all pass through, so one chat
  receiving two machines' runs is readable (`FU-OWNER-11`).
- **The shipped prompt templates carry the field lessons** — claim before handoff, long commands under
  `conductor bg`, the deferred-MCP fallback, the anchor-commit rule for multi-repo plans — and the
  prompt bank is pruned, indexed and choosable.

### Fixed

- **The thirteen bugs the previous run left open.** Inert plan keys (`workflowStep.model`,
  `stage.overrides.model`, `plan.verifyEachDelivery`) are now either wired to their documented meaning
  or rejected at load — never readable-and-ignored. A claim made during a Verify or Audit session is
  counted, stamped and confirmed like any other. A confirmed last stage completes instead of spinning
  forever. One pid-liveness policy everywhere including MCP; `bg start` stops leaking the caller's
  stdout handle; `bg logs` can read a log that is still being written.
- **An open bug now outlives the run that found it.** `conductor bug` was run-scoped: the moment a new
  run started in the same repo, every open bug from the last one silently vanished — no error, an
  empty ledger that looked clean. Carried bugs now appear in `bug list` attributed to the plan that
  filed them, reach the next session's prompt, and are counted at run end.
- **A run says whether it can notify you at all**, once at startup, in the same sentence `doctor` and
  `/telegram/status` give — instead of only answering when asked (`FU-OWNER-12`).
- **A queued plan reload reads as *waiting*, not as *unconfigured*.** Telegram used to advise the plan
  edit you had made and it had accepted seconds earlier (`FU-OWNER-13`).
- **Home's honest connection line was being truncated into a lie**, and Next steps offered a live
  agent when no engine was running.
- **The docs were reconciled with the code.** `tracker.md` documented a `.conductor/` tree in which
  five entries did not exist after thirty-six real sessions and fourteen real artifacts were missing;
  the backlog listed ten shipped features as future work; `operating.md`'s known-gaps list was a
  three-week-old snapshot. All three are now pinned by tests that read the source, so the next drift
  is a red build rather than a re-read.

## [0.2.2] - 2026-08-01

0.2.1 built the rails and left them reading a gauge that was wired to nothing. This is the wire.

### Fixed

- **The live token counters now move while the session runs.** `ClaudeProvider` emitted each assistant
  message's usage to the event stream but never folded it onto the session state, so
  `TokensInput/Output/CacheRead` stayed **null** for the entire session and were first set by the
  terminal `result` envelope. Every rail that asks the live session what it has spent — the
  soft-break and the `maxSessionTokens` ceiling both do — therefore read zero until the session was
  already over. Observed on a real run within an hour of shipping 0.2.1: a session under a 6M ceiling
  reached **17.13M**, and the log recorded the nudge and the ceiling firing in the same second, as the
  agent was exiting anyway. Both rails now fire when they can still change the outcome.
  Accumulation is safe because `TryCountMessageOnce` already rejects the re-emitted content blocks
  that would otherwise count a message three or four times, and `ReadUsage` still ASSIGNS the
  envelope's totals at the end, so the CLI's own number remains authoritative.

Two tests asserted the old behaviour and were rewritten rather than deleted: they encoded the
reasoning that live deltas must not touch the session totals because double-counting would break the
cap. The first half was right; the conclusion was backwards. Leaving those totals null did not
protect the cap, it disabled it.

## [0.2.1] - 2026-08-01

The session budget stops being decorative. Everything below was already configurable, already
documented and already wired to a surface; none of it could change what a run spent. Two live runs
on one machine spent roughly $200 in a night with `maxSessionTokens` set, because every rail between
that number and the agent was open at one end.

### Fixed

- **A plan edit now applies by itself.** `limits` were only re-read on an explicit
  `conductor plan reload`, and nothing said so: the file could read `maxSessionTokens: 6000000`
  while the engine ran the plan it had loaded hours earlier, and the operator would watch sessions
  sail past a ceiling they had already set. The plan file is stamped when loaded and re-applied at
  the next session boundary when it changes. The reload line now names the budget it just put into
  force, so an edit that took hold says so.
- **The per-session token ceiling ends the session.** The check ran only *after* the agent exited,
  which made it a label rather than a limit — a session ran its full length and was then noted as
  having been over budget the whole time. It is now enforced live. This is the change that matters
  most for spend: a session is billed roughly turns × context and context only grows, so the last
  stretch of a long session costs several times the first, and that is exactly the part a ceiling
  has to be able to cut. Measured here, splitting one 164-turn session into three cuts its bill by
  about half for the same work.
- **A budget-killed session reports what it cost.** It emits no result envelope, so the one session
  the rail acts on was the one session reporting $0 — the ledger read as though stopping early were
  free. It is now priced at the run's own observed dollars-per-token, or left blank when no rate
  has been learned yet.
- **The run-level token total counts cache reads.** It summed input, output and reasoning while the
  per-session total also counted cache, so the two disagreed by roughly forty times on real work: a
  run that had read 79M tokens reported 2.9M. Every surface fed from it — the ledger, the report,
  `doctor`'s headroom, and `limits.maxRunTokens` — inherited that, which put a run cap set from
  observed numbers permanently out of reach. Runs carried over from an older engine step up once.

### Added

- **The cooperative soft-break reaches the agent.** It was written as: spend most of the budget,
  then be asked to land the current sub-task and hand off cleanly. Half of it was missing. The
  engine wrote `.conductor/soft-break` and emitted an event, and nothing carried either to the
  agent — a non-interactive session has no inbox and was never told the file existed — so the nudge
  fired into a void every time and the only rail that could still act was the hard one, which is a
  kill. A per-session `--settings` file now attaches a `PostToolUse` hook (`conductor hook-budget`,
  hidden) that speaks once, when and only when the signal is up, riding a tool call the session was
  making anyway.
- **Sessions are told what they may spend.** When a plan sets a per-session ceiling, the prompt
  carries the budget, the arithmetic behind it (turns × context, context only grows) and the few
  habits that actually move the number: commit early and often, read the sections a checkpoint
  names rather than whole design documents, never re-read what is already in context. It names the
  opposite failure too — a session that reads a few files, declares the problem complex and exits
  without landing anything has spent its whole context and delivered nothing.

## [0.2.0] - 2026-07-31

The Sarban core era: the engine says what it knows. Truthful surfaces, board correction verbs,
detach, structured transcripts — and the engine now knows what version it is and can update itself.

### Added

- `conductor version` and `GET /version` report the semver, the git commit, whether the tree was
  dirty at build time, the build date, and **which binary answered** — stamped by the build itself
  rather than typed into a source file, so "is this run using stale engine code" has an answer.
- Automatic tag-height versioning. The csproj no longer carries a hand-typed version number; the
  `v*` tags are the single source of truth, and a release binary answers with its own tag.
- `conductor update` — checks the latest release, and swaps this binary for it. It verifies the
  download against the release's `SHA256SUMS.txt`, then **runs the downloaded engine and asks its
  version** before replacing anything, and swaps by rename so a failure puts the old binary back.
  It **refuses while a run is live**, because every task claim and background start during a session
  spawns the engine again — a mid-run swap means the back half of a session runs on different code.
  `--check` looks without installing.
- `doctor` gained an `update` line: which engine is running and whether a newer release exists. Never
  a failure (an offline machine is not a broken one), memoised for six hours in a user-level cache so
  it costs nothing, and switchable off with `--no-update-check` or `CONDUCTOR_NO_UPDATE_CHECK`.
- Releases now publish a `SHA256SUMS.txt` manifest alongside the platform archives.
- `CHANGELOG.md` (this file), enforced at release time by `tools/changelog-section.sh`.
- `conductor task --blocked-until`, `--todo`, `--blocked`, `--skipped` and `--amend`, so the board
  can be corrected rather than only appended to.
- `conductor run --detach`: the engine takes its own process group, prints its pid and control-plane
  URL, and survives the shell that launched it.
- Per-session digests on `/sessions` — tool mix, files touched with counts, claims, and background
  work as a storyline — built from structured tool events rather than truncated JSON blobs.
- A `RUN-SUMMARY.md` at the end of a run; `report` and `status` work offline from `run.db`.

### Fixed

- The release build could never have compiled. `-p:PublishSingleFile=true` — the flag every platform
  in `release.yml` publishes with — enables the single-file analyzer, and two reads of
  `Assembly.Location` were IL3000 errors under `TreatWarningsAsErrors`. Since that workflow only runs
  on a `v*` tag push, nothing had ever exercised it. Both reads now use `AppContext.BaseDirectory`.

### Changed

- `install.ps1` and `install.sh` print the version they replaced and the version they installed
  (before → after), so an operator can confirm a rebuild actually took. `install.ps1` gained
  `-SkipShim`.
- Telegram starts on every run path, and `/telegram/status` carries a derived `willDeliver` verdict
  instead of leaving delivery to be discovered at the end of a run.
- `conductor status` no longer reports a healthy run as interrupted while a gate is executing.
- Gate verdicts wait for the session's tracked background children, retry a failed required gate
  once, and judge the work rather than the environment.
- `doctor` fails on configurations that used to fail silently at runtime: a model set without the
  model token, unknown `RunIf`/`SkipIf` tokens, literal braces in prompt text, zero-gate stages.
- History squashing runs after the stage's final state write, works on a dirty tree, reports real
  counts, and un-marks the stage when it fails instead of claiming success.

## [0.1.0] - 2026-07-28

### Added

- First public release. Conductor is a stateful execution environment for days-long AI software
  engineering: a .NET orchestrator (`conductor`) that drives a plan of stages and checkpoints one
  verifiable session at a time, and a Go TUI (`conductor-face`) that watches it.
- `conductor demo` — a complete plan driven to a confirmed finish with no credentials and no spend.
- Self-contained release archives for linux-x64, linux-arm64, macos-arm64, macos-x64 and
  windows-x64, each holding the engine and the Face side by side; neither .NET nor Go is needed to
  run them.
- `tools/install.ps1` and `tools/install.sh` for building and installing from source.
