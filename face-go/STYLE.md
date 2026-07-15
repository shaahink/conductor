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
- **Content lives in `paneView`** (`view.go`): each tab returns `(body, help)`. The body is framed with
  `Padding(1,2)` for breathing room; the help shows in the bottom bar. Add a tab by extending
  `MainTab`, `tabNames`, `tabKey`, `paneView`, and `handleTabKey`.
- **The sidebar is glanceable, not focusable.** Plan tree + gates, for context. All *interaction*
  happens through tabs and the command bar — there is no sidebar "focus", by design (less clicking).

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
  sidebar, plan rows, and palette — all fixed this way.)
- Truncate ANSI-safely with lipgloss `MaxWidth`/`Width`, or rune-slice plain text; never byte-slice a
  styled string.

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
