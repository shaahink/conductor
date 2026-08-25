# DV3.4 — routing: which project is this note about?

**Checkpoint:** a voice note sent as a reply to a checkpoint push files against that push's project
with no command typed; sticky `/project` selection; `message_thread_id` topics in supergroups;
unknown slug refused by name; unroutable notes parked in a machine-level dead-letter directory,
never dropped.

**Session:** divan #7 · 2026-08-26 · branch `feat/divan`.

---

## 1. The acceptance, line by line

| Acceptance | Where it lives | Proof |
|---|---|---|
| reply-to-a-push routes with NO command typed | `NoteRouter.Route` + `NoteRouter.PlanNameIn` (`NoteRouter.cs`) | `A_reply_to_a_push_files_against_that_pushs_project_with_no_command`, and end to end in `A_voice_note_replying_to_another_projects_push_lands_in_that_projects_inbox` |
| sticky `/project`, surviving a restart | `ChatRoutes.cs` (a FILE under the state home), `/project` in `CommandRouter` + `RemoteSurface.Routing.cs` | `A_sticky_selection_outlives_the_process_that_set_it` (a second `ChatRoutes` object reading the same disk = a restarted engine), `Project_sets_a_selection_the_next_note_obeys` |
| topics in supergroups | the sticky key is chat **and** `message_thread_id`; `HandleMessageAsync` now carries the topic | `A_topics_selection_is_its_own_and_does_not_become_the_chats` |
| unknown slug refused BY NAME | `ProjectDirectory.Resolve` | `An_unknown_project_is_refused_by_name_and_says_what_this_machine_has`, `A_reply_to_a_push_from_an_unknown_project_is_refused_not_guessed`, plus ambiguity: `An_ambiguous_name_is_refused_with_both_candidates_named` |
| unroutable parked, never dropped | `DeadLetterBox` (`NoteRouter.cs`), `RemoteSurface.Inbound.FileNote` | `A_project_whose_checkout_is_gone_is_unroutable_and_the_note_is_parked_with_its_audio`, `A_note_for_a_vanished_checkout_is_parked_and_the_sender_is_told` |

## 2. What the code DOES

- **The ladder, in one method** (`NoteRouter.Route`): reply-to-push → topic selection → chat
  selection → the run that received it. Every rung is a weaker claim about intent, so the
  acknowledgement always names which one answered (`NoteRoute.Describe()` → "Filed against payesh
  (the run you replied to)").
- **The parse is strict.** `PlanNameIn` requires the identity line's shape — `<plan> · s<n>` — so a
  person's own message containing a middle dot is not mistaken for a push. Six negative cases pinned.
- **The project list is not a new concept.** `ProjectDirectory` reads `StateCatalogue` (K3's
  machine-level catalogue) and folds in the local run so a fresh run can receive a note about itself.
  Matching is exact on slug, plan name or repo folder — no prefixes, no fuzz. Two clones of one plan
  are refused with both slugs named.
- **The media travels with the note.** The channel downloads a file before anything knows where the
  note belongs, so `InboxStore.AdoptMedia` moves it into the routed project's inbox (copying if the
  move fails, never deleting), and a name collision becomes `voice-2.oga` rather than an overwrite.
- **Nothing is dropped.** A note that resolves to nothing, or to a checkout that has gone, is written
  to `<state home>/dead-letter/` with its audio and the reason; the sender gets "Kept, not filed —
  … It is parked at <path> and nothing deletes it." `conductor inbox parked` lists them.

## 3. Tests

```
dotnet test --filter FullyQualifiedName~DV3_4   → 16 passed, 0 failed
dotnet test --filter FullyQualifiedName~KS11    → 134 passed, 0 failed
dotnet build Conductor.slnx -clp:ErrorsOnly     → 0 errors, 0 warnings
```

13 unit tests over the ladder, the parse, the refusals, the dead-letter box and media adoption; 3
surface journeys with the store, the router, the acks and the box all real and only the messenger
faked. Every rig here passes its own temp directory as the state home — bug #73 in this repo's ledger
is a rig that wrote into the operator's real one, and nothing in these files may repeat it.

`KS11_2CommandMatrixTests` gained one case: bare `/project` is a QUESTION ("which project do notes
here go to?") and answers, where bare `/inject` is silent. That is an addition to the matrix's
expectation table for a new verb, in the same shape `/chat` already had — not a relaxation of it.

## 4. What is NOT done here

- **The courier still does not exist.** Routing is wired into the run's own Telegram service, so today
  it routes notes the RUNNING engine receives. DV4.1 moves polling to the daemon; the router,
  `ChatRoutes` and the dead-letter box are all machine-level already so they move with it unchanged.
- **A parked note is moved back by hand.** `conductor inbox parked` lists them; there is no
  "re-file it now that the checkout is back" verb yet.
