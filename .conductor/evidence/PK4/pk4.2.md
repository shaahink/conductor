# PK4.2 - the card: words at the claim, the card at the verdict

Generated 2026-09-17T14:22:25Z on commit 1a90426 from `.conductor/pk42-proof.sh` (bg log `.conductor/bg-logs/pk42-proof-20260917-142007101.log`).

## Acceptance, and what shows it

| Acceptance | Result | Artifact |
|---|---|---|
| a green claim posts exactly one card with the right bar, counts and pair | PASS | `HarnessTests.Card_GreenClaim_PostsExactlyOneCard_WithTheBarTheCountsAndThePair` - one sendMediaGroup to the observer chat, 2 photos (last part H0.1-after.png), bar 5/10 for 1 of 2 confirmed, 0 live, pending footer; the words reach no other chat |
| a red claim posts nothing and the next prompt carries the held words | PASS | `HarnessTests.Card_RedClaim_PostsNothing_AndTheNextPromptCarriesTheHeldWords` - 0 calls to the observer chat over 2 sessions; `logs/session-002.prompt.md` carries the held words, session-001's does not |
| a stage confirm posts one stage card | PASS | `HarnessTests.Card_StageConfirm_PostsOneStageCard_AfterTheCheckpointCard` (perPhase) - checkpoint card (live, full bar) then ONE stage card listing the agent's commit subject, no chore(conductor) |
| --tell stored on the claim event, refused when malformed | PASS | PK4_2CardTests (13) - TaskStatusChanged.Tell -> TaskItem.Tell; no '|', title > 120, sentences > 700, non-done status refused by name |
| stages[].deploys, else the default live rule | PASS | PK4_2CardTests.TheDefaultLiveRuleIsTheLastCheckpointOfAStageAndCountsOnlyConfirmed |
| the card is a NotifyTemplate with bar, done, total, live, title, line, footer | PASS | NotifyDefaults.CheckpointCard / StageCard (placeholders in the template text), MessageComposer.Cards.cs; all seven are facts, plus counters, stage, checkpoint |

Every rig claim went through the REAL verb: the fake agent runs the test bin's `conductor.exe task --done H0.1 --evidence ... --tell "..."` inside the session (CONDUCTOR_PLAN exported by the engine), under a real `RunCommand` with real gates, the evidence watcher and a recording Bot API. The room is a room file in the test process's state home; the plan's telegram block names the admin chat only.

## What the observer chat received (rig transcripts)

### Card_GreenClaim_PostsExactlyOneCard_WithTheBarTheCountsAndThePair

```
observer chat received 1 call(s)
- sendMediaGroup (2 photos, the last H0.1-after.png, 25 B)
    <b>Cards leave at the verdict</b>
    █████░░░░░  1/2 fixed · 0 live
    The engine posted this card after the gates went green. The session only wrote these words.
    <i>Lands with H0</i>
claim verb output:
    checkpoint H0.1 → DONE
```

### Card_RedClaim_PostsNothing_AndTheNextPromptCarriesTheHeldWords

```
observer chat received 0 call(s)
next prompt (session-002.prompt.md), the held-words lines:
    Words for the room are HELD - claimed with --tell, not yet confirmed, so no card has been posted:
    - H0.1: Cards leave at the verdict | The engine posted this card after the gates went green. The session only wrote these words.
    The engine posts each card when the verdict confirms its claim. Do not post them yourself; a re-claim with --tell replaces the words.
claim verb output:
    checkpoint H0.1 → DONE
```

### Card_StageConfirm_PostsOneStageCard_AfterTheCheckpointCard

```
observer chat received 2 call(s)
- sendMediaGroup (2 photos, the last H0.1-after.png, 25 B)
    <b>Cards leave at the verdict</b>
    ██████████  1/1 fixed · 1 live
    The engine posted this card after the gates went green. The session only wrote these words.
    <i>Live now</i>
- sendMessage
    <b>H0 · Cards</b>
    ██████████  1/1 fixed · 1 live
    • feat: deliver the card checkpoint
    <i>Live now</i>
claim verb output:
    checkpoint H0.1 → DONE
```

## Test runs

```
== rig
  Passed Conductor.Tests.HarnessTests.Card_GreenClaim_PostsExactlyOneCard_WithTheBarTheCountsAndThePair [5 s]
  Passed Conductor.Tests.HarnessTests.Card_StageConfirm_PostsOneStageCard_AfterTheCheckpointCard [5 s]
  Passed Conductor.Tests.HarnessTests.Card_RedClaim_PostsNothing_AndTheNextPromptCarriesTheHeldWords [6 s]
Test Run Successful.
== neighbours
Passed!  - Failed:     0, Passed:   596, Skipped:     0, Total:   596, Duration: 1 m 25 s - Conductor.Tests.dll (net10.0)
== done
```

## Found on the way (measured, fixed, in this checkpoint)

- The first rig run posted the card as TEXT with both images registered: the card was composed before the async event drain persisted EvidenceRegistered. MessageComposer.Cards flushes the store before reading (1a90426).
- A stage is CONFIRMED only under gatePolicy perPhase (VerdictEngine.Evaluate: PerPhaseGates && StageComplete schedules the phase gate). Under perSession a plan completes without StageConfirmed, so no stage card - the rig pins perPhase, the policy the field plans run.
- The architecture ratchet (3 types per file, RunLoop.cs at 500 lines) failed on Room.cs (PK4.1) and on the first cut of this checkpoint; fixed in 80743dd.
