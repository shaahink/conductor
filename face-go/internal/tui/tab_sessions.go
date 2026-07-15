package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
)

func (m Model) handleSessionsKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k":
		if m.sessionSelected > 0 {
			m.sessionSelected--
		}
	case "down", "j":
		if m.sessionSelected < len(m.data.Sessions)-1 {
			m.sessionSelected++
		}
	}
	return m, nil
}

// renderSessionsPane lists sessions newest-first (the wire order) with the selected one's detail
// inline underneath — history and drill-down on one page.
func (m Model) renderSessionsPane() (string, string) {
	if len(m.data.Sessions) == 0 {
		return subtleStyle.Render("(no sessions yet — they appear as the engine runs)"), ""
	}
	var lines []string
	for i, s := range m.data.Sessions {
		outcome, oStyle := "running", warnStyle
		if s.Outcome != nil {
			outcome = *s.Outcome
			switch strings.ToLower(outcome) {
			case "completed", "advanced", "progress":
				oStyle = safeStyle
			case "needsretry", "gatesred", "stalled", "noprogress", "interrupted":
				oStyle = destructStyle
			default:
				oStyle = subtleStyle
			}
		}
		// Pad each cell as plain text first, then colour — ANSI-safe alignment (STYLE.md).
		num := fmt.Sprintf("#%-3d", s.Number)
		stage := fmt.Sprintf("%-6s", s.StageId)
		kind := fmt.Sprintf("%-8s", s.Kind)
		out := fmt.Sprintf("%-12s", outcome)
		commits := fmt.Sprintf("%d commits", s.CommitCount)
		if i == m.sessionSelected {
			lines = append(lines, highlightBg.Render("› "+num+" "+stage+" "+kind+" "+out+" "+commits))
			continue
		}
		lines = append(lines, "  "+subtleStyle.Render(num)+" "+accentStyle.Render(stage)+" "+
			textStyle.Render(kind)+" "+oStyle.Render(out)+" "+subtleStyle.Render(commits))
	}

	if m.sessionSelected < len(m.data.Sessions) {
		s := m.data.Sessions[m.sessionSelected]
		detail := fmt.Sprintf("\n%s #%d · %s %s · %s",
			accentStyle.Render("Session"), s.Number, accentStyle.Render(s.StageId), textStyle.Render(s.Kind),
			subtleStyle.Render(fmt.Sprintf("attempt %d, %d resumes", s.Attempt, s.ResumeCount)))
		if s.GateSummary != nil {
			detail += "\n" + subtleStyle.Render("Gates: ") + textStyle.Render(*s.GateSummary)
		}
		if s.ResultSummary != nil {
			detail += "\n" + subtleStyle.Render("Result:") + "\n" + indent(renderMarkdown(*s.ResultSummary, m.paneCols()-4), "  ")
		}
		lines = append(lines, detail)
	}
	return strings.Join(lines, "\n"), "↑↓ navigate"
}
