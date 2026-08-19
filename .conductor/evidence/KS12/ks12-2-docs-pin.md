# KS12.2 (partial) — the flag surface, pinned (2026-08-19, session 21)

**Status: KS12.2 is NOT claimed.** This file records the half that landed. What is still owed is in
the tracker handoff.

## The gap, measured before anything was written

Two doc bars already derive their expectation instead of restating it: `K7_2DocsVerbCoverageTests`
(every shipped verb is named in `docs/cli.md`, scanned off `Program.cs`) and
`SF7_1DocsMatchRealityTests.PlanKeys` (every settable plan key is documented, walked off
`PlanKeySchema`). Both were **already green for everything edge added** — the verbs `worktree`,
`otel`, `mcp-observe` and the gate-class/judge config keys were all documented as they landed. So the
contract's "pin every verb and config key edge added" had nothing to add: it was already enforced,
and saying so is more useful than inventing a redundant test.

**The level below the verb had no bar at all.** Reflecting `[CommandOption]` off every non-hidden
command and asking whether `docs/cli.md` names it:

    non-hidden distinct long options : 82
    never named in docs/cli.md       : 41, across 13 verbs

    bg   --cwd --purpose --tail          bug  --detail --severity --stage --wontfix
    demo --output                        face --demo
    init --model --name --output         log  --query --tail
    mcp-serve --events --journal --run-db --run-id --session --state-dir
    new-plan --name --output             note --kind --stage
    plan --agent --create --model --name --output
    run  --no-control-plane
    task --amend --blocked --blocked-until --commit --evidence --note --skipped --todo
    watch --hook-timeout --notify --poll

`task --evidence` and `task --blocked-until` are on that list — flags **this repo's own session
prompts instruct agents to use**, absent from the reference those agents are pointed at.

## What landed

- **`docs/cli.md` now names all 82.** Written onto the verb's own row rather than dumped in an
  appendix, with the behaviour beside the name (`--blocked-until` is the *timed* wait and is a
  different thing from `--blocked`; `--purpose` defaults to the executable name; `--create` exists
  because nothing reads an undeclared plan key).
- **`tests/Conductor.Tests/SF7_1DocsMatchRealityTests.Flags.cs`** — two facts:
  - `TheCliReferenceNamesEveryLongOptionAShippedVerbDeclares`, derived by reflection off the shipped
    assembly (verb→type still comes from `Program.cs`, because Spectre keeps `CommandApp`'s
    configuration private — the same reason the verb scanner is a source scan).
  - `RemovingOneDocumentedFlagMakesTheDerivationNameThatExactOption` — the pin proving the pin, on
    three flags written three different ways: `--evidence` (inside a table cell), `--purpose` (prose
    on a row), `--no-control-plane` (inside a fenced block).

## The pin went red on its first real run, and it was right

Not a seeded red — an actual defect, and neither the prose nor the author saw it:

    docs/cli.md never names 1 long option(s) that a shipped verb declares: --force

`rollback`'s row read *"Reset working tree to the stage start commit (`--yes` to force)"*. That
conflates two different flags. `CtlCommand.Settings` declares both: `--yes` **confirms** a
destructive action, and `--force` means *proceed even though the working tree is dirty* — and it does
not stash that tree, it **discards** it (`ControlDispatcher.cs:189-194`; ARCHITECTURE.md §5 had it
right all along). A reader following cli.md would have typed `--yes` expecting `--force`'s behaviour
on a dirty tree and been refused, or worse, learned the wrong meaning of the one flag in the CLI that
destroys uncommitted work. Corrected, and `--force` now has its own sentence.

## Proof

    $ dotnet test Conductor.slnx --filter "FullyQualifiedName~SF7_1DocsMatchRealityTests|FullyQualifiedName~K7_2Docs"
    # before the cli.md fix
    Failed!  - Failed: 2, Passed: 33, Total: 35     <- both new facts, naming --force
    # after
    Passed!  - Failed: 0, Passed: 35, Total: 35, Duration: 2 s

Raw logs: `.conductor/bg-logs/ks12flags4-*.log` (the red, with the assertion text) and
`.conductor/bg-logs/ks12flags5-*.log` (the green).

## Also landed under KS12.2

- **`CHANGELOG.md`'s `[Unreleased]` section written as the release body** — `release.yml` refuses a
  tag whose section is missing and uses it verbatim. It covered KS11 only; KS4, KS6, KS7 and KS8 are
  now in it, with a `### Fixed` block. Proved locally with the same script the release guard runs:
  `sh tools/changelog-section.sh Unreleased` → 112 lines, exit 0, captured at
  `.conductor/evidence/KS12/ks12-2-changelog-section.txt`.
- **`docs/troubleshooting.md`** — a new section for the failure mode edge invented, *"A gate exited 0
  and the battery still went red"*, with a row per class (`REGRESSION`, `MUTANTS`, a redacted
  holdout) and what to look at for each. Plus a real correction: the "Where the truth lives" table
  still said `run.db` lives at `.conductor/run.db`, which K3.1 changed — it is in the machine-level
  state home, and a `.conductor/run.db` still on disk is a pre-K3.1 leftover nothing writes.
