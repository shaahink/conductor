# Peyk courier - the courier stands on its own Phase Tracker

**Plan:** Peyk courier - the courier stands on its own | **Branch:** `feat/peyk-courier` | **Design doc:** docs/dev/NEXT-ERA-FINDINGS-2026-09-17.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: s8 claimed PK4.1 (evidence pk4.1.md, rig 16/16) and PK4.2 (evidence pk4.2.md, rig 3/3 via HarnessTests.Cards.cs, neighbours 596/596). PK4.3 IN PROGRESS, half done - budget nudge.
  PK4.3 landed (unwired, builds): src/Conductor.Core/RoomVoiceBattery.cs (character + room voice, fair split of its 4096 cap, clip says so; ReadVoice/EmbeddedCharacter public), docs/rooms/character.md embedded in Conductor.Core.csproj as Conductor.Core.Rooms.character.md, PlanConfig.ChatPairs() (neutral (id, profile) pairs so Core callers never name Telegram*).
  PK4.3 NEXT, in order: (1) wire in PromptBuilder.BatterySection after HeldWordsBattery: `if (_plan.Repo is {Length:>0} r && Courier.Rooms.Resolve(r, _plan.ChatPairs(), Courier.CourierSettings.Load()) is {} room)` add RoomVoiceBattery when !IsEmpty - then RUN prompt/golden tests + ArchitectureTests: the courier.json fallback (one observer chat on the machine) turns the battery on for every repo, check no test state home carries one. (2) RoomVoiceBattery unit tests with a FIXTURE voice (never the owner's). (3) SF7_1 pin: the cli.md `task` row names `--tell` and the verdict; cli.md/operating.md never tell a session to run report.ps1. (4) dry-run proof: fresh build `run --dry-run -p <scratch plan>` with CONDUCTOR_STATE_HOME scratch + a room with observer + fixture voice - the prompt carries `### room-voice` within batteries.maxBytes. (5) delete ~/.claude/skills/telegram-notify/report.ps1 and drop its mentions from that SKILL.md. (6) rewrite rule 11 in C:/Code/bg/plans/conductor.plan.json AND conductor.feel.plan.json, rule 17 in C:/Code/BookToCourse/conductor.hardening.plan.json to `task --done --tell "<title> | <sentences>"` (engine posts at the verdict, red posts nothing and the words are held, before/after = <id>-before.png/<id>-after.png in the watched evidence dir, never post a card yourself, say --to observer for findings, and: if the engine refuses --tell claim without it). Raw string replace of the JSON-escaped rule, json.loads to verify, commit nothing there; `conductor ps` showed no run live in those repos at 14:25Z. BookToCourse rule 17 also cites session.md step 5 (not a plan file - name it in the handoff); bg keeps an in-repo skill copy with its own report.ps1 (leave it).
  PK4.1/PK4.2 facts: a stage is CONFIRMED only under gatePolicy perPhase; the composer must FlushEvents before reading; ArchitectureTests = 3 types/file and RunLoop.cs AT 500 lines; KS11_1SeamBoundary forbids Telegram* in new Core files. The owner's real courier home has no rooms yet - `conductor room import` after the reinstall; bg has no room (its chats live in the repo's .claude copy) - `room add --repo C:/Code/bg --observer <id>`.
  PK2 (still true): real 'Conductor Courier' ARMED 2026-09-17T11:37:30Z, protocol 2; bug #93 OPEN for PK6.1 (`tools/peyk/pk2-3-arm-real-courier.ps1 -ReadOnly`).
next: PK4.3 steps (1)-(6) above, then claim with a pk4.3.md evidence. Do not restart, stop or reinstall the armed courier.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 17 |
| Done | 5 |
| Claimed (unconfirmed) | 3 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### PK1 — Its own process

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PK1.1 | src/Conductor.Courier is its own project, referencing Conductor.Core and nothing else, building conductor-courier.exe; conductor courier run execs it; ArchitectureBoundaryTests carries the rule and names a seeded violation | DONE ✓ | ba634a8 | .conductor/evidence/PK1/pk1.1-fix-seam-boundary-tests.log |
| PK1.2 | tools/install.ps1 publishes both binaries and no longer stops the courier to publish the engine; proven against a scratch install path with a scratch courier live (no restart in its log); release preflight green on the courier check | DONE ✓ | ba634a8 | .conductor/evidence/PK1/pk1.2-live-proof.log |

### PK2 — Alive, or known dead

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PK2.1 | Heartbeat in the presence record; courier status prints alive / stale (last poll N min ago) / dead (last seen T); a presence file found at startup produces a journaled death record carrying the scheduler's last-run result. A scratch courier killed with Stop-Process reads dead with a time by the next status, and the next start logs the record | DONE ✓ | 4affb95 | .conductor/evidence/PK2/pk2.1-live-proof.log |
| PK2.2 | ProcessExit and unhandled-exception journaling; the keep-alive calendar trigger (every five minutes, IgnoreNew) in the task XML, proven by registering a scratch task and measuring the restart of a courier that exited 0; a run restarts a stale courier at the session boundary and says so in the log and the owner queue | DONE ✓ | 4affb95 | .conductor/evidence/PK2/pk2.2-live-proof.log |
| PK2.3 | The instruments armed on the real courier: install.ps1 -CourierOnly publishes conductor-courier.exe alone and re-registers the task with the keep-alive trigger (conductor.exe untouched); alive with a heartbeat, the task XML read back, one protocol-2 push landed; the arming time recorded for PK6.1's read-out | DONE ✓ | 4affb95 | .conductor/evidence/PK2/pk2.3-arming.log |

### PK3 — One transport

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PK3.1 | Protocol 3: POST /send (text or file, replyTo, chat by id or room profile, parse mode, media group), POST /react, POST /delete, GET /chats; every send returns the Telegram message id; messages.jsonl in the courier home; /push (protocol 2) still accepted. One real send to the admin DM, then deleted, both ids in the evidence | DONE | 0d43747 | .conductor/evidence/PK3/pk3.1.md |
| PK3.2 | conductor say with every switch in D5; the direct fallback when the courier is unreachable (environment token, log line, channel-health line naming the path); Telegram's ceilings refused by name. say --dry-run prints the exact bytes and the resolved chat; with the scratch courier stopped a send lands and the log reads sent directly | DONE | 0d43747 | .conductor/evidence/PK3/pk3.2-fix-completion.log |
| PK3.3 | A live run names its own project to the courier: /hello carries repo path and plan name and the allowlist entry is added marked by run; courier allow unchanged. On a rig a fresh plan name files an inbound note on the first boundary without courier allow | DONE | 0d43747 | .conductor/evidence/PK3/pk3.3.md |

### PK4 — Rooms and the card

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PK4.1 | rooms/<slug>.json in the courier home; conductor room add / show / list; the one-time import of the private config files; docs/rooms/character.md in the tree; the voice file only ever pointed at. room list shows the two migrated rooms; an old-shape plan on a room-less machine replays byte-identically | TODO | - | - |
| PK4.2 | conductor task --done --tell stores the session's words; the engine composes the checkpoint card at the verdict and pushes it to the room's observer chat; a red claim posts nothing and the next prompt carries the held words; the card is a NotifyTemplate; stages[].deploys; a stage confirm posts a stage card. Proven on a rig with a fake agent | TODO | - | - |
| PK4.3 | RoomVoiceBattery under the byte cap; report.ps1 deleted from the shared skill; the three field plans' report.ps1 rule rewritten to --tell (their plan files only, nothing committed there); SF7_1DocsMatchRealityTests pins --tell in docs/cli.md | TODO | - | - |

### PK5 — Inbound with a name

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PK5.1 | InboxNote carries MessageId, SenderId, SenderName, SenderUsername; the ack is a reaction, never a message; inbox list shows sender and id; say --reply-to takes a note id or a message id; walk-ids.ps1 deleted; the no-authority rule in the note file's header. An old note without the fields still lists | TODO | - | - |
| PK5.2 | The figure verbs (/progress, /money, /tokens, /status, /evidence) answered by the courier from the project's newest run.db, read-only, whether or not a run is live; a test asserts no store write on that path; the in-run handlers stay for a courier-less machine | TODO | - | - |

### PK6 — The docs, the skill, the close

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PK6.1 | The cause: the read-out of PK2.3's window (at least 24 hours, both timestamps) - a dated finding naming the first recorded exit path, or a dated statement that none occurred with the instruments listed; the reading procedure in docs/operating.md so a later death is read the same way; bug #93 closed on it | TODO | - | - |
| PK6.2 | docs/cli.md, operating.md, plan-config.md and ARCHITECTURE.md reconciled (the courier section rewritten for a separate binary, seams re-counted); ADR-0009 amending ADR-0008 for D3, D4 and D5; the docs battery green with a negative control per new verb and key | TODO | - | - |
| PK6.3 | The telegram-notify skill rewritten to two pages around conductor say and --tell; send.ps1, walk-ids.ps1 and lib/ deleted; watch-live re-pointed; a grep of the skill folder finds no Bot API URL; one real post through say from outside any run, then deleted | TODO | - | - |
| PK6.4 | The close through the machinery: release preflight, the mechanical acts performed, the CHANGELOG section written, the era's numbers measured against a backup copy of the store; the owner's acts printed and parked; the plan doc left in place for plan B | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
