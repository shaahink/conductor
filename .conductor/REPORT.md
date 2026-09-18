# Conductor — Peyk courier - the courier stands on its own run report

_Updated 2026-09-17 16:23 UTC · branch `feat/peyk-courier` · HEAD `98dbfce`_

**Status:** Waiting
**Stage:** PK6 — The docs, the skill, the close · attempts used 0 · working ▸ PK6.1
**Checkpoints:** 15/17 done · **Sessions run:** 12 · **Cost:** $159.0203 (agent $158.9380 + gates $0.0823) · **Tokens:** 2,410,335 in / 1,140,663 out
**Waiting:** waiting until 2026-09-18 11:40:00Z (19h16m from now) — PK6.1 reads out PK2.3's window, which must reach 24 h (armed 2026-09-17T11:37:30Z). PK6.2 and PK6.3 are claimed; the first death is already pre-read in the ledger (12:23:18Z TaskCanceledException from GetUpdatesAsync). Wake, re-read the log for anything later, write the finding, close #93, then PK6.4. [0s ago, 16:23:53Z]
**Confirmed phases:** PK1, PK2, PK3, PK4, PK5
**Channels:** telegram ready · github ready · courier ready
**CI battery:** ci-battery DEGRADED · ci-verdict DEGRADED
**⚠ CI DEGRADED — ci-battery:** CI runs 'powershell tools/gates/ratchet.ps1' that this run's gates do not - a checkpoint can pass one battery and fail the other · fix: add 'powershell tools/gates/ratchet.ps1' to plan.gates, or drop it from ci.yml. 
**⚠ CI DEGRADED — ci-verdict:** CI has no verdict for 98dbfce, the commit this run is on: CI's newest run is for b125405 - a branch reads green when the workflow that would have failed never ran on this head · fix: push the commit, or re-ask once CI has run: conductor github ci

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| PK1 | Its own process | ██████████ 2/2 | confirmed ✓ |
| PK2 | Alive, or known dead | ██████████ 3/3 | confirmed ✓ |
| PK3 | One transport | ██████████ 3/3 | confirmed ✓ |
| PK4 | Rooms and the card | ██████████ 3/3 | confirmed ✓ |
| PK5 | Inbound with a name | ██████████ 2/2 | confirmed ✓ |
| PK6 | The docs, the skill, the close | █████░░░░░ 2/4 | **← active** |

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

<details> ✅<summary>PK4 — Rooms and the card (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK4.1 | rooms/<slug>.json in the courier home; conductor room add / show / list; the one-time import of the private config files; docs/rooms/character.md in the tree; the voice file only ever pointed at. room list shows the two migrated rooms; an old-shape plan on a room-less machine replays byte-identically | ✅ DONE | [`99cb57e`](https://github.com/shaahink/conductor/commit/99cb57e) |
| PK4.2 | conductor task --done --tell stores the session's words; the engine composes the checkpoint card at the verdict and pushes it to the room's observer chat; a red claim posts nothing and the next prompt carries the held words; the card is a NotifyTemplate; stages[].deploys; a stage confirm posts a stage card. Proven on a rig with a fake agent | ✅ DONE | [`99cb57e`](https://github.com/shaahink/conductor/commit/99cb57e) |
| PK4.3 | RoomVoiceBattery under the byte cap; report.ps1 deleted from the shared skill; the three field plans' report.ps1 rule rewritten to --tell (their plan files only, nothing committed there); SF7_1DocsMatchRealityTests pins --tell in docs/cli.md | ✅ DONE | [`f83104e`](https://github.com/shaahink/conductor/commit/f83104e) |

</details>

<details> ✅<summary>PK5 — Inbound with a name (2/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK5.1 | InboxNote carries MessageId, SenderId, SenderName, SenderUsername; the ack is a reaction, never a message; inbox list shows sender and id; say --reply-to takes a note id or a message id; walk-ids.ps1 deleted; the no-authority rule in the note file's header. An old note without the fields still lists | ✅ DONE | [`321c73b`](https://github.com/shaahink/conductor/commit/321c73b) |
| PK5.2 | The figure verbs (/progress, /money, /tokens, /status, /evidence) answered by the courier from the project's newest run.db, read-only, whether or not a run is live; a test asserts no store write on that path; the in-run handlers stay for a courier-less machine | ✅ DONE | [`6bd8150`](https://github.com/shaahink/conductor/commit/6bd8150) |

</details>

<details><summary>PK6 — The docs, the skill, the close (2/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK6.1 | The cause: the read-out of PK2.3's window (at least 24 hours, both timestamps) - a dated finding naming the first recorded exit path, or a dated statement that none occurred with the instruments listed; the reading procedure in docs/operating.md so a later death is read the same way; bug #93 closed on it | ⬜ TODO | - |
| PK6.2 | docs/cli.md, operating.md, plan-config.md and ARCHITECTURE.md reconciled (the courier section rewritten for a separate binary, seams re-counted); ADR-0009 amending ADR-0008 for D3, D4 and D5; the docs battery green with a negative control per new verb and key | ✅ DONE | - |
| PK6.3 | The telegram-notify skill rewritten to two pages around conductor say and --tell; send.ps1, walk-ids.ps1 and lib/ deleted; watch-live re-pointed; a grep of the skill folder finds no Bot API URL; one real post through say from outside any run, then deleted | ✅ DONE | - |
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
| 9 | PK4 | Deliver | 1 | 09-17 14:28 | 0:19 | Advanced | PK4.3 | 4 | engine-fast:OK · face-fast:OK | $9.7180 | $0.0072 | 164,708/73,587 |
| 10 | PK5 | Deliver | 1 | 09-17 14:53 | 0:28 | Advanced | PK5.1 | 3 | engine-fast:OK · face-fast:OK | $12.5092 | $0.0073 | 211,567/92,373 |
| 11 | PK5 | Deliver | 1 | 09-17 15:23 | 0:30 | Advanced | PK5.2 | 4 | engine-fast:OK · face-fast:OK | $17.2329 | $0.0074 | 260,448/110,267 |
| 12 | PK6 | Deliver | 1 | 09-17 16:00 | 0:22 | BlockedUntil | PK6.2 PK6.3 | 4 |  | $17.3193 |  | 242,725/116,673 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 10 | 166.5M | 98.1% | $141.70 | 13 | 12.8M | $10.90 |
| stage PK1 | 2 | 18.3M | 97.3% | $16.23 | 2 | 9.17M | $8.12 |
| stage PK2 | 2 | 27.1M | 97.9% | $22.94 | 3 | 9.02M | $7.65 |
| stage PK3 | 2 | 42.9M | 98.4% | $31.44 | 3 | 14.3M | $10.48 |
| stage PK4 | 2 | 44.7M | 98.3% | $41.33 | 3 | 14.9M | $13.78 |
| stage PK5 | 2 | 33.4M | 98.0% | $29.76 | 2 | 16.7M | $14.88 |
| 2026-09 | 10 | 166.5M | 98.1% | $141.70 | 13 | 12.8M | $10.90 |

_Where the money goes: agent $141.62 (100%) · gate $0.08 (0%) · blended $0.85/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
09-17 14:36:05  ▪ gate face-full pass [phase]  (1.3s)
09-17 14:36:05  ✓ checkpoint PK3.1 confirmed
09-17 14:36:05  ✓ checkpoint PK3.2 confirmed
09-17 14:36:05  ✓ checkpoint PK3.3 confirmed
09-17 14:36:05  ▸ stage PK3 confirmed  (1h26m13s)
09-17 14:36:06  ▸ stage PK4 entered — Rooms and the card
09-17 14:36:06  • session #8 PK4 Deliver started (attempt 1/6)
09-17 15:28:36  ▪ gate engine-fast pass [session]  (1m11s)
09-17 15:28:36  ▪ gate face-fast pass [session]  (2.5s)
09-17 15:28:37  • session #8 PK4 → Advanced · done PK4.1,PK4.2 · 11 commit(s)  (52m30s)
09-17 15:28:37  • session #9 PK4 Deliver started (attempt 1/6)
09-17 15:48:58  ▪ gate engine-fast pass [session]  (1m10s)
09-17 15:48:58  ▪ gate face-fast pass [session]  (2.2s)
09-17 15:48:59  • session #9 PK4 → Advanced · done PK4.3 · 4 commit(s)  (20m21s)
09-17 15:53:51  ▪ gate engine-fast pass [phase]  (0.0s)
09-17 15:53:51  ▪ gate face-fast pass [phase]  (0.0s)
09-17 15:53:51  ▪ gate engine-full pass [phase]  (4m47s)
09-17 15:53:51  ▪ gate face-full pass [phase]  (2.0s)
09-17 15:53:51  ✓ checkpoint PK4.1 confirmed
09-17 15:53:51  ✓ checkpoint PK4.2 confirmed
09-17 15:53:51  ✓ checkpoint PK4.3 confirmed
09-17 15:53:51  ▸ stage PK4 confirmed  (1h17m44s)
09-17 15:53:52  ▸ stage PK5 entered — Inbound with a name
09-17 15:53:52  • session #10 PK5 Deliver started (attempt 1/4)
09-17 16:23:51  ▪ gate engine-fast pass [session]  (1m11s)
09-17 16:23:51  ▪ gate face-fast pass [session]  (2.2s)
09-17 16:23:52  • session #10 PK5 → Advanced · done PK5.1 · 3 commit(s)  (30m00s)
09-17 16:23:52  • session #11 PK5 Deliver started (attempt 1/4)
09-17 16:55:53  ▪ gate engine-fast pass [session]  (1m11s)
09-17 16:55:53  ▪ gate face-fast pass [session]  (2.7s)
09-17 16:55:53  • session #11 PK5 → Advanced · done PK5.2 · 4 commit(s)  (32m00s)
09-17 17:00:54  ▪ gate engine-fast pass [phase]  (0.0s)
09-17 17:00:54  ▪ gate face-fast pass [phase]  (0.0s)
09-17 17:00:54  ▪ gate engine-full pass [phase]  (4m57s)
09-17 17:00:54  ▪ gate face-full pass [phase]  (1.4s)
09-17 17:00:54  ✓ checkpoint PK5.1 confirmed
09-17 17:00:54  ✓ checkpoint PK5.2 confirmed
09-17 17:00:54  ▸ stage PK5 confirmed  (1h07m02s)
09-17 17:00:55  ▸ stage PK6 entered — The docs, the skill, the close
09-17 17:00:55  • session #12 PK6 Deliver started (attempt 1/8)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 12 · retries 3 (25 %) · overall Warn
⚠ [context-saturation] session #11: 20,701,549 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #4: 25,036,574 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 41,561,063 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #8: 31,495,224 context tokens (≥ 20,000,000)
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
- **s9 (PK4 Deliver)** — 4 commit(s):
  - [`125acf9`](https://github.com/shaahink/conductor/commit/125acf9) docs(peyk): handoff after PK4.3 - the room's voice as a battery, report.ps1 retired, three field rules on --tell; PK5.1 next
  - [`712171d`](https://github.com/shaahink/conductor/commit/712171d) docs(evidence): PK4.3 the room's voice in a dry-run prompt under the cap, report.ps1 retired, three field rules on --tell; neighbours 456/456
  - [`e5e316c`](https://github.com/shaahink/conductor/commit/e5e316c) test(docs): PK4.3 pin --tell in the cli.md task row and retire report.ps1 from every shipped doc
  - [`f83104e`](https://github.com/shaahink/conductor/commit/f83104e) feat(rooms): PK4.3 part 2 - the room's voice rides the prompt where a card can go
- **s10 (PK5 Deliver)** — 3 commit(s):
  - [`cb40e10`](https://github.com/shaahink/conductor/commit/cb40e10) docs(peyk): handoff after PK5.1 - notes carry sender and message id, the courier reacts, /note carries the button; PK5.2 next
  - [`c5527c3`](https://github.com/shaahink/conductor/commit/c5527c3) docs(evidence): PK5.1 the reaction on a real admin-DM message (3854, cleared, deleted), rig 25/25; docs name /note and say --reply-to by note
  - [`321c73b`](https://github.com/shaahink/conductor/commit/321c73b) feat(courier): PK5.1 - a note carries its sender and message id, the courier acknowledges with a reaction
- **s11 (PK5 Deliver)** — 4 commit(s):
  - [`1f8a790`](https://github.com/shaahink/conductor/commit/1f8a790) docs(peyk): handoff after PK5.2 - the courier answers the figure verbs from run.db, read-only; PK6 next
  - [`f391cb2`](https://github.com/shaahink/conductor/commit/f391cb2) docs(evidence): PK5.2 the courier answers /money /tokens /progress /status /evidence from a copy of the live store, read-only; rig 27/27, engine money and status match; neighbours 551/551
  - [`e9d40a6`](https://github.com/shaahink/conductor/commit/e9d40a6) feat(courier): PK5.2 - bare /evidence leads with this plan's checkpoints; the live rig and the docs
  - [`6bd8150`](https://github.com/shaahink/conductor/commit/6bd8150) feat(courier): PK5.2 - the figure verbs answered by the courier from run.db, read-only
- **s12 (PK6 Deliver)** — 4 commit(s):
  - [`98dbfce`](https://github.com/shaahink/conductor/commit/98dbfce) docs(peyk): handoff after PK6.2 and PK6.3 - docs and skill claimed; PK6.1 waits for the 24 h window with its first death already read; PK6.4 last
  - [`123b95e`](https://github.com/shaahink/conductor/commit/123b95e) test(docs): PK6.2 - Peyk's surface pinned in SF7_1DocsMatchRealityTests, each with a negative control
  - [`081fa60`](https://github.com/shaahink/conductor/commit/081fa60) docs(peyk): PK6.2 - the courier docs for a separate binary, protocol 3 and the heartbeat; seams recounted to fifteen; ADR-0009 amends 0008
  - [`f47eab3`](https://github.com/shaahink/conductor/commit/f47eab3) docs(evidence): PK6.3 telegram-notify rewritten to two pages around conductor say and --tell; send.ps1 and lib/ deleted, watch-live re-pointed; no Bot API URL in the folder; one real say to the admin DM (3871) sent directly and deleted

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

engine-fast:cached · face-fast:cached · engine-full:OK · face-full:OK

## Last session result

> **PK6.2 docs, pins and ADR-0009 claimed; PK6.3 skill rewrite claimed; PK6.1 blocked-until window end**
> - PK6.3: telegram-notify rewritten around say and --tell, send.ps1 and lib/ deleted, watch-live re-pointed, no Bot API URL; real say 3871 sent directly and deleted
> - PK6.2: cli/operating/plan-config/ARCHITECTURE reconciled, seams recounted to 15, ADR-0009 amends 0008 (4+2 conditions); 9 Peyk facts with negative controls, 73/73 docs, 247/247 neighbours
> - PK6.1 pre-read: first death 12:23:18Z TaskCanceledException from GetUpdatesAsync (CourierDaemon.cs:102/:113); blocked until 2026-09-18T11:40Z
>
> artefacts: f47eab3, 081fa60, 123b95e, 98dbfce, ~/.claude/skills/telegram-notify/SKILL.md, ~/.claude/skills/watch-live/SKILL.md, docs/dev/adr/0009-the-courier-is-its-own-binary-and-one-wire.md, tests/Conductor.Tests/SF7_1DocsMatchRealityTests.Peyk.cs
>
> evidence: .conductor/evidence/PK6/pk6.3.md, .conductor/evidence/PK6/pk6.2.md
>
> gaps: PK6.1 waits for the 24 h window (read-out, bug #93 close, fix decision for the timeout catch); PK6.4 close is owner-gated and last; the plan doc stays in place because plan B reads it

## Tracker handoff

```
last: s12 claimed PK6.3 (skill: .conductor/evidence/PK6/pk6.3.md, f47eab3; real say 3871 to admin DM sent directly + deleted) and PK6.2 (docs: pk6.2.md, 081fa60 + 123b95e; 9 new SF7_1 Peyk facts, 73/73 docs, 247/247 neighbours). PK6.1 set blocked-until 2026-09-18T11:40Z (window must reach 24 h); PK6.4 last.
  PK6.1 is mostly READ already - see s12's "PK6.1 PRE-READ" ledger note: first window death 2026-09-17 12:23:18Z, pid 20052, `courier run DIED (unhandled, terminating): TaskCanceledException ... HttpClient.Timeout of 65 seconds` from GetUpdatesAsync; pid 3232 up 12:25:01Z (keep-alive). Source cause still in tree: CourierDaemon.cs:102/:113 filters let a timeout-TaskCanceledException (ct not cancelled) escape. No death record for it (Main's finally clears presence, CourierProgram.cs:183) - only the exit journal names it.
  PK6.1 to do after 11:37:30Z: re-read courier.log (%LOCALAPPDATA%\conductor\courier) for any death after 12:25Z, schtasks /query /tn "Conductor Courier" /v, System log for sleep; write the dated finding; FOLLOW the procedure PK6.2 already wrote in docs/operating.md "The courier died - reading out why" and correct it where the reading disproves it (add the finally-clears-presence gap); close bug #93 in the same commit. Whether the 2-line catch fix + regression test lands here or as a new bug is that session's call - D2 says measure first, then fix. READ ONLY on the real courier: no restart.
  PK6.4 (ownerGate): release preflight/perform through the fresh build only after checking MigrationRunner.CurrentVersion vs the installed engine (trap 19); money/budget against a sqlite3 .backup COPY; the plan doc is NOT moved - plan B (peyk/watch) reads docs/dev/NEXT-ERA-FINDINGS-2026-09-17.md; pre-flight, print owner acts, park.
  Still true: the PATH engine (0.5.1-alpha.0.43) has no say/room - the rewritten skill works from the owner's reinstall; `say --to admin` refuses on this machine (2 admin chats) - use the id. Real courier pid 3232 speaks protocol 2. Bug #99 open, low.
next: PK6.1 at/after 2026-09-18T11:37:30Z, then PK6.4. Do not restart, stop or reinstall the real courier.
```
