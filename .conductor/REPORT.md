# Conductor — Peyk courier - the courier stands on its own run report

_Updated 2026-09-17 14:28 UTC · branch `feat/peyk-courier` · HEAD `705f9e5`_

**Status:** Idle
**Stage:** PK4 — Rooms and the card · attempts used 0 · working ▸ PK4.3
**Checkpoints:** 10/17 done · **Sessions run:** 8 · **Cost:** $102.2190 (agent $102.1587 + gates $0.0603) · **Tokens:** 1,530,887 in / 747,763 out
**Confirmed phases:** PK1, PK2, PK3
**Channels:** telegram ready · github ready · courier ready
**CI battery:** ci-battery DEGRADED · ci-verdict DEGRADED
**⚠ CI DEGRADED — ci-battery:** CI runs 'powershell tools/gates/ratchet.ps1' that this run's gates do not - a checkpoint can pass one battery and fail the other · fix: add 'powershell tools/gates/ratchet.ps1' to plan.gates, or drop it from ci.yml. 
**⚠ CI DEGRADED — ci-verdict:** CI has no verdict for 705f9e5, the commit this run is on: CI's newest run is for b125405 - a branch reads green when the workflow that would have failed never ran on this head · fix: push the commit, or re-ask once CI has run: conductor github ci

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| PK1 | Its own process | ██████████ 2/2 | confirmed ✓ |
| PK2 | Alive, or known dead | ██████████ 3/3 | confirmed ✓ |
| PK3 | One transport | ██████████ 3/3 | confirmed ✓ |
| PK4 | Rooms and the card | ███████░░░ 2/3 | **← active** |
| PK5 | Inbound with a name | ░░░░░░░░░░ 0/2 | todo |
| PK6 | The docs, the skill, the close | ░░░░░░░░░░ 0/4 | todo |

<details> ✅<summary>PK1 — Its own process (2/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK1.1 | src/Conductor.Courier is its own project, referencing Conductor.Core and nothing else, building conductor-courier.exe; conductor courier run execs it; ArchitectureBoundaryTests carries the rule and names a seeded violation | ✅ DONE | [`ba634a8`](https://github.com/shaahink/conductor/commit/ba634a8) |
| PK1.2 | tools/install.ps1 publishes both binaries and no longer stops the courier to publish the engine; proven against a scratch install path with a scratch courier live (no restart in its log); release preflight green on the courier check | ✅ DONE | [`ba634a8`](https://github.com/shaahink/conductor/commit/ba634a8) |

</details>

<details> ✅<summary>PK2 — Alive, or known dead (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK2.1 | Heartbeat in the presence record; courier status prints alive / stale (last poll N min ago) / dead (last seen T); a presence file found at startup produces a journaled death record carrying the scheduler's last-run result. A scratch courier killed with Stop-Process reads dead with a time by the next status, and the next start logs the record | ✅ DONE | [`4affb95`](https://github.com/shaahink/conductor/commit/4affb95) |
| PK2.2 | ProcessExit and unhandled-exception journaling; the keep-alive calendar trigger (every five minutes, IgnoreNew) in the task XML, proven by registering a scratch task and measuring the restart of a courier that exited 0; a run restarts a stale courier at the session boundary and says so in the log and the owner queue | ✅ DONE | [`4affb95`](https://github.com/shaahink/conductor/commit/4affb95) |
| PK2.3 | The instruments armed on the real courier: install.ps1 -CourierOnly publishes conductor-courier.exe alone and re-registers the task with the keep-alive trigger (conductor.exe untouched); alive with a heartbeat, the task XML read back, one protocol-2 push landed; the arming time recorded for PK6.1's read-out | ✅ DONE | [`4affb95`](https://github.com/shaahink/conductor/commit/4affb95) |

</details>

<details> ✅<summary>PK3 — One transport (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK3.1 | Protocol 3: POST /send (text or file, replyTo, chat by id or room profile, parse mode, media group), POST /react, POST /delete, GET /chats; every send returns the Telegram message id; messages.jsonl in the courier home; /push (protocol 2) still accepted. One real send to the admin DM, then deleted, both ids in the evidence | ✅ DONE | [`0d43747`](https://github.com/shaahink/conductor/commit/0d43747) |
| PK3.2 | conductor say with every switch in D5; the direct fallback when the courier is unreachable (environment token, log line, channel-health line naming the path); Telegram's ceilings refused by name. say --dry-run prints the exact bytes and the resolved chat; with the scratch courier stopped a send lands and the log reads sent directly | ✅ DONE | [`0d43747`](https://github.com/shaahink/conductor/commit/0d43747) |
| PK3.3 | A live run names its own project to the courier: /hello carries repo path and plan name and the allowlist entry is added marked by run; courier allow unchanged. On a rig a fresh plan name files an inbound note on the first boundary without courier allow | ✅ DONE | [`0d43747`](https://github.com/shaahink/conductor/commit/0d43747) |

</details>

<details><summary>PK4 — Rooms and the card (2/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK4.1 | rooms/<slug>.json in the courier home; conductor room add / show / list; the one-time import of the private config files; docs/rooms/character.md in the tree; the voice file only ever pointed at. room list shows the two migrated rooms; an old-shape plan on a room-less machine replays byte-identically | ✅ DONE | - |
| PK4.2 | conductor task --done --tell stores the session's words; the engine composes the checkpoint card at the verdict and pushes it to the room's observer chat; a red claim posts nothing and the next prompt carries the held words; the card is a NotifyTemplate; stages[].deploys; a stage confirm posts a stage card. Proven on a rig with a fake agent | ✅ DONE | - |
| PK4.3 | RoomVoiceBattery under the byte cap; report.ps1 deleted from the shared skill; the three field plans' report.ps1 rule rewritten to --tell (their plan files only, nothing committed there); SF7_1DocsMatchRealityTests pins --tell in docs/cli.md | 🔄 IN PROGRESS | - |

</details>

<details><summary>PK5 — Inbound with a name (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK5.1 | InboxNote carries MessageId, SenderId, SenderName, SenderUsername; the ack is a reaction, never a message; inbox list shows sender and id; say --reply-to takes a note id or a message id; walk-ids.ps1 deleted; the no-authority rule in the note file's header. An old note without the fields still lists | ⬜ TODO | - |
| PK5.2 | The figure verbs (/progress, /money, /tokens, /status, /evidence) answered by the courier from the project's newest run.db, read-only, whether or not a run is live; a test asserts no store write on that path; the in-run handlers stay for a courier-less machine | ⬜ TODO | - |

</details>

<details><summary>PK6 — The docs, the skill, the close (0/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK6.1 | The cause: the read-out of PK2.3's window (at least 24 hours, both timestamps) - a dated finding naming the first recorded exit path, or a dated statement that none occurred with the instruments listed; the reading procedure in docs/operating.md so a later death is read the same way; bug #93 closed on it | ⬜ TODO | - |
| PK6.2 | docs/cli.md, operating.md, plan-config.md and ARCHITECTURE.md reconciled (the courier section rewritten for a separate binary, seams re-counted); ADR-0009 amending ADR-0008 for D3, D4 and D5; the docs battery green with a negative control per new verb and key | ⬜ TODO | - |
| PK6.3 | The telegram-notify skill rewritten to two pages around conductor say and --tell; send.ps1, walk-ids.ps1 and lib/ deleted; watch-live re-pointed; a grep of the skill folder finds no Bot API URL; one real post through say from outside any run, then deleted | ⬜ TODO | - |
| PK6.4 | The close through the machinery: release preflight, the mechanical acts performed, the CHANGELOG section written, the era's numbers measured against a backup copy of the store; the owner's acts printed and parked; the plan doc left in place for plan B | ⬜ TODO | - |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | PK1 | Deliver | 1 | 09-17 09:31 | 0:51 | Advanced | PK1.1 PK1.2 | 11 | engine-fast:OK · face-fast:OK | $14.8693 | $0.0073 | 282,815/146,031 |
| 2 | PK1 | Fix | 2 | 09-17 10:33 | 0:03 | Progress |  | 1 | engine-fast:OK · face-fast:OK | $1.3467 | $0.0068 | 51,258/11,346 |
| 3 | PK2 | Deliver | 1 | 09-17 10:42 | … | running |  | 0 |  |  |  |  |
| 4 | PK2 | Deliver | 1 | 09-17 10:50 | 0:51 | Advanced | PK2.1 PK2.2 PK2.3 | 11 | engine-fast:OK · face-fast:OK | $21.2274 | $0.0160 | 331,016/162,994 |
| 5 | PK2 | Fix | 2 | 09-17 11:59 | 0:04 | Progress |  | 1 | engine-fast:OK · face-fast:OK | $1.6869 | $0.0078 | 57,960/14,558 |
| 6 | PK3 | Deliver | 1 | 09-17 12:09 | 1:06 | Advanced | PK3.1 PK3.2 PK3.3 | 10 | engine-fast:OK · face-fast:OK | $30.5732 | $0.0075 | 413,123/226,173 |
| 7 | PK3 | Fix | 2 | 09-17 13:27 | 0:02 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $0.8547 | $0.0076 | 36,779/5,655 |
| 8 | PK4 | Deliver | 1 | 09-17 13:36 | 0:51 | Advanced | PK4.1 PK4.2 | 11 | engine-fast:OK · face-fast:OK | $31.6004 | $0.0074 | 357,936/181,006 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 6 | 88.3M | 98.0% | $70.61 | 8 | 11M | $8.83 |
| stage PK1 | 2 | 18.3M | 97.3% | $16.23 | 2 | 9.17M | $8.12 |
| stage PK2 | 2 | 27.1M | 97.9% | $22.94 | 3 | 9.02M | $7.65 |
| stage PK3 | 2 | 42.9M | 98.4% | $31.44 | 3 | 14.3M | $10.48 |
| 2026-09 | 6 | 88.3M | 98.0% | $70.61 | 8 | 11M | $8.83 |

_Where the money goes: agent $70.56 (100%) · gate $0.05 (0%) · blended $0.80/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
09-17 12:59:12  ▪ gate face-fast pass [phase]  (0.0s)
09-17 12:59:12  ▪ gate engine-full FAIL [phase]  (6m35s)
09-17 12:59:12  ▪ gate face-full pass [phase]  (4.7s)
09-17 12:59:13  • session #5 PK2 Fix started (attempt 2/6)
09-17 13:05:14  ▪ gate engine-fast pass [session]  (1m15s)
09-17 13:05:14  ▪ gate face-fast pass [session]  (2.5s)
09-17 13:05:15  • session #5 PK2 → Progress · 1 commit(s)  (6m01s)
09-17 13:09:51  ▪ gate engine-fast pass [phase]  (0.0s)
09-17 13:09:51  ▪ gate face-fast pass [phase]  (0.0s)
09-17 13:09:51  ▪ gate engine-full pass [phase]  (4m33s)
09-17 13:09:51  ▪ gate face-full pass [phase]  (1.3s)
09-17 13:09:51  ✓ checkpoint PK2.1 confirmed
09-17 13:09:51  ✓ checkpoint PK2.2 confirmed
09-17 13:09:51  ✓ checkpoint PK2.3 confirmed
09-17 13:09:51  ▸ stage PK2 confirmed  (1h27m34s)
09-17 13:09:52  ▸ stage PK3 entered — One transport
09-17 13:09:52  • session #6 PK3 Deliver started (attempt 1/6)
09-17 14:17:09  ▪ gate engine-fast pass [session]  (1m12s)
09-17 14:17:09  ▪ gate face-fast pass [session]  (2.7s)
09-17 14:17:10  • session #6 PK3 → Advanced · done PK3.1,PK3.2,PK3.3 · 10 commit(s)  (1h07m18s)
09-17 14:27:04  ▪ gate engine-fast pass [phase]  (0.0s)
09-17 14:27:04  ▪ gate face-fast pass [phase]  (0.0s)
09-17 14:27:04  ▪ gate engine-full FAIL [phase]  (4m54s)
09-17 14:27:04  ▪ gate face-full pass [phase]  (1.7s)
09-17 14:27:05  • session #7 PK3 Fix started (attempt 2/6)
09-17 14:31:10  ▪ gate engine-fast pass [session]  (1m14s)
09-17 14:31:10  ▪ gate face-fast pass [session]  (2.2s)
09-17 14:31:10  • session #7 PK3 → Progress · 2 commit(s)  (4m05s)
09-17 14:36:05  ▪ gate engine-fast pass [phase]  (0.0s)
09-17 14:36:05  ▪ gate face-fast pass [phase]  (0.0s)
09-17 14:36:05  ▪ gate engine-full pass [phase]  (4m51s)
09-17 14:36:05  ▪ gate face-full pass [phase]  (1.3s)
09-17 14:36:05  ✓ checkpoint PK3.1 confirmed
09-17 14:36:05  ✓ checkpoint PK3.2 confirmed
09-17 14:36:05  ✓ checkpoint PK3.3 confirmed
09-17 14:36:05  ▸ stage PK3 confirmed  (1h26m13s)
09-17 14:36:06  ▸ stage PK4 entered — Rooms and the card
09-17 14:36:06  • session #8 PK4 Deliver started (attempt 1/6)
09-17 15:28:36  ▪ gate engine-fast pass [session]  (1m11s)
09-17 15:28:36  ▪ gate face-fast pass [session]  (2.5s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 8 · retries 3 (38 %) · overall Warn
⚠ [context-saturation] session #4: 25,036,574 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 41,561,063 context tokens (≥ 20,000,000)
⚠ [gate-oscillation] gate 'engine-full' flipped pass/fail 5x
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/peyk-courier
working tree: M .conductor/REPORT.md
vs upstream: up to date
```

### Commits by session

- **s1 (PK1 Deliver)** — 11 commit(s):
  - [`aca1eba`](https://github.com/shaahink/conductor/commit/aca1eba) docs(peyk): handoff after PK1 - the courier is its own exe in its own directory; PK2.1 next
  - [`9262a55`](https://github.com/shaahink/conductor/commit/9262a55) docs(evidence): PK1.2 live proof - 27/27 on c50bf01, scratch install and scratch courier
  - [`c50bf01`](https://github.com/shaahink/conductor/commit/c50bf01) test(install): PK1.2 rig quotes -clp:ErrorsOnly (5.1 splits it when passed through $args)
  - [`be821b4`](https://github.com/shaahink/conductor/commit/be821b4) test(install): PK1.2 rig hashes with the BCL (Get-FileHash is not loadable in a 5.1 child of pwsh 7)
  - [`d6eb4bc`](https://github.com/shaahink/conductor/commit/d6eb4bc) test(install): PK1.2 live proof rig; handoff mid-checkpoint
  - [`e775bbe`](https://github.com/shaahink/conductor/commit/e775bbe) feat(install): the engine installs around a live courier; -CourierOnly replaces it (PK1.2, D1)
  - [`5605f8b`](https://github.com/shaahink/conductor/commit/5605f8b) feat(courier): the courier's own install directory; install verb and preflight follow it (PK1.2, D1)
  - [`d2b42e4`](https://github.com/shaahink/conductor/commit/d2b42e4) docs(peyk): handoff after PK1.1 - the courier is its own exe; PK1.2 next
  - [`4cf6dcf`](https://github.com/shaahink/conductor/commit/4cf6dcf) docs(evidence): PK1.1 live proof - 13/13 on 2284862, seeded violation and negative control included
  - [`2284862`](https://github.com/shaahink/conductor/commit/2284862) test(courier): PK1.1 live proof rig; a red kill-together test leaves no orphan
  - [`ba634a8`](https://github.com/shaahink/conductor/commit/ba634a8) feat(courier): conductor-courier.exe is its own process; courier run is an alias (PK1.1, D1)
- **s2 (PK1 Fix)** — 1 commit(s):
  - [`6ead315`](https://github.com/shaahink/conductor/commit/6ead315) fix(courier): the messenger's words go back to the adapter file (PK1 battery red, KS11.1 ratchet)
- **s4 (PK2 Deliver)** — 11 commit(s):
  - [`195ee71`](https://github.com/shaahink/conductor/commit/195ee71) docs(peyk): handoff after PK2 - courier armed 11:37:30Z pid 20052, PK6.1 reading procedure, keep-alive measured
  - [`13c7d03`](https://github.com/shaahink/conductor/commit/13c7d03) docs(evidence): PK2.3 the real courier armed at 2026-09-17T11:37:30Z, pid 20052, keep-alive read back
  - [`24dd990`](https://github.com/shaahink/conductor/commit/24dd990) test(courier): PK2.3 arming and read-out script for the real courier; interim handoff before arming
  - [`3239a5b`](https://github.com/shaahink/conductor/commit/3239a5b) docs(evidence): PK2.2 live proof 25/25 on b1a3b0a - keep-alive restarts an exit 0 in 272s and 280s, a run restarts a dead courier
  - [`b1a3b0a`](https://github.com/shaahink/conductor/commit/b1a3b0a) docs(peyk): interim handoff - PK2.2 code and rig committed, live rig running
  - [`7e05c72`](https://github.com/shaahink/conductor/commit/7e05c72) test(courier): PK2.2 live rig - exit journaling, keep-alive measured against a no-trigger control, a run restarts a dead scratch courier
  - [`be76d1f`](https://github.com/shaahink/conductor/commit/be76d1f) feat(courier): PK2.2 exit journaling, five-minute keep-alive trigger, run restarts a dead courier at the boundary
  - [`727b036`](https://github.com/shaahink/conductor/commit/727b036) docs(peyk): handoff after PK2.1 - heartbeat, alive/stale/dead, death record; a running task's last result is 267009
  - [`f1c1a31`](https://github.com/shaahink/conductor/commit/f1c1a31) docs(evidence): PK2.1 live proof 18/18 on 1334051 and affected courier classes 124/124
  - [`1334051`](https://github.com/shaahink/conductor/commit/1334051) test(courier): PK2.1 live rig - scratch courier killed, status reads dead, restart journals the death record
  - [`4affb95`](https://github.com/shaahink/conductor/commit/4affb95) feat(courier): PK2.1 heartbeat, alive/stale/dead status and the startup death record
- **s5 (PK2 Fix)** — 1 commit(s):
  - [`59cfe04`](https://github.com/shaahink/conductor/commit/59cfe04) fix(courier): PK2 architecture ratchet - CourierTaskRun and the queue's surface sources get their own files
- **s6 (PK3 Deliver)** — 10 commit(s):
  - [`1b50136`](https://github.com/shaahink/conductor/commit/1b50136) docs(peyk): handoff after PK3 - protocol 3, say and the direct fallback, the run's hello all claimed
  - [`0968027`](https://github.com/shaahink/conductor/commit/0968027) docs(evidence): PK3.3 live proof 12/12 - a fresh plan in an allowed repo is parked, the run's first boundary hello adds it by run <id>, the next note is filed
  - [`9f63a5f`](https://github.com/shaahink/conductor/commit/9f63a5f) feat(courier): PK3.3 a live run names its own project - POST /hello adds the allowlist entry by run <id>
  - [`eea0e4f`](https://github.com/shaahink/conductor/commit/eea0e4f) docs(peyk): handoff after PK3.2 - say and the direct fallback claimed, rig helpers named, PK3.3 next
  - [`da2626f`](https://github.com/shaahink/conductor/commit/da2626f) docs(evidence): PK3.2 live proof 20/20 - say around a stopped courier, a run's pushes sent directly, one real send (id 3821) deleted
  - [`a9c6a70`](https://github.com/shaahink/conductor/commit/a9c6a70) test(say): PK3.2 live proof rig - say through and around a scratch courier, a scratch run's direct fallback, one real send
  - [`965a6dd`](https://github.com/shaahink/conductor/commit/965a6dd) feat(say): PK3.2 conductor say and the direct fallback - a send no courier takes goes out with the run's own token
  - [`e80c862`](https://github.com/shaahink/conductor/commit/e80c862) docs(peyk): handoff after PK3.1 - protocol 3 claimed, the TelegramSender transport and the relay method for PK3.2
  - [`04a58b0`](https://github.com/shaahink/conductor/commit/04a58b0) docs(evidence): PK3.1 live proof 23/23 - scratch courier answers protocol 3, one real send to the admin DM (id 3820) ledgered and deleted
  - [`0d43747`](https://github.com/shaahink/conductor/commit/0d43747) feat(courier): PK3.1 protocol 3 - /send, /react, /delete, /chats with message ids and messages.jsonl
- **s7 (PK3 Fix)** — 2 commit(s):
  - [`f5ef7dc`](https://github.com/shaahink/conductor/commit/f5ef7dc) docs(peyk): handoff after the PK3 fix - say joins the completion verb list
  - [`2dc1985`](https://github.com/shaahink/conductor/commit/2dc1985) fix(peyk): PK3.2 say reaches shell completion - the exhaustive verb test named it missing
- **s8 (PK4 Deliver)** — 11 commit(s):
  - [`705f9e5`](https://github.com/shaahink/conductor/commit/705f9e5) feat(rooms): PK4.3 part 1 - RoomVoiceBattery and the embedded character, not yet wired; handoff with the remaining steps
  - [`68f0f2f`](https://github.com/shaahink/conductor/commit/68f0f2f) docs(peyk): handoff after PK4.2 - the card at the verdict, the held words, the stage card
  - [`2a6af97`](https://github.com/shaahink/conductor/commit/2a6af97) docs(evidence): PK4.2 rig 3/3 - one card with its pair at the verdict, nothing on red with the words held in prompt 2, one stage card; neighbours 596/596
  - [`1a90426`](https://github.com/shaahink/conductor/commit/1a90426) test(card): PK4.2 the card through a real run - green posts one card with its pair, red posts nothing and prompt 2 holds the words, a stage confirm posts one stage card
  - [`80743dd`](https://github.com/shaahink/conductor/commit/80743dd) fix(card): PK4.1/PK4.2 the architecture ratchet - one more type per file and three more RunLoop lines than allowed
  - [`b636bba`](https://github.com/shaahink/conductor/commit/b636bba) feat(card): PK4.2 the engine composes the checkpoint card at the verdict and posts it to the room's observer chat
  - [`c97b8c5`](https://github.com/shaahink/conductor/commit/c97b8c5) feat(card): PK4.2 task --done --tell stores the room's words on the claim event
  - [`c03a674`](https://github.com/shaahink/conductor/commit/c03a674) docs(peyk): handoff after PK4.1 - rooms in the courier home, the import, character.md in the tree
  - [`64c2dc6`](https://github.com/shaahink/conductor/commit/64c2dc6) docs(evidence): PK4.1 live proof 16/16 - the fresh build imports BookToCourse and cv into a scratch courier home by name, voices pointed at, KS11.1 goldens byte-identical
  - [`e00cad1`](https://github.com/shaahink/conductor/commit/e00cad1) fix(rooms): PK4.1 a room takes the plan's chats as plain pairs - KS11.1's seam test named Room.cs for naming TelegramConfig
  - [`99cb57e`](https://github.com/shaahink/conductor/commit/99cb57e) feat(rooms): PK4.1 rooms in the courier home - room list|show|add|import, the one-time import points at each voice, character.md in the tree

## Phase handovers (audit)

- `.conductor/handovers/B0.md`
- `.conductor/handovers/B1.md`
- `.conductor/handovers/B10.md`
- `.conductor/handovers/B11.md`
- `.conductor/handovers/B2.md`
- `.conductor/handovers/B3.md`
- `.conductor/handovers/B4.md`
- `.conductor/handovers/B5.md`
- `.conductor/handovers/B6.md`
- `.conductor/handovers/B7.md`
- `.conductor/handovers/B8.md`
- `.conductor/handovers/B9.md`
- `.conductor/handovers/F0.md`
- `.conductor/handovers/F1.md`
- `.conductor/handovers/F2.md`
- `.conductor/handovers/F4.md`

## Last gate run

engine-fast:OK · face-fast:OK

## Last session result

> **PK4.1 rooms and PK4.2 card at the verdict claimed; PK4.3 half done**
> - PK4.1: `room list|show|add|import`; import into a scratch home shows BookToCourse and cv by name, voices pointed at, KS11.1 goldens identical (16/16)
> - PK4.2: `--tell` on the claim; engine posts one card with its pair at the verdict, nothing on red, words held in prompt 2, one stage card (rig 3/3, 596/596)
> - PK4.3: RoomVoiceBattery and embedded character.md committed but not used yet; prompt wiring and the field-plan rewrites are next in the handoff
>
> artefacts: 99cb57e, e00cad1, 64c2dc6, c97b8c5, b636bba, 80743dd, 1a90426, 2a6af97
>
> evidence: .conductor/evidence/PK4/pk4.1.md, .conductor/evidence/PK4/pk4.2.md
>
> gaps: PK4.3 open: battery not in PromptBuilder, no unit tests or dry-run proof, no SF7_1 --tell pin, report.ps1 not deleted, bg and BookToCourse plan rules not rewritten; the owner's real courier home still needs `conductor room import`

## Tracker handoff

```
last: s8 claimed PK4.1 (evidence pk4.1.md, rig 16/16) and PK4.2 (evidence pk4.2.md, rig 3/3 via HarnessTests.Cards.cs, neighbours 596/596). PK4.3 IN PROGRESS, half done - budget nudge.
  PK4.3 landed (unwired, builds): src/Conductor.Core/RoomVoiceBattery.cs (character + room voice, fair split of its 4096 cap, clip says so; ReadVoice/EmbeddedCharacter public), docs/rooms/character.md embedded in Conductor.Core.csproj as Conductor.Core.Rooms.character.md, PlanConfig.ChatPairs() (neutral (id, profile) pairs so Core callers never name Telegram*).
  PK4.3 NEXT, in order: (1) wire in PromptBuilder.BatterySection after HeldWordsBattery: `if (_plan.Repo is {Length:>0} r && Courier.Rooms.Resolve(r, _plan.ChatPairs(), Courier.CourierSettings.Load()) is {} room)` add RoomVoiceBattery when !IsEmpty - then RUN prompt/golden tests + ArchitectureTests: the courier.json fallback (one observer chat on the machine) turns the battery on for every repo, check no test state home carries one. (2) RoomVoiceBattery unit tests with a FIXTURE voice (never the owner's). (3) SF7_1 pin: the cli.md `task` row names `--tell` and the verdict; cli.md/operating.md never tell a session to run report.ps1. (4) dry-run proof: fresh build `run --dry-run -p <scratch plan>` with CONDUCTOR_STATE_HOME scratch + a room with observer + fixture voice - the prompt carries `### room-voice` within batteries.maxBytes. (5) delete ~/.claude/skills/telegram-notify/report.ps1 and drop its mentions from that SKILL.md. (6) rewrite rule 11 in C:/Code/bg/plans/conductor.plan.json AND conductor.feel.plan.json, rule 17 in C:/Code/BookToCourse/conductor.hardening.plan.json to `task --done --tell "<title> | <sentences>"` (engine posts at the verdict, red posts nothing and the words are held, before/after = <id>-before.png/<id>-after.png in the watched evidence dir, never post a card yourself, say --to observer for findings, and: if the engine refuses --tell claim without it). Raw string replace of the JSON-escaped rule, json.loads to verify, commit nothing there; `conductor ps` showed no run live in those repos at 14:25Z. BookToCourse rule 17 also cites session.md step 5 (not a plan file - name it in the handoff); bg keeps an in-repo skill copy with its own report.ps1 (leave it).
  PK4.1/PK4.2 facts: a stage is CONFIRMED only under gatePolicy perPhase; the composer must FlushEvents before reading; ArchitectureTests = 3 types/file and RunLoop.cs AT 500 lines; KS11_1SeamBoundary forbids Telegram* in new Core files. The owner's real courier home has no rooms yet - `conductor room import` after the reinstall; bg has no room (its chats live in the repo's .claude copy) - `room add --repo C:/Code/bg --observer <id>`.
  PK2 (still true): real 'Conductor Courier' ARMED 2026-09-17T11:37:30Z, protocol 2; bug #93 OPEN for PK6.1 (`tools/peyk/pk2-3-arm-real-courier.ps1 -ReadOnly`).
next: PK4.3 steps (1)-(6) above, then claim with a pk4.3.md evidence. Do not restart, stop or reinstall the armed courier.
```
