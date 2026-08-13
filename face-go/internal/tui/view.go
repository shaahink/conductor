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

// This package's shared styles. They capture their colour BY VALUE at construction, so they are
// declared bare and built by rebuildStyles — see ApplyTheme in theme.go for why this package needs a
// rebuild of its own rather than riding the widgets one.
var (
	accentStyle   lipgloss.Style
	subtleStyle   lipgloss.Style
	textStyle     lipgloss.Style
	highlightBg   lipgloss.Style
	destructStyle lipgloss.Style
	warnStyle     lipgloss.Style
	safeStyle     lipgloss.Style
	tealStyle     lipgloss.Style
	peachStyle    lipgloss.Style
	// blue = active / in-progress (STYLE.md's role table). It had no shared var, so panes reached
	// for widgets.Blue() ad hoc; the Report tab needs it for running stages/sessions/gates.
	infoStyle lipgloss.Style

	keyStyle lipgloss.Style
)

func init() { rebuildStyles() }

// rebuildStyles repaints this package's shared styles from the live palette. Only ApplyTheme (and
// init) should call it.
func rebuildStyles() {
	accentStyle = lipgloss.NewStyle().Foreground(widgets.Accent()).Bold(true)
	subtleStyle = lipgloss.NewStyle().Foreground(widgets.Overlay())
	textStyle = lipgloss.NewStyle().Foreground(widgets.Text())
	highlightBg = lipgloss.NewStyle().Background(widgets.Selection()).Foreground(widgets.Text())
	destructStyle = lipgloss.NewStyle().Foreground(widgets.Red())
	warnStyle = lipgloss.NewStyle().Foreground(widgets.Yellow())
	safeStyle = lipgloss.NewStyle().Foreground(widgets.Green())
	tealStyle = lipgloss.NewStyle().Foreground(widgets.Teal())
	peachStyle = lipgloss.NewStyle().Foreground(widgets.Peach())
	infoStyle = lipgloss.NewStyle().Foreground(widgets.Blue())
	keyStyle = lipgloss.NewStyle().Foreground(widgets.Accent()).Bold(true)
	// Markdown is memoised per (theme, width, source) and the theme is part of that key, so a switch
	// is already correct without this — dropping the memo just stops the old palette's renders from
	// occupying the cap until they age out (K6.4).
	resetMarkdownCache()
}

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

	// Frame invariant: never hand the renderer more than the window. Everything above already
	// budgets its height, but one miscounted row (a wrapped line, a grown banner) must degrade to
	// a clipped pane — not to the bottom bar and the live tail sliding below the fold.
	screen = lipgloss.NewStyle().MaxWidth(m.width).MaxHeight(m.height).Render(screen)

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

	// Max* are the hard clamp: .Width/.Height only pad SHORT content — an overgrown pane would
	// otherwise stretch the frame past the window and push the bottom bar off-screen.
	return lipgloss.NewStyle().
		Width(rect.Width).Height(rect.Height).
		MaxWidth(rect.Width).MaxHeight(rect.Height).
		Padding(1, 1).
		Border(lipgloss.NormalBorder(), false, true, false, false).
		BorderForeground(widgets.Surface()).
		Render(m.sidebar.View())
}

// frameContent gives every pane consistent breathing room and a fixed footprint. The Max* pair is
// the overflow guard: .Width/.Height only pad short content, they never truncate tall content, so
// without the clamp one overgrown row pushes the bottom bar (and a pinned live tail) below the fold.
func (m Model) frameContent(body string, rect Rect) string {
	return lipgloss.NewStyle().
		Width(rect.Width).Height(rect.Height).
		MaxWidth(rect.Width).MaxHeight(rect.Height).
		Padding(1, 2).
		Render(body)
}

// paneView returns the active tab's body plus the contextual help shown in the bottom bar.
func (m Model) paneView() (body, help string) {
	switch m.tab {
	case TabHome:
		return m.renderHomePane()
	case TabAgent:
		return m.renderAgentPane()
	case TabHistory:
		return m.renderHistoryPane()
	case TabProcesses:
		return m.renderProcessesPane()
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

// historyRows is what a History VIEW gets: the pane minus the view switcher that sits above both of
// them. A view that sized itself against paneRows would be one row too tall and lose its own last
// row to the frame's height clamp — which for the spine is the detail pane it exists to show.
func (m Model) historyRows() int {
	r := m.paneRows() - 1
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

// wrapPlain wraps PLAIN text to w columns and styles each resulting line afterwards — never the
// other way round, because width-formatting an already-styled string measures its escape bytes and
// shears the pane (STYLE.md). It is what a sentence gets instead of `truncate` when the sentence
// names the key that acts on it: cutting "press q to override" at the pane edge deletes the
// affordance and leaves a row that reads like a bug.
//
// KS2.7 added it because the pane viewport does not soft-wrap (deliberately — see newPaneViewport),
// so an over-wide row is now CLIPPED with no ellipsis rather than wrapped by the terminal. Every
// line handed to a viewport has to already fit.
func wrapPlain(text string, w int) []string {
	wrapped := lipgloss.NewStyle().Width(max(8, w)).Render(text)
	lines := strings.Split(wrapped, "\n")
	for i, l := range lines {
		lines[i] = strings.TrimRight(l, " ")
	}
	return lines
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
