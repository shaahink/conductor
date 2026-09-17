# PK5.2 - the figure verbs answered by the courier from run.db, read-only

Session 11, 2026-09-17. Spec: D10, F-COUR-7 (docs/dev/NEXT-ERA-FINDINGS-2026-09-17.md, stage PK5).
Commits: 6bd8150 (courier figures + tests), e9d40a6 (evidence ordering, live rig, docs/cli.md).

## Acceptance (declared before editing, ledger note s11)

| # | What must be true | Artifact | Result |
|---|---|---|---|
| 1 | With no run live, `/money` in a rig chat answers the same figures `conductor money` prints for that store | `pk5.2-live-proof.log`, `pk5.2-rig/engine-money.json`, `pk5.2-rig/replies/reply-02.json`; test `With_no_run_live_money_in_a_rig_chat_answers_the_figures_conductor_money_prints` | PASS: $124.46 billed, 9 sessions, $10.37 per checkpoint (12 closed) in both |
| 2 | `/tokens`, `/progress`, `/status` answered from the same store; `/status` = `conductor status`'s report | rig checks + `pk5.2-rig/engine-status.txt`; test `Tokens_progress_status_and_evidence_answer_from_the_same_store` | PASS: verdict, checkpoints 12/17, sessions 11, $124.39 identical |
| 3 | `/evidence` (bare and by checkpoint) from the evidence registry | `reply-06.json`, `reply-07.json`; same test | PASS |
| 4 | A write to any store is asserted absent | test `No_figure_verb_writes_to_any_store` (every file under state home + checkout: sha, length, mtime, no -wal/-shm); rig: 5 files before/after, copy has no sidecars | PASS |
| 5 | Observer may ask (Browse scope), still may not file; property over the verb list | test `Every_figure_verb_is_a_browse_verb_an_observer_may_ask` | PASS |
| 6 | A live writer holding the store does not stop the answer | test `A_live_run_holding_the_store_is_answered_too` | PASS |
| 7 | In-run handlers untouched; neighbours green | KS11_4/KS11_5 + courier/architecture/status/money neighbours | 551/551, run twice (at 6bd8150 and again with e9d40a6's source) |

ADR-0005 (push-only remote observability; the phone cannot change what the run decides) and ADR-0008
condition 2 (the courier is ingress for notes, never for run state) are cited in
`src/Conductor.Core/Courier/CourierFigures.cs` and `CourierDaemon.Figures.cs`, and held by test 4 and by
`The_courier_sources_never_open_a_writable_store_or_resolve_state` (no `new SqliteRunStore(`,
`StateHome.Resolve(`, `.ResolveState()`, `StateCatalogue.Upsert(` under `Core/Courier`).

## As built

- `CourierFigures` (Core/Courier): resolves the project's plan file (repo, `plans/`, one folder under
  it, matched by `name`; a bare plan when none loads, logged through the courier log), the database with
  zero side effects (repo state pointer, else the catalogue entry, else `StateHome.Peek` without the env
  override), pins the plan to that resolution (`PlanConfig.PinState`), opens `SqliteRunStore.OpenReadOnly`,
  takes `GetLatestRunId(plan)`. Money/tokens/progress are `MessageComposer`'s (RunState from the saved
  `run_state` row, fold as fallback); status is `StatusReportBuilder.Build`; evidence is
  `EvidenceRegistry.From(events)`.
- `CourierDaemon.Figures.cs`: a figure verb is routed BEFORE the filing gate, allowed per
  `SurfaceCommands.AllowedFor(profile)` (all five are Browse), project by the note's own ladder
  (reply-to-push, else `/project` selection). SQLite/IO failures answer in words and log.
- `StateHome.Peek(..., honourEnvOverride)`: a machine-level reader cannot let one env var stand in for
  every project.

## Measured, not read off a comment

- `PlanConfig.RunDbPath` is a WRITER: `ResolveState()` -> `StateHome.Resolve` -> `StateCatalogue.Upsert`
  stamps `lastSeenUtc` on every call and may import a legacy db (`StateHome.cs` Resolve,
  `StateCatalogue.cs` Upsert doc). `MoneySection.Read` goes through it, so an unpinned plan would have
  written the catalogue on every `/money`. Hence the pin.
- The registry on this era's store: 471 artifacts, one sweep, one `CreatedUtc` (15:23Z); newest ten by
  registration were SF5 files. Bare `/evidence` therefore orders this plan's stages first, then other
  checkpoints, then unclaimed (reply-06 now leads with pk5.1 / PK5.1 / PK4.3 ...).
- A sqlite3 CLI open of a WAL-mode copy leaves `-wal`/`-shm` beside it (first rig run failed its
  "no run live" check on exactly that); the rig reads the run id from the live store read-only instead.
- `/status` on the copy says "running - session #11 in PK5" because the copied open session record's
  process (this session) is alive; the fresh `conductor status` prints the same verdict for the same copy.

## Live rig (tools/peyk/pk5-2-live-proof.ps1) - 27/27 at e9d40a6

Scratch state home, scratch checkout carrying this era's plan name, stub Bot API keeping every
sendMessage body, scratch token `111111:pk52-scratch-token`, scratch chat 770000052, scratch courier
port. The store is a `sqlite3 -readonly .backup` copy of the live store placed where the fresh engine's
resolver catalogued the checkout; the live store is only ever read. No real Telegram message was sent;
the real courier, its home, task and port were not touched.

## Not done / open

- `/evidence <id>` names files; it does not send them (bug #76, courier file upload, still open).
- An observer chat cannot run `/project` (Steer), so an observer group gets figures when it replies to
  a push or when its selection was set; unchanged routing rule, stated here.
- Takes effect on the real courier only at the owner's reinstall (trap 4).
