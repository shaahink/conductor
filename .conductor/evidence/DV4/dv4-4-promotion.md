# DV4.4 — promotion: a note becomes work by one tap, and never by itself

Stage DV4, session #13, 2026-08-26. Branch `feat/divan`.

Findings §1.7 (the three tiers), §1.8 (the risk), §1.9 row 6 (the falsifiable exit):
*"A note promoted in the chat appears as a `followups.md` row and opens a lane in the rig."*

---

## What was delivered

| Piece | Where | What it does |
|---|---|---|
| The button | `RemoteSurface.Inbound.cs:69`, `CourierDaemon.cs:236` | Every acknowledgement of a FILED note carries one `MessageButton` / `CourierButton`: `📌 Promote to followup`, payload `promote:<slug>:<noteId>`. |
| The payload | `Inbox/NotePromotion.cs:76` `NotePromoter.Callback` | 64-byte Bot API ceiling enforced; a slug that will not fit is **dropped**, not truncated, and the press falls back to the chat's own route. |
| The press, in-run | `CommandRouter.cs:238` → `SurfaceAction.Promote` → `RemoteSurface.Inbound.cs:PromoteAsync` | Parsed BEFORE the generic `action:intent:confirmed` split, which would have read the note id as an intent. |
| The press, courier | `TelegramCourierSource.cs:101`, `CourierDaemon.cs:159` | **New capability.** See "the defect the wire found" below. |
| The row | `FollowupWriter.cs` (new) | Appends a parser-round-tripping row, pipe/newline sanitised, idempotent per note, id allocated within its own `FU-NOTE-` series. |
| The lane | `FollowupParser.ClaimStage`, `LaneCoordinator.cs:238` | An unclaimed (`next`) row is claimed by the first stage to reach it, BEFORE the lane runs. |
| The refusal | `DV4_4PromotionTests.No_file_that_handles_an_inbound_note_can_reach_the_injection_api` | Auto-inject refused by design, asserted as an absence. |

## The exit, met

`DV4_4PromotionTests.A_promoted_row_opens_a_tier_b_fix_lane_and_is_closed_by_it` — a real git repo
under the temp directory, a note filed into its inbox, promoted through `NotePromoter` with **no
stage** (the courier's case), then `LaneCoordinator.RunFollowupFixLanesAsync("DV6")`:

- `MutatingLaneStarted` with `LaneId == "fix-fu-note-01"` — the lane opened from the promoted row;
- a real worktree, a real agent commit, a real merge gate, `MutatingLaneFinished { Outcome = "success" }`;
- `README.md` in the PRIMARY tree carries the change, so the lane merged;
- the row afterwards reads `owning stage = DV6` (claimed) and `status = CLOSED …` — it fires once.

## The wire, for the courier half

The acknowledgement the daemon actually POSTed to the loopback stand-in for api.telegram.org,
verbatim from the run (`DV4_4PromotionTests` test output):

```json
{"inline_keyboard":[[{"text":"📌 Promote to followup",
  "callback_data":"promote:alpha-repo-alpha-repo-4aca2aa3:1"}]]}
```

and after the press, on a machine with **no run alive**:

```
courier: Promoted — note 1 of alpha-repo is FU-NOTE-01
```

`answerCallbackQuery` is recorded on the wire in the same test — the Bot API's obligation on a press,
which nothing in the courier honoured before.

## The defect the wire found (would not have shown at the seam)

`TelegramCourierSource.DeliveryOfAsync` began `if (update.Message is not { } msg) return
CourierDelivery.Ignored(...)`. A `callback_query` update has **no `message`**, so every press was
turned into an Ignored delivery and the offset advanced past it — silently discarded, never
answered. Telegram permits exactly one `getUpdates` consumer per token, so on a machine where the
courier owns the token there is no second component that could have picked it up: the button would
have been decorative on the only path that matters (findings §1.4-B is that the courier is the one
awake when nothing is running). Fixed by `CourierCallback` on `CourierDelivery`, a callback branch in
`DeliveryOfAsync`, `AnswerCallbackAsync` on the adapter, and `ICourierSource.ReplyAsync` widened with
buttons.

## The second defect, found by a test written for something else — bug #78, fixed here

`FollowupParser.MapHeader` compared column names **case-sensitively** (`c is "id"`) while header
DETECTION is case-insensitive (`cellEq(cells, 0, "id")`). The header
`VerdictEngine.ParseAuditFollowups` writes is `| Id | Item | Stage | Status |` — capitalised. It was
recognised as a header, mapped to **no id column at all**, and every row beneath it was then skipped
for want of one. The mapping persists until the next header replaces it, so it took out later
sections too.

**Consequence: every followup the audit phase has ever written was invisible to `ReadOpenForStage`,
and therefore never opened a fix lane.** One-line fix (`cells[i].ToLowerInvariant()`), pinned by
`A_capitalised_header_does_not_blank_out_every_row_beneath_it`. Widening a match can only make rows
visible; it cannot lose one.

Related placement decision, also measured: the promoted section is created **above** the first
existing `##` section, never trailing. `ParseAuditFollowups` appends its four-column rows at EOF
under whatever header is last; a trailing five-column promoted header would have reinterpreted every
audit row that followed it. Pinned by `The_promoted_section_never_becomes_the_trailing_section`.

## Test run

`dotnet test Conductor.slnx --no-build --filter` over DV3_*, DV4_1/2/3/4, KS11_*, KS4_1, B12_*,
B6_1Telegram, SF7_1, K7_1 —
log `.conductor/bg-logs/DV4.4 evidence run 2-20260826-101344519.log`:

```
Total tests: 406
     Passed: 405
     Failed: 1
```

All 17 `DV4_4PromotionTests` pass:

```
A_capitalised_header_does_not_blank_out_every_row_beneath_it
A_live_run_acknowledges_with_the_button_and_the_press_writes_the_row
A_pipe_or_a_newline_in_what_was_said_cannot_break_the_table
A_promote_press_can_only_ever_produce_a_promotion
A_promoted_row_opens_a_tier_b_fix_lane_and_is_closed_by_it
A_promoted_row_round_trips_through_the_parser_that_opens_lanes
An_armed_chat_that_sends_a_voice_note_files_it_instead_of_injecting_it
An_observer_pressing_promote_is_refused_and_writes_nothing
An_unclaimed_row_is_offered_to_any_stage_and_claimed_by_the_first
Ids_advance_within_the_promoted_series_only
No_file_that_handles_an_inbound_note_can_reach_the_injection_api
Only_the_bare_token_is_unclaimed
Pressing_the_button_twice_writes_one_row
The_callback_payload_never_exceeds_the_bot_api_limit
The_courier_draws_the_button_and_a_press_becomes_a_row_with_no_run_alive
The_courier_refuses_an_observers_press
The_promoted_section_never_becomes_the_trailing_section
```

### The one failure is NOT this checkpoint's — bug #77

`DV3_3TranscriptionTests.Prune_is_the_only_code_in_the_engine_that_deletes_an_inbox_file`.
**Measured, not inferred**: it fails identically on `a179857` with this work `git stash`ed. Its
offenders are `CourierDaemon.Discard` (DV4.1's orphan-media cleanup) and `CourierPresence.Clear`
(DV4.3's presence teardown) — neither deletes a note; the sweep's allowlist (`Prune`, `TryDelete`)
simply predates both. Filed as bug #77 rather than fixed here: widening another checkpoint's
architecture-test allowlist inside a promotion commit is exactly the move this plan forbids.

## Admin-only, unchanged and re-asserted

Filing is `ChatProfiles.MayFile` (admin) at four call sites; the in-run press is refused by
`CommandRouter.cs:232` for every non-admin profile; the courier had no such gate and states its own
now (`CourierDaemon.cs:162`). `An_observer_pressing_promote_is_refused_and_writes_nothing` and
`The_courier_refuses_an_observers_press` pin both halves. The reporter profile stays parked with the
owner — out of scope, as the stage note says.

## Scope not taken

The findings' tier table offers buttons for **both** other tiers. Only the followup button is drawn.
The inject button is refused by design and there is a negative test to that effect; that is the
checkpoint's instruction, not an omission.
