# Peyk courier - the courier stands on its own Phase Tracker

**Plan:** Peyk courier - the courier stands on its own | **Branch:** `feat/peyk-courier` | **Design doc:** docs/dev/NEXT-ERA-FINDINGS-2026-09-17.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: s4 PK2.1 CLAIMED (4affb95 code, 1334051 rig, f1c1a31 evidence; rig tools/peyk/pk2-1-live-proof.ps1 18/18). courier.run.json carries lastPollUtc, beaten every loop iteration incl. failed polls (CourierDaemon `beat` hook, written by CourierProgram). CourierVitals (Core/Courier) = absent/alive/stale/dead/unmetered; stale after 2*(65s + max(60s, interval)) = 250s at 4s. `courier status` prints a `life:` line and a json `vitals` block; when dead/stale it also reads the task's last run. Startup journals `previous courier pid N died silently; last poll T; task last-run result R` (CourierProgram.DeathRecordAsync, CourierTask.LastRunAsync reads /V CSV cols 5-6 by position). Bug #97 fixed+closed (engine = EngineStamp.Current.Full).
  MEASURED: while a task instance runs, schtasks Last Result = 267009 (0x41301). A courier restarted BY ITS OWN TASK finds its predecessor's exit code already overwritten; the text says so. PK2.2's boundary restart must read LastRunAsync BEFORE schtasks /Run and carry it in its log/owner-queue line.
  Rig trick that works: a scratch scheduled task whose action is `cmd /c exit 3` gives a real scheduler result without ever starting a courier under the machine's env.
  Rule from PK1: anything courier-side that names the messenger, even in a string, goes in TelegramCourierSource.cs (KS11_1 ratchet scans Core strings).
  For PK2.3: the real task still runs v0.5.0 `<install>\conductor.exe courier run`; `tools/install.ps1 -CourierOnly` (PK1.2) is the arming act. A pre-PK2.1 courier reads `unmetered` in the new status.
next: PK2.2 - ProcessExit + unhandled-exception journaling in CourierProgram; keep-alive trigger (5-min repeating calendar trigger, IgnoreNew) in CourierTask.BuildXml proven by registering a scratch --task-name and reading back schtasks /query /xml; the run-boundary stale check that starts the task and queues `courier restarted by this run, Nth time`. Read D2(c-e) and the PK2 row.

## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 17 |
| Done | 0 |
| Claimed (unconfirmed) | 2 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### PK1 — Its own process

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PK1.1 | src/Conductor.Courier is its own project, referencing Conductor.Core and nothing else, building conductor-courier.exe; conductor courier run execs it; ArchitectureBoundaryTests carries the rule and names a seeded violation | DONE | ba634a8 | .conductor/evidence/PK1/pk1.1-fix-seam-boundary-tests.log |
| PK1.2 | tools/install.ps1 publishes both binaries and no longer stops the courier to publish the engine; proven against a scratch install path with a scratch courier live (no restart in its log); release preflight green on the courier check | DONE | ba634a8 | .conductor/evidence/PK1/pk1.2-live-proof.log |

### PK2 — Alive, or known dead

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PK2.1 | Heartbeat in the presence record; courier status prints alive / stale (last poll N min ago) / dead (last seen T); a presence file found at startup produces a journaled death record carrying the scheduler's last-run result. A scratch courier killed with Stop-Process reads dead with a time by the next status, and the next start logs the record | TODO | - | - |
| PK2.2 | ProcessExit and unhandled-exception journaling; the keep-alive calendar trigger (every five minutes, IgnoreNew) in the task XML, proven by registering a scratch task and measuring the restart of a courier that exited 0; a run restarts a stale courier at the session boundary and says so in the log and the owner queue | TODO | - | - |
| PK2.3 | The instruments armed on the real courier: install.ps1 -CourierOnly publishes conductor-courier.exe alone and re-registers the task with the keep-alive trigger (conductor.exe untouched); alive with a heartbeat, the task XML read back, one protocol-2 push landed; the arming time recorded for PK6.1's read-out | TODO | - | - |

### PK3 — One transport

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PK3.1 | Protocol 3: POST /send (text or file, replyTo, chat by id or room profile, parse mode, media group), POST /react, POST /delete, GET /chats; every send returns the Telegram message id; messages.jsonl in the courier home; /push (protocol 2) still accepted. One real send to the admin DM, then deleted, both ids in the evidence | TODO | - | - |
| PK3.2 | conductor say with every switch in D5; the direct fallback when the courier is unreachable (environment token, log line, channel-health line naming the path); Telegram's ceilings refused by name. say --dry-run prints the exact bytes and the resolved chat; with the scratch courier stopped a send lands and the log reads sent directly | TODO | - | - |
| PK3.3 | A live run names its own project to the courier: /hello carries repo path and plan name and the allowlist entry is added marked by run; courier allow unchanged. On a rig a fresh plan name files an inbound note on the first boundary without courier allow | TODO | - | - |

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
