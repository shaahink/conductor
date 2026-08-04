package tui

import (
	"fmt"
	"strings"
	"time"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
	"conductor-face-go/internal/widgets"
)

// The Report tab is the OWNER's run report (U2.2): "how is this run going", rendered — not a SQL
// prompt. It replaced the SQL console, which moved to Dev (tab_dev.go) unchanged; the owner's
// verdict on the old tab was "report being sql is stupid — show a good report visually".
//
// Everything here is rendered from data the Face polls over typed endpoints — /state, /sessions and
// (SF1.1) /scores. The scores section used to be the exception: a canned SELECT through the Dev SQL
// console's endpoint, which is why deleting that console was blocked on giving this one section a
// wire type. That result is kept in its own field (reportScores) and never in data.ReportResult,
// which belongs to the Dev console — sharing it would make opening Report wipe whatever the
// developer had just queried.
//
// No interaction beyond scroll, by design: a report you can accidentally edit is not a report.

// sessionsDigestMax caps the sessions section: a report is a summary, and the Sessions tab is one
// keypress away for the full list.
const sessionsDigestMax = 8

// scoresDigestMax caps the scores section the same way. The old canned query carried a LIMIT 20;
// the endpoint returns everything, so the cap lives here where the reader can see it.
const scoresDigestMax = 10

func (m Model) handleReportKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		return m.openTab(TabAgent)
	case "up", "k":
		if m.reportScroll > 0 {
			m.reportScroll--
		}
	case "down", "j":
		m.reportScroll++ // clamped by the renderer against the real body height
	case "home":
		m.reportScroll = 0
	case "pgup":
		m.reportScroll = max(0, m.reportScroll-m.paneRows())
	case "pgdown":
		m.reportScroll += m.paneRows()
	}
	return m, nil
}

func (m Model) renderReportPane() (string, string) {
	s := m.data.Plan
	if s == nil {
		return subtleStyle.Render("No run attached — nothing to report yet."), "esc back"
	}
	w := m.paneCols()
	sections := []string{
		m.renderReportRun(s),
		m.renderReportStages(s, w),
		m.renderReportSessions(w),
		m.renderReportGates(s),
	}
	if sc := m.renderReportScores(); sc != "" {
		sections = append(sections, sc)
	}
	// SF1.2: the per-session token/cost table, re-homed from the deleted Dev tab. It goes LAST because
	// it is the accounting behind the numbers above, not the headline — and because the Report pane
	// scrolls, so a long table costs the sections above it nothing.
	sections = append(sections, m.renderReportSessionTokens())
	body := strings.Join(sections, "\n\n")

	// Clamp scroll to the real body: scrolling past the end leaves the owner staring at blank space
	// wondering whether the report broke.
	lines := strings.Split(body, "\n")
	rows := m.paneRows()
	maxScroll := max(0, len(lines)-rows)
	scroll := min(m.reportScroll, maxScroll)
	if scroll > 0 || maxScroll > 0 {
		end := min(scroll+rows, len(lines))
		lines = lines[scroll:end]
	}
	help := "↑↓ scroll · esc back"
	if maxScroll > 0 {
		help = fmt.Sprintf("↑↓ scroll (%d/%d) · esc back", scroll, maxScroll)
	}
	return strings.Join(lines, "\n"), help
}

// --- run header ---------------------------------------------------------------

func (m Model) renderReportRun(s *api.StateDto) string {
	cost := peachStyle.Render(fmt.Sprintf("$%.2f", s.TotalCostUsd))
	if s.OverheadCostUsd > 0 {
		cost += subtleStyle.Render(fmt.Sprintf("   +$%.2f overhead", s.OverheadCostUsd))
	}
	rows := []string{
		homeRow("plan", textStyle.Render(s.PlanName)),
		homeRow("status", widgets.StatusBadge(s.Status)),
		homeRow("progress", homeProgress(s)),
		homeRow("cost", cost),
		homeRow("tokens", subtleStyle.Render(fmt.Sprintf("%s in · %s out · %s reasoning",
			widgets.FmtTokens(s.TokensInput), widgets.FmtTokens(s.TokensOutput),
			widgets.FmtTokens(s.TokensReasoning)))),
		homeRow("elapsed", m.reportElapsed()),
	}
	if s.AttentionReason != nil && *s.AttentionReason != "" {
		rows = append(rows, homeRow("attention", destructStyle.Render(*s.AttentionReason)))
	}
	return homeSection("Run", rows...)
}

// reportElapsed sums the wall time of every session on the wire — the run's actual working time,
// which is honest and deterministic. It is NOT "now minus run start": a run that sat parked
// overnight did not spend the night working, and a wall clock would also make this untestable.
func (m Model) reportElapsed() string {
	var total time.Duration
	counted := 0
	for _, r := range m.data.Sessions {
		if d, ok := m.sessionDuration(r); ok {
			total += d
			counted++
		}
	}
	if counted == 0 {
		return subtleStyle.Render("—")
	}
	return textStyle.Render(fmtDuration(total)) +
		subtleStyle.Render(fmt.Sprintf(" across %s", plural(counted, "session")))
}

// sessionDuration is a session's wall time. A session still running has no EndedUtc, so the engine's
// live SessionElapsedSec is used for exactly the session /state says is current — never a local
// clock, which would desync the Face from the engine and make every golden frame time-dependent.
func (m Model) sessionDuration(r api.SessionRowDto) (time.Duration, bool) {
	if r.EndedUtc != nil {
		start, okS := timefmt.Parse(r.StartedUtc)
		end, okE := timefmt.Parse(*r.EndedUtc)
		if okS && okE && !end.Before(start) {
			return end.Sub(start), true
		}
		return 0, false
	}
	if s := m.data.Plan; s != nil && s.SessionNumber == r.Number && s.SessionElapsedSec > 0 {
		return time.Duration(s.SessionElapsedSec * float64(time.Second)), true
	}
	return 0, false
}

// fmtDuration is timefmt.Duration plus this pane's one house rule: a duration the Face does not have
// renders as an em-dash, not as "0s". Zero seconds and "we could not work it out" are different
// facts and the sessions table has to be able to say the second one.
func fmtDuration(d time.Duration) string {
	if d <= 0 {
		return "—"
	}
	return timefmt.Duration(d)
}

func plural(n int, unit string) string {
	if n == 1 {
		return fmt.Sprintf("%d %s", n, unit)
	}
	return fmt.Sprintf("%d %ss", n, unit)
}

// fmtCost renders a cost the way the rest of the Face does — 2dp — with one carve-out: a real but
// sub-cent charge must not render as "$0.00". A gate-only session genuinely costs $0.003, and
// rounding that to zero reads as free. "<$0.01" is small AND true.
//
// It is widgets.FmtMoney under the Report's own name — the sub-cent rule lives in one place now
// (SF2.3), so a stage costing a third of a cent cannot read "$0.00" here and "<$0.01" one tab over.
func fmtCost(v float64) string { return widgets.FmtMoney(v) }

// --- stages -------------------------------------------------------------------

func (m Model) renderReportStages(s *api.StateDto, w int) string {
	if len(s.Stages) == 0 {
		return homeSection("Stages", subtleStyle.Render("no stages"))
	}
	rows := []string{reportStagesHeader()}
	for _, st := range s.Stages {
		glyph, gs := widgets.StageGlyph(st.State)
		attempts := subtleStyle.Render("—")
		if st.Attempts > 0 {
			attempts = textStyle.Render(fmt.Sprintf("%d×", st.Attempts))
		}
		cost := subtleStyle.Render("—")
		if st.CostUsd > 0 {
			cost = peachStyle.Render(fmtCost(st.CostUsd))
		}
		outcome := subtleStyle.Render("—")
		if st.LastOutcome != nil && *st.LastOutcome != "" {
			outcome = sessionOutcomeStyle(*st.LastOutcome).Render(*st.LastOutcome)
		}
		// Every column is padded as PLAIN text and styled after (STYLE.md): padding a styled string
		// pads its escape bytes and the whole table shears.
		row := gs.Render(glyph) + " " +
			accentStyle.Render(pad(st.Id, 5)) + " " +
			textStyle.Render(pad(fmt.Sprintf("%d/%d", st.Done, st.Total), 6)) + " " +
			padStyled(attempts, fmt.Sprintf("%d×", st.Attempts), 4, st.Attempts > 0) + " " +
			padStyled(cost, fmtCost(st.CostUsd), 7, st.CostUsd > 0) + " " +
			outcome
		rows = append(rows, lipgloss.NewStyle().MaxWidth(w).Render(row))
	}
	return homeSection("Stages", rows...)
}

func reportStagesHeader() string {
	return subtleStyle.Render(fmt.Sprintf("  %-5s %-6s %-4s %-7s %s", "stage", "done", "att", "cost", "last outcome"))
}

// pad pads plain text to w runes (never bytes — a multi-byte glyph would blow the column).
func pad(s string, w int) string {
	r := []rune(s)
	if len(r) >= w {
		return string(r[:w])
	}
	return s + strings.Repeat(" ", w-len(r))
}

// padStyled pads an ALREADY-styled cell by measuring its plain source text, so the ANSI escapes
// never count toward the column width.
func padStyled(styled, plain string, w int, usePlain bool) string {
	src := "—"
	if usePlain {
		src = plain
	}
	if n := len([]rune(src)); n < w {
		return styled + strings.Repeat(" ", w-n)
	}
	return styled
}

// --- sessions digest ----------------------------------------------------------

func (m Model) renderReportSessions(w int) string {
	if len(m.data.Sessions) == 0 {
		return homeSection("Sessions", subtleStyle.Render("no sessions yet"))
	}
	rows := []string{subtleStyle.Render(fmt.Sprintf("  %-4s %-5s %-9s %-11s %-8s %-7s %s",
		"#", "stage", "kind", "outcome", "duration", "cost", "commits"))}

	// /sessions is newest-first on the wire (STYLE.md) — the digest keeps that order: the session
	// you care about is the one that just ran.
	shown := m.data.Sessions
	if len(shown) > sessionsDigestMax {
		shown = shown[:sessionsDigestMax]
	}
	for _, r := range shown {
		outcome, outStyle := "running", infoStyle
		if r.Outcome != nil && *r.Outcome != "" {
			outcome, outStyle = *r.Outcome, sessionOutcomeStyle(*r.Outcome)
		}
		dur := "—"
		if d, ok := m.sessionDuration(r); ok {
			dur = fmtDuration(d)
		}
		cost := subtleStyle.Render(pad("—", 7))
		if r.CostUsd > 0 {
			cost = peachStyle.Render(pad(fmtCost(r.CostUsd), 7))
		}
		row := textStyle.Render(pad(fmt.Sprintf("#%d", r.Number), 4)) + " " +
			accentStyle.Render(pad(r.StageId, 5)) + " " +
			textStyle.Render(pad(r.Kind, 9)) + " " +
			outStyle.Render(pad(outcome, 11)) + " " +
			textStyle.Render(pad(dur, 8)) + " " +
			cost + " " +
			textStyle.Render(fmt.Sprintf("%d", r.CommitCount))
		rows = append(rows, "  "+lipgloss.NewStyle().MaxWidth(max(1, w-2)).Render(row))
		// SF3.1: one compressed digest line under each row. The numbers above say what a session COST;
		// this says what it bought — and it is the engine's own count (SC7.2), not a re-derivation.
		if line := digestOneLine(r.Digest); line != "" {
			rows = append(rows, "       "+subtleStyle.Render(truncate(line, max(4, w-9))))
		}
	}
	if len(m.data.Sessions) > sessionsDigestMax {
		rows = append(rows, subtleStyle.Render(fmt.Sprintf("  … %d older (Sessions tab)",
			len(m.data.Sessions)-sessionsDigestMax)))
	}
	return homeSection("Sessions", rows...)
}

// --- gates --------------------------------------------------------------------

func (m Model) renderReportGates(s *api.StateDto) string {
	if len(s.Gates) == 0 {
		// Mirrors the engine's own honest wording for a gateless plan (U0.3) rather than rendering
		// an empty box that reads like a bug.
		return homeSection("Gates", subtleStyle.Render("gates green (none configured)"))
	}
	rows := make([]string, 0, len(s.Gates))
	for _, g := range s.Gates {
		glyph, gs := widgets.GateGlyph(g.State)
		el := ""
		if g.ElapsedSec > 0 {
			el = subtleStyle.Render(fmt.Sprintf("  %s", fmtDuration(
				time.Duration(g.ElapsedSec*float64(time.Second)))))
		}
		rows = append(rows, "  "+gs.Render(glyph)+" "+textStyle.Render(pad(g.Name, 14))+el)
	}
	return homeSection("Gates", rows...)
}

// --- per-session tokens (re-homed from the deleted Dev tab, SF1.2) -------------

// renderReportSessionTokens is the per-session token/cost table. The numbers are SUMMED server-side
// from the `costs` table (GET /sessions, U2.2) — the sessions table stores none of them.
//
// It answers a different question from the Sessions digest above it, which is why both exist: that one
// is "what happened in each session" (kind, outcome, duration, commits); this one is "what did each
// session BURN". SF1.2 re-homed it here from the Dev tab rather than deleting it with the SQL console,
// because the token accounting was never the part the owner called stupid.
//
// A session showing real cost against 0 tokens is NOT a bug here: every claude-native session before
// bug #5 (ClaudeProvider never read the `usage` object, fixed in 71fa214) recorded exactly that. This
// table renders what the database says and lets that read as odd, rather than hiding the row or
// back-filling a plausible number.
func (m Model) renderReportSessionTokens() string {
	if len(m.data.Sessions) == 0 {
		return homeSection("Session tokens", subtleStyle.Render("no sessions yet"))
	}
	rows := []string{subtleStyle.Render(fmt.Sprintf("  %-4s %-5s %-8s %-8s %-8s %-9s %s",
		"#", "stage", "in", "out", "reason", "cache-read", "cost"))}
	var zeroTokenRows, noReasonRows int
	for _, r := range m.data.Sessions {
		if r.TokensIn == 0 && r.TokensOut == 0 && r.CostUsd > 0 {
			zeroTokenRows++
		}
		if r.TokensThink == nil {
			noReasonRows++
		}
		cost := subtleStyle.Render(pad("—", 7))
		if r.CostUsd > 0 {
			cost = peachStyle.Render(pad(fmtCost(r.CostUsd), 7))
		}
		// K1.3: nil reasoning is "this provider has no such number", not zero. Padded plain then
		// styled subtle, so the eye reads it as absent rather than as a measured 0.
		think := subtleStyle.Render(pad("n/a", 8))
		if r.TokensThink != nil {
			think = textStyle.Render(pad(widgets.FmtTokens(*r.TokensThink), 8))
		}
		// Every column is padded as PLAIN text and styled after (STYLE.md).
		row := textStyle.Render(pad(fmt.Sprintf("#%d", r.Number), 4)) + " " +
			accentStyle.Render(pad(r.StageId, 5)) + " " +
			textStyle.Render(pad(widgets.FmtTokens(r.TokensIn), 8)) + " " +
			textStyle.Render(pad(widgets.FmtTokens(r.TokensOut), 8)) + " " +
			think + " " +
			textStyle.Render(pad(widgets.FmtTokens(r.TokensCache), 9)) + " " +
			cost
		rows = append(rows, "  "+lipgloss.NewStyle().MaxWidth(max(1, m.paneCols()-2)).Render(row))
	}
	// Name the known cause rather than letting a reader debug the Face for an engine-side gap.
	// Hard-clipped: a note that word-wraps loses its indent and shears the section (STYLE.md).
	var notes []string
	if zeroTokenRows > 0 {
		notes = append(notes, fmt.Sprintf("%s with cost but zero tokens — pre-bug-#5 data, not a Face bug",
			plural(zeroTokenRows, "session")))
	}
	// K1.3: say WHY a reason cell is n/a. The engine sends null when its agent provider has no
	// reasoning-token concept (claude folds that spend into output); the alternative was a permanent
	// 0, which reads as a measurement saying no thinking happened.
	if noReasonRows > 0 {
		notes = append(notes, fmt.Sprintf("reason n/a on %s — this run's agent provider reports no thinking tokens",
			plural(noReasonRows, "session")))
	}
	if len(notes) > 0 {
		rows = append(rows, "")
		for _, note := range notes {
			rows = append(rows, "  "+subtleStyle.Render(
				lipgloss.NewStyle().MaxWidth(max(1, m.paneCols()-2)).Render(note)))
		}
	}
	return homeSection("Session tokens", rows...)
}

// --- verifier scores ----------------------------------------------------------

// renderReportScores renders GET /scores if it returned anything. "When present" is literal: a run
// with no verifier scores (QA dial off, or nothing verified yet) gets NO section rather than an empty
// one, and a fetch error is shown as-is rather than swallowed into "no data".
//
// SF1.1: the score is rendered against the bar it was judged by ("66/80") and coloured by the
// engine's own Passed verdict. The canned-query version could do neither — the SELECT returned three
// raw columns, so every verdict rendered the same grey (PASS, FAIL and WARN all miss
// sessionOutcomeStyle's vocabulary and fall through to subtle), and a reader had no way to know
// whether 66 was good.
func (m Model) renderReportScores() string {
	if m.reportScoresErr != "" {
		return homeSection("Verifier scores", "  "+subtleStyle.Render("unavailable: "+m.reportScoresErr))
	}
	res := m.reportScores
	if res == nil || len(res.Scores) == 0 {
		return ""
	}
	rows := []string{subtleStyle.Render(fmt.Sprintf("  %-8s %-5s %-9s %-8s %s",
		"session", "stage", "score", "verdict", "findings"))}

	shown := res.Scores
	if len(shown) > scoresDigestMax {
		shown = shown[:scoresDigestMax]
	}
	for _, sc := range shown {
		stage := "—"
		if sc.StageId != nil && *sc.StageId != "" {
			stage = *sc.StageId
		}
		verdictStyle := destructStyle
		if sc.Passed {
			verdictStyle = safeStyle
		}
		// Every column is padded as PLAIN text and styled after (STYLE.md).
		score := fmt.Sprintf("%d/%d", sc.Score, sc.Threshold)
		findings := subtleStyle.Render("—")
		if n := len(sc.Findings); n > 0 {
			findings = textStyle.Render(plural(n, "finding"))
		}
		rows = append(rows, "  "+
			textStyle.Render(pad(fmt.Sprintf("#%d", sc.SessionNumber), 8))+" "+
			accentStyle.Render(pad(stage, 5))+" "+
			verdictStyle.Render(pad(score, 9))+" "+
			verdictStyle.Render(pad(sc.Verdict, 8))+" "+
			findings)
	}
	if len(res.Scores) > scoresDigestMax {
		rows = append(rows, subtleStyle.Render(fmt.Sprintf("  … %d older",
			len(res.Scores)-scoresDigestMax)))
	}
	return homeSection("Verifier scores", rows...)
}
