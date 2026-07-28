package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/templates"
	"conductor-face-go/internal/widgets"
)

// previewKinds are the session kinds whose compiled prompt the preview can render — the engine
// builds a different template per kind, so the picker (←/→) lets you inspect each one.
var previewKinds = []string{"Deliver", "Fix", "Resume", "Audit", "Review"}

func (m Model) handleTemplatesKey(key string) (tea.Model, tea.Cmd) {
	if m.promptPreviewOn {
		switch key {
		case "esc", "v":
			m.promptPreviewOn = false
			return m, nil
		case "left", "h":
			m.promptPreviewKind = (m.promptPreviewKind - 1 + len(previewKinds)) % len(previewKinds)
			m.promptPreview, m.promptPreviewErr = nil, ""
			return m, m.cmdFetchPromptPreview(m.currentStageId(), previewKinds[m.promptPreviewKind])
		case "right", "l":
			m.promptPreviewKind = (m.promptPreviewKind + 1) % len(previewKinds)
			m.promptPreview, m.promptPreviewErr = nil, ""
			return m, m.cmdFetchPromptPreview(m.currentStageId(), previewKinds[m.promptPreviewKind])
		}
		return m, nil
	}
	if m.promptMode == PromptEdit {
		switch key {
		case "esc":
			m.promptMode = PromptList
			return m, nil
		case "ctrl+s":
			if m.promptSelected < len(m.promptEntries) {
				entry := m.promptEntries[m.promptSelected]
				if err := templates.Write(entry.Path, m.promptEditor.Value()); err != nil {
					return m, m.addToast("Save failed: "+err.Error(), widgets.ToastError)
				}
				m.promptEntries[m.promptSelected].Exists = true
				return m, m.addToast("Saved "+entry.Label, widgets.ToastSuccess)
			}
			return m, nil
		default:
			// A real editor: insert/delete at the caret, arrow/home/end/pgup-pgdn navigation.
			m.promptEditor = m.promptEditor.Update(key)
		}
		return m, nil
	}
	switch key {
	case "v":
		m.promptPreviewOn, m.promptPreview, m.promptPreviewErr = true, nil, ""
		return m, m.cmdFetchPromptPreview(m.currentStageId(), previewKinds[m.promptPreviewKind])
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
			content := templates.Read(m.promptEntries[m.promptSelected].Path)
			w := m.paneCols() - 30
			if w < 10 {
				w = 10
			}
			m.promptEditor = widgets.NewTextArea(content, w, max(3, m.paneRows()-2))
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
		ed := m.promptEditor
		ed.SetSize(max(10, m.paneCols()-30), max(3, m.paneRows()-2))
		right = accentStyle.Render("editing "+m.promptEntries[m.promptSelected].Label) + "\n\n" + ed.View()
	default:
		hint := "enter edit · v compiled preview"
		right = subtleStyle.Render("Select a template on the left.\n\n") +
			subtleStyle.Render("● on disk   ○ built-in default\n\nSaved to planDir — the engine hot-reloads\nat the next session.\n\n") + subtleStyle.Render(hint)
	}
	rightCol := lipgloss.NewStyle().Width(m.paneCols() - 30).Render(right)

	body := lipgloss.JoinHorizontal(lipgloss.Top, leftCol, subtleStyle.Render("│ "), rightCol)
	help := "↑↓ select · enter edit · v preview"
	switch {
	case m.promptPreviewOn:
		help = "←→ kind · v/esc close"
	case m.promptMode == PromptEdit:
		help = "ctrl+s save · esc back · ←→↑↓ move · home/end"
	}
	return body, help
}

func (m Model) templatesPreview() string {
	stage := m.currentStageId()
	if stage == "" {
		stage = "(none)"
	}
	// Kind picker: the current kind highlighted, the rest dim — ←/→ cycles and re-compiles.
	var kinds []string
	for i, k := range previewKinds {
		if i == m.promptPreviewKind {
			kinds = append(kinds, highlightBg.Render(" "+k+" "))
		} else {
			kinds = append(kinds, subtleStyle.Render(k))
		}
	}
	head := subtleStyle.Render("compiled · stage ") + accentStyle.Render(stage) + "\n" +
		subtleStyle.Render("kind ‹ ") + strings.Join(kinds, subtleStyle.Render(" ")) + subtleStyle.Render(" ›") + "\n\n"
	if m.promptPreviewErr != "" {
		return head + destructStyle.Render("error: "+m.promptPreviewErr)
	}
	if m.promptPreview == nil {
		return head + subtleStyle.Render("compiling…")
	}
	meta := subtleStyle.Render(fmt.Sprintf("model %s · kind %s", m.promptPreview.Model, m.promptPreview.Kind))
	return head + meta + "\n\n" + textStyle.Render(truncateLines(m.promptPreview.Prompt, m.paneRows()-5))
}
