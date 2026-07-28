# Finding — what stands between this repo and an open-source release

*Written 2026-07-28, before the W-series merge to `master`. Audit of the tree at `feat/foreman`.*

The question that prompted this: **does PowerShell close the door to Linux and macOS?** The repo is
.NET 10 and Go, CI compiles both on Ubuntu, and yet everything a person is told to run is a `.ps1`.

Short answer: **no — and the README is the thing doing the closing.** What follows is the evidence,
plus two problems found alongside it that matter more for a public release than the platform question
does.

---

## 1. The engine is portable. The tooling is not. The README says the opposite.

Every place the engine could have hard-coded Windows, it didn't:

| Seam | Where | Behaviour off-Windows |
|---|---|---|
| Default gate shell | `src/Conductor/Core/ProcessRunner.cs:11` | `bash`, not `powershell` |
| Explicit `"shell": "powershell"` gate | `src/Conductor/Core/ProcessRunner.cs:159-163` | runs `pwsh` |
| Console-close / Ctrl-C rails | `src/Conductor/Core/ConsoleCtrlRails.cs:58,114` | guarded by `OperatingSystem.IsWindows()` + `[SupportedOSPlatform]` |
| Process-tree kill | `src/Conductor/Core/JobObject.cs:20,37` | same guard; job object simply not created |
| Face binary name | `src/Conductor/Core/Face/FaceLauncher.cs:29` | `conductor-face`, not `conductor-face.exe` |
| Scaffolded gates | `src/Conductor/Core/RepoKindDetector.cs:31-39` | `dotnet build`, `npm test`, `go test ./...`, `cargo test`, `pytest -q` — and **no `shell` field**, so they resolve to the host default |

That last row is the important one. A developer on Ubuntu who runs `conductor init` in a Go repo gets
a correct, runnable plan **today**, with no Windows assumption anywhere in it.

Against that, `README.md:50` opens the Platform section with:

> **Windows is the supported host.**

and `CONTRIBUTING.md:8` repeats it. The sentence is *true for a contributor* — the gate battery is
PowerShell — and *wrong for a user*, who never runs the gate battery. It is also the first thing both
audiences read. A Linux or macOS developer evaluating this project bounces on that line before
reaching anything that would have worked for them.

### What is genuinely Windows-bound

Worth stating precisely, because it is a much shorter list than the README implies:

- **The repo's own tooling.** 14 `.ps1` files, 0 `.sh`: `install.ps1`, `fake-agent.ps1`,
  `w5/rehearsal.ps1`, `gates/ratchet.ps1`, `demo/make-demo-gif.ps1`. None of these are the product;
  all of them are the only documented way to touch it.
- **Two gate commands in Conductor's own plans** (`ratchet.ps1`, `driver.ps1`). Self-hosting only —
  they never appear in a plan a user writes.
- **`BgLogs.cs:80`** — reaches for `COMSPEC` / `cmd.exe`.
- **The window-close rail** (`tools/w3/window-close.ps1` and the `WM_CLOSE` → `CTRL_CLOSE_EVENT`
  path). Genuinely Win32, and genuinely valuable — but it is a *test of a rail*, not a feature a
  Linux user loses.

### The honest claim, and the gap in it

The engine has no *unguarded* Windows dependency in the run path. But "compiles on Ubuntu" is not
"runs on Ubuntu", and today there is **no runtime proof on Linux at all** — `ci.yml:14` deliberately
skips `dotnet test` on the ubuntu leg, for a good reason (the suite spawns real PowerShell gates and
`.exe` children, so a red there would mean nothing).

So the correct public statement is not "Linux is supported" and not "Windows only". It is:

> The engine and the Face run anywhere .NET 10 and Go run. The **contributor** gate battery is
> Windows-first. Process rails (graceful stop on window close, pid-identity checks) are Windows-only
> and degrade to plain process kill elsewhere.

And the gap should be closed by making the ubuntu CI leg prove something — see §4.

---

## 2. Two thirds of `docs/` is archaeology, and it is in the newcomer's path

155 tracked files under `docs/`, 1.5 MB. By subtree:

| Subtree | Files | What it is |
|---|---:|---|
| `baton/` | 98 | B-era briefs, per-stage notes, and raw `dotnet build` / `dotnet test` transcripts |
| `era3/` | 18 | more gate transcripts |
| `archive/` | 11 | superseded trackers |
| `workgraph/` | 4 | current-era write-ups |
| `qa-reports/`, `maestro/` | 6 | sweep output, M-era delivery notes |
| `workflows/`, `assets/`, `templates/` | 5 | live |
| root of `docs/` | 13 | the actual reference material — of which ~5 are user-facing |

The evidence directories are *correct to keep*. `docs/README.md:61-67` already defends them well:
they are receipts, not documentation, and their whole value is that a past "gates green" can be
audited rather than believed. The problem is not that they exist — it is that they sit one directory
below the front door, and `docs/README.md` presents eras as the organising principle, so a newcomer's
second click lands in a 2024 build transcript.

Alongside that, the repo root is the project's working area rather than a product face:

| File | Lines | What a first-time visitor makes of it |
|---|---:|---|
| `README.md` | 659 | pitch, then the complete plan-config schema, tracker format, runtime file layout, trust model, design decisions |
| `AGENTS.md` | 956 | agent instructions — necessary, but enormous and not for humans |
| `CONDUCTOR-WORKGRAPH.md` | 288 | a **live tracker**: checkpoint rows and a handoff block |
| `CONTRIBUTING.md` | 130 | good, and accurate apart from the platform line |

Roughly half of `README.md` is reference material that only matters once you have decided to use the
tool. It is in front of the decision instead of behind it.

---

## 3. Of the three ways in, only one exists

A person arriving at this repo wants one of three things. The repo serves the first and taxes the
other two heavily:

| Door | Today |
|---|---|
| **Watch it work** | The demo GIF, recorded live from `conductor-face --demo`. Works, and is good. |
| **Run it on my machine, no spend** | clone → install .NET 10 SDK → install Go 1.26 → `powershell -File tools/install.ps1` → `powershell -File tools/w5/rehearsal.ps1`. Windows-only, source-only. |
| **Install it and drive a real plan** | as above, plus an authenticated agent CLI. |

Two things stand out.

**There is no release.** `.github/workflows/ci.yml` is the only workflow in the repo. There is no
job that publishes a binary. Every single user — including one who only wants to look — must install
two toolchains and build from source before seeing anything move. That tax is independent of the
PowerShell question and is larger than it.

**The credential-free proof is a PowerShell script.** `tools/w5/rehearsal.ps1` is the best asset this
project has for a sceptical evaluator: ~90 seconds, 27 checks, one real binary driven from a markdown
document to a finished run, no API key. It is reachable only from Windows, and only after a source
build. The single highest-leverage change in this whole finding is to make that experience a verb on
the binary.

---

## 4. Decisions taken

Recorded here so the changes that follow have a stated rationale.

**Platform — make the user path portable, leave the dev path Windows.** Not full parity. The ratchet
and the rehearsal stay PowerShell; porting them would mean editing the referee, and the ratchet
explicitly forbids that (`tools/gates/ratchet.ps1:149-152`). Instead:

- ship release binaries for `win-x64`, `linux-x64`, `osx-arm64`;
- add `tools/install.sh` as the POSIX twin of `install.ps1`;
- add **`conductor demo`** — a cross-platform verb that does what `rehearsal.ps1` does, with no
  PowerShell, no credentials, and no toolchain beyond the binary itself;
- rewrite the platform claim in `README.md` and `CONTRIBUTING.md` to the honest statement in §1.

`conductor demo` also closes the proof gap: running it on the ubuntu CI leg turns "compiles on
Linux" into "completes a run on Linux", which is the claim the README actually wants to make.

**Docs — three audiences, three trees.**

```
docs/            user documentation only
docs/dev/        contributor material (this finding, testing, architecture)
docs/history/    every era: briefs, stage notes, evidence transcripts
```

History moves rather than gets deleted. It stays auditable, git history is untouched, and one index
page at `docs/history/README.md` states plainly that these are receipts and not documentation — so
nobody wanders in expecting a guide.

**Root README becomes a product page.** The plan-config schema moves to `docs/plan-config.md` and
the CLI table to `docs/cli.md`. What stays: what it is, the GIF, the three doors, the honest platform
table, and links.

---

## 5. What this finding does not change

- **The ratchet, and everything it guards.** `tools/gates/` is untouched; `plans/` gate commands are
  untouched; no test is deleted. The doc restructure moves only `docs/`, which the ratchet does not
  guard.
- **`AGENTS.md`.** It is large and it is at the root, and both are defensible — it is the file
  coding agents look for by convention. Left alone.
- **The evidence transcripts.** Verbose, full of absolute paths from the machine that produced them,
  and kept for exactly that reason.
- **`CONDUCTOR-WORKGRAPH.md` at the root.** It is a live tracker in a repo whose entire thesis is
  that it drives itself. Leaving it in place is the dogfooding proof; the restructured README frames
  it as such rather than leaving a visitor to guess.
