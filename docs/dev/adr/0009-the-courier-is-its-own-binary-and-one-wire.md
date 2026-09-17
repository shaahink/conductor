# ADR 0009 — The courier is its own binary and the machine's one wire, and a run no longer depends on it

- **Status**: Accepted
- **Date**: 2026-09-17
- **Decided in**: Peyk, plan A (`plans/peyk/courier.plan.json`), PK1–PK5; written at PK6.2
- **Amends**: [0008 — the courier outlives the run](0008-the-courier-outlives-the-run.md). 0008 stays
  accepted. This ADR restates its four conditions as they read after Peyk, and adds the two conditions
  that Peyk's new reach requires.
- **Sourced from**: `docs/dev/NEXT-ERA-FINDINGS-2026-09-17.md` §3.1, §3.2 and decisions D1, D2, D3, D4, D5

## Context

ADR-0008 made the courier a machine-level daemon inside `conductor.exe`: one poller for the token, a
loopback port with a secret, a durable offset, and an installer that stopped it to publish. Six runs
later the findings measured what that shape cost:

- **The courier died silently, and the run went quiet with it** (F-COUR-1, bug #93). A run whose
  courier was down could not push at all, because 0008's §5 routed every outbound message through the
  courier. The machine's one poller had become every run's single point of failure for *sending* too,
  and the one-consumer rule never required that.
- **A new plan in an allowed repo was deaf until the owner typed `courier allow --plan`.** A run had to
  ask a human to be heard in its own checkout.
- **Everything else anyone sent went around the courier.** Sessions used a skill's PowerShell
  scripts (`send.ps1`, `report.ps1`), and the owner and watchers used the same. There were three
  transports, message ids that nobody kept, and a Bot API URL in a skill folder.
- **The courier lived in the engine's binary**, so publishing the engine meant stopping the machine's
  poller (0008 §4), and a courier that was not restarted kept running the old engine.

## Decision

**The courier is its own executable (`conductor-courier.exe`, `src/Conductor.Courier`, referencing
`Conductor.Core` only). Its loopback grows into the one local API for everything anyone sends
(protocol 3). A run that cannot reach it sends directly, and a live run names its own project to it.**
`conductor courier install|status|restart|stop|uninstall|allow|deny|chat|unchat` still manage it, and
`conductor say` is the CLI for the new API.

Six conditions make this consistent with ADR-0005 and ADR-0008 rather than a widening of either. The
first four are 0008's, restated for Peyk. The last two are new.

### 1. The port is loopback-only, secret-authenticated, and fixed — unchanged, with more verbs behind it

Still `127.0.0.1`, still port 47137 (`CONDUCTOR_COURIER_PORT` overrides it), and still
`X-Conductor-Courier` matched against `courier.secret` or `401` (`src/Conductor.Courier/CourierListener.cs:115`).
Protocol 3 (D5) adds `POST /send`, `POST /react`, `POST /delete` and `GET /chats` behind that same check
(`CourierEndpoint.cs:50-59`). No verb is reachable from off the machine, and there is still one secret
file and no credential lifecycle. The listener moved into the courier's own project, and what each verb
*means* stays in Core behind `ICourierDesk`.

### 2. It is ingress for *notes*, never for *run state* — unchanged, and now read-only for figures too

Nothing that arrives on the socket writes run state. Every new verb turns into a Telegram message, or
into the undoing of one. Only the figure verbs read a project's store, to answer from it:
`/status`, `/progress`, `/money`, `/tokens` and `/evidence` (PK5.2) open that project's `run.db` with
`SqliteRunStore.OpenReadOnly`, and the plan is pinned so that resolving the path does not upsert the
state catalogue. The hello writes exactly one thing: an allowlist entry (condition 6). The phone still
cannot change what a run decides. A note is still context, not a command.

### 3. The offset is durable and delivery is idempotent — unchanged, and sends are ledgered

`CourierOffset` and the inbox's dedup by delivery id are untouched. What D5 adds is the other
direction's memory. Every send answers with the Telegram message ids it became, and the courier appends
them to `messages.jsonl` (`id`, `chat`, `origin`, `stamp`, `when`, `verb`). A direct send (condition 5)
is ledgered too, so anything sent can be replied to, reacted to or taken back.

### 4. Version skew is refused by name — and the courier binary, not the installer, carries the restart

0008 said: *Version skew is refused by name, and the installer owns the restart.* The first half stands.
`CourierProtocol.Version` is now **3** (`CourierProtocol.cs:32`), and a protocol-2 run's `/push` is still
accepted for one era, so a run on the previous engine keeps working through a new courier. A courier
that speaks an older protocol than the run is still refused by name, with its pid, its engine and
`conductor courier restart`. The installer half of 0008 §4 is reversed by D1. The courier runs from
`<install>\courier\`, so `tools/install.ps1` publishes the engine **without stopping it**, and
`install.ps1 -CourierOnly` is the one path that replaces a live courier. That reversal is what makes the
restart the courier's own business. So restarts are supervised (D2): a heartbeat in `courier.run.json`,
a death record and exit journal in `courier.log`, a keep-alive trigger on the task every five minutes,
and a live run that restarts a dead or stale courier at a session boundary and says so. The task still
comes from XML with its runner injected, so the suite registers nothing on a developer's machine.

### 5. NEW — Sending is not polling: a run that cannot reach the courier sends directly (D3)

Telegram's one-consumer rule constrains `getUpdates` and nothing else, so it never required every
outbound message to pass through the poller. When no courier takes a push, `CourierChannel` sends it
through `TelegramService`'s own transport with the environment's token and logs
`courier unreachable - sent directly` (`Integrations/Messaging/CourierChannel.cs:154`). The cause can be
no courier, a stale protocol or a refused connection. The channel-health line says which path delivered,
and `say` does the same. The courier stays the **preferred** path, because it holds the machine's chat
list and keeps the ledger, but it is no longer a path a run cannot live without.

The condition that keeps this safe: **the fallback only sends.** No code path that the fallback opens
polls, so the rule 0008 §5 enforces (where a courier is configured, in-run polling refuses to start) is
untouched, and two consumers on one token still cannot happen.

### 6. NEW — A live run may add its own project to the allowlist, and nothing wider (D4)

At its first session boundary a run POSTs `/hello` with its checkout and plan name. The courier adds that
entry if it is absent, marked `"by": "run <id>"` in `courier.json`, beside the owner's own entries
(`CourierDesk.cs:105`, `CourierSettings.Introduce`). The hello is refused if it names no plan, no
checkout, or a path that is not a directory on this machine. `courier allow` is unchanged, and it is
still how a project with no run live is allowed.

The condition, recorded as the security note D4 took: **the allowlist keeps a daemon holding the token
out of arbitrary checkouts, and self-registration adds no reach to that.** The hello goes through
condition 1's secret, and the only principal who can read `courier.secret` is this machine's user,
who can already type `courier allow` for any directory. A run already has write access to its own
checkout. The entry grants filing notes into it, which is less than the run itself can do.

## Consequences

- **The run no longer goes quiet when the courier dies.** A dead courier now costs inbound notes until a
  supervisor brings it back, which is at most five minutes with the task registered. It no longer costs
  the run's own messages. Whether the courier dies, and why, is measured before it is fixed. PK2.3 armed
  the window, PK6.1 reads it out, and `docs/operating.md` carries the read-out procedure for every death
  after that.
- **`courier deny` does not outlast a run's next start.** A denied project whose run starts again is
  introduced again at its first boundary, marked `by: run <id>`. To silence a project for good, deny it
  and do not run it here. That is the same power the owner has with `courier allow`, applied by a
  run on the owner's behalf.
- **The skills lose their wire.** `telegram-notify` keeps what a machine cannot do (what earns a message
  and how to write one) and sends through `conductor say`. `send.ps1`, `report.ps1`, `walk-ids.ps1` and
  `lib/` are deleted, and no Bot API URL is left in a skill folder (D20).
- **Two new seams in Core** (`ICourierDesk`, and `IFittingBattery` for the room's voice), recounted in
  `ARCHITECTURE.md`. Neither is a wire we do not own. The desk is the line between HTTP, which moved to
  the courier's project, and meaning, which stayed in Core.
- **Protocol 2 has an end date.** It is accepted for one era. The era after Peyk may drop `/push`, and
  a run on an engine that old will then be refused by name, the same as every other version skew.
- **The honest limit stands.** The courier narrows "no run live" to "machine on". A direct send works
  with the machine on and the courier down. Neither helps a note sent to a sleeping laptop more than
  24 hours before it wakes.
