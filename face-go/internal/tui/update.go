package tui

import (
	"fmt"
	"strings"
	"time"

	tea "charm.land/bubbletea/v2"

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

func (m Model) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	switch msg := msg.(type) {

	case tea.WindowSizeMsg:
		m.width = msg.Width
		m.height = msg.Height
		m.transcript.Width = m.computeTranscriptWidth()
		m.transcript.Height = m.computeTranscriptHeight()
		m.sidebar.Width = m.computeSidebarWidth()
		m.sidebar.Height = m.computeMainHeight()
		return m, nil

	case tea.KeyPressMsg:
		key := msg.String()
		if m.activeModal != ModalNone {
			return m.handleModalKey(key)
		}
		return m.handleKey(key)

	case tea.MouseClickMsg:
		return m.handleMouseClick(msg)

	case tea.MouseWheelMsg:
		return m.handleMouseWheel(msg)

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
		}
		m.data.Connection.Connected = true

	case MsgPollResult:
		if msg.State != nil {
			m.data.Plan = msg.State
			m.sidebar = m.sidebar.Update(widgets.MsgSetData{
				Stages: msg.State.Stages,
				Gates:  msg.State.Gates,
			})
		}
		for _, tx := range msg.Transcripts {
			m.data.Transcript = append(m.data.Transcript, tx)
			if len(m.data.Transcript) > 4000 {
				m.data.Transcript = m.data.Transcript[len(m.data.Transcript)-4000:]
			}
			m.transcript = m.transcript.Update(widgets.MsgAppendLine{Line: tx})
		}
		m.data.Connection.Connected = true

	case MsgProcessesUpdated:
		if msg.Procs != nil {
			m.data.Processes = msg.Procs.Processes
		}

	case MsgSessionsUpdated:
		if msg.Sessions != nil {
			m.data.Sessions = msg.Sessions.Sessions
		}

	case MsgTasksUpdated:
		if msg.Tasks != nil {
			m.data.Tasks = msg.Tasks.Tasks
		}

	case MsgEventReceived:
		m.data.Events = append(m.data.Events, msg.Event)
		if len(m.data.Events) > 400 {
			m.data.Events = m.data.Events[len(m.data.Events)-400:]
		}
		m.eventSeq = msg.Event.Seq

	case MsgTranscriptLine:
		m.data.Transcript = append(m.data.Transcript, msg.Line)
		if len(m.data.Transcript) > 4000 {
			m.data.Transcript = m.data.Transcript[len(m.data.Transcript)-4000:]
		}
		m.txSeq = msg.Line.Seq
		m.transcript = m.transcript.Update(widgets.MsgAppendLine{Line: msg.Line})

	case MsgFetchError:
		m.data.Connection.LastError = &msg.Err
		m.data.Connection.Connected = false
		m.toasts = append(m.toasts, widgets.NewToast("Connection error: "+msg.Err, widgets.ToastError))

	case MsgConnectionChanged:
		m.data.Connection.EventsConnected = msg.EventsConnected
		m.data.Connection.TranscriptConnected = msg.TranscriptConnected
		m.data.Connection.Connected = msg.EventsConnected || msg.TranscriptConnected

	case MsgSidebarToggle:
		m.sidebarOpen = !m.sidebarOpen
		m.recalcDimensions()
		return m, nil

	case MsgSidebarOpen:
		m.sidebarOpen = true
		m.recalcDimensions()
		return m, nil

	case MsgSidebarClose:
		m.sidebarOpen = false
		m.recalcDimensions()
		return m, nil

	case MsgControlSent:
		var kind widgets.ToastKind = widgets.ToastSuccess
		text := fmt.Sprintf("Control: %s — accepted", msg.Verb)
		if !msg.Success {
			kind = widgets.ToastError
			text = fmt.Sprintf("Control: %s — %s", msg.Verb, msg.Error)
		}
		m.toasts = append(m.toasts, widgets.NewToast(text, kind))
		return m, nil

	case MsgInjectSent:
		var kind widgets.ToastKind = widgets.ToastSuccess
		text := "Injection recorded"
		if !msg.Success {
			kind = widgets.ToastError
			text = "Injection failed: " + msg.Error
		}
		m.toasts = append(m.toasts, widgets.NewToast(text, kind))
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
		m.promptSelected = 0
		m.promptMode = PromptList
		return m, nil

	case "h":
		m.activeModal = ModalSessions
		m.sessionSelected = 0
		return m, nil

	case "r":
		m.activeModal = ModalReport
		m.reportSQL = ""
		m.reportQuickSelected = 0
		m.reportFocusQuery = false
		return m, nil

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

func (m *Model) handleModalKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.activeModal = ModalNone
		return m, nil
	}

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
	}
	return m, nil
}

func (m *Model) handlePaletteKey(key string) (tea.Model, tea.Cmd) {
	if m.paletteConfirming {
		switch strings.ToLower(key) {
		case "y":
			verb := allVerbs[m.paletteVerbIdx].Key
			m.activeModal = ModalNone
			m.toasts = append(m.toasts, widgets.NewToast(
				fmt.Sprintf("Sent: %s", verb), widgets.ToastInfo))
			return m, nil
		case "n", "esc":
			m.paletteConfirming = false
			return m, nil
		}
		return m, nil
	}

	switch key {
	case "up", "k":
		verbs := m.filteredVerbs()
		if m.paletteSelected > 0 {
			m.paletteSelected--
		}
		_ = verbs
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
			if origIdx >= 0 && origIdx < len(allVerbs) && !allVerbs[origIdx].Safe {
				m.paletteConfirming = true
				m.paletteVerbIdx = origIdx
				return m, nil
			}
			if origIdx >= 0 && origIdx < len(allVerbs) {
				m.activeModal = ModalNone
				m.toasts = append(m.toasts, widgets.NewToast(
					fmt.Sprintf("Sent: %s", allVerbs[origIdx].Key), widgets.ToastInfo))
			}
		}
		return m, nil
	case "backspace":
		if len(m.paletteQuery) > 0 {
			m.paletteQuery = m.paletteQuery[:len(m.paletteQuery)-1]
			m.paletteSelected = 0
		}
		return m, nil
	default:
		if len(key) == 1 {
			m.paletteQuery += key
			m.paletteSelected = 0
		}
		return m, nil
	}
}

func (m *Model) handleInjectKey(key string) (tea.Model, tea.Cmd) {
	switch key {
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
	case "ctrl+s":
		m.activeModal = ModalNone
		m.toasts = append(m.toasts, widgets.NewToast(
			"Injection recorded to run.db (consumed next session boundary)", widgets.ToastSuccess))
		return m, nil
	default:
		if len(key) == 1 {
			if m.injectField == 0 {
				m.injectStageId += key
			} else {
				m.injectContent += key
			}
		}
		return m, nil
	}
}

func (m *Model) handlePromptKey(key string) (tea.Model, tea.Cmd) {
	if m.promptMode == PromptList {
		switch key {
		case "up", "k":
			if m.promptSelected > 0 {
				m.promptSelected--
			}
			return m, nil
		case "down", "j":
			if m.promptSelected < len(m.promptTemplates)-1 {
				m.promptSelected++
			}
			return m, nil
		case "enter":
			if m.promptSelected < len(m.promptTemplates) {
				m.promptMode = PromptEdit
				m.promptContent = fmt.Sprintf("# %s\n\n[ edit this template content ]\n", m.promptTemplates[m.promptSelected])
			}
			return m, nil
		}
	} else {
		switch key {
		case "esc":
			m.promptMode = PromptList
			return m, nil
		case "ctrl+s":
			m.activeModal = ModalNone
			m.toasts = append(m.toasts, widgets.NewToast(
				"Template saved to planDir/personas/", widgets.ToastSuccess))
			return m, nil
		case "backspace":
			if len(m.promptContent) > 0 {
				m.promptContent = m.promptContent[:len(m.promptContent)-1]
			}
			return m, nil
		default:
			if len(key) == 1 {
				m.promptContent += key
			}
			return m, nil
		}
	}
	return m, nil
}

func (m *Model) handleSessionsKey(key string) (tea.Model, tea.Cmd) {
	switch key {
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
	case "tab":
		m.reportFocusQuery = !m.reportFocusQuery
		return m, nil
	case "up", "k":
		if !m.reportFocusQuery && m.reportQuickSelected > 0 {
			m.reportQuickSelected--
		}
		return m, nil
	case "down", "j":
		if !m.reportFocusQuery && m.reportQuickSelected < 3 {
			m.reportQuickSelected++
		}
		return m, nil
	case "enter":
		if m.reportFocusQuery {
			m.activeModal = ModalNone
			m.toasts = append(m.toasts, widgets.NewToast(
				"Query sent to run.db", widgets.ToastInfo))
		}
		return m, nil
	case "backspace":
		if len(m.reportSQL) > 0 {
			m.reportSQL = m.reportSQL[:len(m.reportSQL)-1]
		}
		return m, nil
	default:
		if m.reportFocusQuery && len(key) == 1 {
			m.reportSQL += key
		}
		return m, nil
	}
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
	x, y := msg.X, msg.Y
	layout := ComputeLayout(m.width, m.height, m.sidebarOpen)

	if y == layout.Footer.Y {
		return m, nil
	}
	if y == layout.Ticker.Y {
		return m, nil
	}

	if m.sidebarOpen && x >= 0 && x <= layout.Sidebar.Width {
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
