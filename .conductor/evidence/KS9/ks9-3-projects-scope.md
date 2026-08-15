# KS9.3 — the Projects v2 board: the precise refusal, and why SKIPPED is the delivery

**Session #20, 2026-08-15.** Contract: `plans/karvansara/contracts/KS9-10.json`, checkpoint KS9.3.
Outcome: **SKIPPED**, by the contract's own refusal branch. Engine under test:
`0.4.1-alpha.0.88+8b70adf8cab6` — the FRESH BUILD (`dotnet run --project src/Conductor`), never the
`conductor` on PATH.

The contract splits this checkpoint in two and says which half is live:

> REFUSAL BRANCH (the live case today …): the checkpoint emits a PRECISE refusal naming (a) the
> scopes observed, (b) the scope required, (c) where the token came from, and (d) the exact one-time
> command the OWNER must run — `gh auth refresh -s project` — and the tracker row is marked SKIPPED
> with that refusal as its evidence. **No half-built board code is left that a later reader could
> mistake for working.**

---

## 1. `gh auth status`, verbatim — the scope is absent

```
github.com
  ✓ Logged in to github.com account shaahink (keyring)
  - Active account: true
  - Git operations protocol: https
  - Token: gho_************************************
  - Token scopes: 'delete_repo', 'gist', 'read:org', 'repo', 'user', 'workflow'
```

No `project`. `repo` does not imply it, and Projects v2 is GraphQL-only — the REST API cannot move a
board item, so there is no way to reach a board with what this machine holds.

**`gh auth refresh -s project` was NOT run.** It is interactive and it rewrites the machine's stored
credential; the contract makes granting the scope an owner act, and this session's only job was to
say so precisely.

## 2. What was measured live — five cases, real `api.github.com`, real token

Fresh build, scratch plan under `%TEMP%\ks9-3-rig` with `-p` passed explicitly and `CONDUCTOR_PLAN`
cleared (trap 4). `CONDUCTOR_GITHUB_TOKEN` set from `gh auth token` — the real, scope-less token.
Every case exits **2**.

### (A) `github sync --repo shaahink/conductor-sync-scratch-ks92 --project 7`

```
a Projects v2 board needs the 'project' scope and this token does not carry it. nothing was written.
  scopes observed: delete_repo, gist, read:org, repo, user, workflow
  scope required: project — Projects v2 is GraphQL-only, and the REST api cannot move a board item.
  token source: CONDUCTOR_GITHUB_TOKEN
  the owner grants it once, interactively: gh auth refresh -s project
  conductor will not run that: it is interactive and it rewrites this machine's stored credential.
  until then set github.board to 'issues' — the issue board mirrors in full without it.
```

All four obligations are in it: **(a)** the scopes observed, **(b)** the scope required with the
reason REST cannot substitute, **(c)** the token's source, **(d)** the one-time owner command.

**The cross-check that makes this a measurement and not a transcription:** those six scopes were read
from the LIVE `X-OAuth-Scopes` response header on `GET /user`, by
`GithubClient.ProbeScopesAsync` (`src/Conductor.Core/Integrations/Github/GithubClient.cs:149`) — and
they agree with §1's `gh auth status` verbatim, name for name. The refusal did not copy a doc.

### (B) `--project 0` — the config gate, free and first

```
github.board is 'issues+project' but github.projectNumber is 0. set it to the Projects v2 board
number from the project url (github.com/users/<owner>/projects/<number>).
  nothing was contacted and nothing was written.
```

### (C) plan `board: "issues+projekt"`, no flag — a typo is refused, not downgraded

```
github.board 'issues+projekt' is not a board. it is 'issues' or 'issues+project'.
  nothing was contacted and nothing was written.
```

Fires **before the destination is resolved** and before the network. Silently reading a misspelt
board as the default is indistinguishable, from the outside, from a project mirror that ran and did
nothing.

### (D) plan `board: "issues+project"`, `projectNumber: 3`, no flag

Identical scope refusal to (A). The gate is a property of the configuration, not of the CLI flag.

### (E) the regression that matters more than the feature — `board: "issues"`

```
nothing to sync. pass --backfill <run> — a run id, a prefix, a catalogue slug, a repo name, or a
path to a run.db.
```

The gate does not fire. A plan that never asked for a project board behaves exactly as it did before
`board` had a reader.

### (F) zero mutations, proved against real GitHub

`shaahink/conductor-sync-scratch-ks92` (the KS9.2 scratch repo, trap 11 — no real repository was
touched at any point):

```
issues before the four gate runs: 4
issues after:                     4
```

The structural reason, not a matter of ordering: **the scope check is a `GET`.** A check that learned
its answer by attempting a mutation and reading the failure would already have written on the tokens
that DO carry the scope.

## 3. What was deliberately NOT built — this is the cut line

**No GraphQL mutation path is merged.** Not a stub, not a disabled branch, not a REST approximation.
The contract names the trap: *"Projects v2 is GraphQL-only — reaching for REST here produces a
plausible-looking no-op that passes a naive test,"* and *"half-done is explicitly worse than
SKIPPED."* Code that could not have been exercised even once against a real board is not a feature,
it is a claim.

So the third branch is also a refusal: **with the `project` scope granted, the gate STILL refuses**
and says the board is not implemented
(`GithubProjects.UnimplementedRefusal`, `src/Conductor.Core/Integrations/Github/GithubProjects.cs:126`).
A gate that fell through to silence once the scope arrived would read, from the outside, exactly like
a board being mirrored. Pinned by
`WithTheScopeGrantedItStillRefusesAndSaysTheBoardIsNotImplemented`.

## 4. Where the gate lives

| File | What it decides |
|---|---|
| `src/Conductor.Core/Models/GithubConfig.cs:82` | `BoardRefusal()` — a misspelt board and a zero `projectNumber`, as a sentence, so "refuses BY NAME" is a test and not a reading. |
| `src/Conductor.Core/Integrations/Github/GithubClient.cs:149` | `ProbeScopesAsync` — one `GET /user`, reading `X-OAuth-Scopes`. Three answers: could-not-ask, no-header (a fine-grained PAT), scopes. |
| `src/Conductor.Core/Integrations/Github/GithubProjects.cs` | The whole project half: `RequiredScope`, `GrantCommand`, the refusals as data, `PreflightAsync` in cost order — config (free), scopes (one GET), unbuilt. |
| `src/Conductor/Commands/GithubCommand.cs:81` | The CLI boundary: the board's coherence before a destination, the scope before the first write. A caller who asked for a project board and cannot have one is refused **whole** — pushing the issue half while quietly dropping the half that was asked for is the silent no-op this gate exists to prevent. |
| `src/Conductor.Core/Integrations/Github/GithubMirror.cs:110` | The RUN's boundary, and the deliberate asymmetry: one log line, then the issue mirror **carries on**. KS9.2's posture is that a run is never harmed by this integration, so a run must not lose a working issue board over an extra it cannot have. What it must not get is silence. |

`board` and `projectNumber` had **zero readers anywhere in `src/`** before this checkpoint, while
`docs/plan-config.md:412` already promised the `projectNumber` refusal existed — the
config-nothing-consumes anti-pattern `NEXT-FEATURES.md:115` names by name. The promise is now true.

## 5. Gates

| Gate | Result |
|---|---|
| `dotnet build Conductor.slnx -clp:ErrorsOnly` | **Build succeeded. 0 Warning(s), 0 Error(s)** |
| `dotnet test --filter FullyQualifiedName~KS9_` | **Passed! Failed: 0, Passed: 59** (KS9.1 + KS9.2 + KS9.3) |
| `tools/gates/ratchet.ps1` | tests floor 1932 → **2311**; archdebt 0 → **0**; pragmas **43 vs ceiling 38 — RED, and pre-existing (bug #44)**. Measured, not assumed: `grep -rc "pragma warning disable" src tests` at `5ff45e3` (the pre-KS9.3 commit) is **43**, and at HEAD is **43**. KS9.3 adds **zero**. The ceiling was not raised. |

## 6. A defect this found in the test suite, not in the product

The first run of the mirror-boundary theory failed with `Assert.NotNull(mirror)` on one case out of
three — the same code, passing for two inputs and failing for a third. Not flakiness in the product:
`$CONDUCTOR_GITHUB_TOKEN` is **process-global**, `KS9_1GithubTokenTests` clears it while asserting
the no-token refusal, and xUnit runs the two classes **in parallel**, so the token vanished
mid-`TryCreate`. Fixed by taking the global out of the test: KS9.3's mirror tests write the token to
the plan's own per-temp-dir `secrets.local.json`, which cannot race. Any future test that reaches for
that environment variable inherits the same hazard.

## 6b. And a second red this found, also pre-existing

`SF7_1DocsMatchRealityTests.PlanConfigDocDocumentsEveryKeyThePlanSchemaDeclares` was **failing before
this session touched anything**: KS9.2 added `github.liveMirror` (commit `70ae34a`) and never gave it
a row in `docs/plan-config.md`, so the derivation named it —

```
docs/plan-config.md documents no `key` row or section for 1 settable path(s): github.liveMirror.
```

The gate was right and the doc was wrong, so the doc got the row it was missing. That suite is now
**22/22**. This is a fix to the same `github` block KS9.3 documents, not a widening of scope.

## 7. The claim

```
conductor task --skipped KS9.3 --evidence .conductor/evidence/KS9/ks9-3-projects-scope.md
```

SKIPPED, not BLOCKED: the contract requires that *"the run does not park on it — a cut-line
checkpoint that cannot proceed is SKIPPED, not BLOCKED-forever."* The one open item for the owner is
recorded for KS10.1's closure ledger:

> **KS9.3 — Projects v2 board. SKIPPED.** The gate ships and refuses precisely; the GraphQL mutation
> path does not exist. To revive it an owner runs `gh auth refresh -s project` once, and a later
> stage writes the mutation against a scratch project — with the scope present, `github sync
> --project <n>` today answers with `the Projects v2 board is not implemented`, which is where that
> work starts.
