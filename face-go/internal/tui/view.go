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
	accentStyle   = lipgloss.NewStyle().Foreground(widgets.Accent()).Bold(true)
	subtleStyle   = lipgloss.NewStyle().Foreground(widgets.Overlay())
	textStyle     = lipgloss.NewStyle().Foreground(widgets.Text())
	highlightBg   = lipgloss.NewStyle().Background(widgets.Selection()).Foreground(widgets.Text())
	destructStyle = lipgloss.NewStyle().Foreground(widgets.Red())
	warnStyle     = lipgloss.NewStyle().Foreground(widgets.Yellow())
	safeStyle     = lipgloss.NewStyle().Foreground(widgets.Green())
	tealStyle     = lipgloss.NewStyle().Foreground(widgets.Teal())

	keyStyle = lipgloss.NewStyle().Foreground(widgets.Accent()).Bold(true)
)

// key styles a keycap for hint lines.
func key(s string) string { return keyStyle.Render(s) }

func (m Model) View() tea.View {
	if m.width < 20 || m.height < 8 {
		return tea.NewView("Terminal too small — resize to at least 20×8")
	}

	layout := ComputeLayout(m.width, m.height, m.sidebarCollapsed)

	top := widgets.RenderTopBar(m.data.Connection, m.data.Plan, m.width)
	tabs := m.renderTabStrip(m.width)

	body, help := m.paneView(layout.Content)
	content := m.frameContent(body, layout.Content)

	middle := content
	if layout.Sidebar.Width > 0 {
		middle = lipgloss.JoinHorizontal(lipgloss.Top, m.renderSidebar(layout.Sidebar), content)
	}

	bottom := m.renderBottomBar(m.width, help)

	screen := lipgloss.JoinVertical(lipgloss.Top, top, tabs, middle, bottom)

	// Transparent overlays — composited onto the live dashboard so the background stays visible.
	switch {
	case m.cmd == CmdHelp:
		screen = compositeCenter(screen, m.renderHelpOverlay(), m.width, m.height)
	case m.cmd == CmdPalette:
		screen = m.overlayPalette(screen, layout)
	}

	if toasts := widgets.RenderToasts(m.toasts); toasts != "" {
		screen = compositeBottomRight(screen, toasts, m.width, m.height)
	}

	v := tea.NewView(screen)
	v.AltScreen = true
	v.MouseMode = tea.MouseModeCellMotion
	v.BackgroundColor = widgets.Base()
	return v
}

// --- tab strip -------------------------------------------------------------

func (m Model) renderTabStrip(width int) string {
	strip := lipgloss.NewStyle().Background(widgets.Mantle()).Padding(0, 1).MaxHeight(1).MaxWidth(width)
	activeTab := lipgloss.NewStyle().Background(widgets.Accent()).Foreground(widgets.Base()).Bold(true)
	idleTab := lipgloss.NewStyle().Foreground(widgets.Overlay())
	keyStyle := lipgloss.NewStyle().Foreground(widgets.Accent())

	var parts []string
	for i := 0; i < int(tabCount); i++ {
		label := fmt.Sprintf(" %s %s ", tabKey[i], tabNames[i])
		if MainTab(i) == m.tab {
			parts = append(parts, activeTab.Render(label))
		} else if width >= 100 {
			parts = append(parts, keyStyle.Render(" "+tabKey[i]+" ")+idleTab.Render(tabNames[i]+" "))
		} else {
			parts = append(parts, keyStyle.Render(tabKey[i]))
		}
	}
	sep := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(" ")
	return strip.Render(strings.Join(parts, sep))
}

// --- sidebar (always visible unless collapsed) -----------------------------

func (m Model) renderSidebar(rect Rect) string {
	m.sidebar.Stages = nil
	m.sidebar.Gates = nil
	if m.data.Plan != nil {
		m.sidebar.Stages = m.data.Plan.Stages
		m.sidebar.Gates = m.data.Plan.Gates
	}
	m.sidebar.Width = rect.Width - 2
	m.sidebar.Height = rect.Height - 1

	return lipgloss.NewStyle().
		Width(rect.Width).Height(rect.Height).
		Padding(1, 1).
		Border(lipgloss.NormalBorder(), false, true, false, false).
		BorderForeground(widgets.Surface()).
		Render(m.sidebar.View())
}

// frameContent gives every pane consistent breathing room and a fixed footprint.
func (m Model) frameContent(body string, rect Rect) string {
	return lipgloss.NewStyle().
		Width(rect.Width).Height(rect.Height).
		Padding(1, 2).
		Render(body)
}

// paneView returns the active tab's body plus the contextual help shown in the bottom bar.
func (m Model) paneView(rect Rect) (body, help string) {
	switch m.tab {
	case TabAgent:
		return m.transcript.View(), "↑↓ scroll · f fold · / search"
	case TabSessions:
		_, b, h := m.renderSessionsPane()
		return b, h
	case TabTimeline:
		_, b, h := m.renderTimelinePane()
		return b, h
	case TabProcesses:
		_, b, h := m.renderProcessesPane()
		return b, h
	case TabConsole:
		_, b, h := m.renderConsolePane()
		return b, h
	case TabTemplates:
		_, b, h := m.renderTemplatesPane()
		return b, h
	case TabPlan:
		_, b, h := m.renderPlanPane()
		return b, h
	case TabReport:
		_, b, h := m.renderReportPane()
		return b, h
	}
	return "", ""
}

// --- bottom bar ------------------------------------------------------------

func (m Model) renderBottomBar(width int, paneHelp string) string {
	bar := lipgloss.NewStyle().Background(widgets.Mantle()).Padding(0, 1).MaxHeight(1).MaxWidth(width)

	switch m.cmd {
	case CmdInject:
		return bar.Render(m.renderInjectBar())
	case CmdPalette:
		if m.paletteGotoActive {
			return bar.Render(accentStyle.Render("goto stage› ") + textStyle.Render(m.paletteGotoInput) + accentStyle.Render("▏"))
		}
		if m.paletteConfirming {
			v := allVerbs[m.paletteVerbIdx]
			return bar.Render(destructStyle.Render("⚠ "+v.Key+" — ") + warnStyle.Render("confirm? y/N"))
		}
		return bar.Render(accentStyle.Render(": ") + textStyle.Render(m.paletteQuery) + accentStyle.Render("▏") + subtleStyle.Render("  ↑↓ enter esc"))
	}

	if m.searchActive || m.transcript.SearchQuery != "" {
		return bar.Render(m.renderSearchLine())
	}

	// Normal hints: global keys + the active pane's contextual help.
	globals := subtleStyle.Render(key(":")+" cmd  "+key("i")+" inject  "+key("/")+" search  "+key("p")+" sidebar  "+key("?")+" help  "+key("q")+" quit")
	if paneHelp != "" && width >= 90 {
		return bar.Render(globals + subtleStyle.Render("   │   ") + subtleStyle.Render(paneHelp))
	}
	return bar.Render(globals)
}

func (m Model) renderInjectBar() string {
	stage := m.injectStageId
	if stage == "" {
		stage = "current"
	}
	cursorStage, cursorBody := "", "▏"
	if m.injectField == 0 {
		cursorStage, cursorBody = "▏", ""
	}
	return accentStyle.Render("inject") + subtleStyle.Render("[") + tealStyle.Render(stage) + cursorStage + subtleStyle.Render("]› ") +
		textStyle.Render(m.injectContent) + accentStyle.Render(cursorBody) +
		subtleStyle.Render("   tab field · ctrl+s send · esc cancel")
}

func (m Model) renderSearchLine() string {
	q := m.transcript.SearchQuery
	cursor := ""
	if m.searchActive {
		cursor = "▏"
	}
	matchInfo := subtleStyle.Render("no matches")
	if len(m.transcript.SearchMatches) > 0 {
		matchInfo = accentStyle.Render(fmt.Sprintf("%d/%d", m.transcript.SearchMatchIdx+1, len(m.transcript.SearchMatches)))
	}
	hint := "enter lock · esc clear"
	if !m.searchActive {
		hint = "n/N next/prev · esc clear"
	}
	return accentStyle.Render("/") + textStyle.Render(q) + accentStyle.Render(cursor) + "  " + matchInfo + "  " + subtleStyle.Render(hint)
}

// --- palette floating list -------------------------------------------------

func (m Model) overlayPalette(screen string, layout LayoutRects) string {
	if m.paletteGotoActive || m.paletteConfirming {
		return screen // the bottom bar already shows the goto/confirm prompt
	}
	idxs := m.filteredVerbs()
	if len(idxs) == 0 {
		return screen
	}
	var lines []string
	for row, origIdx := range idxs {
		if origIdx >= len(allVerbs) {
			continue
		}
		v := allVerbs[origIdx]
		mark, st := "  ", textStyle
		if !v.Safe {
			mark, st = destructStyle.Render("⚠ "), destructStyle
		}
		paddedKey := fmt.Sprintf("%-16s", v.Key) // pad the plain text, then colour it (ANSI-safe alignment)
		line := mark + st.Render(paddedKey) + " " + subtleStyle.Render(v.Desc)
		if row == m.paletteSelected {
			line = highlightBg.Render(fmt.Sprintf(" %s %-16s %s", ternary(v.Safe, " ", "⚠"), v.Key, v.Desc))
		}
		lines = append(lines, line)
	}
	box := lipgloss.NewStyle().
		Background(widgets.Mantle()).
		Border(lipgloss.RoundedBorder()).BorderForeground(widgets.Accent()).
		Padding(0, 1).
		Render(strings.Join(lines, "\n"))
	// Float it just above the bottom bar, left-aligned.
	x := 1
	y := layout.Bottom.Y - lipgloss.Height(box)
	if y < layout.Tabs.Y+1 {
		y = layout.Tabs.Y + 1
	}
	return compositeAt(screen, box, x, y)
}

// --- help overlay ----------------------------------------------------------

func (m Model) renderHelpOverlay() string {
	body := "" +
		accentStyle.Render("Tabs") + subtleStyle.Render("  (number or letter jumps straight there)") + "\n" +
		"  " + key("a") + " Agent    " + key("h") + " Sessions   " + key("t") + " Timeline\n" +
		"  " + key("s") + " Procs    " + key("c") + " Console    " + key("e") + " Templates\n" +
		"  " + key("g") + " Plan     " + key("r") + " Report     " + key("tab") + " cycle tabs\n\n" +
		accentStyle.Render("Actions") + "\n" +
		"  " + key(":") + " command palette   " + key("i") + " inject context\n" +
		"  " + key("/") + " search transcript " + key("f") + " fold tool calls\n" +
		"  " + key("p") + " collapse sidebar  " + key("↑↓") + " navigate\n\n" +
		accentStyle.Render("Global") + "\n" +
		"  " + key("q") + " quit   " + key("esc") + " close / cancel   " + key("?") + " this help"

	title := accentStyle.Render("◆ conductor") + subtleStyle.Render("  ·  keys")
	return lipgloss.NewStyle().
		Background(widgets.Mantle()).
		Border(lipgloss.RoundedBorder()).BorderForeground(widgets.Accent()).
		Padding(1, 3).
		Render(title + "\n\n" + body)
}

// --- pane bodies (carried over, now filling the content pane) ---------------

func (m Model) renderConsolePane() (string, string, string) {
	title := "Native Console — raw agent stdout"
	lines := m.data.RawConsole
	if len(lines) == 0 {
		return title, subtleStyle.Render("(no raw output yet — the agent tees stdout to .conductor/logs/session-NNN.jsonl)"), "↑↓ scroll · end live-tail"
	}
	window := m.paneRows()
	end := len(lines) - m.consoleScroll
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
		out = append(out, subtleStyle.Render(truncate(lines[i].Text, m.paneCols())))
	}
	pos := "live tail"
	if m.consoleScroll > 0 {
		pos = fmt.Sprintf("scrolled back %d", m.consoleScroll)
	}
	out = append(out, "", subtleStyle.Render(fmt.Sprintf("%d lines · %s", len(lines), pos)))
	return title, strings.Join(out, "\n"), "↑↓ scroll · end live-tail"
}

func (m Model) renderTimelinePane() (string, string, string) {
	title := "Timeline"
	if m.timelineLoading && len(m.timelineEntries) == 0 {
		return title, subtleStyle.Render("loading…"), "r refresh"
	}
	if m.timelineErr != "" {
		return title, destructStyle.Render("error: "+m.timelineErr), "r retry"
	}
	if len(m.timelineEntries) == 0 {
		return title, subtleStyle.Render("(no events on the run's spine yet)"), "r refresh"
	}
	window := m.paneRows()
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
		desc := truncate(e.Description, m.paneCols()-16)
		cost := ""
		if e.CostUsd != nil && *e.CostUsd > 0 {
			cost = subtleStyle.Render(fmt.Sprintf("  $%.2f", *e.CostUsd))
		}
		line := fmt.Sprintf("%s %s %s%s", subtleStyle.Render(clock), gs.Render(glyph), textStyle.Render(desc), cost)
		if i == m.timelineSelected {
			line = highlightBg.Render(fmt.Sprintf("%s %s %s", clock, glyph, desc))
		}
		lines = append(lines, line)
	}
	return title, strings.Join(lines, "\n"), "↑↓ navigate · r refresh"
}

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

func timelineClock(utc string) string {
	t, err := time.Parse(time.RFC3339, utc)
	if err != nil {
		return "--:--:--"
	}
	return t.UTC().Format("15:04:05")
}

func (m Model) renderSessionsPane() (string, string, string) {
	title := "Session History"
	if len(m.data.Sessions) == 0 {
		return title, subtleStyle.Render("(no sessions loaded)"), ""
	}
	var lines []string
	for i, s := range m.data.Sessions {
		outcome := "running"
		if s.Outcome != nil {
			outcome = *s.Outcome
		}
		line := fmt.Sprintf("#%-3d %-8s %-7s %-12s commits:%d", s.Number, s.StageId, s.Kind, outcome, s.CommitCount)
		if i == m.sessionSelected {
			line = highlightBg.Render(line)
		}
		lines = append(lines, line)
	}
	if m.sessionSelected < len(m.data.Sessions) {
		s := m.data.Sessions[m.sessionSelected]
		detail := fmt.Sprintf("\n%s #%d | %s %s | %s",
			accentStyle.Render("Session"), s.Number, accentStyle.Render(s.StageId), accentStyle.Render(s.Kind),
			subtleStyle.Render(fmt.Sprintf("attempt %d, %d resumes", s.Attempt, s.ResumeCount)))
		if s.GateSummary != nil {
			detail += "\nGates: " + *s.GateSummary
		}
		if s.ResultSummary != nil {
			detail += "\n" + subtleStyle.Render("Result:") + "\n" + indent(renderMarkdown(*s.ResultSummary, m.paneCols()-4), "  ")
		}
		lines = append(lines, detail)
	}
	return title, strings.Join(lines, "\n"), "↑↓ navigate"
}

func (m Model) renderProcessesPane() (string, string, string) {
	title := "Supervised Processes"
	if len(m.data.Processes) == 0 {
		return title, subtleStyle.Render("(no supervised processes right now)"), ""
	}
	var lines []string
	for i, p := range m.data.Processes {
		glyph, st := "○", subtleStyle
		if p.Alive {
			glyph, st = "●", safeStyle
		}
		stage := "-"
		if p.StageId != nil {
			stage = *p.StageId
		}
		line := fmt.Sprintf("%s %-6d %-22s %-6s %s", st.Render(glyph), p.Pid, truncate(p.Purpose, 22), stage, subtleStyle.Render(formatProcessRuntime(p)))
		if i == m.processSelected {
			line = highlightBg.Render(fmt.Sprintf("%s %-6d %-22s %-6s %s", glyph, p.Pid, truncate(p.Purpose, 22), stage, formatProcessRuntime(p)))
		}
		lines = append(lines, line)
	}
	if m.processSelected < len(m.data.Processes) {
		p := m.data.Processes[m.processSelected]
		if p.LastOutputLine != nil {
			lines = append(lines, "", subtleStyle.Render("last: ")+truncate(*p.LastOutputLine, m.paneCols()-8))
		}
	}
	return title, strings.Join(lines, "\n"), "↑↓ navigate"
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

func (m Model) renderReportPane() (string, string, string) {
	title := "Report / Query"
	var lines []string
	lines = append(lines, subtleStyle.Render("Quick queries:"))
	for i, q := range quickQueries {
		marker := "  "
		if i == m.reportQuickSelected && !m.reportFocusQuery {
			marker = accentStyle.Render("› ")
		}
		lines = append(lines, marker+q.Label)
	}
	lines = append(lines, "")
	sf := subtleStyle
	if m.reportFocusQuery {
		sf = accentStyle
	}
	sqlDisplay := m.reportSQL
	if m.reportFocusQuery {
		sqlDisplay += "▏"
	}
	lines = append(lines, sf.Render("SQL:"), textStyle.Render(truncate(sqlDisplay, m.paneCols())))
	if m.data.ReportLoading {
		lines = append(lines, "", subtleStyle.Render("running…"))
	} else if m.data.ReportResult != nil {
		switch {
		case m.data.ReportResult.Error != nil:
			lines = append(lines, "", destructStyle.Render("error: "+*m.data.ReportResult.Error))
		case len(m.data.ReportResult.Columns) > 0:
			lines = append(lines, "", accentStyle.Render(strings.Join(m.data.ReportResult.Columns, " │ ")))
			for _, row := range m.data.ReportResult.Rows {
				lines = append(lines, subtleStyle.Render(truncate(strings.Join(row.Values, " │ "), m.paneCols())))
			}
			if m.data.ReportResult.Truncated {
				lines = append(lines, warnStyle.Render("… truncated"))
			}
		default:
			lines = append(lines, "", subtleStyle.Render("no rows"))
		}
	}
	return title, strings.Join(lines, "\n"), "tab focus · ↑↓ pick · enter run"
}

func (m Model) renderTemplatesPane() (string, string, string) {
	title := "Templates & Compiled Prompt"

	// Split: list on the left, editor or compiled preview on the right.
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
	return title, body, help
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

// --- pane sizing helpers ---------------------------------------------------

func (m Model) paneCols() int {
	c := ComputeLayout(m.width, m.height, m.sidebarCollapsed).Content.Width - 4
	if c < 10 {
		c = 10
	}
	return c
}

func (m Model) paneRows() int {
	r := ComputeLayout(m.width, m.height, m.sidebarCollapsed).Content.Height - 3
	if r < 3 {
		r = 3
	}
	return r
}

// --- composite (transparent overlay) helpers -------------------------------

func compositeCenter(bg, box string, width, height int) string {
	x := (width - lipgloss.Width(box)) / 2
	y := (height - lipgloss.Height(box)) / 2
	return compositeAt(bg, box, x, y)
}

func compositeBottomRight(bg, box string, width, height int) string {
	x := width - lipgloss.Width(box) - 1
	y := height - lipgloss.Height(box) - 1
	return compositeAt(bg, box, x, y)
}

func compositeAt(bg, box string, x, y int) string {
	if x < 0 {
		x = 0
	}
	if y < 0 {
		y = 0
	}
	base := lipgloss.NewLayer(bg)
	over := lipgloss.NewLayer(box).X(x).Y(y).Z(1)
	return lipgloss.NewCompositor(base, over).Render()
}

func truncate(s string, max int) string {
	if max < 1 {
		return ""
	}
	if lipgloss.Width(s) <= max {
		return s
	}
	if max <= 1 {
		return string([]rune(s)[:max])
	}
	r := []rune(s)
	if len(r) > max {
		return string(r[:max-1]) + "…"
	}
	return s
}

func truncateLines(s string, max int) string {
	lines := strings.Split(s, "\n")
	if len(lines) <= max || max < 1 {
		return s
	}
	return strings.Join(lines[:max], "\n") + fmt.Sprintf("\n… %d more lines", len(lines)-max)
}

func indent(s, prefix string) string {
	lines := strings.Split(s, "\n")
	for i, l := range lines {
		lines[i] = prefix + l
	}
	return strings.Join(lines, "\n")
}

func ternary(cond bool, a, b string) string {
	if cond {
		return a
	}
	return b
}
