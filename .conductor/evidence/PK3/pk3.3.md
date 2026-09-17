# PK3.3 - a live run names its own project to the courier (evidence, s6, 2026-09-17)

Code: 9f63a5f on feat/peyk-courier. Rig: tools/peyk/pk3-3-live-proof.ps1 (helpers pk3-rig-lib.ps1),
log: .conductor/evidence/PK3/pk3.3-live-proof.log - 12/12 checks, on the fresh build of 9f63a5f
(engine 13:06:37Z, courier 13:06:34Z). Scratch courier on its own home and port with a scratch token and a
stub Bot API that delivers inbound notes on demand; the real courier was not touched.

## Acceptance

A fresh plan name in an allowed repo files an inbound note on the first session boundary without
`courier allow`:

1. Setup: the repository is allowed as `PK33OldPlan` (the owner's entry, no `by`).
2. Before any run, a reply to a `PK33FreshPlan · s1` push is PARKED, for the right reason:
   "That message is about "PK33FreshPlan", which is not a project on this machine any more. It has:
   PK33OldPlan." - F-COUR-4, measured.
3. A scratch `conductor run --once` of plan `PK33FreshPlan` in that repository (run
   e385ea6c273c46e2b51efadaeb07d502) reaches its first session boundary; its run log reads
   `courier: the courier files notes for PK33FreshPlan at <repo> from now on - added by run e385ea6c...`;
   courier.json gains `{"plan":"PK33FreshPlan","repo":"<repo>","by":"run e385ea6c..."}` BESIDE the
   untouched owner's entry; courier.log records the addition.
4. The same kind of reply is then FILED into `<repo>/.conductor/inbox`, not parked - with the same courier
   process (no restart: the router reads the allowlist live). The rig never runs `courier allow`.

`courier allow` is unchanged (CourierCommand.Allow untouched by 9f63a5f).

## D4 security note, restated

The allowlist exists so a daemon holding the bot token cannot be made to write into arbitrary checkouts on
this disk. A run already has write access to its own checkout - it is running there - and its hello
carries the install's shared secret like every other loopback verb, so the entry it adds grants no reach
the caller did not already have. The courier still refuses a hello naming a path that is not a directory,
or no plan. `courier allow` stays the way to allow a project with no run live.

## Tests

PK3_3CourierHelloTests 5/5 (the parked-then-filed acceptance end to end against the real daemon and
TelegramCourierSource; the entry beside the owner's and idempotent; an owner edit to courier.json since
startup survives a hello; refusals leave courier.json byte-identical; the run asks once answered and again
while unanswered). PK3_1's protocol properties now include POST /hello. Scoped run 303/303.
