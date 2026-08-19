# KS12.1 — ARCHITECTURE.md reconciled against the engine (2026-08-19, session 21)

The contract: *"ARCHITECTURE.md and `docs/dev` reconciled with the engine for everything edge
changed."* Reconciled means **measured**, not re-read. Every `file:line` in the document was resolved
against the file it names and the line printed; the raw after-state is
`ks12-1-architecture-citations.txt` (91 citations, produced by walking the doc with a regex and
opening each target).

## 1. Twenty-seven citations had drifted — corrected, with the true line measured

Not one of these was *found* by reading the prose; the prose reads fine. They were found by opening
the cited line. The largest are structural — a symbol that moved to a different **file**:

| what the doc names | cited | actually at | why it moved |
|---|---|---|---|
| `VerdictEngine.EvaluateSessionAsync` | `VerdictEngine.cs:116` (→ `ReflectionStep`) | **`VerdictEngine.Evaluate.cs:111`** | KS6.4 (`a8a9066`) split the verdict out of the loop |
| `SelectStage` | `RunLoop.Plumbing.cs:25` (→ a blank line) | **`Orchestration/StageSelection.cs:56`**, and it is `StageSelection.Select` | the method no longer exists under that name anywhere |
| `GateRunner` the class | `GateRunner.cs:25` (→ a parameter) | **`:6`**, and it is `static partial` now | KS4 (`9707af7`) split it into `GateRunner.Classes.cs` |
| `RunAllAsync` | `:35` | **`:22`** | same split |
| `SqliteRunStore.ApplyTaskStatus` | `Sessions.cs:249` (→ a SQL WHERE clause) | **`:367`** | drift |
| `RunLoop.RegenerateTracker` | `Plumbing.cs:301` (→ `SyncWorkGraphFromDeclared`) | **`:313`** | drift |
| `TrackerGenerator.Write` | `:151` (→ an `AppendLine` of a fence) | **`:158`** | drift |
| `ConfirmStageAsync` | `Phase.cs:140` | **`:136`** | drift |
| `RunPhaseGateAsync` (the phase gate) | `Phase.cs:29` | **`:25`** | drift |
| `discoverControlPlane()` | `main.go:187` (→ a comment) | **`:280`** | drift |
| the Face's 1 Hz poll | `messages.go:187` (→ a closing brace) | **`:189`** (`CmdTick`) | drift |
| `c.AddCommand<…>` | `Program.cs:74` (→ a comment) | **`:46`**ff | drift |

The other fifteen are the same shape, one to two dozen lines each: `Orchestrator.RunAsync` 101→103,
the loop's `while` 104→**131**, plan hot-swap 112→**141** (`ReloadThenCheckCap`), read-the-work
199→**163** (`_ctx.ReadWork()`), phase-gate pre-empt 206→**199**, dispatch 399→**414**,
`ResolveTemplatePath` 150→**169**, `Render` 203→**222**, `BatterySection` 269→**288**, the prompt
file write 212→**175**, `AgentSession.Start` 111→**137**, the stdout parse 166→**181**, the live-usage
fold 130→**147** (`EmitLiveUsage`), `CheckSoftBreak` 20→**21**, the hard ceiling 93→**99**
(`EndOnBudget`).

Two entries in the after-state report `NOFILE`: `Phase.cs:136` and `.Resources.cs:15`. Both are the
audit script failing on an abbreviated path in prose, not a bad citation — they are
`VerdictEngine.Phase.cs:136` and `McpObserveServer.Resources.cs:15`, both verified by hand.

## 2. A counted claim was wrong, and counting is the only way that shows

> *"`src/Conductor.Core` declares exactly **nine** `public interface I*`. That is the whole list."*

    $ grep -rn "^public interface I" src/Conductor.Core/ --include=*.cs
    Events/EventLog.cs:8                              IEventSink
    Integrations/Messaging/IMessageChannel.cs:15      IMessageChannel      <-- the tenth
    Integrations/TelegramService.cs:22                IRunNotifier
    IPlanner.cs:7                                     IPlanner
    IReportsStartOutcome.cs:17                        IReportsStartOutcome
    Planning/IProgressProvider.cs:12                  IProgressProvider
    Progress.cs:67                                    IProgressSink
    PromptBattery.cs:10                               IPromptBattery
    Providers/IAgentProvider.cs:6                     IAgentProvider
    Store/IRunStore.cs:10                             IRunStore

**Ten.** KS11.1 extracted `IMessageChannel` and the sentence has been wrong for the whole edge era.
Corrected in place with the reason beside it, and the seam table now carries its row. Two more table
errors found the same way: the row labelled `ITelegramService` names a type that **does not exist** —
the interface at `Integrations/TelegramService.cs:22` is `IRunNotifier` — and the `IPromptBattery`
implementation list was missing KS7.5's two (`RepoMapBattery`, `DefinitionOfDoneBattery`).

## 3. What edge built that the map did not show — added, every claim cited

+112 lines, and each of them names a file and a line that was opened to write it:

- **§4** — the pure verdict function (KS6.4): gather → `SessionVerdict.Decide`
  (`SessionVerdict.cs:19`) → apply `VerdictDecision` (`VerdictDecision.cs:28`), with the three
  *continuation* dispositions (`VerdictDisposition.cs:19,46,53`) that let a total function ask for
  more evidence instead of reaching for it. And judge-as-evidence (KS4.5): an `AdvisoryEvidence` row
  (`SessionEvidence.cs:12`) tagged `judge:<command>`, which `Decide` never reads.
- **§5b, new** — the three gate classes an exit code cannot express. **Holdout**
  (`GateVisibility.cs:28`), redacted where it is produced (`GateRunner.cs:26,29,162`), and
  `GateOrchestrator.cs:37` is the **only** call site in the engine passing `includeHoldout: true` —
  measured, not assumed, so "a session cannot tune to it" is a fact about the call graph.
  **Regression** (`GateRunner.Classes.cs:21,117`) — a deleted test is a red gate even at exit 0.
  **Mutation** (`:73`) — a suite that runs and asserts nothing is a red gate at exit 0. Plus KS4.4's
  attempt worktree and the diff that excludes the engine's own commits.
- **The surfaces** — a third one, and the heading now says why it still reads "two". `mcp-observe`
  (`McpObserveCommand.cs:22`, `McpObserveServer.cs:24`) publishes resources and no tools;
  **`RunArchive.cs:24` opens with `Mode=ReadOnly`, so the refusal is SQLite's, not a policy check**.
  ATIF export at `Interop/AtifExport.cs:38`. And the messenger (KS11): `MessageComposer.cs:24`,
  `CommandRouter.cs:76`, `ChatProfile.cs:11`, with `ChatProfiles.TryParse` (`:46`) refusing an
  unknown profile **by name at plan load**.

## 4. `docs/dev/README.md` — the index had stopped indexing

- **ADR-0007 was not listed.** It has existed since KS8.1; the reference row stopped at 0006.
- **`TOKEN-BUDGET-TUNING.md` was in no table at all** — the document a new plan compiles against was
  reachable only by knowing it was there.
- `EDGE-TRACKER.md`, `CHAPAR-REMOTE-SURFACE-2026-08-18.md` and `GITHUB-SYNC-DESIGN-2026-08-13.md`
  added; the core tracker's row still said "Conductor drives itself with it", which stopped being
  true when core shipped.
- The KS10.3 paragraph still called karvansara-edge **"unauthored"**. Corrected in place, with the
  one thing the next session needs from it: **KS12.3 performs the move**, both trackers and the brief
  together, and nothing may move earlier because `edge.plan.json` names `EDGE-TRACKER.md` as its
  `tracker` and the plan's `readOrder` names the brief by path.

## 5. How to re-run this

The audit is a short script and it is worth keeping. For every `Foo.cs:NN` in ARCHITECTURE.md it
resolves the file (four root prefixes, then a basename walk under `src/`, `face-go/`, `tests/`,
`tools/`) and prints, tab-separated: the doc line number, the citation, **the actual source line at
that number**, and the doc's own sentence. A citation whose source line does not contain the symbol
the sentence names is the finding.

    import re, os
    doc = 'ARCHITECTURE.md'
    lines = open(doc, encoding='utf-8-sig').read().splitlines()
    pat = re.compile(r'([A-Za-z0-9_./-]+[.](?:cs|go|ps1|json|md)):(\d+)')
    roots = ['src/Conductor.Core/', 'src/Conductor/', 'src/', 'face-go/',
             'tests/Conductor.Tests/', '']
    def resolve(p):
        for r in roots:
            if os.path.isfile(r + p): return r + p
        base = os.path.basename(p)
        for top in ('src', 'face-go', 'tests', 'tools'):
            for dirp, _, files in os.walk(top):
                if base in files: return (dirp + '/' + base).replace(os.sep, '/')
        return None
    for i, l in enumerate(lines, 1):
        for m in pat.finditer(l):
            p, ln = m.group(1), int(m.group(2))
            path = resolve(p)
            if not path:
                print(i, p + ':' + str(ln), 'NOFILE', l.strip()[:150], sep='\t'); continue
            src = open(path, encoding='utf-8-sig', errors='replace').read().splitlines()
            act = src[ln-1].strip() if 0 < ln <= len(src) else 'OUT-OF-RANGE'
            print(i, p + ':' + str(ln), act[:70], l.strip()[:130], sep='\t')

This is the **second era in a row** where the semantics had not moved and the line numbers had —
KS10.1 recorded the same thing about the rollback paragraph. That is the argument for running this
mechanically at every era close rather than re-reading the prose and believing it.
