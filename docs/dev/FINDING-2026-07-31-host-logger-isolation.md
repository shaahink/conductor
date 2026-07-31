# FINDING 2026-07-31 — one host's disposal silenced another host's log

Stage SC1, fix session #4. The gate battery came back RED on a single test after session #3
claimed SC1.3. This is the diagnosis, the negative control, and the fix.

## The red

```
Failed Conductor.Tests.HostLoggingTests.DryRunWritesStructuredLogWithRunIdCorrelation [1 m 1 s]
  'stage=S1' never reached conductor-*.log within 60s.
  log dir ...\.conductor\logs contains: conductor-20260731.json (432B), conductor-20260731.log (196B)
  Last content (194 chars):
  2026-07-31 04:25:25.954 [INF] run=run-corr-123 s= stage= gate= conductor start - plan 'hostlog', ...
Failed!  - Failed: 1, Passed: 1055, Skipped: 0, Total: 1056
```

The shape mattered more than the message:

- `RunAsync` returned 0, so the assert on the exit code had already passed. The run completed.
- The log held **exactly one line** - the first one the run writes - and then stopped. Not a
  truncated line, not a missing file, not an empty file. A sink that accepted one event and no more.
- Alone the class is green in ~1s (`--filter FullyQualifiedName~HostLoggingTests`, 5/5). Only the
  full battery reddens it.

That rules out the run loop (it ran to completion), and it rules out the flush race the helper was
written to absorb (the host is disposed before the read, and the `shared: true` file sink flushes per
event). Something closed the sink underneath a live run.

## Root cause

`ConductorHost.Build` registered Serilog as:

```csharp
builder.Services.AddSerilog((_, lc) => { ... });   // preserveStaticLogger defaults to FALSE
```

With `preserveStaticLogger: false`, Serilog.Extensions.Hosting does two process-global things:

1. assigns the built logger to the static `Serilog.Log.Logger`; and
2. registers `SerilogLoggerFactory` with a **null** logger - and a null registered logger means the
   factory's disposal path is `Log.CloseAndFlush()`, i.e. *close whatever the static logger happens
   to be at that moment*.

So with two hosts alive in one process:

- the **second** host to be built owns the static slot, and
- the **first** host to be disposed closes the *other* host's logger and its file sink, mid-run.

No throw, no warning, no dropped-event counter. The live run keeps narrating into a disposed logger
and its log simply stops. The mirror-image half is delivery: a logger resolved after a second host
was built is handed the second host's logger, so its lines land in the other host's file.

Production composes exactly one host per process (`RunCommand`, `DemoCommand`), so the visible
victim was the test suite - which composes ~40 of them, in parallel. Every SC1 session added more
host-building test classes (`SC1TelegramStartsOnRunPathTests`, `SC1TelegramStatusTruthTests`,
`SC1TelegramLateConfigTests`, `SC1TelegramLateConfigRunPathTests`), which is why a latent defect
became a reproducible red at SC1.3 rather than earlier. The hazard is not test-only: any second host
in a process would silence the first the same way.

Note the comment in `DryRunWritesJsonLogWithCorrelationProperties` that reads "Serilog's file sink is
process-global". It is not - the *logger* is. Same symptom, wrong organ, and the earlier session
absorbed it into an assertion instead of chasing it.

## Fix

`src/Conductor/Core/Hosting/ConductorHost.cs` - `preserveStaticLogger: true`. Each host owns its
logger, and disposing a host disposes and flushes exactly that logger. Nothing in this codebase reads
the static `Serilog.Log` (grepped: `ConductorHost.cs` is the only file that so much as imports it),
so preserving it costs nothing.

## Negative control

`tests/Conductor.Tests/HostLoggerIsolationTests.cs` reproduces both halves deterministically - two
hosts, no parallelism, no timing. Against the **unfixed** engine:

```
[xUnit.net]     Conductor.Tests.HostLoggerIsolationTests.BuildingASecondHostDoesNotStealTheFirstHostsLogger [FAIL]
  at Conductor.Tests.HostLoggerIsolationTests.SingleLog(PlanConfig plan) ... line 124
[xUnit.net]   Assert.Contains() Failure: Sub-string not found
  at Conductor.Tests.HostLoggerIsolationTests.DisposingOneHostDoesNotCloseAnotherHostsLogSink() ... line 62
Test Run Failed.  Failed: 2
```

Line 62 is `live-after-unrelated-dispose` - the live host's narration after an unrelated host was
disposed. That is the production symptom, on demand. Line 124 is `SingleLog` finding no log file at
all for host A, because A's line had been delivered into B's file.

## Green

After the fix, same filter: `Total tests: 7, Passed: 7` (5 `HostLoggingTests` + 2 new).

Full `engine-full` gate command (`dotnet test Conductor.slnx`):

```
Passed!  - Failed: 0, Passed: 1058, Skipped: 0, Total: 1058, Duration: 1 m 38 s
```

1056 -> 1058 is the two new tests. No test was deleted, skipped, relaxed or rebaselined; no gate or
ratchet ceiling was touched. `HostLoggingTests` is unchanged.
