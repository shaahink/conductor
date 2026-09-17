# PK4.3 - RoomVoiceBattery, report.ps1 retired, the field plans on --tell

Session 9, 2026-09-17. Commits f83104e (battery wired, fitting group, unit tests, card rig launcher)
and e5e316c (SF7_1 pins). Every voice here is a fixture; no owner room file was read or copied.

## 1. A dry-run prompt for a rig plan with a room carries the battery under the byte cap

Fresh build (`dotnet run --project src/Conductor --no-build --`), scratch repo, scratch
CONDUCTOR_STATE_HOME and CONDUCTOR_COURIER_PORT=47439, CONDUCTOR_PLAN cleared. Plan: one stage,
`telegram.chats` = one admin chat (a made-up id), `batteries.maxBytes` = 3072.

    room add --repo <rig>/repo --project voice-rig --admin <made-up> --observer <made-up> --voice <rig>/voices/voice.md
      -> room voice-rig saved · <scratch state home>\courier\rooms\voice-rig.json
    room list
      -> rooms (1) · voice-rig
    run --dry-run --no-face --no-control-plane -p <rig>/repo/rig.plan.json   -> exit 0
      -> --- DRY RUN: would start session #1 (Deliver, stage R0) with prompt: ---

The battery section of that prompt (head and tail; the character's middle elided here, not in the prompt):

```
### room-voice
The room voice-rig hears from the engine. When the verdict confirms a claim made with `conductor task --done <id> --evidence <path> --tell "<title> | <two to four sentences>"`, the engine posts the card; write those words in this voice. A finding mid-way goes out with `conductor say --to observer`. Never post a card yourself.

#### the character (every room)
[... the character, cut at a line boundary ...]
… (cut to fit the battery)

#### the voice (this room)
# fixture voice (pk4.3 rig)

FIXTURE-VOICE pk43-rig: the people in this room are imaginary.
Write short sentences; say what changed for the reader, then what is next.

_(these context sections did not all fit the `batteries.maxBytes` budget — trimmed: room-voice. What is above is not the whole picture; raise `batteries.maxBytes` in the plan.)_
```

Measured: the rendered battery section (from `### room-voice` to the notice) is **2967 characters,
2995 UTF-8 bytes** as logged with LF (54 lines; 3049 bytes with CRLF) against `batteries.maxBytes` 3072.
The room's own voice survives the cut (the fixture line is present) and the shared character is the
part trimmed, with the group's notice naming `room-voice`.

Why the group now asks the battery to refit: before f83104e's `IFittingBattery`, the group cut the
section from its tail, so at any cap below the battery's own the ROOM'S voice (the second part) was
always the part lost - measured red by `APlanWhoseRoomHasAnObserverCarriesTheVoiceInsideTheBatteryCap`
(voice marker not found at maxBytes 3000) before the change, green after.

## 2. Tests

- `PK4_3RoomVoiceTests` 10/10: off without an observer; header/character/voice order and the --tell
  shape; embedded character == docs/rooms/character.md; missing and unnamed voice said; property over
  300 random (cap 1024-8192, character 0-12k, voice 0-12k) - section <= cap, both parts present, each
  part whole or marked cut; wiring - on for a room with an observer, never for a plan naming no chats
  even with a room on the machine, off for a room with no observer anywhere, on from the plan's own
  observer chat for an unmigrated repo (character alone, no chat id in the text).
- `SF7_1DocsMatchRealityTests` 47/47, two new: the cli.md `task` row names
  `--tell "<title> | <two to four sentences>"`, CardWords.MaxTitle/MaxLine and the verdict; no shipped
  doc (docs/ minus docs/dev, templates, README) names report.ps1.
  Negative control: a seeded `report.ps1` line in docs/operating.md turns it red
  ("these still name it: docs\operating.md"), reverted.
- Card rig (`HarnessTests.Card_*`) 3/3 after the rig launches through PowerShell: its room has an
  observer, so the prompt now carries the voice and passed the 8191 chars `cmd.exe /c` accepts. cmd
  refused silently and the argv guard let it through because cmd.exe is an .exe - filed as bug #99
  (low), not fixed here. The rig transcript still shows one card with its pair on green, nothing on
  red with the held words in prompt 2, and one stage card.
- Neighbour set after both commits (Harness, Prompt, Battery, Golden, Doctor, KS2_6, SC1Telegram, SF6_3, PK3_2, PK4_*, KS11_1, Architecture, SF7_1): **456/456 passed** (1 m 27 s). The same set before the rig launcher: 441/444, the three card rig tests red.

## 3. report.ps1 deleted from ~/.claude/skills/telegram-notify

- `report.ps1` removed (sha256 prefix d4e4aa3b56cfc188); the folder is not under version control, so
  a copy of the folder as it was is kept outside every repo under the temp directory.
- SKILL.md: description, the "Answering" lead-in, the "Posting a checkpoint" section (now: not from
  this skill - claim with --tell, the engine posts at the verdict) and two rules lines rewritten;
  character.md's two report.ps1 sentences now read as docs/rooms/character.md does.
- `grep -r report.ps1` over the skill folder: 0 matches.

## 4. The field plans' promptExtra rules (other repos: edited, nothing committed)

Exact-string replace of the JSON-escaped rule, then ConvertFrom-Json of the whole file; each parsed
promptExtra names `--tell "<title of about six words> | <two to four sentences>"` and no report.ps1;
no brace in any new rule; `git diff --stat` = one line per file.

| file | line | rule |
|---|---|---|
| C:/Code/bg/plans/conductor.plan.json | 205 | 11 |
| C:/Code/bg/plans/conductor.feel.plan.json | 150 | 11 (the TLS sentence about report.ps1 dropped) |
| C:/Code/BookToCourse/conductor.hardening.plan.json | 133 | 17 |

pdf-challenge/conductor.plan.json names no report.ps1 - nothing to change. `conductor ps` showed no run
live in any of those repos. Still naming report.ps1, deliberately untouched (not plan files):
BookToCourse `templates/session.md` line 86 (step 5), and bg's in-repo skill copy under `.claude/`.
