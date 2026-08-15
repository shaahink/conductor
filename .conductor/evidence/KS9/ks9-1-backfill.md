# KS9.1 — the backfill, measured

Session #18, 2026-08-15. Everything below was run through the **fresh build**
(`dotnet run --project src/Conductor --no-build -- …`), never through the `conductor` on PATH — that
copy is the published engine driving this session and has no `github` verb at all.

Every live write went to **shaahink/conductor-sync-scratch**, a PRIVATE scratch repository created
for this proof. Nothing was created, edited or closed on `shaahink/conductor` or any real repository
(promptExtra trap 11). The first real backfill is KS10.3 and belongs to the owner.

---

## 1. The refusal costs zero requests

`CONDUCTOR_GITHUB_TOKEN` unset, no `githubToken` in the plan's secrets file, and
`CONDUCTOR_GITHUB_API` pointed at **port 9** — a dead port. If any code path dialled before deciding
it had no credential, the transcript would carry a connection error. It does not.

```
$env:CONDUCTOR_GITHUB_TOKEN=$null
$env:CONDUCTOR_GITHUB_API="http://127.0.0.1:9"
dotnet run --project src/Conductor --no-build -- github sync -p <rig>\scratch.plan.json \
    --backfill 2349f46b --repo shaahink/conductor-sync-scratch

no GitHub token. nothing was contacted.
  looked at $CONDUCTOR_GITHUB_TOKEN (unset) and
  C:/Users/shahi/AppData/Local/Temp/ks9-rig/repo\.conductor\secrets.local.json (no githubToken).
  a fine-grained or classic token with repo scope is enough for issues; project
  is only needed for a Projects v2 board.

EXIT=2
```

Both sources named, exit non-zero, no stack trace, no network. The refusal text is built in
`GithubIdentity.MissingTokenRefusal` (Core) rather than in three console calls, so
`KS9_1GithubTokenTests.NeitherSourceIsNullAndTheRefusalNamesBoth` asserts the sentence itself.

The rig is a throwaway plan under `%TEMP%\ks9-rig` with its own repo directory, `-p` passed
explicitly and `CONDUCTOR_PLAN` cleared in the shell (traps 0 and 4). Nothing here touched
`C:/code/conductor`'s run state: the verb opens the run through `ArchiveView` over `Mode=ReadOnly`.

## 2. Dry run — reconcile and report, write nothing

```
7 created · 0 updated · 0 unchanged · 0 retired · 10 comments · 0 errors
2 requests
```

Two GETs. Nothing else went out.

## 3. Pass 1 — the board goes up

Run `2349f46bc4cd481e8b179028ba46d79a` (plan `w5-rehearsal`, 10 sessions, 6 checkpoints).

```
run 2349f46b  plan w5-rehearsal  → shaahink/conductor-sync-scratch  token from CONDUCTOR_GITHUB_TOKEN
7 created · 0 updated · 0 unchanged · 0 retired · 10 comments · 0 errors
  T1.1 https://github.com/shaahink/conductor-sync-scratch/issues/1
  T1.2 https://github.com/shaahink/conductor-sync-scratch/issues/2
  T2.1 https://github.com/shaahink/conductor-sync-scratch/issues/3
  T2.2 https://github.com/shaahink/conductor-sync-scratch/issues/4
  T3.1 https://github.com/shaahink/conductor-sync-scratch/issues/5
  … 2 more
29 requests
EXIT=0
```

## 4. Pass 2 — identical command, ZERO minted

```
0 created · 0 updated · 7 unchanged · 0 retired · 0 comments · 0 errors
3 requests
EXIT=0
```

**29 requests down to 3.** Nothing created, nothing commented, nothing patched. This is the
checkpoint's central bar and it holds against the real API, with the map table absent entirely —
identity comes from the marker in the issue body, matched against a full `state=all` issue list.

Not the search API, deliberately: GitHub's search index is eventually consistent, so a backfill run
twice in quick succession would duplicate whatever the index had not caught up with. Idempotence
decided by timing is not idempotence.

## 5. What the real board says

`gh api repos/shaahink/conductor-sync-scratch/issues?state=all` (raw JSON then `ConvertFrom-Json` —
trap 13):

```
#1 [closed] T1.1 — a greeting module exists          | conductor:status:done,conductor:source:tracker,conductor:confirmed | milestone T1
#2 [closed] T1.2 — the greeting is covered by a test | conductor:status:done,conductor:source:tracker,conductor:confirmed | milestone T1
#3 [closed] T2.1 — the entry point calls the greeting| conductor:status:done,conductor:source:tracker,conductor:confirmed | milestone T2
#4 [closed] T2.2 — the newly realised requirement    | conductor:status:done,conductor:confirmed,conductor:source:human   | milestone T2
#5 [closed] T3.1 — the readme documents the greeting | conductor:status:done,conductor:source:tracker,conductor:confirmed | milestone T3
#6 [closed] T3.2 — the changelog names the release   | conductor:status:done,conductor:source:tracker,conductor:confirmed | milestone T3
#7 [closed] run: w5-rehearsal — 2349f46b             | conductor:run                                        | 10 comments
```

`#4` carries `conductor:source:human` where the others carry `conductor:source:tracker` — provenance
survived the mirror rather than being flattened to one value. Every card wears `conductor:confirmed`
because the engine confirmed all six; an unconfirmed claim would not, which is the W1.1 distinction
the mirror must not lose (`KS9_1GithubBackfillTests.OneIssuePerCheckpointCarriesTitleLabelsMilestone
AndMarker` asserts the negative case).

Issue #1's body, verbatim from GitHub:

```
<!-- conductor:task T1.1 -->

**Stage** T1  **Status** done ✓ confirmed
**Source** tracker  **Commit** d5ec347  **Evidence** delivered by the W5.1 rehearsal agent

<sub>Mirrored by conductor. This board is a VIEW: the tracker and the run's event log are the
contract, and nothing here is ever read back into the run.</sub>
```

The diary issue #7, and the first and last of its ten comments:

```
<!-- conductor:run 2349f46bc4cd481e8b179028ba46d79a -->

**Plan** w5-rehearsal
**Repo** C:/Users/shahi/AppData/Local/Temp/conductor-w5-bf4c85d1
**Branch** main
**Engine** 0.3.1-alpha.0.107+f764904bd973
**Run** 2349f46bc4cd481e8b179028ba46d79a
```

```
<!-- conductor:session 2349f46bc4cd481e8b179028ba46d79a#1 -->

**session 1** · stage T1 · Advanced
newly done: T1.1
commits: d5ec347
cost: $0.00
tokens: 360
```

```
<!-- conductor:session 2349f46bc4cd481e8b179028ba46d79a#10 -->

**session 10** · stage T3 · Progress
cost: $0.00
tokens: 180
```

Ten `SessionFinished` events, ten comments, one per session, each carrying its own marker.

---

## Two defects the live run found that the suite did not

### A CLI option template took down EVERY verb

`[CommandOption("--repo <OWNER/NAME>")]` builds clean and passes the whole suite. Running the binary:

```
error: Spectre.Console.Cli.CommandTemplateException: Encountered invalid character '/' in value name.
   at Spectre.Console.Cli.TemplateParser.ParseOptionTemplate(...)
   at Spectre.Console.Cli.CommandModelBuilder.Build(IConfiguration configuration)
   at Spectre.Console.Cli.CommandExecutor.Execute(...)
```

Spectre builds the model for the **whole application** at configure time, so one malformed template
on one new verb is not a broken verb — `status`, `task` and `run` all die with it, before any argv is
read. Value names must be bare words; it is now `--repo <REPO>`.

Pinned by `B11_2DoctorAndCompletionTests.EveryCommandOptionTemplateParses`, which reflects over every
`CommandSettings` type in the shell assembly and reads its option and argument attributes back. The
template is parsed by the ATTRIBUTE CONSTRUCTOR, so reading the attributes reproduces the startup
failure exactly — the test goes red the way the binary did.

### The diary issue stayed open on a finished run

Pass 1 and 2 left issue #7 `open` for a run whose status is `Completed`. The archive spells run
status with the `RunStatus` enum's casing (`Completed`); the task graph spells its statuses
lower-case (`done`, `todo`). The terminal-status check was ordinal, so it matched neither — and the
unit fixture did not catch it because the fixture was written in the other half's vocabulary.

Fixed to a case-insensitive match, and the fixture's default is now spelled the way the archive
spells it, with `KS9_1GithubBackfillTests.TheDiaryIssueClosesWithTheRun` pinning both spellings.

## 6. Pass 3 and 4 — convergence, then quiet again

```
=== PASS 3 (after the casing fix) ===
0 created · 1 updated · 6 unchanged · 0 retired · 0 comments · 0 errors
4 requests
EXIT=0

=== PASS 4 (identical to 3) ===
0 created · 0 updated · 7 unchanged · 0 retired · 0 comments · 0 errors
3 requests
EXIT=0
```

Pass 3 pushed **exactly the one delta** the fix introduced — the diary issue closing — and nothing
else. Pass 4 went back to silent. That is a reconciler converging, not a mirror re-posting.

---

## What is asserted here, and what is asserted in the suite

Live above: the refusal with no network, board mapping, provenance and confirmation labels,
milestones, the diary and its per-session comments, second-pass idempotence against the real API, and
convergence after a change.

In `KS9_1GithubBackfillTests` (a stateful recording fake that serves back the issues it was asked to
create, returns bodies with CRLF and ignores `state` on POST — all three exactly as GitHub does):
retire-don't-delete, foreign-label preservation, dry-run writes nothing, a transport failure is
returned rather than thrown, HTTP errors carry status + URL + token scopes, and every request carries
the User-Agent GitHub answers 403 without.

Retire is not exercised live because an archived run's fold cannot lose a checkpoint — the
declaration is immutable history. It is exercised in the suite by folding a log with the `TaskAdded`
removed, and asserted as: a closing comment, a `conductor:retired` label, `state: closed`, and **no
DELETE anywhere**.

## Gate battery

`dotnet build Conductor.slnx -clp:ErrorsOnly` — 0 warnings, 0 errors.
Full-suite and ratchet numbers are in the session's commit and the handoff.

## Not touched

No migration was added (the `github_map` table moves to KS9.2 — see the card amendment: clause 3's
read-only store and clause 6's local map cannot both hold in this checkpoint, and the marker path is
the stronger of the two). `architecture-baseline.json` is still `{}`. No `PackageReference` was added
to `Conductor.Core.csproj` — the client is raw `HttpClient`. Nothing inbound exists:
`ArchitectureBoundaryTests.TheGithubMirrorNeverWritesRunState` fails the build if anything under
`Integrations/Github` names `TaskWrites`, `EventLog`, `SqliteRunStore`, or implements `IEventSink`.
