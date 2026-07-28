# Documentation

Everything here is about **using** Conductor. Two other trees exist and are deliberately kept out of
your way:

- [`dev/`](dev/) — contributor material: the current design brief, ADRs, findings, backlog.
- [`history/`](history/) — closed eras and their raw gate transcripts. Receipts, not documentation.

## Start here

1. **[quickstart.md](quickstart.md)** — plan → tracker → dry run → first supervised session.
2. **[cli.md](cli.md)** — the verbs, what each is for, and how to override the defaults.
3. **[operating.md](operating.md)** — every control verb and when to reach for it. Written for an
   AI agent driving conductor, and just as usable by a human.
4. **[troubleshooting.md](troubleshooting.md)** — read this first when a run looks stuck, dead, or
   wrong.

## Reference

| Doc | What it is |
|---|---|
| [plan-config.md](plan-config.md) | The complete plan JSON schema. You need very little of it — `conductor init` writes a working plan. |
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
