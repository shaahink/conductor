package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"
)

// The Knowledge tab (M7): the run's memory made visible — OPEN tracked bugs on top, the knowledge
// ledger below. These are the same run.db rows the engine injects into the next session's prompt, so
// the owner can see exactly what the next agent will be told not to re-discover or re-find.
func (m Model) handleKnowledgeKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "r":
		return m, m.cmdFetchKnowledge()
	case "up":
		if m.knowledgeScroll > 0 {
			m.knowledgeScroll--
		}
	case "down", "j":
		m.knowledgeScroll++ // renderer clamps to the content height
	}
	return m, nil
}

func (m Model) renderKnowledgePane() (string, string) {
	lines := m.knowledgeLines()
	if len(lines) == 0 {
		return subtleStyle.Render("No knowledge recorded yet — `conductor note` and `conductor bug new` fill this in."), "r refresh"
	}

	window := m.paneRows()
	maxScroll := len(lines) - window
	if maxScroll < 0 {
		maxScroll = 0
	}
	scroll := m.knowledgeScroll
	if scroll > maxScroll {
		scroll = maxScroll
	}
	end := scroll + window
	if end > len(lines) {
		end = len(lines)
	}

	shown := strings.Join(lines[scroll:end], "\n")
	help := fmt.Sprintf("%d bugs · %d ledger · ↑↓ scroll · r refresh", len(m.data.Bugs), len(m.data.Ledger))
	return shown, help
}

// knowledgeLines builds the full styled body (bugs section, then ledger) as a flat slice so the pane
// can window it with a single scroll offset.
func (m Model) knowledgeLines() []string {
	width := m.paneCols()
	var lines []string

	// ── Open bugs ──
	lines = append(lines, accentStyle.Render(fmt.Sprintf("◆ Open bugs (%d)", len(m.data.Bugs))))
	if len(m.data.Bugs) == 0 {
		lines = append(lines, safeStyle.Render("  none open — clean"))
	}
	for _, b := range m.data.Bugs {
		stage := ""
		if b.StageId != nil && *b.StageId != "" {
			stage = subtleStyle.Render(" ["+*b.StageId+"]")
		}
		head := fmt.Sprintf("  %s %s%s %s",
			peachStyle.Render(fmt.Sprintf("#%d", b.Id)),
			severityStyle(b.Severity).Render("("+b.Severity+")"),
			stage,
			textStyle.Render(truncate(b.Title, width-18)))
		lines = append(lines, head)
		if b.Detail != nil && strings.TrimSpace(*b.Detail) != "" {
			detail := strings.ReplaceAll(strings.ReplaceAll(*b.Detail, "\r", " "), "\n", " ")
			lines = append(lines, subtleStyle.Render("     "+truncate(detail, width-6)))
		}
	}

	lines = append(lines, "")

	// ── Knowledge ledger ──
	lines = append(lines, accentStyle.Render(fmt.Sprintf("◆ Knowledge ledger (%d)", len(m.data.Ledger))))
	if len(m.data.Ledger) == 0 {
		lines = append(lines, subtleStyle.Render("  empty — nothing noted this run"))
	}
	for _, e := range m.data.Ledger {
		where := ""
		if e.SessionNumber != nil {
			where = fmt.Sprintf(" (s%d", *e.SessionNumber)
			if e.StageId != nil && *e.StageId != "" {
				where += "/" + *e.StageId
			}
			where += ")"
		} else if e.StageId != nil && *e.StageId != "" {
			where = " (" + *e.StageId + ")"
		}
		content := strings.ReplaceAll(strings.ReplaceAll(e.Content, "\r", " "), "\n", " ")
		line := fmt.Sprintf("  %s %s%s",
			tealStyle.Render("["+e.Kind+"]"),
			textStyle.Render(truncate(content, width-len(e.Kind)-len(where)-6)),
			subtleStyle.Render(where))
		lines = append(lines, line)
	}
	return lines
}

func severityStyle(sev string) lipgloss.Style {
	switch sev {
	case "high":
		return destructStyle
	case "low":
		return subtleStyle
	default:
		return warnStyle
	}
}
