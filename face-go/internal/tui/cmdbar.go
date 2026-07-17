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

// verbGroup buckets the palette into the three questions the owner actually asks: what is the run
// doing, what is this stage doing, and what will hurt. Grouping is presentation only — `Safe` stays
// the single safety contract, so a verb's group can be reordered without changing what it confirms.
type verbGroup string

const (
	groupRun    verbGroup = "Run"
	groupStage  verbGroup = "Stage"
	groupDanger verbGroup = "Danger"
)

// groupOrder fixes the render order of the headers; allVerbs is kept sorted to match so the
// overlay can emit a header on each group change in one pass.
var groupOrder = []verbGroup{groupRun, groupStage, groupDanger}

// verbKeyPad is the palette's key column. It must clear the longest verb ("pause-after-stage", 17)
// or that row's description hangs one column right of every other row's — pinned by
// TestVerbKeyColumnFitsEveryVerb so adding a longer verb fails loudly instead of skewing the pane.
const verbKeyPad = 18

// allVerbs is a flat list deliberately ORDERED BY GROUP, not a map of groups: every existing
// index-based path (filteredVerbs, paletteSelected, paletteVerbIdx) keeps working unchanged, and
// the overlay just inserts a header whenever Group differs from the previous row.
var allVerbs = []struct {
	Key  string
	Desc string
	Safe bool
	// Group buckets the row under a header in the overlay.
	Group verbGroup
	// Consequence completes "<key> — <consequence>. y/N" on the confirm line: it must say what
	// the verb DOES, in the owner's terms, not restate the verb. Required for every !Safe verb —
	// TestEveryUnsafeVerbNamesItsConsequence pins that, so a new danger verb cannot ship with a
	// bare "confirm?" prompt.
	Consequence string
}{
	// Run — steering the loop. All reversible, none confirm.
	{"pause", "Pause after current session ends", true, groupRun, ""},
	{"resume", "Resume a paused run", true, groupRun, ""},
	{"stop-after", "Stop after current session", true, groupRun, ""},
	{"approve", "Approve and continue", true, groupRun, ""},
	{"heartbeat", "Refresh REPORT.md snapshot now", true, groupRun, ""},
	{"reload-plan", "Swap live plan at next session boundary", true, groupRun, ""},
	// Stage — moving around the plan.
	{"goto", "Jump to a different stage (requires stage ID)", true, groupStage, ""},
	{"retry-stage", "Reset attempt counter, retry stage", false, groupStage,
		"reset attempt counter + rerun stage"},
	{"skip", "Skip current stage", true, groupStage, ""},
	{"pause-after-stage", "Pause once stage completes", true, groupStage, ""},
	// Danger — destroys work or stops the run.
	{"kill", "Kill current agent session", false, groupDanger,
		"kill this agent session; run continues"},
	{"abort", "Abort run immediately", false, groupDanger,
		"kill session + stop conductor"},
	{"rollback", "Git reset --hard to stage start", false, groupDanger,
		"git reset --hard to stage start; uncommitted work lost"},
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
			// Name the consequence, never a bare "confirm?" (U2.1): the owner must be able to
			// decide from this one line without remembering what the verb does.
			return bar.Render(destructStyle.Render("⚠ "+v.Key+" — ") +
				textStyle.Render(v.Consequence+". ") + warnStyle.Render("y/N"))
		}
		return bar.Render(accentStyle.Render(": ") + textStyle.Render(m.paletteQuery) + accentStyle.Render("▏") + subtleStyle.Render("  ↑↓ enter esc"))
	}

	if m.searchActive || m.transcript.SearchQuery != "" {
		return bar.Render(m.renderSearchLine())
	}

	// Normal hints: global keys + the active pane's contextual help.
	globals := subtleStyle.Render(key(":") + " cmd  " + key("i") + " inject  " + key("/") + " search  " + key("\\") + " sidebar  " + key("?") + " help  " + key("q") + " quit")
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
	lastGroup := verbGroup("")
	for row, origIdx := range idxs {
		if origIdx >= len(allVerbs) {
			continue
		}
		v := allVerbs[origIdx]
		// A header per group change. Filtering keeps allVerbs' group order, so a query that
		// matches across groups still shows each surviving group under its own header.
		if v.Group != lastGroup {
			lines = append(lines, subtleStyle.Render(string(v.Group)))
			lastGroup = v.Group
		}
		mark, st := "  ", textStyle
		if !v.Safe {
			mark, st = "⚠ ", destructStyle
		}
		// Pad the plain text, then colour it (STYLE.md: never %-Ns a styled string). The selected
		// row is built from the SAME plain layout so highlighting never shifts a row sideways.
		plain := mark + fmt.Sprintf("%-*s", verbKeyPad, v.Key)
		line := st.Render(plain) + " " + subtleStyle.Render(v.Desc)
		if row == m.paletteSelected {
			line = highlightBg.Render(plain + " " + v.Desc)
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

// tabLegendCell is one "k Name" column of the help card's Tabs grid. The NAME is padded as plain text
// before any styling — padding a styled string pads its escape bytes (STYLE.md).
func tabLegendCell(k, name string) string {
	return key(k) + " " + fmt.Sprintf("%-11s", name)
}

// verbGroupLegend renders one "Group  verb · verb" row of the help card, derived from allVerbs so
// the help can never drift from the palette it documents. Danger keys stay red.
func verbGroupLegend(g verbGroup) string {
	var keys []string
	for _, v := range allVerbs {
		if v.Group != g {
			continue
		}
		if v.Safe {
			keys = append(keys, textStyle.Render(v.Key))
		} else {
			keys = append(keys, destructStyle.Render(v.Key))
		}
	}
	return subtleStyle.Render(fmt.Sprintf("%-7s", string(g))) + strings.Join(keys, subtleStyle.Render(" · "))
}

func (m Model) renderHelpOverlay() string {
	// Row budget matters: this card must stay inside an 80x24 terminal, border included
	// (TestHelpOverlayFitsSmallestTerminal). The `tab` hint rides the Tabs heading and `:` is
	// documented by the Palette heading rather than each costing a row of its own.
	body := "" +
		accentStyle.Render("Tabs") + subtleStyle.Render("  (letter or number jumps · ") + key("tab") +
		subtleStyle.Render(" cycles)") + "\n" +
		"  " + tabLegendCell("h", "Home") + tabLegendCell("a", "Agent") + tabLegendCell("s", "Sessions") + "\n" +
		"  " + tabLegendCell("t", "Timeline") + tabLegendCell("o", "Procs") + tabLegendCell("c", "Console") + "\n" +
		"  " + tabLegendCell("e", "Templates") + tabLegendCell("p", "Plan") + tabLegendCell("r", "Report") + "\n" +
		"  " + tabLegendCell("k", "Knowledge") + tabLegendCell("g", "Telegram") + tabLegendCell("b", "Kanban") + "\n\n" +
		accentStyle.Render("Palette") + subtleStyle.Render("  ") + key(":") + subtleStyle.Render("  ") +
		destructStyle.Render("red") + subtleStyle.Render(" = confirms, and says what it will do") + "\n" +
		"  " + verbGroupLegend(groupRun) + "\n" +
		"  " + verbGroupLegend(groupStage) + "\n" +
		"  " + verbGroupLegend(groupDanger) + "\n\n" +
		accentStyle.Render("Actions") + "\n" +
		"  " + key("i") + " inject context    " + key("/") + " search transcript · " + key("f") +
		" fold tools · " + key("T") + " fold thinking\n" +
		"  " + key("\\") + " collapse sidebar  " + key("↑↓") + " scroll / navigate\n\n" +
		accentStyle.Render("Global") + "\n" +
		"  " + key("q") + " quit   " + key("esc") + " close / cancel   " + key("?") + " this help"

	title := accentStyle.Render("◆ conductor") + subtleStyle.Render("  ·  keys")
	return lipgloss.NewStyle().
		Background(widgets.Mantle()).
		Border(lipgloss.RoundedBorder()).BorderForeground(widgets.Accent()).
		Padding(1, 3).
		Render(title + "\n\n" + body)
}
