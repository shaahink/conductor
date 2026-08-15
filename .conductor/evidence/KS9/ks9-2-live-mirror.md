# KS9.2 — the live mirror: a reconciler over `ReadEventsAfter`

Session #19, 2026-08-15. Contract: `plans/karvansara/contracts/KS9-10.json` → KS9.2.
Commits `70ae34a` (core), `25e3f1f` (tests), `67d2a08` (the two defects the rig found).

## What landed

`GithubMirror` (`src/Conductor.Core/Integrations/Github/GithubMirror.cs`) — a reconciler on the
`WatchLoop` cursor idiom. It is **not** an `IEventSink` and is not registered as one; the delta from
`IRunStore.ReadEventsAfter` decides *whether* to push, the full fold decides *what*, and the cursor
(`github_cursor`, migration **v14** — v13 was taken by `run_limits_provenance`) advances only after a
clean push. Boundaries: run start, session end, blocked-until, owner-gate, needs-human — all
fire-and-forget through `RunContext.MirrorBoard` — plus run complete, which is *waited* for under a
90s budget after `RecordRunEnd` with the terminal status passed explicitly.

Off by default: `github.enabled` **and** `github.liveMirror`, plus a token and a store. Absent any of
them there is no mirror object, so the boundaries call nothing.

## The live rig — real GitHub, real outage

`plans/karvansara/contracts/ks9-2-rig/run-mirror.ps1`, driving the **fresh build**
(`src/Conductor/bin/Debug/net10.0/conductor.exe`) against a temp rig with its own
`CONDUCTOR_STATE_HOME`, its own scratch git repo, `-p` passed explicitly and `CONDUCTOR_PLAN` cleared.
Destination: **private** `shaahink/conductor-sync-scratch-ks92`. The outage is a process-boundary one:
`CONDUCTOR_GITHUB_API` is read per request but cannot be changed inside a running engine, so pass 1
runs entirely against a dead port (`127.0.0.1:9`) and pass 2 entirely against `api.github.com` — which
also proves the cursor survived a process death rather than a retry inside one.

Final run `b9e96dfc`, 2026-08-15 03:16–03:17 (log: `.conductor/bg-logs/ks92final-*.log`):

| pass | API | what the engine logged | board |
|---|---|---|---|
| 1 | dead port | `github mirror behind (run start): HttpRequestException: … actively refused it … — cursor held at 0, 1 requests`, and the same for `run start +coalesced`. **run exit=0** | 0 issues |
| 2 | api.github.com | `run start: 4 created · 0 updated · 0 unchanged · 0 retired · 1 comments · 0 errors — cursor 0→9, 10 requests` then `run start +coalesced: 0 created · 0 updated · 4 unchanged · 0 retired · 1 comments · 0 errors — cursor 9→12, 8 requests` | 4 issues: 3 cards + `run: ks92-mirror — b9e96dfc` with 2 comments |
| 3 | api.github.com | `run start: 0 created · 4 unchanged · 0 comments — cursor 12→13, 3 requests`; `needs-human: … cursor 13→15, 3 requests`; `needs-human +coalesced: … 1 comments — cursor 15→16, 4 requests` | still 4 issues |

Every acceptance clause, against those lines:

- **failure never blocks the loop** — pass 1 exit 0, session and verdict path identical to sync-off,
  one line logged per failed pass, cursor held at 0, board empty.
- **convergence on reconnect** — pass 2 pushed exactly the missed deltas, no manual repair.
- **batching** — a steady boundary costs 3–4 requests, not one per event; 16 events total.
- **zero duplicates** — three passes, 4 issues, ever.
- **nothing inbound** — no GitHub read reaches run state; a human closing a card and adding
  `needs-discussion` changes nothing in the fold and keeps their label (unit test).

## Two defects the rig found that reading could not

**1. A once-mode run truncated its own pass.** `run --once` returns from the loop the instant the
session ends; the teardown disposed the mirror while a fired pass was mid-board — *one* issue of three
on GitHub, no diary, and a `TaskCanceledException` where a real error belonged. Fixed: passes are
tracked and drained at shutdown. Tracking only the *last* fire was the second version of the same bug
(the last fire is usually the boundary that arrived during a slow pass and returned instantly) — which
is also why such a boundary is no longer dropped but **coalesced** into exactly one follow-up.

**2. GitHub's REST issues LIST is eventually consistent.** A pass created four issues; the pass two
seconds behind it listed the repository, saw none of them, and created four more — eight issues, two
complete copies of one board, with correct code on both ends. The rig's own `gh api` call agreed with
the stale view, so this is replica lag, not a cache in the engine. KS9.1's two live passes only looked
idempotent because they were minutes apart. Fixed by the contract's own `github_map`: identity is
still the marker a human reads, but the *authority* on "have I already made this" is a local row
written the moment a create is answered and reloaded by the next process. A mapped issue the listing
does not show is fetched **by number** (which reads through) so never-clobber still holds.

## Tests

`dotnet test --filter "FullyQualifiedName~KS9_1|FullyQualifiedName~KS9_2"` → **35 passed, 0 failed**.

- `KS9_2ReconcilerTests` (against a real `SqliteRunStore`, not a stub): zero requests when nothing is
  new; cursor advances to the batch head only after a clean push; one boundary is one batched pass;
  a failed pass holds the cursor and says so; convergence after outage without a full re-post; a new
  process resumes from the persisted cursor, not from zero and not from now; replay from cursor zero
  mints nothing; a drain lets the fired pass finish; a boundary during a pass is coalesced, not
  dropped; **a completely stale listing cannot produce a second copy of the board**, in one process
  and across processes; a human editing the board on GitHub changes nothing in the run.
- `KS9_2NetworkFailureDoesNotBlockTheLoopTests`: the same three-session sequence driven twice — no
  mirror vs a mirror whose every request fails — with the two event logs compared event for event;
  a hung endpoint returns the boundary in <1s; no mirror exists unless the plan asks for one.
- `ArchitectureBoundaryTests.TheGithubMirrorIsNeverRegisteredOnTheEventPath`: the shell may not name
  `GithubMirror`, and inside orchestration only `RunContext` and `RunLoop.Plumbing` may.

`dotnet build Conductor.slnx -clp:ErrorsOnly` → 0 errors, 0 warnings.
Suppression ratchet: **43 pragmas in `src/`, unchanged** — this checkpoint adds zero. (The ceiling is
38 and was already red before this session; bug #44.)
