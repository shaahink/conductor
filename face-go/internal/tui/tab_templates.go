package tui

import (
	"fmt"
	"strings"

	"charm.land/bubbles/v2/viewport"
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
	// previewVp scrolls the compiled prompt (K6.4, adr/0006 §5: every read-not-select body is a
	// viewport). This pane used to `truncateLines` the prompt to the pane height and stop there, so a
	// session prompt — the longest single document this Face shows, and the one an owner most needs to
	// read to the end before a run starts — was readable only down to its first ~25 lines.
	previewVp viewport.Model
	// listVp scrolls the template LIST. KS2.7 first exempted this surface with the reason "bounded by
	// construction, never a long body" — which is measurably false: templates.List returns the seven
	// fixed session templates PLUS every `*.md` under <planDir>/personas, a directory the owner fills.
	// At 80x24 with twenty personas the list was 27 rows in an 18-row pane, the tail clipped in
	// silence and `end` moving nothing. `selected` stays a SELECTION cursor; the viewport follows it
	// through ensurePaneRow and takes over at the ends of the list.
	listVp viewport.Model
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
		// A newly compiled prompt is a new document: start it at the top rather than wherever the
		// previous kind was left scrolled to.
		m.tmpl.previewVp.GotoTop()
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
		// Size and content FIRST, then move — the clamp lives at the mutation (adr/0006 §1), so the
		// viewport has to hold the prompt that is actually on screen before the offset changes.
		m.tmpl.previewVp = m.templatesPreviewViewport()
		applyPaneScroll(&m.tmpl.previewVp, key)
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
		// The cursor falls through at its ends (panescroll.go, ensurePaneRow): it returns only when
		// it actually MOVED, so on the first row `up` scrolls the list instead of dying there.
		if m.tmpl.selected > 0 {
			m.tmpl.selected--
			m.tmpl.listVp = m.followTemplatesSelection()
			return m, nil
		}
	case "down", "j":
		if m.tmpl.selected < len(m.tmpl.entries)-1 {
			m.tmpl.selected++
			m.tmpl.listVp = m.followTemplatesSelection()
			return m, nil
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
		return m, nil
	}
	// Size and content FIRST, then move — the clamp lives at the mutation (adr/0006 §1).
	m.tmpl.listVp = m.templatesListViewport()
	applyPaneScroll(&m.tmpl.listVp, key)
	return m, nil
}

// templatesListBody is the left-hand list: one row per entry, padded to the column width PLAIN and
// styled after (STYLE.md), so the viewport's lines are the exact rows renderTemplatesPane used to
// join by hand and the column beside it does not shift.
func (m Model) templatesListBody() string {
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
	return lipgloss.NewStyle().Width(templatesListCols).Render(strings.Join(left, "\n"))
}

// templatesListCols is the width of the template list column, and the number renderTemplatesPane
// used to spell out twice.
const templatesListCols = 26

// templatesListViewport is the list's `<surface>Viewport()` builder — the one both the key handler
// and the renderer call.
func (m Model) templatesListViewport() viewport.Model {
	return loadPaneViewport(m.tmpl.listVp, strings.Split(m.templatesListBody(), "\n"),
		templatesListCols, m.paneRows(), false)
}

// followTemplatesSelection is the builder plus the cursor follow, called ONLY from the arms that
// moved the cursor (see ensurePaneRow: calling it from the builder would let every frame re-assert
// the cursor and silently undo `end`). One entry renders as exactly one row, so the cursor IS the
// row index.
func (m Model) followTemplatesSelection() viewport.Model {
	vp := m.templatesListViewport()
	ensurePaneRow(&vp, m.tmpl.selected)
	return vp
}

// renderTemplatesPane: template list on the left; editor or compiled-prompt preview on the right —
// all on one page.
func (m Model) renderTemplatesPane() (string, string) {
	listVp := m.templatesListViewport()
	leftCol := listVp.View()

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
	rightCol := lipgloss.NewStyle().Width(m.templatesPreviewCols()).Render(right)

	body := lipgloss.JoinHorizontal(lipgloss.Top, leftCol, subtleStyle.Render("│ "), rightCol)
	help := "↑↓ select · enter edit · v preview"
	if hint := paneScrollHint(listVp, false); hint != "" {
		// `arrows: false` — ↑↓ walk the SELECTION here and only reach the pane at the list's ends, so
		// naming them as the scroll keys would be the same lie as the tab help that claimed `k`.
		help += " · " + hint
	}
	switch {
	case m.tmpl.previewOn:
		help = "←→ kind · " + paneScrollHelp(m.templatesPreviewViewport()) + " · v/esc close"
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
	return head + meta + "\n\n" + m.templatesPreviewViewport().View()
}

// templatesPreviewBody is the compiled prompt, wrapped to the preview column and then coloured —
// plain text first, style after (STYLE.md), because the source really is plain and wrapping an ANSI
// string is this repo's oldest layout bug.
//
// It stays PLAIN and is deliberately NOT run through renderMarkdown, which is the one call this
// checkpoint considered and rejected on evidence. The compiled prompt is a FIDELITY surface — it
// exists to show an owner the exact bytes an agent will be handed — and glamour sanitises anything
// that parses as an HTML tag, so `task --done <id> --evidence <path>` renders as
// `task --done  --evidence` with the two placeholders silently deleted. Prettier prose is not worth
// a preview that lies about the prompt.
func (m Model) templatesPreviewBody() string {
	if m.tmpl.preview == nil {
		return ""
	}
	return textStyle.Width(m.templatesPreviewCols()).Render(m.tmpl.preview.Prompt)
}

// templatesPreviewViewport sizes the preview's viewport to the column and loads the current prompt.
// The height is the pane less the five rows the header, kind picker and meta line spend above it.
func (m Model) templatesPreviewViewport() viewport.Model {
	vp := m.tmpl.previewVp
	vp.SetWidth(m.templatesPreviewCols())
	vp.SetHeight(max(1, m.paneRows()-5))
	vp.SetContent(m.templatesPreviewBody())
	return vp
}

// templatesPreviewCols is the width the right-hand column gives the preview: the pane minus the
// 26-column template list and its separator. It is a function rather than a literal because
// renderTemplatesPane wraps the same value around the column, and the two drifting apart is how a
// rendered block ends up wrapped twice.
func (m Model) templatesPreviewCols() int { return max(20, m.paneCols()-30) }
