# ADR-0003 — Cross-platform gates, dotnet tool packaging, close-out battery

- **Status:** Accepted (B11.2/B11.3)
- **Date:** 2026-07-09
- **Deciders:** Baton self-plan, session #63 (stage B11)
- **Context source:** `docs/history/baton/stages/B11.md`; `docs/history/baton/BATON-BRIEF.md` §5.1 (gating philosophy), D-12 (.NET standards).

## Context

B11 is the close-out stage: the plan must deliver cross-platform gate support, real packaging
(`dotnet tool`), a readable "what will happen on resume" diagnostic, and a clean-clone battery that
proves zero hidden local state. These three decisions need explicit shape before the plan's acceptance
gate (B11.4 — Shamshir P2.2).

## Decision 1 — Cross-platform gate runner

**`gates[].shell` ∈ `powershell|bash|sh`, with OS-default auto-detect.**

- `ProcessRunner.RunShell(string shell, string command, …)` dispatches on shell name. `powershell`
  invokes `powershell.exe -NoProfile …` on Windows and `pwsh -NoProfile …` on Unix. `bash`/`sh` use
  `-c`. Unknown shells return exit code -1 with an error message (never throw).
- `GateConfig.Shell` is `string?`; `null` (the default) triggers auto-detect via
  `ProcessRunner.DefaultShell` (`powershell` on Windows, `bash` elsewhere).
- `GateRunner.RunOne` resolves the shell via `g.Shell ?? ProcessRunner.DefaultShell`, then delegates
  to `RunShell`.
- `RunPowerShell` (the legacy entry point) delegates to `RunShell("powershell", …)` — zero behaviour
  change for existing callers.
- Battery signature (`GateRunner.BatterySignature`) is unchanged by the `shell` property — two
  identical gates with different shells produce the same signature, preserving the skip-on-green-HEAD
  cache (B10.4 / `LastGreenGateSig`).

**Why not detect a missing shell at validation time?** Validation runs at load time in a local
context; the script host may or may not be present (bash isn't on vanilla Windows). Existence is
checked at run time by `ProcessRunner.Run` (returns exit -1 on launch failure), which is the same
path as every other process launch in Conductor.

## Decision 2 — dotnet tool packaging

**`PackAsTool=true` with `ToolCommandName=conductor`, `PackageId=conductor`, `Version=2.0.0`.**

- `dotnet pack` produces `conductor.2.0.0.nupkg`, installable via `dotnet tool install --global`.
- The stable driver (`C:\Code\conductor\bin\conductor.exe`, built from master) is **not** affected —
  `PackAsTool` only changes `dotnet pack` output, not `dotnet build`/`dotnet run`.
- Tab completion (`conductor completion powershell|bash`) generates scripts users source into their
  shell profiles. The output is a static string from built-in constants (no runtime introspection of
  undefined commands, which avoids Spectre.Console.Cli reflection issues).
- `conductor doctor` is a read-only diagnostic: it reads `plan.json` + `state.json`, then prints
  exactly what will happen on resume (pending fix/resume/gates/audit/owner-approval + remaining
  stages). It never writes state and never blocks.

**Why static completion, not runtime introspection?** Spectre.Console.Cli does not expose a
command-registry query API at runtime. Building a dynamic reflector would add fragility (private
internals) for no user benefit — the verb list changes at most once per release. The completion
scripts are self-contained and trivially updatable.

## Decision 3 — Clean-clone battery

**A fresh `git clone` must build and test identically — proof of zero hidden local state.**

- `dotnet build Conductor.slnx` succeeds with 0 warnings / 0 errors on `net10.0`.
- `dotnet test Conductor.slnx` passes all tests (432 at B11.2).
- No local SDK, toolchain, or machine-specific state is required beyond the .NET 10 SDK and the
  repository.
- The clean-clone battery is re-run at each phase gate as part of the `dotnet build` + `dotnet test`
  gating (the gates themselves run from the clone).

## Consequences

- Plans can mix PowerShell and bash gates — the self-plan's `powershell` default works unchanged;
  a Linux-originated plan can use `bash` gates natively.
- Conductor can be installed globally as a dotnet tool; `conductor doctor` gives users a one-liner
  to understand where the plan is and what comes next.
- The clean-clone battery proves there's no dependency on a writer's machine.

## Alternatives considered

- **Environment-variable shell selection.** Rejected: per-gate `shell` is explicit and battle-tested
  in CI systems (GitHub Actions, Buildkite); an env-var changes meaning silently when the same plan
  moves between OSes.
- **Dynamic command introspection for completion.** Rejected: Spectre.Console.Cli doesn't support it,
  and the maintenance cost of a private-reflection workaround exceeds the value of auto-syncing a
  rarely-changing verb list.
- **`conductor doctor --fix` (auto-repair).** Rejected: doctor is deliberately read-only — writing
  state from a diagnostic command would violate the resumability contract (only the orchestrator owns
  durable writes).
