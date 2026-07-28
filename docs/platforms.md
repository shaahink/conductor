# Platforms

**Short version:** the engine and the Face run anywhere .NET 10 and Go run. The *contributor* gate
battery is Windows-first. Nothing about running Conductor on Linux or macOS requires PowerShell.

That distinction matters because the two audiences hit different surfaces, and only one of them is
Windows-bound.

## What runs where

| Surface | Windows | Linux | macOS |
|---|---|---|---|
| Engine + CLI (`conductor`) | yes | yes | yes |
| Face / dashboard (`conductor-face`) | yes | yes | yes |
| `conductor demo` (credential-free proof) | yes | yes | yes |
| Gates from `conductor init` (`dotnet`, `npm`, `go`, `cargo`, `pytest`) | yes | yes | yes |
| Gates with explicit `"shell": "powershell"` | yes | needs `pwsh` | needs `pwsh` |
| Graceful stop on window close, pid-identity checks | yes | — | — |
| Process-tree kill via Job Objects | yes | best-effort | best-effort |
| This repo's own tooling (`install.ps1`, `ratchet.ps1`, `rehearsal.ps1`) | yes | — | — |

## The gate shell

A gate's `shell` field defaults to **the host's shell** — `powershell` on Windows, `bash` everywhere
else. `conductor init` deliberately writes gates with **no** `shell` field:

```json
{ "name": "build", "command": "dotnet build", "tier": "fast", "timeoutMinutes": 10 },
{ "name": "tests", "command": "dotnet test",  "tier": "full", "timeoutMinutes": 20 }
```

so a scaffolded plan is portable as written. If you *do* set `"shell": "powershell"` explicitly, it
runs `pwsh` on Linux and macOS — install PowerShell Core there, or use `bash`.

## Process rails

Two safety rails are Windows-only, and they are the reason this repo's CI runs its full battery on
`windows-latest`:

- **Graceful stop on window close.** Closing the console window delivers `CTRL_CLOSE_EVENT`, which
  Conductor catches to save state and queue a resume rather than leaving a run half-written. On
  Linux/macOS, `SIGTERM`/`SIGHUP` handling covers the common cases, but the window-close path has no
  equivalent.
- **Pid-identity checks and Job Object process trees.** These stop Conductor killing a recycled pid
  and guarantee a hung agent dies as a tree. Elsewhere the kill is best-effort.

Neither is required for a run to complete. They are what makes a run survive *unattended* on a
desktop that might get closed, and their absence degrades gracefully.

## What is not yet proven

`dotnet test` does not run on the Ubuntu CI leg, on purpose: the suite spawns real PowerShell gates
and `.exe` children, so a red there would mean "Linux is not a supported build host for the test
suite", which is already true and not interesting.

What *is* proven on every push is that the code compiles on Ubuntu and the Face's own suite passes
there. `conductor demo` is the runtime proof — it drives a complete run with no credentials, and it
is the right first thing to try on a non-Windows host:

```bash
conductor demo
```

If it completes, the loop works on your machine. If it doesn't, that output is exactly what an issue
should contain.

## Contributing from Linux or macOS

You can build and you can run. You cannot run the full gate battery, because `tools/gates/ratchet.ps1`
is PowerShell and the ratchet forbids editing itself. Practical approach:

```bash
dotnet build Conductor.slnx
dotnet test  Conductor.slnx     # expect failures in tests that spawn PowerShell gates
cd face-go && go build ./... && go vet ./... && go test ./...
```

then open the PR and let CI's `windows-latest` leg run the battery that decides. See
[`CONTRIBUTING.md`](../CONTRIBUTING.md).
