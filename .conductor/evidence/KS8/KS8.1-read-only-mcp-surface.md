# KS8.1 — the read-only MCP surface

**Checkpoint**: KS8.1 — *Read-only MCP surface: history/status/money as MCP resources; control ops
excluded **by design** (ADR-0005 spirit; MCP's 2026 attack record cited in the ADR that documents
this).*
**Falsifiable exit**: *An MCP client lists runs and quotes reconciled status; no write tool exists on
the surface.*

Both halves are measured below against a live JSON-RPC session with the **fresh build**
(`dotnet run --project src/Conductor --no-build -- mcp-observe`), not the `conductor` on PATH.

---

## 1. What shipped

| | |
|---|---|
| Verb | `conductor mcp-observe [--home <PATH>]` — `src/Conductor/Commands/McpObserveCommand.cs` |
| Server | `McpObserveServer` — `src/Conductor.Core/Integrations/McpObserveServer.cs` (dispatch) and `.Resources.cs` (the three families) |
| ADR | `docs/dev/adr/0007-read-only-mcp-surface.md` |
| Docs | `docs/cli.md` reference row; `docs/operating.md` §2 "Full command reference" |
| Tests | `tests/Conductor.Tests/KS8_1ReadOnlyMcpSurfaceTests.cs` — 11 tests, all green |

Three resource families, matching the checkpoint's three words:

- `conductor://history` — every catalogued run, newest activity first.
- `conductor://runs/{run}/status` — one run: reconciled status, stored status, and the same
  `StateDto` the Face renders.
- `conductor://runs/{run}/money` — one run's billed spend, through the same `MoneyAnalyzer` /
  `MoneyJson` that `conductor money --json` uses.

**A separate server and a separate verb, not a flag on `mcp-serve`.** The difference between the two
surfaces is the threat model, and a threat model belongs in the command an operator types. The
reasoning — MCP's 2026 disclosure record, and the specific badness of a reachable `task_update` in a
project whose whole verdict mechanism rests on "a checkpoint is confirmed by the battery, not by the
claimer" — is in ADR-0007.

---

## 2. Exit half A — an MCP client lists runs and quotes RECONCILED status

Full transcript: `KS8.1-live-mcp-transcript.txt`. Client: `KS8.1-mcp-client-probe.py` (a plain
JSON-RPC-over-stdio client; it does the `initialize` / `notifications/initialized` handshake first).

`resources/read conductor://history` → **35 runs across 12 repos**, of which this excerpt:

```
shortId    plan                   repo           status       stored         cost   sess
9491891f   Karvansara edge - gate conductor      running      running      271.38     19
           ks75-argv              ks75-argv      gone                        0.00      0
c5fe473d   KS7.2 hook-ground-trut ks72-rig       orphaned     running        0.16      2
380c587c   KS7.1 posture rig      ks71-rig       orphaned     running        0.14      1
6cf402fe   Test                   Test           orphaned     paused         0.00      1
9647f1b8   Karvansara core - the  conductor      Aborted      Aborted      147.55     24
...
RECONCILED != STORED on these rows: c5fe473d (running -> orphaned), 380c587c (running -> orphaned),
                                    6cf402fe (paused -> orphaned)
```

This is the claim, and it is proved in **both** directions in one read:

- Three rows whose database still says `running`/`paused` are quoted as `orphaned` — nothing is
  driving those stores. A surface that echoed the status column would be wrong on 3 of 35 rows today.
- Row 1 is **this very run** (`9491891f`, the karvansara-edge run driving this session). It says
  `running` because an engine genuinely holds that store. Reconciliation that only ever said
  "orphaned" would be a broken clock; this one distinguishes.
- Rows whose database is gone are listed as `gone` rather than hidden — a run whose file was deleted
  is a fact worth showing.

Both words ride every row (`status` and `storedStatus`), because reconciling is a rendering decision
and a surface that dropped the stored value would be hiding the evidence for its own claim.

`conductor://runs/c5fe473d/status` returns the orphaned run in full, including the Face's own
projection from the archive:

```json
{ "runId": "c5fe473d381c46aaad55d92763a3c10d", "plan": "KS7.2 hook-ground-truth rig",
  "status": "orphaned", "storedStatus": "running", "storeLooksLive": false,
  "engine": "0.4.2-alpha.0.25+5b8d56ed1604.dirty",
  "state": { "status": "orphaned", "stageId": "R1", "stageTitle": "Posture proof",
             "doneCount": 1, "totalCount": 1, "totalCostUsd": 0.15720507459 } }
```

`conductor://runs/c5fe473d/money` returns billed dollars only — no price table is applied anywhere,
`costUsd 0.1572` is what was billed, split `agent 0.1571` / `gate 0.0001`.

## 3. Exit half B — no write tool exists on the surface

Four independent measurements, in the transcript in order:

1. **`initialize` declares no `tools` capability.** The result carries
   `capabilities: { resources: { subscribe: false, listChanged: false } }` and nothing else. That is
   the part a conforming client reads before it asks anything.
2. **`tools/list` → `{"tools": []}`.** Answered, not errored: a client that sees `[]` knows it asked
   a server that has none, where method-not-found reads as an older server that might have them under
   another name.
3. **`tools/call` is refused for every tool the agent surface offers.** Live, six of them
   (`task_update`, `inject_instruction`, `bg_start`, `conductor_note`, `bug_new`, `run_query`); in
   the test suite, **all sixteen**, and the list is not hand-typed — `AgentSurfaceToolNames()` scans
   `McpTaskServer.cs` for it, so a seventeenth tool added there tomorrow joins this battery
   automatically. Every one gets `-32601` and a sentence naming ADR-0007.
4. **An invented write view is refused by name**: `conductor://runs/{id}/abort` →
   `-32602 unknown view 'abort' — this surface serves /status and /money.`

And the structural guarantee, which is the one that survives the next hand:

- **Read-only is enforced by SQLite, not by discipline.** Every answer is built from
  `RunHistory` / `ArchiveView` / `RunArchive`, and `RunArchive`'s connection is `Mode=ReadOnly`
  (`src/Conductor.Core/History/RunArchive.cs:12,30-31`). A write added here by mistake would be
  refused by the connection at runtime, not shipped.
- `Observe_server_sources_never_reach_a_writable_store` forbids `IRunStore`, `SqliteRunStore`,
  `AppendEvent` and `ExecuteNonQuery` from appearing anywhere in `McpObserveServer*.cs`. That is what
  stops the next change from wiring a writable store in "just to read one more column".
- The server's stderr was **empty** across the whole session: the stdio wire carried JSON-RPC and
  nothing else.

## 4. A defect this checkpoint found and fixed

Driving the surface against the **real** catalogue (rather than only the fixture) caught a bug that
source-reading would not have: `RunHistoryRow.Plan` is the **catalogue entry's** plan name, not the
run's. One store holds every run of a `(repo, plan)` pair as the catalogue *first* saw it, so
conductor's own store is still catalogued as `Karvansara core - the open door` while it now holds the
karvansara-**edge** run. The first draft of `conductor://history` labelled this very run with the
previous plan's name. `conductor history` already picks the run's own name
(`HistoryCommand.cs:141`) without saying why.

Fixed: `plan` is now the run's own `PlanName`, with the catalogue's kept beside it as `cataloguedAs`.
Regression pinned by `History_names_the_runs_own_plan_not_the_catalogue_entrys`, which seeds a second
run into the same store under a renamed plan — what a rename actually looks like on disk.

## 5. Gates run

```
dotnet build Conductor.slnx -clp:ErrorsOnly -nodeReuse:false     → 0 errors, 0 warnings
dotnet test  --filter KS8_1ReadOnlyMcpSurfaceTests               → 11 passed, 0 failed
dotnet test  --filter K7_2DocsVerbCoverage|SF7_1DocsMatchReality|B11_2
                                                                 → 41 passed, 0 failed
```

The last one is the new-verb battery: a shipped verb must be in `Program.cs`, in
`CompletionCommand.Verbs` (both generators), in `docs/cli.md`'s reference table and in
`docs/operating.md` §2. All four landed in this commit.

## 6. What KS8.1 does NOT claim

- No authentication or transport hardening: this is stdio, and whoever can run the process can read
  the catalogue — the same reach they already have over `conductor history`. It is exactly a
  read-only mirror of verbs that already exist, which is why that reach is not a new exposure.
- No subscriptions: `resources` is declared with `subscribe: false`. A client polls.
- KS8.2 (ATIF trajectory export, `AGENTS.md`) is untouched by this checkpoint.
