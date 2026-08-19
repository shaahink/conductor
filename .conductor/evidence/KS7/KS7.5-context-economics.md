# KS7.5 — context economics (B7): what was built, what it costs, and what the exit really needs

Session #9, stage KS7, 2026-08-19. Every number below was measured with the build in this commit,
never read off a doc comment.

## 0. The card's exit, restated honestly

The checkpoint asks for a **measured drop in cache-read tokens per session versus the karvan baseline
on a comparable stage, reported by `conductor budget`**. That exit is not producible by the session
that builds the mechanisms, and this one did not fake it:

- the amendment on the card (2026-08-18, from the session before this one) measured a whole composed
  prompt at 17.7k–26.3k chars — 4.4k–6.6k tokens — against a 135k–195k mean turn. The prompt is
  **3–4% of a turn**; the gate block inside it is 0.2–0.6%.
- cache-read tokens are what accumulates DURING a session (files read, tool output, the growing
  transcript re-sent every turn), not what the prompt seeds it with.
- so a per-session drop can only be observed across **N future sessions running under these
  mechanisms**, compared with the karvan baseline for a comparable stage. One session cannot produce
  the reading, and a number produced by the session that wrote the code would measure nothing.

What this session owed, and delivered: the four named mechanisms, on the shipped seam, with the
accounting that says which of them can matter and why.

## 1. The mechanisms

| # | Mechanism | Where | State |
|---|-----------|-------|-------|
| 1 | Gate output truncated in-prompt, full text spilled to an evidence file | `GateFailureSpill`, wired at all three `VerdictEngine` sites | landed in `ba39a6d` (previous session) |
| 2 | `RepoMapBattery` on the shipped `IPromptBattery` seam | `src/Conductor.Core/PromptBattery.Context.cs` | **registered** in `PromptBuilder.BatterySection`, flag `batteries.repoMap` |
| 3 | Definition-of-done recap battery | same file | **registered**, flag `batteries.definitionOfDone` |
| 4 | Templates teach search-delegation to a subagent | `ToolContract.Render` — reaches session/fix/resume/verify/audit through `{tools}` | landed here |

Until this commit, 2 and 3 were classes nothing constructed: written, compiling, tested by nobody,
reaching no prompt. `PromptBuilder.BatterySection` now takes the folded board and the effective stage
(`SessionComposer.cs`, the `BatterySection(state, store, graph?.Checkpoints(), stage.Id)` call), which
is what lets the recap name the card the session is actually holding instead of a placeholder id.

### The recap does NOT restate the acceptance, on purpose

The first cut of `DefinitionOfDoneBattery` restated the card's context. `PromptBlockRenderer` already
renders that context **verbatim** into the "Work items in scope" section further down the same prompt.
On the card that funded this checkpoint the duplicate was ~300 bytes of a 500-byte battery — a
prompt-side mechanism whose whole content is bytes the prompt already carries. It is now a title
anchor plus the pre-filled claim command, and a test pins the invariant:

`KS7_5ContextEconomicsTests.DefinitionOfDoneDoesNotDuplicateTheAcceptanceTheWorkItemsSectionAlreadyCarries`
composes both sections and asserts the acceptance string appears **exactly once**.

## 2. The measurement that reshaped the checkpoint: a prompt is also an ARGUMENT

The composed prompt travels to the agent in `argv` (`agent.args` carries `{prompt}`;
`AgentSession.ResolveArgs` substitutes it). So it has a hard ceiling, and doctor's KS1.4 lint already
knows the numbers: **8191 chars through a `.cmd`/`.bat` shim, 32767 through CreateProcess**
(`src/Conductor/Commands/DoctorCommand.PromptSemantics.cs:26`, `:110`).

Measured on a minimal scratch plan (`%TEMP%/ks75-argv`, built-in templates, one stage), through the
fresh build — `dotnet run --project src/Conductor -- doctor -p <scratch>`:

| build | longest composed argv | headroom to 8191 |
|-------|----------------------|------------------|
| before this session (stashed tree) | **7545 chars** | 646 |
| first cut of the delegation guidance (+640 chars of new text) | **8207 chars** | **-16 — over the ceiling** |
| this commit (guidance kept, paid for by trimming the block it joined) | **7598 chars** | 593 |

At 8207 two live-run rigs died — `W3AuthTests.LiveRun_ReplayingSession13_ParksForReauth…` and
`SC4_1SettleAndRetryTests.LiveRun_TheGateBatteryDoesNotStartUntil…`. Both spawn their fake agent
through `cmd.exe /c`, as **28 files in this suite do**. The failure says nothing about length:
cmd.exe refuses the line, the agent never starts, and the run scores a retried or dead session.
Verified both directions — the two tests pass on the stashed tree and failed on the tree that added
640 chars, which is how the cause was established rather than guessed.

Second measured fact: **the lint under-measures the real spawn.** W3Auth's rig died at a lint number
of 7837 and survived at 7689, so the live line carries 350–500 chars the lint never sees — the battery
section, `AppendTail`'s sections, and the orchestrator's own flags (claude's `--mcp-config`). Filed as
**bug #55**.

Consequences, all of them acted on in this commit:

1. **Both new batteries are opt-in.** ~280 bytes on by default would spend a fifth of the headroom
   every plan has left. `batteries.repoMap` and `batteries.definitionOfDone` default to `false`, and
   `BatteriesConfig` says why in the property's own doc comment.
2. **The delegation guidance is net-funded.** It adds ~430 chars of new text and pays for it by
   compressing the block it joined; the net delta is +53 chars, not +640. No rule was removed — the
   two phrases other tests pin (`THIS IS THE ONLY WAY TO REPORT PROGRESS`, `There is no second
   mechanism`, `Evidence or it did not happen`) are intact and still asserted.
3. **A ratchet now guards the cliff.** `ShippedPromptStaysUnderTheCmdExeArgvCeiling` measures the
   shipped prompt through doctor's own lint with the ceiling STATED (8191, not resolved from this
   machine) and fails if it plus a 400-char live-spawn allowance would cross it. The next session that
   adds a paragraph to a built-in template gets one legible test failure instead of two dozen rigs
   failing with "the fake agent never started".

## 3. Why the repo map is a bet and is labelled one

`RepoMapBattery` makes the prompt BIGGER. It can only pay for itself by preventing exploration turns —
an exploration turn that lists ten directories costs far more than its ~300 bytes. That is a bet about
session behaviour, not an arithmetic saving, and the class doc says so rather than claiming a saving.
It is bounded (top-level source dirs with file counts, `maxEntries` capped), deterministic, and never
walks `.git`/`bin`/`obj`/`node_modules`.

## 4. Gates run

```
dotnet build Conductor.slnx -clp:ErrorsOnly -nodeReuse:false -p:UseSharedCompilation=false
  → 0 Warning(s), 0 Error(s)

dotnet test Conductor.slnx --filter "~Prompt|~ToolContract|~Composer|~M7Knowledge|~Battery|~KS1_4|~KS7_5"
  → Passed! Failed: 0, Passed: 171, Skipped: 0, Total: 171
```

(The `~Battery` slice is what pulls in both cmd.exe rigs that the argv cliff killed; they are green.)

New tests: `tests/Conductor.Tests/KS7_5ContextEconomicsTests.cs`, 12 facts — the repo map's contents,
determinism, bounds and empty cases; the recap's card selection, pre-filled claim, clipping and the
no-duplication invariant; registration behind each flag, including that a caller with no folded board
renders no empty heading; the delegation guidance reaching every working-session template; and the
argv ratchet.

## 5. What is still open

- The card's stated exit — a measured cache-read drop reported by `conductor budget` — needs N future
  sessions running under these prompts on a comparable stage. Nothing here claims it.
- Neither battery is switched on in this plan. Turning `definitionOfDone` on is a plan edit whose
  argv cost (~280 chars against ~590 of headroom) is now known and affordable; that is the owner's
  call, not a session's, because it edits the live plan of the run in flight.
- Bug #55 (lint under-measures the spawn) and bug #53 (cache_creation TTL split dropped) are open.
