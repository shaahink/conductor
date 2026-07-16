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

	case MsgSpinnerTick:
		if m.data.Plan == nil || !m.data.Plan.AgentActive {
			m.spinnerLive = false
			return m, nil
		}
		m.spinnerFrame++
		return m, cmdSpinnerTick()

	case MsgTick:
		return m, tea.Batch(CmdTick(), m.doPoll())

	case MsgStateUpdated:
		var cmd tea.Cmd
		if msg.State != nil {
			m.data.Plan = msg.State
			m.syncSidebar()
			m.recalcDimensions()
			if msg.State.AgentActive && !m.spinnerLive {
				m.spinnerLive = true
				cmd = cmdSpinnerTick()
			}
		}
		m.data.Connection.Connected = true
		m.data.Connection.LastError = nil
		return m, cmd

	case MsgTasksUpdated:
		if msg.Tasks != nil {
			m.data.Tasks = msg.Tasks.Tasks
			m.syncSidebar()
		}

	case MsgProcessesUpdated:
		if msg.Procs != nil {
			m.data.Processes = msg.Procs.Processes
		}

	case MsgSessionsUpdated:
		if msg.Sessions != nil {
			m.data.Sessions = msg.Sessions.Sessions
			if m.sessionSelected >= len(m.data.Sessions) {
				m.sessionSelected = 0
			}
		}

	case MsgEventReceived:
		m.data.Events = append(m.data.Events, msg.Event)
		if len(m.data.Events) > 400 {
			m.data.Events = m.data.Events[len(m.data.Events)-400:]
		}
		m.eventSeq = msg.Event.Seq
		next := waitForEvent(m.eventCh)
		// Keep the Timeline live while it's on screen: any spine event refreshes it.
		if m.tab == TabTimeline && !m.timelineLoading {
			m.timelineLoading = true
			return m, tea.Batch(next, m.cmdFetchTimeline())
		}
		return m, next

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
		if m.tab == TabPlan && strings.HasPrefix(m.planStatus, "sending") { // P5: the rollover (run) row
			if msg.Success {
				m.planStatus = "✓ " + msg.Verb + " sent (this run only — plan file untouched)"
			} else {
				m.planStatus = "✗ " + text
			}
		}
		return m, m.addToast(text, kind)

	case MsgInjectSent:
		kind, text := widgets.ToastSuccess, "Injection recorded (applied at the next session boundary)"
		if !msg.Success {
			kind, text = widgets.ToastError, "Injection failed: "+msg.Error
		}
		if m.kanbanDetail { // P3 hand-off: reflect the result in the card detail's status line too
			if msg.Success {
				m.kanbanStatus = "✓ hand-off injected (next session boundary)"
			} else {
				m.kanbanStatus = "✗ " + msg.Error
			}
		}
		return m, m.addToast(text, kind)

	case MsgProcessKilled:
		if msg.Success {
			// Re-fetch so the row flips to exited immediately, and toast alongside it.
			return m, tea.Batch(m.addToast(fmt.Sprintf("killed pid %d", msg.Pid), widgets.ToastSuccess), m.cmdFetchProcesses())
		}
		reason := msg.Error
		if reason == "" {
			reason = "unknown reason"
		}
		return m, m.addToast(fmt.Sprintf("kill pid %d rejected: %s", msg.Pid, reason), widgets.ToastError)

	case MsgReportResult:
		m.data.ReportLoading = false
		if msg.Err != "" {
			errCopy := msg.Err
			m.data.ReportResult = &api.QueryResultDto{Error: &errCopy}
		} else {
			m.data.ReportResult = msg.Result
		}
		return m, nil

	case MsgKnowledgeUpdated:
		if msg.Ledger != nil {
			m.data.Ledger = msg.Ledger.Entries
		}
		if msg.Bugs != nil {
			m.data.Bugs = msg.Bugs.Bugs
		}
		return m, nil

	case MsgKnowledgeWritten:
		if msg.Err != "" {
			return m, m.addToast("Failed: "+msg.Err, widgets.ToastError)
		}
		// Re-poll so the new note/bug (or the resolved bug dropping off) shows immediately.
		return m, tea.Batch(m.addToast(msg.Toast, widgets.ToastSuccess), m.cmdFetchKnowledge())

	case MsgTimelineUpdated:
		m.timelineLoading = false
		if msg.Err != "" {
			m.timelineErr = msg.Err
		} else if msg.Timeline != nil {
			m.timelineEntries = msg.Timeline.Entries
			m.timelineErr = ""
			if m.timelineSelected >= len(m.timelineEntries) {
				m.timelineSelected = max(0, len(m.timelineEntries)-1)
			}
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
		m.planImportBusy = false
		if msg.Err != "" {
			m.planImportErr, m.planImportResult = msg.Err, nil
			m.planStatus = ""
			return m, nil
		}
		m.planImportErr = ""
		m.planImportResult = msg.Result
		if msg.Result != nil && !msg.Result.Ok && msg.Result.Error != nil {
			m.planImportErr, m.planImportResult = *msg.Result.Error, nil
			m.planStatus = ""
			return m, nil
		}
		if msg.Result != nil && msg.Result.Applied {
			m.planStatus = fmt.Sprintf("✓ imported — plan v%d", msg.Result.PlanVersion)
			m.planImportResult = nil
			return m, m.cmdFetchPlan()
		}
		m.planStatus = ""
		return m, nil

	case MsgTaskWritten:
		if msg.Err != "" {
			m.kanbanStatus = "✗ " + msg.Err
			return m, nil
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "rejected"
			if msg.Result.Error != nil {
				reason = *msg.Result.Error
			}
			m.kanbanStatus = "✗ " + reason
			return m, nil
		}
		m.kanbanStatus = "✓ " + msg.Verb
		if msg.Verb == "add" && msg.Result != nil && msg.Result.TaskId != nil {
			m.kanbanSelId = *msg.Result.TaskId // focus follows the new card
		}
		// Re-fetch so the board shows what the engine actually recorded. A detail edit (P3) also
		// recomposes the open card's blocks — the edited block must visibly change.
		if msg.Verb == "edit" && m.kanbanDetail && m.kanbanBlocks != nil {
			return m, tea.Batch(m.cmdFetchTasks(), m.cmdFetchPromptBlocks(m.kanbanBlocks.TaskId))
		}
		return m, m.cmdFetchTasks()

	case MsgPromptBlocks:
		if msg.Err != "" {
			m.kanbanBlocksErr = msg.Err
			return m, nil
		}
		if msg.Blocks != nil && !msg.Blocks.Ok {
			if msg.Blocks.Error != nil {
				m.kanbanBlocksErr = *msg.Blocks.Error
			} else {
				m.kanbanBlocksErr = "could not load the card"
			}
			return m, nil
		}
		m.kanbanBlocks, m.kanbanBlocksErr = msg.Blocks, ""
		return m, nil

	case MsgTaskRefined:
		m.kanbanRefining = false
		if msg.Err != "" {
			m.kanbanStatus = "✗ " + msg.Err
			return m, nil
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "refine rejected"
			if msg.Result.Error != nil {
				reason = *msg.Result.Error
			}
			m.kanbanStatus = "✗ " + reason
			return m, nil
		}
		m.kanbanProposal = msg.Result // proposal only — enter applies, esc discards
		m.kanbanStatus = ""
		return m, nil

	case MsgTelegramStatusUpdated:
		if msg.Err != "" {
			return m, nil // status is never load-bearing — same as knowledge/tasks/processes polls
		}
		m.telegramStatus = msg.Status
		return m, nil

	case MsgTelegramTested:
		if msg.Err != "" {
			m.telegramStatusLine = "✗ " + msg.Err
			return m, nil
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "test failed"
			if msg.Result.Error != nil {
				reason = *msg.Result.Error
			}
			m.telegramStatusLine = "✗ " + reason
			return m, m.cmdFetchTelegramStatus()
		}
		name := "bot"
		if msg.Result != nil && msg.Result.BotUsername != nil {
			name = "@" + *msg.Result.BotUsername
		}
		m.telegramStatusLine = "✓ sent — " + name + " is connected"
		return m, m.cmdFetchTelegramStatus()

	case MsgTelegramTokenSaved:
		if msg.Err != "" {
			m.telegramStatusLine = "✗ " + msg.Err
			return m, nil
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "rejected"
			if msg.Result.Message != nil {
				reason = *msg.Result.Message
			}
			m.telegramStatusLine = "✗ " + reason
			return m, nil
		}
		msgText := "saved"
		if msg.Result != nil && msg.Result.Message != nil {
			msgText = *msg.Result.Message
		}
		m.telegramStatusLine = "✓ " + msgText
		m.telegramEditing = false
		return m, m.cmdFetchTelegramStatus()

	case MsgTelegramSettingsSaved:
		if msg.Err != "" {
			m.telegramStatusLine = "✗ " + msg.Err
			return m, nil
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "rejected"
			if msg.Result.Error != nil {
				reason = *msg.Result.Error
			}
			m.telegramStatusLine = "✗ " + reason
			return m, nil
		}
		m.telegramStatusLine = "✓ saved"
		m.telegramEditing = false
		return m, m.cmdFetchTelegramStatus()
	}

	m.toasts = widgets.PruneToasts(m.toasts, 4*time.Second)
	return m, nil
}

// syncSidebar pushes the latest stages/gates/tasks into the always-on rail.
func (m *Model) syncSidebar() {
	var stages, gates any
	if m.data.Plan != nil {
		stages, gates = m.data.Plan.Stages, m.data.Plan.Gates
	}
	m.sidebar = m.sidebar.Update(widgets.MsgSetData{Stages: stages, Gates: gates, Tasks: m.data.Tasks})
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
	case "\\": // sidebar-collapse — moved off `p` so Plan can take its natural mnemonic
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
	case "1", "2", "3", "4", "5", "6", "7", "8", "9":
		if t := int(key[0] - '1'); t < int(tabCount) {
			return m.openTab(MainTab(t))
		}
	case "0":
		// The 10th tab (index 9) has no 1–9 digit; "0" reaches it. Tabs past that (Kanban, the
		// 11th) have no digit at all — mnemonic and tab-cycle only.
		if int(tabCount) > 9 {
			return m.openTab(MainTab(9))
		}
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
		if strings.TrimSpace(m.reportEditor.Value()) == "" {
			m.reportEditor = widgets.NewTextArea(defaultReportSQL, max(10, m.paneCols()), 1)
		}
		m.reportFocusQuery = true
		return m, nil
	case TabConsole:
		m.consoleScroll = 0
		return m, nil
	case TabSessions:
		m.sessionSelected = 0 // /sessions is newest-first; land on the current one
		return m, nil
	case TabKnowledge:
		m.knowledgeScroll, m.knowledgeMode = 0, knowledgeBrowse
		return m, m.cmdFetchKnowledge()
	case TabTelegram:
		m.telegramFieldIdx, m.telegramEditing, m.telegramStatusLine = 0, false, ""
		return m, m.cmdFetchTelegramStatus()
	case TabKanban:
		m.kanbanAdding, m.kanbanStatus = false, ""
		return m, m.cmdFetchTasks()
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
		return m.planDrill || m.planEditing || m.planAdding || m.planDeleting || m.planImportResult != nil ||
			m.planTab == planTabImport || m.planTab == planTabPrompt
	case TabProcesses:
		return m.processKilling
	case TabTelegram:
		return m.telegramEditing
	case TabKnowledge:
		return m.knowledgeMode != knowledgeBrowse
	case TabKanban:
		// The card detail (P3) owns t/c/a/h + its editors; the board itself only the add form.
		return m.kanbanAdding || m.kanbanDetail
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
	case TabKnowledge:
		return m.handleKnowledgeKey(key)
	case TabTelegram:
		return m.handleTelegramKey(key)
	case TabKanban:
		return m.handleKanbanKey(key)
	}
	return m, nil
}

// handleMouseWheel scrolls whatever the active tab shows — transcript, console, or a selection list.
func (m Model) handleMouseWheel(msg tea.MouseWheelMsg) (tea.Model, tea.Cmd) {
	up := msg.Button == tea.MouseWheelUp
	down := msg.Button == tea.MouseWheelDown
	if !up && !down {
		return m, nil
	}
	key := "down"
	if up {
		key = "up"
	}
	switch m.tab {
	case TabAgent:
		return m.handleAgentKey(key)
	case TabConsole:
		return m.handleConsoleKey(key)
	case TabSessions:
		return m.handleSessionsKey(key)
	case TabTimeline:
		return m.handleTimelineKey(key)
	case TabProcesses:
		return m.handleProcessesKey(key)
	case TabKnowledge:
		return m.handleKnowledgeKey(key)
	case TabKanban:
		return m.handleKanbanKey(key)
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
