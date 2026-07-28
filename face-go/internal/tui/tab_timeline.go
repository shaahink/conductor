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

func (m Model) handleTimelineKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "r":
		m.timelineLoading, m.timelineErr = true, ""
		return m, m.cmdFetchTimeline()
	case "up", "k":
		if m.timelineSelected > 0 {
			m.timelineSelected--
		}
	case "down", "j":
		if m.timelineSelected < len(m.timelineEntries)-1 {
			m.timelineSelected++
		}
	}
	return m, nil
}

// renderTimelinePane shows the run's spine — sessions, gates, stalls, verdicts, cost over time.
// It refreshes itself whenever a new engine event lands (see Update), so it is live while open.
func (m Model) renderTimelinePane() (string, string) {
	if m.timelineLoading && len(m.timelineEntries) == 0 {
		return subtleStyle.Render("loading…"), "r refresh"
	}
	if m.timelineErr != "" {
		return destructStyle.Render("error: " + m.timelineErr), "r retry"
	}
	if len(m.timelineEntries) == 0 {
		return subtleStyle.Render("(no events on the run's spine yet)"), "r refresh"
	}
	// Reserve the bottom of the pane for the selected entry's detail (full description + meta).
	detail := m.timelineDetail()
	window := m.paneRows() - lipgloss.Height(detail) - 1
	if window < 3 {
		window = 3
	}
	start := 0
	if m.timelineSelected >= window {
		start = m.timelineSelected - window + 1
	}
	end := start + window
	if end > len(m.timelineEntries) {
		end = len(m.timelineEntries)
	}
	// The rule costs a row inside the window, so the window holds one fewer event. Take it from the
	// OLDEST end: this pane is a live tail, and dropping the newest event to make room for the line
	// that says "the live ones start here" would defeat the point of drawing it.
	if b := m.timelineLiveBoundary(); b > start && b < end && start+1 < end {
		start++
	}
	var lines []string
	for i := start; i < end; i++ {
		if i == m.timelineLiveBoundary() {
			lines = append(lines, m.timelineLiveRule())
		}
		e := m.timelineEntries[i]
		glyph, gs := timelineGlyph(e)
		clock := timelineClock(e.Utc)
		desc := truncate(e.Description, m.paneCols()-16)
		cost := ""
		if e.CostUsd != nil && *e.CostUsd > 0 {
			cost = peachStyle.Render(fmt.Sprintf("  $%.2f", *e.CostUsd))
		}
		line := fmt.Sprintf("%s %s %s%s", subtleStyle.Render(clock), gs.Render(glyph), textStyle.Render(desc), cost)
		if i == m.timelineSelected {
			line = highlightBg.Render(fmt.Sprintf("%s %s %s", clock, glyph, desc))
		}
		lines = append(lines, line)
	}
	// Just the count. The "· live" this used to carry now lives on the rule, where it marks WHERE
	// live begins instead of restating it under a pane that already said it.
	count := subtleStyle.Render(fmt.Sprintf("%d events", len(m.timelineEntries)))
	return strings.Join(lines, "\n") + "\n" + count + "\n" + detail, "↑↓ navigate · r refresh"
}

// timelineLiveBoundary is the index of the first event that arrived AFTER this Face attached, or -1
// when there is no line worth drawing: nothing fetched yet, an attach to a run with no history (the
// whole pane is live — a rule at the top would say nothing), or nothing live since.
func (m Model) timelineLiveBoundary() int {
	if !m.timelineHistorySet || m.timelineHistoryCount <= 0 {
		return -1
	}
	if len(m.timelineEntries) <= m.timelineHistoryCount {
		return -1
	}
	return m.timelineHistoryCount
}

// timelineLiveRule separates replayed history from the live tail. Without it, attaching to a run
// pours its whole spine in at once and reads as an event storm you have just missed (dogfood
// appendix item 6) — the rule is what makes the top half legible as "before you got here".
func (m Model) timelineLiveRule() string {
	const label = " live "
	w := max(1, m.paneCols())
	side := (w - len(label)) / 2
	if side < 1 {
		return accentStyle.Render(strings.TrimSpace(label))
	}
	dash := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(strings.Repeat("─", side))
	return dash + accentStyle.Render(label) + dash
}

// timelineDetail renders the selected entry in full — the row truncates the description, so drilling
// in shows the whole thing plus stage/session/outcome/cost/time that don't fit on one row.
func (m Model) timelineDetail() string {
	if m.timelineSelected >= len(m.timelineEntries) {
		return ""
	}
	e := m.timelineEntries[m.timelineSelected]
	glyph, gs := timelineGlyph(e)
	rule := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(strings.Repeat("─", max(1, m.paneCols())))

	var meta []string
	if e.StageId != nil && *e.StageId != "" {
		meta = append(meta, "stage "+*e.StageId)
	}
	if e.SessionNumber != nil {
		meta = append(meta, fmt.Sprintf("session #%d", *e.SessionNumber))
	}
	if e.Outcome != nil && *e.Outcome != "" {
		meta = append(meta, "outcome "+*e.Outcome)
	}
	if e.CostUsd != nil && *e.CostUsd > 0 {
		meta = append(meta, fmt.Sprintf("$%.2f", *e.CostUsd))
	}
	meta = append(meta, timelineClock(e.Utc)+" UTC")

	title := gs.Render(glyph+" ") + accentStyle.Render(e.Kind)
	desc := lipgloss.NewStyle().MaxWidth(m.paneCols()).Render(textStyle.Render(e.Description))
	return rule + "\n" + title + "\n" + desc + "\n" + subtleStyle.Render(strings.Join(meta, " · "))
}

func timelineGlyph(e api.TimelineEntryDto) (string, lipgloss.Style) {
	switch e.Kind {
	case "session":
		return "▸", accentStyle
	case "gate":
		if e.Outcome != nil && *e.Outcome == "fail" {
			return "✗", destructStyle
		}
		return "✓", safeStyle
	case "stage":
		return "◆", accentStyle
	case "attention":
		return "⚠", warnStyle
	default:
		return "·", subtleStyle
	}
}

func timelineClock(utc string) string {
	t, err := time.Parse(time.RFC3339, utc)
	if err != nil {
		return "--:--:--"
	}
	return t.In(widgets.ClockLocation).Format("15:04:05")
}
