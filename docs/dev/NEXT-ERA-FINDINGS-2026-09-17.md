# Peyk — the courier stands on its own, and the watch stops being a person

**Compiled 2026-09-17**, three weeks after Charkh closed. The design authority for the two plans in
`plans/peyk/` once they are authored; until then it is the findings-turned-spec, in the shape
`docs/history/NEXT-ERA-FINDINGS-2026-08-23.md` set for Divan.

Charkh turned the era-close into verbs. Then conductor went out and drove **seven runs on three other
repositories in three weeks** — the largest field deployment it has had — and every one of them was
babysat by a Claude session in a terminal. This document is what those babysitters wrote down, read
back against the engine, and turned into decisions. The seven runs are the evidence base; the
babysitters' own field log, handoffs and memory notes are the record; the code was read to confirm
each claim before it became a finding.

The two subjects, in the order the owner named them:

1. **The courier is separated from conductor properly and every word to Telegram goes through it.**
   Today four different things hold the bot token and speak to Telegram in four dialects. The daemon
   died silently twenty-two times in two weeks. The stakeholder-facing channel is a PowerShell script
   in a skill folder that conductor knows nothing about.
2. **The watch stops being a person.** The babysitters intervened on the same dozen things every run:
   an argv ceiling, an injection that lost its second line, a decision nobody in the loop had
   authority to make, a stage that advanced past a red gate, a rate limit the detector could not see.
   Each is a place where the engine could have acted, refused, or measured, and instead a person did.

---

## 0. The seven runs, in numbers

From `conductor history --limit 0` on this machine, 2026-09-17. Every figure is the store's own.

| Run | Repo | Checkpoints | Sessions | Cost | Ended |
|---|---|---|---|---|---|
| pdf2ooxml era 1 (`6de609fd`) | pdf-challenge | 13/13 | 30 | $510.28 | Completed 2026-08-28 |
| Book2Course hardening (`cc253e92`) | BookToCourse | 18/18 | 12 | $195.25 | Completed 2026-09-02 |
| pdf2ooxml era 2 (`06d15602`) | pdf-challenge | 22/22 | 35 | $650.79 | Completed 2026-09-04 |
| Takhteh MVP (`3ad5b497`) | bg | 36/36 | 50 | $751.71 | **Aborted** (to free the lock) 2026-09-05 |
| Takhteh Feel (`e525d597`) | bg | 9/9 | 19 | $341.61 | **orphaned** (engine exited without closing) |
| pdf2ooxml era 3 (`dec4377a`) | pdf-challenge | 24/25 | 49 | $1,100.50 | **Aborted** (A3.3 carried to era 4) |
| pdf2ooxml era 4 (`b522be0f`) | pdf-challenge | 27/27 | 36 | $770.95 | Completed 2026-09-14 |
| **Total** | | **149** | **231** | **$4,321** | |

Two of the seven are recorded as `Aborted` and one as `orphaned` although each delivered every
checkpoint it had. That is the first finding, and it is small: **the record cannot say "finished,
then stopped" — a run that completes its board and is then aborted to free a lock reads as a
failure forever.** (F-STORE-1.)

Set that beside the three self-hosted eras that preceded it (Charkh 14/14 for $129, Divan 23/23 for
$320, edge 23/24 for $324): the field runs are three to eight times the size of a conductor era, on
two runs sharing one 16 GB box, with a stakeholder watching one of them from a Telegram group. That
is the operating envelope this era plans for.

---

## 1. Where the evidence came from, and how to re-derive it

| Source | What it is | Where |
|---|---|---|
| The babysitters' field log | 210 KB, appended at the moment of surprise across every run since July, tagged `engine` / `skill` / `driver` / `plan` | `~/.claude/skills/conductor-drive/FIELD-LOG.md` |
| Watch handoffs | The per-run brief a watcher leaves the next one; the Takhteh one is 132 KB of dated observations | `<repo>/.conductor/WATCH-HANDOFF*.md` in `bg`, `pdf-challenge`, `BookToCourse` |
| The babysitters' memory | Distilled rules per project, each with a **Why** and a **How to apply** | `~/.claude/projects/C--Code-{bg,pdf-challenge,BookToCourse}/memory/` |
| The runs' own record | `run.db` per run in the state home; `RUN-SUMMARY.md`, `OWNER-QUEUE.md`, `lessons.md`, `observer-posts.log` per repo | `%LOCALAPPDATA%\conductor\runs\`, `<repo>/.conductor/` |
| The plans that ran | Their `promptExtra` is 5–7 KB of traps each, and the same six traps appear in all three | `bg/plans/conductor.feel.plan.json`, `pdf-challenge/conductor.plan.json`, `BookToCourse/conductor.hardening.plan.json` |
| The courier's own record | 361 lines, 22 starts, zero exits | `%LOCALAPPDATA%\conductor\courier\courier.log` |
| The Telegram tooling outside the engine | The skill every session and every watcher posts through, and the private room files | `~/.claude/skills/telegram-notify/`, `~/.claude/telegram/<repo>/` |
| The open ledgers | 11 open bugs in the karvansara store; 14 `OPEN` rows in `.conductor/followups.md`; `docs/dev/NEXT-FEATURES.md` still-open list | `conductor bug list --plan plans/karvansara/core.plan.json` |

Every finding below names its source. A finding that says **(read)** was confirmed against `src/`;
one that says **(observed)** is the babysitter's measurement and was not reproduced here.

---

## 2. The Telegram surface today: four speakers, one token

This is the picture the consolidation decision is made against. Four things send to the bot, and
they disagree about almost everything.

| Speaker | What it sends | How | Token | Knows the room? |
|---|---|---|---|---|
| **The run** (`TelegramService`, an `IHostedService` in `conductor run`) | Session-end pushes, parks, owner queue, stage moves, evidence photos, board snapshot, run complete | `IMessageChannel`; on a courier machine every push is handed to `CourierChannel` and crosses loopback `/push` (`TelegramService.Channel.cs:59-66`) | Yes, from `CONDUCTOR_TELEGRAM_TOKEN`; never dials Telegram itself when a courier exists | The plan's `telegram.chats` with profiles. All three field plans list **only the admin DM** |
| **The courier** (`conductor courier run`) | Inbound notes into `.conductor/inbox/`, the "📥 Note received" ack, `/project`, the Promote button; relays the run's pushes | `TelegramCourierSource` + `CourierListener` (`/hello`, `/push`) | The one `getUpdates` consumer | `courier.json` chats and a per-project allowlist; `chat-routes.json` maps one chat to one project |
| **The session** (a spawned agent, per claimed checkpoint) | The observer-facing checkpoint card: progress bar, title, two-to-four sentences in the room's voice, before/after PNGs, `-Live` | `report.ps1` in the shared skill; reads the board from `control-plane.json → /tasks`, else `conductor task --list`; appends to `.conductor/observer-posts.log` | **Yes, directly** — `curl.exe` against the Bot API with the same token | `~/.claude/telegram/<repo>/config.json` (chat ids, footer strings) + `voice.md` (who is in the room, the dials) + the skill's `character.md` |
| **The watcher** (a Claude session in a terminal) | Replies, reactions, free-form findings, documents, corrections | `send.ps1` (`-ReplyTo`, `-React`, `-Delete`, `-Document`), `walk-ids.ps1` to discover message ids by forwarding into the admin DM and deleting the copy | **Yes, directly** | Same private room files |

What each dialect can do that the others cannot (all **read**):

- Only the **skill** can reply into a thread, react, delete, send a document or a media group, or
  draw the checkpoint card. The courier's `/push` carries text, one attachment, buttons
  (`CourierWire.cs:23-45`) and answers with `Accepted`/`Detail` — **no message id comes back**, so
  nothing that went through the courier can ever be replied to or undone.
- Only the **run** knows the board, the stage, the money and the identity stamp. `report.ps1`
  re-derives the bar from `/tasks` and its own "live" rule (a stage whose last checkpoint is DONE).
- Only the **courier** receives. But an inbound note is filed as `InboxNote(Id, ReceivedUtc, ChatId,
  Kind, Text, …, ReplyToMessageId, ReplyToText)` (`Inbox/InboxNote.cs:47-58`) — **no message id, no
  sender**, although `msg.MessageId` and `msg.From` are in the update the source parses
  (`TelegramCourierSource.cs:127-157`). `walk-ids.ps1` exists to recover exactly those two fields by
  forwarding messages one at a time into the admin DM.
- The **watcher** can do everything the skill can, and nothing it does is a conductor event.
  `observer-posts.log` is the only record that a checkpoint was announced, and it is written by a
  script.

And the one rule that shaped all of this — *Telegram allows one `getUpdates` consumer per token* —
constrains **receiving only**. Sending never conflicts. Two of the four speakers already send
directly on the same token every day. The "single point of failure" `CourierChannel` documents about
itself (`CourierChannel.cs:15-19`) is therefore self-imposed: a run whose courier is down could send,
and chooses not to.

---

## 3. Findings

Grouped by theme. **Kind**: bug · missing · design · doc/skill · bottleneck. **Cost** is what the
record says it cost, where the record says.

### 3.1 The courier

**F-COUR-1 — The courier dies silently and nothing in the machine notices.** (bug · observed +
read) `courier.log` records 22 `courier run starting` lines between 2026-09-03 and 2026-09-16 and
**zero** stop, exit or exception lines: `RunAsync` catches every exception (`CourierDaemon.cs:85-118`)
and `CourierCommand.Run.cs` journals a clean stop and an uncaught throw — so the process is ending
by a path that writes nothing, after which `RestartOnFailure` (`CourierTask.cs:197-200`) does not
fire because the task saw no failure. Seen by every watcher: "dead since 16:03Z (~6h45m)",
"third silent death of the run", "stopped filing for ninety minutes and nothing said so"
(`pdf-challenge/.conductor/WATCH-HANDOFF-era3.md`, `watch-live/SKILL.md`). Right now, 2026-09-17,
`conductor courier status` prints `running: no` and `loopback: none`; it last started 2026-09-16
19:48Z and is gone again. **Cost:** the whole of pdf2ooxml M3.1 unrecorded on the owner's phone;
a stakeholder group that "moved on for ninety minutes"; every park pushed into the void while the
daemon was down. Charkh's close filed it as **bug #93** ("died with exit 1, restart-on-failure did
not fire, no log anywhere") and round 3 shipped the journal in the installed build — and every
death since is unjournaled, so the exit path is one the journal never sees. The cause is **not
known**; the log is the only instrument and it is blind to this exit.

**F-COUR-2 — The courier is the engine.** (design · read) `conductor courier run` is
`conductor.exe`. A running courier holds the published binary open, so `tools/install.ps1` must stop
it to publish and put it back afterwards (`ARCHITECTURE.md`, "The installer owns the restart"). The
babysitters restarted it by hand after every reinstall and every silent death, and the memory notes
say which verb to use because `conductor courier run` in a shell "dies with the shell". A daemon
that shares its executable with the thing being reinstalled cannot be always-on.

**F-COUR-3 — A run's pushes stop when the courier stops, by choice.** (design · read) See §2:
`CourierChannel.SendAsync` records the refusal and returns (`CourierChannel.cs:99-113`). The run
goes quiet exactly when the owner most needs to hear (the courier is down). The one-consumer rule
does not require this.

**F-COUR-4 — The allowlist is a thing the owner forgets, per project, per era.** (bottleneck ·
observed) pdf-challenge ran era 3 for days "still NOT on the courier's project allowlist … inbound
Telegram notes to THIS run are not filed. Owner has not been asked to add it." The Feel run needed
`courier allow --repo <repo> --plan "<new name>"` because the allowlist keys on plan *name*, and a
new plan in an old repo is a new name. A live run on this machine already writes into its own
checkout; it should be able to name itself.

**F-COUR-5 — A note carries no author and no message id.** (bug · read) `InboxNote` has neither
(`Inbox/InboxNote.cs`). The consequences fill three memory notes: "A filed note carries no author.
Inferring who wrote it is how a joke lands on the wrong person, which has already happened once"
(`BookToCourse/docs/FEEDBACK-LOOP.md §2`); `walk-ids.ps1`'s whole existence; "a gap in your own
message ids means you missed something". The room has explicitly said it feels ignored when it
gets only reactions (inbox note `83806466`). Replying needs the id; the courier had it and dropped it.

**F-COUR-6 — The courier's "📥 Note received" ack is a message the room asked to have deleted.**
(design · observed) `watch-live/SKILL.md`: "One thing that is housekeeping and always allowed:
deleting the courier's own `Note received` posts as they are found. The owner asked for those to
go." `walk-ids.ps1 -PruneAcks` exists to delete them. The room's own rule is that a reaction is the
acknowledgement (`character.md`).

**F-COUR-7 — On a machine with a courier, the chat's figure verbs are dead.** (bug · read, not
reproduced) KS11.4/KS11.5 built `/evidence`, `/progress`, `/money`, `/tokens` on the in-run poll
loop. `TelegramService.AllowsControl => _courier is null && _cfg?.EnableTwoWay == true`
(`TelegramService.Channel.cs:29`), and the courier's `HandleCommandAsync` answers `/project` and
nothing else (`CourierDaemon.cs:300-330`). Every plan on this machine sets `enableTwoWay: true`
and none of it reaches a handler. The observer group asked what the run cost; the answer came from a
watcher reading `run.db` by hand.

**F-COUR-8 — Three chats, two of them are `admin`, and the second admin chat is a stakeholder
group.** (design · read) `courier.json` lists `-1004410246124` (the Book2Course group with the
product owner in it) as `admin`, because `admin` is what may file notes and `observer` may not
(`docs/cli.md`, "courier chat"). The profile that decides who may *steer* is the same profile that
decides who may *be heard*. The BookToCourse room is a conversation between three peers that
"authorises nothing" (memory: `telegram-is-a-conversation-not-orders`) and it is configured as an
admin.

**F-COUR-9 — Rooms live in a private folder the engine does not know about.** (design · read) The
chat ids, footer strings and the voice of each room are in `~/.claude/telegram/<repo folder>/`,
resolved by the skill's `lib/config.ps1` by walking up from the working directory, with four
fallback locations for unmigrated repos. The decision to keep them out of the repo is right (the
voice file describes the people in the group, and the repo is shared with them). The location is
the skill's, not conductor's, so `conductor` cannot render a card for a room, and a session must be
told in `promptExtra` which script to run.

**F-COUR-10 — `courier file upload` (bug #76) is open**, and the skill sends documents and media
groups every day through `send.ps1 -Document` and `report.ps1 -Before/-After`.

**F-COUR-11 — A park is pushed in a loop, and `--dry-run` reaches the notifier.** (bug · observed)
Appendix A1: 553 identical `🛑 NEEDS HUMAN` lines in five minutes, ~200 phone notifications, from a
process that was `conductor run --dry-run`. A park is a state, not an event stream; it should be
pushed once, and a dry run should push nothing.

**F-COUR-12 — One bot per machine is the design, and the env token enforces it silently.** (design ·
read) Appendix A3: `CONDUCTOR_TELEGRAM_TOKEN` wins over a plan's `secrets.local.json`, so a second
plan cannot have its own bot, and nothing says so. This era keeps one bot per machine (the courier
owns it) and makes the refusal explicit rather than adding a second token path.

**F-COUR-13 — `preflight` fails on a dead courier even for a plan with no Telegram block.**
(bug · observed) Appendix A10.

### 3.2 The observer channel is outside the engine

**F-OBS-1 — Conductor tells the stakeholder group nothing; a script does.** (design · read) All
three field plans carry the same rule in `promptExtra`: *"TELL THE OBSERVER, EVERY CHECKPOINT. The
product owner and Shahin follow this run in a Telegram group that receives NOTHING from conductor —
only what you post with report.ps1 right after each `conductor task --done`."* The plans' own
`telegram.chats` list the admin DM alone. So the channel the owner built the courier for is fed by a
PowerShell script the session is asked to run, with the progress bar re-derived from `/tasks`, the
"live" count computed by the script's own rule, and the voice loaded from two markdown files. Every
one of those is something the engine knows better.

**F-OBS-2 — The card is posted at the claim, not at the verdict.** (design · observed) `report.ps1`
runs "right after `conductor task --done`" — before the gate battery has judged the claim. A
checkpoint whose fast gates go red thirty seconds later has already been announced as done. The
Takhteh log shows cards posted for `T2.2` four times over three hours while the stage was red
(`bg/.conductor/observer-posts.log`, 07:39 to 10:50). "Evidence or it did not happen" is the
engine's rule and the card ignores it.

**F-OBS-3 — Two rooms want opposite voices, and the split into engine + dials was right.** (design ·
observed) `character.md` is the engine ("what earns a message, short, finding-not-status, dry
warmth, no fake certainty, the refusal shape"); `voice.md` is the room. Book2Course asked for
quoted Persian poetry; Takhteh Feel asked for philosophy that is earned and never a quotation. That
split survives consolidation; what moves is *where* the files are found and *who* renders the card.

**F-OBS-4 — The room is not an identity layer and must never authorise anything.** (design ·
decided by the owner) "Orders come only from the terminal … the room has no identity layer to
authorise with anyway." ADR-0005's invariant — *a note is context, never a command* — holds and this
era does not touch it.

**F-OBS-5 — What the session-posted card cannot do.** (design · observed) No stage-level report is
ever posted, because the engine confirms a stage after the last session has exited and the watcher
fills the gap by hand (Appendix A7). A token-ceiling rollover drops the post entirely — it is the
last thing a session does (A8): "the group never heard the lobby exists". And the card's spend lags
by a session, because `run.db` books cost at exit and the post is written mid-session (A9). All
three follow from *who* posts and *when*; D7 moves both.

### 3.3 The prompt has a ceiling and injections fall off it

**F-ARGV-1 — The composed prompt is argv, argv has a 32,767-character ceiling, and the run parks on
it.** (design · read + observed) `ArgvLimits.CreateProcessCommandLine = 32767`, `CmdExeCommandLine =
8191` (`ArgvLimits.cs:22-27`). pdf2ooxml era 3 parked **four times** on `NEEDS HUMAN: prompt for
stage <X> could not be composed` (memory `conductor-artifact-quirks`, 2026-09-07). The healthy
composition sits at ~27–30 K, leaving ~3–5 KB for injections, stage notes and amendments together.
Every plan author now budgets characters (`pdf-engine-era4-run`: "≈ 4.6 KB headroom … an injection
over ~3 KB parks the compose").

**F-ARGV-2 — An argv park burns the injections it could not deliver.** (bug · observed) "#37 was
the fourth argv park of this run and it **burned the eight queued items** — the four-part D25/D26
ruling and four owner notes are all `.done` and never reached an agent." Injections are marked
consumed at composition, and a composition that then refuses to spawn has consumed them for nothing.

**F-ARGV-3 — `conductor inject` keeps the first line, caps at ~900 characters, and dies on a
double quote.** (bug · observed, twice, two repos) bg memory `conductor-cli-traps` §1 and
`conductor-artifact-quirks`: "a 3,300-char ruling was stored as its first 902 chars and reported
`queued … (902 chars)` as if that were the whole thing"; "a blank line ends the argument"; "a `"`
inside the message breaks native-arg parsing … having queued nothing". The `.cmd` shim is bug #75;
`inject` has no `--file`/stdin escape (`InjectCommand.cs`), no `--list`, no `--cancel`. Watchers now
write `PART n of N` steers under 600 characters and read the queue file back after every call.

**F-ARGV-4 — `doctor`'s argv estimate ignores injections, amendments and the gate block.**
(bug · observed) "`conductor doctor` says the steady P3 composition is 28,390 (it ignores
injections/amendments/gate blocks)". A fix session composed at 35,829 because the failed-gate block
carried the previous session's whole battery output; a deliver session at 41,566 because a card
carried four amendments (7.8 KB).

**F-ARGV-5 — The generic traps are re-typed into every plan.** (doc/skill · read) The same six
rules — run long things under `conductor bg start`, claim with `task --done --evidence` before the
handoff, evidence lives in the two watched dirs, commit per checkpoint, never weaken a measurement,
post the checkpoint — appear in all three field plans' `promptExtra` (5,299 / 6,996 / 6,650
characters), where they cost argv every session. They are conductor's rules, not the project's.
And conductor's own `## Conductor tools` block was measured at **44 % of a 32,834-character
prompt** (Appendix C1) — identical every session.

**F-ARGV-6 — A card's amendments are rendered uncapped.** (bug · observed) Appendix C2: two ~2 KB
`task --amend` notes took a prompt from 30,816 to 34,208; `batteries` has `maxBytes`, the card
context has nothing.

**F-ARGV-7 — `doctor` prescribes "pass the prompt on stdin" and the engine has no stdin path.**
(bug · read) `DoctorCommand.PromptSemantics.cs:113` names the remedy; `AgentSession.cs:129`
substitutes `{prompt}` into the argument list and `:221` closes stdin (Appendix C3). D11 builds
what the doctor already recommends.

**F-ARGV-8 — Injections cannot be aimed, and a consumed one is never re-delivered.** (design ·
observed) A steer queued two minutes into a session waits for the session after next, by which
time it is stale (Appendix D2, five entries in one night); a Fix session swallowed a docs steer four
seconds after it was queued (D11); a session killed after consuming an injection takes it with it
(D4). `inject` has no `--cancel` (D3).

### 3.4 The verdict's edges

**F-VERD-1 — A stage advances past a red gate once its last checkpoint is DONE.** (bug · observed)
"`GatesRed — queuing fix session (attempt 3/15)` and, two seconds later, `stage → V4` … The queued
fix session never ran: 'all checkpoints DONE' wins over GatesRed" (`conductor-artifact-quirks`,
2026-09-11, era-4 s15). `StageSelection.Decide` checks `AllEffectivelyDone` before anything else
(`StageSelection.cs:206-210`). The red rode into the next stage as that stage's problem.

**F-VERD-2 — A `Progress` verdict consumes no attempt, so a churning stage never trips the
breaker.** (design · observed) "#27 started, and it is STILL 'attempt 3/24': a Progress outcome does
not consume an attempt, so the 24-attempt circuit breaker will never stop this — only the $1,500 cap
will." Three consecutive `gates GREEN · newly DONE []` sessions on T3.4 cost ~$60 before a watcher
decided whether to intervene. Nine consecutive GatesRed sessions in T3 ($283) were the same shape
from the other side: real work landing, nothing confirmable, and the only actor with authority absent.

**F-VERD-3 — A rejected five-hour window with overage disabled is not detected as a usage limit.**
(bug · observed) Takhteh, 2026-09-04 21:00: `session-051.jsonl` opens with a `rate_limit_event`
(`status rejected`, `five_hour utilization 1.01`, overage disabled, `resetsAt 22:00`); "Conductor
did NOT log its usual `usage limit detected — backing off` line for this shape … Sessions die in
~8s at $0.00 and each burns an attempt." Two attempts of Z1's six were spent this way and the run
was paused by hand. The detector reads the result text and the raw tail (`SessionRunner.cs:380-431`);
the stream's own `rate_limit_event` envelope is not consulted.

**F-VERD-4 — A killed session leaves no cost envelope and is priced from live tokens.** (design ·
observed) s41 of era 3 hit the 240-minute wall running a 3-hour tier: exit -1, `$36.85 priced from
48.6M live tokens`, one attempt and one resume spent; nothing lost because the work was committed.
The honest number is there; the outcome accounting is the cost.

**F-VERD-5 — `status`'s `checkpoints n/N` counts only confirmed stages; the card record disagrees.**
(design · observed) "`status` header still prints `checkpoints 33/36`: that counter tallies only
checkpoints in *confirmed* stages … The card record … is correct." A watcher reported the wrong
number to the owner before working out which one was which.

**F-VERD-6 — The `dirty` input.** Fixed at `5f8e48b` for conductor's own writes. What remains is
the Takhteh case: 49 re-shot evidence PNGs left uncommitted flip every subsequent verdict to `dirty
YES` and the flag "becomes exactly as unreadable as pdf2ooxml's" (bg handoff, 16:45). A plan cannot
name paths the verdict should ignore.

**F-VERD-7 — Things that are not the agent's fault burn the agent's attempts.** (design · observed,
the ledger's most expensive theme) A 429 on the account's five-hour window reads as `AgentError`
and burns a stage's whole attempt budget in three minutes (Appendix H1: attempts 2→8 of one stage,
5→6 of another, both runs down); a 529 is labelled "usage limit" (H3); a transient DNS cut goes
from `session #4 exited (code 1, 3m, $0.00)` to `NEEDS HUMAN` in ten minutes because the breaker
counts $0.00 sessions and the advisor is consulted over the same dead network (F4); a machine
reboot produces the same false park (F5). Every one of these shows as `code 1, 0m, $0.00`, and that
discriminator is free.

**F-VERD-8 — A rolled-over session's claim flips DONE with no gates ever run, and its
`commitCount` is 0.** (design · read + observed) Appendix E7 (`SessionRunner.cs:406-417`) and F2
(32 rolled-over sessions recorded 0 of their 24 real commits, which inverted a productivity
verdict). The first is a verification gap, the second a lie in the record.

### 3.5 Gate artefacts

**F-GATE-1 — A failed first attempt's output is written only when the retry completes.** (bug ·
observed) "Gate output for a failed first attempt is only written when the retry completes. Do not
guess." and "A leg that passes on retry may leave its first failure with no artifact at all"
(`conductor-artifact-quirks`). The watcher waited 45 minutes to read a failure that had already
happened.

**F-GATE-2 — The artefact header lies in the fail case.** (bug · observed) "`T3-full-…-attempt1.log`
line 1 says 'kept because a later attempt passed (bug #86)'. THE LATER ATTEMPT DID NOT PASS." The
pass-case sentence is emitted on the fail path; a reader concludes flake and looks elsewhere.

**F-GATE-3 — Capture truncation keeps the count and loses the names.** (design · observed)
`batteries.maxBytes` caps the body, the header is prepended *after* the cap, and the cut is a tail
cut — so a battery whose failure names are scattered through the body arrives as `RED (5 failed)`
with two `[FAIL]` lines. The Takhteh watcher's 16:32 entry is a page on how to tell whether a
truncated artefact's failure list is complete. That analysis should not be necessary.

**F-GATE-4 — Two runs on one box contend, and the seconds are what break.** (bottleneck ·
observed) pdf2ooxml gates on wall-clock (a 2,700 s ceiling; measured spread 2,043–2,575 s) and
Takhteh's e2e listener died under load; a watcher installed a scheduled task to *record* when the
two batteries overlapped (`battery-contention-recorder`) because a lock could not go into either
repo mid-run. Both engines retried on their own; neither knew the other existed. A full battery
failed at 2,590 s, "110 s under their ceiling", under total contention.

**F-GATE-5 — CI and the gate battery differ, and the owner queue says so, forever.** (design ·
observed) CH1.3 shipped `ci-battery DEGRADED/DEAD` as an owner-queue item. On BookToCourse it read
"ci-battery is DEAD — no CI job runs on windows" for the whole run against a project whose CI is
ubuntu-only by design; on Takhteh it named `pnpm install`/`pnpm exec` as commands the gates lack.
The item has no acknowledge, so it is the first thing on the queue every session boundary.

**F-GATE-6 — A `gatePolicy` reload while a stage is gating drops the pending phase gate.** (bug ·
observed + read) Appendix E3: `scheduling full-battery phase gate` → `plan reloaded … v2` → `stage →
H2`, no battery between; `StageSelection.cs:202` guards the pending gate with `plan.PerPhaseGates
&&`. A stage closed with six known regressions.

**F-GATE-7 — The session gate log keeps the tail only, and the phase-gate file is withheld until
the gate completes.** (design · observed) Appendix E8 (a failing test pushed off the ~50-line
tail) and E9 (47 minutes with the cause invisible). Both are the same shape as F-GATE-1.

### 3.6 Decisions, parks and the owner queue

**F-DEC-1 — A decision only the owner may take, with no queue to put it in, deadlocks the run
silently.** (design · observed, the most expensive finding) pdf2ooxml era 3: nine consecutive
GatesRed sessions in T3 (~$283, ~14 h) and again on R3.4, "while `.conductor/OWNER-QUEUE.md`, which
conductor regenerates every session boundary, read **'Nothing is waiting on you'** the whole time.
It only knows parks, `HUMAN:` lines, unapproved owner gates, blocked checkpoints and skipped stages;
a D21 ruling is none of those." The owner's ruling, 2026-09-07: *"I'd rather squeeze the decisions to
the end of the plan or even after end of the plan as part of cleaning up … but we don't end up with
dead end and dead block for something that that session cannot fix."* The project built its own
answer in prose (D25/D26: a session banks the decision in `docs/RULINGS.md`, keeps going, and the
batch is drained at the close) — and then era 4 parked **three sessions in a row** (s33–s35, ~$18 and
~2 h of batteries) because the drain was delegated to "the watcher" and no watcher was armed.

**F-DEC-2 — `conductor watches` says `SUPERVISOR none` with a live watch attached.** (bug ·
observed) The column reads the plan's `supervisor` block, not a live `conductor watch`
(`WatchesCommand.cs:116-130`). "This column alone cost this run three sessions."

**F-DEC-3 — `approve` on a budget park resets the counter; the correct sequence is three commands
in the right order.** (design · documented) Known since July, still in every watcher's handoff as a
warning: "approve alone resets the counter and doubles the authorisation". The verb should refuse
the wrong order or print the arithmetic.

**F-DEC-4 — `resume` on a human park re-parks within seconds; `kill` pauses.** (design ·
documented) Both are in `intervene.md` because both surprised a watcher.

**F-DEC-5 — Owner-queue items carry no age.** (bug · read) `OwnerQueue.cs:434`:
`"unknown — this source carries no timestamp"` printed under every item on every queue this era.

**F-DEC-6 — The park matcher and the queue disagree about what a `HUMAN:` line is.** (bug · read)
`ProgressConventions.cs:59` parks on `handoff.Contains(token)`; `OwnerQueue.cs:98-99` lists a line
only when it `StartsWith` the token (Appendix I1). A mid-line mention parks the run with no queue
entry; the built-in templates teach the convention, so a session that passes it on parks itself.
Re-learned on five runs; `doctor` now sweeps for it, the engine still disagrees with itself.

**F-DEC-7 — A `BLOCKED` checkpoint makes its stage unclosable forever, with no tell.** (design ·
read + observed) `TrackerParser.AllDone` counts `IsDone || IsSkipped`; selection skips BLOCKED
(Appendix I4). Takhteh would have "ended at 35/36 with a permanent record saying the staging deploy
was blocked by Cloudflare, which is false" had a watcher not claimed the card by hand.

**F-DEC-8 — The budget verbs are ordered wrong for the way people use them.** (design · read) The
cap is checked before a queued reload is consumed, three seconds apart (Appendix G2); a reload
un-parks a session-cap park and silently declines a budget park (G3); the `--once` exit path never
persists the session's cost, so the cap is per process (G8). All three are one seam.

### 3.7 The store and the cards

**F-STORE-1 — A finished-then-aborted run reads as a failure.** See §0. `conductor run close`
refuses an already-aborted run ("already Aborted — nothing to close", bg memory).

**F-STORE-2 — Checkpoint cards are immutable mid-run, and a session's tracker edit is silently
reverted.** (bug · observed) Takhteh, 2026-09-04: the owner re-scoped a stage; session #44 wrote two
new rows into `TRACKER.md` (commit `e5b48c8`); conductor regenerated the tracker from `run.db` at
the boundary and reverted both — "no error, no warning". The stage then completed on its one
surviving card with a stale title. `conductor task` offers status transitions only.

**F-STORE-3 — A second plan in the same repo trips four traps.** (design · observed) bg memory
`conductor-cli-traps` §4: the paused engine holds the state-dir lock; `.conductor/state-pointer.json`
pins the old run's db so `doctor` and `run` open the old graph; the old run's pending injections are
consumed by the new run's first session; the courier allowlist keys on plan name. Each was
discovered by hitting it.

**F-STORE-4 — Bugs and non-ASCII.** `conductor bug new` stores non-ASCII as mojibake ("an em dash
became three cp437 glyphs, Persian became box characters"). Bug ids restart at 1 per run while the
project's own `BUGS.md` numbers run on; every rulings file carries both numbers.

**F-STORE-5 — `bg_status` returns every process the run ever started** — 136 rows, tens of
thousands of tokens, by session 18.

**F-STORE-6 — `conductor` is not on PATH in the session's Git Bash, and the MCP surface lacks the
correcting verbs.** (bug · observed) "every `conductor task|bug|note` call dies with `command not
found`; the MCP tools cover `task_update` (status only), `bug_new/fix`, `conductor_note` and `bg_*`,
but **not** `task --amend`, `--blocked`, `--todo` or `--skipped` — which is exactly what a session
needs to correct the board."

**F-STORE-7 — The evidence watcher scans two directories, and two of three projects wrote elsewhere.**
(design · observed) BookToCourse: "no screenshot was delivered for the first 21 sessions of the run".
Takhteh mirrors `eval/evidence` into `.conductor/evidence` by hand in its harness.

**F-STORE-8 — The stage squash rewinds, rejects and repeats.** (bug · observed, severe) Appendix
J5: a defensive `rebase --abort` of a *stale* rebase rewound a branch 28 commits and the engine read
the truncated history as success. J6: a squash after a mid-stage push guarantees a non-fast-forward
reject, the merge that resolves it seeds the next squash's refusal, and BookToCourse's memory holds a
five-step recipe for the divergence that follows (`conductor-squash-diverges-from-remote`). J9: the
final squash runs before the last report write, so every completed run ends dirty. The owner's own
ruling on it: "stop squashing or force-push".

**F-STORE-9 — `plan set` rewrites the file, and a live plan edit rewrites the tracker.** (bug ·
observed) `plan set` strips JSONC comments, adds a BOM, materialises defaults and writes a `.bak`
inside the repo (Appendix G6, and `conductor-plan-editor-rewrites-the-file` from August). A plan
file changed on disk auto-reloads and `WorkGraphSync` regenerates the tracker with a **stale
handoff block**, reverting a park-clearing edit (G7).

**F-STORE-10 — What the surfaces say that is not so.** (bugs · observed) `status` reports a healthy
run as "interrupted mid-session — resume with `conductor run`" for the whole verdict window, and
following that advice starts a second engine (Appendix B1, open five weeks); `attentionReason` and
`what hurt` are last-known, not current (B6); `status -p <run name>` crashes and drops a crash log,
and any verb run from a subdirectory drops one *there*, which `git add -A` then commits (B10, B11);
filenames are UTC and log lines are local (B13); `--dry-run` writes `stage → X` into
`conductor.log`, which is the dead-engine signature (B14); `--port` is a request, not a fact, so a
watcher on 4317 can read the wrong run (K1).

### 3.8 The watch itself

**F-WATCH-1 — The intervention procedures are a skill because the engine does not perform them.**
(doc/skill · read) `conductor-watch/reference/intervene.md` is 100 lines, of which the two budget
procedures are "counterintuitive and cost real money when improvised". The monitor filter is
copied, never retyped, "because retyping it dropped `verdict inputs` on the Charkh launch and left
the watcher blind to every outcome". `conductor watch` exists (SF5) and folds 5,209 events into RAM
on a box whose RAM is the parallelism limit; it was killed twice by the harness under memory
pressure and replaced with a log tail.

**F-WATCH-2 — The two rooms' watchers did work that was not watching.** Claiming a checkpoint on the
owner's word without a session (`task --done` + `--amend`, "cost zero sessions and zero dollars");
draining rulings as the owner's delegate (`OWNER-REVIEW-<date>.md` + an inject); restarting the
courier; deleting stale injections; writing the contention recorder. Each is a verb the engine could
own: the first two are F-DEC-1 and F-STORE-2 from the other side.

**F-WATCH-3 — The record of what the watchers learned is in a skill folder.** The field log's
`engine`-tagged entries are conductor's backlog and none of them is in `conductor bug list`.

**F-WATCH-4 — There is no stable vocabulary a watcher can subscribe to.** (design · observed, six
times) Every monitor filter was derived from prose and every one missed a real event until the
`Log($"…")` string was grepped from source: the budget park (`AwaitingOwner` is never printed), run
completion, the token ceiling, the owner gate, `verdict inputs` (Appendix B5). `conductor log
--query` crashes on a live run over a `FileShare` flag (B2). There is no `conductor ps`, so two
engines on one box are told apart by `Win32_Process` command lines (B3, B4), and `Get-Process` has
missed a live engine outright.

**F-WATCH-5 — Sessions of one run wrote into another run's repo.** (design · observed) Appendix
J15: a BookToCourse session wrote the room files into `C:\Code\bg`. Nothing scopes a session to its
own checkout; nothing this era changes that (D21), but the room files no longer live in a repo (D6).

### 3.9 Still open from before, carried

- **Bug #76** courier file upload · **#46** bugs do not survive a state-home split · **#42** a
  live-store duplicate cannot be repaired · **#56** `ControlPlaneServer` split · **#72** the Face's
  key dispatch · **#41/#47/#83** payesh anonymity (satellite) · **#80/#82/#90** owner acts on
  GitHub. Followups: `FU-F1-06` (`UpdateRunStatus` — a run that ends `NeedsHuman`/`Paused` reads
  `running`; it is what makes F-STORE-1's `orphaned` rows), `#24` (`AgentConfig.Merge` drops `Env`),
  the Face `tokens cap` row reading the plan file, `approve` missing `--yes`/`--force`.
- `NEXT-FEATURES.md` still-open: branch hygiene warn-only; commit/push discipline unenforced; the
  processes lane; lane spend outside the cap. None of these bit the seven runs and none is planned
  here.

---

## 4. Decisions

Taken here so the sessions build rather than argue. Each names what it settles and what it leaves.
An alternative that was considered and rejected is stated once.

**D1 — The courier is its own executable, managed by `conductor`.** A new project
`src/Conductor.Courier` (references `Conductor.Core`, nothing else) builds `conductor-courier.exe`.
`conductor courier install|status|restart|stop|uninstall|allow|deny|chat|unchat` stay exactly where
they are and manage that binary; `conductor courier run` becomes an alias that execs it. The
installer publishes both and **no longer needs to stop the courier to publish the engine** — the
file-lock coupling is the thing D1 exists to remove. The protocol version still gates a stale
courier; `restart` is still the fix. *Rejected:* a Windows Service (needs elevation; the per-user
task was chosen at DV4.2 for exactly that reason) and leaving it inside `conductor.exe` with a
copy-then-run trick (it hides the coupling instead of removing it).

**D2 — The courier's death becomes a recorded, supervised event, and the cause is measured before
it is fixed.** In order: (a) a heartbeat — `courier.run.json` gains `lastPollUtc`, written every
poll, and `courier status` reports `alive`, `stale (last poll N min ago)` or `dead (last seen T)`
rather than pid-liveness alone; (b) a **death record** — a presence file found at startup means the
previous instance did not clear it, and the new instance writes `previous courier pid N died
silently; last poll T; task last-run result R` to the log, with `R` read from the scheduler
(`schtasks /query /v` for the task's last result and last run time); (c) an unhandled-exception and
`ProcessExit` handler that journals before dying; (d) a **keep-alive trigger** on the task — a
repeating calendar trigger every five minutes with `MultipleInstancesPolicy IgnoreNew`, which
restarts a task that exited *for any reason*, exit code 0 included, because `RestartOnFailure` does
not; (e) a run checks the heartbeat at every session boundary and, when it is stale, starts the task
itself and says so in the log and the owner queue (`courier restarted by this run, 3rd time`). The
checkpoint that ships (b) and (c) runs the courier under them for at least 48 hours and reports the
first recorded cause. If the cause turns out to be the machine sleeping, the record says so and (d)
is still the fix.

**D3 — When the courier is unreachable, a run sends directly.** `CourierChannel` falls back to
`TelegramService`'s own transport with the token from the environment, logs `courier unreachable —
sent directly`, and the channel health line says which path delivered. Sending does not violate the
one-consumer rule; only polling does. The courier stays the preferred path because it holds the
identity of the machine's chats and keeps the message-id ledger (D5).

**D4 — A live run names its own project to the courier.** The run's `/hello` carries repo path and
plan name; the courier adds the allowlist entry marked `by: run <id>` if absent. `courier allow`
stays for projects with no run live. *Security note, recorded:* the allowlist prevents a daemon from
writing into arbitrary checkouts; a run already has write access to its own checkout, so this adds
no reach.

**D5 — One local API and one CLI verb for everything anyone sends: protocol 3.** The courier's
loopback grows `POST /send` (text or file, `replyTo`, `chat` by id or by room profile, parse mode,
media group), `POST /react`, `POST /delete`, `GET /chats`; every send **returns the Telegram
message id** and the courier keeps `messages.jsonl` (id, chat, origin, stamp, when) as the ledger
`observer-posts.log` used to be. `conductor say` is the CLI: `say --to observer --file body.md`,
`--reply-to <id>`, `--react 🫡 --message <id>`, `--delete <id>`, `--document a.md,b.md`,
`--photo before.png,after.png`, `--dry-run`. It goes through the courier when live, directly
otherwise (D3), and refuses past Telegram's own ceilings by name (4096 / 1024 / 10 × 10 MB) as
`TelegramLimits` already knows them. `/push` (protocol 2) stays accepted for one era.
*Rejected:* keeping the PowerShell scripts as the wire and having the engine call them — the engine
would then depend on a skill folder.

**D6 — Rooms are conductor's, and stay private.** A room is `{project, chats: {admin, observer},
footer strings, voice: <path>}`. It lives in the courier home as `rooms/<project-slug>.json`, the
voice file stays wherever the owner keeps it (today `~/.claude/telegram/<repo>/voice.md`) and is
only ever *pointed at*; nothing about a room enters a repository. `conductor room add|show|list
--repo` manage it; `courier chat` and the plan's `telegram.chats` are read as the fallback so an
unmigrated repo keeps working. The one-time migration reads every `~/.claude/telegram/*/config.json`
and offers to import it. The skill's `character.md` (the engine of the voice) moves into the tree as
`docs/rooms/character.md` — it names no person, no project and no poet, and it is the part every
room shares.

**D7 — The checkpoint card is composed by the engine and pushed at the verdict, not by the session
at the claim.** `conductor task --done <id> --evidence <path> --tell "<title> | <two to four
sentences>"` stores the session's words on the claim event. When the verdict confirms the claim
(gates green), the engine composes the card — bar from the work graph, counts and the live rule from
the plan, the footer from the room, the words from the claim, the before/after pair from the
evidence registry — and pushes it to the room's observer chat through D5. A claim whose gates go red
posts nothing; the session is told in its next prompt that the words are held. The card is a
`NotifyTemplate` like every other push (`{bar} {done} {total} {live} {title} {line} {footer}`),
owner-editable per plan. `report.ps1` is retired. The "live" rule is the skill's ("DONE checkpoints
of every stage whose last checkpoint is DONE") as the default, with `stages[].deploys: true` to name
a stage's deploy checkpoint explicitly. *Rejected:* posting at the claim to keep the room's
immediacy — F-OBS-2 is exactly the cost of that.

**D8 — A session writes in the room's voice because the voice reaches it as a battery.**
`RoomVoiceBattery`, bounded in bytes like every other battery, carrying `character.md` plus the
room's `voice.md`, on for a plan whose room has an observer chat. A session that has to post a
finding mid-way uses `conductor say`; the card it does not post at all (D7).

**D9 — An inbound note carries `MessageId`, `SenderId`, `SenderName`, `SenderUsername`.** All four
are in the update already. The ack becomes a reaction (✍️) on the note, never a message; the Promote
button moves to the reply the courier sends only when asked. `conductor inbox list` shows the sender
and the id; `conductor say --reply-to` takes a note id *or* a message id. `walk-ids.ps1` is deleted.
The no-authority rule (F-OBS-4) is restated in the note's own file header: identity is for
addressing, never for permission.

**D10 — The figure verbs are answered by the courier from `run.db`, read-only.** `/progress`,
`/money`, `/tokens`, `/status` for a project resolve through the catalogue to that project's newest
run and answer from the store the way `conductor status` and `money` do, whether or not a run is
live. `/evidence` is answered by the courier from the evidence registry. The in-run handlers stay
for a courier-less machine. Nothing the courier answers writes run state — ADR-0005 and ADR-0008
hold.

**D11 — The prompt leaves argv.** The Claude CLI reads the prompt from stdin when `-p` is given
without a positional; `agent.promptVia: "stdin" | "argv"` defaults to `stdin` for provider `claude`
and `argv` for everything else. The first checkpoint of that stage **measures** it (a 60 K prompt,
byte-identical on the far side, on this machine's CLI) before anything depends on it. The 8191 shim
ceiling and the 32,767 CreateProcess ceiling then stop being plan-authoring numbers for the common
case; `preflight` keeps reporting them for `argv` providers and adds the queued injections, the
card's amendments and the gate block to its estimate so the number it prints is the number that
spawns. *Rejected:* a prompt file plus `@file` — the CLI does not read one.

**D12 — Injections are consumed when the session spawns, and the verb stops lying.** `inject`
gains `--file <path>` (and `-` for stdin), `--list`, `--cancel <n>`, keeps every byte, and prints
the stored length **and** the input length so a truncation cannot hide. A composition that refuses
to spawn leaves the queue untouched. An injection consumed by a session that ends with no verdict
(killed, crashed, parked at compose) is **re-queued**, marked `redelivered`. `--for deliver|fix|any`
(default `any`) and `--stage <id>` aim it, so a docs steer is not swallowed by a fix session. The
600-character / `PART n of N` folklore ends.

**D13 — Decisions are banked, not parked.** `conductor decision ask "<question>" --vote "<the
session's own answer>" --for <checkpoint>` from a session (and the matching MCP tool) writes a row
to `.conductor/decisions.md` and a `DecisionAsked` event; it becomes an owner-queue item of a **new
kind** with an age, pushed to the admin chat with two buttons (accept the vote / reject) and a
`conductor decision answer <n> --accept|--reject --note`. The session keeps going on its vote. At a
stage boundary the engine lists the unanswered decisions in the next prompt; at the run's close they
are the owner's list. A drain by a delegate is `decision answer` with `--as delegate`, recorded.
This is D25/D26 and the era-4 rulings drain as machinery, with the owner's own words as the brief.
*Rejected:* parking on every decision — the owner ruled it out in so many words.

**D14 — The verdict's edges are closed.** (a) A stage whose last verdict was `GatesRed` cannot
advance on `AllEffectivelyDone`; the queued fix session runs first, and the stage confirms only on
a green verdict or its phase gate. (b) `limits.maxProgressWithoutClaim` (default **3**): that many
consecutive `Progress` verdicts with no new claim on one stage consults the advisor with the run's
recent handoffs and then parks as `churn`, an owner-queue kind with a `resume`/`skip` clearing
command. (c) **Nothing the agent did not do burns an attempt.** A new outcome,
`BackendUnavailable`: a session that exits non-zero having spent `$0.00`, produced no assistant
text and lived under `limits.backendFailureSeconds` (default 90) — or whose stream opened with a
`rate_limit_event` of status `rejected`, or whose tail carries a 5xx — backs off (to the envelope's
`resetsAt` when it names one, else the plan's `backoffMinutes`), burns no attempt, does not count
toward the same-failure breaker, and does **not** consult the advisor (it runs on the same backend).
The log names the shape: `backend unavailable — 429 five_hour rejected, reset 22:00 UTC` /
`— 529` / `— no route to host`. A reboot between two sessions (`LastBootUpTime` after the last log
line) is logged as such and resumes rather than parks. (d) `status` prints `delivered n/N ·
confirmed m/N`. (e) A session killed at the wall while a tracked bg job is alive is recorded
`TimedOut (bg alive)` and consumes a resume, not an attempt; a rolled-over session's `commitCount`
is recorded from git, not left at 0. (f) `verdict.dirtyIgnore: [globs]` in the plan. (g) A
`BLOCKED` checkpoint whose stage has nothing else runnable is an owner-queue item with age, and
`status` says `stage X cannot close: 1 BLOCKED`.

**D15 — Gate artefacts tell the truth.** Every failed attempt's output is written when it fails,
named `<stage>-<tier>-<utc>-attempt<n>.log`; the header states this attempt's own verdict and the
retry's, once it exists; `batteries.maxBytes` applies to the body and keeps **head and tail** with a
marker between, so a verdict block at the end survives.

**D16 — Two runs on one box share a battery lock.** `gates.exclusive: true` (default **true** for
`full` tier) takes a machine-level lock file in the state home with pid, run id and started-at;
the second run waits with a heartbeat line every minute up to `gates.exclusiveWaitMinutes`
(default 45), then proceeds and stamps the gate record `contended`. A dead holder (pid gone, or
older than the gate's own timeout) is reaped. `conductor watches` lists the other runs on the box
with their stage and whether they hold the lock. *Rejected:* a scheduler that serialises sessions
too — the owner runs two boxes' worth of work on one on purpose.

**D17 — The cards can change, by event.** `conductor task --add <stage> "<title>"`,
`--retitle <id> "<title>"`, `--split <id> "<a>" "<b>"` write `TaskAdded`/`TaskRetitled` events; the
tracker is regenerated from the graph as it is today, so the markdown never disagrees with the
store again — it stops being the thing a session edits. A session's hand edit of the checkpoint
table is detected at the boundary and reported (`tracker rows edited by hand and discarded: …`)
instead of silently reverted.

**D18 — Small verbs that were folklore.** `plan set` refuses an unknown leaf key. `kill` prints
`the run is now Paused — conductor resume to continue`. `approve` on a budget park prints the
arithmetic and refuses `--amount` above the remaining authorisation unless `--reset` is given.
`resume` on a human park refuses by name while the token is still in the handoff. `run` against a
repo whose state pointer names a different plan refuses and prints the four steps. `bug new`
writes UTF-8. `bg_status` returns live processes unless `--all`. `evidence.dirs` is a plan key.
The session's environment gets the engine's own directory on `PATH`; the MCP task tool gains
`amend`, `blocked`, `todo`, `skipped`. `run close --status completed` is allowed on an aborted run
whose board is full, and `orphaned` becomes `Completed`/`Paused` where the store can prove it
(`FU-F1-06`). Owner-queue items carry the timestamp of the event that raised them. The CI
disagreement item gains `conductor ci accept` and stops repeating once accepted.

**D19 — The generic traps ship with the engine.** The six rules every plan re-types move into the
built-in tool contract and `plan new`'s scaffold, worded once, measured by a test that the three
field plans' `promptExtra` can lose them without a dry-run prompt losing a rule.

**D20 — The skills shrink to the part a machine cannot do.** `telegram-notify` keeps `character.md`
(moved into the tree, D6) and a two-page SKILL.md on *what earns a message and how to write one*,
and loses `send.ps1`, `report.ps1`, `walk-ids.ps1`, `lib/`. `conductor-watch` and `conductor-drive`
lose every procedure D12–D18 make a verb, keep the monitor filter until `conductor watch` streams
(a separate, later decision), and their `engine`-tagged field-log entries are triaged into
`conductor bug list` by the first checkpoint of the second plan. `watch-live` is BookToCourse's and
stays theirs.

**D21 — What is not built.** No second messenger. No inbound port beyond loopback. No steering
from the room. No sandbox. No scheduler across runs beyond the battery lock. No change to the
one-consumer rule. No Face work beyond the rows D18 fixes.

**D22 — Naming and shape.** The era is **Peyk** (پیک, the courier — Chapar was the KS11 spec's name
and stays as the seam's). Two plans, launched in this order: **`peyk/courier.plan.json`** (D1–D10,
D20's Telegram half) and **`peyk/watch.plan.json`** (D11–D19, D23–D26, D20's watch half). The
courier plan goes first because the watch plan's own babysitter needs it. Each is its own tracker,
its own branch (`feat/peyk-courier`, `feat/peyk-watch`), its own cap. *Rejected:* one plan of
thirty checkpoints — the last three eras show a plan past ~25 checkpoints loses its second half to
drift.

**D23 — The watcher subscribes to events, not to prose.** `conductor events --follow [--json]
[--since <seq>]` streams the run's event log as one line per event with the event's **type name**
(`SessionStarted`, `VerdictRecorded`, `BudgetParked`, `PhaseGateFinished`, `RunEnded` …) and the
fields a watcher filters on. The monitor filter in the two skills becomes one line naming types.
`conductor ps` lists every engine, courier and watch on the machine with repo, plan, run id, pid,
control-plane port and state, read from the presence records. `--dry-run` writes nothing to
`conductor.log` and pushes nothing. `status` stops reporting a run "interrupted" while its own
engine is alive (it checks the engine's presence, not the spawned pids). `attentionReason` and
`what hurt` carry the timestamp of the event that set them. `conductor.log` lines carry the same
UTC stamp the filenames do. `log --query` opens with `FileShare.ReadWrite`. `--port` that fell back
prints the port it got and `control-plane.json` is the only truth.

**D24 — A park is pushed once, and the token means one thing.** The park matcher and the owner
queue use the same rule: a `HUMAN:` at the **start** of a line in the handoff block. A park emits
one `Parked` event and one push; the loop that re-evaluates a parked run does not re-push. A
`dry-run` never reaches the notifier (D23).

**D25 — The stage squash is off by default.** `report.squash` defaults **false**; a plan that
turns it on gets the existing behaviour plus a refusal when `.git/rebase-merge` exists and a
`--ff-only` check against the remote before rewriting anything. The final report write happens
before the last bookkeeping commit, so a completed run ends clean. The field runs all set
`report.commit: true` and every one of them hit the reject-merge-reject cycle; nothing in the
record shows the squash bought anything.

**D26 — `plan set` and a live plan edit preserve what they did not touch.** `plan set` edits the
JSONC in place (comments kept, no BOM, no materialised defaults, `.bak` in the state home not the
repo). A plan file changed on disk reloads the plan and **does not** regenerate the tracker's
handoff block — `WorkGraphSync` rewrites the checkpoint table only. A `gatePolicy` reload with a
pending phase gate runs the gate.

---

## 5. The plans

Numbers are compiled against `docs/dev/TOKEN-BUDGET-TUNING.md` §12 and Charkh's own run: **42M /
0.9** as Charkh ran (re-derive with `conductor budget` on the Charkh db before launch and change
the pair only on its verdict). Cost per checkpoint on this repo since Karvansara: $9–14 (Charkh
$9.23, Divan $13.93, edge $14.09). Caps below are set at two times the projection.

### Plan A — `peyk/courier.plan.json` — the courier stands on its own

Cap **$300** · 15 checkpoints · 6 stages · branch `feat/peyk-courier`.

| Stage | Checkpoint | Delivers | Acceptance |
|---|---|---|---|
| **PK1 — Its own process** | PK1.1 | `src/Conductor.Courier` project, `conductor-courier.exe`, `conductor courier run` execs it; `ArchitectureBoundaryTests` gains the rule (Courier → Core only) | `dotnet build` produces both binaries; the boundary test names a violation when one is seeded |
| | PK1.2 | `tools/install.ps1` publishes both and no longer stops the courier; `release preflight`'s courier check re-measured | An install with a live courier succeeds without stopping it; the courier log shows no restart; `release preflight` green on the courier check |
| **PK2 — Alive, or known dead** (D2) | PK2.1 | Heartbeat in presence; `courier status` prints `alive / stale / dead (last seen)`; the death record on startup with the scheduler's last-run result | A courier killed with `Stop-Process` is reported `dead` with a time by the next `status`, and the next start logs the death record |
| | PK2.2 | `ProcessExit` + unhandled-exception journaling; the keep-alive trigger in the task XML; a run restarts a stale courier at the boundary and reports it | The task restarts a courier that exited 0 within five minutes (measured); a run's owner queue carries `courier restarted by this run` |
| | PK2.3 | **The cause.** 48 hours under PK2.1/2.2 on the owner's machine, the first recorded death read out; the finding filed in the tracker and `docs/operating.md` | A dated finding naming the exit path, or a dated statement that no death occurred in the window with the instruments listed |
| **PK3 — One transport** (D3, D4, D5) | PK3.1 | Protocol 3: `/send`, `/react`, `/delete`, `/chats`; message ids returned; `messages.jsonl`; `/push` still accepted | A rig courier answers all four; a protocol-2 push is accepted; the ledger carries the id a real send returned (one live send to the admin DM, then deleted) |
| | PK3.2 | `conductor say` with every switch in D5; direct fallback (D3) with the log line and the health line | `say --dry-run` prints the exact bytes and the resolved chat; with the courier stopped, a send lands and the run log reads `sent directly`; ceilings refused by name |
| | PK3.3 | The run's `/hello` registers its project (D4); `courier allow` unchanged | A fresh plan name in an allowed repo files an inbound note on the first session boundary without `courier allow` |
| **PK4 — Rooms and the card** (D6, D7, D8) | PK4.1 | `rooms/<slug>.json`, `conductor room add\|show\|list`, the one-time import of `~/.claude/telegram/*/config.json`, `character.md` into `docs/rooms/` | `room list` shows the two migrated rooms; a plan with no room and an old-shape `telegram` block behaves byte-identically (golden replay) |
| | PK4.2 | `task --done --tell`, the `checkpoint-card` template, composed and pushed at the verdict to the observer chat; `stages[].deploys`; the held-words line in the next prompt | On a rig run with a fake agent: a green claim posts exactly one card with the right bar, counts and pair; a red claim posts nothing and the next prompt carries the held words |
| | PK4.3 | `RoomVoiceBattery`; `report.ps1` deleted from the skill; the three field plans' `promptExtra` rule 11/17 rewritten to `--tell` | A dry-run prompt for a plan with a room carries the battery under the byte cap; `SF7_1DocsMatchRealityTests` pins `--tell` in `docs/cli.md` |
| **PK5 — Inbound with a name** (D9, D10) | PK5.1 | `InboxNote` gains the four identity fields; the ack is a reaction; `inbox list` shows sender and id; `say --reply-to <note>` | A note filed from a rig update carries sender and message id; an old note without them still lists; the reaction is sent instead of a message |
| | PK5.2 | The figure verbs answered by the courier from `run.db` (read-only) for the chat's project; `/evidence` from the registry | With no run live, `/money` in the admin DM answers the same figures `conductor money` prints; a write to any store is asserted absent |
| **PK6 — The docs, the skill, the close** | PK6.1 | `docs/cli.md`, `operating.md`, `plan-config.md`, `ARCHITECTURE.md` (the courier section rewritten for a separate binary; the seam count re-counted), ADR-0009 amending 0008 for D3/D4/D5 | The docs battery green; the ADR states the four conditions and the two new ones |
| | PK6.2 | `telegram-notify` rewritten to two pages around `conductor say` and `--tell`; the private room files left in place and pointed at; `walk-ids.ps1`, `send.ps1`, `lib/` deleted; `watch-live` re-pointed | A grep of the skill folder finds no Bot API URL; a watcher session posts a reply through `say` (one real post to the admin DM, then deleted) |
| | PK6.3 | `release preflight` + `perform`: CHANGELOG section, tag, the era's own numbers via `budget`/`money` | Owner acts printed; the plan doc moves to `history/` with the repoints in the same act |

### Plan B — `peyk/watch.plan.json` — the watch stops being a person

Cap **$350** · 16 checkpoints · 7 stages · branch `feat/peyk-watch` · launches after Plan A tags.

| Stage | Checkpoint | Delivers | Acceptance |
|---|---|---|---|
| **PW1 — The ledger first** (D20) | PW1.1 | Every entry of Appendix A triaged into `conductor bug list` — `fixed <commit>` / `open, this plan: <checkpoint>` / `open, sweep` / `not a bug: <why>` — each carrying the entry's id and date; the `engine`-tagged field-log entries get the same pass | A table in the tracker with one row per appendix entry; the count of `open, sweep` rows is the input to PW7.2 |
| **PW2 — The prompt has no ceiling** (D11, D12, D19) | PW2.1 | **Measure first:** a 60 K prompt on stdin to the installed Claude CLI, byte-identical; `agent.promptVia`; `preflight`/`doctor` estimates include injections, amendments and the gate block; the card's amendment context capped like a battery | An evidence file with the CLI version and the byte comparison; `preflight` prints the number the spawn uses on a rig with three queued injections and two amendments |
| | PW2.2 | `inject --file/-`, `--list`, `--cancel`, `--for`, `--stage`; whole-text storage; stored-vs-input lengths; consumption at spawn; re-queue on a session with no verdict | A 5 K, three-paragraph, quoted injection round-trips; a refused composition leaves it pending; a killed session's injection reappears marked `redelivered`; a `--for deliver` steer survives a fix session |
| | PW2.3 | The generic traps in the tool contract and `plan new`; the `## Conductor tools` block measured and trimmed against its 44 %; the three field plans' `promptExtra` shortened with no rule lost | A test diffs the dry-run prompt with and without the six rules in `promptExtra` and finds every rule present either way; the block's size before and after in the evidence |
| **PW3 — Decisions are banked** (D13) | PW3.1 | `conductor decision ask\|list\|answer` + MCP tool; `DecisionAsked`/`DecisionAnswered` events; `.conductor/decisions.md`; the unanswered list in the next stage's prompt | A rig session asks two decisions and keeps going; `list` shows them with age; the next prompt carries them |
| | PW3.2 | The owner-queue kind with age; the push with two buttons through Plan A's `say`; the answer injected at the next boundary; `--as delegate` recorded | Pressing accept in the admin DM answers the decision and the next prompt carries the answer |
| **PW4 — Nothing the agent did not do burns an attempt** (D14) | PW4.1 | (c) `BackendUnavailable`: the `$0.00`/no-text/short-life rule, the `rate_limit_event` envelope, 5xx, the reboot check; no attempt, no breaker count, no advisor; the log line names the shape | The Takhteh `session-051.jsonl` opening envelope, replayed on a rig, backs off to its `resetsAt` and burns no attempt; a 529 tail and a DNS failure each land as `backend unavailable`; the breaker's count is unchanged after three of them |
| | PW4.2 | (a) GatesRed cannot be overtaken; (b) `maxProgressWithoutClaim` → advisor → `churn`; (d) `delivered/confirmed`; (e) `TimedOut (bg alive)` + rollover commit counts; (f) `dirtyIgnore`; (g) the BLOCKED tell | A rig with a red fast gate and a full board runs the fix session before advancing; three no-claim greens park `churn`; a stage with one BLOCKED card is named in `status` and the owner queue |
| **PW5 — The record tells the truth** (D15, D23, D24, F-DEC-8) | PW5.1 | Every failed attempt's artefact, the truthful header, head+tail capture; the phase-gate file streamed as it runs; session gate logs no longer tail-only | A two-attempt red battery on a rig leaves two files whose headers name their own verdicts and whose tails carry the verdict block; the phase-gate file exists while the gate runs |
| | PW5.2 | `conductor events --follow`, `conductor ps`; `--dry-run` writes and pushes nothing; `status` stops lying about interruption; timestamps on attention fields; UTC in the log; `log --query` on a live run; the `--port` fallback printed | The two skills' monitor filters replaced by one `events` line that a test proves catches every wake in `docs/operating.md`'s table; `ps` on a rig with two engines names both |
| | PW5.3 | The park matcher equals the queue matcher; a park pushes once; `approve` prints the arithmetic and refuses over the remaining authorisation; a reload un-parks a budget park it satisfies; the cap is checked after a pending reload is consumed; `--once` persists cost; `resume` on a token refuses; `kill` prints `Paused`; `goto` applies once | One test per rule; the 553-line loop's fixture replayed produces one push |
| **PW6 — The cards and the tree** (D17, D25, D26, D18) | PW6.1 | `task --add\|--retitle\|--split` as events; hand edits detected and reported; a live plan edit keeps the handoff block; a `gatePolicy` reload runs the pending phase gate | A rig session's tracker edit is reported at the boundary and the store is unchanged; a park-clearing handoff edit survives a plan-file change; the pending gate runs on the reload |
| | PW6.2 | `report.squash` off by default with the two guards when on; the report written before the last bookkeeping commit; `plan set` in place (comments, no BOM, `.bak` in the state home); the state-pointer refusal; `evidence.dirs`; UTF-8 bugs; `bg_status` live-only; engine dir on the session `PATH`; MCP `amend/blocked/todo/skipped`; owner-queue ages; `ci accept`; `run close` on a full aborted run; `FU-F1-06` | One test per item naming the text `docs/cli.md` prints; a completed rig run ends with a clean tree; a rig session runs `conductor task --amend` from Git Bash |
| **PW7 — Two runs, one box; the sweep; the close** (D16, D20) | PW7.1 | The battery lock with heartbeat, bounded wait, reaping, `contended` stamp; `conductor watches` lists the box's runs and live watchers (fixes F-DEC-2) | Two rig runs on one state home serialise their full batteries and both logs show the wait; `watches` shows a live `conductor watch` |
| | PW7.2 | The one-seam sweep of PW1.1's `open, sweep` rows, in rounds, each bug closed in the commit that fixes it | Every `open, sweep` row is `fixed` or re-homed with a reason; `conductor bug list` shows none open in that class |
| | PW7.3 | `conductor-drive` and `conductor-watch` rewritten around the verbs, `intervene.md` reduced to what still needs a person, the silent-failure table re-verified against this build; `release preflight` + `perform`; the era's numbers; both plan docs to `history/` | Owner acts printed; the tag exists; `docs/dev/README.md` names no open era |

### Pre-launch, for both

0. **Charkh is merged and untagged** — `master` is `b125405`, the newest tag is `v0.5.0`, and the
   CHANGELOG's `[Unreleased]` is Charkh's section. This is the shape edge was in before Divan, and
   it is the owner's act (the version number, then `conductor release perform`). Do it before Peyk
   branches, or Peyk's close carries both eras the way Divan's did — decide, and write which in the
   tracker's first handoff.
1. Reinstall the engine at `master` (`tools/install.ps1`) — every field run since 2026-09-06 ran
   `0.5.1-alpha.0.43`; the plan's traps are for the installed engine.
2. `conductor budget <charkh db copy>` and write its verdict into the plan's `promptExtra` trap 12.
3. **Authored 2026-09-17:** `plans/peyk/courier.plan.json` + `COURIER-TRACKER.md`,
   `plans/peyk/watch.plan.json` + `WATCH-TRACKER.md`, shared `plans/peyk/templates/` — from
   `plans/charkh/` (gates, limits, agent, advisor, github mirror), `promptExtra` re-targeted at this
   era (23 traps, 8,520 / 7,504 characters), `readOrder` = this document, every stage's `notes`
   naming its D-numbers and carrying the acceptance column above.
4. **Measured on the installed `0.5.1-alpha.0.43`, 2026-09-17:** both plans `doctor` **24 ok · 3
   warn · 3 fail**, and every fail is the machine, not the plan — `github` (no token in the
   environment; pass `gh auth token` at launch as Charkh did), `courier` (not running — F-COUR-1,
   the owner restarts the task), `ci-verdict` (a stale `ci-status.json` measured on `07dc493`, from
   before CH1.3 fixed CI; re-ask with a token). The warnings: the dirty tree (this document and the
   bundle, uncommitted), `ci-battery` (CI runs `ratchet.ps1`, the gates do not — Charkh ran the
   same way), and the argv line. `journey` shows opus-5 on every stage; the templates' 16 brace
   tokens all resolve; the escalation token appears nowhere; the trackers' handoff blocks carry no
   token; `conductor history` still lists 41 runs after both `doctor` passes (no duplicate import).
   Longest composed argv: **26,072** (courier, PK4) and **24,914** (watch, PW5) against 32,767 —
   about 6.7 K of headroom each, which is the budget PW2.1 removes.
5. `conductor room add --repo C:/Code/conductor` is **not** needed for Plan A's own run — this
   repo's plan pushes to the admin DM only, as Charkh did. The observer-card path is proven on rigs.
6. Launch `--once`, then `--detach`, from PowerShell at the repo root, with `CONDUCTOR_PLAN` cleared.

---

## 6. What this era does not answer

- The 24-hour Telegram retention on a sleeping laptop. Still an always-on host, still not a setting.
- Sandboxing the agent. KARVANSARA §8 still parks it.
- The Face. Two rows in D18 and nothing else; the tab consolidation ADR stands.
- Lane spend outside the cap, branch hygiene, commit discipline — carried on `NEXT-FEATURES.md`.
- A `conductor watch` that streams instead of folding — a decision for after PW5.1 shows what the
  box can hold.

---

## Appendix A — the babysitters' ledger

Mined on 2026-09-17 from the whole of `FIELD-LOG.md` (2,710 lines) and the middle of the Takhteh
`WATCH-HANDOFF.md` (lines 179–1566; the head and tail are cited in §3 directly). Status is what the
log itself says; a cross-reference to this repo's commit subjects is marked `[repo git log, not in
sources]`. **FL L###** = field-log line range; **WH "§…"** = a handoff section header. PW1.1
triages every entry here into the bug ledger; §3 cites entries by their letter-number.

### A. Telegram / courier / notification

- **A1. A `NEEDS HUMAN` park hot-loops and floods Telegram — including under `--dry-run`.** bug ·
  THE SECOND REEL 2026-08-02 14:30 · "553 identical `🛑 NEEDS HUMAN` lines, 14:18:13 → 14:23:38 … 711
  Telegram references in one day's engine log, many `[WRN] Telegram send error` at
  `TelegramService.cs:420`"; ~200 phone notifications; "the looping process was literally
  `conductor.exe run … --dry-run`" · not stated fixed · FL L1286–1336
- **A2. The courier daemon stops on its own, silently; every surface still says "configured".** bug ·
  Charkh launch 2026-08-27; pdf2ooxml era 1 2026-08-27 20:30; Book2Course hardening 2026-09-02
  ("third time in this log"); WH "courier pid 32540 … has died silently once already" · scheduled
  task "`State: Ready` with `LastTaskResult: 1` about an hour after a verified-running install";
  only `doctor`/`preflight` catch it · root cause not diagnosed · FL L2244–2346, L2380–2406; WH
  "§OVERNIGHT TUNING, 23:36"
- **A3. No getUpdates 409 handling; the env token wins over per-plan secrets, so two engines cannot
  have separate bots.** design · Book2Course prep 2026-08-19 21:00 · "grepped `TelegramService.cs`,
  zero hits" for 409; "a polling engine EATS the getUpdates you need to read a new chat id" · open ·
  FL L2080–2105
- **A4. False "will deliver nothing" startup warning on a chats-only plan.** bug · 2026-08-19 21:20 ·
  `TelegramService.Lifecycle.cs:53` counts `AllowedChatIds.Count` not `TelegramConfig.ChatCount`;
  `/telegram/status` says `willDeliver: true` · open, harmless · FL L2106–2131
- **A5. `POST /telegram/test` indexes `AllowedChatIds[0]` — 500 on a chats-only plan.** bug ·
  `TelegramService.cs:231` · open · FL L2106–2131
- **A6. Keyboards are admin-only; a group member's press answers into their private chat.** doc
  gap · `RemoteSurface.cs:149`, `TelegramService.cs:381` · FL L2106–2131
- **A7. No stage-level report is ever posted — the engine confirms after the last session exited.**
  missing · Takhteh MVP WH 05:50 (2026-09-04) · "a standing gap for the watcher to fill at every
  stage boundary" · open · WH "§05:50"
- **A8. A token-ceiling rollover drops the session's observer post.** bottleneck · WH 01:40 ·
  "#31's rollover ate it … the group never heard the lobby exists … a rollover always drops it" ·
  WH "§01:40"
- **A9. Observer checkpoint posts under-report spend by one session.** design · pdf2ooxml era 2
  2026-09-03 · "run.db books a session's cost only when that session exits … the first post of a
  run reads $0, and every later post lags by roughly one session ($25-40)" · FL L2442–2457
- **A10. `preflight` reports NOT READY on a dead courier even with no telegram block.** bug ·
  2026-08-27 20:30 · FL L2339–2346
- **A11. The skill's `Invoke-RestMethod` transport fails when .NET TLS is broken; bug #22
  misdiagnosed as network.** bug (skill) · WH 17:20 (2026-09-04) · `curl.exe` 302 in 74 ms;
  `Invoke-RestMethod` `SocketException` · WH "§17:20"
- **A12. The shared skill does not fall back when `.claude/telegram/config.json` is removed.** bug
  (skill) · WH 14:12 (2026-09-05) · WH "§14:12"
- **A13. Observer profile = engine chatter; no profile = no `/status /progress /evidence`.** design ·
  2026-09-02, 2026-09-03 · FL L2380–2441
- **A14. `courier allow --repo --plan` is a launch step that is easy to miss.** doc · 2026-09-03
  02:12 · FL L2458–2468

### B. Watcher / monitor tooling and the supervisor role

- **B1. `status` reports a healthy run as "interrupted mid-session — resume with `conductor run`"
  during every verdict window.** bug (dangerous advice) · 2026-07-30 10:48; still true 2026-09-05 ·
  `StatusReportBuilder.cs:84` finds `SessionStarted` with no `SessionFinished`; `RunHasLiveProcess`
  (`:102`) scans spawned pids only · open five weeks · FL L595–657; WH "§12:01"
- **B2. `conductor log --query` crashes on any live run.** bug · 2026-07-30 05:00 ·
  `LogCommand.cs:69` `File.ReadLines` opens `FileShare.Read`; fix is `ReadWrite | Delete` · not
  stated fixed · FL L558–594
- **B3. `Get-Process` can miss a live engine; the skill's "no process = exited" test would start a
  second engine.** doc + missing (`conductor ps`) · 2026-09-13 · `Win32_Process` found pid 204
  heartbeating `── Paused · stage A4 · 24/27 done · $719.31` into its detach log · FL L2695–2710
- **B4. No way to tell engines apart on one machine.** missing · 2026-07-29 ~04:26 · "mapping
  process → run required `Win32_Process` CommandLine queries" · FL L125–159
- **B5. Every prose-derived monitor filter has missed a real event.** doc (recurring) · budget park
  (`AwaitingOwner` never printed, 2026-07-29); run completion (`plan '<name>' complete`,
  2026-07-31); `token cap` never logged (real lines `hit its token ceiling`, `rolled over`,
  2026-08-05); `owner-gate: stage {id} green — awaiting owner approval` (2026-08-04); `verdict
  inputs` dropped → blind to every outcome, `gates (green|RED)` case-mismatches, bare `error`
  fires on `0 errors` (Charkh 2026-08-27); `report push failed:` matches nothing (2026-08-15) ·
  FL L311–427, L894–909, L1652–1670, L1507–1651, L2244–2338, L2054–2079
- **B6. `/state.attentionReason` and `what hurt` are sticky.** bug · 2026-07-29 ~18:35 · `"advisor:
  human intervention required"` on a `Running` run whose park was 10 hours earlier · FL L311–427
- **B7. Persistent Monitors outlive `/clear` and session end; no listing verb; duplicates found
  only by duplicate events.** bottleneck (harness) · 2026-07-29, 2026-08-14, 2026-09-05 ·
  FL L311–427, L502–531, L1945–1962, L2029–2053; WH "§02:05"
- **B8. The engine has no machine-health signal; a watcher sees only `conductor.log`.** missing ·
  WH 14:05, 12:30 (2026-09-05) · a scheduled-task "Battery Contention Sampler" was hand-built ·
  WH "§14:05", "§12:30"
- **B9. Watcher takeover cost 12,767 words before the first event.** doc · 2026-07-31 · fixed by
  the `conductor-watch` skill · FL L769–815
- **B10. `status -p <run name>` crashes the CLI and drops `.conductor/logs/crash-*.log`.** bug · WH
  2026-09-03, 2026-09-05 · WH "§OVERNIGHT TUNING", "§02:05"
- **B11. Any verb from a subdirectory writes a `crash-*.log` there; `doctor` calls it dirty; `git
  add -A` commits it.** bug · 2026-09-03 twice · FL L2407–2441, L2458–2468
- **B12. Crash dumps can appear while the run is healthy.** bug · 2026-07-31 · FL L894–909
- **B13. Mixed time zones: UTC filenames, local log lines, write-time not attempt-time.** design ·
  WH 12:52, 13:36, 02:55 · "it mis-led a third party" · WH "§12:52", "§13:36", "§02:55"
- **B14. `--dry-run` forges the dead-engine signature in `conductor.log`.** bug · 2026-08-04,
  2026-08-27, 2026-09-02, 2026-09-03, 2026-09-05 · "appends `conductor start`, the telegram notice
  and `stage → X`" · FL L1447–1506, L2347–2363, L2380–2441, L2600–2631
- **B15. The `--dry-run` epilogue reads like a real run ended.** bug (cosmetic) · FL L16–93
- **B16. The detached launch discarded stderr — the only place a compose refusal appears.** fixed
  by `--detach`'s capture log · FL L658–728, L2054–2079
- **B17. `status` says `running` while `resume` errors — both correct after a Face pause/resume.**
  doc · 2026-08-05 · FL L1507–1651

### C. Prompt size and the argv ceiling

- **C1. Compose-time park at 32,767; `## Conductor tools` is 44 % of the prompt; it fires after
  the stage has advanced.** design · WH 14:45 (2026-09-04); pdf2ooxml "fourth argv park in one run
  (M3 32,860 · T3 36,039 · T3 41,747 · P3 35,645)" 2026-09-07 · `composed argv 32,834 chars ·
  CreateProcess ceiling 32,767 · 67 OVER`; `14317 ## Conductor tools (44 %)` · open · WH "§14:45";
  FL L2668–2694
- **C2. `PromptBlockRenderer.RenderCard` renders `TaskContext` uncapped — `task --amend` leaks into
  the ceiling.** bug · 2026-09-03 12:00 · 30,059 → 34,208 after two ~2 KB amendments ·
  FL L2469–2509
- **C3. Doctor prescribes "pass the prompt on stdin"; the engine has no stdin path.** bug ·
  `DoctorCommand.PromptSemantics.cs:113`; `AgentSession.cs:129`, `:221` · FL L2469–2509
- **C4. A compose-time park burns every queued injection.** bug · 2026-09-07; WH 14:45 · "all eight
  items were sitting in the queue as `.done` — none of them had ever reached an agent" ·
  FL L2668–2694; WH "§14:45"
- **C5. `preflight`/`doctor` measure a smaller argv than the live spawn.** bug · preflight `22546`
  vs live `34,396` · FL L2469–2509, L2668–2694
- **C6. The 8,191 cmd.exe warning is noise on an `.EXE` shim but a real portability fault.** doc ·
  FL L2080–2105, L2339–2346, L2469–2509, L2600–2631
- **C7. `BatteryGroup.Render` truncates the concatenation — the ledger starves the open-bugs
  battery.** bug · 2026-08-21 · "Seven of forty-five prompts carried the bug list" · mitigated per
  plan (`ledgerMaxEntries: 3`, `maxBytes: 6144`) · FL L2132–2199
- **C8. A literal `{word}` in stage notes killed the engine 13 hours in.** fixed by SC3.3 ·
  FL L658–728, L1337–1446
- **C9. `templatesDir` is plan-dir-relative while `tracker`/`planDoc` are repo-relative; a miss
  falls back to built-ins silently.** design · 2026-08-03 · FL L1337–1446
- **C10. `--dry-run` on a run parked at a phase gate composes no session prompt.** doc · 2026-08-11
  · FL L1782–1870
- **C11. Stage-brief size is unbounded and drives the ceiling (G1 notes 4,805; `promptExtra`
  4,383).** design · WH "§14:45"

### D. Injections and the queue

- **D1. `inject` keeps only the first line of its argument (`.cmd` shim) — bug #75.** bug · WH
  23:36 (2026-09-03), 16:56 (2026-09-04) · "1,637-char steer queued as 384 chars with no warning"
  · `[repo git log: #75 in the round-3 fixed list]` · WH "§OVERNIGHT TUNING", "§16:56"
- **D2. An inject queued mid-session lands on the session after next; the boundary lasts ~5 s.**
  bottleneck · WH 04:50, 05:20, 05:40, 05:50, 09:05 (2026-09-04) · "second time this watch that a
  queued note went stale within minutes" · WH "§04:50"–"§09:05"
- **D3. `inject` has no `--cancel`.** missing · WH 2026-09-03
- **D4. A consumed inject is not re-delivered when the consuming session is killed.** design ·
  2026-08-13; 2026-09-07 · FL L1871–1904, L2668–2694
- **D5. `kill` PAUSES the run; injections are not consumed while paused; help text says
  otherwise.** bug (help) · 2026-09-07 · FL L2658–2667
- **D6. `goto <STAGE>` queued twice never applied; moving a parked run needs resolve → kill → skip
  → resume again.** bug · WH 10:02, 09:30 (2026-09-04) · WH "§10:02", "§09:30"
- **D7. `plan add-stage` cannot be fed from a PowerShell pipe (UTF-8 BOM).** bug · 2026-08-01 ·
  FL L1054–1111
- **D8. `--detail=-` needs the `=`; a bare `-` is parsed as an option.** bug · WH "§16:56"
- **D9. An old run's pending queue is consumed by a new plan's first session in the same tree.**
  design · 2026-09-05 · FL L2600–2631
- **D10. Injects carry the plan's authority and none of its review.** design · 2026-08-01 ·
  FL L1112–1171
- **D11. Injects go to whichever session spawns next — a Fix session swallowed a docs steer.**
  bottleneck · WH "§01:40"
- **D12. Non-ASCII in `bug new` titles is mangled on the console.** bug · FL L1013–1053

### E. Gates, batteries and artefacts

- **E1. A gate that passes on retry throws away the failed attempt's output — bug #86.** bug ·
  Charkh 2026-08-27; WH 13:15 · "`FAIL (exit 1) in 307s` then `PASS in 312s` … no test name" ·
  open · FL L2244–2338; WH "§13:15", "§13:00"
- **E2. SC4.1's unconditional retry had no per-gate knob.** `[repo git log: 5f8e48b "a per-gate
  retry knob"]` · FL L2510–2575; WH "§17:52"
- **E3. Switching `gatePolicy` perPhase → perSession while gating drops the pending phase gate.**
  bug · 2026-09-03 22:04 · `StageSelection.cs:202` · open · FL L2510–2575
- **E4. `perPhase` + a template that says "conductor runs the full battery after you exit" = five
  checkpoints of unseen regressions.** doc/template · FL L2510–2575
- **E5. The gate cache key is the anchor HEAD — sibling changes get a stale `CACHED` hit.** bug ·
  2026-07-29 · `watchPaths` mitigates · FL L195–227
- **E6. `journey` cannot show gate→stage binding — a scoping hole hid a defect.** missing ·
  FL L16–93, L1054–1111
- **E7. A rolled-over session's gates never run; the claim still flips.** design ·
  `SessionRunner.cs:406-417`; WH 06:52 "T2.1 recorded without a gate or an attempt" · FL L910–951,
  L1447–1506; WH "§06:52"
- **E8. Session gate logs retain only the tail (~50 lines).** bug · WH "§OVERNIGHT TUNING", "§01:40"
- **E9. The phase-gate output file is not written until the gate completes.** design · WH "§13:00",
  "§11:30"
- **E10. Sessions redirect ad-hoc runs into `.conductor/gate-output/`; 19 of 72 logs have no
  verdict.** design · WH "§12:58", "§13:15"
- **E11. The stall watchdog kills foreground-subprocess work; `SessionWatchdog.Remedy` names
  `--name` where the flag is `--purpose`.** design + doc · 2026-08-11, 2026-08-14 · FL L1782–1870,
  L1992–2028
- **E12. A headless agent that "waits for a notification" ends its session; its bg battery dies.**
  template · 2026-09-06 03:50 · FL L2632–2647
- **E13. `dirty` counts conductor's own tracker write on one run and not another.** `[repo git log:
  5f8e48b]` · WH "§13:50", "§13:58"
- **E14. Gate batteries from two runs are fratricidal; nothing serialises a gate against the other
  run's agent.** design · 2026-08-14 · FL L1963–1991, L2029–2053
- **E15. Gate timeouts are the only kill; contention can bust a sibling run's cap.** missing · WH
  "§13:00"
- **E16. Session verdict = fast tier, phase gate = full tier; the log does not say which "gates
  GREEN" means.** doc · WH "§05:20"
- **E17. The bookkeeping commit sweeps `REPORT.md` only, not evidence files.** design · WH "§04:52"
- **E18. "The verdict diffs the tracker table" is a myth that cost two watchers an alarm.** doc ·
  WH "§OVERNIGHT TUNING"
- **E19. The gate timing comparison line (`754s vs 761s when it last passed, -1%`) is a genuinely
  useful tell — keep.** WH "§17:52"
- **E20. A finished PR in a sibling repo can sit unmerged forever and no surface shows it.**
  missing · 2026-07-30 · FL L729–768

### F. Verdict, attempts and the circuit breaker

- **F1. `NoProgress` when work landed in a sibling repo.** fixed via `satelliteRepos`
  (`VerdictEngine.cs:363`) · FL L195–227, L1054–1111
- **F2. `commitCount` and `newly_done` are structurally 0 for every `RolledOver` session.** bug ·
  "32 RolledOver sessions recorded 0 commits, REAL 24" · open · FL L1222–1285, L1507–1651
- **F3. A `Progress` outcome never consumes an attempt — green-but-unclaimed churns to the cap.**
  design · era 3 T3 2026-09-07; era 1 P1.1 2026-08-27 (4 sessions / $111 at attempt 1/8) ·
  FL L2648–2657, L2364–2379
- **F4. A transient network cut escalates to `NEEDS HUMAN` in ten minutes.** design · 2026-08-05 ·
  `session #4 exited (code 1, 3m, $0.00)` → `#5 start … ONE SECOND later` → `circuit breaker:
  identical failure (AgentError x2)` → advisor burned 6 min → park · FL L1507–1651
- **F5. A machine reboot produces the same false park.** design · 2026-08-14 · exit `1073807364` /
  `-1073741502` read as AgentError ×2 · FL L1992–2028
- **F6. Advisor unset/unreachable → deterministic default → human park, after a hang-to-timeout.**
  design · FL L311–427, L1871–1904
- **F7. `retry-stage` on a fully-DONE stage re-runs the phase gate directly — undocumented and
  useful.** doc · FL L1992–2028
- **F8. Attempts = `sessions × 2`; the counter resets on a claim; stages addressed by array index
  only.** doc/missing · WH "§OVERNIGHT TUNING", "§08:25", "§16:56"
- **F9. A Fix session with an empty failure list was told "gates came back RED".** fixed in the
  skill template · FL L195–262
- **F10. An agent's own WMI process sweep can kill its own session.** design (agent-side) ·
  FL L816–832
- **F11. A killed session's uncommitted tree can be half-written; resume adopts it blind.** doc ·
  FL L1992–2028

### G. Budget, approve, plan set/reload and token caps

- **G1. `approve` on a budget park RESETS the counter.** design · 2026-07-29 19:03 ·
  `ApprovalOutcome.ResetBudgetAndResume` (`VerdictEngine.Phase.cs:181-190`) · `/state` gained
  `budgetApprovals`, `windowCostUsd`, `lifetimeCostUsd` · FL L428–501, L952–982
- **G2. The budget check runs BEFORE a queued reload is consumed (3 s).** bug · `CheckBudgetCap`
  post-verdict, `ConsumeReloadPending()` at loop top (`RunLoop.cs:107`) · FL L428–501
- **G3. `ApplyPlanReload` un-parks only a session-cap park and says nothing when it declines.** bug
  · `RunLoop.cs:456-467` · FL L428–501
- **G4. `plan set` accepts a non-existent single-segment key and creates it at the root.** bug ·
  `PlanSetCommand.cs:42-66` · FL L311–427
- **G5. `plan set` did not reach a running engine.** fixed — auto-queues `reload-plan` when it can
  match a live pid · FL L311–427, L910–951, L1172–1221, L2510–2575
- **G6. `plan set` destroys JSONC comments, adds a BOM, materialises 27 defaults, writes a `.bak`
  inside the repo.** bug · 2026-08-01 · FL L1172–1221
- **G7. A live plan-file edit auto-reloads and `WorkGraphSync` regenerates the tracker with a STALE
  handoff block.** bug · 2026-08-01 · FL L1054–1111
- **G8. `restored budget: $0.00` — session cost is never persisted on the `--once` exit path.** bug
  · 2026-07-31, 2026-08-11 · FL L769–815, L1782–1870
- **G9. `maxSessionTokens` counts cache reads (~40× context); `RolloverCommand` help says "e.g.
  200000".** bug (help) · FL L910–951, L1782–1870, L2407–2441
- **G10. A token ceiling is only valid for the model and plan it was measured on.** partly addressed
  by `doctor`'s `tokens` line · FL L1507–1651, L1782–1870, L1447–1506
- **G11. The soft-break nudge.** fixed by B13.3 (PostToolUse hook); WH 12:01 shows `delivered x7,
  OBEYED` against "exactly once" · FL L910–951, L1507–1651; WH "§12:01"
- **G12. `owner approved` in the log does not imply a budget reset.** doc · FL L952–982
- **G13. `approve` on an unparked run is a no-op logged as `ResumeRun`.** doc · WH "§OVERNIGHT
  TUNING, 23:36"
- **G14. A stage with `ownerGate: true` parks twice, with different clearers.** doc · FL L952–982
- **G15. Per-session cost variance is 4×.** doc · FL L94–124
- **G16. A `plan set` while a control is queued writes the file but queues no second reload.** doc ·
  WH "§OVERNIGHT TUNING"
- **G17. Phase-gate cost grows every stage; no rail budgets gate time separately.** missing · WH
  "§11:30"

### H. Rate limits and provider failures

- **H1. The account 5-hour limit (429) reads as `AgentError` and burns a stage's attempt budget in
  three minutes; the advisor also 429s → `NEEDS HUMAN`.** bug · 2026-08-13 21:34 · "burned attempts
  2→8 of E1 and desktop 5→6 of N2" · FL L1871–1904
- **H2. The detector misses a `status: rejected` five-hour window.** bug · Takhteh #51 2026-09-04 ·
  `rate_limit_event … "status":"rejected","rateLimitType":"five_hour","overageStatus":"rejected"` →
  `AgentError — queuing fix session (attempt 3/6)` · FL L2576–2599; WH "§21:00"
- **H3. A 529 is mislabelled "usage limit detected".** bug · sessions #16–#24 at $0.00 · WH "§Still
  true from before"
- **H4. Two parallel opus runs consume a 5-hour window in ~3h20m.** doc · FL L1871–1904
- **H5. A rolled-over session has no cost envelope; `code -1` looks like a crash.** design · WH
  "§06:52"; FL L1652–1670

### I. Owner queue and parks

- **I1. The park matcher is a substring test; the queue is line-anchored.** bug ·
  `ProgressConventions.cs:59` `Contains` vs `OwnerQueue.cs:98-99` `StartsWith`; the built-in
  templates teach the token · engine mismatch not fixed · FL L1013–1053, L1172–1221, L1286–1336,
  L2080–2105
- **I2. `resume` never clears a `HUMAN:` park — re-learned five weeks apart.** doc · FL L228–262;
  WH "§09:30"
- **I3. A park was cleared by something else within ~60–86 s; a queued `resume` is invisible.**
  unknown · 2026-07-31 · FL L833–854
- **I4. A BLOCKED checkpoint makes its stage unclosable forever, with no tell.** design ·
  `TrackerParser.cs:50` · WH "the run ends at 35/36 with a permanent record saying the staging
  deploy was blocked by Cloudflare, which is false" · FL L983–1012; WH "§~~OPEN~~"
- **I5. Eight parks on one question — the same `HUMAN:` line survives sessions verbatim.** plan
  gap · FL L1054–1111
- **I6. Deferred decisions need a home outside the handoff.** `task --amend` landed 2026-09-03 ·
  FL L174–194, L1013–1053, L2469–2509
- **I7. `ownerGate: true` was absent from the skill's plan template.** doc · FL L855–893
- **I8. Unblocking a BLOCKED card is a live run-state write with no verb short of `task --done`.**
  bottleneck · WH "§~~OPEN~~"

### J. State store, history, status counters and git bookkeeping

- **J1. A run whose engine is killed stays `running` forever; `run close` now exists but refuses an
  Aborted row.** partly landed · FL L1682–1708, L2600–2631
- **J2. `.conductor/run.db` in the repo is the pre-import decoy; bug #46 lost karvan's rows.**
  design · FL L2200–2243
- **J3. `state-pointer.json` pins a new plan to the old run's graph; a paused engine holds the lock
  and calls a different plan "this plan".** design · 2026-09-05 · FL L2600–2631
- **J4. The GitHub mirror retires another run's checkpoints on every era transition.** bug #84 ·
  `GithubBoardSync.cs:204-227` · fixed at CH4.3 `[repo]` · FL L2244–2338
- **J5. The P4 squash rewound a branch 28 commits via a defensive `rebase --abort` of a stale rebase
  and read it as success.** bug (severe) · 2026-08-14 00:55 · FL L1905–1944
- **J6. The squash after a mid-stage push guarantees a non-fast-forward reject; every squash seeds
  the merge that makes the next one refuse.** design · 2026-08-14; WH 06:25, 06:52, 12:50 ·
  FL L2029–2053, L1671–1681; WH "§06:25", "§06:52", "§12:50"
- **J7. `report push failed:` logs with an empty reason.** bug · FL L2029–2053, L2054–2079
- **J8. Squash failures once logged with no exit code; later builds self-announce.** partly improved
  · FL L311–427, L502–531, L1671–1681
- **J9. The final squash runs before the last report write, so a completed run always ends dirty.**
  bug · FL L894–909, L2080–2105
- **J10. `face` keeps running after a completed run and holds the exes; `install.ps1` fails
  MSB3027.** bottleneck · FL L855–893, L894–909, L1507–1651, L2080–2105
- **J11. The GitHub mirror token is the launcher's to provide; the edge run's record died to `no
  token`.** doc · FL L2200–2243
- **J12. Verdict commit counts inflated by merges (`commits 24 (+11 conductor bookkeeping)`).**
  cosmetic · WH "§06:30"
- **J13. A follow-on plan inherits the bug ledger (pointer outranks slug) — undocumented.** doc ·
  FL L2132–2199
- **J14. `task --list` prints `No run found` before a run exists.** minor · FL L2407–2441
- **J15. Sessions of one run wrote files into another run's repo.** design · WH "§14:12"

### K. Two runs on one box

- **K1. Two engines launched with `--port 4317`; the second silently took 4318.** bug · WH "§13:15"
- **K2. Battery contention makes durations unquotable and failures ambiguous; one run's timeout can
  be busted by the other.** missing · WH "§12:30"–"§14:38"
- **K3. Gate fratricide and delivery-session interference.** see E14
- **K4. Publishing a rebuild over a live run breaks someone else's run.** doc · FL L1507–1651,
  L1709–1781
- **K5. Shared token / getUpdates fight (A3); shared 5-hour window (H4).**
- **K6. The other run's `claude`/`node` processes look like leaks.** doc · WH "§00:45"
- **K7. Machine sleep would freeze an unattended run; no preflight for it.** missing · WH
  "§OVERNIGHT TUNING"
- **K8. Free disk: conductor watches nothing about it.** missing · FL L2510–2575

### L. Plan authoring and doctor

- **L1. A stage id must be letters-then-digits; `K.1` rows never parse and `doctor` only warns.**
  bug (severity) · FL L1337–1446
- **L2. Auth preflight is inconclusive by design.** design · FL L16–93
- **L3. No version verb.** fixed (`doctor`'s `update` line; installer prints old→new) · FL L16–93,
  L1709–1781, L2080–2105
- **L4. `doctor` cannot see stall-shaped stages, a dead `templatesDir`, battery truncation, or a
  plan's disk rule.** doc · FL L1782–1870, L1337–1446, L2132–2199, L1507–1651
- **L5. `doctor`'s `ci-battery ✗` is advisory, not a blocker.** doc · FL L2380–2406, L2458–2468
- **L6. `kind: "review"` stages' completion semantics are undocumented.** doc · FL L2458–2468
- **L7. `journey`'s Model column truncates.** cosmetic · FL L1447–1506
- **L8. Added stages keep a bare `### E` heading until a later sync.** cosmetic · FL L1054–1111
- **L9. The tracker's role as checkpoint source was undocumented.** doc · FL L16–93, L276–310
- **L10. Unreachable acceptance floors are a budget sink with no rail.** see F3 · FL L2364–2379,
  L2510–2575; WH "§17:52"
- **L11. `preflight` promises "nothing created under `.conductor/`"; `--dry-run` does not.** see B14
- **L12. Multi-repo: sibling PR queues (E20) and the gate cache key (E5) remain.**

### M. Docs, skills, help text and templates

- **M1. Stale help/doc strings inside the engine:** `PlanSetCommand.cs:11`, `SessionWatchdog.Remedy`
  (`--name`), `RolloverCommand` ("e.g. 200000"), `kill --yes` ("the loop then re-evaluates"),
  doctor's stdin prescription · FL L311–427, L1782–1870, L910–951, L2658–2667, L2469–2509
- **M2. The skill's tracker template legend sat inside the scanned handoff block.** fixed
  2026-08-02 · FL L1286–1336
- **M3. The field log itself was nearly lost.** FL L311–427, L1286–1336
- **M4. `reference/recovery` said a post-reboot `conductor run` resumes a dead conversation —
  wrong.** FL L1992–2028
- **M5. Templates must say how to wait (inside a tool call) and that amendments accumulate.**
  FL L2632–2647, L2469–2509, L2510–2575
- **M6. Quirks-table entries age faster than the engine; "re-measure before acting" paid four
  times.** FL L311–427, L1337–1446, L1507–1651, L1709–1781

### N. Other

- **N1. User-scope MCP servers did not reach sessions.** fixed (`K1.4`, 0.4.0+1a554372eb74) ·
  FL L94–124, L1709–1781
- **N2. Engine killed as a harness background task.** fixed by `--detach` · FL L125–159, L263–275,
  L2200–2243, L2458–2468
- **N3. `session-NNN.prompt.md` is written even for a compose that parks — keep.** WH "§14:45";
  FL L2668–2694
- **N4. Watcher edits to a tracked plan file show as `dirty YES` until a bookkeeping commit.** WH
  "§16:56", "§17:20"
- **N5. `Select-Object -First N` on a conductor pipe returns 255 — PowerShell, not conductor.**
  FL L428–501, L855–893
- **N6. `conductor` is not on Git Bash's PATH.** FL L1054–1111, L2407–2441
- **N7. The interrupted-run rail is excellent — confirmed three times.** FL L125–159, L1992–2028,
  L2200–2243
- **N8. Progress-verdict honesty (`gates green (none configured)`, `kit:cached`) is what lets a
  watcher audit from the log — keep.** FL L94–124, L160–173

### The five that cost the most, in the log's own accounting

1. **Attempts and breakers burned by things that are not the agent's fault** — 429s, 529s, DNS
   loss, reboots, self-kills; every one `code 1, 0m, $0.00`; the advisor fails over the same outage.
   Two runs down 2026-08-13; a park 2026-08-05; two of Z1's six attempts 2026-09-04. (F4–F6, H1–H3)
2. **The argv ceiling** — five parks across two runs, each at a stage boundary after the stage had
   advanced, each eating the queued injections. (C1–C6)
3. **The `HUMAN:` token park** — substring vs line-anchored, templates that teach it, a hot loop
   that paged the owner 200 times under `--dry-run`, `resume` that never clears it. (A1, I1–I3)
4. **Evidence conductor throws away** — retry-pass gate output, tail-only logs, withheld phase-gate
   output, zero `commitCount` on rollover, empty push-failure reasons, stderr outside the log.
   (E1, E8, E9, F2, J7, B16)
5. **Two runs on one machine with no coordination primitive** — port fallback, shared token,
   courier deaths, gate fratricide, unquotable batteries, cross-repo writes, no `ps`. (K1–K8, A2–A3,
   B3–B4, B8)

### Already fixed, per the log — do not re-plan

`conductor run --detach` (2026-08-14, with stderr capture); `satelliteRepos` in the progress test;
brace tokens outside templates (SC3.3) and `doctor`'s template/token sweeps; `plan set`
auto-queuing `reload-plan`; the soft-break hook (B13.3); operator MCP servers inherited (`K1.4`);
version visibility (`doctor`'s `update` line, the installer's old→new); `doctor`'s `tokens` line and
`budget --json`; `/state`'s budget fields; `task --amend`; `run close`; `preflight`; `courier
status/restart/allow`; the squash's self-announced refusal; the skill-side filter and template
fixes. From this repo's commits since: bug #75 (`inject` first line), the per-gate retry knob, the
dirty input ignoring conductor's own writes, bug #84 (the retire sweep).
