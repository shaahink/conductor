package tui

import (
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"
)

// The help card is the one overlay that grows every time a feature adds a legend row (U2.1 added the
// palette groups). TestGoldenSizes renders the dashboard at each width but never opens an overlay, so
// nothing caught the card outgrowing the terminal. 80x24 is STYLE.md's documented floor: past it the
// compositor clips the card's own border and the help becomes unreadable exactly when the window is
// too small to read it in.
func TestHelpOverlayFitsSmallestTerminal(t *testing.T) {
	const w, h = 80, 24
	var tm tea.Model = newGoldenModel(w, h)
	tm, _ = tm.Update(keyMsg("?"))
	lines := strings.Split(stripANSI(tm.(Model).View().Content), "\n")

	if len(lines) > h {
		t.Errorf("help frame is %d rows at %dx%d — the card no longer fits the smallest supported "+
			"terminal; trim a legend row", len(lines), w, h)
	}
	for i, l := range lines {
		if n := len([]rune(strings.TrimRight(l, " "))); n > w {
			t.Errorf("help row %d is %d cols wide at %dx%d (max %d): %q", i, n, w, h, w, l)
		}
	}
	// The card must still read as a card: if the top/bottom border is missing, it got clipped.
	joined := strings.Join(lines, "\n")
	if !strings.Contains(joined, "╭") || !strings.Contains(joined, "╰") {
		t.Error("help card lost a horizontal border at 80x24 — it is being clipped, not composited")
	}
}
