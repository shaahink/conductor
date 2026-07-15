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
	// costs has no stage_id of its own (schema: costs.session_number → sessions.number)
	{"cost per stage", "SELECT s.stage_id, SUM(c.cost_usd) as cost_usd FROM costs c JOIN sessions s ON s.number = c.session_number AND s.run_id = c.run_id GROUP BY s.stage_id ORDER BY cost_usd DESC"},
	{"which gates fail most", "SELECT name, COUNT(*) as failures FROM gates WHERE passed = 0 GROUP BY name ORDER BY failures DESC"},
	{"recent sessions", "SELECT number, stage_id, kind, outcome FROM sessions ORDER BY number DESC LIMIT 20"},
	{"verifier scores", "SELECT session_number, score, verdict FROM scores ORDER BY session_number DESC LIMIT 20"},
}

const defaultReportSQL = "SELECT s.stage_id, SUM(c.cost_usd) as cost_usd FROM costs c JOIN sessions s ON s.number = c.session_number AND s.run_id = c.run_id GROUP BY s.stage_id"

// typedChar returns the literal character a key press should insert into a text field, and
// whether it represents one at all. Bubble Tea's Key.String() deliberately returns "space" (a
// keybinding name, useful for chord matching like "ctrl+space") rather than a literal " " for the
// spacebar — every plain len(key) == 1 check in this file would otherwise silently eat spaces.
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
		if m.activeModal != ModalNone {
			return m.handleModalKey(key)
		}
		if m.searchActive {
			return m.handleSearchKey(key)
		}
		return m.handleKey(key)

	case tea.MouseClickMsg:
		return m.handleMouseClick(msg)

	case tea.MouseWheelMsg:
		return m.handleMouseWheel(msg)

	case MsgAnimTick:
		if m.advanceToastAnims() {
			return m, cmdAnimTick()
		}
		return m, nil

	case MsgTick:
		if m.activeModal == ModalNone {
			return m, tea.Batch(CmdTick(), m.doPoll())
		}
		return m, CmdTick()

	case MsgStateUpdated:
		if msg.State != nil {
			m.data.Plan = msg.State
			m.sidebar = m.sidebar.Update(widgets.MsgSetData{
				Stages: msg.State.Stages,
				Gates:  msg.State.Gates,
			})
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
		kind := widgets.ToastSuccess
		text := fmt.Sprintf("%s accepted", msg.Verb)
		if !msg.Success {
			kind = widgets.ToastError
			reason := msg.Error
			if reason == "" {
				reason = "unknown reason"
			}
			text = fmt.Sprintf("%s rejected: %s", msg.Verb, reason)
		}
		animCmd := m.addToast(text, kind)
		return m, animCmd

	case MsgInjectSent:
		kind := widgets.ToastSuccess
		text := "Injection recorded (not yet auto-applied to a prompt)"
		if !msg.Success {
			kind = widgets.ToastError
			text = "Injection failed: " + msg.Error
		}
		animCmd := m.addToast(text, kind)
		return m, animCmd

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
			m.promptPreviewErr = msg.Err
			m.promptPreview = nil
		} else {
			m.promptPreview = msg.Preview
			m.promptPreviewErr = ""
		}
		return m, nil
	}

	m.toasts = widgets.PruneToasts(m.toasts, 4*time.Second)
	return m, nil
}

func (m Model) handleKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "q", "ctrl+c":
		return m, tea.Quit

	case ":":
		m.activeModal = ModalPalette
		m.paletteQuery = ""
		m.paletteSelected = 0
		m.paletteConfirming = false
		m.paletteGotoActive = false
		return m, nil

	case "p":
		m.sidebarOpen = !m.sidebarOpen
		m.recalcDimensions()
		return m, nil

	case "i":
		m.activeModal = ModalInject
		m.injectStageId = m.currentStageId()
		m.injectContent = ""
		m.injectField = 1
		return m, nil

	case "e":
		m.activeModal = ModalPrompt
		m.promptEntries = templates.List(m.currentPlanDir())
		m.promptSelected = 0
		m.promptMode = PromptList
		return m, nil

	case "h":
		m.activeModal = ModalSessions
		m.sessionSelected = 0
		return m, nil

	case "r":
		m.activeModal = ModalReport
		m.reportSQL = defaultReportSQL
		m.reportQuickSelected = 0
		m.reportFocusQuery = true
		return m, nil

	case "s":
		m.activeModal = ModalProcesses
		m.processSelected = 0
		return m, nil

	case "t":
		m.activeModal = ModalTimeline
		m.timelineSelected = 0
		m.timelineLoading = true
		m.timelineErr = ""
		return m, m.cmdFetchTimeline()

	case "?":
		m.activeModal = ModalHelp
		return m, nil

	case "up", "k":
		if m.sidebarOpen {
			m.sidebar = m.sidebar.Update(widgets.MsgSelectUp)
		} else {
			m.transcript = m.transcript.Update(widgets.MsgScrollUp)
		}
		return m, nil

	case "down", "j":
		if m.sidebarOpen {
			m.sidebar = m.sidebar.Update(widgets.MsgSelectDown)
		} else {
			m.transcript = m.transcript.Update(widgets.MsgScrollDown)
		}
		return m, nil

	case "pgup":
		m.transcript = m.transcript.Update(widgets.MsgScrollPageUp)
		return m, nil

	case "pgdown":
		m.transcript = m.transcript.Update(widgets.MsgScrollPageDown)
		return m, nil

	case "home":
		m.transcript = m.transcript.Update(widgets.MsgScrollUp)
		return m, nil

	case "end":
		m.transcript = m.transcript.Update(widgets.MsgScrollEnd)
		return m, nil

	case "f":
		m.transcript = m.transcript.Update(widgets.MsgToggleFold)
		return m, nil

	case "/":
		m.searchActive = true
		m.transcript = m.transcript.Update(widgets.MsgSetSearch{Query: ""})
		return m, nil

	case "n":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.transcript.Update(widgets.MsgNextMatch)
		}
		return m, nil

	case "N":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.transcript.Update(widgets.MsgPrevMatch)
		}
		return m, nil

	case "enter":
		if m.sidebarOpen {
			m.sidebar = m.sidebar.Update(widgets.MsgSelectExpand)
			return m, nil
		}
		return m, nil
	}

	return m, nil
}

func (m *Model) handleSearchKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.searchActive = false
		m.transcript = m.transcript.Update(widgets.MsgSetSearch{Query: ""})
		return m, nil
	case "enter":
		m.searchActive = false
		return m, nil
	case "backspace":
		q := m.transcript.SearchQuery
		if len(q) > 0 {
			m.transcript = m.transcript.Update(widgets.MsgSetSearch{Query: q[:len(q)-1]})
		}
		return m, nil
	default:
		if ch, ok := typedChar(key); ok {
			m.transcript = m.transcript.Update(widgets.MsgSetSearch{Query: m.transcript.SearchQuery + ch})
		}
		return m, nil
	}
}

func (m *Model) handleModalKey(key string) (tea.Model, tea.Cmd) {
	switch m.activeModal {
	case ModalPalette:
		return m.handlePaletteKey(key)
	case ModalInject:
		return m.handleInjectKey(key)
	case ModalPrompt:
		return m.handlePromptKey(key)
	case ModalSessions:
		return m.handleSessionsKey(key)
	case ModalReport:
		return m.handleReportKey(key)
	case ModalProcesses:
		return m.handleProcessesKey(key)
	case ModalTimeline:
		return m.handleTimelineKey(key)
	case ModalHelp:
		if key == "esc" || key == "?" {
			m.activeModal = ModalNone
		}
		return m, nil
	}
	if key == "esc" {
		m.activeModal = ModalNone
	}
	return m, nil
}

func (m *Model) handlePaletteKey(key string) (tea.Model, tea.Cmd) {
	if key == "esc" {
		if m.paletteGotoActive || m.paletteConfirming {
			m.paletteGotoActive = false
			m.paletteConfirming = false
			return m, nil
		}
		m.activeModal = ModalNone
		return m, nil
	}

	if m.paletteGotoActive {
		switch key {
		case "enter":
			stageId := strings.TrimSpace(m.paletteGotoInput)
			m.paletteGotoActive = false
			m.activeModal = ModalNone
			return m, m.cmdPostControl(api.ControlRequestDto{Command: "goto", StageId: stageId})
		case "backspace":
			if len(m.paletteGotoInput) > 0 {
				m.paletteGotoInput = m.paletteGotoInput[:len(m.paletteGotoInput)-1]
			}
			return m, nil
		default:
			if ch, ok := typedChar(key); ok {
				m.paletteGotoInput += ch
			}
			return m, nil
		}
	}

	if m.paletteConfirming {
		switch strings.ToLower(key) {
		case "y", "enter":
			verb := allVerbs[m.paletteVerbIdx].Key
			m.activeModal = ModalNone
			m.paletteConfirming = false
			return m, m.cmdPostControl(api.ControlRequestDto{Command: verb, Force: true, Confirmed: true})
		case "n":
			m.paletteConfirming = false
			return m, nil
		}
		return m, nil
	}

	switch key {
	case "up", "k":
		if m.paletteSelected > 0 {
			m.paletteSelected--
		}
		return m, nil
	case "down", "j":
		verbs := m.filteredVerbs()
		if m.paletteSelected < len(verbs)-1 {
			m.paletteSelected++
		}
		return m, nil
	case "enter":
		verbs := m.filteredVerbs()
		if m.paletteSelected < len(verbs) {
			origIdx := m.verbOriginalIndex(m.paletteSelected)
			if origIdx < 0 || origIdx >= len(allVerbs) {
				return m, nil
			}
			verb := allVerbs[origIdx]
			if verb.Key == "goto" {
				m.paletteGotoActive = true
				m.paletteGotoInput = m.currentStageId()
				return m, nil
			}
			if !verb.Safe {
				m.paletteConfirming = true
				m.paletteVerbIdx = origIdx
				return m, nil
			}
			m.activeModal = ModalNone
			return m, m.cmdPostControl(api.ControlRequestDto{Command: verb.Key})
		}
		return m, nil
	case "backspace":
		if len(m.paletteQuery) > 0 {
			m.paletteQuery = m.paletteQuery[:len(m.paletteQuery)-1]
			m.paletteSelected = 0
		}
		return m, nil
	default:
		if ch, ok := typedChar(key); ok {
			m.paletteQuery += ch
			m.paletteSelected = 0
		}
		return m, nil
	}
}

func (m *Model) handleInjectKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.activeModal = ModalNone
		return m, nil
	case "tab":
		m.injectField = 1 - m.injectField
		return m, nil
	case "backspace":
		if m.injectField == 0 && len(m.injectStageId) > 0 {
			m.injectStageId = m.injectStageId[:len(m.injectStageId)-1]
		} else if m.injectField == 1 && len(m.injectContent) > 0 {
			m.injectContent = m.injectContent[:len(m.injectContent)-1]
		}
		return m, nil
	case "enter":
		if m.injectField == 1 {
			m.injectContent += "\n"
		}
		return m, nil
	case "ctrl+s":
		if strings.TrimSpace(m.injectContent) == "" {
			return m, nil
		}
		req := api.InjectRequestDto{
			Content: m.injectContent,
			StageId: strings.TrimSpace(m.injectStageId),
		}
		m.activeModal = ModalNone
		return m, m.cmdPostInject(req)
	default:
		if ch, ok := typedChar(key); ok {
			if m.injectField == 0 {
				m.injectStageId += ch
			} else {
				m.injectContent += ch
			}
		}
		return m, nil
	}
}

func (m *Model) handlePromptKey(key string) (tea.Model, tea.Cmd) {
	if key == "esc" {
		if m.promptPreviewOn {
			m.promptPreviewOn = false
			return m, nil
		}
		if m.promptMode == PromptEdit {
			m.promptMode = PromptList
			return m, nil
		}
		m.activeModal = ModalNone
		return m, nil
	}

	if m.promptMode == PromptList {
		switch key {
		case "v":
			// M5.5: show the exact compiled prompt for the current stage beside the template list.
			m.promptPreviewOn = !m.promptPreviewOn
			if m.promptPreviewOn {
				m.promptPreview = nil
				m.promptPreviewErr = ""
				return m, m.cmdFetchPromptPreview(m.currentStageId(), "Deliver")
			}
			return m, nil
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
				entry := m.promptEntries[m.promptSelected]
				m.promptMode = PromptEdit
				m.promptContent = templates.Read(entry.Path)
			}
		}
		return m, nil
	}

	switch key {
	case "ctrl+s":
		if m.promptSelected < len(m.promptEntries) {
			entry := m.promptEntries[m.promptSelected]
			if err := templates.Write(entry.Path, m.promptContent); err != nil {
				return m, m.addToast("Save failed: "+err.Error(), widgets.ToastError)
			}
			m.promptEntries[m.promptSelected].Exists = true
			return m, m.addToast("Saved "+entry.Path, widgets.ToastSuccess)
		}
		return m, nil
	case "enter":
		m.promptContent += "\n"
		return m, nil
	case "backspace":
		if len(m.promptContent) > 0 {
			m.promptContent = m.promptContent[:len(m.promptContent)-1]
		}
		return m, nil
	default:
		if ch, ok := typedChar(key); ok {
			m.promptContent += ch
		}
		return m, nil
	}
}

func (m *Model) handleSessionsKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.activeModal = ModalNone
		return m, nil
	case "up", "k":
		if m.sessionSelected > 0 {
			m.sessionSelected--
		}
		return m, nil
	case "down", "j":
		if m.sessionSelected < len(m.data.Sessions)-1 {
			m.sessionSelected++
		}
		return m, nil
	}
	return m, nil
}

func (m *Model) handleReportKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.activeModal = ModalNone
		return m, nil
	case "tab":
		m.reportFocusQuery = !m.reportFocusQuery
		return m, nil
	case "up", "k":
		if !m.reportFocusQuery && m.reportQuickSelected > 0 {
			m.reportQuickSelected--
		}
		return m, nil
	case "down", "j":
		if !m.reportFocusQuery && m.reportQuickSelected < len(quickQueries)-1 {
			m.reportQuickSelected++
		}
		return m, nil
	case "enter":
		var sql string
		if m.reportFocusQuery {
			sql = m.reportSQL
		} else {
			sql = quickQueries[m.reportQuickSelected].SQL
			m.reportSQL = sql
		}
		m.data.ReportLoading = true
		return m, m.cmdQueryReport(sql)
	case "backspace":
		if m.reportFocusQuery && len(m.reportSQL) > 0 {
			m.reportSQL = m.reportSQL[:len(m.reportSQL)-1]
		}
		return m, nil
	default:
		if ch, ok := typedChar(key); m.reportFocusQuery && ok {
			m.reportSQL += ch
		}
		return m, nil
	}
}

func (m *Model) handleProcessesKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.activeModal = ModalNone
		return m, nil
	case "up", "k":
		if m.processSelected > 0 {
			m.processSelected--
		}
		return m, nil
	case "down", "j":
		if m.processSelected < len(m.data.Processes)-1 {
			m.processSelected++
		}
		return m, nil
	}
	return m, nil
}

func (m *Model) handleTimelineKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.activeModal = ModalNone
		return m, nil
	case "r":
		m.timelineLoading = true
		m.timelineErr = ""
		return m, m.cmdFetchTimeline()
	case "up", "k":
		if m.timelineSelected > 0 {
			m.timelineSelected--
		}
		return m, nil
	case "down", "j":
		if m.timelineSelected < len(m.timelineEntries)-1 {
			m.timelineSelected++
		}
		return m, nil
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
		if strings.Contains(strings.ToLower(v.Key), q) ||
			strings.Contains(strings.ToLower(v.Desc), q) {
			idxs = append(idxs, i)
		}
	}
	return idxs
}

func (m Model) verbOriginalIndex(filteredIdx int) int {
	idxs := m.filteredVerbs()
	if filteredIdx < len(idxs) {
		return idxs[filteredIdx]
	}
	return -1
}

func (m Model) handleMouseClick(msg tea.MouseClickMsg) (tea.Model, tea.Cmd) {
	if m.activeModal != ModalNone {
		return m, nil
	}

	x, y := msg.X, msg.Y
	layout := ComputeLayout(m.width, m.height, m.sidebarOpen)

	if m.sidebarOpen && x >= layout.Sidebar.X && x < layout.Sidebar.X+layout.Sidebar.Width &&
		y >= layout.Sidebar.Y && y < layout.Sidebar.Y+layout.Sidebar.Height {
		row := y - layout.Sidebar.Y - 1 // -1 for the "▶ PLAN" title line
		if row >= 0 {
			m.sidebar.Selected = row
		}
		return m, nil
	}

	return m, nil
}

func (m Model) handleMouseWheel(msg tea.MouseWheelMsg) (tea.Model, tea.Cmd) {
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
	m.transcript.Width = m.computeTranscriptWidth()
	m.transcript.Height = m.computeTranscriptHeight()
	m.sidebar.Width = m.computeSidebarWidth()
	m.sidebar.Height = m.computeMainHeight()
}

func (m Model) computeTranscriptWidth() int {
	layout := ComputeLayout(m.width, m.height, m.sidebarOpen)
	return layout.Transcr.Width
}

func (m Model) computeTranscriptHeight() int {
	layout := ComputeLayout(m.width, m.height, m.sidebarOpen)
	h := layout.Transcr.Height
	if !m.sidebarOpen && m.data.Plan != nil && len(m.data.Plan.Gates) > 0 {
		h-- // reserve one row for the inline gate bar (sidebar's own GATES section covers this when open)
	}
	if h < 2 {
		h = 2
	}
	return h
}

func (m Model) computeSidebarWidth() int {
	layout := ComputeLayout(m.width, m.height, m.sidebarOpen)
	w := layout.Sidebar.Width
	if w < 1 {
		w = 24
	}
	return w
}

func (m Model) computeMainHeight() int {
	layout := ComputeLayout(m.width, m.height, m.sidebarOpen)
	return layout.Main.Height
}
