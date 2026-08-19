# KS6.3 — complexity budgets that bind, and the config typo that voids them in silence

Session 13, 2026-08-19. Commits `094c5c3` (the budgets + the referee) and `71c1e64` (the fix its own
tests found, plus the fourteen seeded loosenings).

Artifacts: `.conductor/evidence/KS6/KS6.3-gate-run.log` (the gate against the working tree, and the
seeded-attack suite green), `tests/Conductor.Tests/KS6_3ComplexityBudgetTests.cs` (the same attacks made
permanent), `tools/gates/complexity-budget.ps1` (the referee), five `CodeMetricsConfig.txt` files.

---

## 1. The finding that decided the design

CA1502 (cyclomatic complexity per method), CA1505 (maintainability index) and CA1506 (class coupling)
ship **disabled** in `Microsoft.CodeAnalysis.NetAnalyzers` and read their thresholds from an
`AdditionalFiles` item named `CodeMetricsConfig.txt`. Measured on this repo, 2026-08-19:

| probe on `src/Conductor.Planning`, rebuilt each time | diagnostics |
|---|---|
| `CA1502: 8` | **6** |
| `CA1502: 8` + `CA1502(Method): 20` | 0 (the SymbolKind form parses and overrides — correct) |
| `CA1502: 8` + `CA1506: 20` + `CA1502(ClaimItems): 20` | **0** |
| `CA1502: 8` + `CA1506: 20` + `this is not a rule at all` | **0** |

**One line the analyzer cannot parse disables every code-metrics rule in that compilation, silently.**
Not just the rule on the bad line — CA1506 died with it. No `AD0001`. No `CA1509`, which is the
diagnostic that exists for exactly this case and was set to `error` in `.editorconfig` while the probe
ran (`grep -n CA1509 .editorconfig` → line 47; it never fired). A repo whose budgets are dead is
byte-for-byte indistinguishable, in build output, from a repo whose budgets are met.

Two consequences, both structural:

1. **Per-symbol exemptions are impossible.** The grammar is `RuleId: N` and `RuleId(SymbolKind): N`,
   nothing else. A named-offender debt list of the KS6.2 shape cannot be built on this analyzer — the
   obvious spelling of one is the exact input that voids the file.
2. **Any referee must prove the rules are LIVE, not configured.** This is the KS6.1 lesson one layer
   down: `roslynator_analyzers.enabled_by_default = false` read correct and was read by nobody.

## 2. What was measured, and what the budgets are

Whole-solution build with the thresholds wound down to 5 / 40 / 20 (`bg-logs/ks63probe-*.log`,
1342 warnings). Budgets are set to each project's **measured worst**, so every project sits exactly at
its own bar — the next branch added to the worst method is a build error.

| project | CA1502 (max cyclomatic) | CA1505 (min maintainability) | CA1506 (max coupling) |
|---|---|---|---|
| `src/Conductor.Planning` | **10** | **40** | **25** |
| `tools/plan-lint` | 39 | 18 | **42** |
| `src/Conductor` | 39 | **21** | 240 |
| `tests/Conductor.Tests` | 39 | **35** | 135 |
| `src/Conductor.Core` | 91 | 8 | 182 |
| *analyzer default* | *25* | *10* | *95* |

Bold = stricter than the analyzer's own default. 7 of the 15 numbers are; 8 are looser. That is the
honest count and it is why the acceptance I declared at the top of this session was amended on the card:
**a repo-wide budget at the analyzer defaults is not reachable from here.** At 25 / 10 / 95, `src` alone
has 23 methods over the cyclomatic ceiling, 9 types over the coupling ceiling and 1 method under the
maintainability floor — 33 build errors, with no exemption mechanism to park them behind (see §1) and no
pragma budget to spend (KS6.2 ratcheted that to 31 and it is at its floor).

What is *not* a compromise: **before this checkpoint all three rules were off entirely**, so every one of
the 15 numbers is strictly more enforcement than existed, none of them carries a single point of slack,
and the referee in §4 means they can only ever come down.

## 3. Proven live, not merely configured

`src/Conductor.Planning/CodeMetricsConfig.txt`, `CA1502: 10` → `CA1502: 9`, project rebuilt:

```
error CA1502: 'ClaimItems' has a cyclomatic complexity of '10'. Rewrite or refactor the code to
  decrease its complexity below '10'.  [src\Conductor.Planning\Conductor.Planning.csproj]
Build FAILED.
```

Restored to `10` → `Build succeeded.` The budget fails builds; it does not advise them. The same proof
is permanent as `TheBudgetIsLiveAndNotMerelyConfigured`, which copies this repo's real
`Directory.Build.props`, `Directory.Packages.props` and `.editorconfig` into a scratch project with a
budget of 1 and requires `error CA1502` from the compiler. It reads no configuration at all, because §1
is what reading configuration is worth here.

## 4. The referee — `tools/gates/complexity-budget.ps1`

Invoked from `ratchet.ps1` §6 next to `analyzer-debt.ps1`, so the plan's existing gate command picks it
up with no plan edit. Five refusals:

1. **A missing budget file** — the analyzer defaults are looser than every budget here, so deleting the
   file is the cheapest loosening there is.
2. **An unparseable line** — §1. Only `RuleId: N` and `RuleId(SymbolKind): N` pass.
3. **A rule with no budget** — a missing line is the analyzer default, quietly.
4. **A budget looser than the STRICTEST value over the last 25 commits that touched one.** Direction is
   per rule: CA1502 and CA1506 are ceilings (lower is stricter), CA1505 is a floor (higher is stricter).
5. **A rule un-enforced or unwired** — any severity but `error`/`warning` in any `.editorconfig` section,
   or `Directory.Build.props` no longer handing the files to the compiler as `AdditionalFiles`.

The bar is a window minimum, **never `origin/<branch>`**, and this gate goes further than
`analyzer-debt.ps1`: it does not read `CONDUCTOR_BASE_REF` and `ratchet.ps1` does not pass it `-Anchor`.
KS6.2 measured why — a session commits *and pushes* before conductor runs the battery, so at gate time
`origin/<branch>` **is** HEAD and a single-commit anchor compares the tree with itself.

## 5. The bug the seeded tests found in the referee

The first draft enumerated historical projects with `git ls-tree -r --name-only <ref> -- *.csproj`.

```
$ git ls-tree -r --name-only HEAD -- '*.csproj' | wc -l     ->  0
$ git ls-files -- '*.csproj' | wc -l                        ->  5
```

`ls-tree` matches a path **prefix**, not a glob, and reports the difference by exiting 0 with no output.
Every historical bar came back empty, so the gate printed `OK` on loosenings it had never compared
against anything — five of the fourteen tests were red for that one reason. The gate reads correctly
either way; only the seeded attack could tell the difference. That is the KS6.2 vacuous-gate shape a
second time, in a different git subcommand.

Fixed by listing the whole tree and filtering on the extension. The bars are now real — see
`KS6.3-gate-run.log`.

## 6. The fourteen seeded loosenings (all green, `KS6_3ComplexityBudgetTests`)

Clean tree passes · deleted budget file · unparseable line · per-SymbolKind budget hiding behind the
global one · missing rule · raised ceiling · **lowered maintainability floor** (CA1505 runs the other
way; a gate comparing all three alike waves this one through and refuses the tightening instead) ·
tightening every budget passes · dropped line · **the loosening COMMITTED first** · un-enforced rule ·
path-scoped `none` · unwired `AdditionalFiles` · the compiling canary.

## 7. What this hands KS6.4, measured

KS6.4 extracts the pure evidence→verdict function from `VerdictEngine`. The numbers say where that moves
a budget and where it does not:

| project | binding type (CA1506) | next one down |
|---|---|---|
| `src/Conductor` | **ControlPlaneServer 240** (12 partial files) | DoctorCommand **134** |
| `src/Conductor.Core` | RunLoop **182** | **VerdictEngine 144**, SessionRunner 138 |

- The stage named VerdictEngine at 8 files and ControlPlaneServer at 11. They are **9 and 12** now.
- **`VerdictEngine` is not the binding constraint on `Conductor.Core`** — `RunLoop` is, at 182 against
  VerdictEngine's 144. KS6.4 can halve VerdictEngine's coupling and the project budget will not move.
  Same for CA1502: the 91 is `RunLoop.RunAsync`; VerdictEngine's `EvaluateSessionAsync` is 70. Whoever
  writes KS6.4's exit criterion should not phrase it as "the budget drops", because it will not.
- **`ControlPlaneServer` is the binding constraint on `src/Conductor`, by a mile.** Splitting that one
  type takes CA1506 from 240 to 134 in a single move — a 44% tightening, the largest single budget
  reduction available anywhere in this repo. That is a named, measured target for whoever takes it.
