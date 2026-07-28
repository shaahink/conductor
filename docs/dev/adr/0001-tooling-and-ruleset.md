# ADR-0001 — Tooling & analyzer ruleset

- **Status:** Accepted (B0.2), maintained through B11
- **Date:** 2026-07-08 · **Last updated:** 2026-07-09 (B11.3)
- **Deciders:** Baton self-plan, session #1 (stage B0)
- **Context source:** `docs/history/baton/BATON-BRIEF.md` §5 (.NET standards) + §7 (anti-patterns, A17);
  `docs/history/baton/stages/B0.md` R0.2/R0.3; draft `docs/history/baton/tooling/meziantou-ruleset.md`.

## Context

Finding F-10: Conductor was `net9.0` with no `.editorconfig`, no `Directory.Build.props`, no
analyzers, no central packages, no warnings-as-errors. The code was clean but unguarded against
drift, while the Baton plan demands enforced SOLID + modern-C# + correct-async discipline across
every later stage. B0 must put the guardrails in place **without changing runtime behaviour**.

## Decision

1. **Runtime:** `net10.0` (SDK 10.0.301 present); `LangVersion=latest`, nullable + implicit usings,
   `InvariantGlobalization`, `Deterministic`. Centralised in root `Directory.Build.props` (B0.1).
2. **Central packages:** `Directory.Packages.props` with `ManagePackageVersionsCentrally=true`;
   projects reference packages without inline versions (B0.1).
3. **Solution:** the existing `Conductor.slnx` is the single build entry (verified, not recreated).
4. **Analyzers:** `Meziantou.Analyzer` (PrivateAssets=all) + built-in `Microsoft.CodeAnalysis`
   NetAnalyzers (`EnableNETAnalyzers`, `AnalysisLevel=latest`), `EnforceCodeStyleInBuild=true`.
5. **The build gate is the quality gate.** `TreatWarningsAsErrors=true` +
   `CodeAnalysisTreatWarningsAsErrors=true`, so `dotnet build Conductor.slnx` fails on any warning.
   No separate lint gate is needed (BATON-BRIEF §5.1).
6. **Curated, not the firehose.** `AnalysisMode=AllEnabledByDefault` is deliberately **not** used;
   the theme is correct async/threading. Severities live in `.editorconfig`.

## Rule severities & rationale

### Errors — must fix the code, never lower (A17)
| Rule | What | Why here |
|------|------|----------|
| MA0004 / CA2007 | `ConfigureAwait(false)` in library code | TUI thread must stay free; no context capture. (0 violations today — kept as error to prevent regression.) |
| MA0040 / MA0079 / CA2016 | Forward `CancellationToken` (incl. `IAsyncEnumerable`) | The whole run is cancellable (Ctrl+C/kill/timeout); a dropped token = an unkillable op. (0 today.) |
| MA0042 | No blocking `.Result`/`.Wait()`/sync `Run` (deadlock class) | Sync-over-async in the session loop is exactly how orchestrators deadlock. **Fixed 1 site** (`Program.cs` → `RunAsync`). |
| CA1849 | Call async methods when already in an async method | BCL twin of the above. (0 today.) |
| MA0074 | Culture-explicit string ops (`StartsWith`/`Contains`) | Determinism in production (`InvariantGlobalization` on). **Fixed 1 src site** (`AgentSession` → `StringComparison.Ordinal`). Relaxed in **test** scope only (asserts on ASCII literals). |

### Warnings (fix or track)
`MA0134` (observe Task result — **fixed** the `LiveDashboard` fire-and-forget with an error-surfacing
wrapper), `MA0015` (ArgumentException param name — **fixed** `PromptBuilder`), `CA2000` (dispose
before losing scope — **fixed** `Commands` CTS via `using`), `CA1063` (IDisposable pattern).

### Also fixed this checkpoint (modern-C#)
`MA0158` — `System.Threading.Lock` instead of `lock(object)` (BRIEF §5): 4 sites
(`AgentSession`, `GateRunner`, `ProcessRunner`, `LiveDashboard`).

## Deliberately relaxed to `suggestion` in B0.2 (ratchet later — recorded, never silent)

Per the B0 trap: a rule whose fix class exceeds the ~15-file diff budget is landed at `suggestion`
with an explicit ratchet followup, **not** silently disabled. These are tracked; B8 will mirror them
into `.conductor/followups.md`.

| Rule | Sites | Why deferred | Ratchet target |
|------|-------|--------------|----------------|
| MA0045 | 28 | Sync→async I/O rewrite is signature-changing across the Orchestrator; it *is* the B2 async/Host/DI rework. The deadlock-class twin (MA0042) stays **error**. | **B2** |
| MA0002 | 38 | Explicit `StringComparer` on dictionaries/sets/`Contains` is cross-cutting and mechanical; low correctness risk under `InvariantGlobalization`. | post-B2 |
| MA0009 | 7 | Regex ReDoS timeouts; the `TrackerParser` regex timeout was added in B1.4 (`ProgressConventions.RegexTimeout`). | **B1.4** → ✅ DONE (ratcheted to `error` in B1.4 per FU-B0-3) |
| MA0002 | 38 | Explicit `StringComparer` on dictionaries/sets/`Contains` — cross-cutting and mechanical; low correctness risk under `InvariantGlobalization`. | post-B2 (tracked followup) |
| MA0045 | 28 | Sync→async I/O rewrite — signature-changing across the Orchestrator; the B2 async/Host/DI rework landed preconditions but not the full surface rewrite. | post-B2 (tracked followup) |

Advisory suggestions (non-blocking, IDE-only): MA0006, MA0011, MA0016, MA0048, MA0051, CA1031,
CA1051, CA1861. `CA1848` (LoggerMessage) and `CA1303` (localize literals) set to `none` — the former
is applicable since structured logging landed (B2.5) but not yet mass-adopted; the latter is N/A for an
invariant-culture CLI.

## Ratchet status (B11.3)

- **MA0009**: `error` — regex timeouts enforced on all tracker regexes (B1.4).

## Consequences

- `dotnet build Conductor.slnx` is now the enforced quality bar: 0 warn / 0 err on net10, 56 tests
  green (evidence `docs/history/baton/evidence/B0.2-gate.txt`).
- New code in every later stage inherits these guardrails automatically.
- Three ratchet followups (MA0045, MA0002, MA0009) are owed and tracked above.
