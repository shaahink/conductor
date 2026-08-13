package tui

// KS2.8: the reader — ONE full-screen overlay that opens any truncated cell or row and shows it
// whole. Every surface opens the SAME overlay; there is no per-surface reader, because ten readers
// with ten key sets is the drift adr/0006 was written to end, one layer up.
//
// Why an overlay and not an eleventh tab: a tab answers a standing question ("what is the run
// doing"); the reader answers one reader's momentary question ("what does this cell say, ALL of
// it") and then gets out of the way. It owns no data — the source string is handed to it at open —
// it fetches nothing, and `esc` returns to exactly the surface and sub-state it was opened from,
// because opening it mutates nothing but the reader itself. STYLE.md's Overlays section names it as
// the sanctioned fourth float for exactly this reason.
//
// Its viewport is the ONE deviation from newPaneViewport: SoftWrap is ON. The pane viewports carry
// pre-styled, pre-clipped rows and re-wrapping a styled row breaks its columns — but the reader is
// handed RAW prose (or glamour output already wrapped to its width), so wrapping is precisely its
// job: a 300-character card note must occupy several rows with every character present, not one
// clipped row ending in an ellipsis. That is why this constructor exists instead of a flag flip on
// newPaneViewport (which stays SoftWrap=false for every pane).

import (
	"strings"

	"charm.land/bubbles/v2/viewport"
	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/widgets"
)

// readerOpenKey opens the reader on every browse surface that clips prose. It is effectively a
// global key, so it lives in the tab-mnemonic namespace (TestTabMnemonicsAreUnique) and was chosen
// the way STYLE.md demands: grep first. `v` is NOT free (the Templates preview toggle); `z` is bound
// by no tab, no folded tab, no global and no pane handler.
//
// Inside a surface that captures all keys (Kanban detail, Telegram's edit branch) the key must be
// routed by that surface's own handler — and in a text editor it stays a typed character, which is
// why the reader opens from Telegram's NON-editing branch only.
const readerOpenKey = "z"

// readerModel is the overlay's whole state: what it shows and how far the reader has scrolled.
// One title, one source text, one flag saying whether the source is markdown, and exactly ONE
// viewport. It lives on the root Model beside `cmd` because it floats over every tab, like the
// command bar — it is not a tab and holds no tab's state.
type readerModel struct {
	open       bool
	title      string
	source     string
	isMarkdown bool
	vp         viewport.Model
}

// newReaderViewport is the reader's own constructor — see the file comment for why SoftWrap is ON
// here and stays OFF in newPaneViewport. Nothing else may use this: a pane that wants soft wrap is
// a pane about to shear its own columns.
func newReaderViewport() viewport.Model {
	vp := widgets.NewPaneViewport()
	vp.SoftWrap = true
	return vp
}

// openReader opens the overlay on a document. A blank document stays closed: a reader over nothing
// would cost the user an `esc` to learn nothing.
func (m Model) openReader(title, source string, isMarkdown bool) Model {
	if strings.TrimSpace(source) == "" {
		return m
	}
	m.reader = readerModel{open: true, title: title, source: source, isMarkdown: isMarkdown, vp: newReaderViewport()}
	return m
}

// handleReaderKey owns every key while the reader is up. It is peeled in Update at the same
// precedence as the command bar — BEFORE handleKey's esc ladder and before `q` can quit — because
// `q` inside a 2000-line document must not kill the Face, and `esc` must close one layer, not two.
// It binds the adr/0006 pane-scroll set and `esc`, and NOTHING else: a tab mnemonic pressed in here
// is a no-op, so closing the reader always returns to the surface it was opened from.
func (m Model) handleReaderKey(key string) (tea.Model, tea.Cmd) {
	if key == "ctrl+c" {
		// The one exception, by the same rule handleKey states: the global quit affordance must not
		// be swallowable by a sub-state. handleKey answers ctrl+c before touching any tab.
		return m.handleKey(key)
	}
	m.quitArmed = false
	if key == "esc" {
		// Close the overlay and nothing else. The surface underneath was never mutated, so the
		// sub-state it was opened from — a Kanban card, a Telegram field, a History selection — is
		// still exactly where it was.
		m.reader = readerModel{}
		return m, nil
	}
	// Size and content FIRST, then move — the clamp lives at the mutation (adr/0006 §1), and the
	// reader is a pane, not an exception to the one scroll idiom.
	m.reader.vp = m.readerViewport()
	applyPaneScroll(&m.reader.vp, key)
	return m, nil
}

// readerInnerWidth is what the overlay's chrome leaves the text: the box spans the window minus a
// one-column margin each side, and border + Padding(0,2) cost six more. At the 80-column floor that
// is 72 columns of prose — the wrap is sized against THIS, never against m.width, or soft wrap
// would only misbehave at the smallest size (the case that reaches nobody's eye until it ships).
func (m Model) readerInnerWidth() int { return max(20, m.width-8) }

// readerRows is the viewport's height: the box (window minus top bar and bottom bar) minus its
// border and its two head rows (title, rule).
func (m Model) readerRows() int { return max(3, m.height-6) }

// readerViewport is the reader's `<surface>Viewport()` builder — the key handler and the renderer
// both call it, exactly like every pane (bug #30's fix is an ordering, and the reader keeps it).
//
// Markdown goes through renderMarkdown — the memoised, theme-projected renderer — never a fresh
// glamour.NewTermRenderer: the memo is what makes calling this on every keypress free
// (markdownRenders() moves at most once per (theme, width, source)), and the projection is what
// keeps an agent's prose legible on latte. Glamour wraps to the width it is given, so its output
// never triggers the soft wrap; raw prose does, which is the point.
func (m Model) readerViewport() viewport.Model {
	w := m.readerInnerWidth()
	body := m.reader.source
	if m.reader.isMarkdown {
		body = renderMarkdown(body, w)
	}
	vp := m.reader.vp
	vp.SetWidth(w)
	vp.SetHeight(m.readerRows())
	vp.SetContentLines(strings.Split(body, "\n"))
	return vp
}

// renderReaderOverlay is the full-screen box View() composites over the dashboard (compositeAt,
// never lipgloss.Place — STYLE.md's transparent-overlay rule, though this box is nearly the window).
// It leaves the top bar and the bottom bar visible: the bottom bar is where the key hints live, and
// a frame whose last row is not the bottom bar is the overflow shape the frame invariant forbids.
func (m Model) renderReaderOverlay() string {
	w := m.readerInnerWidth()
	vp := m.readerViewport()

	// The status line: a clamped percent (paneScrollStatus — empty when the body fits, so a note
	// that fits carries no permanent "100%"), plus the one key that matters here.
	right := "esc close"
	if pct := paneScrollStatus(vp); pct != "" {
		right = pct + " · esc close"
	}
	title := truncate(m.reader.title, max(1, w-lipgloss.Width(right)-2))
	head := padBetween(accentStyle.Render(title), subtleStyle.Render(right), w)
	rule := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(strings.Repeat("─", max(1, w)))

	boxW, boxH := max(10, m.width-2), max(5, m.height-2)
	return lipgloss.NewStyle().
		Background(widgets.Mantle()).
		Border(lipgloss.RoundedBorder()).BorderForeground(widgets.Accent()).
		Padding(0, 2).
		Width(boxW).
		MaxWidth(boxW).MaxHeight(boxH).
		Render(head + "\n" + rule + "\n" + vp.View())
}

// readerBarHelp is what the bottom bar says while the reader is up — the pane-scroll hint every
// scrollable surface carries, plus the way out.
func (m Model) readerBarHelp() string {
	return paneScrollHelp(m.readerViewport()) + " · esc close"
}
