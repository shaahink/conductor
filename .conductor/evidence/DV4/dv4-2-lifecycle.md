# DV4.2 — the courier's lifecycle: a logon task, a presence record, a version it states

**Claimed:** 2026-08-26, session 10, branch `feat/divan`, commits `962d6bc` + this one.
**Live proof:** `.conductor/evidence/DV4/dv4-2-live-proof.log` — **PASS**, 32 checks, exit 0, against
the REAL Windows Task Scheduler with a scratch-named task that is registered and then removed.
**Scoped suite:** `DV4_2 | DV4_1 | SF7_1 | K7_2 | KS11_1` → **121 passed, 0 failed**.

---

## What the checkpoint asked for, and where each piece is

| Asked | Delivered | Measured |
|---|---|---|
| `courier install / uninstall / restart / status` | plus `stop`; `src/Conductor/Commands/CourierCommand.Lifecycle.cs:15,62,86,111`, routed at `CourierCommand.cs:84-90` | live proof steps 2, 4, 7 |
| per-user Scheduled Task, restart-on-failure | `CourierTask.BuildXml` — `src/Conductor.Core/Courier/CourierTask.cs:153`, `RestartOnFailure` PT1M | live proof step 3, read back from the SCHEDULER |
| no admin rights | `LeastPrivilege` + `InteractiveToken` principal — `CourierTask.cs:176`; registered from an **unelevated** shell | live proof header (`elevated: False`) and step 3 |
| `tools/install.ps1` stops and restarts a running courier | `tools/install.ps1:44,78,96` dot-sources `tools/lib/courier-guard.ps1` and brackets the publish | live proof step 6; order asserted in `DV4_2CourierLifecycleTests` |
| version handshake, refusing a stale courier BY NAME, naming the restart command | `CourierProtocol.Version` = 1 and `RefuseStale` — `src/Conductor.Core/Courier/CourierProtocol.cs:22,36`; surfaced at `CourierCommand.cs:128,178` | live proof step 5, through the real CLI |
| live proof registers a scratch-named task and unregisters it | `tools/dv4/dv4-2-live-proof.ps1`, task `Conductor Courier SCRATCH dv4-2` | steps 0, 7, 8 — the owner's task is queried before and after and is unchanged |

## The two decisions worth reading

**XML, not `schtasks /SC ONLOGON`.** The command-line form cannot express restart-on-failure at all,
and that is the setting the whole daemon depends on: a laptop wakes with no network, the first poll
throws, and a courier that exits there has silently stopped answering the phone — weeks later nobody
knows. The definition also carries `IgnoreNew` (one `getUpdates` consumer per token, so a second
logon must not start a second poller) and `ExecutionTimeLimit PT0S` (the default would kill a daemon
after three days).

**The presence record is DV4.3's loopback hello, written down.** `courier run` writes
`courier.run.json` into the state home — pid, protocol, engine version, the exe it holds open, the
task that started it (`CourierPresence.Current/Write` at `CourierPresence.cs:47,61`) — and clears it
on the way out (`CourierCommand.cs:236,255`). Three readers need that answer today and none can ask
the daemon directly until DV4.3's listener exists: `courier status`, `install.ps1`, and the version
handshake. `Live()` (`CourierPresence.cs:87`) checks the pid is running **and** that its start time
matches the record, because a recycled pid is otherwise indistinguishable from the courier.

## What the live proof measured that a test could not

1. **The scheduler accepts the definition.** Only `schtasks.exe` can say whether the XML validates,
   whether a standard user may register it, and what survives the round trip. Step 3 reads back the
   scheduler's OWN copy and asserts eight properties of it.
2. **The scheduler NORMALISES what it stores** — it drops `<RunLevel>` when it is the default
   (LeastPrivilege) and rewrites `<UserId>` to the account SID. The first version of the proof
   asserted the literal element and went red on a correct registration; the honest measurement is
   what is *absent* (`HighestAvailable` never appears) plus the fact that an unelevated shell
   registered it. That correction is in the script, with the reason.
3. **A real defect in the installer's guard, found here and nowhere else.** `courier-guard.ps1`
   called `schtasks.exe` directly, and `tools/install.ps1` runs with
   `$ErrorActionPreference = "Stop"` — under which a native command writing to **stderr** is a
   TERMINATING error in Windows PowerShell. `schtasks /Query` on an unknown task name writes
   `ERROR: The system cannot find the file specified.` to stderr, so the guard would have crashed
   the installer on every machine that has *not* installed a courier — which is every machine today.
   Fixed by routing every shell-out through `Invoke-CourierSchtasks`, which sets
   `$ErrorActionPreference = "Continue"` in its own scope and reads `$LASTEXITCODE`. The proof now
   runs the guard in a child shell under `Stop` against an unknown task and asserts exit 0; the test
   suite pins that there is exactly one `& schtasks.exe` call site in the file.
4. **A courier it cannot stop is refused, not killed.** Step 6 plants a live presence record and the
   guard reports `stopped=False`, which is what makes `install.ps1` throw with the pid and the exe
   rather than overwrite a locked binary or kill a process it did not start.

## Safety posture of the proof (traps 1, 3, 4)

* It never runs `tools/install.ps1` and never publishes — it exercises the guard functions directly.
* Its own state home under `%TEMP%\dv42-rig`, its own scratch task name, `CONDUCTOR_PLAN` cleared.
* It **starts** the registered task only when the machine cannot be made to poll for real: this
  machine has `CONDUCTOR_TELEGRAM_TOKEN` set (process + user), but no configured courier at
  `%LOCALAPPDATA%\conductor\courier\courier.json`, so the daemon the scheduler started refused by
  name (`no chats are listed`) and exited before dialling anything. The gate and its reasoning are
  in the script header; with a configured courier present it skips that step and says so.
* The owner's `Conductor Courier` task is queried before and after: not registered, unchanged.

## Not in this checkpoint

* The loopback listener, the shared secret and `CourierChannel` are **DV4.3**. The handshake shipped
  here reads the presence file; DV4.3 serves the same record over the socket and reuses
  `CourierProtocol.RefuseStale` unchanged.
* The real installation with the real token is the owner's, at **DV7.3**. Nothing here installs a
  courier on this machine.
