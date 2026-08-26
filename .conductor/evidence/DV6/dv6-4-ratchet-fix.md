# DV6.4 follow-through — the architecture ratchet the SARIF commit tripped

Session #20 (FIX), 2026-08-26. Stage DV6, attempt 2/8.

## What was red

Conductor's independent `engine-full` battery after session #19 came back RED — one test,
one assertion, out of 3468:

```
Architecture ratchet — type count went the wrong way:
GithubSarifDtos.cs             declares 4 types (allowed 3). Give each type its own file.
   at Conductor.Tests.ArchitectureTests.NoFileGrowsPastItsTypeCeilingOrItsRecordedDebt()
      in C:\code\conductor\tests\Conductor.Tests\ArchitectureTests.cs:line 108

Failed!  - Failed: 1, Passed: 3467, Skipped: 0, Total: 3468
```

Nothing in the SARIF feature was wrong. `GithubSarifDtos.cs` was added whole by DV6.4's own
feature commit (7a336e3) carrying four records, and `tests/Conductor.Tests/architecture-baseline.json`
is EMPTY — `filesOverLineCeiling: {}`, `filesOverTypeCeiling: {}`. There is no debt slot to widen,
`maxTypesPerFile` is a hard 3, and raising it is the one move this run forbids.

## The fix — a split that means something, not a shuffle

`GithubRepoInfo` is not a SARIF document. It is the *repository* read the upload path consults to
say WHY code scanning refused — the private-repo / Advanced-Security sentence DV6.4 shipped. It
came out into `src/Conductor.Core/Integrations/Github/GithubRepoInfo.cs`, which is already this
folder's convention (`GithubCard.cs`, `GithubSyncResult.cs`, `SarifBugLocation.cs`,
`SarifDocument.cs` are each one type, one file).

`GithubJsonContext.cs:35` needed no edit — same namespace, so the source-generated
`[JsonSerializable(typeof(GithubRepoInfo))]` binding is unchanged.

Diff: +13 / -9, two files. No test touched, no expectation relaxed, no ceiling raised, no
baseline entry added.

## Proof

**1. Both ratchets measured independently across all of `src`** (the test's own regex and its own
file scope, run outside the test host so the measurement is not the thing being measured):

```
types>3: []
lines>500: []
```

Not just the one file — nothing anywhere in the engine is over either ceiling.

**2. The architecture suite, through a fresh build of the working tree:**

```
dotnet test tests/Conductor.Tests/Conductor.Tests.csproj --filter FullyQualifiedName~ArchitectureTests
Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7, Duration: 319 ms
```

All seven, including `BaselineDoesNotListDebtThatIsAlreadyPaid` — which would have caught a
baseline entry added to buy silence.

**3. The GitHub + SARIF regression set**, to prove the moved DTO still round-trips through the
source-generated JSON context: see `dv6-4-ratchet-fix-tests.txt` beside this file.

The solution compiled clean as part of both runs — a type moving files is exactly the change a
compiler catches, and it did not.

## What is NOT claimed here

Bug #82 still stands: GitHub has never answered this path with a 202, because every repo a DV6
proof may touch is private and this account has no Advanced Security. That is unchanged by this
fix and remains the owner's one command at DV7.3. #81, #80, #79 and #76 are likewise untouched.
