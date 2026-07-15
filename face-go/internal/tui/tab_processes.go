package tui

import (
	"fmt"
	"strings"
	"time"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

func (m Model) handleProcessesKey(key string) (tea.Model, tea.Cmd) {
	if m.processKilling {
		switch strings.ToLower(key) {
		case "y", "enter":
			m.processKilling = false
			if p, ok := m.selectedProcess(); ok && p.Alive {
				return m, m.cmdPostProcessKill(p.Pid)
			}
			return m, nil
		case "n", "esc":
			m.processKilling = false
		}
		return m, nil
	}
	switch key {
	case "up", "k":
		if m.processSelected > 0 {
			m.processSelected--
		}
	case "down", "j":
		if m.processSelected < len(m.data.Processes)-1 {
			m.processSelected++
		}
	case "x": // kill the selected process (only if it's still alive) — x avoids the k=Knowledge mnemonic
		if p, ok := m.selectedProcess(); ok && p.Alive {
			m.processKilling = true
		}
	}
	return m, nil
}

func (m Model) selectedProcess() (api.ProcessDto, bool) {
	if m.processSelected >= 0 && m.processSelected < len(m.data.Processes) {
		return m.data.Processes[m.processSelected], true
	}
	return api.ProcessDto{}, false
}

func (m Model) renderProcessesPane() (string, string) {
	if len(m.data.Processes) == 0 {
		return subtleStyle.Render("(no supervised processes right now)"), ""
	}
	header := subtleStyle.Render(fmt.Sprintf("  %-6s %-22s %-6s %s", "PID", "PURPOSE", "STAGE", "RUNTIME"))
	lines := []string{header}
	for i, p := range m.data.Processes {
		glyph, st := "○", subtleStyle
		if p.Alive {
			glyph, st = "●", safeStyle
		}
		stage := "-"
		if p.StageId != nil {
			stage = *p.StageId
		}
		row := fmt.Sprintf("%-6d %-22s %-6s %s", p.Pid, truncate(p.Purpose, 22), stage, formatProcessRuntime(p))
		if i == m.processSelected {
			lines = append(lines, highlightBg.Render(glyph+" "+row))
			continue
		}
		lines = append(lines, st.Render(glyph)+" "+textStyle.Render(row))
	}
	if m.processSelected < len(m.data.Processes) {
		p := m.data.Processes[m.processSelected]
		if p.LastOutputLine != nil {
			lines = append(lines, "", subtleStyle.Render("last: ")+tealStyle.Render(truncate(*p.LastOutputLine, m.paneCols()-8)))
		}
	}
	if m.processKilling {
		if p, ok := m.selectedProcess(); ok {
			lines = append(lines, "", "  "+destructStyle.Render("⚠ kill ")+accentStyle.Render(fmt.Sprintf("pid %d", p.Pid))+destructStyle.Render(" ("+truncate(p.Purpose, 20)+") ?")+"  "+warnStyle.Render("y/N"))
		}
		return strings.Join(lines, "\n"), "y confirm · n cancel"
	}
	help := "↑↓ navigate"
	if p, ok := m.selectedProcess(); ok && p.Alive {
		help = "↑↓ navigate · x kill"
	}
	return strings.Join(lines, "\n"), help
}

func formatProcessRuntime(p api.ProcessDto) string {
	start, err := time.Parse(time.RFC3339, p.StartedUtc)
	if err != nil {
		return ""
	}
	end := time.Now()
	if p.ExitedUtc != nil {
		if t, err := time.Parse(time.RFC3339, *p.ExitedUtc); err == nil {
			end = t
		}
	}
	sec := int(end.Sub(start).Seconds())
	if sec < 0 {
		sec = 0
	}
	if sec >= 60 {
		return fmt.Sprintf("%dm%02ds", sec/60, sec%60)
	}
	return fmt.Sprintf("%ds", sec)
}
