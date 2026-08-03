# s10 / S1 — the two Shamshir violations fixed at the source, not waived

Session #10, fix attempt 3/4 on stage N1. N1 itself is complete and was re-verified by session #9;
the single line keeping `fleet-green` RED was `shaahink/Shamshir / Release`, which belongs to S1.
Sessions #8 and #9 both parked it as "two owner decisions". This session made the fixes instead.

## Why the owner-block was reversed

Neither item is a design decision. Both are the repo's **own declared invariants** being violated by
product code that predates this plan:

- `IAuditableEntity.cs` carries a source `TODO(iter-38 T1)`: *"retrofit ALL existing entities to
  implement this and add the two columns"*. Migrations `M48_ReferenceScaleAudit`,
  `M49_StrategyCellParkAudit` and `M50_VenueSessionWalkForwardAudit` are three prior instalments of
  exactly that retrofit. `VenueSymbolSpecEntity` was the last one outstanding.
- `EnginePurityTests` bans BCL time types on `TradingEngine.Engine`'s exported surface, and its own
  AF6 comment accepts time entering the Engine **through a Domain-owned contract**. A Domain value
  object is that contract.

The plan forbids *weakening* a test, not fixing product code to satisfy one. The tempting fake fixes
— excluding `tests/TradingEngine.Tests.Architecture` from the test run, or adding a new AF6-style
exemption to `EnginePurityTests` — stay refused. Nothing under `tests/` was touched.

## The fix — four product files, zero test or gate edits

Commit `403aced` on `fix/release-node-and-gh-release`:

1. **New** `src/TradingEngine.Domain/ValueObjects/SimTime.cs` —
   `public readonly record struct SimTime(DateTime Utc)`, the same idiom as `Price`, `Pips`, `Money`.
2. `EngineReducer.ReconcileToVenue`'s fourth parameter changes from `DateTime simTimeUtc` to
   `SimTime simTime`; the body reads `simTime.Utc`. Behaviour is identical. There is exactly **one**
   production call site (`KernelBacktestLoop.cs:191`, now `new SimTime(bar.BarOpenTimeUtc)`) and
   **zero** `.cs` call sites under `tests/` — the only other grep hits were compiled `bin/` dlls.
3. `VenueSymbolSpecEntity : IAuditableEntity` with `CreatedAtUtc` / `UpdatedAtUtc`.
4. `20260803162752_M56_VenueSymbolSpecAudit` — **generated**, not hand-written, with
   `dotnet ef migrations add --context TradingDbContext --project src/TradingEngine.Infrastructure
   --startup-project src/TradingEngine.Web --output-dir Persistence/Migrations`. Its `Up` is
   byte-for-byte the shape of `M50`'s, and `TradingDbContextModelSnapshot.cs` was regenerated with it.

Local proof before pushing:

    dotnet test tests/TradingEngine.Tests.Architecture -c Release --filter "RequiresCTrader!=true&..."
    Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8

## Bug #4 closed as well — PRs into the default branch had no checks

Commit `af9900c`. `pr.yml` fired only on `pull_request: branches: [develop]`, so a PR into `main` —
the default branch — got zero checks, which is precisely why the two architecture violations survived
so long and why the merge-on-green rule was unsatisfiable on PR 3. `branches` is now
`[develop, main]` and `paths` gains `.github/workflows/**`, since a workflow change can break CI as
surely as a source change and was the one kind of change that merged unchecked. This strengthens the
measurement; nothing was relaxed.

The effect was immediate: PR 3 picked up `build-and-test` and `lint` runs the moment the commit
landed (run 30832397417), where before `gh pr checks 3` reported none at all.

## Remote verification

RUNS_PLACEHOLDER
