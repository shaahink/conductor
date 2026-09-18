# PK6.1 — THE CAUSE: the read-out of PK2.3's window

**Dated 2026-09-18.** Read-only against the real courier: nothing was started, stopped,
restarted or reinstalled. Session s13, stage PK6.

## The window

| | UTC | Instrument |
|---|---|---|
| `T0` — PK2.3 armed the instruments | **2026-09-17 11:37:30Z** | `pk2.3-verify.log`; the courier's own first line under them is `2026-09-17 11:37:31Z courier run starting: pid 20052, engine 0.5.1-alpha.0.66+24dd990f19c5.dirty, protocol 2` |
| `T1` — this read-out | **2026-09-18 11:40:33Z** | `courier.run.json` `lastPollUtc` at the moment of reading |

**24 h 03 m elapsed.** The stage's floor (24 h) is met; D2's own text asks for 48 h, which
this window does not reach — see *What this window does not settle*, below.

## The finding, in one line

**The first recorded death in the window is 2026-09-17 12:23:18Z: pid 20052 died by an
unhandled `TaskCanceledException` — HttpClient's 65-second timeout on `getUpdates` —
escaping the poll loop and terminating the process. It is not the machine sleeping. The
same exit path killed the courier a second time, 22 h later, at 2026-09-18 10:42:33Z.**

## Every event in the window

| UTC | pid | What | Recovery |
|---|---|---|---|
| 09-17 11:37:31Z | 20052 | start (PK2.3's arming); wrote a death record for its predecessor pid 7988 | — |
| **09-17 12:23:18Z** | **20052** | **`courier run DIED (unhandled, terminating): TaskCanceledException`** | pid 3232 up 12:25:01Z — **gap 1 m 43 s** |
| 09-17 14:39:13Z | 3232 | `getUpdates conflict … terminated by other getUpdates request` — a second poller, logged once and backed off. Not a death | loop continued |
| 09-18 08:35:53Z | 3232 | `courier received SIGHUP - the process is being asked to end`; last poll 08:35:27Z | pid 1972 up 08:40:01Z — **gap 4 m 34 s** from last poll |
| **09-18 10:42:33Z** | **1972** | **`courier run DIED (unhandled, terminating): TaskCanceledException`** — same exception, same stack | pid 20860 up 10:45:02Z — **gap 2 m 29 s** |
| 09-18 11:40:33Z | 20860 | alive, polling | — |

Two deaths, one requested end, three recoveries, **zero unrecovered outages**. Across the
whole 55 KB log (no `courier.log.1`, so this is its entire history) there are exactly
**2** `DIED (unhandled` lines: both are in this window, and both are this exception.

## The exit path, quoted and located

The journal line, twice, verbatim:

```
2026-09-17 12:23:18Z courier run DIED (unhandled, terminating): TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 65 seconds elapsing.
2026-09-18 10:42:33Z courier run DIED (unhandled, terminating): TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 65 seconds elapsing.
```

Both stacks are identical through the managed frames (the innermost differs only in
`InitialFillAsync` vs `EnsureFullTlsFrameAsync` — the same TLS read, at a different point):

```
SocketException (995) -> IOException -> TaskCanceledException -> TimeoutException -> TaskCanceledException
  TelegramService.GetUpdatesAsync   src/Conductor.Core/Integrations/TelegramService.Polling.cs:114
  TelegramCourierSource.FetchAsync  src/Conductor.Core/Integrations/TelegramCourierSource.cs:102
  CourierDaemon.PollOnceAsync       src/Conductor.Core/Courier/CourierDaemon.cs:155
  CourierDaemon.RunAsync            src/Conductor.Core/Courier/CourierDaemon.cs:96
  CourierProgram.RunAsync           src/Conductor.Courier/CourierProgram.cs:176
  CourierProgram.Main               src/Conductor.Courier/CourierProgram.cs:36
```

**Why it escapes — the hole is between two catch clauses**, `CourierDaemon.cs:102` and
`:113`:

```csharp
102: catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
...
113: catch (Exception ex) when (ex is not OperationCanceledException)
```

`TaskCanceledException` **is an** `OperationCanceledException`. On an HttpClient timeout
the daemon's own `ct` is **not** cancelled, so `:102`'s filter is false; and `:113`'s
filter excludes `OperationCanceledException` by name, so it is false too. Nothing catches
it: it unwinds `RunAsync`, leaves `Main`, and the runtime terminates the process. Filed as
**bug #100** (high); the fix and its regression test are the commit after this one.

## What the supervision did — the keep-alive trigger (D2d) works

`schtasks /query /tn "Conductor Courier" /v /fo LIST`, read at `T1`:

```
Status:            Running
Last Run Time:     18/09/2026 12:40:01     (local = UTC+1, so 11:40:01Z)
Last Result:       -2147020576             (= 0x800710E0)
Next Run Time:     18/09/2026 12:45:00
Repeat: Every:     0 Hour(s), 5 Minute(s)
Repeat: Until: Duration: Disabled
Task To Run:       ...\conductor\courier\conductor-courier.exe --task-name "Conductor Courier"
```

Two things to read here, and the second is a trap for the owner:

1. The five-minute repeating trigger is live, and it is what brought the courier back all
   three times: every recovery gap (1 m 43 s, 2 m 29 s, 4 m 34 s) lands on the next
   five-minute boundary after the death.
2. **`Last Result: -2147020576` = `0x800710E0` is not a failure.** It is the scheduler
   refusing to start a *second* instance because `MultipleInstancesPolicy` is `IgnoreNew`
   — i.e. the keep-alive trigger firing at 11:40:01Z against a courier that was already
   healthy. A negative last result on a task whose `Status` is `Running` is the trigger
   working as designed.

## It is not the machine

`Get-WinEvent` over the System log for the whole window, ids 42/107 (sleep/resume),
1 (Power-Troubleshooter wake), 12/13 (kernel boot/shutdown), 109:

> **No sleep, no resume, no boot and no shutdown in the entire window.** The only System
> events matching were three `Kernel-General` id 1 "system time has changed" at
> 2026-09-18 08:54:16Z (an NTP step of milliseconds, 14 minutes *after* the 08:40:01Z
> restart and 1 h 48 m before the second death).

The machine stayed up for all 24 h. D2's "if the cause turns out to be the machine
sleeping" branch does **not** apply. The keep-alive trigger is still the thing that saved
every outage — it is just not saving us from sleep, it is saving us from bug #100.

## Bug #93

Already `fixed` before this session: filed 2026-08-27 08:59 at stage **CH5** of the
*previous* run (`run_id 858b4838…`), closed 2026-08-27 18:49. `conductor bug fix 93` run
again at this session re-affirms it (`bug #93 -> fixed`, exit 0). Its complaint was that a
courier outage was *undiagnosable* — "no log, no stdout capture … no second record either".
This read-out is the proof that its fix holds: every question #93 could not answer is
answered above, from instruments that did not exist when it was filed. Its own detail
already recorded that `Microsoft-Windows-TaskScheduler/Operational` is disabled on this
machine; that is still true today (`IsEnabled=False`), which is why the SIGHUP's sender
below cannot be named.

## Three corrections the reading forces on the procedure

These were measured against `docs/operating.md` "The courier died — reading out why" by
following it, and are now written back into it:

1. **An unhandled-exception death leaves NO death record.** Neither successor (12:25:01Z,
   10:45:02Z) wrote one, because the exception unwinds through
   `CourierProgram.cs:181-184`'s `finally { CourierPresence.Clear(); }` — presence is
   cleared on the way out, so the next start sees nothing stale. The doc's death-record
   row implied the record is how a death is seen. It is not: **the exit journal is the
   first instrument, and the death record covers only the ends that skip that finally.**
2. **A signal death can read as a silent one.** 08:35:53Z journaled `received SIGHUP` and
   then produced *no* `Main returned` line and *did* produce a death record calling it
   "died silently". The signal line is the truth; the record's wording is not.
3. **`courier process exit:` has never been written — not once in the whole log.** The
   handler is registered (`CourierExitJournal.cs:33`, `AppDomain.ProcessExit`) and is
   correct: the CLR does not raise `ProcessExit` on an unhandled exception, nor when the
   OS tears down a signalled console process. So it records only an *orderly* end, and
   this courier has not had one. A reader waiting for that line to explain a death waits
   forever.

## What this window does not settle

- **Who sent the SIGHUP at 2026-09-18 08:35:53Z.** No shutdown, logoff or sleep event
  accompanies it, and `Microsoft-Windows-TaskScheduler/Operational` is disabled, so the
  second record that would name the sender does not exist. The procedure now says how to
  enable it (an owner act, admin) so the next read-out can answer this.
- **D2 asks for 48 h; this window is 24 h 03 m** (the stage's floor). The instruments stay
  armed and the procedure is written down, so the watch plan's ledger checkpoint reads the
  next 24 h the same way — which is the point of writing the procedure rather than the
  answer.

## Instruments read (all read-only)

| Instrument | Command / path |
|---|---|
| Heartbeat | `%LOCALAPPDATA%\conductor\courier\courier.run.json` — `lastPollUtc 2026-09-18T11:40:33.0369066+00:00`, pid 20860, started 10:45:01Z |
| Courier log + exit journal + death records | `%LOCALAPPDATA%\conductor\courier\courier.log` (55591 bytes, no `.1` generation) |
| Scheduler | `schtasks /query /tn "Conductor Courier" /v /fo LIST` |
| Scheduler's own history | `Microsoft-Windows-TaskScheduler/Operational` — **disabled** (`IsEnabled=False`) |
| Machine | `Get-WinEvent` System, ids 42, 107, 1, 109, 12, 13, from 2026-09-17T11:30:00Z |
| Source | `CourierDaemon.cs:93-116`, `CourierProgram.cs:176-185`, `CourierExitJournal.cs:33,53-57,72-74` |

## Appendix — the fix for bug #100 and its negative control

`CourierDaemon.cs:121` widened from `catch (Exception ex) when (ex is not
OperationCanceledException)` to `catch (Exception ex)`. Nothing else changed: our own
cancellation is still taken by `:102` (checked against `ct`) and by the `Delay`'s own
`catch … { break; }`, which is why the loop still stops when asked.

`tests/Conductor.Tests/PK6_1CourierPollSurvivalTests.cs` pins it as a **property over the
eight things a poll can throw**, not an example — the poll's failure vocabulary grows every
time the source learns a call, and the rule is the same for all of them.

**With the fix** — the whole courier battery:

```
dotnet test Conductor.slnx --filter "FullyQualifiedName~Courier"
Passed!  - Failed: 0, Passed: 165, Skipped: 0, Total: 165, Duration: 16 s
```

**Without it** — the old filter restored, the new class alone:

```
Failed … NothingAPollThrowsEndsTheProcess(what: "HttpClient's own timeout (bug #100, the measured death)")
Failed … NothingAPollThrowsEndsTheProcess(what: "a bare TaskCanceledException")
Failed … NothingAPollThrowsEndsTheProcess(what: "a bare OperationCanceledException on a token nobody holds")
Failed … NothingAPollThrowsEndsTheProcess(what: "a foreign token's cancellation")
```

Exactly the four `OperationCanceledException`-family cases fail and the other four pass —
which is the shape of the hole the read-out found, reproduced in a test: the old filter
caught transport errors correctly and let precisely this family through. The daemon file
was restored from a copy immediately after; `docs/operating.md` battery re-run separately,
`SF7_1Docs` 56/56.

**The real courier still runs the old binary** (pid 20860, started 2026-09-18 10:45:02Z).
This fix reaches it at the owner's reinstall between plans, not from any session.
