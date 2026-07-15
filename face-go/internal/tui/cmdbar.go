package tui

// The transient command layer: the `: `palette, the `i` inject bar, `/` search, and the `?` help
// card. These float over the dashboard (bottom bar or a composited overlay) instead of opening a
// modal page — see STYLE.md.

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

var allVerbs = []struct {
	Key  string
	Desc string
	Safe bool
}{
	{"pause", "Pause after current session ends", true},
	{"resume", "Resume a paused run", true},
	{"approve", "Approve and continue", true},
	{"skip", "Skip current stage", true},
	{"abort", "Abort run immediately", false},
	{"kill", "Kill current agent session", false},
	{"stop-after", "Stop after current session", true},
	{"retry-stage", "Reset attempt counter, retry stage", false},
	{"rollback", "Git reset --hard to stage start", false},
	{"pause-after-stage", "Pause once stage completes", true},
	{"goto", "Jump to a different stage (requires stage ID)", true},
}

// --- key handling ------------------------------------------------------------

func (m Model) handleCmdKey(key string) (tea.Model, tea.Cmd) {
	switch m.cmd {
	case CmdHelp:
		if key == "esc" || key == "?" || key == "q" {
			m.cmd = CmdNone
		}
		return m, nil
	case CmdPalette:
		return m.handlePaletteKey(key)
	case CmdInject:
		return m.handleInjectKey(key)
	}
	return m, nil
}

func (m *Model) handleSearchKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.searchActive = false
		m.transcript = m.transcript.Update(widgets.MsgSetSearch{Query: ""})
	case "enter":
		m.searchActive = false
	case "backspace":
		q := m.transcript.SearchQuery
		if len(q) > 0 {
			m.transcript = m.transcript.Update(widgets.MsgSetSearch{Query: q[:len(q)-1]})
		}
	default:
		if ch, ok := typedChar(key); ok {
			m.transcript = m.transcript.Update(widgets.MsgSetSearch{Query: m.transcript.SearchQuery + ch})
		}
	}
	return m, nil
}

func (m *Model) handlePaletteKey(key string) (tea.Model, tea.Cmd) {
	if key == "esc" {
		if m.paletteGotoActive || m.paletteConfirming {
			m.paletteGotoActive, m.paletteConfirming = false, false
			return m, nil
		}
		m.cmd = CmdNone
		return m, nil
	}
	if m.paletteGotoActive {
		switch key {
		case "enter":
			stageId := strings.TrimSpace(m.paletteGotoInput)
			m.paletteGotoActive, m.cmd = false, CmdNone
			return m, m.cmdPostControl(api.ControlRequestDto{Command: "goto", StageId: stageId})
		case "backspace":
			if len(m.paletteGotoInput) > 0 {
				m.paletteGotoInput = m.paletteGotoInput[:len(m.paletteGotoInput)-1]
			}
		default:
			if ch, ok := typedChar(key); ok {
				m.paletteGotoInput += ch
			}
		}
		return m, nil
	}
	if m.paletteConfirming {
		switch strings.ToLower(key) {
		case "y", "enter":
			verb := allVerbs[m.paletteVerbIdx].Key
			m.cmd, m.paletteConfirming = CmdNone, false
			return m, m.cmdPostControl(api.ControlRequestDto{Command: verb, Force: true, Confirmed: true})
		case "n":
			m.paletteConfirming = false
		}
		return m, nil
	}
	switch key {
	case "up", "k":
		if m.paletteSelected > 0 {
			m.paletteSelected--
		}
	case "down", "j":
		if m.paletteSelected < len(m.filteredVerbs())-1 {
			m.paletteSelected++
		}
	case "enter":
		idxs := m.filteredVerbs()
		if m.paletteSelected < len(idxs) {
			origIdx := idxs[m.paletteSelected]
			verb := allVerbs[origIdx]
			if verb.Key == "goto" {
				m.paletteGotoActive, m.paletteGotoInput = true, m.currentStageId()
				return m, nil
			}
			if !verb.Safe {
				m.paletteConfirming, m.paletteVerbIdx = true, origIdx
				return m, nil
			}
			m.cmd = CmdNone
			return m, m.cmdPostControl(api.ControlRequestDto{Command: verb.Key})
		}
	case "backspace":
		if len(m.paletteQuery) > 0 {
			m.paletteQuery, m.paletteSelected = m.paletteQuery[:len(m.paletteQuery)-1], 0
		}
	default:
		if ch, ok := typedChar(key); ok {
			m.paletteQuery, m.paletteSelected = m.paletteQuery+ch, 0
		}
	}
	return m, nil
}

func (m *Model) handleInjectKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.cmd = CmdNone
	case "tab":
		m.injectField = 1 - m.injectField
	case "backspace":
		if m.injectField == 0 && len(m.injectStageId) > 0 {
			m.injectStageId = m.injectStageId[:len(m.injectStageId)-1]
		} else if m.injectField == 1 && len(m.injectContent) > 0 {
			m.injectContent = m.injectContent[:len(m.injectContent)-1]
		}
	case "ctrl+s":
		if strings.TrimSpace(m.injectContent) == "" {
			return m, nil
		}
		req := api.InjectRequestDto{Content: m.injectContent, StageId: strings.TrimSpace(m.injectStageId)}
		m.cmd = CmdNone
		return m, m.cmdPostInject(req)
	default:
		if ch, ok := typedChar(key); ok {
			if m.injectField == 0 {
				m.injectStageId += ch
			} else {
				m.injectContent += ch
			}
		}
	}
	return m, nil
}

func (m Model) filteredVerbs() []int {
	if m.paletteQuery == "" {
		idxs := make([]int, len(allVerbs))
		for i := range allVerbs {
			idxs[i] = i
		}
		return idxs
	}
	var idxs []int
	q := strings.ToLower(m.paletteQuery)
	for i, v := range allVerbs {
		if strings.Contains(strings.ToLower(v.Key), q) || strings.Contains(strings.ToLower(v.Desc), q) {
			idxs = append(idxs, i)
		}
	}
	return idxs
}

// --- rendering ---------------------------------------------------------------

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
	globals := subtleStyle.Render(key(":") + " cmd  " + key("i") + " inject  " + key("/") + " search  " + key("p") + " sidebar  " + key("?") + " help  " + key("q") + " quit")
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

func (m Model) renderHelpOverlay() string {
	body := "" +
		accentStyle.Render("Tabs") + subtleStyle.Render("  (number or letter jumps straight there)") + "\n" +
		"  " + key("a") + " Agent    " + key("h") + " Sessions   " + key("t") + " Timeline\n" +
		"  " + key("s") + " Procs    " + key("c") + " Console    " + key("e") + " Templates\n" +
		"  " + key("g") + " Plan     " + key("r") + " Report     " + key("tab") + " cycle tabs\n\n" +
		accentStyle.Render("Actions") + "\n" +
		"  " + key(":") + " command palette   " + key("i") + " inject context\n" +
		"  " + key("/") + " search transcript " + key("f") + " fold tool calls\n" +
		"  " + key("p") + " collapse sidebar  " + key("↑↓") + " scroll / navigate\n\n" +
		accentStyle.Render("Global") + "\n" +
		"  " + key("q") + " quit   " + key("esc") + " close / cancel   " + key("?") + " this help"

	title := accentStyle.Render("◆ conductor") + subtleStyle.Render("  ·  keys")
	return lipgloss.NewStyle().
		Background(widgets.Mantle()).
		Border(lipgloss.RoundedBorder()).BorderForeground(widgets.Accent()).
		Padding(1, 3).
		Render(title + "\n\n" + body)
}
