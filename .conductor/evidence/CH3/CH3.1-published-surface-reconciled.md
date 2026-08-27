# CH3.1 - the published surface, reconciled against a binary rather than against intent

Measured 2026-08-27, session 4, on `feat/charkh` at parent commit `ac60b4c`.

## What was measured, and how to re-run it

Two binaries, the same eight documents, a mechanical diff in both directions:

```
tools\ch3\dump-help.ps1 -Exe <conductor.exe> -OutDir <dir>      # the binary's whole CLI surface
python tools/ch3/docs-surface-diff.py <dir> README.md docs/cli.md docs/operating.md \
    docs/plan-config.md docs/quickstart.md docs/troubleshooting.md docs/tracker.md docs/README.md
```

| Binary | What it is | Where its help was dumped |
|---|---|---|
| `conductor` on PATH | the INSTALLED engine, `0.5.0+e60ae79c92dc` - what a reader has today | `%TEMP%\ch31-help-installed` |
| `src/Conductor/bin/Debug/net10.0/conductor.exe` | a fresh build of this tree - what the next release ships | `%TEMP%\ch31-help-fresh` |

The dump takes each verb's help AND each sub-verb's, and the diff reads flags only out of a help's
`OPTIONS` block - a verb's *description* quotes other verbs' flags (`journey` names
`conductor run [--paused]`), and scanning the whole text attributes `--paused` to `journey`.
Doc side, the unit is a **block**: one table row, one fenced listing, or one paragraph. That matters -
a paragraph naming eight verbs on one line and the flag on the next is one statement, not two.

Raw output, committed beside this file:

- `CH3.1-surface-diff-tree.txt` - against the fresh build. **exit 0.**
- `CH3.1-surface-diff-installed-v0.5.0.txt` - against the installed engine. **exit 1**, by design; see section 3.

## 1. What the measurement found, before the fixes

```
== UNDOCUMENTED: binary parses a flag no doc names for that verb ==
  budget --home . catalogue --home . face --timeout . github --home
  history --home --output . mcp-serve --repo . money --home . new-plan --repo
  ps --ports --timeout . run --home . watches --timeout
== counts == verbs=52 stale=0 misplaced=4
```

Thirteen flags across eleven verbs. **The existing battery cannot see these.**
`SF7_1DocsMatchRealityTests.Flags.cs:33` asks whether `docs/cli.md` names each declared long
option *anywhere in the document* - so `--home`, named on the `spend` and `mcp-observe` rows,
counted as documented for the six other verbs that declare it and whose rows never mentioned it. The
per-verb question is strictly stronger and it is the question a reader actually asks. All thirteen
are now closed; the diff against the fresh build is clean.

## 2. The one flag the docs offered that no binary parses

`docs/operating.md:84` read:

    | `report [--query <SQL>]` | Regenerate `REPORT.md`; `--query` runs read-only SQL over `run.db`. |

Measured, through the fresh build:

    > conductor report --query "select 1"
    error: Unknown option 'query'.
    exit 1

It was real once - `docs/history/archive/trackers/CONDUCTOR-VNEXT-PLAN.md:43` records F1.4 shipping
`ReportCommand --query <SQL>`, and `SARBAN-FACE-TRACKER.md:50` records **SF1.2 deleting it** with
the Dev SQL console. The operating guide kept offering it for two eras. Corrected to say what
replaced it (the MCP `run_query` tool, which `conductor chat` reaches).

## 3. Why the installed-engine run exits 1, and why that is the right answer

```
== STALE: the docs write a flag NO conductor verb declares ==
  docs/cli.md:347       --branch   | `github ci [--repo owner/name] [--branch <branch>]` |
  docs/operating.md:145 --branch   | `github ci [--repo owner/name] [--branch <branch>]` |
```

This is the whole delta between the two binaries - measured, not assumed:

    fresh-build flags the installed engine does not have:  github --branch   (and nothing else)
    installed `github --help`  VERB argument: sync, sarif
    fresh     `github --help`  VERB argument: sync, sarif, ci

`github ci` is CH1.3's verb. It is in master and it is not in `v0.5.0`, so both rows now open with
**"New since `v0.5.0`; not in the released binary yet"** instead of the internal checkpoint id
`CH1.3`, which told a public reader nothing. The tool still reports it against the installed engine,
correctly - the binary has not changed. The doc now explains the gap instead of hiding it.

## 4. The GIF caption, stale since CH2.1 landed the day before

README's alt text and caption still described the old tour: *"Seven screens ... home, agent transcript,
work board, card detail, timeline, plan editor, command palette"*. Derived from
`docs/assets/demo.manifest.json` - the file CH2.2 made the recorder write - the tour is **nine
stops**: Home, the owner queue (`w`), Agent, Kanban and a card, History, Report, **Telegram**,
Plan, and the command palette with the **run switcher**. The courier's tab and the inbox pane, the two
surfaces CH2.1 was added for, were the ones the caption omitted. Rewritten from the manifest, which is
now linked from the caption.

## 5. The courier, described as what it is

The stage note is right, and the specific failure is worse than framing.
`src/Conductor.Core/Courier/CourierSettings.cs:51`: *"The chats it answers. A message from anywhere
else is not replied to and not filed"*. README's three-line setup snippet led with
`courier install` and never mentioned `courier chat --id` - so a reader who follows the front page
exactly ends up with a scheduled task that starts at logon, restarts on failure, and **answers
nobody**. The snippet now leads with the two lines that decide who and what it serves, says the
courier is this machine's one Telegram consumer, and says in plain words that a courier installed
without them runs perfectly and is silent.

Measured on this machine (read-only, `conductor courier status`): task `Conductor Courier`
registered, `running: yes pid 33884 - protocol 2 - since 2026-08-26 22:04:43Z`, four projects
allowed, one admin chat.

## 6. Findings recorded rather than fixed here

- **bug #87** - `courier status` prints `running: yes pid 33884` and then, as its last verdict
  line, `ready - conductor courier run starts polling.` `CourierCommand.cs:200-203` branches on
  `Blocker()` alone and never consults the presence it just printed. Engine, not docs.
- **bug #88** - `CHANGELOG.md:21` `[Unreleased]` still says *"Nothing yet"* with CH1.1-CH2.2 in
  master, and `CHANGELOG.md:9` says the release workflow uses that section as the release body.
  Left to CH4.1, which owns the CHANGELOG precondition by design.
- `docs/dev/workgraph/W5-REHEARSAL.md:25` still tells a reader to read `run.db` with
  `conductor report --query` - its own line 174 records that SF1.2 deleted the option. Internal doc,
  so it belongs to **CH3.2**'s sweep rather than here.
- `docs/` has **mixed line endings** - `docs/operating.md:84` ends CRLF while README's caption
  block is LF. Nothing here depends on it, but it is the CH1.1 class of problem one directory over.

## 7. Verification

- `dotnet build Conductor.slnx -clp:ErrorsOnly` - Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test Conductor.slnx --filter FullyQualifiedName~Docs` - **Passed! 45/45**, after the edits.
- `docs-surface-diff.py` against the fresh build - stale 0, undocumented 0, exit 0.
- The four remaining `MISPLACED` rows are reviewed heuristic misses, not doc defects: two are the
  agent CLI's `--model` in plan-config prose, one is `run --max-sessions` cited on the
  `maxSessions` schema row, one is `run --once` cited in a quickstart bullet about stages. The
  tier exists so they are visible without making the exit code lie.
