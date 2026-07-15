package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
)

func (m Model) handleConsoleKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k":
		m.consoleScroll++
	case "down", "j":
		if m.consoleScroll > 0 {
			m.consoleScroll--
		}
	case "end":
		m.consoleScroll = 0
	}
	return m, nil
}

// renderConsolePane is the native console: the agent CLI's raw stdout, exactly as it prints.
func (m Model) renderConsolePane() (string, string) {
	lines := m.data.RawConsole
	if len(lines) == 0 {
		return subtleStyle.Render("(no raw output yet — the agent tees stdout to .conductor/logs/session-NNN.jsonl)"), "↑↓ scroll · end live-tail"
	}
	window := m.paneRows() - 1
	if window < 3 {
		window = 3
	}
	end := len(lines) - m.consoleScroll
	if end < 1 {
		end = 1
	}
	if end > len(lines) {
		end = len(lines)
	}
	start := end - window
	if start < 0 {
		start = 0
	}
	var out []string
	for i := start; i < end; i++ {
		out = append(out, subtleStyle.Render(truncate(lines[i].Text, m.paneCols())))
	}
	pos := safeStyle.Render("● live tail")
	if m.consoleScroll > 0 {
		pos = warnStyle.Render(fmt.Sprintf("↕ scrolled back %d — end to live-tail", m.consoleScroll))
	}
	out = append(out, subtleStyle.Render(fmt.Sprintf("%d lines · ", len(lines)))+pos)
	return strings.Join(out, "\n"), "↑↓ scroll · end live-tail"
}
