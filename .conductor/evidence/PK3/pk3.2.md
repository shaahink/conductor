# PK3.2 - conductor say and the direct fallback (evidence, s6, 2026-09-17)

Code: 965a6dd (say, CourierChannel direct path, health line, caption clip) on feat/peyk-courier.
Rig: a9c6a70 tools/peyk/pk3-2-live-proof.ps1 -RealSend (helpers tools/peyk/pk3-rig-lib.ps1),
log: .conductor/evidence/PK3/pk3.2-live-proof.log - 20/20 checks. The engine and courier the rig drove
are the fresh build of 965a6dd (src/Conductor/bin, src/Conductor.Courier/bin, 12:48Z).

## Acceptance, item by item

- `say --dry-run` prints the exact bytes and the resolved chat (part A): `--to observer --file body.md
  --reply-to 12 --dry-run` printed `chat: observer -> 770000002`, `path: through the courier on port N`,
  method, parse mode, reply-to, the character and byte count, and the body between markers - compared
  byte for byte with the file (PASS); nothing reached the stub Bot API. With the courier stopped the
  dry run names `directly ... courier unreachable: no courier is running ...`.
- With the scratch courier stopped, a send lands and the log reads sent directly:
  - `say` (part A, stub): `courier unreachable - sent directly: chat 770000001, message id(s) 5002`.
  - a scratch `conductor run --once` in courier mode (part B, stub): polling not started, its five pushes
    (4 sendMessage + sendDocument) went out directly, the run log (.conductor/logs/conductor-<date>.log)
    reads `courier unreachable - sent directly: no courier is running on this machine...` once per push,
    and REPORT.md / OWNER-QUEUE.md's courier health line ends `last push went directly at 12:55:33Z`.
  - ONE real message (part C): scratch courier started with the environment token behind the send-only
    relay, then STOPPED; `say --to admin --text ...` printed
    `courier unreachable - sent directly: chat 99205495, message id(s) 3821`; `say --delete 3821` printed
    `courier unreachable - deleted directly`. Relay: sendMessage -> 200, deleteMessage -> 200, no
    getUpdates reached Telegram. messages.jsonl: 3821 send + 3821 delete, origin `conductor say (sent directly)`.
    **chat id 99205495 (admin DM), message id 3821, sent 2026-09-17T12:55:39Z, deleted 12:55:40Z.**
- Ceilings refused by name in tests: PK3_2SayAndDirectFallbackTests.Ceilings_and_nonsense_are_refused_by_name_before_anything_is_sent
  (4096 text, 1024 caption, ten files, 10 MB photo, 50 MB document, photos+documents, and the switch
  combinations) - exit 2, nothing sent; the rig refused a 4097-character say by name with nothing sent.

## Tests

PK3_2SayAndDirectFallbackTests 11/11: dry run bytes/chat/path; dry run through a live courier with a media
group; say through a live courier with ids and ledger; say directly with no courier; a courier whose port
refuses the connection is unreachable (Unanswered) and say goes direct; no courier and no token fails by
name; react and delete directly; ceilings; a run in courier mode with its courier down sends directly,
logs it, the health line names the path, and when the courier comes back the next push goes through it;
a run with no token of its own records the refusal instead; the attachment caption is clipped to 1024
(bug #98, closed). Scoped battery (courier, telegram, channel, docs, architecture, KS11, DV*, SF7, K7_2):
734/735, the one miss docs/operating.md section 2 lacking `say`, added; docs tests 83/83.
