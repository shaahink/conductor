# conductor-face — Go UI style (v3 "dashboard")

The design language every future face-go change should follow. This replaced the v2 modal-heavy
build after the owner's redesign brief: *dashboard, not modals; sidebar always visible; everything on
one page; fewer clicks; transparent overlays; better colour and spacing.*

## Layout — a persistent dashboard

```
┌ Top bar ─ brand · connection · plan · stage · live cost/tokens ───────────────┐  row 0 (mantle bg)
│ Tab strip ─ Agent Sessions Timeline Procs Console Templates Plan Report Knowl…  │  row 1 (mantle bg)
│ Sidebar (always on, collapsible with \) │ Content pane (the active tab)        │  rows 2 … H-2
│ Bottom bar ─ key hints, OR the command line (: / i / /)                         │  row H-1 (mantle bg)
└────────────────────────────────────────────────────────────────────────────────┘
```

- **One page.** Everything the old build hid behind a modal is a **tab** in the content pane, one
  keypress away. There are **thirteen** tabs — Home Agent Sessions Timeline Procs Console Templates Plan
  Report Knowledge Telegram Kanban Dev — reached by `h a s t o c e p r k g b d`, or by `1`–`9`/`0` (the
  last three, Telegram, Kanban and Dev, have no digit — mnemonic and tab-cycle only), or
  `tab`/`shift+tab`. The plan
  **sidebar is always visible** beside it (collapse with `\`). Never add a full-screen modal for a view
  again — add a tab (and extend `tabNames`/`tabKey`, both length `tabCount`). The tab strip is adaptive:
  full names when they fit, key-only (active tab keeps its name) when they don't — never clipped.
- **A tab mnemonic is a GLOBAL key — check it is free before claiming it.** `handleKey` runs the
  global switch and the `tabKey` loop *before* the active pane's handler whenever `tabHandlesAllKeys()`
  is false, so a letter taken by a tab is taken from every pane that isn't in an owning sub-state.
  Adding Dev on `d` (U2.2) silently made the Plan editor's delete unreachable; it moved to `x`, which
  is also the Procs tab's kill key — **`x` is this codebase's destructive-with-`y/N` mnemonic**, reuse
  it rather than inventing another. `TestTabMnemonicsAreUnique` pins uniqueness, but a collision with a
  *pane* key it cannot see: when adding a mnemonic, grep the pane handlers for that letter, and pin it
  through `handleKey` (never through the pane handler directly — that bypasses the precedence that
  breaks it, which is exactly why `plan_test.go`'s `drive()` never caught this).
- **Report is rendered, never queried.** `TabReport` is the owner's run report (U2.2) — header,
  progress, stages, sessions digest, gates, verifier scores (`/scores`, SF1.1), per-session token/cost
  — from typed endpoints the Face already polls, with scroll as its only interaction. There is no SQL
  anywhere in the Face: SF1.2 deleted `TabDev`, `QueryReport` and `GET /report/query` on the owner's
  "delete this stupid sql query report and its traces". Ad-hoc SQL against run.db is the MCP
  `run_query` tool's job, behind `conductor chat` — not a Face surface.
- **A deleted surface's honest panels get re-homed, not deleted with it.** Dev's two non-SQL panels
  went where their question already lives: the wiring internals (token presence, seq, poll, run id) to
  Home's `Wiring` section, the per-session token/cost table to the bottom of Report. Home cannot
  scroll, so anything added to it declares a shed tier — and goes in a section positioned so it sheds
  before rows Home already showed (`fitHome` sheds from the last section backwards).
- **Home is the landing.** `TabHome` is index 0 and the tab the Face opens on (U1.1): where am I, what
  is running, in which directory, what does it cost, what next — answered before a keypress, from the
  `/state` + `/plan` the Face already polls. It fetches nothing of its own and owns no keys. `esc` still
  returns to Agent: Home is where you arrive, Agent is where the work is.
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

- `h a s t o c e p r k g b` jump straight to a tab; `1`–`9`/`0` and `tab`/`shift+tab` also switch. `esc`
  from any browse pane returns to Agent. `d` is deliberately unbound since SF1.2 deleted the Dev tab:
  it meant "the SQL console" to anyone who used this Face, and landing them somewhere else instead is
  worse than a key that does nothing.
- **Text entry uses one editor, not append-only strings.** `widgets.TextArea` (real caret:
  left/right/up/down, home/end, insert/delete mid-string, pgup/pgdn) backs the template editor;
  short single-line fields (goto, inject, plan/telegram field edits, knowledge
  note/bug) may still use the light `typedChar` append path, but anything multi-line or long-lived
  should use `TextArea` so a typo in the middle is fixable.
- A tab that is *editing text or in an interactive sub-state* owns every key
  (`tabHandlesAllKeys`); otherwise the dashboard globals (`:` `i` `/` `p` `?` `q`, tab switches) win.
  Plan sub-sections switch with `←/→` (so `tab` stays free for main tabs).

## Colour — roles, not hexes

Defined once in `widgets/style.go`; the tui package pulls it via the exported accessors
(`widgets.Accent()`, `.Green()`, `.Overlay()`, …). **Never hardcode a hex in a pane** — name the
ROLE. Since U3.1 that rule has teeth: a pane that hardcodes `#CBA6F7` is correct in mocha and wrong
in the other three schemes.

| role | use | mocha (the default) |
|-------|-----|-----|
| accent | brand, selection, active tab, keycaps | `#CBA6F7` |
| blue | active / in-progress | `#89B4FA` |
| green | success / done | `#A6E3A1` |
| red | fail / destructive | `#F38BA8` |
| yellow | warn / running | `#F9E2AF` |
| peach | cost / attention | `#FAB387` |
| teal | tools | `#94E2D5` |
| sky | system | `#89DCEB` |
| text | primary text | `#CDD6F4` |
| overlay | muted text | `#6C7086` |
| pending | todo / not reached | `#585B70` |
| skipped | skipped / thinking | `#7F849C` |
| surface | borders, rules | `#313244` |
| selection | selection background | `#45475A` |
| mantle | top/bottom bars, tab strip | `#181825` |
| base | window background | `#1E1E2E` |

## Themes (U3.1)

Four curated schemes: **mocha** (Catppuccin Mocha, dark, the default), **latte** (Catppuccin Latte,
light), **nord**, **gruvbox**. A scheme is a `widgets.Theme` — the sixteen roles above and nothing
else — so **adding one is a new entry in `widgets.themes` and no pane changes**. It gets its palette
row, its `--theme` value and its help legend for free: all three are derived from that registry.

- **Selecting:** `--theme <name>` overrides for one launch (a bad name is a hard error — the user
  named something specific). The palette's **Face** group (`:` then `theme`) switches live *and*
  persists to `os.UserConfigDir()/conductor-face/config.json`; a stale name in that file falls back
  to mocha rather than refusing to start. The flag deliberately does **not** write the config —
  otherwise one `--theme x` would destroy the saved choice with no way back to it.
- **Switching is TWO rebuilds, one per package.** `widgets` and `tui` each own a block of shared
  style vars, and a lipgloss style captures its colour **by value** at construction — so swapping the
  palette alone leaves those vars painting the old scheme. Each package therefore has exactly one
  rebuild function, and `tui.ApplyTheme` is the only caller of both. **Rebuild one and the frame
  renders in two themes at once.** Styles built *inside* a render func (`lipgloss.NewStyle().
  Foreground(widgets.Blue())`) need no rebuild — they read the live palette every frame. That is the
  cheap way to add colour to a pane, and the reason most of the tui package needed no changes.
- **Goldens pin mocha**, so a theme test must restore the default (`t.Cleanup`) or it turns the
  golden suite red from a distance.
- **A new scheme must clear `TestEveryThemeIsLegibleOnItsBase`** — text ≥4.5:1, semantics and
  overlay ≥3:1, quiet roles ≥1.5:1 and ordered (pending recedes furthest). This is not decoration:
  the active tab paints `Base` **on** `Accent` and a search match paints `Base` **on** `Yellow`, so a
  scheme whose yellow sits near its base renders invisible matches. Stock Catppuccin Latte does
  exactly that (2.3:1) — which is why the shipped `latte` darkens green/yellow/peach/teal/sky in-hue.
  Catppuccin tunes those for syntax highlighting; this Face paints them as status text and as fills.

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
- **Sizes.** `TestGoldenSizes` pins the DEFAULT tab at 80×24 / 120×30 / 200×50 (the M5 truth gate —
  200×50 is the only wide coverage there is). `glitch_sweep_test.go` (U3.2) adds **every tab** at
  132×40 / 100×30 / 80×24 under worst-case state, which is the axis nothing covered: every size test
  before it rendered one tab. The two are additive on purpose — neither set is redundant.
- **A golden proves a frame is UNCHANGED, never that it is CORRECT.** `size_80x24.golden` pinned a
  Home page whose entire Next steps section had been clipped away since the day it was written, and
  matched itself happily forever. Read the frame before `-update`, and for anything the height clamp
  can silently eat, prefer an *invariant* (does the body fit `paneRows()`?) over a pinned frame.
- **Drive tabs through `handleKey`, and actually switch to the one you are testing.**
  `TestFrameNeverExceedsWindowHeight` built 30 multi-paragraph transcript events as its worst case
  and asserted against **Home**, because `newGoldenModel` opens there and nothing switched — the
  regression test for the overflow bug could not have exhibited the overflow bug. Same family as
  `plan_test.go`'s `drive()` calling pane handlers directly and missing global key precedence.

## Running

- **Demo:** `conductor-face --demo` — the whole dashboard, offline, no spend.
- **Live:** just `conductor-face` inside a repo with a live `conductor run --control-plane`; it
  auto-discovers `.conductor/control-plane.json` (walks up from cwd). `--url` overrides.
