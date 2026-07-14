package widgets

import (
	"strings"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

type SidebarModel struct {
	Stages   []api.StageDto
	Gates    []api.GateDto
	Expanded map[string]bool
	Selected int
	Width    int
	Height   int
}

func NewSidebar() SidebarModel {
	return SidebarModel{
		Expanded: make(map[string]bool),
		Width:    30,
		Height:   20,
	}
}

func (m SidebarModel) Init() SidebarModel { return m }

func (m SidebarModel) Update(msg any) SidebarModel {
	switch msg := msg.(type) {
	case MsgSetData:
		if stages, ok := msg.Stages.([]api.StageDto); ok {
			m.Stages = stages
			for _, s := range stages {
				if s.State == "active" || s.State == "gating" {
					m.Expanded[s.Id] = true
				}
			}
		}
		if gates, ok := msg.Gates.([]api.GateDto); ok {
			m.Gates = gates
		}
		return m

	case MsgToggleStage:
		if m.Expanded[msg.StageId] {
			delete(m.Expanded, msg.StageId)
		} else {
			m.Expanded[msg.StageId] = true
		}
		return m

	case WidgetMsg:
		switch msg {
		case MsgSelectUp:
			if m.Selected > 0 {
				m.Selected--
			}
			return m

		case MsgSelectDown:
			if m.Selected < m.lineCount()-1 {
				m.Selected++
			}
			return m

		case MsgSelectExpand:
			m.expandSelected()
			return m
		}
	}
	return m
}

func (m SidebarModel) View() string {
	var sb strings.Builder

	title := sidebarTitleStyle.Render("\u25B6 PLAN")
	sb.WriteString(title)
	sb.WriteByte('\n')

	lineIdx := 0
	for _, stage := range m.Stages {
		glyph, style := stageGlyph(stage.State)
		line := style.Render(glyph + " " + stage.Id + " " + truncate(stage.Title, m.Width-8))
		if lineIdx == m.Selected {
			line = highlightStyle.Render(line)
		}
		sb.WriteString(line)
		sb.WriteByte('\n')
		lineIdx++

		expanded := m.Expanded[stage.Id]
		if expanded && len(stage.Checkpoints) > 0 {
			for _, cp := range stage.Checkpoints {
				cg, cs := checkpointGlyph(cp.Status)
				cLine := "  " + cs.Render(cg+" "+cp.Id+" "+truncate(cp.Title, m.Width-10))
				if lineIdx == m.Selected {
					cLine = highlightStyle.Render(cLine)
				}
				sb.WriteString(cLine)
				sb.WriteByte('\n')
				lineIdx++
			}
		}
	}

	sb.WriteByte('\n')
	sb.WriteString(dimStyle.Render("\u2500\u2500 GATES \u2500\u2500"))
	sb.WriteByte('\n')

	for _, gate := range m.Gates {
		g, gs := gateGlyph(gate.State)
		gLine := "  " + gs.Render(g+" "+gate.Name)
		sb.WriteString(gLine)
		sb.WriteByte('\n')
		lineIdx++
	}

	content := sb.String()
	return lipgloss.NewStyle().
		Width(m.Width).Height(m.Height).
		Render(content)
}

func (m *SidebarModel) expandSelected() {
	idx := 0
	for _, stage := range m.Stages {
		if idx == m.Selected {
			m.Expanded[stage.Id] = !m.Expanded[stage.Id]
			return
		}
		idx++
		if m.Expanded[stage.Id] {
			for range stage.Checkpoints {
				idx++
				if idx == m.Selected {
					return
				}
			}
		}
	}
}

func (m SidebarModel) lineCount() int {
	count := 1 // title
	for _, stage := range m.Stages {
		count++
		if m.Expanded[stage.Id] || stage.State == "active" {
			count += len(stage.Checkpoints)
		}
	}
	count += 2 // gates header
	count += len(m.Gates)
	return count
}

func stageGlyph(state string) (string, lipgloss.Style) {
	switch state {
	case "confirmed", "done":
		return "\u2713 ", stageDoneStyle
	case "active", "gating":
		return "\u25CF ", stageActiveStyle
	case "failed":
		return "\u2717 ", stageFailStyle
	case "skipped":
		return "\u2298 ", stageSkippedStyle
	default:
		return "\u25CB ", stageTodoStyle
	}
}

func checkpointGlyph(status string) (string, lipgloss.Style) {
	switch status {
	case "done", "confirmed":
		return "\u2713 ", cpDoneStyle
	case "in_progress":
		return "\u25CF ", cpActiveStyle
	case "skipped":
		return "\u2298 ", cpSkippedStyle
	default:
		return "\u25CB ", cpTodoStyle
	}
}

func gateGlyph(state string) (string, lipgloss.Style) {
	switch state {
	case "pass":
		return "\u2713 ", gatePassStyle
	case "running":
		return "\u25CF ", gateRunningStyle
	case "fail":
		return "\u2717 ", gateFailStyle
	case "skip":
		return "\u2298 ", gateSkipStyle
	default:
		return "\u25CB ", gatePendingStyle
	}
}
