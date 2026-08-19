# KS12.2 — the published surface, part 2: README, the index, §7, quickstart, and the harvest

Session 22. The three parts session 21 landed (`docs/cli.md` + the flags pin, the CHANGELOG release
body, `troubleshooting.md`) are evidenced in `ks12-2-docs-pin.md` and `ks12-2-changelog-section.txt`.
This file is the remainder: the front page, the docs index, `operating.md` §7, `quickstart.md`, an
extension to the docs-match-reality pin, and the payesh harvest re-run.

---

## 1. README.md — what edge added, and one claim it was making that nothing had measured

Measured before editing: `grep -n -i -E "mcp-observe|atif|otel|otlp|gate class|classed|holdout|chat
profile|observer|chats|chapar" README.md docs/README.md` returned **zero hits in both files**. Every
user-visible surface KS4, KS8 and KS11 added was absent from the page a reader opens first.

Added, each checked against the engine rather than against another doc:

| Added to README | Checked against |
|---|---|
| The gate-class table: `regression`, `mutation`, `holdout` | `docs/plan-config.md:362,407,323`, which are themselves derived from `PlanConfig` by the `PlanKeys` pin |
| Telegram surface split browse / steer / control, with all 14 verbs | `src/Conductor.Core/Integrations/Messaging/SurfaceCommands.cs:47-62` — the list is the engine's own |
| `admin` / `observer` chat profiles and the `telegram.chats` block | `SurfaceCommands.cs:30` — `AllowedFor` is literally `profile == Admin \|\| Scope == Browse` |
| "Getting the run's data out": `history export --atif`, `otel --dry-run`, `mcp-observe` | `src/Conductor/Program.cs` verb registrations; ADR-0007 link resolved to a file that exists |
| Live tracker now points at `EDGE-TRACKER.md` as well as `CORE-TRACKER.md` | the era in flight is edge |

**One correction, not an addition.** The README's Design decisions said battery collapse saves
"30-50% of output tokens". `docs/plan-config.md:41` already says, in as many words, that **the size
of the saving has never been measured** (FU-B10-2 — it needs an A/B on the same checkpoints with
only the flag flipped). The front page was quoting a number the project cannot stand behind, three
docs after the doc that retracted it. Replaced with the measured position.

## 2. docs/README.md — the index

`operating.md`'s row now names the observer-profile setup and the three read-only exits;
`plan-config.md`'s row names the anti-cheat gate classes and `telegram.chats`. A reader starting at
the index can now reach every surface edge added.

## 3. docs/operating.md §7 — the era-close gap list, re-measured

Re-dated from "as of 2026-08-15, end of the Karvansara era" (the CORE close) to the edge close.

**The false claim.** The closing paragraph asserted the era ended with "the anti-cheat ratchet
green". Measured this session:

```
$ powershell -NoProfile -File tools/gates/analyzer-debt.ps1
analyzer-debt: pragma-src           bar=31   now=33   unjustified=0
analyzer-debt: severity-downgrade   bar=15   now=17   unjustified=0
analyzer-debt: TOTAL                bar=46   now=50   unjustified=0
analyzer-debt: rules-enforced       bar=50   now=50   un-enforced=0

ANALYZER-DEBT GATE FAILED:
  * SUPPRESSIONS ROSE - kind 'pragma-src' went 31 -> 33 (#pragma warning disable under src/).
    The bar of 31 was set by commit 9707af7a1, and committing this does not move it.
EXIT=1
```

It is RED, it is bug 60, and §7 now says so — including that the bar may not be moved, because
there is no baseline file to edit: the bar is the minimum over the last 25 commits that touched a
measured file. The claim was inherited from the core close and was never true of edge.

**The true claim, re-measured rather than inherited:**

```
$ dotnet build Conductor.slnx -clp:ErrorsOnly     (bg child, 3m 1s)
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**Two rows added**, both operationally relevant and both costing this era real time:

- **10** — a newer engine migrates a store the installed one then cannot open (bug 45). This is the
  trap that would have killed KS12.1: `MigrationRunner.CurrentVersion` is 15 on this branch and 14
  on master.
- **11** — `CONDUCTOR_RUN_DB` does not redirect the measuring verbs (bug 61); `budget` resolves by
  repo first. Pass the db as the positional path.

## 4. docs/quickstart.md

One paragraph at the end of §3: two gates are enough to start, and a green exit code stops being
enough the moment the same agent writes the code and the tests. Points at the three classes and says
explicitly not to reach for them on day one — the quickstart's job is the first run, not the
hardened one.

## 5. The docs-match-reality pin, extended — and it caught the author

`tests/Conductor.Tests/SF7_1DocsMatchRealityTests.Readme.cs`. The README pin resolved a fenced
command's verb from the first word alone. Two of the engine's verbs are spelled as **two words** and
rewritten to a hidden one-word command before Spectre sees the argv — `history export` (KS8.2,
`VerbRewrites.HistoryExport`) and `run close|adopt` (KS0.2, `RewriteRunRecordVerbs`). So the pin read
the new README line as the `history` verb and called a working command broken:

```
README.md writes options the command does not declare:
  `conductor history export --atif -o run.json` -> history declares no --atif
  `conductor history export --atif -o run.json` -> history declares no -o
```

The pin was wrong, not the README — and it would have been wrong about `conductor run close` too.
Fixed by applying **the engine's own rewrite**, reflected out of `conductor.dll`, plus a mirror for
the local function that has no reflectable name, plus a new test
(`ProgramStillPerformsTheArgvRewritesThisPinMirrors`) that fails if `Program.cs` ever stops applying
either rewrite. `DeclaredOptions` now resolves hidden verbs, because a hidden verb's options are
still real.

**Proven red on a seeded stale doc.** With `--trajectory-only` seeded into the README's export line:

```
  `conductor history export --atif --trajectory-only -o run.json`
     -> history-export declares no --trajectory-only (it declares: --all --atif --home --output -o)
Failed!  - Failed: 1, Passed: 0, Total: 1
```

The pin now resolves through the rewrite, names the *real* command, and lists its *real* options —
which is also the proof that `--atif` and `-o` in the README are genuine. README restored; full
class green:

```
$ dotnet test Conductor.slnx --filter FullyQualifiedName~SF7_1DocsMatchRealityTests
Passed!  - Failed: 0, Passed: 35, Skipped: 0, Total: 35
```

(34 before this session's addition, 35 after.)

The **config-key** half of the acceptance needed no new test: `SF7_1DocsMatchRealityTests.PlanKeys.cs`
derives its expectation from `PlanKeySchema` walking `PlanConfig`'s type graph, so every key edge
added — `holdoutGates`, `gates[].class`, `gates[].visibility`, `telegram.chats` — is already pinned
by construction and green above. A hand-typed list would have been the rot the suite exists to stop.

## 6. The payesh harvest, re-run — and it changed a published fact

`C:/code/conductor-site` (shaahink/payesh), branch `ks12/harvest-era-close`, **PR
https://github.com/shaahink/payesh/pull/2**. Never main; the owner merges at KS12.3.

The harvest is the site's first litmus test: no figure is typed in, every one is recomputed from the
run store. Re-running it against the store as it stands today **broke the site's own corpus test**:

```
test/corpus.test.mjs:265 — no row in the shipped corpus has an unlabelled status
  ... has status "closed", which RunTable.astro paints in no role
```

Root cause, measured: three July runs were published as `abandoned` with the caveat "the engine
exited without closing the record". They are `closed` in the store now — closed **by hand through
the CLI**, which is exactly what `src/Conductor.Core/Store/RunRecord.cs:22` defines the word to
mean: *"the run is over and will not be resumed."* It says nothing about whether the work finished.
`harvest.mjs`'s `disposition()` passed the word straight through.

Fixed at the harvest: `closed` resolves through `anonymise.json` exactly like `running`, and is
refused without a `disposition` for the same reason — the store cannot tell an abandoned run from a
tidied one. The three runs stay `abandoned`, which is what they were. What legitimately changed:
they now carry a recorded end, so their date label is "ended" rather than "last active" and the
caveat note is gone.

```
harvest: 18 runs published, 12 excluded by anonymise.json · 340 sessions · $3,016.29 · 648/677 gates green
node --test: 126 pass, 0 fail
```

Two things stated rather than fixed:

- `npm run anonymity` is **red, and was red before this branch** — it fails on the generic word
  `website`, because a catalogued repo whose whole name is an ordinary noun makes every built page
  look like a leak. That is conductor bug 41, open since KS10.
- The two Karvansara runs (`9491891f` edge among them) remain **excluded** by `anonymise.json`, so
  the era closing this week is not published on the site. That is the fail-closed rule working;
  publishing them is an editorial call for the owner, not a session.
