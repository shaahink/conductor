package tui

import (
	"fmt"
	"strings"

	"charm.land/bubbles/v2/viewport"
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
	// vp is the pane's viewport (KS2.7). renderProcessesPane emitted every supervised process
	// unwindowed; on a long-running plan the rows past the pane's height were eaten by frameContent's
	// MaxHeight, and the last-output line under the selection went with them. `selected` stays — it is
	// a SELECTION cursor (the `x` kill acts on it), and the viewport follows it via ensurePaneRow.
	vp viewport.Model
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
	// This tab's own semantic keys first (↑↓ move the selection, `x` kills it), then the one
	// pane-scroll set against a viewport that has just been sized and loaded (adr/0006 §1).
	switch key {
	case "up", "k":
		if m.processes.selected > 0 {
			m.processes.selected--
			m.processes.vp = m.followProcessSelection()
			return m, nil
		}
	case "down", "j":
		if m.processes.selected < len(m.data.Processes)-1 {
			m.processes.selected++
			m.processes.vp = m.followProcessSelection()
			return m, nil
		}
	case "x": // kill the selected process (only if it's still alive) — x avoids the k=Knowledge mnemonic
		if p, ok := m.selectedProcess(); ok && p.Alive {
			m.processes.killing = true
		}
		return m, nil
	case readerOpenKey:
		// KS2.8: the selected row's `last:` line is agent output truncated to the pane; the reader
		// is where it (and the row's own clipped purpose) reads whole.
		title, body := m.processReaderDoc()
		return m.openReader(title, body, false), nil
	}
	m.processes.vp = m.processesViewport()
	applyPaneScroll(&m.processes.vp, key)
	return m, nil
}

// processReaderDoc is the selected process as one plain document: the row's cells unclipped, and
// the full last-output line the pane truncates.
func (m Model) processReaderDoc() (title, body string) {
	p, ok := m.selectedProcess()
	if !ok {
		return "", ""
	}
	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("pid %d · %s\n", p.Pid, p.Purpose))
	state := "exited"
	if p.Alive {
		state = "alive"
	}
	stage := "-"
	if p.StageId != nil {
		stage = *p.StageId
	}
	sb.WriteString(fmt.Sprintf("\nstage %s · %s · %s\n", stage, state, formatProcessRuntime(p)))
	if p.LastOutputLine != nil && strings.TrimSpace(*p.LastOutputLine) != "" {
		sb.WriteString("\nlast output\n\n" + *p.LastOutputLine + "\n")
	}
	return fmt.Sprintf("process %d", p.Pid), sb.String()
}

// processesViewport is this tab's `<surface>Viewport()` builder — the same construction Report uses,
// so the two surfaces cannot drift apart. Both the key handler and the renderer go through it.
func (m Model) processesViewport() viewport.Model {
	lines := m.processesLines()
	vp := loadPaneViewport(m.processes.vp, lines, m.paneCols(), m.paneRows(), false)
	if m.processes.killing {
		// A y/N confirm the reader cannot see is a confirm they cannot answer. It is the last row of
		// the body, so the pane goes to it rather than to the selection it is asking about.
		vp.GotoBottom()
		return vp
	}
	return vp
}

// followProcessSelection is the builder plus the cursor follow, called ONLY from the arms that moved
// the cursor (see ensurePaneRow). +1 for the header row above the list, so the selected process's
// own row sits one further down.
func (m Model) followProcessSelection() viewport.Model {
	vp := m.processesViewport()
	ensurePaneRow(&vp, min(m.processes.selected+1, max(0, len(m.processesLines())-1)))
	return vp
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
	vp := m.processesViewport()
	if m.processes.killing {
		return vp.View(), "y confirm · n cancel"
	}
	help := "↑↓ select · z read"
	if p, ok := m.selectedProcess(); ok && p.Alive {
		help = "↑↓ select · x kill · z read"
	}
	if hint := paneScrollHint(vp, false); hint != "" {
		help += " · " + hint
	}
	return vp.View(), help
}

// processesLines is the whole table — header, one row per process, the selected process's last
// output line, and the kill confirm when it is open — ready for the viewport. Built here rather than
// inside the renderer so the key handler can load the same bytes into the viewport it scrolls.
func (m Model) processesLines() []string {
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
	}
	return lines
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
