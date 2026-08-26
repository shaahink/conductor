# DV4 repair (session #14) — the two architecture ratchets the s013 battery caught

Conductor's independent `engine-full` after session #13 came back RED with 2 failures out of 3339.
Both were real, both were introduced by DV4's own checkpoints, and both are fixed here by MOVING
CODE. No baseline entry was added, no test was skipped, no expectation relaxed, no ceiling raised.

## Failure 1 — `ArchitectureTests.NoFileGrowsPastItsTypeCeilingOrItsRecordedDebt`

```
  CourierWire.cs                 declares 4 types (allowed 3). Give each type its own file.
  ICourierSource.cs              declares 4 types (allowed 3). Give each type its own file.
```

Ceiling is `maxTypesPerFile: 3` in `tests/Conductor.Tests/architecture-baseline.json`; neither file
had a debt entry, and none was added. DV4.3 grew both files past the bar. Fix — one type each moved
into its own file, exactly what the failure message asks for:

| was | now | file |
|---|---|---|
| `CourierWire.cs` (CourierPush, CourierButton, CourierAck, **CourierJson**) | 3 types | new `src/Conductor.Core/Courier/CourierJson.cs` |
| `ICourierSource.cs` (CourierDelivery, CourierCallback, ICourierSource, **CourierConflictException**) | 3 types | new `src/Conductor.Core/Courier/CourierConflictException.cs` |

`CourierWire.cs` also drops its two now-unused `System.Text.Json` usings, which travelled with
`CourierJson`.

## Failure 2 — `DV3_3TranscriptionTests.Prune_is_the_only_code_in_the_engine_that_deletes_an_inbox_file`

Filed as bug **#77**. The reported offenders:

```
  CourierDaemon.cs:353 in private static void Discard(InboxStore store, string? adopted)
  CourierPresence.cs:114 in public static void Clear(string? stateHomeRoot = null)
```

**How the sweep judges** (read before theorising): it is FILE-level. Every `src/**/*.cs` whose text
mentions `InboxStore` or `InboxNote` is swept; every `File.Delete(` / `Directory.Delete(` line in
such a file whose enclosing method is not named `Prune` or `TryDelete` is an offender. So a file
lands in scope by MENTIONING the inbox, not by touching one.

### 2a. `CourierPresence.Clear` — in scope for a coupling that should never have existed

`CourierPresence.Clear` deletes `CourierHome.PresencePathFor(...)`: a courier presence record, not an
inbox file, ever. The file was swept because of exactly one line — it called
`InboxStore.WriteAtomic` as a general-purpose atomic writer.

`InboxStore.WriteAtomic` had quietly become the engine's atomic writer for FOUR unrelated records:
the courier's durable offset, its presence claim, its settings, and the dead-letter box. Fix — a real
home for the primitive, and the note store stops being a file-utility library:

* new `src/Conductor.Core/AtomicFile.cs` — `AtomicFile.Write(path, content)` (temp file + rename over
  target) with its own private `TryDelete` that removes only the temp file it just wrote;
* `InboxStore.WriteAtomic` is **removed**, not delegated — its own two callers (`AttachTranscript`,
  the note rewrite) now call `AtomicFile.Write`;
* `CourierOffset`, `CourierPresence`, `CourierSettings` and `DeadLetterBox` repointed; `CourierOffset`'s
  `<see cref="InboxStore.WriteAtomic"/>` doc reference follows it.

`CourierPresence.cs` now contains no reference to the inbox at all, so it is out of the sweep on the
truth rather than on an exemption. The sweep's power to catch a real inbox deletion is unchanged.

### 2b. `CourierDaemon.Discard` — a genuine second deleter of inbox files, now deleted

This one was not a false positive. `Discard` combined `store.Dir` with the relative path
`AdoptMedia` returned and called `File.Delete` on it: a file inside an inbox, removed by something
that is not prune.

It ran in exactly one place — the narrow race where `store.Has(id)` said the note was new but
`store.Append` then refused it because another writer filed the same id in between. The media had
already been moved into the inbox by `AdoptMedia`, so it was an orphan no note names.

**The fix is to stop deleting.** `Discard` is gone; the orphan stays and is named in the daemon's log
line for that race. Three reasons, in order:

1. DV3.3's property is deliberately absolute — *nothing* in the engine removes a file from an inbox
   except prune. That is what makes the inbox safe to hold the only copy of something the owner said,
   and the test's own doc comment names "a well-meaning future clean-up" as the thing it exists to
   stop. `Discard` was that clean-up.
2. `RemoteSurface.Inbound` — the OTHER producer of the same record, which the codebase repeatedly
   insists must not diverge from the courier — already leaves the identical orphan in the identical
   race, and always has.
3. A duplicate copy of a voice note costs kilobytes. A second code path that deletes inbox files
   costs the invariant.

Nothing was weakened to allow this: **no test asserted the discard behaviour** (`grep -rn "Discard"
tests/` finds no daemon test), so removing it removes an unmeasured behaviour and restores a measured
one. The architecture test is itself the regression test — re-adding any inbox deletion outside
prune turns it red again.

Bug #77 is closed by this commit.

## Verification

```
$ dotnet build Conductor.slnx -nr:false
Build succeeded.  0 Warning(s)  0 Error(s)

$ dotnet test Conductor.slnx --no-build -nr:false \
    --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~DV3_3TranscriptionTests"
Passed!  - Failed: 0, Passed: 27, Skipped: 0, Total: 27
```

Full suite (background child `s14-full`, trap 17):

```
$ conductor bg start --purpose s14-full -- dotnet test Conductor.slnx --no-build -nr:false
bg started PID=5064 purpose=s14-full
Passed!  - Failed:     0, Passed:  3339, Skipped:     0, Total:  3339, Duration: 3 m 46 s - Conductor.Tests.dll (net10.0)
```

**The total is the proof that nothing was weakened: 3339 tests before, 3339 tests after.** The red
battery reported `Failed: 2, Passed: 3337, Total: 3339`; this one reports `Failed: 0, Passed: 3339,
Total: 3339`. The two that were failing now pass; no test was removed, skipped or relaxed, and
`architecture-baseline.json` is byte-identical (`git diff` touches no file under `tests/`).

Log: `.conductor/bg-logs/s14-full-20260826-103519094.log`
