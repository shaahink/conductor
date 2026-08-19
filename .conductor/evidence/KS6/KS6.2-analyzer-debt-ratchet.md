# KS6.2 — the analyzer-debt ratchet, and the anchor that was inside the game

Stage KS6, session 12, 2026-08-19. Deliverable: `tools/gates/analyzer-debt.ps1`, wired from
`tools/gates/ratchet.ps1`, plus the removal of 14 suppressions that were proved to be guarding nothing.

Commits: `0cb514d` (the gate + the dead-pragma removal), `4b25081` (the windowed anchor + the two
corrections it forced), and the test commit that follows this file.

---

## 1. The count was one of five. All of them are counted now, and each is named.

`ratchet.ps1` counted `#pragma warning disable` under `src/` and nothing else. Measured with the exact
regexes the gate uses (`git grep`, working tree, before any edit this session):

| kind | what it is | at session start |
|---|---|---|
| `pragma-src` | `#pragma warning disable` under `src/` — the only one the old gate saw | 45 |
| `pragma-tests-tools` | the same pragma under `tests/` or `tools/`, outside the old PathSpec | 0 |
| `suppressmessage` | `[SuppressMessage]` attributes | 0 |
| `nowarn` | `NoWarn` / `WarningsNotAsErrors` in props, csproj, targets | 0 |
| `severity-downgrade` | `dotnet_diagnostic.X.severity = none/silent/suggestion` | 17 |
| `severity-blanket` | `dotnet_analyzer_diagnostic[.category-X].severity` — a whole category in one line | 0 |

`warning` is deliberately NOT counted as a downgrade: `TreatWarningsAsErrors` is true here (and
`ratchet.ps1` section 2 keeps it true), so a warning fails the build exactly as an error does.

## 2. Bug 44, resolved downward. 14 of the 45 pragmas were guarding nothing.

Not argued — measured. Strip every pragma line from `src`, drop `MA0045`/`MA0040`/`CA1849`/`CA1031` to
warning, build the whole solution, then map each diagnostic back to the pragma region that had been
covering it. The strip shifts line numbers, so the mapping is *stripped line N -> the N-th surviving
original line*.

- Build log: `.conductor/bg-logs/ks62-dead-suppression-census-20260819-013810327.log` (exit 0, 193
  distinct diagnostic sites).
- **5 CA1031 pragmas**: `CA1031` sits at `suggestion` (`.editorconfig:35`), so these suppress a
  diagnostic that cannot fail a build. Removed; the design intent that rode on their comments was moved
  onto the `catch` itself so nothing was lost.
- **9 MA0045/CA1849 regions containing zero diagnostics**: mostly file-top disables on *partial* classes
  where the sync call actually lives in a sibling partial file that carries its own pragma
  (`RunLoop.cs`, `VerdictEngine.cs`, `EventLog.cs`, `TranscriptLog.cs`, `SoftBreak.cs`,
  `RunRecordMaintenance.cs`, `McpTaskServer.Handlers.cs`, `AuditCommand.cs`, `BgLogsHandler.cs`).

**45 -> 31 pragmas, build green, 0 warnings 0 errors.** `maxPragmas` in `ratchet-baseline.json` ratchets
**down 38 -> 31**. The ceiling was never raised, and bug 44 closes with 7 to spare.

Unjustified suppressions are now **zero** across all six kinds: the two remaining bare pragmas got real
one-line reasons, and two severity downgrades that carried no reason at all (`CA1051`, and `MA0004`
under `[tests/**/*.cs]`) now carry one.

## 3. The finding: the anchor was reachable from inside the game — and so is all of ratchet section 3.

The first version of this gate anchored on `origin/<branch>`, copying `ratchet.ps1`. Seeding the proof
rather than trusting it showed that is worth nothing **in the flow this repo actually runs**:

> A session commits, pushes, and THEN conductor runs the battery. At gate time `origin/<branch>` **is**
> HEAD, so every check phrased as "not worse than what is already pushed" compares the tree against
> itself.

That is 3a (the floor file), 3b (architecture debt), 3c (tests may not be deleted) and 3d (the gate
scripts) — all four catch an *uncommitted* seed and nothing else. Attack 8 below is the demonstration.

**The bar is now the minimum over the last 25 commits that touched a measured file.** One commit cannot
move a minimum; twenty cannot either, since the debt would have to stay elevated across the whole window
while the gate went red on every commit in it. Raising it on purpose is `-Anchor` /
`CONDUCTOR_ANALYZER_ANCHOR` — a human's call, in the open. There is no baseline file to rewrite.

Two corrections the window forced, both real:

- **A raw count of severity downgrades is not a ratchet.** Asserting it was would have made KS6.1's own
  deliverable illegal: declining to adopt a rule is how a curated set is written and it looks identical
  to debt. Growth is checked **per rule** instead — a rule *enforced* at any commit in the window must
  still be enforced. Adopting is free, declining what was never enforced is free, quietly un-enforcing
  is not.
- **`ratchet.ps1` now passes `-BaseRef` down** as the child's `-Anchor`. Seeded with `-BaseRef HEAD` it
  caught the ceiling rewrite while the child was still measuring against `origin/` and passing.

## 4. Seeded attacks — eight, each expected RED, each captured

Full transcript: `.conductor/evidence/KS6/KS6.2-seeded-attacks.log`, run against the committed tree at
`4b25081`, tree restored and green at the end.

| # | attack | caught by |
|---|---|---|
| 1 | a bare pragma added to `src` | `pragma-src` rose 31->32 **and** UNJUSTIFIED, named with its file |
| 2 | a pragma laundered into `MA0045.severity = none` — the old gate scored this as an improvement | RULES QUIETLY UN-ENFORCED, naming `MA0045` and the commit that enforced it |
| 3 | a pragma parked under `tests/`, outside the old PathSpec | `pragma-tests-tools` rose 0->1 |
| 4 | the referee itself rewritten to `exit 0` | `ratchet.ps1` 3d, naming `analyzer-debt.ps1` |
| 5 | `maxPragmas` raised back to 45 | `ratchet.ps1` 3a, THE SUPPRESSION CEILING WAS RAISED (31 -> 45) |
| 6 | `CA1849` quietly moved error -> suggestion | RULES QUIETLY UN-ENFORCED |
| 7 | a blanket `dotnet_analyzer_diagnostic.severity = none` | `severity-blanket` rose 0->1 |
| 8 | **the suppression committed** — i.e. what `origin/<branch>` would have been | `pragma-src` rose anyway: *"the bar of 31 was set by commit 0cb514df7, and committing this does not move it"* |

Attack 8 is the one the design turns on. Against the single-ref anchor it **passed**; against the window
minimum it is red.

## 5. Made permanent

`tests/Conductor.Tests/KS6_2AnalyzerDebtRatchetTests.cs` drives the gate against a throwaway git repo and
re-runs attacks 1, 2, 3, 7 and 8 on every battery, plus the case that must stay GREEN: declining a rule
this repo never enforced. A one-day proof becomes a standing one.

## 6. What this does not cover, stated plainly

`ratchet.ps1` 3d protects the referee's *source* only while an edit is uncommitted or unpushed. Once
pushed, the diff is empty and 3d has nothing to say. The window minimum closes that gap for the
*measurement*; the referee's source is covered by review and by the tests above.

The same hole leaves a real gap that is **not** this checkpoint's to close: 3c ("tests may not be
deleted") is vacuous for the same reason, and the absolute floor behind it is `minTests: 1932` against an
actual 2487 — so 555 test attributes could be deleted today with both halves of the gate silent. Recorded
in the ledger for whoever owns it.
