# DV6.2 — the columns: the Projects v2 mutation path, landed and exercised

Session 17, 2026-08-26, stage DV6, branch `feat/divan`.

KS9.3 left the Projects v2 half **unbuilt** — honestly SKIPPED, because the machine's token could not
exercise a mutation even once and this project's contract makes half-done worse than skipped. DV6.2's
acceptance is that **the refusal moves either way**. It moved. What follows is what was measured.

---

## 1. The scope question, re-measured today — the answer is still no

The stage notes say to check the token before assuming. Two independent reads, 2026-08-26:

```
$ gh auth status
  Token scopes: 'delete_repo', 'gist', 'read:org', 'repo', 'user', 'workflow'

$ GET https://api.github.com/user   (Authorization: Bearer $CONDUCTOR_GITHUB_TOKEN)
  200
  X-OAuth-Scopes: delete_repo, gist, read:org, repo, user, workflow
  login=shaahink
```

No `project`. The engine's own token — the one `github sync` resolves — is the second read, and it is
the one that matters; `gh auth status` reports a different credential (`gho_…`, the keyring one).
So this checkpoint takes the branch the stage names: **implement, prove it stubbed, and file the
finding naming the owner's one-command unblock.** Filed as **bug #80**.

## 2. What was proven LIVE against api.github.com — the wire, read-only

A stub will happily answer a document the real API would reject. So every GraphQL document the engine
sends was validated against the real schema before it was written into the code, read-only, with the
scopeless token. GitHub validates a document **before** it authorises it, so `INSUFFICIENT_SCOPES`
means *the document is correct and the token is not*:

| Document | Sent to api.github.com | Answer |
|---|---|---|
| `ResolveQuery` — `repositoryOwner(login:) { … on ProjectV2Owner { projectV2(number:) { id title url field(name:"Status"){ … on ProjectV2SingleSelectField { id name options{id name} } } } } }` | yes | 200, `INSUFFICIENT_SCOPES` on `id`, `title`, `name`, `options.id`, `options.name` — every field resolved, then refused for scope |
| `ItemsQuery` — `node(id:) { … on ProjectV2 { items(first:100,after:) { pageInfo nodes{ id content{…on Issue{number}} fieldValueByName(name:"Status"){…on ProjectV2ItemFieldSingleSelectValue{optionId}} } } } }` | yes | 200, `INSUFFICIENT_SCOPES` on `id` and `optionId` — same reading |
| `AddItemMutation` input shape | **not sent** — introspected instead | `AddProjectV2ItemByIdInput`: `clientMutationId`, `projectId: ID!`, `contentId: ID!`; payload `AddProjectV2ItemByIdPayload { clientMutationId, item }` |
| `SetStatusMutation` input shape | **not sent** — introspected instead | `UpdateProjectV2ItemFieldValueInput`: `projectId: ID!`, `itemId: ID!`, `fieldId: ID!`, `value: ProjectV2FieldValue!`; `ProjectV2FieldValue` carries `singleSelectOptionId: String`; payload `UpdateProjectV2ItemFieldValuePayload { clientMutationId, projectV2Item }` |

The two mutations were **never sent**. Introspection needs no scope, so the write shapes were
confirmed without a single write — the same "zero mutations without the scope" bar KS9.3 set, kept.
`DV6_2ProjectColumnsTests.TheFourDocumentsCarryTheNamesTheLiveSchemaConfirmed` pins each of those
names, so a later edit that breaks the live wire fails a test rather than a run.

## 3. The refusal MOVED — measured through the fresh build against the real API

```
$ dotnet run --project src/Conductor -- github sync --project 7 \
      --repo shaahink/dv62-scratch-does-not-exist -p <scratch plan>

a Projects v2 board needs the 'project' scope and this token does not carry it. nothing was written.
  scopes observed: delete_repo, gist, read:org, repo, user, workflow
  scope required: project — Projects v2 is GraphQL-only, and the REST api cannot move a board item.
  token source: CONDUCTOR_GITHUB_TOKEN
  the owner grants it once, interactively: gh auth refresh -s project
  conductor will not run that: it is interactive and it rewrites this machine's stored credential.
  until then set github.board to 'issues' — the issue board mirrors in full without it.
exit=2
```

That is the *scope* refusal, and it is now the **only** one. KS9.3's second refusal — the one that
fired even with the scope granted, saying the mutation path did not exist — is gone from `src` and
from the tests, along with `GithubProjects.NotImplementedLine` and `UnimplementedRefusal`. Three
surfaces printed that sentence and all three moved with it:

| Surface | Was (KS9.3) | Is (DV6.2) |
|---|---|---|
| `GithubProjects.PreflightAsync` with the scope present | returned `UnimplementedRefusal` | returns **empty** — proceed |
| `GithubMirror.TryCreate` log line | `github project board off: … not implemented` | `github project board #7 on: the Projects v2 board is attempted at each boundary; it needs the 'project' scope …` |
| `ChannelHealth` | `Degraded` — "the project board is off" | `Ready` — "mirroring to … + project board #7 …", fix hint `gh auth refresh -s project` |

Pinned by `KS9_3ProjectsScopeRefusalTests.WithTheScopeGrantedTheGateFallsThroughAndTheBoardIsWritten`
(empty refusal, one GET, nothing else) and
`ARunWithACoherentProjectBoardIsToldWhichBoardAndWhatTheTokenNeeds`.

## 4. The board, written end to end by the real binary — `tools/dv6/dv6-2-live-proof.ps1`

The one thing neither a unit test nor the live probe can reach is the CLI's own wiring. So the rig
puts a loopback GitHub on `127.0.0.1:8791` whose `/user` answers with the scope **present** — that
header is the only fiction in it — copies the run store, and drives the freshly built engine three
times. Full transcript: **`.conductor/evidence/DV6/dv6-2-live-rig.log`**.

```
=== pass 1 ===  60 created · 17 updated · 4 unchanged · 0 errors
                project: 60 added · 60 moved · 0 in place · 0 unplaced · 0 errors
                after pass 1: 120 mutations, 60 board items

=== pass 2 ===  project: 0 added · 11 moved · 49 in place · 0 errors
                after pass 2: 131 mutations, 60 board items

=== pass 3 ===  project: 0 added ·  0 moved · 60 in place · 0 errors
                after pass 3: 131 mutations, 60 board items      <- zero new writes

=== the board the engine actually wrote ===
issues created : 60
board items    : 60
columns        : Done 35 · In Progress 1 · Todo 24
graphql documents on the wire:
  query($owner:String!,$number:Int!){ repositoryOwner(login:$o…
  query($project:ID!,$cursor:String){ node(id:$project){ ... o…
  mutation($project:ID!,$content:ID!){ addProjectV2ItemById(in…
  mutation($project:ID!,$item:ID!,$field:ID!,$option:String!){…
```

60 issues, 60 board items, 60 columns set, **131 mutations for three passes** — 120 to build the
board and 11 to settle it. Pass 3 adds none. The column split (35 Done / 24 Todo / 1 In Progress)
is the Karvan core run's real shape.

**Why pass 2 still moved 11 cards, and why that is not this checkpoint's defect.** Filed as
**bug #81**, measured here: `.conductor/followups.md` carries **91 rows for 55 distinct ids**.
`GithubLedgerPlan.Cards` builds one card per ROW, so the same issue is claimed twice, and rows for
one id disagree about OPEN vs CLOSED. On pass 1 the closed duplicate has no issue yet and is skipped;
on pass 2 its sibling's issue exists, so it places a *different* column and the card moves. DV6.2
guards its own half — `PlaceAsync` dedupes by issue number, first wins, and **says how many it
dropped** (`32 of 92 cards named an issue another card had already claimed`) — which is why pass 3
settles. The ISSUE half is untouched by this checkpoint and still reports `35 updated` on a settled
board; that is bug #81, for whoever takes it.

## 5. What the tests hold

`dotnet test --filter "FullyQualifiedName~DV6_2ProjectColumns"` → **21 passed**, and
`~KS9_3Projects` → **23 passed** (44 together, 0 failed).

| Claim | Test |
|---|---|
| a status decides a column, end to end through the reconciler | `EachCheckpointLandsInTheColumnItsStatusNames` |
| a second pass over an unchanged board issues **zero mutations** | `ASecondPassMovesNothingAndIssuesZeroMutations` |
| bug #79's lesson: a stale item listing costs redundant writes and **cannot mint a second item** | `AStaleBoardListingCostsRedundantWritesAndCannotMintASecondItem` |
| a card that changes status is carried across a column | `AStatusChangeBetweenPassesMovesTheCardAcrossColumns` |
| bugs and followups get columns too | `LedgerIssuesGetColumnsToo` |
| a board with no Blocked option places blocked cards in In Progress **and says so** | `ABlockedCardFallsBackToInProgressAndTheFallbackIsSaidOutLoud` |
| a status no column matches is UNPLACED, named, with the board's options listed | `AStatusWithNoColumnAtAllIsUnplacedAndNamedRatherThanGuessedAt` |
| a renamed Status field / a missing project are named, not guessed at | `ABoardWithNoStatusFieldIsNamedRatherThanSearchedForByShape`, `AProjectNumberThatDoesNotExistSaysSoAndSaysWhereTheNumberComesFrom` |
| GraphQL answers failure with **HTTP 200** — the scope error is surfaced verbatim anyway | `TheScopeErrorGitHubActuallySendsIsSurfacedVerbatimDespiteTheTwoHundred` |
| KS9.2's posture: a project half that fails completely leaves the issue board whole | `AProjectHalfThatFailsCompletelyLeavesTheIssueBoardWhole` |
| duplicate ledger rows place once, and the drop is said out loud | `TwoCardsNamingOneIssueArePlacedOnceAndTheDuplicateIsSaidOutLoud` |
| the four documents carry the names the live schema confirmed | `TheFourDocumentsCarryTheNamesTheLiveSchemaConfirmed` |
| the refusal moved | `WithTheScopeGrantedTheGateFallsThroughAndTheBoardIsWritten`, `TheStandingSentenceIsAboutTheScopeAndNoLongerAboutAnUnbuiltFeature` |

## 6. What changed

| File | What |
|---|---|
| `src/Conductor.Core/Integrations/Github/GithubProjectSync.cs` | **new** — the mutation path: resolve, list, add, set status; dedupe by issue; every refusal named |
| `src/Conductor.Core/Integrations/Github/GithubProjectColumns.cs` | **new** — status → column preference table, fallback and unplaced sentences |
| `src/Conductor.Core/Integrations/Github/GithubClient.GraphQl.cs` | **new** — the one GraphQL door; 200-with-`errors` lifted into the `(value, error)` pair; `MutationCount` |
| `GithubProjects.cs` | `NotImplementedLine` / `UnimplementedRefusal` **deleted**; preflight returns empty on success; `NeedsScopeLine` replaces the standing sentence |
| `GithubBoardSync.cs` / `.Ledger.cs` | placements collected per pass, project reconciled last so a failed board costs the issue mirror nothing |
| `GithubMirror.cs`, `ChannelHealth.cs`, `GithubCommand.cs` | the moved sentence, and the CLI builds the project sync after the gate returns empty |
| `GithubDtos.cs`, `GithubCard.cs`, `GithubSyncResult.cs` | `node_id` on an issue, the fold's status word on a card, `Project` on a result |
| `tests/…/FakeGithub.Projects.cs`, `DV6_2ProjectColumnsTests.cs` | the stateful Projects v2 fake and 21 tests |
| `tools/dv6/dv6-2-live-proof.ps1` | the loopback rig above |
| `docs/cli.md`, `docs/plan-config.md` | the refusal moved in the published surface too, with the column table |

## 7. What the owner has to do

One command, once, interactive, on this machine:

```
gh auth refresh -s project
```

Then `conductor github sync --backfill <run> --project <n> --repo shaahink/conductor` writes real
columns. Conductor will not run that command: it rewrites the machine's stored credential, which is
an owner's decision and not a session's. Bug #80 stays open until a real board has been written.
