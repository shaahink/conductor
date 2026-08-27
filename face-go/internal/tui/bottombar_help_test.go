package tui

import (
	"strings"
	"testing"

	"charm.land/lipgloss/v2"
)

// Bug #18: the pane's contextual help used to be clipped by the frame's MaxWidth alone, from the
// right, with no marker. The Agent tab advertised a nonexistent "end l" key for an era because its
// help had outgrown the bar at 120 cols and the golden pinned the clipped frame as correct. The bar
// now gives the help the columns that are really left and cuts it with truncate(), so a cut says so.
func TestBottomBarHelpIsTruncatedWithAMarkerNotClippedSilently(t *testing.T) {
	m := newTestModel()
	long := "↑↓ scroll · f fold · T thinking · c raw · / search · end live-tail · g top · G bottom"

	for _, width := range []int{90, 100, 110} {
		bar := stripANSI(m.renderBottomBar(width, long))
		if w := lipgloss.Width(bar); w > width {
			t.Errorf("at %d cols the bar is %d wide - the frame invariant is broken", width, w)
		}
		if !strings.Contains(bar, "…") {
			t.Errorf("at %d cols a help that cannot fit must end in an ellipsis, got: %q", width, bar)
		}
		if strings.Contains(bar, "end l") && !strings.Contains(bar, "end live-tail") {
			t.Errorf("at %d cols the bar still advertises a key that does not exist: %q", width, bar)
		}
	}
}

// A help that fits is shown whole, and the divider is still there.
func TestBottomBarHelpThatFitsIsShownWhole(t *testing.T) {
	m := newTestModel()
	bar := stripANSI(m.renderBottomBar(200, "↑↓ scroll · f fold"))
	if !strings.Contains(bar, "│") || !strings.Contains(bar, "↑↓ scroll · f fold") {
		t.Fatalf("a help that fits must be shown whole after the divider, got: %q", bar)
	}
	if strings.Contains(bar, "…") {
		t.Fatalf("nothing was cut, so nothing may say it was: %q", bar)
	}
}

// Under the readable minimum the help is dropped whole, the way the top bar tiers its segments - a
// three-character help advertises nothing and reads as a rendering fault.
func TestBottomBarDropsTheHelpRatherThanShowingAStub(t *testing.T) {
	m := newTestModel()
	globals := stripANSI(m.renderBottomBar(90, ""))
	// 90 is the width gate; find the widest bar at which the help is dropped by walking down from a
	// width that shows it and asserting the two shapes are the only ones that ever appear.
	for width := 90; width <= 140; width++ {
		bar := stripANSI(m.renderBottomBar(width, "↑↓ scroll · f fold · T thinking · c raw"))
		if strings.Contains(bar, "│") {
			after := bar[strings.Index(bar, "│")+len("│"):]
			if n := lipgloss.Width(strings.TrimSpace(after)); n < paneHelpMinCols {
				t.Errorf("at %d cols the help is a %d-column stub: %q", width, n, bar)
			}
		} else if strings.TrimSpace(bar) != strings.TrimSpace(globals) {
			t.Errorf("at %d cols the bar is neither the globals alone nor globals+help: %q", width, bar)
		}
	}
}
