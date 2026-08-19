# KS4.1 — Holdout gates: a gate class the agent cannot see, name, discover or run

Session 15, stage KS4, 2026-08-19. Branch `feat/karvansara-edge`.

Exit criterion from the plan (§6, KS4 table): *"`visibility: holdout` gate class — excluded from
prompts, tool contract, logs the agent reads; run only by the engine at verdict time. Grep of
composed prompt + transcript proves absence; a seeded gaming fake-agent passes visible gates, fails
holdout, verdict red."*

Both halves are delivered and both are asserted by tests that fail if the property breaks.

---

## 1. What the map said, and why it decided the design

Before writing anything I mapped every route by which a gate's **name** or **command** reaches the
coding agent. The map is what made the obvious design wrong. The routes:

| route | file:line | what leaks |
|---|---|---|
| the tools block, in **every** composed prompt | `src/Conductor.Core/ToolContract.cs:120-124` | a gate's **command**, verbatim, as the `conductor bg start` sample — chosen by searching gate commands for the word `test` |
| `.conductor/conductor.log` — inside the agent's own working tree | `src/Conductor.Core/GateRunner.cs` `RunOneAsync` | `gate <name>: <command>`, the exact command line that ran |
| the fix prompt's gate-failure block | `src/Conductor.Core/GateFailureSpill.cs:39-68` | name, exit code, duration, 700-char output excerpt |
| the spill file itself | `GateFailureSpill.cs:83-93` | the gate name **in the filename**, the untruncated output in the body |
| MCP `run_query` — arbitrary SQL over `run.db` | `McpTaskServer.Handlers.Queries.cs:12-43`, schema `SqliteRunStore.Sessions.cs:188-190` | the whole `gates` table: name, tier, exit_code, 2000-char tail |
| MCP `session_detail` | `McpTaskServer.Handlers.Queries.cs:194-203` | per-session gate name + tier + pass/skip |
| `conductor gate` | `src/Conductor/Commands/GateCommand.cs:36-56` | runs the battery and prints names + full tails |
| `conductor journey` | `src/Conductor/Commands/JourneyCommand.cs:150-157` | **name and command** for every gate, grouped by tier |
| `.conductor/REPORT.md` | `src/Conductor.Core/Reporter.cs:223-235` | all names + glyphs + failing tails |
| `state.json` / `GateSummary` | `SessionRecord.cs:24`, set at `VerdictEngine.Evaluate.cs:191` | names + glyphs per session |
| `RecentFailureBattery` prompt battery | `PromptBattery.Batteries.cs:39-40` | last red session's gate names into any session prompt |
| the plan-architect brief | `PlanImportService.cs:235` | name + command + tier |
| control-plane `PlanGateDto` (localhost HTTP) | `Http/Contracts/Plan/PlanDtos.cs:36` | name + command |
| `conductor doctor`'s gate lint | `src/Conductor/Commands/DoctorCommand.PlanSemantics.cs:45-90` | gate name, cwd, leading program, skipIfMissing, watchPaths |

**The design that follows.** Redacting thirty surfaces gives you thirty tests and one forgotten
surface — and the forgotten one is discovered by an agent, not by a reviewer. So the redaction
happens **where the result is produced**, not where it is shown:

`GateRunner` returns a `GateResult` for a holdout gate that never held the secret —
`Name = GateVisibility.RedactedName` (`"holdout"`, deliberately identical for every holdout so two
of them are indistinguishable), `Tail = GateVisibility.FailureNotice` or `PassNotice` instead of the
command's output, `ExitCode` normalised to 0/1 so a code cannot identify which check fired,
`Holdout = true`. And `RunOneAsync` cuts the progress sink at the top for a holdout
(`src/Conductor.Core/GateRunner.cs`, `var log = g.IsHoldout ? null : onProgress;`), so no log line
naming it is ever composed — including the one that printed the command verbatim.

Every renderer in the table above is then correct **without knowing holdouts exist**. Four surfaces
read `plan.Gates` directly and so bypass the runner; those go through `GateVisibility.VisibleOnly`
(`ToolContract`, `JourneyCommand`, `PlanImportService`) or redact explicitly (`PlanDtos`,
`DoctorCommand.CheckGatePaths`).

`doctor` is the interesting one: it is a verb the agent can run, and every message its gate lint
emits quotes a piece of the gate's configuration. Suppressing the holdout's diagnostics would have
been the easy answer and the wrong one — the owner must still learn that a holdout cannot resolve its
shell or its cwd. So the lint keeps running over holdout gates and reports under the redacted label
with every quoted value replaced by `(withheld)`.

## 2. The leak the runner cannot close — and the load-time refusal that does

A gate's command lives in the plan file, and the plan file normally lives **in the repo the agent is
editing**. An inline `"visibility": "holdout"` gate there is one `cat` away; the runner's redaction
would work perfectly and the class would still be worthless.

So `src/Conductor.Core/Models/HoldoutGateSource.cs` makes it a **load-time refusal, failing closed**.
`PlanConfig.Load` calls `HoldoutGateSource.Apply` before `Validate`:

- an inline holdout gate in a plan file that lives inside the repo → refused **by name**;
- `plan.holdoutGates` pointing at a file inside the repo working tree → refused **by name**;
- `plan.holdoutGates` pointing at a file that does not exist → **refused, not treated as empty**. A
  missing file silently reducing the battery to its visible gates and reporting green is exactly the
  vacuous-gate shape KS6.2 and KS6.3 each caught once already;
- a gate in the holdout file has `Visibility` **forced** to holdout regardless of what the file says,
  so a forgotten key cannot quietly produce a visible gate carrying a secret command.

Two further vocabulary refusals in `PlanConfig.CollectErrors`: an unknown `visibility` value is
refused by name (a typo must never project to "visible" in silence), and a *visible* gate may not be
named `holdout`, since that is the name every redacted result wears.

## 3. Engine-only, proven rather than asserted in prose

`GateRunner.RunAllAsync` gained `bool includeHoldout = false`. **The default is the checkpoint.**
Every route the agent can reach — `conductor gate`, the lane merge battery, every test helper —
takes the default and cannot run, time or observe a holdout.

`src/Conductor.Core/Orchestration/GateOrchestrator.cs` is the only caller that passes
`includeHoldout: true`, and `RunBatteryAsync` is reached from `VerdictEngine` alone (the per-session
battery at `VerdictEngine.Evaluate.cs:187`, the phase gate at `VerdictEngine.Phase.cs:53`, the
closing battery at `VerdictEngine.Completion.cs:25`). That *is* "run only by the engine at verdict
time", and `OnlyTheVerdictTimeBatteryOptsIntoHoldoutGates` scans `src/**/*.cs` and fails if a second
file ever opts in.

A holdout gate is also never served from the per-gate result cache: the cache keys on gate name, the
stored name is the shared redacted one, and a holdout that a cached pass can skip is one that can be
replayed.

## 4. The verdict still moves — nothing was weakened to buy the anonymity

`GateRunner.AllRequiredPassed` is what `SessionEvidence.GatesGreen` is computed from
(`VerdictEngine.Evaluate.cs:225`), and it counts the holdout like any other required gate. The
anonymity costs the measurement nothing.

## 5. The proofs

`tests/Conductor.Tests/KS4_1HoldoutGatesTests.cs` — 16 tests, unit level:

- `HoldoutGatesDoNotRunUnlessTheEngineAsksForThem` — same plan, two calls: the default returns only
  the visible gate; `includeHoldout: true` returns both.
- `OnlyTheVerdictTimeBatteryOptsIntoHoldoutGates` — source scan; exactly one file opts in.
- `AFailingHoldoutIsAnonymousInItsOwnResult` / `APassingHoldoutSaysNothingBeyondHavingRun` — the
  result carries the redacted name, the fixed notice, a normalised exit code; the command's own
  output (which is the assertion message) is gone.
- `TheRetryPreambleDoesNotPutTheExitCodeBack` — SC4.1's unconditional retry prepends the first
  attempt's exit code to the tail; for a holdout that would have put back exactly what was removed.
- `TheProgressLogNeverNamesAHoldoutOrItsCommand` — and asserts the **visible** gate is still logged
  as `gate visible: exit 0`, so this is not a blanket gag.
- `NoDownstreamRendererCanLeakWhatTheResultDoesNotCarry` — `Summary`, `Token`, `ConfirmationBasis`,
  `FailureDetails` and `GateFailureSpill.Render` over a battery whose holdout failed: no name, no
  command, spill filename and body clean — while the battery still reads `gates RED` and the session
  is still told a holdout failed.
- `TheDoctorGateLintNamesNoHoldoutAndQuotesNoneOfItsConfiguration` — a holdout with a broken cwd, a
  command whose program is not on PATH and a missing `skipIfMissing`: the lint still reports all
  three, under the redacted label, quoting none of them.
- `AFailedHoldoutStillTurnsTheBatteryRed`, `TheToolsBlockNeverSamplesAHoldoutCommand`, and the five
  load-time refusals.

`tests/Conductor.Tests/KS4_1GamingAgentHarnessTests.cs` — 3 tests, live through the real orchestrator
over a real temp repo, through a real `PlanConfig.Load` (so the location rule is exercised, not
bypassed by building a plan in memory):

- **`AGamingAgentPassesEveryVisibleGateAndTheHoldoutStillTurnsItRed`** — the checkpoint's headline.
  The seeded agent writes the deliverable the visible gate greps for, marks its checkpoint DONE,
  commits, and reports a clean SESSION-RESULT. `GateSummary` contains `visible-deliverable:OK`, one
  checkpoint newly done, commits present — and the session outcome is `SessionOutcome.GatesRed`,
  with `holdout:FAIL` in the summary. It is a gaming agent, not a broken one.
- **`TheSameRigGoesGreenForAnAgentThatActuallyDidTheWork`** — the falsifier. Same plan, same holdout;
  an agent that also does the work the holdout checks comes back not-red with `holdout:OK`. Without
  this leg, the test above would pass just as well against a gate that always fails.
- **`NeitherTheHoldoutsNameNorItsCommandSurvivesAnywhereTheAgentCanRead`** — the absence proof, taken
  after the run in which the holdout **ran and failed** (the case with the most to leak). It greps
  the exact composed prompt the engine wrote to `.conductor/logs/session-001.prompt.md`, then byte-
  scans **every file under the repo root** except `.git` — the transcript, `conductor.log`,
  `REPORT.md`, `state.json`, the gate-output spill and `run.db` all live inside `.conductor/`, inside
  the tree the agent edits. Bytes, not text, because `run.db` is sqlite and a leaked name is stored
  in it as plain UTF-8.
  Three controls keep the absence honest: the scanner finds `visible-deliverable` all over the same
  tree, and finds **both exact needles** in the holdout file itself, which lives outside the repo
  where it is allowed to.

## 6. Result

19 new tests, all green, and the full suite green with them:

```
dotnet test Conductor.slnx --no-build
Passed!  -  Failed: 0, Passed: 2960, Skipped: 0, Total: 2960
```

Logs: `.conductor/evidence/KS4/KS4.1-test-run.txt` (the KS4.1 filter),
`.conductor/evidence/KS4/KS4.1-full-suite.txt` (the whole suite).

No existing test was edited, no gate weakened, no expectation relaxed.

**Two ratchets fired at this change and both were right.** The architecture ratchet caught
`PlanConfig.cs` growing to 514 lines past its 500 ceiling — fixed by moving the gate rules to
`src/Conductor.Core/Models/GateRules.cs`, which is where they belonged (`PlanConfig.CollectErrors`
now has one line for them and both its callers get them for free), not by touching the baseline.
`SF7_1DocsMatchRealityTests` named the two new plan keys — `gates.visibility` and `holdoutGates` —
as undocumented, so `docs/plan-config.md` now carries the field row and a section explaining the
class, the file layout and all five load-time refusals.

## 7. A machine-level finding, filed as bug #57 — not caused by this change

`dotnet build Conductor.slnx` on this machine **flaps** between green and 100+ `MA00xx` analyzer
errors in files nobody touched. Cause, measured: **MSBuild node reuse**. A reused worker carries the
working directory spelled `C:\Code\conductor` while this shell's is `C:\code\conductor`; Roslyn
matches `.editorconfig` sections by **case-sensitive** path prefix, so when that node serves the
build every `dotnet_diagnostic.*.severity` override is silently dropped and rules the repo set to
`suggestion` (MA0006, MA0016, MA0051) fire at default severity — which `TreatWarningsAsErrors` turns
into build errors.

Evidence: three consecutive identical invocations, two RED with paths printed as `C:\Code\conductor`
and one GREEN with `C:\code\conductor`; `dotnet build Conductor.slnx -nr:false` is green every time.

This matters beyond a session's convenience: **a gate battery can go red for reasons that have
nothing to do with the tree**, and a second conductor run sharing this machine is a plausible source
of the differently-cased node. The workaround is `-nr:false` on every build.
