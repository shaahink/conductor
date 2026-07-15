# conductor-face — Go UI style (v3 "dashboard")

The design language every future face-go change should follow. This replaced the v2 modal-heavy
build after the owner's redesign brief: *dashboard, not modals; sidebar always visible; everything on
one page; fewer clicks; transparent overlays; better colour and spacing.*

## Layout — a persistent dashboard

```
┌ Top bar ─ brand · connection · plan · stage · live cost/tokens ───────────────┐  row 0 (mantle bg)
│ Tab strip ─ Agent Sessions Timeline Procs Console Templates Plan Report        │  row 1 (mantle bg)
│ Sidebar (always on, collapsible with p) │ Content pane (the active tab)        │  rows 2 … H-2
│ Bottom bar ─ key hints, OR the command line (: / i / /)                         │  row H-1 (mantle bg)
└────────────────────────────────────────────────────────────────────────────────┘
```

- **One page.** Everything the old build hid behind a modal is a **tab** in the content pane, one
  keypress away (`a h t s c e g r`, or `1–8`, or `tab`/`shift+tab`). The plan **sidebar is always
  visible** beside it (collapse with `p`). Never add a full-screen modal for a view again — add a tab.
- **One file per tab.** Each tab's key handler + renderer live together in `tab_<name>.go` (the plan
  editor in `plan.go`); the palette/inject/search/help layer is `cmdbar.go`; `view.go` only assembles
  the frame; `update.go` is only the message loop + global routing. Add a tab by creating its
  `tab_*.go` and extending `MainTab`, `tabNames`, `tabKey`, `paneView`, and `handleTabKey`.
- **The Agent tab is mission control.** A status strip (session · attempt · current checkpoint ·
  gate chips · live MCP-task progress · elapsed, plus a red attention banner with the engine's
  reason) sits above the transcript — "what is happening right now" never needs a tab switch.
- **The sidebar is glanceable, not focusable.** Plan tree + gates + live tasks, for context. All
  *interaction* happens through tabs and the command bar — there is no sidebar "focus", by design.
- **Alive, cheaply.** The top bar shows a braille spinner + live session cost/elapsed while (and only
  while) the engine reports an active agent (`widgets.Spinner`, armed by `MsgSpinnerTick`); the
  Timeline refreshes itself whenever a spine event lands while it's open. Never poll for animation —
  arm a ticker when something is genuinely in flight and let it die when it isn't.

## Overlays — transparent, composited, rare

- Only three things float: the **command palette** (list above the bottom bar), the **help** card, and
  **toasts**. Everything else is a tab.
- Float with the lipgloss v2 **compositor**, never `lipgloss.Place` (which is opaque):
  `compositeAt / compositeCenter / compositeBottomRight` in `view.go` layer the box over the live
  dashboard so the background stays visible — that is the "transparent modal" the owner asked for.
- Transient text input (palette query, inject, goto, search, destructive confirm) is a **bottom
  command bar** (`CmdMode` + `renderBottomBar`), not a boxed modal.

## Keys — direct, few clicks

- `a h t s c e g r` jump straight to a tab; `1–8` and `tab`/`shift+tab` also switch. `esc` from any
  browse pane returns to Agent.
- A tab that is *editing text or in an interactive sub-state* owns every key
  (`tabHandlesAllKeys`); otherwise the dashboard globals (`:` `i` `/` `p` `?` `q`, tab switches) win.
  Plan sub-sections switch with `←/→` (so `tab` stays free for main tabs).

## Colour — Catppuccin Mocha, one scheme

Defined once in `widgets/style.go`; the tui package pulls it via the exported accessors
(`widgets.Accent()`, `.Green()`, `.Overlay()`, …). **Never hardcode a hex in a pane.** Roles:

| token | hex | use |
|-------|-----|-----|
| accent (mauve) | `#CBA6F7` | brand, selection, active tab, keycaps |
| blue | `#89B4FA` | active/in-progress |
| green | `#A6E3A1` | success / done |
| red | `#F38BA8` | fail / destructive |
| yellow | `#F9E2AF` | warn / running |
| peach | `#FAB387` | cost / attention |
| teal | `#94E2D5` | tools |
| text `#CDD6F4` · overlay `#6C7086` · surface `#313244` · mantle `#181825` · base `#1E1E2E` | | |

## Spacing & alignment

- Panels breathe: content `Padding(1,2)`; the sidebar has a single right border in `surface`.
- **Never `%-Ns` a styled (ANSI-wrapped) string** — it pads the escape bytes and misaligns. Pad the
  plain text first, *then* colour it, or render the whole plain row and apply one style. (This bit the
  sidebar, plan rows, sessions rows, and palette — all fixed this way.)
- Truncate ANSI-safely with lipgloss `MaxWidth`/`Width`, or rune-slice plain text; never byte-slice a
  styled string.
- **lipgloss v2 counts borders inside `.Width()`**: a panel with a right border + `Padding(1,1)` has
  a content area of `width−3`, not `width−2`. Get this wrong and rows silently word-wrap, pushing
  everything below them down (this bit the sidebar at 80 cols). Belt-and-braces: hard-clip each row
  with `MaxWidth` before joining, and drop whole meta tokens (`$0.30`) rather than clipping them
  mid-value.

## Semantics worth keeping

- **Scrollback counts from the tail.** `TranscriptModel.ScrollOffset` is lines back from the live
  tail (0 = pinned); one `↑` steps into history, `end`/`l` re-pins, and a scrolled pane shows a
  "↕ N lines below" note *inside* its height budget. Never use offset-from-top for a live stream —
  the first keypress teleports to the top of a 4000-line buffer.
- **Wire order is the truth.** `GET /sessions` is newest-first (`ORDER BY number DESC`); the demo
  source and golden fixtures must mirror the real wire, not an idealised one.
- Transcript lines carry a dim UTC `HH:MM:SS` prefix when the pane is ≥70 cols and the line has a
  timestamp; search matches are painted `yellow`-on-`base` in place.

## Tests — golden + interaction, no TTY needed

- `golden_test.go` drives real `Msg`/key updates against `fakeSource` and diffs `View()` against
  `testdata/golden/*.golden`. Add a scenario as a `{name, do}` case; run `-run TestGolden -v` to *see*
  frames, `-update` to refresh after an intentional change (always read the frame first).
- `update_test.go` / `plan_test.go` exercise the routing through the exported handlers.
- Every pane must render cleanly at 80×24 / 120×30 / 200×50 (`TestGoldenSizes`).

## Running

- **Demo:** `conductor-face --demo` — the whole dashboard, offline, no spend.
- **Live:** just `conductor-face` inside a repo with a live `conductor run --control-plane`; it
  auto-discovers `.conductor/control-plane.json` (walks up from cwd). `--url` overrides.
