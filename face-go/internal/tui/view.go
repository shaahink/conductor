package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/widgets"
)

var (
	accentStyle   = lipgloss.NewStyle().Foreground(lipgloss.Color("#58A6FF")).Bold(true)
	subtleStyle   = lipgloss.NewStyle().Foreground(lipgloss.Color("#484F58"))
	textStyle     = lipgloss.NewStyle().Foreground(lipgloss.Color("#C9D1D9"))
	highlightBg   = lipgloss.NewStyle().Background(lipgloss.Color("#1F6FEB")).Foreground(lipgloss.Color("#FFFFFF"))
	destructStyle = lipgloss.NewStyle().Foreground(lipgloss.Color("#F85149"))
	warnStyle     = lipgloss.NewStyle().Foreground(lipgloss.Color("#D29922"))
	safeStyle     = lipgloss.NewStyle().Foreground(lipgloss.Color("#3FB950"))
	inputStyle    = lipgloss.NewStyle().Border(lipgloss.NormalBorder()).BorderForeground(lipgloss.Color("#58A6FF")).Padding(0, 1).Width(50)
)

func (m Model) View() tea.View {
	if m.width < 10 || m.height < 5 {
		v := tea.NewView("Terminal too small")
		return v
	}

	tw := m.width
	if tw < 10 {
		tw = 80
	}

	ticker := widgets.RenderTicker(m.data.Connection, m.data.Plan, tw)
	footer := widgets.RenderFooter(tw, m.sidebarOpen)
	transcript := m.renderTranscript()

	mainContent := transcript
	if m.sidebarOpen {
		sb := m.renderSidebar()
		mainContent = lipgloss.JoinHorizontal(lipgloss.Top, sb, transcript)
	}

	result := lipgloss.JoinVertical(lipgloss.Top, ticker, mainContent, footer)

	if m.activeModal != ModalNone {
		result = m.renderModal(result)
	}

	toasts := widgets.RenderToasts(m.toasts)
	if toasts != "" {
		toastStyle := lipgloss.NewStyle().Padding(0, 1).MaxWidth(tw)
		result = lipgloss.JoinVertical(lipgloss.Bottom, result, toastStyle.Render(toasts))
	}

	v := tea.NewView(result)
	v.AltScreen = true
	v.MouseMode = tea.MouseModeCellMotion
	return v
}

func (m Model) renderTranscript() string {
	tStyle := lipgloss.NewStyle().Width(m.transcript.Width).Height(m.transcript.Height)
	return tStyle.Render(m.transcript.View())
}

func (m Model) renderSidebar() string {
	m.sidebar.Stages = nil
	m.sidebar.Gates = nil
	if m.data.Plan != nil {
		m.sidebar.Stages = m.data.Plan.Stages
		m.sidebar.Gates = m.data.Plan.Gates
	}

	sStyle := lipgloss.NewStyle().
		Width(m.sidebar.Width).Height(m.sidebar.Height).
		Border(lipgloss.RoundedBorder(), false, true, false, false).
		BorderForeground(lipgloss.Color("#30363D"))

	return sStyle.Render(m.sidebar.View())
}

func (m Model) renderModal(background string) string {
	modalW := m.width - 4
	if modalW > 76 {
		modalW = 76
	}
	if modalW < 30 {
		modalW = 30
	}
	modalH := m.height - 4
	if modalH > 30 {
		modalH = 30
	}

	title, body, help := m.modalContent()

	header := accentStyle.Render(title) + "\n\n"
	content := body
	footer := "\n\n" + subtleStyle.Render(help)

	inner := header + content + footer
	modalStr := lipgloss.NewStyle().
		Border(lipgloss.DoubleBorder()).
		BorderForeground(lipgloss.Color("#58A6FF")).
		Padding(1, 2).
		Width(modalW).MaxHeight(modalH).
		Render(inner)

	return placeOverlay(background, modalStr, m.width, m.height)
}

func (m Model) modalContent() (string, string, string) {
	switch m.activeModal {
	case ModalPalette:
		return m.renderPaletteModal()
	case ModalInject:
		return m.renderInjectModal()
	case ModalPrompt:
		return m.renderPromptModal()
	case ModalSessions:
		return m.renderSessionsModal()
	case ModalReport:
		return m.renderReportModal()
	case ModalHelp:
		return m.renderHelpModal()
	}
	return "", "", ""
}

func (m Model) renderPaletteModal() (string, string, string) {
	title := "Command Palette"

	if m.paletteConfirming {
		verb := allVerbs[m.paletteVerbIdx]
		body := fmt.Sprintf("\n  %s %s\n  %s\n\n  %s\n",
			destructStyle.Render("\u26A0 "+verb.Key),
			subtleStyle.Render(verb.Desc),
			destructStyle.Render("This is a destructive action."),
			warnStyle.Render("Confirm? [y/N]"),
		)
		return title, body, "[y: confirm] [n/esc: cancel]"
	}

	idxs := m.filteredVerbs()
	var lines []string
	if m.paletteQuery != "" {
		lines = append(lines, subtleStyle.Render("  filter: "+m.paletteQuery+"_"))
	} else {
		lines = append(lines, subtleStyle.Render("  [type to filter]"))
	}
	lines = append(lines, "")

	for i, origIdx := range idxs {
		if origIdx >= len(allVerbs) {
			continue
		}
		verb := allVerbs[origIdx]
		symbol := "  "
		vs := textStyle
		if !verb.Safe {
			symbol = "\u26A0 "
			vs = destructStyle
		}
		line := fmt.Sprintf("%s%-14s  %s", symbol, vs.Render(verb.Key), subtleStyle.Render(verb.Desc))
		if i == m.paletteSelected {
			line = highlightBg.Render(line)
		}
		lines = append(lines, line)
	}

	body := strings.Join(lines, "\n")
	return title, body, "[\u2191\u2193: navigate] [enter: execute] [esc: close] [type: filter]"
}

func (m Model) renderInjectModal() (string, string, string) {
	title := "Inject Context"

	sf := subtleStyle
	if m.injectField == 0 {
		sf = accentStyle
	}
	cf := subtleStyle
	if m.injectField == 1 {
		cf = accentStyle
	}

	stageLine := fmt.Sprintf("  %s %s",
		subtleStyle.Render("Stage:"),
		sf.Render(m.injectStageId+"_"))

	contentLine := fmt.Sprintf("  %s %s",
		subtleStyle.Render("Content:"),
		cf.Render(m.injectContent+"_"))

	body := fmt.Sprintf("%s\n\n%s\n\n  %s",
		stageLine,
		contentLine,
		subtleStyle.Render("Injection recorded to run.db — consumed on next session boundary."),
	)

	return title, body, "[tab: switch field] [ctrl+s: submit] [esc: close]"
}

func (m Model) renderPromptModal() (string, string, string) {
	title := "Template Editor"

	if m.promptMode == PromptEdit {
		body := fmt.Sprintf("  %s: %s\n\n%s",
			subtleStyle.Render("Editing"),
			accentStyle.Render(m.promptTemplates[m.promptSelected]),
			textStyle.Render(m.promptContent),
		)
		return title, body, "[ctrl+s: save] [esc: back to list]"
	}

	var lines []string
	for i, t := range m.promptTemplates {
		line := fmt.Sprintf("  %s", t)
		if i == m.promptSelected {
			line = highlightBg.Render(line)
		}
		lines = append(lines, line)
	}
	body := strings.Join(lines, "\n")
	body += fmt.Sprintf("\n\n  %s", subtleStyle.Render("Saved to planDir/personas/ — engine hot-reloads on next session."))
	return title, body, "[\u2191\u2193: select] [enter: edit] [esc: close]"
}

func (m Model) renderSessionsModal() (string, string, string) {
	title := "Session History"

	if len(m.data.Sessions) == 0 {
		return title, "  " + subtleStyle.Render("(no sessions loaded)"), "[esc: close]"
	}

	var lines []string
	for i, s := range m.data.Sessions {
		outcome := "running"
		if s.Outcome != nil {
			outcome = *s.Outcome
		}
		line := fmt.Sprintf("  #%-3d %-8s %-7s %-12s commits:%d",
			s.Number, s.StageId, s.Kind, outcome, s.CommitCount)
		if i == m.sessionSelected {
			line = highlightBg.Render(line)
		}
		lines = append(lines, line)
	}

	if m.sessionSelected < len(m.data.Sessions) {
		s := m.data.Sessions[m.sessionSelected]
		detail := fmt.Sprintf("\n\n  %s #%d | %s %s | %s",
			accentStyle.Render("Session"),
			s.Number,
			accentStyle.Render(s.StageId),
			accentStyle.Render(s.Kind),
			subtleStyle.Render(fmt.Sprintf("attempt %d, %d resumes", s.Attempt, s.ResumeCount)),
		)
		if s.GateSummary != nil {
			detail += fmt.Sprintf("\n  Gates: %s", *s.GateSummary)
		}
		if s.ResultSummary != nil {
			detail += fmt.Sprintf("\n  Result: %s", *s.ResultSummary)
		}
		lines = append(lines, detail)
	}

	body := strings.Join(lines, "\n")
	return title, body, "[\u2191\u2193: navigate] [esc: close]"
}

func (m Model) renderReportModal() (string, string, string) {
	title := "Report / Query"

	quickQueries := []string{
		"Cost per stage",
		"Which gates fail most",
		"Recent sessions",
		"Verifier scores",
	}

	var lines []string
	lines = append(lines, "  Quick queries:")
	for i, q := range quickQueries {
		marker := "  "
		if i == m.reportQuickSelected && !m.reportFocusQuery {
			marker = highlightBg.Render(" >")
		}
		lines = append(lines, fmt.Sprintf("  %s %s", marker, q))
	}

	lines = append(lines, "")
	lines = append(lines, "  Custom SQL:")
	sqlDisplay := m.reportSQL
	if m.reportFocusQuery {
		sqlDisplay += "_"
	}
	lines = append(lines, "  "+subtleStyle.Render(sqlDisplay))

	if m.data.ReportResult != nil && len(m.data.ReportResult.Columns) > 0 {
		lines = append(lines, "")
		lines = append(lines, fmt.Sprintf("  %s", accentStyle.Render("Results:")))
		for _, row := range m.data.ReportResult.Rows {
			if len(row.Values) > 0 {
				lines = append(lines, "  "+strings.Join(row.Values, " | "))
			}
		}
	}

	info := subtleStyle.Render("\n\n  SELECT only. Max 500 rows.")
	body := strings.Join(lines, "\n") + info
	return title, body, "[tab: switch focus] [\u2191\u2193: select] [enter: run] [esc: close]"
}

func (m Model) renderHelpModal() (string, string, string) {
	title := "Keybindings"

	body := `
  NAVIGATION
    p         Toggle plan sidebar
    ↑↓/j/k    Scroll transcript / navigate sidebar
    PgUp/Dn   Scroll page
    Home/End  Jump top/bottom
    f         Toggle tool-call folding
    /         Search transcript

  ACTIONS
    :         Command palette (11 verbs)
    i         Inject context
    e         Edit templates
    h         Session history
    r         Report / query
    ?         This help

  GLOBAL
    q / ^C    Quit
    esc       Close modal

  MOUSE
    Scroll    Navigate transcript
    Click     Select items`

	help := "conductor-face v2 · Go + Bubble Tea · --demo for offline mode"
	return title, body, help
}

func placeOverlay(bg, fg string, width, height int) string {
	return lipgloss.Place(width, height,
		lipgloss.Center, lipgloss.Center,
		fg,
		lipgloss.WithWhitespaceStyle(lipgloss.NewStyle().Background(lipgloss.Color("#0D1117"))),
	)
}
