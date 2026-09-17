# PK6.3 — the telegram-notify skill rewritten around `conductor say` and `--tell`

Session 12, 2026-09-17. Spec: D20 (the Telegram half), plan A row PK6.2 of the findings doc (PK6.3 in the plan).

## What changed (outside the repo: `~/.claude/skills/`)

| Path | Act |
|---|---|
| `telegram-notify/SKILL.md` | Rewritten, 158 → 102 lines (two pages): the wire is `conductor say`; *what earns a message* (a table: card via `--tell`, finding via `say --to observer`, answer via `say --reply-to <note id>`, ack via `say --react`, asked-for post sent in the same turn); *how to write one* (UTF-8 file, `--dry-run` first, `--to` resolution, HTML default, `--photo`/`--document`, the ceilings `say` refuses by name, `rtl.py`); answering a note through `conductor inbox`; the `--tell` card; adding a room with `conductor room add/import/list` |
| `telegram-notify/send.ps1` | Deleted (sha256 `f15d7676…fe192`) |
| `telegram-notify/lib/config.ps1`, `lib/telegram-http.ps1` | Deleted with `lib/` (sha256 `7af9292b…9056`, `471da36c…9a83`) |
| `telegram-notify/walk-ids.ps1`, `report.ps1` | Already gone (PK5.1, PK4.3) |
| `telegram-notify/character.md` | Replaced by the in-tree canonical `docs/rooms/character.md` (the old copy still pointed at `<repo>/.claude/telegram/voice.md`, a path no room uses) |
| `telegram-notify/rtl.py` | Kept (no transport in it; D20 does not list it) |
| `watch-live/SKILL.md` | Re-pointed: the room from `conductor room show`; everything outgoing through `conductor say`; notes via `conductor inbox list`; `courier status` reads `alive / stale / dead`, the keep-alive trigger and the run's boundary restart named; `-ReplyTo`/delete/react now `say --reply-to`, `say --delete`, `say --react` |

Before deleting, every file was checked for callers: a grep of `~/.claude/skills`, `~/.claude/CLAUDE.md` and the plans of
conductor, bg, BookToCourse and pdf-challenge for `telegram-notify/send.ps1`, `telegram-notify/lib`, `walk-ids.ps1` and
`send.ps1 -To` found only this plan's own tracker, plan file and findings doc (prose). A backup of the deleted files
sits under the session temp dir (`telegram-notify-pre-pk6.3`), not committed.

The private room files `~/.claude/telegram/BookToCourse/{config.json,voice.md}` and `~/.claude/telegram/cv/{config.json,voice.md}`
are untouched, and the skill only points at them. Nothing from them is copied here.

Final folder: `SKILL.md` (sha256 `5504ccdb…b727c`), `character.md` (`dfbf926f…efc`, the same bytes as `docs/rooms/character.md`), `rtl.py` (`09802067…25f3`).

## Acceptance 1 — a grep of the skill folder finds no Bot API URL

```
$ cd ~/.claude/skills/telegram-notify && grep -rn "api\.telegram\.org\|https\?://[^ ]*telegram" . ; echo "url-grep exit $?"
url-grep exit 1 (1 = no match)
```

Before the change, the same folder held three `https://api.telegram.org/bot$Token/$Method` call sites
(`lib/telegram-http.ps1:57,93,114`). The only Telegram method name left anywhere in the folder is the prose word
`getUpdates` in SKILL.md:71, which explains why nobody else may poll.

## Acceptance 2 — one real post to the admin DM through `say`, from a shell outside any run, then deleted

Fresh build of this branch (`dotnet build Conductor.slnx`, 0 warnings, 0 errors), run as
`dotnet run --no-build --project src/Conductor -- say …` from `%TEMP%` with `CONDUCTOR_PLAN` and `CONDUCTOR_PID` removed
from the environment, so no plan and no run are in play. The PATH engine (0.5.1-alpha.0.43) has no `say` (`Unknown command 'say'`).

Dry run (`say --to admin --file body.txt --dry-run`, exit 2). The profile is refused by name and nothing is sent:

```
  chat:       admin -> not resolvable on this machine (this courier lists 2 admin chats; name the one you mean by id.); …
  path:       directly, with this environment's token - courier unreachable: the courier "Conductor Courier" (pid 3232) speaks protocol 2; this run speaks 3. …
  method:     sendMessage
  parse mode: HTML
  text:    139 characters, 139 UTF-8 bytes
```

The skill's `--to` bullet now says this: with two rooms, name the id from `conductor room show`.

Send and delete, to the admin DM `99205495`:

```
sent_utc: 2026-09-17T16:06:27.4244401Z
courier unreachable - sent directly: chat 99205495, message id(s) 3871 (the courier "Conductor Courier" (pid 3232) speaks protocol 2; this run speaks 3. …)
exit: 0
deleted_utc: 2026-09-17T16:06:33.8068456Z
courier unreachable - deleted directly: chat 99205495, message id 3871 (…)
exit: 0
```

**Message id 3871 was sent and then deleted.** The real courier speaks protocol 2 until the owner's reinstall, so D3's
direct path delivered, using the environment's token. Sending never takes the courier's one `getUpdates` slot. The ledger
(`%LOCALAPPDATA%\conductor\courier\messages.jsonl`) recorded both acts:

```
{"id":3871,"chat":"99205495","origin":"conductor say (sent directly)","when":"2026-09-17T16:06:28.7553711+00:00","verb":"send"}
{"id":3871,"chat":"99205495","origin":"conductor say (sent directly)","when":"2026-09-17T16:06:34.9870597+00:00","verb":"delete"}
```

Nothing was run against the real courier. It was not restarted, stopped or reinstalled, and its task was not touched.
`say` only read its presence (for the protocol) and its chat list (for `--to`).

## Open

- `conductor say` / `room` reach the skill's readers only at the owner's reinstall (the PATH engine predates them); the skill says so in its first paragraph.
- A by-product for PK6.1, recorded in the ledger: the real courier is now pid 3232, and PK2.3 armed pid 20052. The window read-out owns that.
