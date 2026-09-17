# PK5.1 - inbound with a name (D9; findings F-COUR-5, F-COUR-6, F-OBS-4)

Session 10, 2026-09-17. Code commit 321c73b; proof script, docs and this file in the commit that adds it.

## Acceptance, as declared before the first edit, and what shows it

| # | Acceptance | Shown by |
|---|---|---|
| 1 | A note filed from a rig update with a `from` object carries `MessageId`, `SenderId`, `SenderName`, `SenderUsername` on disk | `PK5_1InboundWithANameTests.A_note_filed_from_a_rig_update_carries_its_sender_and_message_id` (reads the JSON file); live rig A1, the note JSON is printed in `pk5.1-live-proof.log` |
| 2 | A note written before PK5.1 still reads and lists; `inbox list` gains `from` and `msg` | `A_note_filed_before_PK5_1_still_reads_and_lists_with_no_sender`; rig A4: the fresh engine's table lists note 83806400 (pre-PK5.1 shape) with `-` beside Ada's row `Ada Lovelace (@ada_l)`, `41` |
| 3 | The courier acknowledges a filed note with `setMessageReaction` on its own message id and sends NO message; refusals stay words | `Every_kind_of_filed_note_is_acknowledged_by_a_reaction_on_it_and_never_a_message` (text + every `InboundMediaKind`, iterated from the enum, so a new kind fails until pinned); `A_file_too_big_to_fetch_is_refused_in_words_and_the_note_still_gets_its_reaction`; rig A2: calls after the note = `setMessageReaction` only |
| 4 | The Promote button rides only the reply to `/note` | `Note_asked_as_a_reply_names_the_note_its_sender_and_project_and_carries_the_button`; `DV4_4PromotionTests` courier cases now ask with `/note` and press the button from that reply; rig A3: one `sendMessage`, no reaction, nothing filed |
| 5 | `say --reply-to` takes a note id or a message id | `Say_reply_to_a_note_id_answers_that_notes_message_in_the_chat_it_came_from`, `Say_reply_to_a_number_that_is_no_note_is_a_message_id_as_before`, `Say_reply_to_an_old_note_or_to_a_note_of_another_chat_is_refused_by_name`; rig A5 through the fresh engine |
| 6 | `walk-ids.ps1` deleted from the skill | `~/.claude/skills/telegram-notify/walk-ids.ps1` removed (sha256 E1616F0B...8BEB; copy of the folder at `%TEMP%\pk51\skill-backup`); its SKILL.md section and watch-live/SKILL.md rewritten to `inbox list` + `say --reply-to`; zero `walk-ids` references left under `~/.claude/skills` |
| 7 | The no-authority rule restated in the note's own file header | `src/Conductor.Core/Inbox/InboxNote.cs` summary: "Identity is for addressing, never for permission"; repeated on `InboundNote`'s sender params and `NoteLookup` |
| 8 | One real reaction on a real message in the admin DM, then cleared | rig B, below |

## The real reaction (rig B)

- ONE message to the admin DM `99205495`, sent directly by the fresh `say` through a relay: **message id 3854**.
- The fresh `conductor-courier.exe`, holding the environment's token, was pointed at the relay. The relay
  answered `getUpdates` locally with a SYNTHESIZED update naming message 3854 (so the real courier's
  `getUpdates` was never touched), and forwarded only `sendMessage`, `setMessageReaction`, `deleteMessage`.
- The courier filed the note (MessageId 3854) and its reaction reached Telegram: `forwarded setMessageReaction -> 200`.
- Cleared: `setMessageReaction` with `reaction: []` -> 200. Deleted: `say --to admin --delete 3854` -> 200.
- Relay log, whole: `sendMessage -> 200 | setMessageReaction -> 200 | setMessageReaction -> 200 | deleteMessage -> 200`.

**Measured:** Telegram accepts the bare U+270D writing hand. D9 writes the emoji with the variation
selector; `InboundAck.Reaction` is the bare form, which is how the Bot API's fixed reaction list spells it.

Rig: `tools/peyk/pk5-1-live-proof.ps1 -RealSend` - **25/25**, log `pk5.1-live-proof.log` beside this file.

## Tests

- `PK5_1InboundWithANameTests` 8 new; with `DV4_1CourierTests`, `DV4_4PromotionTests`, `PK3_2SayAndDirectFallbackTests`,
  `KS11_1SeamBoundaryTests`, `ArchitectureTests`: 63/63.
- Docs and inbound neighbours (`SF7_1DocsMatchRealityTests*`, `K7_2DocsVerbCoverageTests`, `BugSweep_Round3Tests`,
  `DV3_1/3_2/3_3/3_4`, `PK3_1CourierProtocol3Tests`, `DV4_3CourierSeamTests`): 156/156.
- Two existing assertions changed with the behaviour D9 changes, neither weakened: `DV4_1` "Filed against" reply ->
  a single `setMessageReaction` on message 2 and only the `/project` reply as a message; the kill test's "told once"
  count now counts reactions AND messages (was messages only).

## Scope, stated

- The ack change is the COURIER's (`CourierDaemon.Notes.cs`). The in-run `RemoteSurface` path, which only runs on a
  machine with no courier, still acknowledges media with a message and the button: `IMessageChannel` has no reaction.
  Notes it files do carry the four fields - both producers now build the record through one `InboundNote.ToInboxNote`.
- Takes effect on this machine at the owner's courier reinstall; the armed real courier was not touched.
- Outside this repo, not committed: `C:/Code/bg/.conductor/WATCH-HANDOFF.md` and `C:/Code/BookToCourse/docs/FEEDBACK-LOOP.md`
  still mention walk-ids.ps1 as history; left alone.
