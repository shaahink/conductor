package tui

import (
	"strings"
	"testing"
	"time"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

// These pin the Report tab's RULES (U2.2) — the judgements a golden frame can't state, and the ones
// that would otherwise only fail as a wrong-looking number nobody notices.

// The report must never invent a figure it wasn't given. Zero tokens against a real cost is what a
// session recorded before bug #5 (ClaudeProvider not reading `usage`) honestly looks like, and the
// digest has to show that cost rather than hide the row or fabricate tokens.
func TestReportDigestShowsRealCostOnAZeroTokenSession(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	plain := stripANSI(m.renderReportSessions(100))
	if !strings.Contains(plain, "#8") {
		t.Fatalf("session 8 (real cost, zero tokens) is missing from the digest:\n%s", plain)
	}
	if !strings.Contains(plain, "$0.09") {
		t.Errorf("session 8's real $0.0912 cost must render, not be zeroed:\n%s", plain)
	}
}

// A sub-cent cost is real money the run spent. Rounding it to "$0.00" reads as free.
func TestFmtCostNeverRoundsARealChargeToZero(t *testing.T) {
	cases := []struct {
		in   float64
		want string
	}{
		{0, "$0.00"},       // genuinely nothing — say so plainly
		{0.0027, "<$0.01"}, // a real gate-only charge from this repo's own run.db
		{0.0912, "$0.09"},
		{13.294975, "$13.29"},
	}
	for _, c := range cases {
		if got := fmtCost(c.in); got != c.want {
			t.Errorf("fmtCost(%v) = %q, want %q", c.in, got, c.want)
		}
	}
}

// "when present" is literal: no scores = no section. An empty "Verifier scores" heading reads like
// the query broke.
func TestReportScoresSectionAbsentWhenNoScores(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	if got := m.renderReportScores(); got != "" {
		t.Errorf("no scores fetched yet must render nothing, got:\n%s", got)
	}
	m.reportScores = &api.QueryResultDto{Columns: []string{"session_number", "score", "verdict"}}
	if got := m.renderReportScores(); got != "" {
		t.Errorf("an empty scores result must render nothing, got:\n%s", got)
	}
	// A failed query is surfaced, not swallowed — the owner needs to know the section is unavailable
	// rather than conclude the run has no scores.
	errMsg := "no such table: scores"
	m.reportScores = &api.QueryResultDto{Error: &errMsg}
	if got := stripANSI(m.renderReportScores()); !strings.Contains(got, "no such table") {
		t.Errorf("a scores query error must be surfaced, got:\n%s", got)
	}
}

// The live session has no EndedUtc. Its duration must come from the engine's SessionElapsedSec —
// never a local clock, which would drift from the engine and make every golden time-dependent.
func TestSessionDurationUsesEngineElapsedForTheLiveSession(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	live := api.SessionRowDto{Number: m.data.Plan.SessionNumber, StartedUtc: "2026-07-15T10:00:00Z"}
	d, ok := m.sessionDuration(live)
	if !ok {
		t.Fatal("the live session must report a duration from the engine's elapsed")
	}
	if want := time.Duration(m.data.Plan.SessionElapsedSec) * time.Second; d != want {
		t.Errorf("live duration = %v, want the engine's %v", d, want)
	}
	// A session that is neither ended nor current has no honest duration to show.
	orphan := api.SessionRowDto{Number: 999, StartedUtc: "2026-07-15T10:00:00Z"}
	if _, ok := m.sessionDuration(orphan); ok {
		t.Error("a non-current session with no EndedUtc must report no duration, not guess one")
	}
}

// Report and the sidebar must answer the same stage state identically — the duplicate switch this
// replaced rendered "confirmed" stages as ○ in Report while the sidebar showed ✓.
func TestReportStageGlyphsMatchTheSidebarVocabulary(t *testing.T) {
	for _, state := range []string{"confirmed", "done", "active", "gating", "failed", "skipped", "todo", ""} {
		g, _ := widgets.StageGlyph(state)
		if g == "" {
			t.Errorf("state %q has no glyph", state)
		}
	}
	// The engine's real "already finished" states must not fall through to the todo glyph.
	for _, state := range []string{"confirmed", "done"} {
		if g, _ := widgets.StageGlyph(state); g != "✓" {
			t.Errorf("state %q rendered %q, want ✓ — a finished stage is showing as unstarted", state, g)
		}
	}
	if g, _ := widgets.GateGlyph("pass"); g != "✓" {
		t.Errorf("gate state \"pass\" rendered %q, want ✓", g)
	}
}

// Scrolling past the end must not strand the owner on a blank pane.
func TestReportScrollClampsToTheBody(t *testing.T) {
	var m = newGoldenModel(120, 30).(Model)
	for i := 0; i < 500; i++ {
		m = asModel(mustHandle(m.handleReportKey("down")))
	}
	body, _ := m.renderReportPane()
	if strings.TrimSpace(stripANSI(body)) == "" {
		t.Error("scrolling past the end blanked the report — the offset is not clamped to the body")
	}
}
