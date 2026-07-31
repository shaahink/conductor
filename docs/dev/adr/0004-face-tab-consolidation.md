# ADR-0004 — The Face consolidates twelve tabs into ten

- **Status:** Accepted (SF1.3)
- **Date:** 2026-08-01
- **Deciders:** Sarban face self-plan, session #7 (stage SF1)
- **Context source:** `docs/history/CONDUCTOR-SARBAN.md` §SF1; `face-go/STYLE.md`;
  owner brief — *"we got a few tabs for observability and report; some might merge and be consolidated"*.

## Context

The v3 dashboard's founding rule is **"never add a full-screen modal for a view again — add a tab"**
(`face-go/STYLE.md`). It worked: every modal died. It also had no brake, so the tab strip grew to
thirteen. SF1.2 deleted the Dev/SQL tab and left twelve. Twelve is still more surfaces than a person
holds in their head, and the strip stops rendering full names well before it runs out of tabs.

The owner's complaint is not "too many tabs" in the abstract — it is that **some of them answer the
same question**. Two pairs do:

| pair | why they are one question |
|---|---|
| **Agent** and **Console** | Both render the running agent's output. Agent parses the JSONL stream into events; Console shows the same session's raw stdout. You do not go to a different *place* to see raw output — you want the *same* stream, undecorated, when the parser is hiding something. |
| **Sessions** and **Timeline** | Both are the run's history. A session **is** a timeline span: `/timeline`'s `session` entries carry the very `SessionNumber` that `/sessions` lists. Two tabs, one chronology, and no way to get from an event to the session it belongs to. |

The remaining eight each answer a question no other tab answers (where am I, what does the plan say,
what is running on this box, what did the run cost, what did agents learn, what can I edit, what is
on the board, is Telegram wired), so this ADR consolidates exactly two pairs and stops.

## Decision 1 — Console folds into Agent as a raw-stream toggle

`TabConsole` is deleted. `Model.agentRaw` selects what fills the Agent tab's body:

- **off** (default) — the parsed transcript, as today.
- **on** — the raw agent stdout tail (`data.RawConsole`), with the console pane's own scrolling
  (`consoleScroll`, `home`/`end`/`pgup`/`pgdn`) unchanged.

The **agent strip stays in both modes**. That is the point of folding rather than deleting: the strip
is mission control (session, checkpoint, gate chips, attention banner) and it is exactly the context
you lose today when you tab away to the console to read raw output. The footer (model/tokens/cost) is
suppressed in raw mode — the raw stream is a debugging surface, and the status line is the *parsed*
view's furniture.

`tab_console.go` is deleted and its two functions move into `tab_agent.go`, per STYLE.md's
one-file-per-tab rule.

## Decision 2 — Sessions and Timeline merge into one History tab

`TabSessions` and `TabTimeline` become `TabHistory`, with two views selected by `Model.historyView`:

- `historySessions` — the sessions list plus the selected session's detail (unchanged).
- `historyTimeline` — the run's spine, with its live-boundary rule and detail pane (unchanged).

Views switch with **`left`/`right`**, which is already this codebase's sub-section idiom (`planTab`
in `plan.go`, per STYLE.md "Plan sub-sections switch with left/right so tab stays free for main
tabs"). A view-switcher header names both views and marks the active one, so the second view is
discoverable rather than folklore.

Opening History, and switching *into* the timeline view, both kick `cmdFetchTimeline`. The
live-refresh-on-spine-event behaviour is preserved and scoped to the timeline view, so sitting on the
sessions list does not fetch the spine on every event.

`tab_sessions.go` and `tab_timeline.go` become `tab_history.go`.

## Decision 3 — A folded tab's mnemonic is NOT freed; it opens the fold

This is the decision that separates SF1.3 from SF1.2, and it is deliberate.

SF1.2 deleted the Dev tab and left `d` **unbound**, reasoning that `d` meant "the SQL console" and
quietly landing that user somewhere else is worse than a key that does nothing. That reasoning is
correct **for a deleted surface**. It is wrong here: `c` and `t` name surfaces that still exist —
they just live one level in. Leaving them dead would break working muscle memory to no end, and
"the raw console is gone" is a lie the Face would be telling.

So the two freed keys become **folded-tab aliases**, handled in `handleKey` alongside the `tabKey`
loop:

| key | before | after |
|---|---|---|
| `c` | open the Console tab | open Agent with the raw stream showing; **toggle** it when Agent is already the active tab |
| `t` | open the Timeline tab | open History on the spine view |
| `s` | open the Sessions tab | open History on the sessions view |

Every keystroke a user of this Face has learned still reaches the surface it always reached. `d`
stays unbound — the SQL console really is gone.

The aliases live in `foldedTabKey`, a declared map, not scattered `case` arms, so
`TestTabMnemonicsAreUnique` can pin `tabKey` and the aliases as **one** namespace: an alias that
collides with a tab mnemonic is unreachable in exactly the way a duplicate `tabKey` entry is.

## Decision 4 — Order, keys, and the digit row

Ten tabs, in the order a run is read (where am I, what is happening, what happened, then the
supporting surfaces):

| # | tab | key | digit |
|---|---|---|---|
| 0 | Home | `h` | `1` |
| 1 | Agent | `a` | `2` |
| 2 | History | `s` | `3` |
| 3 | Procs | `o` | `4` |
| 4 | Templates | `e` | `5` |
| 5 | Plan | `p` | `6` |
| 6 | Report | `r` | `7` |
| 7 | Knowledge | `k` | `8` |
| 8 | Telegram | `g` | `9` |
| 9 | Kanban | `b` | `0` |

**At ten tabs every tab has a digit for the first time.** `1`–`9` plus `0` covers exactly ten, so the
"the last N tabs are mnemonic-and-cycle only" caveat that has ridden the code and the help since the
tab strip outgrew the digit row is now false and is deleted rather than reworded. Ten is therefore
not an arbitrary target — it is the number of tabs this keyboard model can address, which is the
reason to stop here and the reason a future tab must fold into an existing one instead.

**History keeps `s`, not `h`.** `h` is Home, the landing page, and moving Home's key to give History
a first-letter match would break the one tab every user hits first. `s` and `t` are the two keys
people already press for these two views and **both still work** — which is worth more than a
first-letter match on a name.

## Consequences

- Every golden moves: the tab strip renders on every frame. Regeneration is a **separate commit**
  from the behaviour change (STYLE.md / trap 5), so a reviewer can read the code change without
  a hundred rebaselined frames in the same diff.
- `tabKey` is still the single source for tab mnemonics, but it is no longer the *whole* keyboard
  story — `foldedTabKey` is the second half, and the hand-maintained help legend must name both.
  `TestHelpLegendNamesEveryTabItsRealMnemonic` is extended to cover the aliases for that reason.
- `c` and `t` are global again (they were global as tab mnemonics), so no pane key changes hands.
  The Kanban card detail's `t`/`c` keep working because that sub-state owns every key
  (`tabHandlesAllKeys`).
- Raw output loses its own tab from the strip. That is a real discoverability cost, paid down by the
  help card's folded row, by the Agent tab's contextual help line naming `c`, and by `c` still doing
  what it always did.
