package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/templates"
	"conductor-face-go/internal/widgets"
)

func (m Model) handleTemplatesKey(key string) (tea.Model, tea.Cmd) {
	if m.promptPreviewOn {
		if key == "esc" || key == "v" {
			m.promptPreviewOn = false
		}
		return m, nil
	}
	if m.promptMode == PromptEdit {
		switch key {
		case "esc":
			m.promptMode = PromptList
		case "ctrl+s":
			if m.promptSelected < len(m.promptEntries) {
				entry := m.promptEntries[m.promptSelected]
				if err := templates.Write(entry.Path, m.promptContent); err != nil {
					return m, m.addToast("Save failed: "+err.Error(), widgets.ToastError)
				}
				m.promptEntries[m.promptSelected].Exists = true
				return m, m.addToast("Saved "+entry.Label, widgets.ToastSuccess)
			}
		case "enter":
			m.promptContent += "\n"
		case "backspace":
			if len(m.promptContent) > 0 {
				m.promptContent = m.promptContent[:len(m.promptContent)-1]
			}
		default:
			if ch, ok := typedChar(key); ok {
				m.promptContent += ch
			}
		}
		return m, nil
	}
	switch key {
	case "v":
		m.promptPreviewOn, m.promptPreview, m.promptPreviewErr = true, nil, ""
		return m, m.cmdFetchPromptPreview(m.currentStageId(), "Deliver")
	case "up", "k":
		if m.promptSelected > 0 {
			m.promptSelected--
		}
	case "down", "j":
		if m.promptSelected < len(m.promptEntries)-1 {
			m.promptSelected++
		}
	case "enter":
		if m.promptSelected < len(m.promptEntries) {
			m.promptMode = PromptEdit
			m.promptContent = templates.Read(m.promptEntries[m.promptSelected].Path)
		}
	}
	return m, nil
}

// renderTemplatesPane: template list on the left; editor or compiled-prompt preview on the right —
// all on one page.
func (m Model) renderTemplatesPane() (string, string) {
	var left []string
	if len(m.promptEntries) == 0 {
		left = append(left, subtleStyle.Render("(no plan dir yet)"))
	}
	for i, e := range m.promptEntries {
		status := safeStyle.Render("●")
		if !e.Exists {
			status = subtleStyle.Render("○")
		}
		row := fmt.Sprintf("%s %s", status, e.Label)
		if i == m.promptSelected {
			row = highlightBg.Render(fmt.Sprintf("%s %s", "•", e.Label))
		}
		left = append(left, row)
	}
	leftCol := lipgloss.NewStyle().Width(26).Render(strings.Join(left, "\n"))

	var right string
	switch {
	case m.promptPreviewOn:
		right = m.templatesPreview()
	case m.promptMode == PromptEdit && m.promptSelected < len(m.promptEntries):
		right = accentStyle.Render("editing "+m.promptEntries[m.promptSelected].Label) + "\n\n" + textStyle.Render(m.promptContent) + accentStyle.Render("▏")
	default:
		hint := "enter edit · v compiled preview"
		right = subtleStyle.Render("Select a template on the left.\n\n") +
			subtleStyle.Render("● on disk   ○ built-in default\n\nSaved to planDir — the engine hot-reloads\nat the next session.\n\n") + subtleStyle.Render(hint)
	}
	rightCol := lipgloss.NewStyle().Width(m.paneCols() - 30).Render(right)

	body := lipgloss.JoinHorizontal(lipgloss.Top, leftCol, subtleStyle.Render("│ "), rightCol)
	help := "↑↓ select · enter edit · v preview"
	if m.promptMode == PromptEdit {
		help = "ctrl+s save · esc back"
	}
	return body, help
}

func (m Model) templatesPreview() string {
	stage := m.currentStageId()
	if stage == "" {
		stage = "(none)"
	}
	head := subtleStyle.Render("compiled · stage ") + accentStyle.Render(stage) + "\n\n"
	if m.promptPreviewErr != "" {
		return head + destructStyle.Render("error: "+m.promptPreviewErr)
	}
	if m.promptPreview == nil {
		return head + subtleStyle.Render("compiling…")
	}
	meta := subtleStyle.Render(fmt.Sprintf("model %s · kind %s", m.promptPreview.Model, m.promptPreview.Kind))
	return head + meta + "\n\n" + textStyle.Render(truncateLines(m.promptPreview.Prompt, m.paneRows()-4))
}
