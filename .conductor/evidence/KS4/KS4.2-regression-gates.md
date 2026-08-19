# KS4.2 — the regression gate class (PASS-TO-PASS)

**Claim.** `class: "regression"` is a named gate class with SWE-bench PASS_TO_PASS semantics —
*nothing that worked broke* — with reporting distinct from an ordinary gate failure at every surface,
and a seeded regression flips a live verdict with the class named in the evidence.

Session 16, stage KS4, 2026-08-19. Commits `8d649ea` (the class) and the commit carrying this file.

---

## The gap it closes, stated as the failure it allows

A test command answers one question — *is anything failing right now* — and it answers "no" just as
happily when the failing test has been **deleted, renamed away, skipped, filtered out of the run, or
excluded from the project file**. Every one of those is a green battery over broken work, and none of
them requires writing a lie. This repo's own session rules name the deleted test first among the
forbidden moves, which is an admission that the rule had nothing mechanical behind it. It does now.

---

## A1 — semantics: a gate that exits 0 and is red anyway

A regression gate declares how to read the set of checks it reported **passing** out of its own run
(`passSet.format`: `trx`, `go-test`, `lines`). The engine keeps the last set per run and per gate as
the baseline; a name in the baseline that is not in the current set is a regression.

| Piece | Where |
|---|---|
| The class and its contract | `src/Conductor.Core/Models/GateClass.cs` |
| The three readers (positive reading only — names that PASSED) | `src/Conductor.Core/PassSetExtractor.cs` |
| Pass set read where the whole output still exists | `src/Conductor.Core/GateRunner.cs:436` |
| The set difference | `GateRunner.LostChecks`, `src/Conductor.Core/GateRunner.cs:242` |
| The decision, after the retries | `GateRunner.ApplyRegressionClass`, `src/Conductor.Core/GateRunner.cs:199` |
| **The one line that carries it to every consumer** | `GateResult.IsGreen`, `src/Conductor.Core/GateRunner.cs:48` |

`IsGreen => Skipped || Cached || Optional || (Passed && !HasRegressions)` — so the phase gate, the
lane merge battery and the session verdict treat a regression as red without knowing the class
exists. Same shape KS4.1 used to keep a holdout out of thirty rendering surfaces.

Computed **only for a gate that passed**: a compile error yields an empty pass set, and calling every
check in the baseline "regressed" because the build broke is noise that trains a reader to ignore the
class.

Measured, from the live rig's own `conductor.log`:

```
[15:11:52] gate suite: PASS in 0s
[15:11:52] gate suite: REGRESSION — the gate exited 0, but 1 check(s) that passed before no longer pass: Suite.TheKarvansaraInvariant
[15:11:52] verdict inputs: gates RED · commits 1 · newly DONE [H0.2] · dirty no
[15:11:52] session #2 GatesRed — queuing fix session (attempt 2/12)
```

## A2 — distinct reporting, at every surface a reader reaches

| Surface | Before | Now |
|---|---|---|
| Battery summary glyph | `suite:OK` | `suite:REGRESSION` (`GateRunner.cs:34`, `-warn` when optional) |
| Battery outcome line | `gate suite: PASS (0s)` | `gate suite: REGRESSION (0s) — exited 0, but 1 check(s)…` (`GateOrchestrator.cs:59`) |
| Outcome key for the renderers | `pass` | `fail` (`GateOrchestrator.cs:43`) |
| Fix brief (workflow path) | — | `GateRunner.FailureDetails` → `RegressionDetail`, `GateRunner.cs:532` |
| **Fix brief (ordinary run)** | `(no gate output captured)` | the same block — `GateFailureSpill.cs:46,55` |
| Verdict reason + log line | *(empty)* | `regression class (PASS-TO-PASS): gate 'suite' passed but 1 check(s)…` (`SessionVerdict.cs:232-233,240`) |
| Evidence taxonomy | — | `SessionEvidence.Regressions`, filled at `VerdictEngine.Evaluate.cs:228` |

**The finding that made this checkpoint worth more than its class.** The engine has TWO fix-brief
renderers and only one is on the path a real run takes: `GateRunner.FailureDetails` serves
`conductor gate` and the workflow/pipeline path, while an ordinary session's brief comes from
`GateFailureSpill.Render`. Both filtered on `!r.Passed && !r.Skipped` — and a regressing gate
*passed*. The unit test over `FailureDetails` was green while the live rig handed its fix session
literally `(no gate output captured)` for the one failure it most needed explained. Found by reading
the composed prompt off disk, not by reading source. Both renderers now share one block.

## A3 — the flip, live

`tests/Conductor.Tests/KS4_2RegressionHarnessTests.cs` — three real sessions of the real orchestrator
over a real temp repo, through a real `PlanConfig.Load`.

- **session 1** honest → `suite:OK`, baseline recorded.
- **session 2** deletes one check; its suite still exits 0; it claims H0.2 and commits — everything a
  delivery is made of — and comes back **`GatesRed`** with `suite:REGRESSION` in the record, and
  never `suite:FAIL`.
- **session 3** is handed a fix brief naming the class (`REGRESSION`, `PASS-TO-PASS`, `EXITED 0`) and
  the missing check by name — asserted against `.conductor/logs/session-003.prompt.md`, the prompt
  the process was actually given — restores the check, and the run is green again.
- **the falsifier**: the same rig where session 2 *adds* a check instead of deleting one runs green
  through all three sessions. So the red is the class's doing and nothing else's.

## A4 — no laundering

The baseline advances only when a regression gate passes **clean**
(`GateRunner.cs:199`, and the note in `Store/Migrations/v15_gate_pass_sets.sql`). Overwriting it every
battery would make deleting a check cost exactly one red session and then be invisible forever.
`ARegressingBatteryDoesNotAdvanceTheBaselineSoTheNextOneAsksAgain` runs the regressing battery three
times and asserts the same regression each time, then asserts the store still holds the larger set.

There is deliberately **no verb that resets a baseline**: every verb this engine has is one the
coding agent can call. A legitimate rename therefore reads as a regression; the documented answer is
`optional: true` while it lands, and the plan-config page says so.

Two more properties chosen against the obvious implementation:

- **Never cache-served** (`GateRunner.cs:103`). The result-cache key is HEAD-derived and a session's
  work is uncommitted for most of its length, so a cached pass would mean nobody looked at the tree
  where the check went missing. Asserted by filing a passing row under the exact cache key and
  showing the gate still ran and still regressed.
- **Red on an unreadable pass set** (`GateClass.EmptyPassSetNotice`). "The trx moved" and "everything
  passed" are the same exit code, and the first is the cheapest way to switch the class off from
  inside the repo.

## A5 — refused at plan load, not half-supported

`src/Conductor.Core/Models/GateRules.cs:35-54` — an unknown `class`; `regression` with no or an
unknown `passSet.format`; `trx` with no `path`; and **holdout + regression**, because a regression
names the checks that stopped passing and a holdout may not. Each refusal is asserted by its message.

---

## What was run

```
dotnet test tests/Conductor.Tests/Conductor.Tests.csproj -nr:false \
  --filter "…Gate|…Verdict|…SF7_1|…KS6_4|…K3_|…Store|…Migration|…PlanConfig|…Schema|…RunDb|…KS4_"
Passed!  - Failed: 0, Passed: 447, Skipped: 0, Total: 447, Duration: 54 s
```

New tests: `KS4_2RegressionGateTests` (19) and `KS4_2RegressionHarnessTests` (4). The trx reader was
written against trx this suite actually emitted — the fixture is verbatim, self-closing form and
attribute order included, because attribute order is not stable across SDKs.

Two schema pins moved with the migration, deliberately and out loud: `RunDbTests.Schema_version_is_fifteen`
and `K3_3ProvenanceTests` (v15 = `gate_pass_sets`). Docs: `docs/plan-config.md` gains the two field
rows and a section on the class; the docs-match-reality derivation is green on it.

## Two more findings, recorded because they cost time and will again

- **`VerifyEachDelivery` defaults to true** (`PlanConfig.cs:56`). A verify session takes a session
  slot, runs the same agent command and buys no gate battery — so in a multi-session harness rig
  `state.History[1]` is the *verifier*. KS4.1's rig never met this: it ran exactly one session.
- **A fake agent spawned through `cmd.exe` dies on a fix session.** The prompt is passed as an
  argument; `cmd.exe` caps a command line at 8191 characters and this rig's fix prompt is 8131 before
  the gate block. The session exits 1, the record says `AgentError`, and the only trace is
  `[stderr] The command line is too long.` in `session-NNN.jsonl`. Use `powershell.exe`
  (CreateProcess limit 32767) for any multi-session rig.
