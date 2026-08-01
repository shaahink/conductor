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

// Trap 11, made testable. tabKey in model.go is the single source for tab mnemonics, but this legend
// is hand-maintained string concatenation — so a mnemonic changed in one and not the other makes the
// help lie, silently, until someone presses the key it advertises. SF1.2 removed a tab and had to
// touch both; this is what keeps the next one honest.
func TestHelpLegendNamesEveryTabItsRealMnemonic(t *testing.T) {
	var tm tea.Model = newGoldenModel(120, 40)
	tm, _ = tm.Update(keyMsg("?"))
	help := stripANSI(tm.(Model).View().Content)

	for i, name := range tabNames {
		// The rendered cell is "<key> <name padded to 11>" — assert the PAIR, so a legend that lists
		// the right tab under the wrong key still fails.
		if cell := tabKey[i] + " " + name; !strings.Contains(help, cell) {
			t.Errorf("the help card does not show %q — tab %s is unreachable-looking or listed under "+
				"the wrong key:\n%s", cell, name, help)
		}
	}
	// And nothing the legend advertises may be a tab that no longer exists. Asserted as the rendered
	// CELL (`key name`), not the bare word: after SF1.3 the folded row legitimately mentions Agent's
	// raw stream and History's spine, and a bare-word check could not tell "c Console is a tab" from
	// "c reaches the raw stream inside Agent".
	for _, gone := range []string{"d Dev", "c Console", "t Timeline", "s Sessions"} {
		if strings.Contains(help, gone) {
			t.Errorf("the help card still advertises %q as a tab — that surface was deleted or "+
				"folded:\n%s", gone, help)
		}
	}

	// SF1.3: the folded mnemonics are alive and the help is the only place that says where they went.
	// Without this the two keys look deleted, and a user who learned them concludes the surface is.
	for k := range foldedTabKey {
		if !strings.Contains(help, "folded") || !strings.Contains(help, k+" ") {
			t.Errorf("the help card does not document the folded mnemonic %q — it still works, so a "+
				"legend that omits it makes the surface look deleted:\n%s", k, help)
		}
	}
}
