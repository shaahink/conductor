# PK6.2 — the docs reconciled for the courier as its own binary; seams recounted; ADR-0009

Session 12, 2026-09-17. Spec: plan A stage table row "PK6.1" of `docs/dev/NEXT-ERA-FINDINGS-2026-09-17.md` (PK6.2 in the plan), decisions D1, D2, D3, D4, D5.

## How the gaps were found

A read-only survey compared every verb, switch, key, endpoint, home file and task change on
`master..feat/peyk-courier` with `docs/cli.md`, `docs/operating.md`, `docs/plan-config.md` and
`ARCHITECTURE.md`. Much of it was already documented checkpoint by checkpoint (`say`, `room`,
protocol 3, `/hello`, the figures, the reaction). Several things were missing or wrong:

| Gap | Where it was wrong | Fixed in |
|---|---|---|
| `courier status` heartbeat (`alive / stale / dead / absent / alive by pid only`) | cli.md, operating.md did not have it | both |
| Keep-alive trigger every five minutes (`PT5M`, `IgnoreNew`) | cli.md said only "restarts on failure every minute" | cli.md, operating.md, ARCHITECTURE.md |
| "`tools/install.ps1` stops the courier" | cli.md, operating.md §2 and §3 still said it; false since PK1.2 | rewritten: the engine is published around the courier; `-CourierOnly` replaces it |
| `lastPollUtc`, the death record, the exit journal, a run's boundary restart | not documented anywhere shipped | cli.md ("Alive, or known dead"), operating.md §3, ARCHITECTURE.md |
| **How to read a courier death out** | nowhere | operating.md §3 "The courier died — reading out why": the instruments table and a six-step procedure. PK6.1 follows it against the real courier and corrects anything the measurement disproves |
| `task --done --tell` | missing from operating.md §2 | the `task` row |
| `inbox list` `from`/`msg` columns | missing from operating.md §2 | the `inbox` row |
| `room add --project --counters --footer-live --footer-pending` | missing from operating.md's `room` row | the row |
| `release preflight --courier-task` | missing from operating.md | the row |
| `courier.secret` among the home files | missing from cli.md | the state paragraph |
| `notify/<event>.md` overrides, incl. `checkpoint-card` and `stage-card` | missing from every shipped doc | plan-config.md `templatesDir` |
| ARCHITECTURE: the project map without `src/Conductor.Courier` | lines 12-21 | map redrawn: CLI → Courier (build copy) → Core only |
| ARCHITECTURE: "the courier is a separate **process** (`conductor courier run`)" | line 36 | "separate **executable**" |
| ARCHITECTURE: courier section describing `Http/CourierListener.cs` in the CLI, `/hello` and `/push` only, `Version = 2` | lines 472-525 | rewritten: three homes (Courier project / Core/Courier / CLI), lifecycle, home files, the protocol-3 loopback table, four invariants, "Alive, or known dead" |
| ARCHITECTURE: "exactly **thirteen** `public interface I*`" | line 307 | **fifteen**, counted from source, with rows for `ICourierDesk` and `IFittingBattery` |

## The seam count, measured

```
$ grep -rhn "^\s*public interface I[A-Z]" src/Conductor.Core --include=*.cs | sort   (15)
IAgentProvider; ICloudCli; ICourierDesk; ICourierSource; IEventSink; IFittingBattery : IPromptBattery;
IMessageChannel; IPlanner; IProgressProvider; IProgressSink; IPromptBattery; IReportsStartOutcome;
IRunNotifier; IRunStore : IDisposable; ITranscriber
```

The DV7.1 table had 13 of these. The two new ones are `ICourierDesk` (PK3.1, `Courier/CourierDesk.cs:5`) and `IFittingBattery` (PK4.3, `PromptBattery.cs:26`).
The Charkh section's "still thirteen at CH5.1 (2026-08-27)" is left as it was, because it is a dated
historical statement.

## ADR-0009

`docs/dev/adr/0009-the-courier-is-its-own-binary-and-one-wire.md`, which amends 0008. It restates 0008's **four
conditions** by their own titles: (1) loopback, secret, fixed port, with protocol 3's verbs added; (2) ingress for
notes and never for run state, with the figures now read-only; (3) a durable offset, with sends now ledgered; (4) version
skew refused by name, where 0008's installer half is reversed by D1 and the restart is supervised under D2. It adds
the **two new conditions**: (5) sending is not polling, so a run falls back to a direct send (D3); (6) a live run
may add only its own project (D4), together with D4's security note. It carries Consequences, including one found
while writing it: `courier deny` does not outlast the project's next run start. It is linked from
`docs/dev/README.md`'s ADR row, from 0008's Status line, from `README.md` and from ARCHITECTURE.md.

## The pins — `tests/Conductor.Tests/SF7_1DocsMatchRealityTests.Peyk.cs` (9 facts)

Each pin is derived from the code or constant that owns it, and each has a negative control inside the test:

| Fact | Derived from | Negative control (asserted in the test) |
|---|---|---|
| Every Peyk verb, switch, subverb and key is inside the general nets and documented in cli.md **and** operating.md §2 | `ShippedVerbs()`, reflected `CommandOption`s of `SayCommand`/`RoomCommand`/`TaskCommand --tell`/`ReleaseCommand --courier-task`, `DeclaredSubverbs()`, `PlanKeySchema` (`stages.deploys`) | — (see next row) |
| Dropping one item makes each derivation name exactly it | as above | `say` blanked in §2 → `[say]`; `--reply-to` → `[--reply-to]`; `room import` → `[room import]`; the `deploys` row → `[stages.deploys]` |
| `courier status` row names every life in its own words | `CourierVitals.Describe` for every `CourierLife` value (a property: a new life fails here) | `` `stale`` blanked → `[stale]` |
| Keep-alive phrase and `-CourierOnly` | `CourierTask.KeepAliveInterval` via `XmlConvert` → "every five minutes"; `[switch]$CourierOnly` in `tools/install.ps1` | the phrase for `PT10M` is absent from cli.md |
| Every loopback path and the protocol version in cli.md and ARCHITECTURE.md | reflected `CourierEndpoint.*Path` constants, `CourierProtocol.Version` | `/react` blanked → `[/react]` |
| Every courier-home entry in cli.md and ARCHITECTURE.md | reflected `CourierHome.*FileName/*DirName`, `Rooms.DirName`, camel-cased `CourierPresence.LastPollUtc` | `messages.jsonl` blanked → `[messages.jsonl]` |
| ARCHITECTURE seam count and table | every `public interface I*` scanned from `src/Conductor.Core` | "fifteen" → "fourteen" names the mismatch; the `ICourierDesk` row removed → `[ICourierDesk]` |
| Every notify event in plan-config's templates section | every `ComposeAsync("<event>", …)` in `src/` | `` `stage-card` `` blanked → `[stage-card]` |
| ADR-0009 restates 0008's conditions by title and adds two naming D3/D4; indexed | 0008's own "Four conditions" sentence + its `### N.` titles | condition 2's title removed → `[that title]`; `### 6.` removed → `[5 numbered conditions, expected 6, no new condition names D4]` |

**Against the real files** (`pk6.2-seeded-mutations.log`): with ARCHITECTURE.md set back to "**thirteen**", the
seam fact failed with `["says thirteen, the source declares 15"]`. With ADR-0009's sixth heading removed, the ADR fact failed with
`["5 numbered conditions, expected 6", "no new condition names D4"]`. Both files were then restored byte for byte from the pre-mutation copies.

## Runs

- `dotnet build Conductor.slnx -clp:ErrorsOnly`: 0 warnings, 0 errors. `dotnet build tests/Conductor.Tests`: 0 warnings, 0 errors.
- Docs tests before the new pins, `--filter SF7_1DocsMatchRealityTests|K7_2DocsVerbCoverageTests|ArchitectureBoundaryTests`: **64/64** on the reconciled docs.
- With the pins: **73/73** (9 new).
- Neighbours that read these docs (BugSweep_Round3, CH4_1ReleasePreflight, CH4_2ReleasePerform, DV4_1Courier, K7_1ClosureLedger,
  KS3_3SchemaHonesty, KS9_3ProjectsScopeRefusal, PK4_3RoomVoice, SC2TruthfulSurfaces, PlanSetCommand, plus the above): **247/247**.
- `python tools/ch3/link-sweep.py`: `live-broken=0` (frozen-broken=444 is the historical record, unchanged).

## Commits

`081fa60` (the docs and ADR), plus the commit that carries the pins, the ADR wording fixes, `courier.secret` in cli.md and this file.
