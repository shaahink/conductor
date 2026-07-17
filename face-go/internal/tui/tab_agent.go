package tui

// The Agent tab is mission control: a live status strip (session · checkpoint · gates · current
// task · attention) over the streaming transcript. Everything about "what is happening right now"
// is on this one screen — no tab-hopping to see the process.

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

func (m Model) handleAgentKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k":
		m.transcript = m.transcript.Update(widgets.MsgScrollUp)
	case "down", "j":
		m.transcript = m.transcript.Update(widgets.MsgScrollDown)
	case "pgup":
		m.transcript = m.transcript.Update(widgets.MsgScrollPageUp)
	case "pgdown":
		m.transcript = m.transcript.Update(widgets.MsgScrollPageDown)
	case "end", "l":
		m.transcript = m.transcript.Update(widgets.MsgScrollEnd)
	case "f":
		m.transcript = m.transcript.Update(widgets.MsgToggleFold)
	case "T":
		m.transcript = m.transcript.Update(widgets.MsgToggleThinking)
	case "n":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.transcript.Update(widgets.MsgNextMatch)
		}
	case "N":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.transcript.Update(widgets.MsgPrevMatch)
		}
	}
	return m, nil
}

func (m Model) renderAgentPane() (string, string) {
	help := "↑↓ scroll · f fold · T thinking · / search · end live-tail"
	if m.data.Plan == nil {
		return m.renderSplash(), help
	}

	strip := m.renderAgentStrip()
	m.transcript.Height -= lipgloss.Height(strip)
	if m.transcript.Height < 3 {
		m.transcript.Height = 3
	}
	return strip + "\n" + m.transcript.View(), help
}

// renderAgentStrip is the glanceable header: status+session on line 1, gates+task on line 2,
// an attention banner when the engine needs a human, then a hairline rule.
func (m Model) renderAgentStrip() string {
	s := m.data.Plan
	w := m.paneCols()

	// Line 1 — session · checkpoint, elapsed pinned right. (Run status lives in the top bar.)
	var segs []string
	if s.SessionNumber > 0 {
		seg := accentStyle.Render("s"+fmt.Sprint(s.SessionNumber)) + " " +
			textStyle.Render(s.SessionKind)
		// "attempt 0/0" is pre-first-attempt noise, not information — render only when real.
		if s.MaxAttempts > 0 {
			seg += subtleStyle.Render(fmt.Sprintf(" · attempt %d/%d", s.Attempt, s.MaxAttempts))
		}
		if s.Model != "" {
			seg += subtleStyle.Render(" · ") + tealStyle.Render(shortModel(s.Model))
		}
		if s.Persona != nil && *s.Persona != "" {
			seg += subtleStyle.Render(" · ") + tealStyle.Render(*s.Persona)
		}
		segs = append(segs, seg)
	}
	if s.CurrentCheckpoint != "" {
		segs = append(segs, accentStyle.Render("◆ "+s.CurrentCheckpoint)+" "+subtleStyle.Render(truncate(s.CurrentCheckpointTitle, 32)))
	}
	left := strings.Join(segs, "  ")
	right := ""
	if s.AgentActive {
		right = safeStyle.Render(widgets.Spinner(m.spinnerFrame)) + " " + subtleStyle.Render(widgets.FmtWall(s.SessionElapsedSec))
	}
	line1 := padBetween(left, right, w)

	// Line 2 — gate chips left, current task right.
	gates := widgets.GateChips(s.Gates, w)
	task := m.currentTaskSegment(w - lipgloss.Width(gates) - 3)
	line2 := padBetween(gates, task, w)

	rows := []string{line1, line2}

	if reason := attentionReason(s.Status, s.AttentionReason); reason != "" {
		banner := destructStyle.Bold(true).Render("⚠ needs human — ") + warnStyle.Render(truncate(reason, w-16))
		rows = append(rows, banner)
	}

	// Live mode only: if the poll/stream has dropped, say so loudly — the strip below is last-known
	// state, not "what's happening now". (Demo mode is always "connected".)
	if m.data.Connection.Mode == api.ModeLive && !m.data.Connection.Connected {
		rows = append(rows, peachStyle.Bold(true).Render("● disconnected")+subtleStyle.Render(" — showing last-known state; retrying…"))
	}

	rule := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(strings.Repeat("─", max(1, w)))
	rows = append(rows, rule)
	return strings.Join(rows, "\n")
}

// shortModel compresses a model id for the one-line strip: "claude-opus-4-8" → "opus-4-8".
// Non-Claude ids pass through untouched — the strip should never guess at unknown vendors.
func shortModel(id string) string {
	return strings.TrimPrefix(id, "claude-")
}

// currentTaskSegment shows live MCP task progress: "task 3/4 ▸ Wire RunDb…".
func (m Model) currentTaskSegment(maxW int) string {
	tasks := m.data.Tasks
	if len(tasks) == 0 || maxW < 12 {
		return ""
	}
	done := 0
	current := ""
	for _, t := range tasks {
		if t.Status == "done" {
			done++
		} else if current == "" && t.Status == "in_progress" {
			current = t.Title
		}
	}
	seg := subtleStyle.Render(fmt.Sprintf("task %d/%d", done, len(tasks)))
	if current != "" {
		seg += tealStyle.Render(" ▸ " + truncate(current, maxW-12))
	}
	return seg
}

func attentionReason(status string, reason *string) string {
	if reason != nil && *reason != "" {
		return *reason
	}
	lower := strings.ToLower(status)
	if strings.Contains(lower, "attention") || strings.Contains(lower, "human") || strings.Contains(lower, "stall") {
		return status
	}
	return ""
}

// renderSplash is the empty state shown before a run is attached (live mode, engine not up yet).
func (m Model) renderSplash() string {
	mark := accentStyle.Render("◆ conductor") + subtleStyle.Render("  — the autonomous engineering conductor")
	how := subtleStyle.Render("No run attached.") + "\n\n" +
		textStyle.Render("Start one:") + "\n" +
		subtleStyle.Render("  conductor run --control-plane -p plans/<your>.plan.json") + "\n\n" +
		subtleStyle.Render("This face auto-discovers .conductor/control-plane.json and attaches.") + "\n" +
		subtleStyle.Render("Or explore offline:  conductor-face --demo")
	target := subtleStyle.Render("waiting on ") + tealStyle.Render(m.baseURL)

	body := mark + "\n\n" + how + "\n\n" + target
	// Center-ish vertically in the pane without a hard dependency on exact pane height.
	pad := (m.paneRows() - lipgloss.Height(body)) / 3
	if pad > 0 {
		body = strings.Repeat("\n", pad) + body
	}
	return body
}

// padBetween joins left and right with enough spaces that right lands on the pane's right edge.
//
// When they do not both fit, the LEFT is sacrificed. MaxWidth truncates from the right edge, so the
// old `MaxWidth(left + " " + right)` ate the right segment — the elapsed clock, which is pinned there
// precisely because it must stay visible — while the left segments stayed whole. That is dogfood
// appendix item 8: at ~100 cols the agent strip dropped its elapsed and kept everything else.
func padBetween(left, right string, width int) string {
	if width < 1 {
		return ""
	}
	if right == "" {
		return lipgloss.NewStyle().MaxWidth(width).Render(left)
	}
	gap := width - lipgloss.Width(left) - lipgloss.Width(right)
	if gap < 1 {
		rw := lipgloss.Width(right)
		// Not even the right half fits: it is still the more important one, so keep as much of it
		// as the width allows rather than showing a left segment that is also cut off.
		if rw+2 > width {
			return lipgloss.NewStyle().MaxWidth(width).Render(right)
		}
		left = lipgloss.NewStyle().MaxWidth(width - rw - 1).Render(left)
		return left + " " + right
	}
	return left + strings.Repeat(" ", gap) + right
}
