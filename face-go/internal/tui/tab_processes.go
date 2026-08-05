package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
	"conductor-face-go/internal/widgets"
)

// processesModel is the Processes tab's own state (K6.3): which row is selected, and whether the
// kill-confirm prompt is open for it.
type processesModel struct {
	selected int
	killing  bool
}

// updateProcesses handles the process poll and the kill result. data.Processes has exactly one
// reader — this tab — so the landing belongs here.
func (m Model) updateProcesses(msg tea.Msg) (Model, tea.Cmd, bool) {
	switch msg := msg.(type) {

	case MsgProcessesUpdated:
		if msg.Procs != nil {
			m.data.Processes = msg.Procs.Processes
		}
		return m, nil, true

	case MsgProcessKilled:
		if msg.Success {
			// Re-fetch so the row flips to exited immediately, and toast alongside it.
			return m, tea.Batch(m.addToast(fmt.Sprintf("killed pid %d", msg.Pid), widgets.ToastSuccess), m.cmdFetchProcesses()), true
		}
		reason := msg.Error
		if reason == "" {
			reason = "unknown reason"
		}
		return m, m.addToast(fmt.Sprintf("kill pid %d rejected: %s", msg.Pid, reason), widgets.ToastError), true
	}
	return m, nil, false
}

func (m Model) handleProcessesKey(key string) (tea.Model, tea.Cmd) {
	if m.processes.killing {
		switch strings.ToLower(key) {
		case "y", "enter":
			m.processes.killing = false
			if p, ok := m.selectedProcess(); ok && p.Alive {
				return m, m.cmdPostProcessKill(p.Pid)
			}
			return m, nil
		case "n", "esc":
			m.processes.killing = false
		}
		return m, nil
	}
	switch key {
	case "up", "k":
		if m.processes.selected > 0 {
			m.processes.selected--
		}
	case "down", "j":
		if m.processes.selected < len(m.data.Processes)-1 {
			m.processes.selected++
		}
	case "x": // kill the selected process (only if it's still alive) — x avoids the k=Knowledge mnemonic
		if p, ok := m.selectedProcess(); ok && p.Alive {
			m.processes.killing = true
		}
	}
	return m, nil
}

func (m Model) selectedProcess() (api.ProcessDto, bool) {
	if m.processes.selected >= 0 && m.processes.selected < len(m.data.Processes) {
		return m.data.Processes[m.processes.selected], true
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
		if i == m.processes.selected {
			lines = append(lines, highlightBg.Render(glyph+" "+row))
			continue
		}
		lines = append(lines, st.Render(glyph)+" "+textStyle.Render(row))
	}
	if m.processes.selected < len(m.data.Processes) {
		p := m.data.Processes[m.processes.selected]
		if p.LastOutputLine != nil {
			lines = append(lines, "", subtleStyle.Render("last: ")+tealStyle.Render(truncate(*p.LastOutputLine, m.paneCols()-8)))
		}
	}
	if m.processes.killing {
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

// formatProcessRuntime is how long the process has been up, or how long it ran before it exited. The
// arithmetic and the formatting now both live in timefmt: this used to carry its own %dm%02ds copy
// with no hour bucket, so a gate that had been running for three hours read "184m30s".
func formatProcessRuntime(p api.ProcessDto) string {
	start, ok := timefmt.Parse(p.StartedUtc)
	if !ok {
		return ""
	}
	end := timefmt.Now()
	if p.ExitedUtc != nil {
		if t, ok := timefmt.Parse(*p.ExitedUtc); ok {
			end = t
		}
	}
	return timefmt.Duration(end.Sub(start))
}
