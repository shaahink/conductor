package tui

import (
	"fmt"

	bkey "charm.land/bubbles/v2/key"
	"charm.land/bubbles/v2/viewport"

	"conductor-face-go/internal/widgets"
)

// The one scroll idiom (adr/0006 decision 1 and 2). Every body that can outgrow its pane is a
// viewport.Model owned by the surface that renders it, and every one of them binds THIS key set and
// no other.
//
// The rule the viewport encodes, and the whole reason bug #30 dies rather than gets patched: the
// offset is clamped WHERE IT IS CHANGED, never in the renderer. The Face used to do the opposite —
// `m.reportScroll++` was unbounded in Update and the only clamp was a local copy inside View
// (`scroll := min(m.reportScroll, maxScroll)`) that a value receiver could never write back. Measured
// at 120x30: the Report body stopped changing at offset 12, 400 `down` presses left the field at 400,
// and 389 `up` presses were then needed before one line moved. viewport.SetYOffset clamps on every
// mutation, so the offset can never leave the body in the first place.
//
// The bindings are key.Binding values (adr/0006 decision 4, gh-dash's model) so the help text comes
// from the same place as the match. A hand-written switch beside a hand-maintained legend is exactly
// how the Face's tab help came to lie.
var paneScrollKeys = struct {
	Down, Up, HalfDown, HalfUp, PageDown, PageUp, Bottom, Top bkey.Binding
}{
	// `k` is deliberately absent, and this is not a style preference. The mnemonic loop
	// (update.go:608) is an exact-string match resolving BEFORE any pane handler, so every lowercase
	// letter in tabKey is unreachable inside a pane: `k` opens Knowledge everywhere and always will.
	// That is why Knowledge's own `k` silently did nothing. Uppercase letters are free, hence `G`.
	Down: bkey.NewBinding(bkey.WithKeys("down", "j"), bkey.WithHelp("↓/j", "line down")),
	Up:   bkey.NewBinding(bkey.WithKeys("up"), bkey.WithHelp("↑", "line up")),
	// Half-page on d/u is lazygit's and vim's; `d` came free when SF1.2 deleted the Dev tab.
	HalfDown: bkey.NewBinding(bkey.WithKeys("d"), bkey.WithHelp("d", "half page down")),
	HalfUp:   bkey.NewBinding(bkey.WithKeys("u"), bkey.WithHelp("u", "half page up")),
	// "pgdown" is spelled the way bubbletea v2 spells it (uv.KeyPgDown); the ADR's "pgdn" is the same
	// key. The ADR also offered `f` as an alias on the belief that it "was never bound" — measurably
	// wrong: tab_agent.go:36 binds `f` to fold-tools and the help card's Actions row documents it with
	// no tab qualifier. One letter meaning two things in one legend is the drift this ADR exists to
	// kill, so `f` is dropped and page-down is the page key alone.
	PageDown: bkey.NewBinding(bkey.WithKeys("pgdown"), bkey.WithHelp("pgdn", "page down")),
	PageUp:   bkey.NewBinding(bkey.WithKeys("pgup"), bkey.WithHelp("pgup", "page up")),
	Bottom:   bkey.NewBinding(bkey.WithKeys("end", "G"), bkey.WithHelp("end/G", "bottom")),
	Top:      bkey.NewBinding(bkey.WithKeys("home"), bkey.WithHelp("home", "top")),
}

// paneScrollBindings is the set in help order — one list, so the uniqueness guard and any future help
// renderer read the same source the matcher does.
func paneScrollBindings() []bkey.Binding {
	k := paneScrollKeys
	return []bkey.Binding{k.Down, k.Up, k.HalfDown, k.HalfUp, k.PageDown, k.PageUp, k.Bottom, k.Top}
}

// bound answers "is this the key string for this binding". The pane handlers are still routed by
// string (update.go hands them msg.String()), so key.Matches — which wants a tea.KeyMsg — cannot be
// used until K6.3 gives each tab its own Update. Reading Keys() keeps ONE source of truth in the
// meantime; the alternative was a switch that drifts from the help text.
func bound(b bkey.Binding, k string) bool {
	if !b.Enabled() {
		return false
	}
	for _, want := range b.Keys() {
		if want == k {
			return true
		}
	}
	return false
}

// applyPaneScroll moves vp for one keypress and reports whether the key belonged to the scroll set.
// Every mutation goes through a viewport method, so every one of them is clamped.
func applyPaneScroll(vp *viewport.Model, k string) bool {
	switch {
	case bound(paneScrollKeys.Down, k):
		vp.ScrollDown(1)
	case bound(paneScrollKeys.Up, k):
		vp.ScrollUp(1)
	case bound(paneScrollKeys.HalfDown, k):
		vp.HalfPageDown()
	case bound(paneScrollKeys.HalfUp, k):
		vp.HalfPageUp()
	case bound(paneScrollKeys.PageDown, k):
		vp.PageDown()
	case bound(paneScrollKeys.PageUp, k):
		vp.PageUp()
	case bound(paneScrollKeys.Bottom, k):
		vp.GotoBottom()
	case bound(paneScrollKeys.Top, k):
		vp.GotoTop()
	default:
		return false
	}
	return true
}

// newPaneViewport is the constructor every scrollable pane uses, so none of them can disagree about
// wrapping or padding. SoftWrap stays off: these bodies are already clipped to the pane width by the
// renderer that builds them (STYLE.md — pad plain, style after), and re-wrapping a styled row would
// break its columns.
//
// The body lives in widgets.NewPaneViewport because the transcript widget owns a pane viewport too
// and `widgets` cannot import `tui`. This name stays, because this file is where a reader looking
// for the scroll idiom arrives.
func newPaneViewport() viewport.Model { return widgets.NewPaneViewport() }

// loadPaneViewport is the shared half of every `<surface>Viewport()` builder: size the viewport to
// the pane it is about to be drawn in, load the body that is actually on screen, and — for a body
// that grows at the BOTTOM — keep a reader who was following the live tail on it.
//
// The at-bottom test is taken BEFORE the new content lands, because that is the only moment at which
// "this reader was following the tail" is still a fact rather than an accident of the new body's
// height. That is adr/0006 decision 1's live-tail clause expressed the way the ADR asked for it:
// GotoBottom + AtBottom, never an inverted offset counting back from the tail. STYLE.md's warning
// still holds and is what `tail` exists for — a viewport that opened at offset 0 would teleport a
// live 4000-line buffer to its oldest line the moment the tab is opened.
func loadPaneViewport(vp viewport.Model, lines []string, w, h int, tail bool) viewport.Model {
	atBottom := vp.AtBottom()
	vp.SetWidth(max(1, w))
	vp.SetHeight(max(1, h))
	vp.SetContentLines(lines)
	if tail && atBottom {
		vp.GotoBottom()
	}
	return vp
}

// ensurePaneRow keeps a SELECTION visible in a pane that scrolls. History, Processes, Telegram and
// the Plan lists all move a cursor with ↑/↓ — that cursor is not a scroll offset (it survives a
// resize, it addresses a row not a line), so the viewport follows it rather than replacing it. It
// scrolls by the MINIMUM that brings the row back into view: EnsureVisible parks the row at the top
// instead, which reads as a jump when you were merely stepping past the edge.
//
// It belongs in the KEY HANDLER, on the arm that moved the selection, and nowhere else. Calling it
// from the `<surface>Viewport()` builder — which the renderer also calls — would make every frame
// re-assert the cursor's position and silently undo the pane keys: press `end` on a 500-row list and
// the next View drags you back to row 0, which is the same class of defect as clamping in the
// renderer. Scrolling away from the selection is a thing a reader is allowed to do.
//
// THE CURSOR FALLS THROUGH AT ITS ENDS. Every surface that owns ↑/↓ as a selection key returns from
// that arm only when the cursor actually MOVED; at the top or the bottom of the list the key drops
// past the switch to applyPaneScroll and scrolls the pane instead. Without it, ↓ simply dies on the
// last row — and on Telegram, whose "list" is five fields above a poll error that can be hundreds of
// lines long, that meant the arrow keys could not reach the text at all. A key that stops working
// at a boundary is indistinguishable from a key that is broken.
func ensurePaneRow(vp *viewport.Model, row int) {
	if row < 0 {
		return
	}
	h := max(1, vp.Height())
	switch {
	case row < vp.YOffset():
		vp.SetYOffset(row)
	case row >= vp.YOffset()+h:
		vp.SetYOffset(row - h + 1)
	}
}

// paneTailReadout is the position note for a live-tail pane: "● live tail" while the reader is on
// the newest line, and the same clamped PERCENT every other pane carries plus the key back once they
// are not. It replaces the raw stream's `↕ scrolled back N — end to live-tail`, which reported an
// inverted integer nobody could act on — "scrolled back 137" answers neither "how much is left" nor
// "am I live", which are the only two questions a tail pane raises.
//
// The caller styles it: this returns the plain text and whether it is the live case, because padding
// or styling here would put ANSI bytes inside a string the bottom bar measures (STYLE.md).
func paneTailReadout(vp viewport.Model) (text string, atBottom bool) {
	if vp.AtBottom() {
		return "● live tail", true
	}
	pct := paneScrollStatus(vp)
	if pct == "" {
		pct = "0%"
	}
	return "↕ " + pct + " — end to live-tail", false
}

// paneScrollStatus is the position readout: a clamped PERCENT, following glow (ui/pager.go:309),
// not a line count. A line count answers "which line am I on", which nobody asked; a percent answers
// "how much is left", which is the question a long document raises. Empty when nothing scrolls —
// a pane that fits should not carry a permanent "100%".
func paneScrollStatus(vp viewport.Model) string {
	if vp.TotalLineCount() <= vp.Height() {
		return ""
	}
	return fmt.Sprintf("%d%%", int(vp.ScrollPercent()*100+0.5))
}

// paneScrollHelp is the one-line hint the bottom bar carries, and it is deliberately terse: Knowledge
// already spends most of that row on counts and its three write keys. It names the moves that are NOT
// guessable from an arrow key — the half-page pair and the two ends — and leaves the page keys to the
// help card, since a pager's page keys are the one part a reader guesses right.
func paneScrollHelp(vp viewport.Model) string {
	h := "↑↓ d/u G/home scroll"
	if pct := paneScrollStatus(vp); pct != "" {
		h += " " + pct
	}
	return h
}

// paneScrollHint is the hint every OTHER surface carries — the ones whose bottom bar is already
// spending most of its width on their own keys, and the ones where `↑↓` mean something else.
//
// `arrows` is not a style choice. On History, Processes, Telegram and Plan the arrows move a
// SELECTION and only fall through to the pane at the ends of the list, so advertising them as the
// scroll keys would be the same species of lie as the tab help that named `k` on Knowledge: a key
// the legend claims and the surface spends elsewhere. It returns "" when nothing scrolls, so a pane
// that fits never carries a permanent readout — the rule paneScrollStatus already encodes.
func paneScrollHint(vp viewport.Model, arrows bool) string {
	pct := paneScrollStatus(vp)
	if pct == "" {
		return ""
	}
	if arrows {
		return "↑↓ d/u G/home " + pct
	}
	return "d/u G/home " + pct
}
