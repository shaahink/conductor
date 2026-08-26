# Documentation

Everything here is about **using** Conductor. Two other trees exist and are deliberately kept out of
your way:

- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) — how the engine is put together: the assemblies, one
  session's lifecycle, the seams, the surfaces, and the courier — the one process that outlives a
  run. Read it before changing anything.
- [`dev/`](dev/) — contributor material: ADRs, findings, backlog, and the pointer to the era in flight.
- [`history/`](history/) — closed eras and their raw gate transcripts. Receipts, not documentation.
  Each closed era's design brief sits directly in `history/`; the tracker it was driven against sits in
  [`history/archive/trackers/`](history/archive/trackers/) with an index.

## Start here

1. **[quickstart.md](quickstart.md)** — plan → tracker → dry run → first supervised session.
2. **[cli.md](cli.md)** — the verbs, what each is for, and how to override the defaults.
3. **[operating.md](operating.md)** — every control verb and when to reach for it. Written for an
   AI agent driving conductor, and just as usable by a human. It also carries the two things you set
   up once and forget: **a group chat with an observer profile** (someone watches the run, nobody
   else can move it), **the courier** (`conductor courier install` — the one process that keeps
   listening when no run is live, so a note sent at 23:00 still lands), and the read-only ways data
   leaves a run — `history export --atif`, `otel`, `mcp-observe`.
4. **[troubleshooting.md](troubleshooting.md)** — read this first when a run looks stuck, dead, or
   wrong.

## Reference

| Doc | What it is |
|---|---|
| [plan-config.md](plan-config.md) | The complete plan JSON schema. You need very little of it — `conductor init` writes a working plan. Read it when you want a gate that cannot be gamed (`class: "regression"`, `class: "mutation"`, `holdoutGates`), a Telegram chat that can watch but not steer (`telegram.chats`), voice notes turned into text by a command you choose (`courier.transcribe`), or the opt-in cloud review lane (`cloud`, off unless you say so). |
| [tracker.md](tracker.md) | Tracker format (handoff block, checkpoint table, QA-previous) and the `.conductor/` runtime files. |
| [platforms.md](platforms.md) | What runs on Windows, Linux and macOS — and the two rails that are Windows-only. |
| [`../face-go/STYLE.md`](../face-go/STYLE.md) | The dashboard's live keybinding + layout reference. Kept current there, not duplicated. |
| [`../examples/`](../examples/) | Ready-to-run plans that are not part of the engine. |

## Not sure you want this yet?

```
conductor demo
```

One command, no credentials, no spend: it drives a complete plan end to end against a built-in fake
agent in a throwaway directory, and prints what happened. If you have not installed anything, the
demo GIF in the [main README](../README.md) shows the same thing without the download.
