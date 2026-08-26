# CH1.3 — the CI-only failure, reproduced on purpose and then closed

## What CI said, twice in a row

`CI / windows - full gate battery`, `dotnet test`, on `feat/charkh`:

    151b4293  Conductor.Tests.KS1_2StagesFromFoldTests.DerivedStatusMatchesTheStatusSurface_ForEverySeededRun [FAIL]
              Assert.Equal() Failure: Values differ  Expected: 0  Actual: 1
              KS1_2StagesFromFoldTests.cs:line 175
              Failed: 1, Passed: 3505, Total: 3506
    3750f9a0  identical, same test, same line, same values
              Failed: 1, Passed: 3505, Total: 3506

Full job logs: `ci-151b4293-windows-job.log`, `ci-3750f9a0-windows-job.log`.
The same suite on the owner's machine: `Passed! - Failed: 0, Passed: 3506`. Twice.
The `ubuntu - cross-platform build` leg was green throughout.

## Root cause — measured, not read off the doc comment

`src/Conductor.Core/Store/SqliteRunStore.Events.cs`. Two consumers drain one
`ConcurrentQueue`:

  * `DrainLoopAsync` (line 64) — dequeues a batch every 200ms, then persists it.
  * `FlushEvents` (line 104) — dequeues on the CALLER's thread and persists, so a
    control-plane write can respond once its event is durably readable.

The doc comment on `FlushEvents` claimed the two were "safe alongside" each other
because "each event lands exactly once". Each event does land exactly once. That
was never the promise at risk. The one that was: **read-after-flush**. The drain
loop can be anywhere between its last `TryDequeue` and `PersistBatch` holding a
batch in a local list; a flush arriving in that window finds an EMPTY queue,
persists nothing, and returns — having told its caller the event is durable.

That is precisely the shape of the KS1_2 failure. The test reads the events
INSIDE the `using` (so the status surface counts no session for the stage → 0)
and reads the archive AFTER dispose, whose final drain has by then persisted the
event (→ 1). `Expected: 0, Actual: 1`.

## Proof by perturbation

The window is a few instructions wide, which is why 4 attempts to provoke it on
this 16-core machine — tight emit/flush loops, single-core affinity, a thread
holding the persist gate — all stayed green. So the window was WIDENED instead:
a 50ms spin inserted between the dequeue and the persist, and nothing else
changed.

    ungated drain loop + 50ms window:
      Conductor.Tests.KS1_2StagesFromFoldTests.DerivedStatusMatchesTheStatusSurface_ForEverySeededRun
        Assert.Equal() Failure: Values differ  Expected: 0  Actual: 1
        KS1_2StagesFromFoldTests.cs:line 175          <-- CI's failure, to the line and the values
      Conductor.Tests.KS1_2StagesFromFoldTests.StagesDeriveFromTheFold
        Assert.Equal() Failure: Strings differ  Expected: "active"  Actual: "todo"
      Failed: 2, Passed: 5, Total: 7

## The fix

Taking a batch OUT of the queue and committing it is now one indivisible step for
BOTH drainers, under a new `_drainGate` held from `TryDequeue` through
`PersistBatch` and never across the loop's `await`. A flush therefore either
drains the queue itself, or waits for the batch already in flight — either way,
everything emitted before the call is readable when it returns.

    gated drain loop + the SAME 50ms window:
      (see below)
      Passed! - Failed: 0, Passed: 7, Total: 7      (KS1_2 + CH1_3, same 50ms window)

The perturbation was then removed and replaced by a permanent one: `DrainWindowProbe`,
an internal per-instance seam that runs inside the gate between the dequeue and the
commit, null in production. `CH1_3FlushEventsTests.AFlushWaitsForABatchTheDrainLoopHas-
TakenButNotYetCommitted` sets it to hold each batch for 600ms, emits one event, waits
past the 200ms cadence so the event is out of the queue and not yet in the database,
and then flushes and reads.

## Negative control on the shipped test

The gate removed from `FlushEvents` alone — probe, drain loop and test byte-identical:

    Conductor.Tests.CH1_3FlushEventsTests.AFlushWaitsForABatchTheDrainLoopHasTakenButNotYetCommitted [FAIL]
      Assert.Contains() Failure: Filter not matched in collection
      Collection: []
    Failed: 1, Passed: 1, Total: 2

The flush returned having persisted nothing, which is the bug, stated by the test that
now stands guard over it. With the gate restored: 7/7.
