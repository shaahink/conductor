package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

// The Dev tab is the developer screen (U2.3). It hosts the SQL console that used to BE the Report
// tab — moved here unchanged when Report became a rendered run report (U2.2), because "report being
// sql is stupid" was about the owner's report, not about losing the console. Report answers "how is
// the run going"; Dev answers "what is the machine actually doing".
//
// The state and endpoint keep their `report*` / `/report/query` names: they name the report DATABASE
// this console queries, which is still exactly what it does, and renaming them would churn the tests
// that pin its behaviour for no truth gained.

var quickQueries = []struct {
	Label string
	SQL   string
}{
	{"cost per stage", "SELECT s.stage_id, SUM(c.cost_usd) as cost_usd FROM costs c JOIN sessions s ON s.number = c.session_number AND s.run_id = c.run_id GROUP BY s.stage_id ORDER BY cost_usd DESC"},
	{"which gates fail most", "SELECT name, COUNT(*) as failures FROM gates WHERE passed = 0 GROUP BY name ORDER BY failures DESC"},
	{"recent sessions", "SELECT number, stage_id, kind, outcome FROM sessions ORDER BY number DESC LIMIT 20"},
	{"verifier scores", "SELECT session_number, score, verdict FROM scores ORDER BY session_number DESC LIMIT 20"},
}

const defaultReportSQL = "SELECT s.stage_id, SUM(c.cost_usd) as cost_usd FROM costs c JOIN sessions s ON s.number = c.session_number AND s.run_id = c.run_id GROUP BY s.stage_id"

const reportHScrollStep = 8 // columns of horizontal scroll per left/right press

type labeledQuery struct{ Label, SQL string }

// reportQueries is the pickable list: the curated quick queries, then this run's history (most-recent
// first) so a query you just ran is one keypress away to re-run or tweak.
func (m Model) reportQueries() []labeledQuery {
	out := make([]labeledQuery, 0, len(quickQueries)+len(m.reportHistory))
	for _, q := range quickQueries {
		out = append(out, labeledQuery{q.Label, q.SQL})
	}
	for _, h := range m.reportHistory {
		out = append(out, labeledQuery{"↺ " + truncate(h, 44), h})
	}
	return out
}

func (m Model) handleDevKey(key string) (tea.Model, tea.Cmd) {
	// Query editor has focus: full cursor editing; enter runs (SQL is single logical line here).
	if m.reportFocusQuery {
		switch key {
		case "esc", "tab":
			m.reportFocusQuery = false
			return m, nil
		case "enter":
			return m.runReport(m.reportEditor.Value())
		default:
			m.reportEditor = m.reportEditor.Update(key)
			return m, nil
		}
	}

	list := m.reportQueries()
	switch key {
	case "esc":
		return m.openTab(TabAgent)
	case "tab":
		m.reportFocusQuery = true
	case "up", "k":
		if m.reportQuickSelected > 0 {
			m.reportQuickSelected--
		}
	case "down", "j":
		if m.reportQuickSelected < len(list)-1 {
			m.reportQuickSelected++
		}
	case "left", "h":
		if m.reportHScroll > 0 {
			m.reportHScroll-- // scroll a wide result table left
		}
	case "right", "l":
		m.reportHScroll++ // scroll right (renderer clamps)
	// U2.3: pgup/pgdn scroll the WHOLE pane, because internals + session stats sit below a result
	// grid that can be arbitrarily tall. ↑↓ stays on the quick-query list (the console's existing
	// behaviour, unchanged) — so the page keys are what reach the sections underneath.
	case "pgup":
		m.devScroll = max(0, m.devScroll-m.paneRows())
	case "pgdown":
		m.devScroll += m.paneRows()
	case "home":
		m.devScroll = 0
	case "enter":
		if m.reportQuickSelected < len(list) {
			sql := list[m.reportQuickSelected].SQL
			m.reportEditor = widgets.NewTextArea(sql, max(10, m.paneCols()), 1)
			return m.runReport(sql)
		}
	}
	return m, nil
}

func (m Model) runReport(sql string) (tea.Model, tea.Cmd) {
	sql = strings.TrimSpace(sql)
	if sql == "" {
		return m, nil
	}
	m.reportHistory = pushHistory(m.reportHistory, sql)
	m.reportHScroll = 0
	m.data.ReportLoading = true
	return m, m.cmdQueryReport(sql)
}

// pushHistory keeps the last 8 distinct queries, most-recent-first.
func pushHistory(hist []string, sql string) []string {
	out := []string{sql}
	for _, h := range hist {
		if h != sql {
			out = append(out, h)
		}
	}
	if len(out) > 8 {
		out = out[:8]
	}
	return out
}

func (m Model) renderDevPane() (string, string) {
	var lines []string
	list := m.reportQueries()
	lines = append(lines, subtleStyle.Render("Queries (↑↓ pick · enter run):"))
	for i, q := range list {
		marker := "  "
		if i == m.reportQuickSelected && !m.reportFocusQuery {
			marker = accentStyle.Render("› ")
		}
		lines = append(lines, marker+truncate(q.Label, m.paneCols()-4))
	}
	lines = append(lines, "")

	sf := subtleStyle
	if m.reportFocusQuery {
		sf = accentStyle
	}
	ed := m.reportEditor
	ed.SetSize(max(10, m.paneCols()), 1)
	sqlView := textStyle.Render(truncate(ed.Value(), m.paneCols()))
	if m.reportFocusQuery {
		sqlView = ed.View() // live caret + horizontal scroll within the line
	}
	lines = append(lines, sf.Render("SQL (tab to focus/blur):"), sqlView)

	if m.data.ReportLoading {
		lines = append(lines, "", subtleStyle.Render("running…"))
	} else if m.data.ReportResult != nil {
		lines = append(lines, "")
		lines = append(lines, m.renderResultTable(m.data.ReportResult)...)
	}

	// U2.3: the developer screen's other half — what the machine is actually doing.
	lines = append(lines, "", m.renderDevInternals(), "", m.renderDevSessionStats())

	// The console's result grid is unbounded, so the sections below it are only reachable by
	// scrolling the composed pane. Measure RENDERED lines, not slice elements: homeSection returns
	// one multi-line string per section, so len(lines) is ~16 where the body is 26 lines — which
	// computed maxScroll as 0 and made pgdn silently do nothing while the frame clipped the bottom.
	rendered := strings.Split(strings.Join(lines, "\n"), "\n")
	rows := m.paneRows()
	maxScroll := max(0, len(rendered)-rows)
	scroll := min(m.devScroll, maxScroll)
	if maxScroll > 0 {
		rendered = rendered[scroll:min(scroll+rows, len(rendered))]
	}
	help := "tab focus · ↑↓ pick · ←→ scroll cols · enter run"
	if maxScroll > 0 {
		help += fmt.Sprintf(" · pgup/pgdn pane (%d/%d)", scroll, maxScroll)
	}
	return strings.Join(rendered, "\n"), help
}

// --- U2.3: run internals ------------------------------------------------------

// devInternalLabels is every label this pane puts in the shared homeRow gutter. homeRow pads a label
// to homeLabelW with NO separator, so a label of exactly that width butts straight against its value
// ("write tokenpresent" — the first cut of this pane did precisely that). Listing them here is what
// makes the rule testable: TestDevInternalsLabelsFitTheGutter checks the whole set, so the next row
// added can't reintroduce it.
var devInternalLabels = []string{
	"mode", "url", "token", "streams", "seq", "poll", "run id", "state dir", "last error",
}

// renderDevInternals answers "is the Face actually wired to anything, and how". Every value is read
// from live client state — nothing here is a constant dressed up as a reading.
func (m Model) renderDevInternals() string {
	c := m.data.Connection
	conn := func(ok bool, label string) string {
		if ok {
			return safeStyle.Render("●") + " " + textStyle.Render(label)
		}
		return destructStyle.Render("○") + " " + subtleStyle.Render(label)
	}

	url := m.baseURL
	if url == "" {
		url = "—"
	}
	// Token presence, never the token itself: this pane is the one a developer screenshots.
	tok := destructStyle.Render("absent — writes will be refused")
	if m.source.HasWriteToken() {
		tok = safeStyle.Render("present")
	}
	if c.Mode == api.ModeDemo {
		tok = subtleStyle.Render("n/a (demo)")
	}

	rows := []string{
		homeRow("mode", textStyle.Render(string(c.Mode))),
		homeRow("url", textStyle.Render(url)),
		// "write token" is exactly homeLabelW (11) chars, so %-11s pads it to nothing and the value
		// collides with the label ("write tokenpresent"). Labels in this gutter must be ≤10.
		homeRow("token", tok),
		homeRow("streams", conn(c.EventsConnected, "events")+"   "+
			conn(c.TranscriptConnected, "transcript")),
		homeRow("seq", subtleStyle.Render(fmt.Sprintf("events %d · transcript %d · console %d",
			m.data.LastEventSeq, m.data.LastTxSeq, m.consoleSeq))),
		// The cadences are the code's real constants (messages.go), not aspirations.
		homeRow("poll", subtleStyle.Render("state 1s · spinner 120ms · toast anim 33ms")),
	}
	if s := m.data.Plan; s != nil {
		if s.RunId != "" {
			rows = append(rows, homeRow("run id", textStyle.Render(s.RunId)))
		}
		if s.StateDir != "" {
			rows = append(rows, homeRow("state dir", subtleStyle.Render(s.StateDir)))
		}
	}
	if c.LastError != nil && *c.LastError != "" {
		rows = append(rows, homeRow("last error", destructStyle.Render(*c.LastError)))
	}
	return homeSection("Run internals", rows...)
}

// --- U2.3: per-session stats --------------------------------------------------

// renderDevSessionStats is the per-session token/cost table. The numbers are SUMMED server-side from
// the `costs` table (GET /sessions, U2.2) — the sessions table stores none of them.
//
// A session showing real cost against 0 tokens is NOT a bug here: every claude-native session before
// bug #5 (ClaudeProvider never read the `usage` object, fixed in 71fa214) recorded exactly that. This
// table renders what the database says and lets that read as odd, rather than hiding the row or
// back-filling a plausible number — the whole point of a developer screen.
func (m Model) renderDevSessionStats() string {
	if len(m.data.Sessions) == 0 {
		return homeSection("Session stats", subtleStyle.Render("no sessions yet"))
	}
	rows := []string{subtleStyle.Render(fmt.Sprintf("  %-4s %-5s %-8s %-8s %-8s %-9s %s",
		"#", "stage", "in", "out", "reason", "cache-read", "cost"))}
	var zeroTokenRows int
	for _, r := range m.data.Sessions {
		if r.TokensIn == 0 && r.TokensOut == 0 && r.CostUsd > 0 {
			zeroTokenRows++
		}
		cost := subtleStyle.Render(pad("—", 7))
		if r.CostUsd > 0 {
			cost = peachStyle.Render(pad(fmtCost(r.CostUsd), 7))
		}
		row := textStyle.Render(pad(fmt.Sprintf("#%d", r.Number), 4)) + " " +
			accentStyle.Render(pad(r.StageId, 5)) + " " +
			textStyle.Render(pad(widgets.FmtTokens(r.TokensIn), 8)) + " " +
			textStyle.Render(pad(widgets.FmtTokens(r.TokensOut), 8)) + " " +
			textStyle.Render(pad(widgets.FmtTokens(r.TokensThink), 8)) + " " +
			textStyle.Render(pad(widgets.FmtTokens(r.TokensCache), 9)) + " " +
			cost
		rows = append(rows, "  "+lipgloss.NewStyle().MaxWidth(max(1, m.paneCols()-2)).Render(row))
	}
	// Name the known cause rather than letting a developer debug the Face for an engine-side gap.
	// Hard-clipped: a note that word-wraps loses its indent and shears the section (STYLE.md).
	if zeroTokenRows > 0 {
		note := fmt.Sprintf("%s with cost but zero tokens — pre-bug-#5 data, not a Face bug",
			plural(zeroTokenRows, "session"))
		rows = append(rows, "", "  "+subtleStyle.Render(
			lipgloss.NewStyle().MaxWidth(max(1, m.paneCols()-2)).Render(note)))
	}
	return homeSection("Session stats", rows...)
}

// renderResultTable renders a result grid with padded, aligned columns and horizontal scrolling for
// tables wider than the pane (←→ when the query isn't focused).
func (m Model) renderResultTable(res *api.QueryResultDto) []string {
	if res.Error != nil {
		return []string{destructStyle.Render("error: " + *res.Error)}
	}
	if len(res.Columns) == 0 {
		return []string{subtleStyle.Render("no rows")}
	}

	widths := make([]int, len(res.Columns))
	for i, c := range res.Columns {
		widths[i] = len([]rune(c))
	}
	for _, row := range res.Rows {
		for i, v := range row.Values {
			if i < len(widths) && len([]rune(v)) > widths[i] {
				widths[i] = len([]rune(v))
			}
		}
	}
	// Build each row as full (untruncated) plain text, then window horizontally.
	full := func(vals []string) string {
		var cells []string
		for i, v := range vals {
			w := 8
			if i < len(widths) {
				w = widths[i]
			}
			cells = append(cells, v+strings.Repeat(" ", max(0, w-len([]rune(v)))))
		}
		return strings.Join(cells, "  ")
	}
	// Clamp the horizontal offset to the widest row so scroll-right can't run off into blank space.
	rowWidth := len([]rune(full(res.Columns)))
	maxOff := rowWidth - m.paneCols()
	if maxOff < 0 {
		maxOff = 0
	}
	off := min(m.reportHScroll*reportHScrollStep, maxOff)
	hclip := func(s string) string {
		r := []rune(s)
		if off < len(r) {
			r = r[off:]
		} else {
			r = nil
		}
		return lipgloss.NewStyle().MaxWidth(m.paneCols()).Render(string(r))
	}

	out := []string{accentStyle.Render(hclip(full(res.Columns)))}
	for _, row := range res.Rows {
		out = append(out, textStyle.Render(hclip(full(row.Values))))
	}
	if res.Truncated {
		out = append(out, warnStyle.Render("… truncated"))
	}
	foot := pluralRows(len(res.Rows))
	if maxOff > 0 {
		foot += fmt.Sprintf(" · cols %d–… (←→ scroll)", off+1)
	}
	out = append(out, subtleStyle.Render(foot))
	return out
}

func pluralRows(n int) string {
	if n == 1 {
		return "1 row"
	}
	return fmt.Sprintf("%d rows", n)
}
