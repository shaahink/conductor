package tui

import (
	"strings"
	"testing"

	"conductor-face-go/internal/widgets"
)

// K6.4. The two claims adr/0006 §6 makes about the markdown renderer, each measured rather than
// asserted: it takes its colours from the ACTIVE theme, and glamour runs on a content/size/theme
// change instead of on every frame.

// withTheme applies a scheme for the duration of a test and restores the one that was in force.
// Style vars are package state, so a test that switches and forgets poisons every test after it.
func withTheme(t *testing.T, name string) {
	t.Helper()
	before := widgets.CurrentTheme().Name
	if err := ApplyTheme(name); err != nil {
		t.Fatalf("ApplyTheme(%q): %v", name, err)
	}
	t.Cleanup(func() { _ = ApplyTheme(before) })
}

// The bug this closes: renderMarkdown hard-coded glamour's "dark" style, so on `latte` — a LIGHT
// scheme — an agent's result summary was painted in near-white on a near-white pane. The assertion
// is on the escape sequences, not on "the output differs": two themes could differ by a wrapped line
// and still both be rendering in glamour's dark palette.
func TestMarkdownTakesItsColoursFromTheActiveTheme(t *testing.T) {
	const src = "# Landed\n\nWired the **caching layer** in `RunDb`.\n"

	withTheme(t, "mocha")
	mocha := renderMarkdown(src, 60)
	mochaAccent := rgbTriple(widgets.CurrentTheme().Accent)

	withTheme(t, "latte")
	latte := renderMarkdown(src, 60)
	latteText := rgbTriple(widgets.CurrentTheme().Text)

	if mocha == latte {
		t.Fatal("mocha and latte rendered the same bytes — the renderer is still theme-blind")
	}
	if !strings.Contains(mocha, mochaAccent) {
		t.Errorf("mocha's heading does not carry the theme accent %s", mochaAccent)
	}
	if !strings.Contains(latte, latteText) {
		t.Errorf("latte's prose does not carry the theme text colour %s", latteText)
	}
	// A light scheme must not inherit the dark config's body colour. Latte's Text is dark; mocha's is
	// near-white. If latte were still on the dark config, mocha's text colour would show up in it.
	withTheme(t, "mocha")
	if strings.Contains(latte, rgbTriple(widgets.CurrentTheme().Text)) {
		t.Error("the latte render carries mocha's text colour — the light config was not selected")
	}
}

// Polarity picks which of glamour's two base configs the overrides start from. Getting this wrong is
// invisible in the parts we set and wrong in every part we do not (chroma's syntax theme, the
// checkbox glyphs).
func TestThemePolarityMatchesTheScheme(t *testing.T) {
	for name, wantLight := range map[string]bool{"mocha": false, "latte": true, "nord": false, "gruvbox": false} {
		th, ok := widgets.ThemeByName(name)
		if !ok {
			t.Fatalf("theme %q is gone — this test names the schemes by hand", name)
		}
		if got := th.IsLight(); got != wantLight {
			t.Errorf("%s: IsLight()=%v, want %v (Base luminance %.3f)", name, got, wantLight,
				widgets.Luminance(th.Base))
		}
	}
}

// The other half of §6: the old renderer ran goldmark plus chroma inside View, so every tick, every
// keystroke and every resize re-parsed text that had not changed. Rendering is now memoised on
// (theme, width, source) — measured by counting REAL glamour invocations across a frame storm, which
// is the only way to tell a cached call from an uncached one from the outside.
func TestMarkdownRendersOncePerFrameStorm(t *testing.T) {
	withTheme(t, "mocha")
	resetMarkdownCache()
	const src = "## Session 24\n\nThe engine knows what it did and what it *cost*.\n"

	before := markdownRenders()
	first := renderMarkdown(src, 72)
	afterFirst := markdownRenders()
	if afterFirst != before+1 {
		t.Fatalf("first render invoked glamour %d times, want 1", afterFirst-before)
	}

	for i := 0; i < 200; i++ {
		if got := renderMarkdown(src, 72); got != first {
			t.Fatalf("frame %d rendered different bytes from the first", i)
		}
	}
	if got := markdownRenders(); got != afterFirst {
		t.Errorf("200 frames of unchanged content invoked glamour %d more times — the memo is not "+
			"holding, which is the per-frame render adr/0006 §6 removed", got-afterFirst)
	}

	// A real change must still get through the memo, or the cache would be a freeze.
	renderMarkdown(src, 40)
	if got := markdownRenders(); got != afterFirst+1 {
		t.Errorf("a width change rendered %d times, want 1 — the memo is keyed too coarsely", got-afterFirst)
	}
	renderMarkdown(src+"\nand one more line.\n", 72)
	if got := markdownRenders(); got != afterFirst+2 {
		t.Errorf("a content change rendered %d times, want 1", got-afterFirst-1)
	}
	withTheme(t, "latte")
	renderMarkdown(src, 72)
	if got := markdownRenders(); got != afterFirst+3 {
		t.Errorf("a theme switch rendered %d times, want 1 — the memo would serve the old palette", got-afterFirst-2)
	}
}

// rgbTriple is the "R;G;B" a 24-bit SGR sequence carries, built from the theme role so the test
// cannot drift from the palette by hard-coding a hex. It matches the triple rather than a whole
// escape sequence because glamour emits foreground and background in ONE sequence for a filled
// heading, and pinning the exact sequence would be pinning glamour rather than the theme.
func rgbTriple(c interface{ RGBA() (r, g, b, a uint32) }) string {
	r, g, b, _ := c.RGBA()
	return itoa(int(r>>8)) + ";" + itoa(int(g>>8)) + ";" + itoa(int(b>>8))
}
