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

// verbGroup buckets the palette into the questions the owner actually asks: what is the run doing,
// what is this stage doing, what will hurt — and, since U3.1, what does this Face look like.
// Grouping is presentation only: `Safe` stays the single safety contract and `Local` the single
// dispatch one, so a verb's group can be reordered without changing what it confirms or where it
// goes.
type verbGroup string

const (
	groupRun    verbGroup = "Run"
	groupStage  verbGroup = "Stage"
	groupDanger verbGroup = "Danger"
	// groupFace is the odd one out: its verbs change THIS Face and never reach the engine (see
	// Local). It sits last so the three operational groups keep the top of the list.
	groupFace verbGroup = "Face"
)

// groupOrder fixes the render order of the headers; allVerbs is kept sorted to match so the
// overlay can emit a header on each group change in one pass.
var groupOrder = []verbGroup{groupRun, groupStage, groupDanger, groupFace}

// verbKeyPad is the palette's key column. It must clear the longest verb ("pause-after-stage", 17)
// or that row's description hangs one column right of every other row's — pinned by
// TestVerbKeyColumnFitsEveryVerb so adding a longer verb fails loudly instead of skewing the pane.
const verbKeyPad = 18

// paletteVerb is one row of the palette.
type paletteVerb struct {
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
	// Local marks a verb the Face handles ITSELF — it never becomes a control-plane POST. Theme
	// switching is the only one today, and it has to be: it must work in --demo and while nothing
	// is listening, neither of which can be said of a control verb.
	Local bool
}

// allVerbs is a flat list deliberately ORDERED BY GROUP, not a map of groups: every existing
// index-based path (filteredVerbs, paletteSelected, paletteVerbIdx) keeps working unchanged, and
// the overlay just inserts a header whenever Group differs from the previous row.
var allVerbs = []paletteVerb{
	// Run — steering the loop. All reversible, none confirm.
	{Key: "pause", Desc: "Pause after current session ends", Safe: true, Group: groupRun},
	{Key: "resume", Desc: "Resume a paused run", Safe: true, Group: groupRun},
	{Key: "stop-after", Desc: "Stop after current session", Safe: true, Group: groupRun},
	{Key: "approve", Desc: "Approve and continue", Safe: true, Group: groupRun},
	{Key: "heartbeat", Desc: "Refresh REPORT.md snapshot now", Safe: true, Group: groupRun},
	{Key: "reload-plan", Desc: "Swap live plan at next session boundary", Safe: true, Group: groupRun},
	// Stage — moving around the plan.
	{Key: "goto", Desc: "Jump to a different stage (requires stage ID)", Safe: true, Group: groupStage},
	{Key: "retry-stage", Desc: "Reset attempt counter, retry stage", Group: groupStage,
		Consequence: "reset attempt counter + rerun stage"},
	{Key: "skip", Desc: "Skip current stage", Safe: true, Group: groupStage},
	{Key: "pause-after-stage", Desc: "Pause once stage completes", Safe: true, Group: groupStage},
	// Danger — destroys work or stops the run.
	{Key: "kill", Desc: "Kill current agent session", Group: groupDanger,
		Consequence: "kill this agent session; run continues"},
	{Key: "abort", Desc: "Abort run immediately", Group: groupDanger,
		Consequence: "kill session + stop conductor"},
	{Key: "rollback", Desc: "Git reset --hard to stage start", Group: groupDanger,
		Consequence: "git reset --hard to stage start; uncommitted work lost"},
	// Face — this Face, never the engine. KS2.4's switcher is the first non-theme member: it changes
	// which run this process is looking at, which is as local as a colour scheme and just as wrong
	// to send down a control plane. The theme rows are appended after it by init().
	// The description is kept inside the widest existing one ("Jump to a different stage (requires
	// stage ID)"): the palette box is sized to its longest row, and a wider box at the 80-column
	// floor is a box that starts clipping against the window edge.
	{Key: switchVerb, Desc: "Switch to another run on this machine", Safe: true,
		Group: groupFace, Local: true},
}

// themeVerbPrefix is the palette key's first word: `theme mocha`, `theme latte`, … Typing `:theme`
// filters to exactly the curated set, so the names never have to be memorised.
const themeVerbPrefix = "theme "

// The Face group is DERIVED from the theme registry rather than hand-listed beside it, so a scheme
// added in widgets gets its palette row (and its description) for free and the two cannot drift.
// TestPaletteOffersEveryTheme pins that.
func init() {
	for _, name := range widgets.ThemeNames() {
		t, ok := widgets.ThemeByName(name)
		if !ok {
			continue
		}
		allVerbs = append(allVerbs, paletteVerb{
			Key:   themeVerbPrefix + t.Name,
			Desc:  t.Description,
			Safe:  true,
			Group: groupFace,
			Local: true,
		})
	}
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
	// Every arm below moves the transcript's viewport (jumpToMatch, or a re-pin to the tail), so each
	// one goes through sizedTranscript: size and content FIRST, then move (adr/0006 §1). Updating
	// m.transcript directly would clamp the jump against whatever height the last resize left behind.
	switch key {
	case "esc":
		m.agent.searchActive = false
		m.transcript = m.sizedTranscript().Update(widgets.MsgSetSearch{Query: ""})
	case "enter":
		m.agent.searchActive = false
	case "backspace":
		q := m.transcript.SearchQuery
		if len(q) > 0 {
			m.transcript = m.sizedTranscript().Update(widgets.MsgSetSearch{Query: q[:len(q)-1]})
		}
	default:
		if ch, ok := typedChar(key); ok {
			m.transcript = m.sizedTranscript().Update(widgets.MsgSetSearch{Query: m.transcript.SearchQuery + ch})
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
			if verb.Key == switchVerb {
				// The one Local verb that changes a SCREEN rather than a setting, so it answers with
				// a model of its own rather than a command (KS2.4).
				return m.openSwitcher()
			}
			if verb.Local {
				return m, m.runLocalVerb(verb.Key)
			}
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

// runLocalVerb executes a Local palette verb — one the Face answers itself, with no engine involved.
// Theme switching is the only one: it has to work in --demo and with nothing listening.
func (m *Model) runLocalVerb(key string) tea.Cmd {
	name, ok := strings.CutPrefix(key, themeVerbPrefix)
	if !ok {
		return m.addToast("unknown command: "+key, widgets.ToastError)
	}
	if err := ApplyTheme(name); err != nil {
		return m.addToast(err.Error(), widgets.ToastError)
	}
	// The repaint has already happened by the line above; persisting is what makes it STICK. Report
	// that separately — a theme that switches now but silently reverts at the next launch is the
	// genuinely confusing outcome, and it is the one the user cannot see.
	if err := SaveConfig(Config{Theme: name}); err != nil {
		return m.addToast("theme "+name+" — not saved: "+err.Error(), widgets.ToastWarn)
	}
	return m.addToast("theme "+name, widgets.ToastSuccess)
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

	if m.agent.searchActive || m.transcript.SearchQuery != "" {
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
	if m.agent.searchActive {
		cursor = "▏"
	}
	matchInfo := subtleStyle.Render("no matches")
	if len(m.transcript.SearchMatches) > 0 {
		matchInfo = accentStyle.Render(fmt.Sprintf("%d/%d", m.transcript.SearchMatchIdx+1, len(m.transcript.SearchMatches)))
	}
	hint := "enter lock · esc clear"
	if !m.agent.searchActive {
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
		switch {
		case !v.Safe:
			mark, st = "⚠ ", destructStyle
		case v.Local && v.Key == themeVerbPrefix+widgets.CurrentTheme().Name:
			// Which scheme is live belongs where you switch it. The mark gutter is already 2 cols
			// wide for the ⚠, so reusing it costs no layout — pinned by TestPaletteMarksActiveTheme.
			mark, st = "● ", accentStyle
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

// tabLegendRows renders the help card's Tabs grid from tabKey and tabNames, four cells to a row.
//
// K6.3: this used to be three hand-typed rows, and the comment beside them admitted the hazard — a
// mnemonic changed in tabKey and not here makes the help lie. It is now DERIVED, the way
// verbGroupLegend is derived from allVerbs and themeLegend from the theme registry: the same two
// arrays the strip and the mnemonic loop read. A tab added, renamed or rebound reaches the help card
// with no second edit, so the class of drift is gone rather than guarded.
func tabLegendRows() string {
	const perRow = 4
	rows := make([]string, 0, (int(tabCount)+perRow-1)/perRow)
	for i := 0; i < int(tabCount); i += perRow {
		row := "  "
		for j := i; j < i+perRow && j < int(tabCount); j++ {
			row += tabLegendCell(tabKey[j], tabNames[j])
		}
		rows = append(rows, row)
	}
	return strings.Join(rows, "\n")
}

// foldedLegend renders the help card's folded row from foldedTabs. SF1.3 merged Console into Agent
// and Timeline into History and kept both mnemonics pointing at the surfaces they always named — so
// the help must say where they went, or the two keys look deleted. Derived for the same reason as
// the grid above; `←/→` is appended by hand because it is a nav key, not a folded surface.
func foldedLegend() string {
	out := subtleStyle.Render("folded  ")
	for _, f := range foldedTabs {
		out += key(f.Key) + subtleStyle.Render(" "+f.Help+" · ")
	}
	return out + key("←/→") + subtleStyle.Render(" History views")
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

// faceLegend renders the help card's Face row. Like verbGroupLegend it is DERIVED — from allVerbs
// and the theme registry, the same sources the palette's Face rows come from — so help and palette
// cannot drift. It collapses the `theme ` prefix those rows carry (four rows reading "theme X" are,
// in prose, one verb and a list of names) and paints the live scheme in accent.
//
// KS2.4: the group's non-theme verbs are listed first, from allVerbs, so the run switcher reaches
// the help card the way a rebound tab mnemonic does — without a second edit here.
func faceLegend() string {
	out := subtleStyle.Render(fmt.Sprintf("%-7s", string(groupFace)))
	for _, v := range allVerbs {
		if v.Group == groupFace && !strings.HasPrefix(v.Key, themeVerbPrefix) {
			out += key(v.Key) + subtleStyle.Render(" · ")
		}
	}

	names := widgets.ThemeNames()
	styled := make([]string, 0, len(names))
	for _, n := range names {
		st := textStyle
		if n == widgets.CurrentTheme().Name {
			st = accentStyle
		}
		styled = append(styled, st.Render(n))
	}
	return out + key("theme") + " " + strings.Join(styled, subtleStyle.Render(" · "))
}

func (m Model) renderHelpOverlay() string {
	// Row budget matters: this card must stay inside an 80x24 terminal, border included
	// (TestHelpOverlayFitsSmallestTerminal). The `tab` hint rides the Tabs heading and `:` is
	// documented by the Palette heading rather than each costing a row of its own.
	body := "" +
		accentStyle.Render("Tabs") + subtleStyle.Render("  (letter or number jumps · ") + key("tab") +
		subtleStyle.Render(" cycles)") + "\n" +
		tabLegendRows() + "\n" +
		"  " + foldedLegend() + "\n\n" +
		accentStyle.Render("Palette") + subtleStyle.Render("  ") + key(":") + subtleStyle.Render("  ") +
		destructStyle.Render("red") + subtleStyle.Render(" = confirms, and says what it will do") + "\n" +
		"  " + verbGroupLegend(groupRun) + "\n" +
		"  " + verbGroupLegend(groupStage) + "\n" +
		"  " + verbGroupLegend(groupDanger) + "\n" +
		"  " + faceLegend() + "\n\n" +
		accentStyle.Render("Actions") + "\n" +
		"  " + key("i") + " inject context    " + key("/") + " search transcript · " + key("f") +
		" fold tools · " + key("T") + " fold thinking\n" +
		// K6.2 / adr/0006 decision 2: the pane scroll set, spelled out once, because it is the same set
		// on every scrollable pane. `k` is absent and must stay absent — `k` opens Knowledge, and a vim
		// `k` here would document a key that the mnemonic loop swallows before any pane sees it.
		"  " + key("\\") + " collapse sidebar  " + key("↑↓/j d/u pgdn G/home") + " scroll · " + key("w") +
		" owner queue\n\n" +
		// KS2.8: the reader's open key rides the Global row rather than costing a row of its own —
		// the card must not outgrow the 80x24 floor — and its pager keys are the scroll set the
		// Actions row above already documents (the reader binds exactly that set and no other,
		// pinned by TestReaderBindsOnlyThePaneScrollSet).
		accentStyle.Render("Global") + "\n" +
		"  " + key("q") + " quit   " + key("esc") + " close / cancel   " + key("?") + " this help   " +
		key(readerOpenKey) + " read long text"

	title := accentStyle.Render("◆ conductor") + subtleStyle.Render("  ·  keys")
	return lipgloss.NewStyle().
		Background(widgets.Mantle()).
		Border(lipgloss.RoundedBorder()).BorderForeground(widgets.Accent()).
		Padding(1, 3).
		Render(title + "\n\n" + body)
}
