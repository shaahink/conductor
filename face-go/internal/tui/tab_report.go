package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
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

func (m Model) handleReportKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		if m.reportFocusQuery {
			m.reportFocusQuery = false
			return m, nil
		}
		return m.openTab(TabAgent)
	case "tab":
		m.reportFocusQuery = !m.reportFocusQuery
	case "up", "k":
		if !m.reportFocusQuery && m.reportQuickSelected > 0 {
			m.reportQuickSelected--
		}
	case "down", "j":
		if !m.reportFocusQuery && m.reportQuickSelected < len(quickQueries)-1 {
			m.reportQuickSelected++
		}
	case "enter":
		sql := m.reportSQL
		if !m.reportFocusQuery {
			sql = quickQueries[m.reportQuickSelected].SQL
			m.reportSQL = sql
		}
		m.data.ReportLoading = true
		return m, m.cmdQueryReport(sql)
	case "backspace":
		if m.reportFocusQuery && len(m.reportSQL) > 0 {
			m.reportSQL = m.reportSQL[:len(m.reportSQL)-1]
		}
	default:
		if ch, ok := typedChar(key); m.reportFocusQuery && ok {
			m.reportSQL += ch
		}
	}
	return m, nil
}

func (m Model) renderReportPane() (string, string) {
	var lines []string
	lines = append(lines, subtleStyle.Render("Quick queries:"))
	for i, q := range quickQueries {
		marker := "  "
		if i == m.reportQuickSelected && !m.reportFocusQuery {
			marker = accentStyle.Render("› ")
		}
		lines = append(lines, marker+q.Label)
	}
	lines = append(lines, "")
	sf := subtleStyle
	if m.reportFocusQuery {
		sf = accentStyle
	}
	sqlDisplay := m.reportSQL
	if m.reportFocusQuery {
		sqlDisplay += "▏"
	}
	lines = append(lines, sf.Render("SQL:"), textStyle.Render(truncate(sqlDisplay, m.paneCols())))
	if m.data.ReportLoading {
		lines = append(lines, "", subtleStyle.Render("running…"))
	} else if m.data.ReportResult != nil {
		lines = append(lines, "")
		lines = append(lines, m.renderResultTable(m.data.ReportResult)...)
	}
	return strings.Join(lines, "\n"), "tab focus · ↑↓ pick · enter run"
}

// renderResultTable renders a result grid with padded, aligned columns.
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
	maxW := m.paneCols()
	pad := func(vals []string) string {
		var cells []string
		for i, v := range vals {
			w := 8
			if i < len(widths) {
				w = widths[i]
			}
			cells = append(cells, v+strings.Repeat(" ", max(0, w-len([]rune(v)))))
		}
		return truncate(strings.Join(cells, "  "), maxW)
	}

	out := []string{accentStyle.Render(pad(res.Columns))}
	for _, row := range res.Rows {
		out = append(out, textStyle.Render(pad(row.Values)))
	}
	if res.Truncated {
		out = append(out, warnStyle.Render("… truncated"))
	}
	out = append(out, subtleStyle.Render(pluralRows(len(res.Rows))))
	return out
}

func pluralRows(n int) string {
	if n == 1 {
		return "1 row"
	}
	return fmt.Sprintf("%d rows", n)
}
