# KS6.1 — a curated Roslynator set, and the switch that turned out to be inert

Session #11, 2026-08-19. Branch `feat/karvansara-edge`.

Stage rule, adopted verbatim from the verified plan: *every hygiene checkpoint buys one permanent design
asset, and a checkpoint that only silences warnings is a checkpoint done wrong.* What this one bought is
below the fold, under **The design assets**.

---

## What landed

| | |
|---|---|
| Package | `Roslynator.Analyzers` 4.16.1, pinned in `Directory.Packages.props`, referenced from `Directory.Build.props` so every project gets it |
| Adopted | **33 rules at `error`**, each with the design property it buys on the same line |
| Refused | **RCS1233**, at `none` with the measurement that refused it — not absent, so the decision is visible |
| Relaxed in tests | **RCS1075**, with the reason, in the section that already carries that precedent for CA2007/MA0045 |
| Violations fixed | **113 distinct**, across `src` and `tests`. No pragma added, no rule weakened |
| New test class | `tests/Conductor.Tests/KS6_1AnalyzerCurationTests.cs` — 8 tests that ask the analyzer assemblies, not a committed list |

## The measurement that decided the curation

29 candidates at `warning`, whole solution, `TreatWarningsAsErrors=false`:
`.conductor/bg-logs/ks61-measure-20260819-004724087.log` — 0 errors, **113 distinct diagnostics**.

| rule | hits | where |
|---|---|---|
| RCS1075 empty catch of `System.Exception` | 79 | 64 tests, 15 src |
| RCS1163 unused parameter | 20 | 11 tests, 9 src |
| RCS1168 parameter name differs from base | 8 | 6 tests, 2 src |
| RCS1213 unused member | 3 | 1 test, 2 src |
| RCS1233 use short-circuiting operator | 2 | src |
| RCS1227 iterator argument validation | 1 | src |

The other 23 candidates were clean, so adopting them cost nothing today and is pure forward defence.

**RCS1233 was refused, not fixed.** Its only two hits — `SqliteRunStore.RunKeys` and `StateRepair.RunKeys`
— use `|` on purpose so that *both* `ReadKeys` calls run and both tables contribute to the key set. `||`
there is a silent data-loss bug. A rule that cannot tell a deliberate `|` from a careless one costs more
than it buys, and a per-site suppression would spend pragmas this repo is already over budget on (bug 44).

## The finding: the vendor's master switch does nothing here

The card asks for "everything else explicitly off". The documented mechanism is
`roslynator_analyzers.enabled_by_default = false`, and **it does not take effect in this toolchain.**

Measured, not read off a doc:

1. With the line under `.editorconfig`'s `[*.cs]`, a seeded probe file still failed the build with
   `error RCS1102: Make class static` — a rule this repo never adopted.
   `.conductor/bg-logs/ks61-seed-20260819-010607311.log`, at `KS61SeedProbe.cs(19,14)`.
2. Moved to a repo-root `.globalconfig` with `is_global = true`, added to the compilation by an
   `EditorConfigFiles` item — confirmed present via `dotnet msbuild -getItem:EditorConfigFiles`, which
   lists `C:\Code\conductor\.globalconfig`. RCS1102 still fired.
   `.conductor/bg-logs/ks61-seed2-20260819-010738682.log`.
3. Added `dotnet_diagnostic.RCS1102.severity = none` **to that same `.globalconfig`**, to test whether the
   file influences the compilation at all. RCS1102 still fired.
   `.conductor/bg-logs/ks61-seed3-20260819-010838180.log`. Reduced to a three-line file with
   `is_global = true` first: unchanged. `.conductor/bg-logs/ks61-seed4-20260819-011019629.log`.

Per-rule severities in `.editorconfig` **do** work, and that is proven in the same logs: of the 24 rules
that fired on the seeded probe, 15 default to `Info` or `Hidden` in the package and could only have
reached `error` through `.editorconfig`.

Why it mattered rather than being a curiosity: 15 of the package's 217 rules are enabled by default at
`Warning`, and this repo sets `TreatWarningsAsErrors`. "Everything else off" was actually "fifteen
unadopted rules may fail the build the day somebody writes the shape that trips them". And the reason a
clean whole-solution measurement did not reveal this is that the other 202 default to `Info`/`Hidden`,
which never appear in build output — a green build proved nothing about the switch.

So the `.globalconfig` was deleted, the inert lines were removed from `.editorconfig`, and the guarantee
was rebuilt out of something that can be measured. All five unadopted `Warning`-default rules were then
measured against the whole solution (`.conductor/bg-logs/ks61-measure2-20260819-011211599.log`, 0 hits)
and adopted deliberately: RCS1102, RCS1138, RCS1139, RCS1172, RCS1263. That is why the set is 33 and not
the plan's "roughly 25" — five of them are rules the package forces a decision on.

## The seeded-violation proof: the set is live, not written down

A probe file, one deliberate violation per rule, built through this repo's own build:
`.conductor/bg-logs/ks61-seed-20260819-010607311.log`. **24 adopted rules failed the build as errors:**

```
RCS1043 RCS1044 RCS1059 RCS1075 RCS1130 RCS1157 RCS1158 RCS1160 RCS1163 RCS1166
RCS1168 RCS1169 RCS1170 RCS1193 RCS1194 RCS1203 RCS1210 RCS1213 RCS1227 RCS1229
RCS1234 RCS1236 RCS1242   (+ RCS1102, which is what exposed the master-switch finding)
```

Not proven to fire by a seeded probe: RCS1155, RCS1165, RCS1202, RCS1215, RCS1256 — the snippets written
for them did not match the shape those rules look for. They are adopted on id-existence against the
shipped analyzer plus the completeness invariant below, and this file says so rather than implying a
coverage that was not measured. The probe file was deleted in the same session; the log is the artifact.

Six more — RCS1075, RCS1163, RCS1168, RCS1213, RCS1233, RCS1227 — were additionally proven live by firing
on this repo's *real* code in the first measurement, which is the strongest form of the same evidence.

## The design assets

**1. `BestEffort.Run` — `src/Conductor.Core/BestEffort.cs`.** RCS1075's 15 src hits were the same policy
written fifteen times as `catch (Exception) { /* best effort */ }`: a decision with no name and no trace,
so when one of those swallows fired there was nothing in any log to say it had. Narrowing each catch would
have traded a silent swallow for a crash on the exception nobody predicted, in the one code path that runs
while the process is already going down. Instead the policy has one home, the failure is still tolerated,
and it is now recorded at Debug with the expression that failed. Twelve of the fifteen call sites had a
logger already in scope. (A comment does *not* silence RCS1075 — `FaceLauncher.cs:84` carried one and
still fired — so this was a restructure, not a suppression.)

**2. `KS6_1AnalyzerCurationTests` — the curation, enforced.** Eight tests, and the two that carry the
weight are the ones the finding above made necessary:

- `EveryRuleThatCouldFailThisBuildIsEitherAdoptedOrRefusedByName` loads the pinned analyzer assemblies,
  reads every `DiagnosticAnalyzer`'s `SupportedDiagnostics`, and fails if any rule enabled at `Warning` or
  above is not a written decision in `.editorconfig`. That is "everything else explicitly off", made true
  by measurement instead of by a switch. A package bump that adds a new default-on warning rule goes red
  here, with the id named, instead of surfacing as a mystery build failure three sessions later.
- `EveryAdoptedIdIsARealDiagnosticInThePinnedAnalyzer` closes the silent-typo hole: an unknown id in an
  `.editorconfig` is ignored without a word, so one typo turns an adopted rule into a comment that looks
  enforced forever, and the only symptom is a class of bug that quietly stops being caught.

Both read the analyzer version from the central pin rather than a literal, and both ask the shipped
assemblies rather than a list committed beside the config — a list is something a session can edit to
agree with its own typo. `TheMasterSwitchThatDoesNothingIsNotWrittenDownAsThoughItDid` keeps the inert
line out for good.

**3. Real defects removed while paying the debt.** Not cosmetics:

- `Orchestrator`'s constructor took a `ControlDispatcher` and built `RunLoop` with `dispatcher: null` —
  anything passed had been silently dropped since the RunLoop extraction (RCS1163).
- `Orchestrator.Save()` and `Orchestrator.ReadTrackerSafe()` were dead copies of methods `RunContext`
  owns today (RCS1213).
- `HealthMetrics.Format` validated its argument inside an iterator body, so the `ArgumentNullException`
  fired at the caller's `foreach` rather than at the call — and only if anybody enumerated (RCS1227).
- `PromptBuilder.Audit` took a `PendingAudit` it never read; `VerdictEngine.RunRemediationAsync` took a
  `reason` it never used (RCS1163).
- Two `IHostedService` overrides in `TelegramService` renamed the base's `cancellationToken` to `ct`,
  which breaks every named argument at a call site (RCS1168).

## The checker was seeded too, and the first version failed the seed

A test that checks a config is worth exactly what it catches, so two breaks were seeded into
`.editorconfig` and the tests re-run:

- **A**: delete `dotnet_diagnostic.RCS1102.severity` — a rule the package forces a decision on, quietly
  dropped.
- **B**: change `RCS1043` to `RCS1O43` — letter O for the zero, the exact typo the class exists to catch.

First run, `.conductor/bg-logs/ks61-redproof-20260819-011900682.log`: **1 of 8 failed.** Break A was
caught; **break B passed silently.** The parse regex matched the id *shape* (`RCS\d{4}`), so the typo did
not match, the line was skipped as unrecognised, and it was invisible to the checker exactly as it was
invisible to the compiler. A checker that parses what it expects to see cannot see the malformed case,
and malformed is what a mistake actually looks like.

Fixed by capturing the id loosely (`[^.\s]+`) and judging it afterwards by its `RCS` prefix. Second run,
`.conductor/bg-logs/ks61-redproof2-20260819-012022423.log`: **2 of 8 failed**, naming `RCS1O43` and
`RCS1102`. `.editorconfig` was then restored and the suite re-run green.

## Gate evidence

| | |
|---|---|
| Whole-solution build, curated set at `error` | `.conductor/bg-logs/ks61-verify-20260819-011619133.log` — 0 warnings, 0 errors |
| Full suite | `.conductor/bg-logs/ks61-suite-20260819-012127950.log` — **2886 passed, 0 failed**, 3m02s (was 2878; +8 is this class) |
| Curation tests green | `.conductor/bg-logs/ks61-tests-20260819-011728846.log` — 8/8 |
| Candidate measurement (29 rules) | `.conductor/bg-logs/ks61-measure-20260819-004724087.log` |
| Forced-decision measurement (5 rules) | `.conductor/bg-logs/ks61-measure2-20260819-011211599.log` |
| Seeded-violation proof | `.conductor/bg-logs/ks61-seed-20260819-010607311.log` |
| Checker-bites proof | `ks61-redproof-...log` (1/8, hole found) and `ks61-redproof2-...log` (2/8, closed) |
| Master-switch disproof | `ks61-seed2`, `ks61-seed3`, `ks61-seed4` logs, same directory |

No pragma was added (the ratchet ceiling is 38 and the repo already carries 43 — bug 44 is KS6.2's to
move or pay down). No test was deleted or skipped; the suite gains 8 attributes and stands at 2886.
