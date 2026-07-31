package tui

import (
	"fmt"
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
// the fetch broke.
func TestReportScoresSectionAbsentWhenNoScores(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	if got := m.renderReportScores(); got != "" {
		t.Errorf("no scores fetched yet must render nothing, got:\n%s", got)
	}
	m.reportScores = &api.ScoresDto{}
	if got := m.renderReportScores(); got != "" {
		t.Errorf("an empty scores result must render nothing, got:\n%s", got)
	}
	// A failed fetch is surfaced, not swallowed — the owner needs to know the section is unavailable
	// rather than conclude the run has no scores.
	m.reportScoresErr = "GET /scores: 500"
	if got := stripANSI(m.renderReportScores()); !strings.Contains(got, "GET /scores: 500") {
		t.Errorf("a scores fetch error must be surfaced, got:\n%s", got)
	}
}

// SF1.1: the whole point of the DTO. The canned SELECT returned three raw columns, so the section
// could only print the number — a reader had no way to tell whether 66 was a pass, and PASS/FAIL/WARN
// all fell through sessionOutcomeStyle to the same grey. The score now renders against the bar the
// ENGINE judged it by, and a stage with its own QA dial must show ITS bar, not a hardcoded 80.
func TestReportScoresShowTheBarTheEngineJudgedBy(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	m.reportScores = &api.ScoresDto{Scores: []api.ScoreDto{
		{SessionNumber: 11, StageId: strPtr("F7"), Score: 66, Verdict: "WARN", Passed: false, Threshold: 80,
			Findings: []string{"one", "two"}},
		// A stricter stage dial: 88 is a FAIL here, and a client deriving pass/fail itself would say
		// otherwise. Only the engine knows this.
		{SessionNumber: 9, StageId: strPtr("F9"), Score: 88, Verdict: "FAIL", Passed: false, Threshold: 95},
		{SessionNumber: 8, StageId: strPtr("F6"), Score: 90, Verdict: "PASS", Passed: true, Threshold: 80},
	}}
	plain := stripANSI(m.renderReportScores())
	for _, want := range []string{"66/80", "88/95", "90/80", "F7", "F9", "2 findings"} {
		if !strings.Contains(plain, want) {
			t.Errorf("the scores section must show %q:\n%s", want, plain)
		}
	}

	// Colour is the other half: a failing verdict must not render identically to a passing one, which
	// is exactly what the canned query produced.
	styled := m.renderReportScores()
	failLine, passLine := "", ""
	for _, line := range strings.Split(styled, "\n") {
		if strings.Contains(stripANSI(line), "#11") {
			failLine = line
		}
		if strings.Contains(stripANSI(line), "#8 ") {
			passLine = line
		}
	}
	if failLine == "" || passLine == "" {
		t.Fatalf("expected a row for #11 and #8:\n%s", stripANSI(styled))
	}
	if ansiCodes(failLine) == ansiCodes(passLine) {
		t.Errorf("a failed verdict renders with the same styling as a passed one:\n%s\n%s", failLine, passLine)
	}
}

// ansiCodes returns just the escape sequences of a styled line, so a test can assert that two rows
// are coloured DIFFERENTLY without pinning any particular palette.
func ansiCodes(s string) string {
	return strings.Join(ansiRe.FindAllString(s, -1), "")
}

// --demo has to render this section too, off the REAL demo source — not the golden fixture. An
// offline face whose scores section renders nothing (or nonsense) is exactly what --demo exists to
// catch, and it is how the section is reviewed without a live engine. Prints the frame under -v so
// the rendered result is capturable as evidence.
func TestDemoSourceRendersTheScoresSection(t *testing.T) {
	src := api.NewDemoSource()
	defer src.Close()
	scores, err := src.FetchScores()
	if err != nil {
		t.Fatalf("the demo source must answer FetchScores: %v", err)
	}
	if len(scores.Scores) == 0 {
		t.Fatal("the demo source returned no scores — the offline Report tab would show no section at all")
	}

	m := newGoldenModel(120, 40).(Model)
	m.reportScores = scores
	section := m.renderReportScores()
	plain := stripANSI(section)
	if !strings.Contains(plain, "Verifier scores") {
		t.Fatalf("the demo scores section did not render:\n%s", plain)
	}
	for _, sc := range scores.Scores {
		want := fmt.Sprintf("%d/%d", sc.Score, sc.Threshold)
		if !strings.Contains(plain, want) {
			t.Errorf("demo score %q is missing from the rendered section:\n%s", want, plain)
		}
	}
	t.Logf("--demo Report tab, Verifier scores section:\n%s", section)
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

// --- session tokens, re-homed from the deleted Dev tab (SF1.2) ----------------

// This came from tab_dev_test.go with the table it measures. The Dev tab's stats table renders what
// run.db says, including the ugly truth: a session that recorded a real cost against zero tokens is
// what EVERY claude-native session looked like before bug #5 (ClaudeProvider never read `usage`). The
// table must show that, name the reason, and never quietly drop the row or invent a plausible count.
func TestReportSessionTokensShowsZeroTokenSessionsAndNamesTheCause(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	stats := stripANSI(m.renderReportSessionTokens())

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
	if got := stripANSI(m.renderReportSessionTokens()); strings.Contains(got, "#5") {
		t.Errorf("no zero-token session means no note:\n%s", got)
	}
}

// The table has to be REACHABLE, not merely rendered: it is the last section of a scrolling pane, and
// the Dev tab shipped with exactly this bug — a pane whose scroll maximum was computed from slice
// elements instead of rendered lines, so the bottom was silently clipped and pgdn did nothing.
func TestReportSessionTokensIsReachableByScrolling(t *testing.T) {
	var m = newGoldenModel(120, 30).(Model)
	top, _ := m.renderReportPane()
	if strings.Contains(stripANSI(top), "Session tokens") {
		t.Skip("the table already fits on the first frame at this size; nothing to prove about scroll")
	}
	for i := 0; i < 200; i++ {
		m = asModel(mustHandle(m.handleReportKey("pgdown")))
	}
	bottom, _ := m.renderReportPane()
	if !strings.Contains(stripANSI(bottom), "Session tokens") {
		t.Errorf("scrolling to the end never reaches the re-homed token table:\n%s", stripANSI(bottom))
	}
}
