# Meziantou.Analyzer ruleset — rationale (B0.2 / ADR-0001)

Source: https://github.com/meziantou/Meziantou.Analyzer

We adopt a **curated** subset, not the raw firehose. The theme for Conductor is **correct async and
threading** (it spawns processes, drains streams on background tasks, and must never deadlock or leak),
so async-correctness rules are errors; stylistic rules are suggestions. Severities live in
`.editorconfig`; this file is the "why" (folded into ADR-0001).

## Errors (must fix the code, never lower — A17)

| Rule | What | Why it matters here |
|------|------|---------------------|
| MA0004 | Use `ConfigureAwait(false)` | Library code (`Core/`) must not capture a context; the TUI thread must stay free. |
| MA0040 | Forward the `CancellationToken` | The whole run is cancellable (Ctrl+C, kill, timeouts); a dropped token = an unkillable op. |
| MA0042 / MA0045 | No blocking calls on async (`.Result`/`.Wait()`) | Sync-over-async in the session loop is exactly how orchestrators deadlock. |
| MA0079 | Forward token via `.WithCancellation` | Same as MA0040 for `IAsyncEnumerable` (the provider event streams, B2). |
| CA2007 / CA2016 / CA1849 | ConfigureAwait / token forwarding / async-in-async | The BCL twins of the above. |

## Warnings (fix or track as a followup)

| Rule | What | Note |
|------|------|------|
| MA0134 | Observe Task results | No floating fire-and-forget without intent; if intentional, `_ = ...` + comment. |
| MA0011 | Missing `IFormatProvider` | Determinism (InvariantGlobalization is on; make formatting explicit). |
| CA1031 | Catch general exception | Enforces "no silent catch {}" (BATON-BRIEF F-finding / A15). Some boundary catches are legit → justify + narrow. |
| CA2000 / CA1063 | Disposal correctness | Process/StreamWriter/JobObject lifetimes are real leak risks here. |

## Suggestions (advisory, non-blocking)

MA0006, MA0016, MA0051, CA1051, style rules — surfaced in the IDE, not build-breaking. Revisit as
ratchets in a later stage if the codebase warrants.

## Deliberately relaxed at first (ratchet later, recorded here)

- **CS1591** (missing XML doc): `warning`, in `NoWarn`, because `GenerateDocumentationFile=true` would
  otherwise flood B0. Ratchet to error only once public surfaces are documented (a B11 close-out task).
- Any rule that would require a large cross-cutting change bigger than the ~15-file diff budget is
  landed at `suggestion` in B0.2 with a followup to ratchet it — **recorded explicitly in ADR-0001**,
  never silently disabled.

## Process

1. Promote the drafts (`.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`).
2. `dotnet build Conductor.slnx` → triage the diagnostics.
3. Fix by theme (async, disposal, sealed/readonly), each commit within budget, tests green.
4. Land warnings-as-errors green; write ADR-0001 listing every rule set below `error` and why.
