# ADR 0005 — Remote observability is push-only

- **Status:** Accepted
- **Date:** 2026-08-05
- **Decided in:** Karvan core, K5.4
- **Supersedes the open question:** "an authenticated tunnel to the control plane (E3)"

## Context

The recurring ask is "can I see the run from my phone". The obvious answer is to reach the control
plane remotely: it already serves the whole run — state, tracker, gates, evidence, owner queue — over
HTTP, and the Face is nothing but a client of it.

That control plane is a **loopback HTTP server with no authentication of any kind**. It binds
127.0.0.1, it has no tokens, no sessions and no TLS, and its verbs are not read-only: the same server
that answers `GET /state` accepts control actions that pause, resume, abort and approve a run. Its
security model is entirely "nothing outside this machine can reach it".

Making it reachable from a phone means either exposing that port or tunnelling to it. Both replace
the one property the design leans on with an authentication story this project does not have and
would have to get right — on a server whose verbs can stop a run, on a machine that is usually
someone's development box, for a benefit that is fundamentally *reading*.

## Decision

**Remote observability is push-only. The control plane is not exposed beyond loopback, and no inbound
port, tunnel or reverse proxy is part of the supported design.**

What the owner gets instead:

- A **richer push** (K5.2, K5.3, K5.4): every message names the plan, the session, the repo, the
  branch, the stage and the checkpoint; progress on every push; money with headroom against the cap;
  commits and pull requests as links; evidence artifacts sent as photos and documents rather than as
  paths; severity mapped to notify-versus-silent so only a parked or finished run buzzes.
- A **shareable report** — `.conductor/REPORT.md` is committed to the run's own branch, so every push
  can carry a link to it, and the link works from anywhere the repo does.
- **Two-way control where it already exists**, over Telegram's own authenticated channel: an allowed
  chat id and inline keyboards writing `control.json`. The authentication is Telegram's, not ours,
  and the surface is a fixed set of actions rather than an HTTP server.

## Consequences

- "See the run from my phone" is answered by what arrives, not by what can be fetched. Anything the
  owner should be able to see remotely has to be *pushed*, which is a real constraint on future work:
  a new surface is not remotely visible until something sends it.
- The control plane stays a loopback service with no auth story to maintain, and no new inbound
  attack surface is introduced on the machine running the agent.
- There is no live query from the phone. An owner who wants a fact nobody pushed reaches the machine,
  or reads the linked report.
- If this is ever revisited, the work is an authentication story first and a transport second —
  exposing the current server as it stands is not a smaller version of that work.

## Addendum — the GitHub mirror is the same decision (KS9.1, 2026-08-15)

`conductor github sync` puts a run's board on GitHub issues. It is worth naming here because it looks
like an exception and is not one.

The mirror is **push-only in exactly this ADR's sense**: conductor writes issues, comments, labels and
milestones, and no code path reads GitHub state back into run state, the tracker, or the task graph.
`Events/TaskWrites.cs` remains the only writer of task state; `ArchitectureBoundaryTests.TheGithubMirror
NeverWritesRunState` fails the build if anything under `Integrations/Github` names it, implements
`IEventSink`, or touches the store.

GitHub *is* read — a full issue list per pass — but only to answer one question: **which issue is
already ours**. That is identity resolution against a marker in the issue body, not ingress. Nothing
observed there can change what the mirror decides to push, which is a pure function of the fold.

The consequence is the one this ADR already accepts: dragging a card on GitHub changes nothing in the
run. That is correct behaviour, not a gap. Two-way sync was considered and rejected at L6.3 (D-7); a
board that could write back would be a second contract competing with the tracker.

## Addendum — three more GitHub surfaces, same decision (DV6, 2026-08-26)

Divan pushed the mirror onto three surfaces beyond issues, and none of them changes the shape above.

- **Ledger issues** (DV6.1, `GithubBoardSync.Ledger.cs`) — bugs and followups become durable issues
  with their own labels and markers, closed by the ledger with a comment rather than by the run
  ending.
- **Projects v2 columns** (DV6.2, `GithubProjectColumns.cs`, `GithubProjectSync.cs`, over GraphQL) —
  a card's column is *set* from the fold. The board's field and option ids are read first, which is
  the same identity resolution the addendum above already licenses, not ingress.
- **Code-scanning alerts** (DV6.4, `GithubSarifSync.cs`) — open bugs that name a file and a line
  become one SARIF run, uploaded to `/code-scanning/sarifs`.

Two things are read back, and both answer "what would this write do", never "what should the run do":
issue identity (above) and `GithubRepoInfo` — the repository read for one fact, private or not,
consulted so a refused SARIF upload can say *why* rather than fail blind. `ArchitectureBoundary
Tests.TheGithubMirrorNeverWritesRunState` still holds the line by file name for every one of them.

The board snapshot (DV6.3) is not a GitHub surface at all: `board.html` is rendered locally and
**pushed** as a Telegram document, which is this ADR's original prescription — a richer push — rather
than an exception to it.
