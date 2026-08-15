# GitHub as the visible board and the durable history — design assessment

*2026-08-13. Research pass over the tree at `1632b9f` (feat/karvan, v0.4.0 tagged), the karvan
plans, and the NEXT-ERA record. Written when nothing here was implemented, so that the
implementation would start from the decisions already taken instead of re-deriving them.*

> **⚠ Status at KS10.1, 2026-08-15 — this is no longer a design-only document.** It became core
> stage KS9 (decision ND-9) and **shipped**, so read the sections below as the reasoning behind code
> that exists rather than as a proposal. **§A and §B are built** — `Core/Integrations/Github/`
> (thirteen files: `GithubClient`, `GithubMirror`, `GithubBoardSync`, `GithubMap`, `GithubIdentity`,
> the DTOs and the request shapes), the `github` verb at `Commands/GithubCommand.cs`, the token in
> `SecretsStore`, and migration `v14_github_cursor.sql` behind `SqliteRunStore.Github.cs`. **§C's
> reconciler is built** and reconciles over `ReadEventsAfter` from a persisted cursor, batched and
> network-failure-proof. **The Projects v2 half of §A is NOT built and is SKIPPED, not deferred** —
> the token on this machine carries no `project` scope, and KS9.3's answer was to report the precise
> refusal rather than half-build it. **Nothing inbound was built and nothing ever should be**
> (ADR-0005). Off by default. The first real backfill against a live repository is the owner's, at
> KS10.3 — every proof to date ran against a scratch repo.

## What the owner asked for

Three things, in the owner's framing:

1. **The plan is declarative — when it is handed to conductor, the Kanban should be visible
   somewhere.** Today the board lives in the Face TUI and the loopback control plane, which means
   it is visible only on the machine running the run.
2. **GitHub becomes the history.** A run should leave a durable, browsable record somewhere that
   is not the owner's disk.
3. **Session history should show too** — not just which cards moved, but the sessions that moved
   them, with their outcomes and costs.

## What already existed — the decisions were taken, only the code was missing

*(KS9 wrote that code. This section is the record of what it started from.)*

This is not a new idea in this repo. The trail:

- **C3** (`docs/dev/NEXT-ERA-FINDINGS-2026-08-04.md:444`) — the research finding: GitHub is a
  release client and nothing more; `Core/Update/ReleaseClient.cs` is the only GitHub caller in
  the tree. The owner's ask ("conductor talk to github api for tasks") is recorded there.
- **D-7** (`NEXT-ERA-FINDINGS:853`) — the decision: **one-way, off by default, and only after
  G4** (branch hygiene). Two-way sync is named a trap: the tracker is the verified contract and
  must not become an eventually-consistent mirror of someone else's board (anti-pattern A16).
- **L6.3** (`docs/history/CONDUCTOR-KARVAN.md:634`, `plans/karvan/LANES-TRACKER.md:82`) — the
  authored checkpoint, in the lanes plan, which is **authored but not launched** (0/23 done).
  Its brief: a claim closes or comments on a mapped Issue, the run report can post as a PR
  comment, nothing inbound.
- **L5.3** (`LANES-TRACKER.md:74`) — the adjacent card: the kanban and the history surface become
  lane-aware. A GitHub board mirror should carry the same lane fields when lanes land.
- **ADR 0005** (`docs/dev/adr/0005-push-only-remote-observability.md`) — remote observability is
  push-only; no inbound port, tunnel, or reverse proxy. A GitHub mirror is *compatible*: it is an
  outbound push to a third-party surface, and it is arguably the first remote *read* surface the
  ADR's posture permits. "See the run from my phone" becomes "open the GitHub Project."

**The constraint to hold onto: one-way.** GitHub is a *view and an archive*, never an ingress.
The engine already has exactly one write path for task state (`Events/TaskWrites.cs`, used by
MCP, HTTP, and CLI alike); a GitHub sync must be a **reader** of the event log, never a fourth
writer. If a human drags a card on the GitHub board, nothing happens in the run — and that is
correct behaviour, worth a line in the docs when this ships.

Also worth recording: **`shaahink/conductor` has zero GitHub issues today**, and the owner's
current `gh` token has `repo` scope but not `project` — Projects v2 needs a one-time
`gh auth refresh -s project` before any of part B below can be exercised.

## The shape: three pushes, one reconciler

### A. The board mirror — plan → Issues (+ optionally a Projects v2 board)

One Issue per work item, created and moved by the engine:

| TaskItem field (`Models/TaskItem.cs`) | GitHub |
|---|---|
| `CheckpointId` + `Title` | Issue title: `K4.2 — Title` |
| `Status` (todo / in_progress / blocked / done / skipped) | open/closed + a `status:*` label, and the Project's Status column when a board is configured |
| `StageId` | Milestone (one per stage) or a Project single-select field |
| `Source` (plan / tracker / import / human / agent) | label `source:*` |
| `Confirmed` (engine-verified vs agent-claimed) | label `confirmed` — **the distinction the tracker's `DONE ✓` carries must survive the mirror** |
| `Commit`, `Evidence` | issue comment with the commit link (`Reporter.RemoteUrl` + `FormatCommitLink` already build these) and the evidence path |
| `StatusSinceUtc`, `Attempts`, `SessionNumber` | body fields — age-in-column is already tracked, a Kanban wants it |
| stage `DependsOn` | task-list reference in the body ("blocked by #12") — informational only |

Identity: a stable marker in the issue body (`<!-- conductor:task <taskId> -->`) plus a local
mapping table in run.db (`github_map(task_id, issue_number, last_pushed_seq)`), so the sync is
idempotent when the cache is lost — search the marker, rebuild the map.

The semantics to copy are **`Planning/WorkGraphSync.cs`**, verbatim in spirit: upsert never
clobber, retire don't delete (an archived card gets a label and a closing comment, never
deletion), provenance tagged, and a `SyncResult` the log and the notify channel can report.
One warning from the current tree: `plans/karvan/CORE-TRACKER.md` carries seven stray rows from
another plan's board (`F0.*`, `R0.*`). The sync scopes to the current plan's stages or it
imports garbage.

Projects v2 is **GraphQL-only** (REST covers Issues; the board, its Status field, and item
placement are `gh api graphql` territory). That is the only genuinely new client surface; Issues
alone already deliver most of the value (labels + milestones render a usable board in GitHub's
own "Issues by milestone" view), so **Issues are the first checkpoint and Projects the second** —
if the token scope or the GraphQL surface turns hostile, an honest skip on the Projects half
still leaves a visible board.

### B. The history — one run, one issue, sessions as its timeline

The user-visible answer to "GitHub becomes the history":

- **A `run:<slug>` issue per run**, opened at `RunStarted`, closed at `RunFinished`, carrying the
  plan name, repo, branch, engine version (all already in the `runs` table, v11 provenance
  included).
- **One comment per session** at `SessionFinished`: number, stage, kind, outcome, commits,
  newly-done checkpoints, cost, tokens. Everything needed is already in the typed event
  (`SessionFinished { Number, StageId, Outcome, NewCommits, NewlyDone, CostUsd, Tokens* }`) and
  in `conductor history --json`'s `RunHistoryDetailJson` — the composition layer
  (`Messaging/NotifyTemplate.cs`, owner-editable `notify/<event>.md` templates) renders Telegram
  today and can render a GitHub comment body with zero new concepts.
- **Parks and attention requests** (`AttentionRequested`, `RunBlockedUntil`,
  `OwnerApprovalRequested`) as comments too — the run issue becomes the scrollable story of the
  run, which is exactly what the session INDEX.md is locally.
- **The final report as the closing comment** (and per L6.3, optionally as a PR comment when the
  run's branch has one — `RemoteLinks.LinkifyPullRequests` already acknowledges PRs only exist
  when an agent wrote one into a handoff).

Closed run issues + closed task issues *are* the history, browsable and searchable on GitHub
with no conductor installed. That answers "for the sake of the history" without inventing an
archive format.

### C. Where it plugs in — a reconciler on the event cursor, not a hot sink

Two candidate seams, both real:

1. `IEventSink` decorator (`Events/EventLog.cs:8`) — sees every event live.
2. A **periodic reconciler** over `IRunStore.ReadEventsAfter(runId, afterSeq)`
   (`Store/IRunStore.cs:141`) — a cursor, batched, resumable.

**Recommend the reconciler.** Reasons: a board wants *convergence*, not fire-and-forget (the
`WebhookNotifier` precedent is fine for strings; a mapped board that missed one event is wrong
until the next full sync); GitHub rate limits want batching (one session end = one issue comment
+ N card moves = one reconcile pass, not N+1 independent POSTs racing the loop); and a network
failure costs nothing — the cursor doesn't advance, the next pass converges. Run it at the
moments the engine already treats as boundaries (session end, run start/resume, park, run end —
the same call sites as `Notify`), plus a manual `conductor github sync` verb for catch-up, which
also gives the off-by-default feature a way to backfill an already-finished run.

Failure posture: same as webhooks — **a failed sync never blocks the loop** (log it, keep the
cursor, converge later). The run store stays the source of truth; GitHub is allowed to be
minutes stale, never allowed to be consulted.

### Mechanics

- **No Octokit.** `ReleaseClient.cs` is the house style: raw `HttpClient`, source-generated
  `System.Text.Json`, explicit User-Agent (GitHub 403s without one). REST v3 for issues,
  labels, milestones, comments; GraphQL for Projects v2.
- **Token: `SecretsStore` grows a second field** (`Integrations/SecretsStore.cs` is
  Telegram-only today and L6.3 names it as the home), with `CONDUCTOR_GITHUB_TOKEN` taking
  precedence per the store's existing rule. Needs `repo` scope; `project` scope only for part B
  of the board.
- **`owner/repo` derivation:** `Reporter.RemoteUrl` (`Reporter.cs:468`) already normalises
  `git@` and `https` origins to a browse URL; factor the owner/repo pair out of it rather than
  re-shelling git. A plan-level `github.repo` override for the multi-repo/lanes future.
- **Config:** a `github` block on the plan, off by default:

  ```jsonc
  "github": {
    "enabled": false,
    "repo": "",                  // "" = derive from origin
    "board": "issues",           // "issues" | "issues+project"
    "projectNumber": 0,          // required for issues+project
    "runHistoryIssue": true,
    "reportAsPrComment": false,
    "labelPrefix": "conductor"
  }
  ```

  One lesson from this month's own backlog applies with force: **`mutatingLanes[]` is plan
  config that nothing reads** (`NEXT-FEATURES.md:115`). Do not add the `github` block to
  `PlanConfig` until the code that reads it lands in the same checkpoint.

## What this is not

- **Not two-way.** No inbound webhook receiver, no polling GitHub for card moves, no reading
  issue state back. D-7, A16, ADR 0005 — three separate records all say the same thing.
- **Not a replacement for the tracker.** TRACKER.md remains the generated view of the
  contract; GitHub is a second generated view, remote and durable.
- **Not a notification channel.** Telegram already does urgency. GitHub does *state* and
  *history*. Severity stays out of it.

## Sequencing, honestly

L6.3 sits in stage L6 of a plan that has not launched, and D-7 gates it behind G4 (branch
hygiene, L1.1/L1.2 — still open, `requireCleanTree` exists nowhere in the tree). Three options:

1. **Run the lanes plan as authored** — GitHub sync arrives late but in order.
2. **Promote it**: split a `github-sync` stage into the front of the lanes plan (or a small
   standalone plan), since it touches no orchestration internals — it is a pure reader of the
   event log plus one secrets field. The G4 gate matters for *what the sync publishes* (a dirty
   tree makes lying commits), less for the sync machinery itself.
3. **Fold it into the next-era plan** (the truthful-read-side era) — it is philosophically at
   home there: fold is truth, side-tables are views, **GitHub is just the remotest view**.

Recommendation: **option 3**, with the checkpoint split as: (1) SecretsStore field + client +
`conductor github sync` backfill verb over a finished run — provable against this repo's own
history with no live run; (2) reconciler wired to the boundary call sites; (3) Projects v2
board; (4) run-issue timeline + report comment. Each lands with its own evidence and an honest
skip is acceptable from (3) on.

## What the owner sees when it ships

Open `github.com/shaahink/<repo>/issues`: one milestone per stage, cards moving columns as
sessions claim and the engine confirms, each card's timeline showing which session moved it,
what it cost, and the commit that proves it. One pinned run issue reading like the run's diary.
All of it still there in five years, on a machine the owner threw away.
