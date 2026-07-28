package tui

import (
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

// U2.3: the Dev tab's stats table renders what run.db says, including the ugly truth. A session that
// recorded a real cost against zero tokens is what EVERY claude-native session looked like before
// bug #5 (ClaudeProvider never read `usage`) — the developer screen must show that, name the reason,
// and never quietly drop the row or invent a plausible token count.
func TestDevSessionStatsShowsZeroTokenSessionsAndNamesTheCause(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	stats := stripANSI(m.renderDevSessionStats())

	if !strings.Contains(stats, "#8") {
		t.Fatalf("the zero-token session must still be listed:\n%s", stats)
	}
	if !strings.Contains(stats, "$0.09") {
		t.Errorf("its real cost must render:\n%s", stats)
	}
	if !strings.Contains(stats, "#5") {
		t.Errorf("a cost-with-zero-tokens row must name the known cause (bug #5), or it reads as a "+
			"Face bug:\n%s", stats)
	}
	// A run where every session has tokens must NOT carry the note — it would be a lie about the data.
	m.data.Sessions = []api.SessionRowDto{
		{Number: 1, StageId: "F1", TokensIn: 10, TokensOut: 5, CostUsd: 0.01},
	}
	if got := stripANSI(m.renderDevSessionStats()); strings.Contains(got, "#5") {
		t.Errorf("no zero-token session means no note:\n%s", got)
	}
}

// The internals pane is the one a developer screenshots into a bug report. It must say whether a
// write token is present — never what it is.
func TestDevInternalsReportsTokenPresenceWithoutLeakingIt(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	got := stripANSI(m.renderDevInternals())
	if !strings.Contains(got, "present") {
		t.Errorf("token presence must be stated:\n%s", got)
	}
	if !strings.Contains(got, "http://127.0.0.1:4317") {
		t.Errorf("the control-plane url must be stated:\n%s", got)
	}
	// fakeSource reports a token; the rendered pane must never contain a token-looking secret. The
	// Face only ever holds presence, so this pins that the pane can't start echoing one later.
	for _, leak := range []string{"543BCE", "X-Conductor-Token"} {
		if strings.Contains(got, leak) {
			t.Errorf("internals leaked a secret (%s):\n%s", leak, got)
		}
	}
}

// homeRow pads a label to homeLabelW with no separator, so a label of exactly that width butts
// straight against its value — "write token" rendered as "write tokenpresent". Reading the frame
// caught it; nothing else would have. Pin the rule for every label the pane uses, not just that one.
func TestDevInternalsLabelsFitTheGutter(t *testing.T) {
	for _, label := range devInternalLabels {
		if len([]rune(label)) >= homeLabelW {
			t.Errorf("label %q is %d runes; homeLabelW is %d, so homeRow pads it to nothing and the "+
				"value collides with it — keep gutter labels under %d",
				label, len([]rune(label)), homeLabelW, homeLabelW)
		}
	}
	// And prove it end-to-end on the rendered pane: every label must be followed by a space.
	got := stripANSI(newGoldenModel(120, 30).(Model).renderDevInternals())
	for _, label := range []string{"mode", "url", "token", "streams", "seq", "poll"} {
		if !strings.Contains(got, label+" ") {
			t.Errorf("label %q is not followed by a gap in the rendered pane:\n%s", label, got)
		}
	}
}

// pgup/pgdn scroll the whole Dev pane, because internals + stats sit below an unbounded result grid.
// ↑↓ must keep picking quick queries — that is the console's behaviour and U2.2 moved it unchanged.
func TestDevPaneScrollsWithoutStealingTheQuickQueryKeys(t *testing.T) {
	var m = newGoldenModel(120, 30).(Model)
	m = asModel(mustHandle(m.handleKey("d")))
	m.reportFocusQuery = false // leave the editor so the pane's own keys apply

	before := m.reportQuickSelected
	m = asModel(mustHandle(m.handleDevKey("down")))
	if m.reportQuickSelected != before+1 {
		t.Error("↓ must still move the quick-query selection, not scroll the pane")
	}
	if m.devScroll != 0 {
		t.Error("↓ must not scroll the pane")
	}

	// Assert the RENDERED body moves, not just that devScroll changed. The first cut of this pane
	// measured slice elements instead of rendered lines, so devScroll advanced to 24 while the
	// output never moved and the bottom was silently clipped — a state-only assertion passed it.
	top, _ := m.renderDevPane()
	scrolled := asModel(mustHandle(m.handleDevKey("pgdown")))
	if scrolled.devScroll == 0 {
		t.Error("pgdn must scroll the pane toward the internals/stats sections")
	}
	after, help := scrolled.renderDevPane()
	if stripANSI(after) == stripANSI(top) {
		t.Error("pgdn changed devScroll but the rendered pane is identical — the scroll is not applied")
	}
	if !strings.Contains(help, "pgup/pgdn") {
		t.Errorf("a scrollable pane must say so in its help line, got %q", help)
	}
	m = asModel(mustHandle(scrolled.handleDevKey("home")))
	if m.devScroll != 0 {
		t.Error("home must return the pane to the top")
	}

	// Scrolling past the end must not blank the pane.
	for i := 0; i < 200; i++ {
		m = asModel(mustHandle(m.handleDevKey("pgdown")))
	}
	body, _ := m.renderDevPane()
	if strings.TrimSpace(stripANSI(body)) == "" {
		t.Error("scrolling past the end blanked the Dev pane — devScroll is not clamped")
	}
}
