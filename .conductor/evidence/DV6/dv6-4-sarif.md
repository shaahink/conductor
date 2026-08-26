# DV6.4 — the bug ledger as GitHub code-scanning alerts

Session 19, 2026-08-26, branch `feat/divan`. Everything below was run through the FRESH build
(`dotnet run --no-build --project src/Conductor`), never the `conductor` on PATH.

## Acceptance, and what shows it

| # | Acceptance | Verdict | Where |
|---|---|---|---|
| 1 | citations lifted from real bug prose, refused when not resolvable | MET | live run over 32 open bugs: 6 located, 26 named no place — §2 |
| 2 | one valid SARIF 2.1.0 run, golden-pinned | MET | golden `testdata/dv6-4/bugs.sarif`; official schema, 0 errors — §3 |
| 3 | a real REST upload, wire-proven and driven by a fresh-build subverb | MET | 32 tests; live `github sarif` reached api.github.com — §4 |
| 4 | a LIVE call whose response is captured verbatim | MET | 403 from GitHub, quoted in full — §5 |
| 5 | docs state the public-free / private-needs-Advanced-Security split | MET | `docs/cli.md` §"Bugs as code-scanning alerts" |

## 1. What was built

| File | What it is |
|---|---|
| `src/Conductor.Core/Integrations/Github/SarifBugLocation.cs` | the citation parser and the tracked-file resolver |
| `src/Conductor.Core/Integrations/Github/SarifDocument.cs` | the SARIF 2.1.0 renderer, deterministic, clock-free |
| `src/Conductor.Core/Integrations/Github/GithubClient.Sarif.cs` | POST `/code-scanning/sarifs` (gzip+base64), GET the status, GET the repo |
| `src/Conductor.Core/Integrations/Github/GithubSarifSync.cs` | preflight, upload, settle; `GithubSarifPass` |
| `src/Conductor.Core/Integrations/Github/GithubSarifDtos.cs` | the four payloads, registered in `GithubJsonContext` |
| `src/Conductor/Commands/GithubCommand.cs` | the `sarif` subverb: `--out`, `--sha`, `--gitref` |
| `tests/Conductor.Tests/DV6_4SarifTests.cs` | 32 tests, incl. the golden |

## 2. The real corpus — 6 located out of 32 open

Run against a **`sqlite3 .backup` COPY** of the live store (trap 18: a fresh build whose migration
version may exceed the installed engine's never opens the live `run.db`):

```
$ dotnet run --no-build --project src/Conductor -- github sarif \
    --backfill C:/Users/shahi/AppData/Local/Temp/dv64/live.db \
    -p plans/divan/core.plan.json --repo shaahink/scratch --dry-run --out …/bugs.sarif
run aa916828  → shaahink/scratch  category conductor-bugs/  token from CONDUCTOR_GITHUB_TOKEN
commit 7a336e3d3b1fd934da808649c7ab67f68ed1cf17  ref refs/heads/feat/divan
dry run — nothing will be sent.
sarif: 6 located, 26 without a file and line
  bug #43 src/Conductor.Core/Evidence/EvidenceArtifact.cs:87
  bug #45 src/Conductor.Core/Store/MigrationRunner.cs:21-45
  bug #49 tests/Conductor.Tests/KS1_2StagesFromFoldTests.cs:174
  bug #61 src/Conductor.Core/Store/StateHome.cs:27-29
  bug #72 face-go/internal/tui/update.go:36
  bug #79 src/Conductor/Commands/GithubCommand.cs:186
0 requests
```

Six of thirty-two is the honest reach of the feature and it is **reported, not hidden**: the `bugs`
table has no file column (`Store/Migrations/v7_bugs.sql:4-17`, unaltered by v8–v15), so a citation
only exists when a session wrote one into the prose. Four of the six were written as a full
repo-relative path; two (`#72`, `#49`) were bare file names resolved against `git ls-files` because
exactly one tracked file bears each name. The other 26 open bugs remain issues — DV6.1 already gets
those out.

## 3. The document — pinned, and valid to the official schema

`.conductor/evidence/DV6/dv6-4-bugs-live.sarif` is the real document, 320 lines. It validated clean
against the official SARIF 2.1.0 schema:

```
$ curl -sSL -o sarif-schema.json https://json.schemastore.org/sarif-2.1.0.json
$ python -c "…jsonschema.Draft7Validator(schema).iter_errors(doc)…"
schema id: https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json
errors: 0
```

(The `$id` inside that schema is a **dead** URL — `master/Schemata/…` answers 404, measured. The
`$schema` conductor writes, `main/sarif-2.1/schema/…`, answers 200.)

Three properties are load-bearing and each is a test:

- **`automationDetails.id = "conductor-bugs/"`** — its own analysis category, so an upload cannot
  close alerts another tool raised.
- **`partialFingerprints.conductorBugId`** — the bug's row id, so a re-upload from a later commit
  updates the alert instead of raising a second one. This is bug #79's duplicate failure, designed
  out rather than discovered.
- **no clock anywhere** — two renders of an unchanged ledger are byte-identical, which is what makes
  `testdata/dv6-4/bugs.sarif` a bar rather than a diary.

Only OPEN bugs are rendered, and that IS the closing mechanism: code scanning resolves an alert whose
result stops appearing in a later analysis of the same category, so `conductor bug fix <id>` closes
the alert at the next upload with no second call
(`A_fixed_bug_leaves_the_document_so_its_alert_closes_itself`).

## 4. The gate run

```
$ dotnet build Conductor.slnx -clp:ErrorsOnly   → Build succeeded. 0 Warning(s) 0 Error(s)
$ dotnet test --filter FullyQualifiedName~DV6_4Sarif           → 32 passed, 0 failed
$ dotnet test --filter "…~Github|…~DV6_"                       → 98 passed, 0 failed
$ dotnet test --filter "…~Docs|…~Cli|…~Help"                   → 102 passed, 0 failed
```

The wire is asserted against the bytes the server received, not the intent of the code that sent
them: `The_upload_is_gzip_base64_and_the_server_reads_back_the_document` ungzips the request body and
compares it to the rendered document character for character, and checks `commit_sha`, `ref` and
`validate: true`. `A_document_github_rejects_after_the_202_is_a_failure` holds the rule that a 202 is
a receipt and not an ingestion.

## 5. The live call, and the caveat it MEASURED

Against the private scratch repo `shaahink/dv61-ledger-scratch` (trap 5), commit
`5ffab28ae82949021df0f4163cfd59eee09e7f02` seeded there for the alerts to anchor to. Verbatim:

```
run aa916828  → shaahink/dv61-ledger-scratch  category conductor-bugs/  token from CONDUCTOR_GITHUB_TOKEN
commit 5ffab28ae82949021df0f4163cfd59eee09e7f02  ref refs/heads/main
sarif: 6 located, 26 without a file and line, 1 error(s)
note shaahink/dv61-ledger-scratch is PRIVATE — code scanning is free on PUBLIC repositories; a
     PRIVATE repository needs GitHub Advanced Security (GitHub Code Security) and refuses the
     upload with 403 without it.
note GitHub documents 'security_events' for a private upload and this token (CONDUCTOR_GITHUB_TOKEN)
     carries [delete_repo, gist, read:org, repo, user, workflow]. attempting anyway — if the 403
     below is about the TOKEN, the owner grants it once: gh auth refresh -s security_events
403 Forbidden from https://api.github.com/repos/shaahink/dv61-ledger-scratch/code-scanning/sarifs
     [token scopes: delete_repo, gist, read:org, repo, user, workflow] —
     {"message":"Code scanning is not enabled for this repository. Please enable code scanning in
     the repository settings.","documentation_url":"https://docs.github.com/rest/code-scanning/…
     — code scanning is free on PUBLIC repositories; a PRIVATE repository needs GitHub Advanced
     Security (GitHub Code Security) and refuses the upload with 403 without it. if the repository
     IS public or does have it, the token may be missing 'security_events': gh auth refresh -s
     security_events
3 requests
                                                                              exit 1
```

**This measurement changed the design.** The first build carried a KS9.3-shaped gate: a private repo
without `security_events` was refused by name and nothing was sent. A direct `gh api --method POST`
of the *same payload* with the *same token* (scopes `repo,workflow,gist,read:org,user,delete_repo` —
no `security_events`) was answered:

```
403 {"message":"Code scanning is not enabled for this repository. Please enable code scanning in the
     repository settings."}
```

GitHub's wall on a private repository is the repository's **entitlement**, and its answer says
nothing about the token. The `security_events` requirement is therefore **unobserved on this path**,
and refusing on it would deny an organisation that HAS Advanced Security a call that may well
succeed. The gate was removed and replaced by a note. Recorded in the ledger the same day.

## 6. What is NOT proven here, precisely

GitHub never returned a **202** to conductor, because every repository this session was permitted to
touch is private (trap 5: DV6 proofs use a PRIVATE scratch repo) and this account has no Advanced
Security. The unproven leaf is GitHub's own ingestion of the document — and it is one leaf, not the
feature:

- the request **reached** api.github.com, authenticated, correctly routed and correctly encoded — a
  403 on the entitlement is a request GitHub parsed and refused, not one it failed to understand;
- the document is **valid to the official SARIF 2.1.0 schema**, zero errors, §3;
- the 202 → status → complete/failed path is wire-proven against a fake that replays GitHub's own
  shapes, including a rejected document and a pending poll.

The public leg belongs to DV7.3, which already runs `github sync --backfill` of this run against the
public `shaahink/conductor` as its closing act; `github sarif --backfill` alongside it costs nothing
there and is free on a public repository. It is in the runbook, not left to be rediscovered.
