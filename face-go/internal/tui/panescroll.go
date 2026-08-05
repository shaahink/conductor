package tui

import (
	"fmt"

	bkey "charm.land/bubbles/v2/key"
	"charm.land/bubbles/v2/viewport"
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
func newPaneViewport() viewport.Model {
	vp := viewport.New()
	vp.SoftWrap = false
	return vp
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
