package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"
)

// sessionOutcomeStyle colours a session outcome against the engine's real outcome vocabulary
// (RunLoop verdicts). Normalised so "NeedsHuman", "needs-human", "needshuman" all match — the old
// map missed AgentError/TimedOut/NeedsHuman/RolledOver, so an error session rendered plain grey.
func sessionOutcomeStyle(outcome string) lipgloss.Style {
	norm := strings.Map(func(r rune) rune {
		if r >= 'a' && r <= 'z' {
			return r
		}
		if r >= 'A' && r <= 'Z' {
			return r + 32
		}
		return -1 // drop digits, spaces, dashes
	}, outcome)
	switch norm {
	case "advanced", "progress", "completed", "confirmed", "done":
		return safeStyle // green — real forward motion
	case "needshuman":
		return peachStyle // attention — the run parked for a human
	case "limitbackoff", "rolledover", "backoff", "ratelimited":
		return warnStyle // transient — no attempt burned, the loop retries itself
	case "gatesred", "stalled", "timedout", "agenterror", "authfailed", "noprogress", "needsretry", "failed", "error", "interrupted":
		return destructStyle // red — a failure the loop had to react to
	default:
		return subtleStyle
	}
}

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
			oStyle = sessionOutcomeStyle(outcome)
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
