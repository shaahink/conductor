# DV6.3 — the board snapshot as one self-contained HTML file

Measured 2026-08-26, session 18. Everything below is a file:line in this commit or a test that ran.

## Acceptance, declared before editing (ledger, session 18)

| # | Bar | How it is proven |
|---|-----|------------------|
| 1 | ONE self-contained HTML file rendered from the EXISTING Http/Contracts | golden `tests/Conductor.Tests/testdata/dv6-3/board.html` + `The_page_reaches_out_to_nothing` |
| 2 | The page states its own staleness | `The_page_says_when_it_was_rendered_which_boundary_made_it_and_that_it_does_not_update` |
| 3 | Rendered at each boundary, pushed as a Telegram DOCUMENT | `FullCycle_BoardPage_IsRenderedAtTheSessionBoundary` (real run) + `The_board_page_arrives_as_a_document_not_as_a_path` (wire) |
| 4 | No inbound anything — ADR-0005 holds | `The_publishing_path_opens_no_port_no_listener_and_no_tunnel` (source scan) |

## What was built

- `src/Conductor.Core/Publishing/BoardSnapshot.cs` — the model: `StateDto`, `TasksDto`,
  `OwnerQueueDto`, `EvidenceArtifactDto[]`, the DV6.1 ledger line, plus the two facts a FILE needs
  and a live view does not — the render instant and the boundary label.
- `src/Conductor.Core/Publishing/BoardSnapshotHtml.cs` — the render. Columns, cards, age-in-column,
  cost, owner queue with the clearing command, evidence, footer. Styles inline; no script, no font,
  no image, no link.
- `src/Conductor.Core/Publishing/BoardSnapshotPublisher.cs` — compose + render + atomic write to
  `<stateDir>/board.html`; returns the page AND the model it was rendered from, so the caption cannot
  state a different board.
- `src/Conductor.Core/Orchestration/RunContext.Board.cs:22` — `PublishBoard`, the boundary hook.
- `src/Conductor.Core/Orchestration/RunLoop.Plumbing.cs` — called from `EmitSessionFinished`, on the
  same beat as the GitHub mirror and AFTER the tracker regeneration, so the page shows the board the
  tracker just showed.
- `TelegramService.cs` (`IRunNotifier.PushBoardSnapshotAsync`, defaulted) → `RemoteSurface` →
  `OutboundAttachment(path, AsPhoto: false, caption)` → the existing multipart `sendDocument` path.
- `NotifyDefaults.Board` + `MessageComposer.BoardCaptionAsync` — the caption in CH-5's grammar.

## Reuse rather than a second copy of an answer

Three numbers that already existed were being computed in one place and needed in two. Each was
MOVED, not copied:

- `ControlPlaneMapper.WithBudget` (`src/Conductor.Core/Http/ControlPlaneMapper.cs:65`) — was
  `internal static` inside `ControlPlaneServer` (server assembly). `ControlPlaneServer.WithBudget`
  now forwards to it (`src/Conductor/Http/ControlPlaneServer.State.cs:171`), so spend-vs-cap is one
  arithmetic and the six existing KS5/SC23 tests still call the old name and still pass.
- `LedgerSummary.Line` (`src/Conductor.Core/LedgerSummary.cs:25`) — DV6.1's digest line.
  `MessageComposer.LedgerLine()` now delegates to it in one line; the digest's words are unchanged
  and its goldens are untouched.
- `ControlPlaneMapper.FromArtifact` (`src/Conductor.Core/Http/ControlPlaneMapper.cs:121`) — the
  registry row → wire DTO projection, previously inlined in two handlers.

## The columns are DV6.2's columns

`BoardSnapshotHtml.ColumnName` reads `GithubProjectColumns.Preferences(status)[0]`, so the page's
five headings — Todo / In Progress / Blocked / Done / Skipped — are exactly the first choice the
Projects v2 mirror makes for the same status. The page and the board cannot grow two vocabularies.

## What the page refuses to guess

- A card with no `statusSinceUtc` renders **age unknown**, never "0 days" — SF3.2's rule, kept.
- An owner item with no command says **nothing typed clears this**, rather than inventing one.
- An empty ledger renders **no ledger row** — DV6.1's rule ("0 open bugs" every day teaches a reader
  to skip the line that will one day say eleven).
- A status with no column is **reported** ("1 card in no column — status quarantined"), never dropped.
- A cost cap that is absent renders "no cap set", never "$0.00 remaining".

## Publish, not serve

`The_publishing_path_opens_no_port_no_listener_and_no_tunnel` greps the four files of the publishing
path for `HttpListener|TcpListener|Socket|.Prefixes|Bind(|ngrok|cloudflared|tailscale|Funnel` and
fails on a hit. The page's own footer says the same thing to its reader.

## The document, on the wire

`The_board_page_arrives_as_a_document_not_as_a_path` drives a real `TelegramService` with a SCRATCH
token against `RecordingBotApi` (a loopback stub at `TelegramConfig.ApiBaseUrl`) and asserts what
left the process: method `sendDocument`, part name `document`, filename `board.html`, > 4000 bytes
uploaded (the file, not a path), `disable_notification=true`, and a caption carrying
"as of 2026-08-26 12:00 UTC … it does not update".

## The boundary, on a real run

`FullCycle_BoardPage_IsRenderedAtTheSessionBoundary` runs the harness's fake agent through one whole
session and then reads the scratch repo's own `.conductor/board.html`: it exists, it says
`session 1 end`, it says `does not update`, it starts `<!doctype html>`, it reaches out to nothing,
and no `.tmp-` litter is left beside it.

## Test results

Fast loop, this commit, 2026-08-26:

- `dotnet build Conductor.slnx -clp:ErrorsOnly` — **0 errors, 0 warnings**. The analyzer bar held
  without a single pragma: the boundary hook lives in `RunContext.Board.cs` rather than in `RunLoop`
  because adding two type references to `RunLoop` tripped **CA1506** (185 coupled types against a bar
  of 183). That is the ratchet doing its job, not a bar being moved.
- `dotnet test --filter "…DV6_3BoardPageTests|…FullCycle_BoardPage"` — **15 passed, 0 failed**
  (14 unit + wire, 1 full-cycle run).
- Regression scope for everything this touched —
  `KS11 | K5_4 | DV1_2 | DV6_ | SC23 | KS5_ | ControlPlane | Harness` —
  **478 passed, 0 failed, 1 m 55 s**
  (`.conductor/bg-logs/dv63b-20260826-132018502.log`). That set is chosen, not arbitrary: KS11 owns
  the grammar goldens the new caption sits beside, K5_4 the multipart transport it rides, DV6_1 the
  ledger line that moved, and SC23/KS5_ the six tests that call `WithBudget` through its old name.

## The one runtime trap this cost

`DateTime.TryParse(s, culture, DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, …)`
compiles and **throws ArgumentException at runtime** — the two flags are mutually exclusive. It only
fires when a card actually carries a `statusSinceUtc`, so a page renders happily in a test with no
stamps and dies on a real board. `AssumeUniversal | AdjustToUniversal` is the correct pair and is
also the safer read: an unstamped older event is then UTC rather than the operator's local time,
which would otherwise make every age on the page wrong by their offset, invisibly. Recorded in the
knowledge ledger.

## What this does NOT do

- No static host. The strand doc offers Telegram document **and/or** a private static deployment;
  this is the document half, which needs no infrastructure and no secret. Payesh stays a DV7 concern.
- No per-boundary suppression. The page is rendered and pushed at every session boundary, exactly as
  the ranking asks. If that proves noisy in practice it is a threshold to add with a measurement,
  not a guess to build in now.
- Evidence is listed as PATHS, not links. The file is read on a phone; a `file://` link to the
  engine's disk is a link that never opens, and the footer says which machine the paths are on.
