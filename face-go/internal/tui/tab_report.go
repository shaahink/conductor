package tui

import (
	"fmt"
	"strings"
	"time"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

// The Report tab is the OWNER's run report (U2.2): "how is this run going", rendered — not a SQL
// prompt. It replaced the SQL console, which moved to Dev (tab_dev.go) unchanged; the owner's
// verdict on the old tab was "report being sql is stupid — show a good report visually".
//
// Everything here is rendered from data the Face ALREADY polls (/state + /sessions), with one
// exception: verifier scores have no DTO on the wire, so they come from the canned query the spec
// sanctions for exactly that gap. That result is kept in its own field (reportScores) and never in
// data.ReportResult, which belongs to the Dev console — sharing it would make opening Report wipe
// whatever the developer had just queried.
//
// No interaction beyond scroll, by design: a report you can accidentally edit is not a report.

// scoresSQL is the canned query behind the "Verifier scores" section. Kept identical in shape to the
// Dev console's "verifier scores" quick query.
const scoresSQL = "SELECT session_number, score, verdict FROM scores ORDER BY session_number DESC LIMIT 20"

// sessionsDigestMax caps the sessions section: a report is a summary, and the Sessions tab is one
// keypress away for the full list.
const sessionsDigestMax = 8

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
		start, okS := parseUTC(r.StartedUtc)
		end, okE := parseUTC(*r.EndedUtc)
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

func parseUTC(s string) (time.Time, bool) {
	if s == "" {
		return time.Time{}, false
	}
	t, err := time.Parse(time.RFC3339, s)
	if err != nil {
		return time.Time{}, false
	}
	return t, true
}

// fmtDuration renders a wall time the way an owner reads one: the two largest units that matter,
// never "3724.184s".
func fmtDuration(d time.Duration) string {
	if d <= 0 {
		return "—"
	}
	switch {
	case d < time.Minute:
		return fmt.Sprintf("%ds", int(d.Seconds()))
	case d < time.Hour:
		return fmt.Sprintf("%dm %02ds", int(d.Minutes()), int(d.Seconds())%60)
	default:
		return fmt.Sprintf("%dh %02dm", int(d.Hours()), int(d.Minutes())%60)
	}
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
func fmtCost(v float64) string {
	if v > 0 && v < 0.005 {
		return "<$0.01"
	}
	return fmt.Sprintf("$%.2f", v)
}

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

// --- verifier scores ----------------------------------------------------------

// renderReportScores renders the scores canned query if it returned anything. "When present" is
// literal: a run with no verifier scores (QA dial off, or nothing verified yet) gets NO section
// rather than an empty one, and a query error is shown as-is rather than swallowed into "no data".
func (m Model) renderReportScores() string {
	res := m.reportScores
	if res == nil {
		return ""
	}
	if res.Error != nil {
		return homeSection("Verifier scores", "  "+subtleStyle.Render("unavailable: "+*res.Error))
	}
	if len(res.Rows) == 0 {
		return ""
	}
	rows := []string{subtleStyle.Render(fmt.Sprintf("  %-8s %-6s %s", "session", "score", "verdict"))}
	for _, r := range res.Rows {
		v := r.Values
		if len(v) < 3 {
			continue
		}
		rows = append(rows, "  "+textStyle.Render(pad("#"+v[0], 8))+" "+
			textStyle.Render(pad(v[1], 6))+" "+
			sessionOutcomeStyle(v[2]).Render(v[2]))
	}
	return homeSection("Verifier scores", rows...)
}
