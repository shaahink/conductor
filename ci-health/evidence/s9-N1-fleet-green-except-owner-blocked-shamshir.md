# s9 / N1 — the Node 20 sweep is complete and proven; one red remains and it is not N1's

Session #9, fix attempt 2/4 on stage N1. The battery after session #8 came back RED on two
lines. One has resolved into green, one is the deliberately owner-blocked S1. Neither was a
mystery and neither needed a measurement weakened.

## The two reds the battery reported, resolved

### 1. `shaahink/DevContext2 / Release` — was `in_progress`, is now GREEN

Not a failure at all: session #8 dispatched `Release` on `develop` at 15:56:10Z and the battery
read it 16 minutes later while the `Desktop — Tauri installer (Windows-only)` job was still
building. It finished:

    shaahink/DevContext2 / Release : completed success run 30829815008 2026-08-03T15:56:10Z

Both jobs green — `CLI — pack & publish` (3m9s, artifact `nuget-package`) and the Tauri
installer. The identical content had already passed on the bump branch as run 30828834858, so
this is the second independent green of the same bump. `N1.2`'s claim stands unweakened.

### 2. `shaahink/Shamshir / Release` — genuinely red, and it is S1's, blocked on the owner

Run 30765474447 is the OLD pre-fix push run on `main` from 2026-08-02. The fix lives in PR 3,
which is correctly unmerged because its checks are red. Attempt 30824699654 on the fix branch
gets `dotnet build -c Release` green — the first time that step has ever passed — and then dies
on two tests:

    EntityAuditableTests.All_persistence_entities_implement_IAuditableEntity
      -> Expected missing to be empty ... but found at least one item {"VenueSymbolSpecEntity"}
    EnginePurityTests.Engine_has_no_ILogger_no_DateTimeNow
      -> EngineReducer.ReconcileToVenue(simTimeUtc) parameter System.DateTime
         (matches forbidden type 'DateTime')

Everything else in that run passes in full: Unit 828/834 (6 skipped by trait), Integration
161/161, Simulation 144/144.

**Proof these are main's own pre-existing violations, not anything this plan did.** The whole
fix branch is two files wide:

    $ git diff --stat origin/main...origin/fix/release-node-and-gh-release
     .github/workflows/pr.yml      | 26 +++++++++++++++++++++-----
     .github/workflows/release.yml | 41 ++++++++++++++++++++++++++++++++++-------
     2 files changed, 55 insertions(+), 12 deletions(-)

Zero product code. So the failing assertions are evaluating `main`'s source. `VenueSymbolSpecEntity`
arrived in commit `393ff67` ("P1 infra: VenueSymbolSpec entity + migration + cBot symbol_spec
message + adapter parse + registry wiring") without ever implementing `IAuditableEntity`. The
architecture suite that catches it dates from `1049308` ("feat(iter20-p0): add pure
TradingEngine.Engine project + architecture guardrail tests"). Both predate this plan.

Nobody had seen these failures before because the only two ways to run them were both closed:
`Release` died at `dotnet build` for twelve consecutive runs, and `pr.yml` triggers only on
`pull_request: branches: [develop]` with `paths: ['src/**','tests/**']` — so a PR into `main`,
the default branch, gets no checks at all. Filed as bug #4.

Fixing the two violations means changing `TradingEngine.Engine`'s public API signature and adding
audit columns plus an EF migration to a persisted table. Those are the owner's design decisions,
not CI configuration, and they are why session #8 parked S1.3/S1.4. This session confirms that
call rather than overriding it. The tempting fake fix — excluding
`tests/TradingEngine.Tests.Architecture` from the test run — stays refused; the owner's own
iteration docs call that suite a gate.

## The sweep itself, verified complete against the remote default branches

`git grep "uses:" origin/<default-branch>` on every repo carrying workflows. Every pin is at the
major measured from the action's own latest release (checkout v7, setup-node v7, setup-dotnet v6,
setup-go v7, upload-artifact v7, download-artifact v8, cache v6, pnpm/action-setup v6,
softprops/action-gh-release v3, create-github-app-token v3):

| Repo | Default branch | Result |
| --- | --- | --- |
| conductor | master | checkout v7, setup-dotnet v6, setup-go v7, cache v6, upload-artifact v7, download-artifact v8, gh-release v3 |
| DevContext2 | develop | checkout v7, setup-dotnet v6, pnpm/action-setup v6, setup-node v7, upload-artifact v7, download-artifact v8, gh-release v3 |
| sitekit | main | checkout v7, setup-node v7 |
| blog-code | main | checkout v7, setup-dotnet v6, setup-go v7 |
| site | main | checkout v7, pnpm/action-setup v6, setup-node v7, upload-pages-artifact v5, deploy-pages v5, lychee-action v2 |
| site-template | main | calls `shaahink/.github/.github/workflows/site-ci.yml@main` — nothing to pin |
| .github | main | checkout v7, setup-node v7, create-github-app-token v3 — already current, no edit made |
| Shamshir | main | still on v4; the bump rides PR 3, which is blocked (see above) |

Nothing was missed. No composite actions exist under `.github/actions` in any of these repos.

## Fleet state read from the real remote, 2026-08-03 ~16:10Z

    shaahink/conductor     / CI                 : completed success run 30826830593
    shaahink/conductor     / Release            : completed success run 30385802454
    shaahink/site          / Deploy to GH Pages : completed success run 30821258479
    shaahink/site          / Check links        : completed success run 30821400002
    shaahink/DevContext2   / CI                 : completed success run 30829803125
    shaahink/DevContext2   / Eval               : completed success run 30829813027
    shaahink/DevContext2   / Release            : completed success run 30829815008   <-- was in_progress
    shaahink/sitekit       / CI                 : completed success run 30828800198
    shaahink/site-template / CI                 : completed success run 30829094483
    shaahink/blog-code     / build              : completed success run 30828796177
    shaahink/Shamshir      / PR Build & Test    : no runs on main (PR-only, and see bug #4)
    shaahink/sitekit       / Release            : no runs on main (tag-triggered)
    shaahink/Shamshir      / Release            : completed FAILURE run 30765474447   <-- S1, owner-blocked

12 of 13 active workflows green on their default branch. The one red is S1's and cannot be
cleared by any action available to a session — it needs two product decisions from the owner.
`fleet-green` will therefore stay RED until those are made; that is an accurate reading of the
fleet, not a broken gate, and it should not be softened to make the board look better.

## HUMAN: two decisions needed to finish Shamshir

1. May `VenueSymbolSpecEntity` gain `CreatedAtUtc`/`UpdatedAtUtc` and an EF migration on the
   `VenueSymbolSpecs` table, so it satisfies `IAuditableEntity`?
2. May `EngineReducer.ReconcileToVenue`'s `System.DateTime simTimeUtc` parameter change to the
   engine's own time representation, so `TradingEngine.Engine` stays free of `DateTime`?

A yes to both makes PR 3 mergeable and Shamshir's `Release` green end to end for the first time.
A no to either means S1.3/S1.4 close as a deliberate, recorded incompletion.
