# ADR 0006 — TUI conventions: one scroll idiom, one key namespace, one markdown renderer

- **Status:** Accepted
- **Date:** 2026-08-05
- **Decided in:** Karvan core, K6.1
- **Implemented by:** K6.2 (viewport + bubbles), K6.3 (per-tab models), K6.4 (markdown + remaining swaps)
- **Amends:** [0004](0004-face-tab-consolidation.md), which settled *which* tabs exist; this settles how a tab behaves

## Context

`face-go/go.mod` requires bubbletea v2, lipgloss v2, glamour, harmonica and `x/term`. **`bubbles` is
not a dependency at all**, so every widget it ships is re-implemented here — with its own bugs, and
without agreeing with its neighbours. The four hand-rolled scroll handlers bind four different key
sets:

| surface | line | page | top / bottom | polarity |
| --- | --- | --- | --- | --- |
| Report (`tab_report.go:37`) | `↑`/`k`, `↓`/`j` | `pgup`/`pgdown` | `home` only — **no `end`** | offset from top |
| Owner queue (`tab_home_owner.go:138`) | `↑`/`k`, `↓`/`j` | `pgup`/`pgdown` | `home` only — **no `end`** | offset from top |
| Knowledge (`tab_knowledge.go:43`) | `↑`, `↓`/`j` | **none** | **none** | offset from top |
| Agent raw (`tab_agent.go:100`) | `↑`/`k`, `↓`/`j` | `pgup`/`pgdown` | `home` = oldest, `end` = live | **inverted**, counts back from the tail |

Knowledge — the surface the owner reports as unreadable — cannot page, cannot jump, and does not even
answer `k`, because `k` is the Knowledge *tab* mnemonic and the mnemonic loop (`update.go:608`)
resolves before any pane handler.

Worse, none of these clamp. `m.reportScroll++` (`tab_report.go:46`) is unbounded in `Update`; the only
clamp is a **local copy inside the renderer** (`tab_report.go:83`, `scroll := min(m.reportScroll,
maxScroll)`) which is never written back and — `View` having a value receiver — cannot be. Measured
against `newGoldenModel(120, 30)`: the Report body stops changing at offset 12, but 400 `down` presses
leave `reportScroll` at 400, and **389 `up` presses were then needed before one line moved**. Two
seconds on the arrow key past the end of a document freezes the pane for the next four hundred
keystrokes. That is not a rendering glitch; it is a missing clamp, four times over. (Bug #30.)

## What we read

- **glow** — `viewport` + `glamour`, the canonical long-markdown pager. Glamour runs in a `tea.Cmd`
  returning `contentRenderedMsg`, never inline in `View` (`ui/pager.go:410`, `:248`); resize
  *re-renders* rather than merely resizing, because wrap width is baked into the render (`:269`);
  height is `h - statusBarHeight` with `statusBarHeight = 1` (`:123`); position is a clamped
  **percent**, not a line count (`:309`). Notably glow uses **neither `bubbles/key` nor
  `bubbles/help`** — raw `switch msg.String()` and hand-concatenated help strings (`:189`, `:368`).
- **soft-serve** — every pane is its own model behind `Component{Model; help.KeyMap; SetSize(w, h)}`
  (`pkg/ui/common/component.go:9-41`); the root holds `panes []TabComponent` + `activeTab int`,
  forwards `WindowSizeMsg` to **all** panes but keys only to the active one
  (`pkg/ui/pages/repo/repo.go:182`, `pkg/ssh/ui.go:152`); focus is a plain int enum, not a stack; help
  composes upward from the focused child (`repo.go:130`); long content is one wrapped
  `charm.land/bubbles/v2/viewport` (`pkg/ui/components/viewport/viewport.go`).
- **gh-dash** — the closest analogue to our Kanban and Report: `[]Section` + an int cursor
  (`internal/tui/ui.go:59`), a dedicated `keys` package of `key.Binding` fields implementing
  `help.KeyMap` per view (`keys/keys.go:30`, `:53`), `bubbles/help` in the footer — and a
  **hand-rolled table**, explicitly not `bubbles/table`, to get `Grow` columns, multi-line cells and
  per-row styling (`components/table/table.go:19`, `:308`) over a real viewport.
- **lazygit** — panel focus is directional and numbered, never one cycle key (`<tab>`/`<backtab>`,
  `h`/`l`, `1`-`5`); item movement (`j`/`k`) is a *different key set* from scrolling the main content
  pane (`<pgup>`/`K`/`<ctrl+u>`); `<`/`>` are scroll-to-top/bottom; help is a modal opened by `?`
  built from the focused context's bindings (`pkg/config/user_config.go`,
  `docs/keybindings/Keybindings_en.md`).

## Decisions

### 1. One scroll idiom: `bubbles/viewport`, and the offset lives in `Update`

Any body that can outgrow its pane is a `viewport.Model` owned by the surface that renders it. No
surface keeps a bare scroll integer. The rule this encodes, and the reason the bug class dies: **the
offset is clamped where it is changed, never in the renderer.** `View` stays pure and may not be the
only thing standing between the model and an impossible offset.

`bubbles/v2` resolves to **v2.1.1** (`go list -m -versions charm.land/bubbles/v2`), the `charm.land`
path matching bubbletea v2 — the same module soft-serve imports.

Position is shown as a **percent** (glow `pager.go:309`), not a line count, in the pane's existing
status line. Live-tail surfaces (Agent raw) keep tail-anchored semantics but express them as
`GotoBottom` + an at-bottom test, not as an inverted offset.

### 2. The pane-scroll key set, and the namespace rule that shapes it

**Tab mnemonics resolve before pane keys** (`update.go:608`), and the comparison is an exact string
match, so lowercase letters in `tabKey` / `foldedTabKey` are unreachable inside a pane and uppercase
letters are free. `tabKey` is `{h a s o e p r k g b}` and `foldedTabKey` is `{c t}`; therefore
soft-serve's `g` (goto-top), `b` (page-up) and vim's `k` (line-up) **cannot** be adopted here. This is
not a preference, it is why Knowledge's `k` silently does nothing today.

The set every scrollable pane binds, and the only one:

| intent | keys |
| --- | --- |
| line down / up | `↓` `j` / `↑` |
| half page down / up | `d` / `u` |
| page down / up | `pgdn` `f` / `pgup` |
| bottom / top | `end` `G` / `home` |

`d` is free by SF1.2 and `u`, `f`, `G` were never bound. `k` is deliberately absent — it opens
Knowledge, everywhere, and always will. `TestTabMnemonicsAreUnique` is extended to treat these pane
keys as part of the same namespace, so a future tab mnemonic cannot silently eat one.

### 3. Focus: one focusable body per tab; `←`/`→` within, `tab` between

The Face already binds `tab`/`shift+tab` to tab navigation (`update.go:589`) and `←`/`→` to
sub-section switching (`historyView`, `planTab`). Keep both. Where a tab has two bodies, `←`/`→`
moves between them and focus is a plain int enum on that tab's model (soft-serve's `activePane`), not
a stack and not a focus manager. `WindowSizeMsg` goes to every tab; keys go only to the active one.

### 4. Bindings come from `bubbles/key`; the help *renderer* stays ours

Every binding is a `key.Binding` with its `WithHelp` text, and each tab implements `ShortHelp` /
`FullHelp`. This is gh-dash's model (`keys/keys.go:53`) and it kills the class of defect the code
already documents: the Tabs grid at `cmdbar.go:458-460` is hand-maintained, so a mnemonic changed in
`tabKey` and not there makes the help lie.

We adopt `bubbles/key` and **not** `bubbles/help`'s renderer. The help card is themed, laid out as a
grid, and pinned by `golden_test.go` and `help_size_test.go`; `help.Model`'s output is a different
shape and would churn those goldens for no reader benefit. glow is the precedent for a hand-rendered
help view; gh-dash is the precedent for a `key.Binding` source of truth. Take one from each.

### 5. `viewport` yes; `list` and `table` no

- **`viewport`** — every read-not-select body: Report, Knowledge, Agent raw, the owner-queue full
  view, Kanban detail, evidence.
- **`bubbles/list`** — not adopted. Our lists are dense single-line rows with per-row styling and no
  filter/title/pagination chrome; `list.Model` brings all of that and would fight the goldens.
- **`bubbles/table`** — not adopted, for the reason gh-dash gives by example: the dashboard closest to
  our Kanban and Report hand-rolls its table over a viewport to keep `Grow` columns, multi-line cells
  and per-row styling (`table/table.go:19`). Our tables stay lipgloss-composed; when they overflow,
  they scroll in a viewport like everything else.

`bubbles/textarea` replaces `widgets/editor.go` and `bubbles/spinner`/`progress` are considered only
where they do not disturb a golden; anything left is named in K6.4 rather than silently skipped.

### 6. One markdown renderer, in the active theme, off the `View` path

`renderMarkdown` (`markdown.go:14`) hard-codes `glamour.WithStandardStyle("dark")` (`:22`), ignoring
the theme system entirely, and has exactly **one** production call site (`tab_history.go:358`) — which
calls it *inside* `View`, re-running glamour every frame. Decision: one renderer, taking the active
theme, cached per (theme, width), used everywhere an agent wrote prose; rendering happens on content
or size change and the result is stored, following glow (`pager.go:410`, `:269`), never per frame.

## Consequences

K6.2 declares `charm.land/bubbles/v2` and moves Report and Knowledge onto a viewport, closing bug #30
by construction. K6.3 gives each tab its own model with its own `Update`/`View` — required, not
optional, because a viewport cannot live in a tab whose scroll state is an integer on a struct four
hundred lines away — and the mnemonic map and help legend change in the same commit. K6.4 does the
markdown unification and names anything left. Goldens are the regression net throughout, and any
baseline is regenerated in a **separate rebaseline commit**.

## Amendment — KS2.8: the reader

KS2.7 finished decision 1 (every scrollable body a viewport, ratcheted by `scroll_intent_test.go`);
KS2.8 adds the surface these decisions were pointing at all along — glow's actual job — as ONE
full-screen overlay, `internal/tui/reader.go`, opened with `z` over any cell the pane clips
(Kanban card blocks, bug details and ledger notes, session results and gate summaries, spine
descriptions, Telegram's delivery reason and poll error, a process's last output). Each decision
applies to it unchanged:

- **Decision 1**: the reader's body is a `viewport.Model` and the offset is clamped in `Update`
  (`readerViewport()` is a standard `<surface>Viewport()` builder; both the key handler and the
  renderer call it). Position is the same clamped percent, in the overlay's head.
- **Decision 2**: the pane-scroll set is the reader's ENTIRE key set, plus `esc`. Nothing else is
  bound, so no mnemonic, global, or surface key can leak through it — and `esc` returns to the
  exact sub-state it was opened from because opening it mutates nothing else.
- **Decision 6**: a markdown body goes through `renderMarkdown` — memoised, theme-projected —
  never a fresh `glamour.NewTermRenderer`.

One deliberate deviation: the reader's constructor (`newReaderViewport`) sets `SoftWrap = true`,
the only such viewport in the Face. The pane viewports carry pre-styled, pre-clipped rows, and
re-wrapping a styled row breaks its columns; the reader is handed raw prose (or glamour output
already wrapped to its width), and wrapping raw prose whole is the reason it exists. `z` joins the
tab-mnemonic namespace in `TestTabMnemonicsAreUnique`, and `truncationSites`
(`reader_test.go`) enumerates every clipping call site with its route to the full text, so a new
truncation cannot ship silent.
