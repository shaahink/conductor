package tui

import (
	"fmt"
	"strings"
	"time"

	tea "charm.land/bubbletea/v2"

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
		// KS2.8: the reader is peeled at the same precedence as the command bar — BEFORE the esc
		// ladder in handleKey and before `q` can reach the quit arm. Without this, `q` inside a
		// 2000-line document kills the Face and `esc` drops the user two layers instead of one.
		// KS2.4: the run switcher is peeled at the same precedence and for the same reason — it is
		// the whole screen while it is up, and its `esc`/`q` mean "back to the run I am on", not
		// "quit the Face".
		if m.switcher.open {
			return m.handleSwitcherKey(key)
		}
		if m.reader.open {
			return m.handleReaderKey(key)
		}
		if m.agent.searchActive {
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
			// Learn where this run keeps its state while the engine is still able to tell us: when it
			// dies, that directory is where RUN-SUMMARY.md will be, and /state will not be answering
			// to point at it. Discovery (main.go) covers the cold-start case; this covers the one that
			// matters more, a Face that was watching when the run ended.
			//
			// The engine's answer OVERWRITES discovery's guess rather than deferring to it: discovery
			// can only find a directory literally named `.conductor`, while PlanConfig.StateDir is
			// configurable, so the walk-up is the fallback and /state is the fact.
			if msg.State.StateDir != "" {
				m.stateDir = msg.State.StateDir
			}
			// The transcript borrows its prefix vocabulary from the provider driving this run
			// (U3.3). "" (older engine) stays "" here → the neutral house set, never a guess.
			m.transcript.Provider = msg.State.Provider
			m.syncSidebar()
			m.recalcDimensions()
			if msg.State.AgentActive && !m.spinnerLive {
				m.spinnerLive = true
				cmd = cmdSpinnerTick()
			}
		}
		m.setConnected(true)
		m.data.Connection.LastError = nil
		return m, cmd

	case MsgTasksUpdated:
		// A failed poll must not blank a board that is already on screen — keep the last good cards
		// and let the pane say the feed went away. Only the error text is replaced every poll, so a
		// recovered fetch clears it.
		if msg.Err != nil {
			m.kanban.tasksErr = msg.Err.Error()
		} else if msg.Tasks != nil {
			m.kanban.tasksErr = ""
			m.kanban.tasksLoaded = true
			m.data.Tasks = msg.Tasks.Tasks
			m.syncSidebar()
		}

	case MsgSessionsUpdated:
		if msg.Sessions != nil {
			m.data.Sessions = msg.Sessions.Sessions
			if m.history.sessionSelected >= len(m.data.Sessions) {
				m.history.sessionSelected = 0
			}
		}

	case MsgEventReceived:
		m.data.Events = append(m.data.Events, msg.Event)
		if len(m.data.Events) > 400 {
			m.data.Events = m.data.Events[len(m.data.Events)-400:]
		}
		m.eventSeq = msg.Event.Seq
		next := waitForEvent(m.eventCh)
		// Keep the spine live while it's on screen: any spine event refreshes it. Scoped to the
		// VISIBLE view, not the whole History tab — sitting on the sessions list must not fetch the
		// timeline on every event, and switching into the spine refetches anyway (applyHistoryView).
		if m.tab == TabHistory && m.history.view == historyTimeline && !m.history.loading {
			m.history.loading = true
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
		wasConnected := m.data.Connection.Connected
		m.setConnected(false)
		if wasConnected || m.lastRun == nil {
			// The engine just went away (or we have never looked): its RUN-SUMMARY.md is the only
			// thing left that can say what happened, and it is written at completion — so the read
			// belongs HERE, on the transition, not once at startup.
			return m, m.cmdLoadLastRun()
		}

	// The two stream messages move their OWN indicator and nothing else. Deriving Connected from
	// them is what made a healthy poll with a dropped SSE read as "disconnected" (SF2.1).
	case MsgEventsConnChanged:
		m.data.Connection.EventsConnected = msg.Connected
		return m, waitForEventsConn(m.eventsConnCh)

	case MsgTxConnChanged:
		m.data.Connection.TranscriptConnected = msg.Connected
		return m, waitForTxConn(m.txConnCh)

	case MsgLastRunLoaded:
		m.lastRun = msg.Summary

	case MsgControlSent:
		kind, text := widgets.ToastSuccess, fmt.Sprintf("%s accepted", msg.Verb)
		if !msg.Success {
			reason := msg.Error
			if reason == "" {
				reason = "unknown reason"
			}
			kind, text = widgets.ToastError, fmt.Sprintf("%s rejected: %s", msg.Verb, reason)
		}
		if m.tab == TabPlan && strings.HasPrefix(m.plan.status, "sending") { // P5: the rollover (run) row
			if msg.Success {
				m.plan.status = "✓ " + msg.Verb + " sent (this run only — plan file untouched)"
			} else {
				m.plan.status = "✗ " + text
			}
		}
		return m, m.addToast(text, kind)

	case MsgInjectSent:
		kind, text := widgets.ToastSuccess, "Injection recorded (applied at the next session boundary)"
		if !msg.Success {
			kind, text = widgets.ToastError, "Injection failed: "+msg.Error
		}
		if m.kanban.detail { // P3 hand-off: reflect the result in the card detail's status line too
			if msg.Success {
				m.kanban.status = "✓ hand-off injected (next session boundary)"
			} else {
				m.kanban.status = "✗ " + msg.Error
			}
		}
		return m, m.addToast(text, kind)

	default:
		// K6.3: everything the shell does not own is offered to the tabs. Each tab handles the
		// messages whose effects only its own surface reads, in its own `tab_*.go` file — see
		// updateTabs. A message nobody claims falls through to the toast prune below, exactly as an
		// unknown message always did.
		if m2, cmd, handled := m.updateTabs(msg); handled {
			m2.toasts = widgets.PruneToasts(m2.toasts, 4*time.Second)
			return m2, cmd
		}
	}

	m.toasts = widgets.PruneToasts(m.toasts, 4*time.Second)
	return m, nil
}

// tabUpdaters is the dispatch K6.3 put where 80 `case` arms used to be. A message belongs to the
// surface that reads its effect, so the handler lives in that surface's file and this list is just
// the order they are offered in — order is irrelevant, because no two tabs claim the same message.
//
// A tab is absent from this list when it has no async messages of its own (Agent: its two bodies are
// fed by the streams, which the shell owns because it owns the channels).
var tabUpdaters = []func(Model, tea.Msg) (Model, tea.Cmd, bool){
	Model.updateHome,
	Model.updateHistory,
	Model.updateProcesses,
	Model.updateTemplates,
	Model.updatePlan,
	Model.updateReport,
	Model.updateKnowledge,
	Model.updateTelegram,
	Model.updateKanban,
}

// updateTabs offers a message to each tab model in turn and stops at the one that owns it.
func (m Model) updateTabs(msg tea.Msg) (Model, tea.Cmd, bool) {
	for _, update := range tabUpdaters {
		if m2, cmd, handled := update(m, msg); handled {
			return m2, cmd, true
		}
	}
	return m, nil, false
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
	// ctrl+c is the one key handled before anything else, INCLUDING a tab that captures all keys:
	// a global quit affordance must not be swallowable by a sub-state, and the double-tap is what
	// makes it safe to answer everywhere. A second tap while armed quits; the first arms + hints.
	if key == "ctrl+c" {
		if m.quitArmed {
			return m, tea.Quit
		}
		m.quitArmed = true
		return m, m.addToast("press ctrl+c again to quit", widgets.ToastInfo)
	}
	// Any other key disarms — the quit intent does not survive a keystroke of real work.
	m.quitArmed = false

	// A tab in an editing/interactive sub-state owns every key; its handler processes esc internally.
	if m.tabHandlesAllKeys() {
		return m.handleTabKey(key)
	}

	switch key {
	case "q":
		return m, tea.Quit
	case "esc":
		// esc backs out one layer. Search and command overlays are peeled earlier in Update (their
		// own handlers own esc); by here the outstanding layer is a non-Agent tab, or Agent's raw
		// stream — which IS a layer over the parsed transcript, and the transcript is the base the
		// dashboard rests on. On the parsed Agent view esc is a no-op — already home.
		// SF4.2: the owner queue is a layer over Home the same way the raw stream is a layer over the
		// transcript, so esc peels it FIRST — leaving Home rather than leaving for Agent. Checked
		// before the tab test below, or `w` would be the only way back off a full-pane list.
		if m.tab == TabHome && m.home.view != homeLanding {
			m.home.view = homeLanding
			return m, nil
		}
		if m.tab != TabAgent {
			return m.openTab(TabAgent)
		}
		m.agent.raw = false
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
			m.agent.searchActive = true
			m.transcript = m.sizedTranscript().Update(widgets.MsgSetSearch{Query: ""})
		}
		return m, nil
	case "w":
		// SF4.2's owner queue — a GLOBAL key, resolved here alongside the tab mnemonics and before any
		// pane handler, because that is the precedence a letter that opens a surface has to have to be
		// reachable from every pane. `w` was free: it is not a tab mnemonic, not a folded one, and no
		// pane handler claimed it (STYLE.md's rule for adding a letter — grep the pane handlers first).
		// A toggle once Home is up, like `c` on Agent: a full-pane list needs a way back that is not
		// "go somewhere else and come back".
		return m.openOwnerQueue()
	case "tab":
		return m.switchTab(1)
	case "shift+tab":
		return m.switchTab(-1)
	case "1", "2", "3", "4", "5", "6", "7", "8", "9":
		if t := int(key[0] - '1'); t < int(tabCount) {
			return m.openTab(MainTab(t))
		}
	case "0":
		// The 10th tab (index 9) has no 1–9 digit; "0" reaches it. Since SF1.3 took the Face to ten
		// tabs, that is the LAST one — the digit row now addresses every tab, and the caveat this
		// comment used to carry ("the tabs past the digits are mnemonic-only") is simply gone.
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

	// …and the folded tabs' mnemonics jump straight to what they always opened, one level in
	// (SF1.3). Global, exactly as they were when they were tab mnemonics, so no pane key changes
	// hands. See foldedTabKey for why these are alive and `d` is not.
	if t, folded := foldedTabKey[key]; folded {
		return m.openFolded(key, t)
	}

	return m.handleTabKey(key)
}

// openFolded opens the tab that absorbed a folded surface, showing that surface.
//
// `c` is a TOGGLE once Agent is up — Agent has two bodies and the raw one needs a way back that is
// not "go somewhere else and come back". `t`/`s` are not: History's two views are each other's way
// back, so each key is idempotent and pressing it twice is never a surprise.
func (m Model) openFolded(key string, t MainTab) (tea.Model, tea.Cmd) {
	switch t {
	case TabAgent:
		if m.tab == TabAgent {
			m.agent.raw = !m.agent.raw
		} else {
			m.agent.raw = true
		}
		if m.agent.raw {
			// Land on the live tail, as opening the Console tab used to. GotoBottom, not `= 0`: this
			// was the write site OUTSIDE tab_agent.go that made `consoleScroll` a field two files
			// could disagree about (adr/0006 decision 1).
			m.agent.rawVp = m.agentRawViewport()
			m.agent.rawVp.GotoBottom()
		}
		return m.openTab(TabAgent)
	case TabHistory:
		return m.openHistory(historyTimeline)
	}
	return m.openTab(t)
}

// openOwnerQueue opens Home's full owner-queue view, or closes it again if it is already up.
//
// It does NOT wait for a fetch: the queue is polled with everything else, so `w` renders whatever the
// last poll left — and if that is nothing yet, the pane says so rather than showing an empty list
// that reads as "nothing is owed".
func (m Model) openOwnerQueue() (tea.Model, tea.Cmd) {
	if m.tab == TabHome && m.home.view == homeOwnerQueue {
		m.home.view = homeLanding
		return m, nil
	}
	m.tab = TabHome
	m.home.view = homeOwnerQueue
	m.home.queueVp.GotoTop()
	return m, m.cmdFetchOwnerQueue()
}

// openHistory opens the History tab on a specific view.
func (m Model) openHistory(v historyView) (tea.Model, tea.Cmd) {
	m.tab = TabHistory
	return m.applyHistoryView(v)
}

// applyHistoryView selects a History view and resets what opening it means — assumes m.tab is already
// TabHistory. Switching INTO the spine refetches it: the live-refresh in Update only runs while the
// spine is the VISIBLE view, so arriving from the sessions list would otherwise show whatever the
// last fetch left behind.
func (m Model) applyHistoryView(v historyView) (tea.Model, tea.Cmd) {
	m.history.view = v
	if v == historyTimeline {
		m.history.selected, m.history.loading, m.history.err = 0, true, ""
		return m, m.cmdFetchTimeline()
	}
	m.history.sessionSelected = 0 // /sessions is newest-first; land on the current one
	return m, nil
}

func (m Model) switchTab(delta int) (tea.Model, tea.Cmd) {
	next := (int(m.tab) + delta + int(tabCount)) % int(tabCount)
	return m.openTab(MainTab(next))
}

// openTab switches the active pane and kicks off any data it needs.
func (m Model) openTab(t MainTab) (tea.Model, tea.Cmd) {
	m.tab = t
	switch t {
	case TabHome:
		// Opening Home lands on the LANDING, the way opening History lands on the sessions list: `h`
		// means "show me where I am", and a preserved owner-queue view would answer a question the
		// keypress did not ask. `w` is one key away and says which view it means.
		m.home.view = homeLanding
	case TabHistory:
		// Opening History lands on the sessions list, the way every other tab resets what it shows on
		// open. The spine is `t`, or `←/→` from here — both one keypress, and both say which view
		// they mean, which a preserved-across-tab-cycle view would not.
		return m.applyHistoryView(historySessions)
	case TabTemplates:
		m.tmpl.entries = templates.List(m.currentPlanDir())
		m.tmpl.selected, m.tmpl.mode, m.tmpl.previewOn = 0, PromptList, false
		m.tmpl.listVp.GotoTop() // the cursor went back to row 0; the window follows it
		return m, nil
	case TabPlan:
		if m.plan.doc == nil {
			return m, m.cmdFetchPlan()
		}
		return m, nil
	case TabReport:
		// U2.2: the report is rendered, not queried. Scroll resets to the top so the run header —
		// the answer to "how is it going" — is what opening the tab actually shows.
		m.report.vp.GotoTop()
		return m, m.cmdFetchScores()
	case TabKnowledge:
		m.knowledge.vp.GotoTop()
		m.knowledge.mode = knowledgeBrowse
		return m, m.cmdFetchKnowledge()
	case TabTelegram:
		m.telegram.fieldIdx, m.telegram.editing, m.telegram.statusLine = 0, false, ""
		return m, m.cmdFetchTelegramStatus()
	case TabKanban:
		m.kanban.adding, m.kanban.status = false, ""
		return m, m.cmdFetchTasks()
	}
	return m, nil
}

// tabHandlesAllKeys reports whether the active tab is in a sub-state that should capture every key.
func (m Model) tabHandlesAllKeys() bool {
	switch m.tab {
	case TabTemplates:
		return m.tmpl.mode == PromptEdit || m.tmpl.previewOn
	case TabPlan:
		return m.plan.drill || m.plan.editing || m.plan.adding || m.plan.deleting || m.plan.importResult != nil ||
			m.plan.tab == planTabImport || m.plan.tab == planTabPrompt
	case TabProcesses:
		return m.processes.killing
	case TabTelegram:
		return m.telegram.editing
	case TabKnowledge:
		return m.knowledge.mode != knowledgeBrowse
	case TabKanban:
		// The card detail (P3) owns t/c/a/h + its editors; the board itself only the add form.
		return m.kanban.adding || m.kanban.detail
	}
	return false
}

// handleTabKey routes navigation keys to the active pane's handler.
func (m Model) handleTabKey(key string) (tea.Model, tea.Cmd) {
	switch m.tab {
	case TabHome:
		// The landing owns no keys, by design (STYLE.md). Its owner-queue view does — it is a list
		// that can outgrow the pane, and a full-pane view with no scroll is a clipped one.
		if m.home.view == homeOwnerQueue {
			return m.handleOwnerQueueKey(key)
		}
	case TabAgent:
		return m.handleAgentKey(key)
	case TabHistory:
		return m.handleHistoryKey(key)
	case TabProcesses:
		return m.handleProcessesKey(key)
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
	// The reader floats over whatever tab is under it, so the wheel belongs to it while it is up —
	// same precedence as the key path. The switcher covers even the reader.
	if m.switcher.open {
		return m.handleSwitcherKey(key)
	}
	if m.reader.open {
		return m.handleReaderKey(key)
	}
	switch m.tab {
	case TabAgent:
		return m.handleAgentKey(key)
	case TabHistory:
		return m.handleHistoryKey(key)
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
