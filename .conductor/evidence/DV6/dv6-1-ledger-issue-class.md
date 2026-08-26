# DV6.1 — bugs and followups as an issue class that outlives the run

Session 16, 2026-08-26. Commit `8d14fe5` (code + tests + golden), plus this artifact and the rig.

## Acceptance, declared before editing (ledger entries, session 16)

1. a bug and a followup become issues in their own class — `conductor:bug` / `conductor:followup`,
   marker identity, **created only while open**
2. the ledger issue **outlives its run** — a terminal run closes cards and the diary and leaves every
   open ledger issue open; the retire sweep cannot reach one
3. **closed by the ledger, not by the run** — `conductor bug fix` and a followups row leaving OPEN
   close their issues on the next pass, with a comment saying which side closed them
4. the daily digest gains **one** ledger line, golden-pinned, absent when the ledger is empty

All four delivered.

## What was built (file:line)

| | |
|---|---|
| `src/Conductor.Core/Integrations/Github/GithubLedgerPlan.cs` | the decision, from the LOCAL ledger only. `CardFor(BugRow…)` / `CardFor(FollowupEntry…)`; `CreateIfMissing` is false for a closed entry |
| `src/Conductor.Core/Integrations/Github/GithubBoardSync.Ledger.cs` | the reconciler. Separate sweep, indexed on the bug/followup marker — `RetireAsync` indexes the TASK marker, so the two sets cannot intersect |
| `GithubIdentity.cs:41-58` | `BugMarker` / `FollowupMarker` / `BugIdIn` / `FollowupIdIn` |
| `GithubMirror.cs:165-176, 289-303` | `ReadLedger()` and the second "is there news" answer — the event cursor is blind to the bugs table |
| `MessageComposer.Views.cs:175-235` | `LedgerLine()` and its place in the digest |
| `FollowupParser.cs:81-101` | `IsOpen` — looser than `ReadOpenForStage` on purpose, and why |
| `SqliteRunStore.Bugs.cs:108-123`, `RunArchive.Bugs.cs`, `ArchiveView.cs:196-205` | the whole ledger, live and read-only |
| `GithubCommand.cs:203-212` | `github sync --backfill` pushes the ledger too |

## The unit/harness evidence — 8 tests, all green

`tests/Conductor.Tests/DV6_1LedgerIssueClassTests.cs`, driven through the REAL `GithubMirror` over a
real `SqliteRunStore` and `FakeGithub` (the stateful fake that serves back what it was asked to
create, and reproduces GitHub's CRLF bodies and its create-always-open rule):

```
dotnet test --filter FullyQualifiedName~DV6_1
Passed!  - Failed: 0, Passed: 8, Total: 8            (.conductor/bg-logs/dv61-tests-20260826-115424846.log)
```

- `ABugAndAFollowupBecomeIssuesInTheirOwnLabelledClass` — labels, title shape, and `TaskIdIn(body)` is
  **null** on a ledger issue
- `AnEntryThatIsAlreadyClosedIsNeverCreated`
- `TheRunEndingClosesTheBoardAndLeavesTheLedgerOpen` — the claim
- `TheRetireSweepNeverTouchesALedgerIssue`
- `AFixedBugClosesItsIssueOnTheNextPassWithAComment`
- `ABugFiledWithNoOtherNewsStillReachesTheBoard` — the cursor cannot see the bugs table
- `AnUnchangedLedgerAndNoEventsCostsZeroRequests` — and the other half stays free
- `AProseOpenRowCountsAsOpenForTheLedger`

Digest: `KS11_5MetricsOnDemandTests` +2 tests, golden `testdata/ks11-5/answer-daily.txt` gains exactly
one line — `ledger: 2 open bugs · 2 open followups · oldest bug 26 days` (3 bugs seeded, one fixed;
3 followup rows, one CLOSED, one spelled `**OPEN, owner-gated**`). `KS11` 136 green, `SF7_1` 35 green.

## The live proof — the real GitHub API, private scratch repo

`tools/dv6/dv6-1-live-proof.ps1` (own state home, own repo, own PRIVATE destination
`shaahink/dv61-ledger-scratch`, the fresh Debug build — traps 1, 2, 5).
Log: `.conductor/bg-logs/dv61-live5-20260826-121013947.log`; engine output in
`%TEMP%/dv61-rig/{dry,pass1,pass2,pass3}.log`.

Measured, against the real API:

- **dry run**: `8 created · 0 errors`, 2 requests, and the repository stayed **empty** — OK
- **pass 1**: `8 created · 0 updated · 0 unchanged · 0 retired · 1 comments · 0 errors`, 13 requests.
  2 checkpoint cards, **3 bug issues, 2 followup issues** (the `CLOSED` row minted nothing), 1 diary.
  GitHub accepted every label; the bug issues carry no task marker; the body states the lifetime.
- **pass 2**, after `conductor bug fix 3` and one followups row flipped to CLOSED:
  the fixed bug's issue is **closed**, relabelled `conductor:status:fixed`, and carries the comment
  *"the bug ledger in this repo's run.db no longer lists this bug as open"* — while **the bug nobody
  fixed and the prose-OPEN followup are still open**, on a run whose cards were being closed in the
  same pass. That is the whole checkpoint, on a real board.
- **pass 3**: `0 created · 0 errors`.

### What the live run caught that no test could — bug #79, filed

Pass 2 also created **4 duplicate issues**: it listed the repository seconds after pass 1, saw only 3
of the 8 issues, and re-created the rest (`bug:1` was #5 and became #10; the diary was #8 and became
#12). This is **pre-existing and not DV6.1's**: `GithubCommand.PushAsync` builds its sync with
`GithubMap.Transient()` because a read-only backfill must not write to the archive it reads, so
nothing survives the process — exactly the failure KS9.2 measured and fixed *for the live mirror* by
persisting `github_map` rows. **DV7.3's owner backfill is the command that will hit it.** Filed as
bug **#79 (high)**; the rig now settles the destination before the second pass, and asserts
`0 created`, so the lag cannot be mistaken for a lifetime failure again.

Second observation, by design rather than a defect: a ledger entry that is **closed while its issue
is invisible to the replica** is not created and not lost — the close simply lands on the next pass
(seen live: `followup:FU-DV6-1` closed on pass 3, `1 updated`).

## Honest deviation

"Closed by the commit that closes them": a bug row carries `fixed_session`, not a sha, so the closing
comment names the **ledger** rather than a commit. Nothing in the store links a bug to the commit that
fixed it; inventing one would be a guess dressed as provenance.

## Final live run — all checks green

With the settle step in place, `.conductor/bg-logs/dv61-live6-20260826-121217265.log` is clean end to
end, including the two checks the lag had been hiding: **"the second pass creates NOTHING - every
entry already has its issue"** and **"the CLOSED followup row's issue is CLOSED"**.

```
DV6.1 LIVE PROOF: all checks passed
```

The board it produced was kept for a human to read (private):
`https://github.com/shaahink/dv61-ledger-scratch` — delete it whenever.
