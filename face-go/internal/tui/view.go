package tui

// The dashboard skeleton: top bar · tab strip · [sidebar | content] · bottom bar, with the rare
// transparent overlays (palette, help, toasts) composited on top. Each tab's body lives in its
// own tab_*.go / plan.go file; this file only assembles the frame.

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

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
	peachStyle    = lipgloss.NewStyle().Foreground(widgets.Peach())

	keyStyle = lipgloss.NewStyle().Foreground(widgets.Accent()).Bold(true)
)

// key styles a keycap for hint lines.
func key(s string) string { return keyStyle.Render(s) }

func (m Model) View() tea.View {
	if m.width < 20 || m.height < 8 {
		return tea.NewView("Terminal too small — resize to at least 20×8")
	}

	layout := ComputeLayout(m.width, m.height, m.sidebarCollapsed)

	top := widgets.RenderTopBar(m.data.Connection, m.data.Plan, m.width, m.spinnerFrame)
	tabs := m.renderTabStrip(m.width)

	body, help := m.paneView()
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

// --- tab strip ---------------------------------------------------------------

func (m Model) renderTabStrip(width int) string {
	// Adaptive: full names when every tab fits, otherwise compact (key-only idle tabs, the active
	// one keeps its name). A fixed width threshold silently clipped the last tab off the strip.
	strip := lipgloss.NewStyle().Background(widgets.Mantle()).Padding(0, 1).MaxHeight(1).MaxWidth(width)
	if full := m.joinTabParts(true); lipgloss.Width(full)+2 <= width { // +2: the strip's padding
		return strip.Render(full)
	}
	return strip.Render(m.joinTabParts(false))
}

func (m Model) joinTabParts(fullNames bool) string {
	activeTab := lipgloss.NewStyle().Background(widgets.Accent()).Foreground(widgets.Base()).Bold(true)
	idleTab := lipgloss.NewStyle().Foreground(widgets.Overlay())
	keyStyle := lipgloss.NewStyle().Foreground(widgets.Accent())

	var parts []string
	for i := 0; i < int(tabCount); i++ {
		label := fmt.Sprintf(" %s %s ", tabKey[i], tabNames[i])
		if MainTab(i) == m.tab {
			parts = append(parts, activeTab.Render(label))
		} else if fullNames {
			parts = append(parts, keyStyle.Render(" "+tabKey[i]+" ")+idleTab.Render(tabNames[i]+" "))
		} else {
			parts = append(parts, keyStyle.Render(tabKey[i]))
		}
	}
	sep := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(" ")
	return strings.Join(parts, sep)
}

// --- sidebar (always visible unless collapsed) --------------------------------

func (m Model) renderSidebar(rect Rect) string {
	// lipgloss v2 counts the border inside .Width(): content = width − 1 (border) − 2 (padding).
	m.sidebar.Width = rect.Width - 3
	m.sidebar.Height = rect.Height - 2

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
func (m Model) paneView() (body, help string) {
	switch m.tab {
	case TabAgent:
		return m.renderAgentPane()
	case TabSessions:
		return m.renderSessionsPane()
	case TabTimeline:
		return m.renderTimelinePane()
	case TabProcesses:
		return m.renderProcessesPane()
	case TabConsole:
		return m.renderConsolePane()
	case TabTemplates:
		return m.renderTemplatesPane()
	case TabPlan:
		return m.renderPlanPane()
	case TabReport:
		return m.renderReportPane()
	case TabKnowledge:
		return m.renderKnowledgePane()
	case TabTelegram:
		return m.renderTelegramPane()
	case TabKanban:
		return m.renderKanbanPane()
	}
	return "", ""
}

// --- pane sizing helpers -------------------------------------------------------

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

// --- composite (transparent overlay) helpers -----------------------------------

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

// --- shared text helpers ---------------------------------------------------------

func truncate(s string, max int) string {
	if max < 1 {
		return ""
	}
	if lipgloss.Width(s) <= max {
		return s
	}
	r := []rune(s)
	if len(r) > max {
		if max == 1 {
			return "…"
		}
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
