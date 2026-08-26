# ADR 0008 — The courier outlives the run, and its inbound port is loopback with a secret

- **Status**: Accepted
- **Date**: 2026-08-26
- **Decided in**: Divan, DV4.1–DV4.4 (`src/Conductor.Core/Courier/`), reconciled at DV7.1
- **Supersedes**: nothing. Sits beside [0005 — push-only remote observability](0005-push-only-remote-observability.md),
  which decided that no inbound port is part of the supported design. This ADR opens one and states
  the four conditions under which that is not a reversal.
- **Sourced from**: `docs/dev/NEXT-ERA-FINDINGS-2026-08-23.md` §6.2, §6.4, §6.5, §6.6, §6.9

## Context

Until Divan, every conductor process was born and died with a run. The Telegram poll loop lived
exactly as long as the run that owned it (`TelegramService.cs` — `private int _offset;`, advanced in
memory), which is why the offset never needed to be durable and why the machine answered *nothing*
between runs. The findings measured the consequence: the owner sends a voice note at 23:00, no run is
live, and the note reaches nobody. Telegram holds an undelivered update for **24 hours** and then
drops it.

Fixing that means one process on the machine that is awake when no run is. That is a genuinely new
shape for this codebase, and it collides with three things the repo had already settled:

1. **ADR-0005** — "no inbound port, tunnel or reverse proxy is part of the supported design."
   The courier must accept a push from a live run (so a run's own outbound messages go through the
   one process that holds the token), which is an inbound socket on this machine.
2. **The install discipline** — "reinstall only when no run is live." A process designed to outlive
   every run is, by construction, running during every reinstall.
3. **One consumer per token** — Telegram allows a single `getUpdates` consumer. Two pollers on one
   token starve each other, so the day the courier exists, in-run polling cannot also exist.

## Decision

**One machine-level daemon (`conductor courier run`) owns the bot token and the poll loop. It is
installed as a per-user Scheduled Task, it survives runs and reboots, and the only thing it exposes
is a loopback HTTP endpoint authenticated by a per-install shared secret.**

Four conditions make this consistent with ADR-0005 rather than a reversal of it:

### 1. The port is loopback-only, secret-authenticated, and fixed

`CourierEndpoint.cs:39` binds `127.0.0.1` and nothing else; `:27` fixes the port at **47137**
(`CONDUCTOR_COURIER_PORT` overrides it — a named port, never a scan, per the trap-3 discipline that
two conductor runs may share a machine). `CourierListener.cs:112` rejects any request whose
`X-Conductor-Courier` header does not match `CourierSecret`, with `401` and the path to read the
secret from. The secret lives in the state home at `courier.secret` (`CourierHome.cs:39`),
file-permission-protected by `CourierSecret.Protect`, and `CourierSecret.ProtectionComplaint` says so
out loud when the ACL is wrong.

This is the **same posture the control plane already carries** — ADR-0005's argument was never "no
socket", it was "no surface reachable from off this machine, and no auth story to maintain." A
loopback listener with a shared secret introduces no remote attack surface and no credential
lifecycle beyond one file.

### 2. It is ingress for *notes*, never for *run state*

The courier accepts `/hello` and `/push` (`CourierEndpoint.cs:43,46`) — a presence handshake and an
outbound message handed over. Nothing that arrives on that socket writes run state. The inbound half
(owner notes) lands in `.conductor/inbox/`, which a session **reads as a prompt battery** and a human
promotes by hand. ADR-0005's real invariant — *the phone cannot change what the run decides* —
survives intact: a note is context, not a command.

### 3. The offset is durable and delivery is idempotent

`TelegramService`'s in-memory offset was correct for a process that dies with the run and wrong for
one that restarts. `CourierOffset` (`Courier/CourierOffset.cs`) persists it to `offset.json` in the
state home, and the inbox dedups by delivery id, so a courier killed between receive and acknowledge
files the note exactly once on restart rather than replaying every update Telegram still holds
(findings §6.2). Two writers, one inbox, is handled the same way: atomic temp-file-plus-rename
(`AtomicFile`) and an append-only index, with a read cursor so the battery does not grow without
bound (§6.6).

### 4. Version skew is refused by name, and the installer owns the restart

A process built to outlive everything else will, left alone, keep running last month's engine. Two
mechanisms close that:

- **The protocol states its version.** `CourierProtocol.Version = 2`; `CourierProtocol.RefuseStale`
  compares what the courier speaks against what the run speaks and refuses **by name**, naming
  `conductor courier restart` (`CourierProtocol.cs:31`) as the fix.
- **`tools/install.ps1` stops it and puts it back.** A running courier holds the published exe open,
  so the publish would fail on a file lock. Step 0 of the installer calls `Stop-ConductorCourier`
  (`tools/lib/courier-guard.ps1`), and after the publish it restarts it on the *new* engine, warning
  loudly if it could not (`tools/install.ps1:77-99`).

The Scheduled Task is registered from XML rather than `schtasks /SC ONLOGON`, because the
command-line form cannot express restart-on-failure (`CourierTask.cs:37-47`). Per-user, logon
trigger, no admin rights, no Windows Service ceremony. `CourierTask` takes its shell runner as a
constructor argument, so the test suite never registers anything on the developer's machine.

### 5. Where the courier is configured, in-run polling refuses to start

`CourierPrecedence.Configured` / `.PollingRefusal` (`Courier/CourierPrecedence.cs:34,43`): when a
courier exists on the machine, a plan whose telegram block would poll refuses to start that loop and
names the courier. The run pushes through `CourierChannel` or not at all. A machine with **no**
courier keeps today's behaviour byte-identically — the KS11.1 golden-replay standard, reused
(findings §6.9).

## Consequences

- **The machine now has a process the owner installs and the owner stops.** `conductor courier
  install | uninstall | restart | stop | status` is a lifecycle, not a flag, and it is documented as
  one. `ARCHITECTURE.md`'s claim that there is no long-lived process outside the run needed the
  correction it got at DV7.1.
- **The honest limit is stated, not hidden.** The courier narrows the gap from "no run live" to
  "machine on". A note sent to a sleeping laptop on Friday is gone by Monday, dropped by *Telegram*,
  not by conductor (24-hour `getUpdates` retention, findings §6.3). That sentence belongs in the
  courier's operating docs and is the honest long-term argument for an always-on host.
- **Reinstall is no longer a two-step ritual the owner has to remember.** It is also no longer safe to
  publish the engine by hand around a live courier; the installer is the supported path.
- **A fourth listener exists on the machine — but not in the run's process.** `CourierListener` is
  constructed only by `conductor courier run` (`Commands/CourierCommand.cs:276`); the run process
  still registers exactly one `IHostedService` (`Hosting/ConductorHost.cs:121`, `TelegramService`),
  so `ARCHITECTURE.md`'s "there is no `IHostedService` running the loop" stayed true and gained a
  pointer instead of a correction.
- **If a note ever needs to *steer* a run**, that is a new decision and this ADR does not grant it.
  Promotion stays an explicit act — `conductor inbox` plus a human, or DV4.4's single promote button
  — precisely so that ADR-0005's invariant keeps a name.
