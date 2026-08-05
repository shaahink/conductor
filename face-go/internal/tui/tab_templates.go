package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/templates"
	"conductor-face-go/internal/widgets"
)

// templatesModel is the Templates tab's own state (K6.3): the prompt list, the editor over the
// selected file, and the compiled preview beside it. Held on Model as `tmpl` rather than
// `templates`, which is the package this tab calls.
type templatesModel struct {
	entries     []templates.Entry
	selected    int
	editor      widgets.TextArea
	mode        PromptMode
	preview     *api.PromptPreviewDto
	previewOn   bool
	previewErr  string
	previewKind int // index into previewKinds — which session kind's compiled prompt to show
}

// updateTemplates handles the compiled-preview result — the tab's one async message.
func (m Model) updateTemplates(msg tea.Msg) (Model, tea.Cmd, bool) {
	switch msg := msg.(type) {

	case MsgPromptPreview:
		if msg.Err != "" {
			m.tmpl.previewErr, m.tmpl.preview = msg.Err, nil
		} else {
			m.tmpl.preview, m.tmpl.previewErr = msg.Preview, ""
		}
		return m, nil, true
	}
	return m, nil, false
}

// previewKinds are the session kinds whose compiled prompt the preview can render — the engine
// builds a different template per kind, so the picker (←/→) lets you inspect each one.
var previewKinds = []string{"Deliver", "Fix", "Resume", "Audit", "Review"}

func (m Model) handleTemplatesKey(key string) (tea.Model, tea.Cmd) {
	if m.tmpl.previewOn {
		switch key {
		case "esc", "v":
			m.tmpl.previewOn = false
			return m, nil
		case "left", "h":
			m.tmpl.previewKind = (m.tmpl.previewKind - 1 + len(previewKinds)) % len(previewKinds)
			m.tmpl.preview, m.tmpl.previewErr = nil, ""
			return m, m.cmdFetchPromptPreview(m.currentStageId(), previewKinds[m.tmpl.previewKind])
		case "right", "l":
			m.tmpl.previewKind = (m.tmpl.previewKind + 1) % len(previewKinds)
			m.tmpl.preview, m.tmpl.previewErr = nil, ""
			return m, m.cmdFetchPromptPreview(m.currentStageId(), previewKinds[m.tmpl.previewKind])
		}
		return m, nil
	}
	if m.tmpl.mode == PromptEdit {
		switch key {
		case "esc":
			m.tmpl.mode = PromptList
			return m, nil
		case "ctrl+s":
			if m.tmpl.selected < len(m.tmpl.entries) {
				entry := m.tmpl.entries[m.tmpl.selected]
				if err := templates.Write(entry.Path, m.tmpl.editor.Value()); err != nil {
					return m, m.addToast("Save failed: "+err.Error(), widgets.ToastError)
				}
				m.tmpl.entries[m.tmpl.selected].Exists = true
				return m, m.addToast("Saved "+entry.Label, widgets.ToastSuccess)
			}
			return m, nil
		default:
			// A real editor: insert/delete at the caret, arrow/home/end/pgup-pgdn navigation.
			m.tmpl.editor = m.tmpl.editor.Update(key)
		}
		return m, nil
	}
	switch key {
	case "v":
		m.tmpl.previewOn, m.tmpl.preview, m.tmpl.previewErr = true, nil, ""
		return m, m.cmdFetchPromptPreview(m.currentStageId(), previewKinds[m.tmpl.previewKind])
	case "up", "k":
		if m.tmpl.selected > 0 {
			m.tmpl.selected--
		}
	case "down", "j":
		if m.tmpl.selected < len(m.tmpl.entries)-1 {
			m.tmpl.selected++
		}
	case "enter":
		if m.tmpl.selected < len(m.tmpl.entries) {
			m.tmpl.mode = PromptEdit
			content := templates.Read(m.tmpl.entries[m.tmpl.selected].Path)
			w := m.paneCols() - 30
			if w < 10 {
				w = 10
			}
			m.tmpl.editor = widgets.NewTextArea(content, w, max(3, m.paneRows()-2))
		}
	}
	return m, nil
}

// renderTemplatesPane: template list on the left; editor or compiled-prompt preview on the right —
// all on one page.
func (m Model) renderTemplatesPane() (string, string) {
	var left []string
	if len(m.tmpl.entries) == 0 {
		left = append(left, subtleStyle.Render("(no plan dir yet)"))
	}
	for i, e := range m.tmpl.entries {
		status := safeStyle.Render("●")
		if !e.Exists {
			status = subtleStyle.Render("○")
		}
		row := fmt.Sprintf("%s %s", status, e.Label)
		if i == m.tmpl.selected {
			row = highlightBg.Render(fmt.Sprintf("%s %s", "•", e.Label))
		}
		left = append(left, row)
	}
	leftCol := lipgloss.NewStyle().Width(26).Render(strings.Join(left, "\n"))

	var right string
	switch {
	case m.tmpl.previewOn:
		right = m.templatesPreview()
	case m.tmpl.mode == PromptEdit && m.tmpl.selected < len(m.tmpl.entries):
		ed := m.tmpl.editor
		ed.SetSize(max(10, m.paneCols()-30), max(3, m.paneRows()-2))
		right = accentStyle.Render("editing "+m.tmpl.entries[m.tmpl.selected].Label) + "\n\n" + ed.View()
	default:
		hint := "enter edit · v compiled preview"
		right = subtleStyle.Render("Select a template on the left.\n\n") +
			subtleStyle.Render("● on disk   ○ built-in default\n\nSaved to planDir — the engine hot-reloads\nat the next session.\n\n") + subtleStyle.Render(hint)
	}
	rightCol := lipgloss.NewStyle().Width(m.paneCols() - 30).Render(right)

	body := lipgloss.JoinHorizontal(lipgloss.Top, leftCol, subtleStyle.Render("│ "), rightCol)
	help := "↑↓ select · enter edit · v preview"
	switch {
	case m.tmpl.previewOn:
		help = "←→ kind · v/esc close"
	case m.tmpl.mode == PromptEdit:
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
		if i == m.tmpl.previewKind {
			kinds = append(kinds, highlightBg.Render(" "+k+" "))
		} else {
			kinds = append(kinds, subtleStyle.Render(k))
		}
	}
	head := subtleStyle.Render("compiled · stage ") + accentStyle.Render(stage) + "\n" +
		subtleStyle.Render("kind ‹ ") + strings.Join(kinds, subtleStyle.Render(" ")) + subtleStyle.Render(" ›") + "\n\n"
	if m.tmpl.previewErr != "" {
		return head + destructStyle.Render("error: "+m.tmpl.previewErr)
	}
	if m.tmpl.preview == nil {
		return head + subtleStyle.Render("compiling…")
	}
	meta := subtleStyle.Render(fmt.Sprintf("model %s · kind %s", m.tmpl.preview.Model, m.tmpl.preview.Kind))
	return head + meta + "\n\n" + textStyle.Render(truncateLines(m.tmpl.preview.Prompt, m.paneRows()-5))
}
