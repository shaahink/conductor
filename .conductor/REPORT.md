# Conductor — Peyk courier - the courier stands on its own run report

_Updated 2026-09-18 12:16 UTC · branch `feat/peyk-courier` · HEAD `fa99849`_

**Status:** Completed
**Stage:** PK6 — The docs, the skill, the close · attempts used 0
**Checkpoints:** 17/17 done · **Sessions run:** 14 · **Cost:** $168.0218 (agent $167.9230 + gates $0.0988) · **Tokens:** 2,619,638 in / 1,228,625 out
**Confirmed phases:** PK1, PK2, PK3, PK4, PK5, PK6
**Channels:** telegram ready · github ready · courier ready
**CI battery:** ci-battery DEGRADED · ci-verdict DEGRADED
**⚠ CI DEGRADED — ci-battery:** CI runs 'powershell tools/gates/ratchet.ps1' that this run's gates do not - a checkpoint can pass one battery and fail the other · fix: add 'powershell tools/gates/ratchet.ps1' to plan.gates, or drop it from ci.yml. 
**⚠ CI DEGRADED — ci-verdict:** CI has no verdict for fa99849, the commit this run is on: CI's newest run is for b125405 - a branch reads green when the workflow that would have failed never ran on this head · fix: push the commit, or re-ask once CI has run: conductor github ci

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| PK1 | Its own process | ██████████ 2/2 | confirmed ✓ |
| PK2 | Alive, or known dead | ██████████ 3/3 | confirmed ✓ |
| PK3 | One transport | ██████████ 3/3 | confirmed ✓ |
| PK4 | Rooms and the card | ██████████ 3/3 | confirmed ✓ |
| PK5 | Inbound with a name | ██████████ 2/2 | confirmed ✓ |
| PK6 | The docs, the skill, the close | ██████████ 4/4 | confirmed ✓ |

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

<details> ✅<summary>PK6 — The docs, the skill, the close (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| PK6.1 | The cause: the read-out of PK2.3's window (at least 24 hours, both timestamps) - a dated finding naming the first recorded exit path, or a dated statement that none occurred with the instruments listed; the reading procedure in docs/operating.md so a later death is read the same way; bug #93 closed on it | ✅ DONE | [`c714b41`](https://github.com/shaahink/conductor/commit/c714b41) |
| PK6.2 | docs/cli.md, operating.md, plan-config.md and ARCHITECTURE.md reconciled (the courier section rewritten for a separate binary, seams re-counted); ADR-0009 amending ADR-0008 for D3, D4 and D5; the docs battery green with a negative control per new verb and key | ✅ DONE | [`f47eab3`](https://github.com/shaahink/conductor/commit/f47eab3) |
| PK6.3 | The telegram-notify skill rewritten to two pages around conductor say and --tell; send.ps1, walk-ids.ps1 and lib/ deleted; watch-live re-pointed; a grep of the skill folder finds no Bot API URL; one real post through say from outside any run, then deleted | ✅ DONE | [`f47eab3`](https://github.com/shaahink/conductor/commit/f47eab3) |
| PK6.4 | The close through the machinery: release preflight, the mechanical acts performed, the CHANGELOG section written, the era's numbers measured against a backup copy of the store; the owner's acts printed and parked; the plan doc left in place for plan B | ✅ DONE | [`10e4eee`](https://github.com/shaahink/conductor/commit/10e4eee) |

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
| 13 | PK6 | Deliver | 1 | 09-18 11:40 | 0:13 | Advanced | PK6.1 | 3 | engine-fast:OK · face-fast:OK | $4.9684 | $0.0093 | 110,171/49,805 |
| 14 | PK6 | Deliver | 1 | 09-18 11:59 | 0:10 | Advanced | PK6.4 | 2 | engine-fast:OK · face-fast:OK | $4.0166 | $0.0072 | 99,132/38,157 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 13 | 195.5M | 98.0% | $168.02 | 17 | 11.5M | $9.88 |
| stage PK1 | 2 | 18.3M | 97.3% | $16.23 | 2 | 9.17M | $8.12 |
| stage PK2 | 2 | 27.1M | 97.9% | $22.94 | 3 | 9.02M | $7.65 |
| stage PK3 | 2 | 42.9M | 98.4% | $31.44 | 3 | 14.3M | $10.48 |
| stage PK4 | 2 | 44.7M | 98.3% | $41.33 | 3 | 14.9M | $13.78 |
| stage PK5 | 2 | 33.4M | 98.0% | $29.76 | 2 | 16.7M | $14.88 |
| stage PK6 | 3 | 29M | 97.7% | $26.32 | 4 | 7.26M | $6.58 |
| 2026-09 | 13 | 195.5M | 98.0% | $168.02 | 17 | 11.5M | $9.88 |

_Where the money goes: agent $167.92 (100%) · gate $0.10 (0%) · blended $0.86/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
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
09-17 17:23:53  • session #12 PK6 → BlockedUntil · done PK6.2,PK6.3 · 4 commit(s)  (22m58s)
09-18 12:40:01  • session #13 PK6 Deliver started (attempt 1/8)
09-18 12:54:59  ▪ gate engine-fast pass [session]  (1m11s)
09-18 12:54:59  ▪ gate face-fast pass [session]  (22.2s)
09-18 12:55:00  • session #13 PK6 → Advanced · done PK6.1 · 3 commit(s)  (14m58s)
09-18 12:59:42  • session #14 PK6 Deliver started (attempt 1/8)
09-18 13:10:59  ▪ gate engine-fast pass [session]  (1m09s)
09-18 13:10:59  ▪ gate face-fast pass [session]  (2.6s)
09-18 13:11:00  • session #14 PK6 → Advanced · done PK6.4 · 2 commit(s)  (11m17s)
09-18 13:16:00  ▪ gate engine-fast pass [phase]  (0.0s)
09-18 13:16:00  ▪ gate face-fast pass [phase]  (0.0s)
09-18 13:16:00  ▪ gate engine-full pass [phase]  (4m56s)
09-18 13:16:00  ▪ gate face-full pass [phase]  (2.1s)
09-18 13:16:00  § owner approval requested — PK6
09-18 13:16:06  § owner approval granted — PK6
09-18 13:16:06  ✓ checkpoint PK6.1 confirmed
09-18 13:16:06  ✓ checkpoint PK6.4 confirmed
09-18 13:16:06  ▸ stage PK6 confirmed  (20h15m10s)
09-18 13:16:06  ◆ run finished — Completed · 17/17 checkpoints  (26h45m04s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 14 · retries 3 (21 %) · overall Warn
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
working tree: M .conductor/REPORT.md, M plans/peyk/COURIER-TRACKER.md
vs upstream: up to date
```

### Commits by session

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
- **s13 (PK6 Deliver)** — 3 commit(s):
  - [`1d7cd32`](https://github.com/shaahink/conductor/commit/1d7cd32) docs(peyk): handoff after PK6.1 - the cause is measured and fixed; PK6.4 is the last one and it is ownerGate
  - [`9fb16ef`](https://github.com/shaahink/conductor/commit/9fb16ef) fix(courier): bug #100 - an HttpClient timeout no longer kills the courier
  - [`c714b41`](https://github.com/shaahink/conductor/commit/c714b41) docs(peyk): PK6.1 - the cause: HttpClient's 65s getUpdates timeout kills the courier, twice in the window
- **s14 (PK6 Deliver)** — 2 commit(s):
  - [`fa99849`](https://github.com/shaahink/conductor/commit/fa99849) docs(peyk): PK6.4 - the close pre-flighted and parked; the owner's acts printed
  - [`10e4eee`](https://github.com/shaahink/conductor/commit/10e4eee) docs(release): PK6.4 - the release section carries Peyk beside Charkh

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

> **PK6.4 landed — the era-close pre-flighted, the owner's acts printed, Plan A complete**
> - CHANGELOG section written for both eras in one `[Unreleased]` block; heading left intact so the mechanical rename still matches
> - preflight/perform-dry-run/runbook all captured; 4 reds, every one a state only the owner's acts clear
> - bug #101 filed: docmove would move the plan doc out from under plan B, and there is no per-act skip
>
> artefacts: CHANGELOG.md, plans/peyk/COURIER-TRACKER.md, commits 10e4eee, fa99849
>
> evidence: .conductor/evidence/PK6/pk6.4.md, .conductor/evidence/PK6/pk6.4-runbook.md, .conductor/evidence/PK6/pk6.4-preflight.txt, .conductor/evidence/PK6/pk6.4-perform-dryrun.txt, .conductor/evidence/PK6/pk6.4-money.txt, .conductor/evidence/PK6/pk6.4-budget.txt
>
> gaps: the merge, tag, reinstall and both pushes are the owner's and unperformed; docmove must not run until plan B is launched or repointed (bug #101); the GitHub backfill stays declined; the real courier is still on the old binary and gets bug #100's fix only at the reinstall.

## Tracker handoff

```
last: s14 claimed **PK6.4** (evidence `.conductor/evidence/PK6/pk6.4.md`). **Stage PK6 is complete and Plan A has no checkpoints left.** The close is pre-flighted and parked, which is what ownerGate means here: `release perform --yes` is refused outright while a run is live in the plan's state dir (`ReleaseCommand.Perform.cs:53`), so a session can only rehearse — and it did, through the fresh build, against `--tag 0.6.0`.
  Landed: the CHANGELOG section for **both** eras in one `[Unreleased]` block (commit `10e4eee`, 91 lines inserted, nothing deleted). The heading is **deliberately still `## [Unreleased]`** — `DoChangelogAsync` renames exactly that literal, so renaming it by hand would take the mechanical act away from the owner and make the verb report a failure for work already done.
  Measured: tree and installed engine (0.5.1-alpha.0.43) BOTH carry `MigrationRunner.CurrentVersion` 15 — no skew either way, trap 19 clear. **`C:\Code\conductor\.conductor\run.db` is NOT the live store**; it is a stale artifact at schema 9. The real one is under `%LOCALAPPDATA%\conductor\runs\...-308cfb9b\`, and `budget`/`money` ran off a `sqlite3 .backup` copy of it: Peyk 12 sessions, 191.2M tokens, 98.1% cache, USD 164.00, 16 checkpoints; Charkh 9 / 178.9M / USD 129.20 / 13; v0.6.0 total USD 293.20 over 29. Both are floors. The tuner prescribes 48M/0.90 for the next era.
  Preflight is 4-of-7 red and **not one red is a defect in the branch**: merge (dirty tree — `.conductor/REPORT.md` is rewritten every stage while the run is live), changelog (no 0.6.0 section yet, by design), docs (11 rows the docs act rewrites), processes (this engine, pid 15672). Migration and courier are green; the real courier is untouched, still on the old binary at pid 20860.
  **New: bug #101 (medium).** `release perform`'s `docmove` act derives its moves from THIS plan only and repoints only THIS plan file, so it would `git mv` `docs/dev/NEXT-ERA-FINDINGS-2026-09-17.md` into `docs/history` while `plans/peyk/watch.plan.json:8` and `:210`, `plans/peyk/templates/session.md:32` and `plans/peyk/WATCH-TRACKER.md:3` all still name the old path — plan B's sessions would open nothing. `MechanicalOrder` has no per-act skip. **The plan doc is NOT moved, and must not be until plan B has launched or been repointed by hand.**
next: nothing is left for a session on Plan A. The remaining acts are the owner's and are printed verbatim in section 6 of the evidence and in the generated `.conductor/evidence/PK6/pk6.4-runbook.md`: version (0.6.0), split (one release — decided), corpus (**declined**, bug #84), reinstall (`tools/install.ps1` — this is what finally delivers bug #100's fix to the real courier), publish (`git push origin master` then `git push origin v0.6.0`). Branch CI was last red only on the Courier complexity budget, fixed in `4955e9f`.
```
