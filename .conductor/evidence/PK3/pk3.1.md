# PK3.1 - protocol 3 on the courier (evidence, s6, 2026-09-17)

Code: commit 0d43747 (feat/peyk-courier). Rig: tools/peyk/pk3-1-live-proof.ps1 -RealSend,
log: .conductor/evidence/PK3/pk3.1-live-proof.log - 23/23 checks.

The courier binary the rig drove is src/Conductor.Courier/bin/Debug/net10.0/conductor-courier.exe,
built 12:22:10Z from the tree committed as 0d43747 (its stamp reads 59cfe04.dirty because the build
ran before the commit; the only later change is the rig script's own scratch project line).

## Acceptance, item by item

- A scratch courier answers all four: part A - fresh conductor-courier on its own
  CONDUCTOR_STATE_HOME, own CONDUCTOR_COURIER_PORT, scratch token, stub Bot API. GET /hello protocol 3;
  POST /send text to a PROFILE -> id + resolved chat; media group of two photos -> two ids; one
  document -> id; POST /react and POST /delete accepted; GET /chats lists both chats with profiles.
- A protocol-2 push is accepted: POST /push with protocol 2 -> 200, accepted, and now its id.
- Refusals by name: protocol 4 -> 409 naming protocol 3 and the restart verb; no secret -> 401;
  4097 characters -> "over Telegram's 4096-character message ceiling"; unknown chat -> named.
- messages.jsonl: id, chat, origin, stamp, when (+ verb) for every send, the push and the delete.
- The ledger carries the id a REAL send returned: part B - a second scratch courier holding the
  environment token, its Bot API base a local relay that forwards sendMessage and deleteMessage only
  (getUpdates answered locally x2, none reached Telegram, so the real courier's poll was never
  contended). ONE message to the admin DM:
  - chat id 99205495 (admin DM), message id 3820, sent 2026-09-17T12:30:57Z
  - /delete of 3820 accepted (Telegram 200), ledger line verb delete
- Bug #76 (courier does not upload files): a pushed artifact now uploads (sendPhoto/sendDocument by
  TelegramLimits.MethodFor); DV4_3CourierSeamTests.An_artifact_is_uploaded_rather_than_named.

## Tests

PK3_1CourierProtocol3Tests (8): all four verbs end to end over HTTP with ids and ledger; protocol-2
push; the secret and the newer-protocol refusal as properties over every path CourierEndpoint names;
every ceiling (4096 text, 1024 caption, 10 files, 10 MB photo, photos+documents mix, unreadable file,
parse mode, empty) refused before any Bot API call, and 4096 exactly accepted; parse mode none; a
refused delete in the messenger's words with nothing ledgered; profile-to-chat resolution.
Scoped run (PK3_1, DV4_1/2/3/4, PK1_1, PK2_1/2, KS11_1, Architecture*, FuOwner11, K5_*): 280/280.
