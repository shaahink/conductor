package tui

import (
	"fmt"
	"strings"
	"time"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/templates"
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

var quickQueries = []struct {
	Label string
	SQL   string
}{
	{"cost per stage", "SELECT s.stage_id, SUM(c.cost_usd) as cost_usd FROM costs c JOIN sessions s ON s.number = c.session_number AND s.run_id = c.run_id GROUP BY s.stage_id ORDER BY cost_usd DESC"},
	{"which gates fail most", "SELECT name, COUNT(*) as failures FROM gates WHERE passed = 0 GROUP BY name ORDER BY failures DESC"},
	{"recent sessions", "SELECT number, stage_id, kind, outcome FROM sessions ORDER BY number DESC LIMIT 20"},
	{"verifier scores", "SELECT session_number, score, verdict FROM scores ORDER BY session_number DESC LIMIT 20"},
}

const defaultReportSQL = "SELECT s.stage_id, SUM(c.cost_usd) as cost_usd FROM costs c JOIN sessions s ON s.number = c.session_number AND s.run_id = c.run_id GROUP BY s.stage_id"

// typedChar returns the literal character a key press should insert into a text field. Bubble Tea v2's
// Key.String() returns "space" for the spacebar, so a bare len==1 check silently eats spaces.
func typedChar(key string) (string, bool) {
	if key == "space" {
		return " ", true
	}
	if len(key) == 1 {
		return key, true
	}
	return "", false
}

func (m Model) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	switch msg := msg.(type) {

	case tea.WindowSizeMsg:
		m.width = msg.Width
		m.height = msg.Height
		m.recalcDimensions()
		return m, nil

	case tea.KeyPressMsg:
		key := msg.String()
		if m.cmd != CmdNone {
			return m.handleCmdKey(key)
		}
		if m.searchActive {
			return m.handleSearchKey(key)
		}
		return m.handleKey(key)

	case tea.MouseWheelMsg:
		return m.handleMouseWheel(msg)

	case MsgAnimTick:
		if m.advanceToastAnims() {
			return m, cmdAnimTick()
		}
		return m, nil

	case MsgTick:
		return m, tea.Batch(CmdTick(), m.doPoll())

	case MsgStateUpdated:
		if msg.State != nil {
			m.data.Plan = msg.State
			m.sidebar = m.sidebar.Update(widgets.MsgSetData{Stages: msg.State.Stages, Gates: msg.State.Gates})
			m.recalcDimensions()
		}
		m.data.Connection.Connected = true
		m.data.Connection.LastError = nil

	case MsgTasksUpdated:
		if msg.Tasks != nil {
			m.data.Tasks = msg.Tasks.Tasks
		}

	case MsgProcessesUpdated:
		if msg.Procs != nil {
			m.data.Processes = msg.Procs.Processes
		}

	case MsgSessionsUpdated:
		if msg.Sessions != nil {
			m.data.Sessions = msg.Sessions.Sessions
		}

	case MsgEventReceived:
		m.data.Events = append(m.data.Events, msg.Event)
		if len(m.data.Events) > 400 {
			m.data.Events = m.data.Events[len(m.data.Events)-400:]
		}
		m.eventSeq = msg.Event.Seq
		return m, waitForEvent(m.eventCh)

	case MsgTranscriptLine:
		m.data.Transcript = append(m.data.Transcript, msg.Line)
		if len(m.data.Transcript) > 4000 {
			m.data.Transcript = m.data.Transcript[len(m.data.Transcript)-4000:]
		}
		m.txSeq = msg.Line.Seq
		m.transcript = m.transcript.Update(widgets.MsgAppendLine{Line: msg.Line})
		return m, waitForTranscript(m.txCh)

	case MsgConsoleLine:
		m.data.RawConsole = append(m.data.RawConsole, msg.Line)
		if len(m.data.RawConsole) > 2000 {
			m.data.RawConsole = m.data.RawConsole[len(m.data.RawConsole)-2000:]
		}
		m.consoleSeq = msg.Line.Seq
		return m, waitForConsole(m.consoleCh)

	case MsgFetchError:
		m.data.Connection.LastError = &msg.Err
		m.data.Connection.Connected = false

	case MsgEventsConnChanged:
		m.data.Connection.EventsConnected = msg.Connected
		m.data.Connection.Connected = m.data.Connection.EventsConnected || m.data.Connection.TranscriptConnected
		return m, waitForEventsConn(m.eventsConnCh)

	case MsgTxConnChanged:
		m.data.Connection.TranscriptConnected = msg.Connected
		m.data.Connection.Connected = m.data.Connection.EventsConnected || m.data.Connection.TranscriptConnected
		return m, waitForTxConn(m.txConnCh)

	case MsgControlSent:
		kind, text := widgets.ToastSuccess, fmt.Sprintf("%s accepted", msg.Verb)
		if !msg.Success {
			reason := msg.Error
			if reason == "" {
				reason = "unknown reason"
			}
			kind, text = widgets.ToastError, fmt.Sprintf("%s rejected: %s", msg.Verb, reason)
		}
		return m, m.addToast(text, kind)

	case MsgInjectSent:
		kind, text := widgets.ToastSuccess, "Injection recorded (applied at the next session boundary)"
		if !msg.Success {
			kind, text = widgets.ToastError, "Injection failed: "+msg.Error
		}
		return m, m.addToast(text, kind)

	case MsgReportResult:
		m.data.ReportLoading = false
		if msg.Err != "" {
			errCopy := msg.Err
			m.data.ReportResult = &api.QueryResultDto{Error: &errCopy}
		} else {
			m.data.ReportResult = msg.Result
		}
		return m, nil

	case MsgTimelineUpdated:
		m.timelineLoading = false
		if msg.Err != "" {
			m.timelineErr = msg.Err
		} else if msg.Timeline != nil {
			m.timelineEntries = msg.Timeline.Entries
			m.timelineErr = ""
		}
		return m, nil

	case MsgPromptPreview:
		if msg.Err != "" {
			m.promptPreviewErr, m.promptPreview = msg.Err, nil
		} else {
			m.promptPreview, m.promptPreviewErr = msg.Preview, ""
		}
		return m, nil

	case MsgPlanLoaded:
		if msg.Err != "" {
			m.planStatus = "load failed: " + msg.Err
		} else {
			m.plan = msg.Plan
		}
		return m, nil

	case MsgPlanEdited:
		if msg.Err != "" {
			m.planStatus = "✗ " + msg.Err
			return m, nil
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "rejected"
			if msg.Result.Error != nil {
				reason = *msg.Result.Error
			}
			m.planStatus = "✗ " + reason
			return m, nil
		}
		m.planStatus = fmt.Sprintf("✓ saved — plan v%d", planVersionOf(msg.Result))
		m.planEditing = false
		return m, m.cmdFetchPlan()

	case MsgPlanImported:
		if msg.Err != "" {
			m.planImportErr, m.planImportResult = msg.Err, nil
			return m, nil
		}
		m.planImportErr = ""
		m.planImportResult = msg.Result
		if msg.Result != nil && !msg.Result.Ok && msg.Result.Error != nil {
			m.planImportErr, m.planImportResult = *msg.Result.Error, nil
			return m, nil
		}
		if msg.Result != nil && msg.Result.Applied {
			m.planStatus = fmt.Sprintf("✓ imported — plan v%d", msg.Result.PlanVersion)
			m.planImportResult = nil
			return m, m.cmdFetchPlan()
		}
		return m, nil
	}

	m.toasts = widgets.PruneToasts(m.toasts, 4*time.Second)
	return m, nil
}

// handleKey is the dashboard's top-level router when no command bar is open.
func (m Model) handleKey(key string) (tea.Model, tea.Cmd) {
	// A tab in an editing/interactive sub-state owns every key; its handler processes esc internally.
	if m.tabHandlesAllKeys() {
		return m.handleTabKey(key)
	}

	switch key {
	case "q", "ctrl+c":
		return m, tea.Quit
	case "esc":
		if m.tab != TabAgent {
			return m.openTab(TabAgent)
		}
		return m, nil
	case ":":
		m.cmd = CmdPalette
		m.paletteQuery, m.paletteSelected, m.paletteConfirming, m.paletteGotoActive = "", 0, false, false
		return m, nil
	case "i":
		m.cmd = CmdInject
		m.injectStageId, m.injectContent, m.injectField = m.currentStageId(), "", 1
		return m, nil
	case "?":
		m.cmd = CmdHelp
		return m, nil
	case "p":
		m.sidebarCollapsed = !m.sidebarCollapsed
		m.recalcDimensions()
		return m, nil
	case "/":
		if m.tab == TabAgent {
			m.searchActive = true
			m.transcript = m.transcript.Update(widgets.MsgSetSearch{Query: ""})
		}
		return m, nil
	case "tab":
		return m.switchTab(1)
	case "shift+tab":
		return m.switchTab(-1)
	case "1", "2", "3", "4", "5", "6", "7", "8":
		return m.openTab(MainTab(int(key[0] - '1')))
	}

	// Letter mnemonics jump straight to a tab.
	for i := 0; i < int(tabCount); i++ {
		if key == tabKey[i] {
			return m.openTab(MainTab(i))
		}
	}

	return m.handleTabKey(key)
}

func (m Model) switchTab(delta int) (tea.Model, tea.Cmd) {
	next := (int(m.tab) + delta + int(tabCount)) % int(tabCount)
	return m.openTab(MainTab(next))
}

// openTab switches the active pane and kicks off any data it needs.
func (m Model) openTab(t MainTab) (tea.Model, tea.Cmd) {
	m.tab = t
	switch t {
	case TabTimeline:
		m.timelineSelected, m.timelineLoading, m.timelineErr = 0, true, ""
		return m, m.cmdFetchTimeline()
	case TabTemplates:
		m.promptEntries = templates.List(m.currentPlanDir())
		m.promptSelected, m.promptMode, m.promptPreviewOn = 0, PromptList, false
		return m, nil
	case TabPlan:
		if m.plan == nil {
			return m, m.cmdFetchPlan()
		}
		return m, nil
	case TabReport:
		if m.reportSQL == "" {
			m.reportSQL = defaultReportSQL
		}
		m.reportFocusQuery = true
		return m, nil
	case TabConsole:
		m.consoleScroll = 0
		return m, nil
	}
	return m, nil
}

// tabHandlesAllKeys reports whether the active tab is in a sub-state that should capture every key.
func (m Model) tabHandlesAllKeys() bool {
	switch m.tab {
	case TabReport:
		return m.reportFocusQuery
	case TabTemplates:
		return m.promptMode == PromptEdit || m.promptPreviewOn
	case TabPlan:
		return m.planDrill || m.planEditing || m.planImportResult != nil || m.planTab == planTabImport
	}
	return false
}

// handleTabKey routes navigation keys to the active pane's handler.
func (m Model) handleTabKey(key string) (tea.Model, tea.Cmd) {
	switch m.tab {
	case TabAgent:
		return m.handleAgentKey(key)
	case TabSessions:
		return m.handleSessionsKey(key)
	case TabTimeline:
		return m.handleTimelineKey(key)
	case TabProcesses:
		return m.handleProcessesKey(key)
	case TabConsole:
		return m.handleConsoleKey(key)
	case TabTemplates:
		return m.handleTemplatesKey(key)
	case TabPlan:
		return m.handlePlanKey(key)
	case TabReport:
		return m.handleReportKey(key)
	}
	return m, nil
}

func (m Model) handleAgentKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k":
		m.transcript = m.transcript.Update(widgets.MsgScrollUp)
	case "down", "j":
		m.transcript = m.transcript.Update(widgets.MsgScrollDown)
	case "pgup":
		m.transcript = m.transcript.Update(widgets.MsgScrollPageUp)
	case "pgdown":
		m.transcript = m.transcript.Update(widgets.MsgScrollPageDown)
	case "home":
		m.transcript = m.transcript.Update(widgets.MsgScrollUp)
	case "end":
		m.transcript = m.transcript.Update(widgets.MsgScrollEnd)
	case "f":
		m.transcript = m.transcript.Update(widgets.MsgToggleFold)
	case "n":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.transcript.Update(widgets.MsgNextMatch)
		}
	case "N":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.transcript.Update(widgets.MsgPrevMatch)
		}
	}
	return m, nil
}

// --- command bar (palette / inject / help) ---------------------------------

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

// --- pane handlers ---------------------------------------------------------

func (m Model) handleSessionsKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k":
		if m.sessionSelected > 0 {
			m.sessionSelected--
		}
	case "down", "j":
		if m.sessionSelected < len(m.data.Sessions)-1 {
			m.sessionSelected++
		}
	}
	return m, nil
}

func (m Model) handleReportKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		if m.reportFocusQuery {
			m.reportFocusQuery = false
			return m, nil
		}
		return m.openTab(TabAgent)
	case "tab":
		m.reportFocusQuery = !m.reportFocusQuery
	case "up", "k":
		if !m.reportFocusQuery && m.reportQuickSelected > 0 {
			m.reportQuickSelected--
		}
	case "down", "j":
		if !m.reportFocusQuery && m.reportQuickSelected < len(quickQueries)-1 {
			m.reportQuickSelected++
		}
	case "enter":
		sql := m.reportSQL
		if !m.reportFocusQuery {
			sql = quickQueries[m.reportQuickSelected].SQL
			m.reportSQL = sql
		}
		m.data.ReportLoading = true
		return m, m.cmdQueryReport(sql)
	case "backspace":
		if m.reportFocusQuery && len(m.reportSQL) > 0 {
			m.reportSQL = m.reportSQL[:len(m.reportSQL)-1]
		}
	default:
		if ch, ok := typedChar(key); m.reportFocusQuery && ok {
			m.reportSQL += ch
		}
	}
	return m, nil
}

func (m Model) handleProcessesKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k":
		if m.processSelected > 0 {
			m.processSelected--
		}
	case "down", "j":
		if m.processSelected < len(m.data.Processes)-1 {
			m.processSelected++
		}
	}
	return m, nil
}

func (m Model) handleConsoleKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k":
		m.consoleScroll++
	case "down", "j":
		if m.consoleScroll > 0 {
			m.consoleScroll--
		}
	case "end":
		m.consoleScroll = 0
	}
	return m, nil
}

func (m Model) handleTimelineKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "r":
		m.timelineLoading, m.timelineErr = true, ""
		return m, m.cmdFetchTimeline()
	case "up", "k":
		if m.timelineSelected > 0 {
			m.timelineSelected--
		}
	case "down", "j":
		if m.timelineSelected < len(m.timelineEntries)-1 {
			m.timelineSelected++
		}
	}
	return m, nil
}

func (m Model) handleTemplatesKey(key string) (tea.Model, tea.Cmd) {
	if m.promptPreviewOn {
		if key == "esc" || key == "v" {
			m.promptPreviewOn = false
		}
		return m, nil
	}
	if m.promptMode == PromptEdit {
		switch key {
		case "esc":
			m.promptMode = PromptList
		case "ctrl+s":
			if m.promptSelected < len(m.promptEntries) {
				entry := m.promptEntries[m.promptSelected]
				if err := templates.Write(entry.Path, m.promptContent); err != nil {
					return m, m.addToast("Save failed: "+err.Error(), widgets.ToastError)
				}
				m.promptEntries[m.promptSelected].Exists = true
				return m, m.addToast("Saved "+entry.Label, widgets.ToastSuccess)
			}
		case "enter":
			m.promptContent += "\n"
		case "backspace":
			if len(m.promptContent) > 0 {
				m.promptContent = m.promptContent[:len(m.promptContent)-1]
			}
		default:
			if ch, ok := typedChar(key); ok {
				m.promptContent += ch
			}
		}
		return m, nil
	}
	switch key {
	case "v":
		m.promptPreviewOn, m.promptPreview, m.promptPreviewErr = true, nil, ""
		return m, m.cmdFetchPromptPreview(m.currentStageId(), "Deliver")
	case "up", "k":
		if m.promptSelected > 0 {
			m.promptSelected--
		}
	case "down", "j":
		if m.promptSelected < len(m.promptEntries)-1 {
			m.promptSelected++
		}
	case "enter":
		if m.promptSelected < len(m.promptEntries) {
			m.promptMode = PromptEdit
			m.promptContent = templates.Read(m.promptEntries[m.promptSelected].Path)
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

func (m Model) handleMouseWheel(msg tea.MouseWheelMsg) (tea.Model, tea.Cmd) {
	if m.tab != TabAgent {
		return m, nil
	}
	if msg.Button == tea.MouseWheelUp {
		m.transcript = m.transcript.Update(widgets.MsgScrollUp)
	} else if msg.Button == tea.MouseWheelDown {
		m.transcript = m.transcript.Update(widgets.MsgScrollDown)
	}
	return m, nil
}

func (m Model) currentStageId() string {
	if m.data.Plan != nil {
		return m.data.Plan.StageId
	}
	return ""
}

func (m Model) currentPlanDir() string {
	if m.data.Plan != nil && m.data.Plan.PlanDir != "" {
		return m.data.Plan.PlanDir
	}
	return "."
}

func (m *Model) recalcDimensions() {
	layout := ComputeLayout(m.width, m.height, m.sidebarCollapsed)
	m.transcript.Width = layout.Content.Width - 4
	m.transcript.Height = layout.Content.Height - 2
	if m.transcript.Width < 10 {
		m.transcript.Width = 10
	}
	if m.transcript.Height < 3 {
		m.transcript.Height = 3
	}
}
