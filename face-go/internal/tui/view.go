package tui

import (
	"fmt"
	"strings"
	"time"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
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
	transcript := m.renderTranscript()

	mainContent := transcript
	showGateBar := !m.sidebarOpen && m.data.Plan != nil && len(m.data.Plan.Gates) > 0
	if m.sidebarOpen {
		sb := m.renderSidebar()
		mainContent = lipgloss.JoinHorizontal(lipgloss.Top, sb, transcript)
	} else if showGateBar {
		gateBar := lipgloss.NewStyle().Padding(0, 1).Render(widgets.RenderGateBar(m.data.Plan.Gates, tw-2))
		mainContent = lipgloss.JoinVertical(lipgloss.Top, transcript, gateBar)
	}

	footer := widgets.RenderFooter(tw, m.sidebarOpen)
	if m.searchActive || m.transcript.SearchQuery != "" {
		footer = m.renderSearchBar(tw)
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

func (m Model) renderSearchBar(width int) string {
	style := lipgloss.NewStyle().
		Background(lipgloss.Color("#161B22")).
		Foreground(lipgloss.Color("#484F58")).
		Padding(0, 1).MaxHeight(1).MaxWidth(width)

	q := m.transcript.SearchQuery
	cursor := ""
	if m.searchActive {
		cursor = "_"
	}

	matchInfo := subtleStyle.Render("no matches")
	if len(m.transcript.SearchMatches) > 0 {
		matchInfo = accentStyle.Render(fmt.Sprintf("%d/%d", m.transcript.SearchMatchIdx+1, len(m.transcript.SearchMatches)))
	}

	hint := "enter: lock  ·  esc: clear"
	if !m.searchActive {
		hint = "n/N: next/prev  ·  esc: clear"
	}

	line := fmt.Sprintf("/%s%s  %s  %s", q, cursor, matchInfo, subtleStyle.Render(hint))
	return style.Render(line)
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
	case ModalProcesses:
		return m.renderProcessesModal()
	case ModalTimeline:
		return m.renderTimelineModal()
	case ModalConsole:
		return m.renderConsoleModal()
	case ModalPlan:
		return m.renderPlanModal()
	case ModalHelp:
		return m.renderHelpModal()
	}
	return "", "", ""
}

func (m Model) renderConsoleModal() (string, string, string) {
	title := "Native Console — raw agent stdout"

	lines := m.data.RawConsole
	if len(lines) == 0 {
		return title,
			"  " + subtleStyle.Render("(no raw output yet — the agent tees stdout to .conductor/logs/session-NNN.jsonl)"),
			"[esc/c: close]"
	}

	const window = 18
	end := len(lines) - m.consoleScroll // consoleScroll counts lines back from the live tail
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
		out = append(out, "  "+subtleStyle.Render(truncate(lines[i].Text, 66)))
	}

	pos := "live tail"
	if m.consoleScroll > 0 {
		pos = fmt.Sprintf("scrolled back %d", m.consoleScroll)
	}
	out = append(out, "", subtleStyle.Render(fmt.Sprintf("  %d lines · %s · the transcript pane is the folded view", len(lines), pos)))
	return title, strings.Join(out, "\n"), "[↑↓: scroll] [end: live tail] [esc/c: close]"
}

func (m Model) renderTimelineModal() (string, string, string) {
	title := "Timeline"

	if m.timelineLoading && len(m.timelineEntries) == 0 {
		return title, "  " + subtleStyle.Render("loading…"), "[esc: close]"
	}
	if m.timelineErr != "" {
		return title, "  " + destructStyle.Render("error: "+m.timelineErr), "[r: retry] [esc: close]"
	}
	if len(m.timelineEntries) == 0 {
		return title, "  " + subtleStyle.Render("(no events on the run's spine yet)"), "[r: refresh] [esc: close]"
	}

	// Show a scrolling window around the selection so long runs stay navigable in a fixed modal.
	const window = 16
	start := 0
	if m.timelineSelected >= window {
		start = m.timelineSelected - window + 1
	}
	end := start + window
	if end > len(m.timelineEntries) {
		end = len(m.timelineEntries)
	}

	var lines []string
	for i := start; i < end; i++ {
		e := m.timelineEntries[i]
		glyph, gs := timelineGlyph(e)
		clock := timelineClock(e.Utc)
		desc := truncate(e.Description, 52)
		cost := ""
		if e.CostUsd != nil && *e.CostUsd > 0 {
			cost = subtleStyle.Render(fmt.Sprintf("  $%.2f", *e.CostUsd))
		}
		line := fmt.Sprintf("  %s %s %s%s", subtleStyle.Render(clock), gs.Render(glyph), textStyle.Render(desc), cost)
		if i == m.timelineSelected {
			line = highlightBg.Render(fmt.Sprintf("  %s %s %s%s", clock, glyph, desc, ""))
		}
		lines = append(lines, line)
	}
	if end < len(m.timelineEntries) {
		lines = append(lines, subtleStyle.Render(fmt.Sprintf("  … %d more below", len(m.timelineEntries)-end)))
	}
	lines = append(lines, "", subtleStyle.Render(fmt.Sprintf("  %d events · folded from run.db's event spine", len(m.timelineEntries))))

	body := strings.Join(lines, "\n")
	return title, body, "[↑↓: navigate] [r: refresh] [esc: close]"
}

// timelineGlyph maps an event kind to a marker + colour, mirroring the transcript's visual grammar.
func timelineGlyph(e api.TimelineEntryDto) (string, lipgloss.Style) {
	switch e.Kind {
	case "session":
		return "▸", accentStyle
	case "gate":
		if e.Outcome != nil && *e.Outcome == "fail" {
			return "✗", destructStyle
		}
		return "✓", safeStyle
	case "stage":
		return "◆", accentStyle
	case "attention":
		return "⚠", warnStyle
	default:
		return "·", subtleStyle
	}
}

// timelineClock renders just HH:MM:SS from an ISO-8601 UTC timestamp, tolerating a bad value.
func timelineClock(utc string) string {
	t, err := time.Parse(time.RFC3339, utc)
	if err != nil {
		return "--:--:--"
	}
	return t.UTC().Format("15:04:05")
}

func (m Model) renderPaletteModal() (string, string, string) {
	title := "Command Palette"

	if m.paletteGotoActive {
		body := fmt.Sprintf("\n  %s\n\n  %s%s\n",
			textStyle.Render("Jump to stage id:"),
			accentStyle.Render(m.paletteGotoInput),
			accentStyle.Render("▏"),
		)
		return title, body, "[enter: confirm] [esc: back]"
	}

	if m.paletteConfirming {
		verb := allVerbs[m.paletteVerbIdx]
		body := fmt.Sprintf("\n  %s %s\n  %s\n\n  %s\n",
			destructStyle.Render("⚠ "+verb.Key),
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
			symbol = "⚠ "
			vs = destructStyle
		}
		line := fmt.Sprintf("%s%-14s  %s", symbol, vs.Render(verb.Key), subtleStyle.Render(verb.Desc))
		if i == m.paletteSelected {
			line = highlightBg.Render(line)
		}
		lines = append(lines, line)
	}

	body := strings.Join(lines, "\n")
	return title, body, "[↑↓: navigate] [enter: execute] [esc: close] [type: filter]"
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

	contentLine := fmt.Sprintf("  %s\n%s",
		subtleStyle.Render("Content:"),
		cf.Render(indent(m.injectContent+"_", "  ")))

	body := fmt.Sprintf("%s\n\n%s\n\n  %s",
		stageLine,
		contentLine,
		subtleStyle.Render("Injection recorded to run.db — consumed on next session boundary."),
	)

	return title, body, "[tab: switch field] [ctrl+s: submit] [esc: close]"
}

func (m Model) renderPromptModal() (string, string, string) {
	title := "Template Editor"

	// M5.5: compiled-prompt preview — the exact prompt that would be sent for the current stage.
	if m.promptPreviewOn {
		stage := m.currentStageId()
		if stage == "" {
			stage = "(none)"
		}
		header := fmt.Sprintf("  %s %s\n\n",
			subtleStyle.Render("Compiled prompt · stage"), accentStyle.Render(stage))
		if m.promptPreviewErr != "" {
			return title, header + "  " + destructStyle.Render("error: "+m.promptPreviewErr), "[v: hide preview] [esc: back]"
		}
		if m.promptPreview == nil {
			return title, header + "  " + subtleStyle.Render("compiling…"), "[esc: back]"
		}
		meta := subtleStyle.Render(fmt.Sprintf("  model %s · kind %s", m.promptPreview.Model, m.promptPreview.Kind))
		body := header + meta + "\n\n" + textStyle.Render(indent(truncateLines(m.promptPreview.Prompt, 18), "  "))
		return title, body, "[v: hide preview] [esc: back]"
	}

	if m.promptMode == PromptEdit && m.promptSelected < len(m.promptEntries) {
		entry := m.promptEntries[m.promptSelected]
		body := fmt.Sprintf("  %s: %s\n\n%s",
			subtleStyle.Render("Editing"),
			accentStyle.Render(entry.Label),
			textStyle.Render(m.promptContent),
		)
		return title, body, "[ctrl+s: save] [esc: back to list]"
	}

	if len(m.promptEntries) == 0 {
		return title, "  " + subtleStyle.Render("(no plan directory known yet)"), "[esc: close]"
	}

	var lines []string
	for i, e := range m.promptEntries {
		status := safeStyle.Render("on disk")
		if !e.Exists {
			status = subtleStyle.Render("built-in default")
		}
		line := fmt.Sprintf("  %-28s %s", e.Label, status)
		if i == m.promptSelected {
			line = highlightBg.Render(line)
		}
		lines = append(lines, line)
	}
	body := strings.Join(lines, "\n")
	body += fmt.Sprintf("\n\n  %s", subtleStyle.Render("Saved to planDir — engine hot-reloads on next session."))
	return title, body, "[↑↓: select] [enter: edit] [v: compiled preview] [esc: close]"
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
			detail += "\n  " + subtleStyle.Render("Result:") + "\n" + indent(renderMarkdown(*s.ResultSummary, 60), "  ")
		}

		tail := transcriptTailForSession(m.data.Transcript, s.Number, 12)
		if len(tail) > 0 {
			detail += "\n\n  " + subtleStyle.Render("transcript tail (buffered this connection):")
			for _, l := range tail {
				style := textStyle
				if l.Kind == "thinking" {
					style = subtleStyle
				}
				detail += "\n    " + style.Render(truncate(l.Text, 80))
			}
		}
		lines = append(lines, detail)
	}

	body := strings.Join(lines, "\n")
	return title, body, "[↑↓: navigate] [esc: close]"
}

func transcriptTailForSession(lines []api.TranscriptLineDto, sessionNumber int, max int) []api.TranscriptLineDto {
	sid := fmt.Sprintf("%d", sessionNumber)
	var out []api.TranscriptLineDto
	for _, l := range lines {
		if l.SessionId == sid {
			out = append(out, l)
		}
	}
	if len(out) > max {
		out = out[len(out)-max:]
	}
	return out
}

func (m Model) renderReportModal() (string, string, string) {
	title := "Report / Query"

	var lines []string
	lines = append(lines, "  Quick queries:")
	for i, q := range quickQueries {
		marker := "  "
		if i == m.reportQuickSelected && !m.reportFocusQuery {
			marker = highlightBg.Render(" >")
		}
		lines = append(lines, fmt.Sprintf("  %s %s", marker, q.Label))
	}

	lines = append(lines, "")
	sf := subtleStyle
	if m.reportFocusQuery {
		sf = accentStyle
	}
	lines = append(lines, "  "+sf.Render("SQL:"))
	sqlDisplay := m.reportSQL
	if m.reportFocusQuery {
		sqlDisplay += "_"
	}
	lines = append(lines, "  "+textStyle.Render(sqlDisplay))

	if m.data.ReportLoading {
		lines = append(lines, "", "  "+subtleStyle.Render("running…"))
	} else if m.data.ReportResult != nil {
		if m.data.ReportResult.Error != nil {
			lines = append(lines, "", "  "+destructStyle.Render("error: "+*m.data.ReportResult.Error))
		} else if len(m.data.ReportResult.Columns) > 0 {
			lines = append(lines, "", "  "+accentStyle.Render("Results:"))
			lines = append(lines, "  "+textStyle.Bold(true).Render(strings.Join(m.data.ReportResult.Columns, " | ")))
			for _, row := range m.data.ReportResult.Rows {
				if len(row.Values) > 0 {
					lines = append(lines, "  "+subtleStyle.Render(strings.Join(row.Values, " | ")))
				}
			}
			if m.data.ReportResult.Truncated {
				lines = append(lines, "  "+warnStyle.Render("… truncated"))
			}
		} else {
			lines = append(lines, "", "  "+subtleStyle.Render("no rows"))
		}
	}

	info := subtleStyle.Render("\n\n  SELECT only. Max 500 rows.")
	body := strings.Join(lines, "\n") + info
	return title, body, "[tab: switch focus] [↑↓: select] [enter: run] [esc: close]"
}

func (m Model) renderProcessesModal() (string, string, string) {
	title := "Supervised Processes"

	if len(m.data.Processes) == 0 {
		return title, "  " + subtleStyle.Render("(no supervised processes right now)"), "[esc: close]"
	}

	var lines []string
	for i, p := range m.data.Processes {
		glyph := "○"
		st := subtleStyle
		if p.Alive {
			glyph = "●"
			st = safeStyle
		}
		stage := "-"
		if p.StageId != nil {
			stage = *p.StageId
		}
		line := fmt.Sprintf("%s %-6d %-20s %-6s %s",
			st.Render(glyph), p.Pid, truncate(p.Purpose, 20), stage,
			subtleStyle.Render(formatProcessRuntime(p)))
		if i == m.processSelected {
			line = highlightBg.Render(line)
		}
		lines = append(lines, line)
	}

	if m.processSelected < len(m.data.Processes) {
		p := m.data.Processes[m.processSelected]
		if p.LastOutputLine != nil {
			lines = append(lines, "", "  "+subtleStyle.Render("last: ")+truncate(*p.LastOutputLine, 90))
		}
	}

	body := strings.Join(lines, "\n")
	return title, body, "[↑↓: navigate] [esc: close]"
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
	mins := sec / 60
	secs := sec % 60
	if mins > 0 {
		return fmt.Sprintf("%dm%02ds", mins, secs)
	}
	return fmt.Sprintf("%ds", secs)
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
    /         Search transcript (n/N: jump, esc: clear)

  ACTIONS
    :         Command palette (destructive/goto verbs confirm first)
    g         Plan editor (stages, models, workflows, gates, import)
    i         Inject context
    e         Edit templates (v: compiled-prompt preview)
    h         Session history
    s         Supervised processes
    r         Report / query
    t         Timeline (sessions, gates, verdicts, cost)
    c         Native console (raw agent stdout)
    ?         This help

  GLOBAL
    q / ^C    Quit
    esc       Close modal / cancel

  MOUSE
    Scroll    Navigate transcript
    Click     Select a plan-tree row`

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

func truncate(s string, max int) string {
	if len(s) <= max {
		return s
	}
	if max <= 1 {
		return s[:max]
	}
	return s[:max-1] + "…"
}

// truncateLines keeps the first max lines of s, appending a count of what was hidden. Used by the
// compiled-prompt preview so a long prompt stays inside the fixed modal without scrolling machinery.
func truncateLines(s string, max int) string {
	lines := strings.Split(s, "\n")
	if len(lines) <= max {
		return s
	}
	kept := lines[:max]
	return strings.Join(kept, "\n") + fmt.Sprintf("\n… %d more lines", len(lines)-max)
}

func indent(s, prefix string) string {
	lines := strings.Split(s, "\n")
	for i, l := range lines {
		lines[i] = prefix + l
	}
	return strings.Join(lines, "\n")
}
