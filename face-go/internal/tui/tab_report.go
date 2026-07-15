package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

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

func (m Model) handleReportKey(key string) (tea.Model, tea.Cmd) {
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

func (m Model) renderReportPane() (string, string) {
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
	return strings.Join(lines, "\n"), "tab focus · ↑↓ pick · ←→ scroll cols · enter run"
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
