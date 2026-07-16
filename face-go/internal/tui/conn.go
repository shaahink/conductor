package tui

import (
	"fmt"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// doPoll fetches the four snapshot endpoints (state, tasks, processes, sessions) independently,
// mirroring the Ink client's Promise.allSettled semantics: one failing must never block the others.
func (m Model) doPoll() tea.Cmd {
	return tea.Batch(
		m.cmdFetchState(),
		m.cmdFetchTasks(),
		m.cmdFetchProcesses(),
		m.cmdFetchSessions(),
		m.cmdFetchKnowledge(),
	)
}

// cmdFetchKnowledge polls the M7 ledger + bugs together (both are small snapshot endpoints). A failure
// on either is swallowed (nil msg), mirroring the other snapshot polls — knowledge is never load-bearing.
func (m Model) cmdFetchKnowledge() tea.Cmd {
	source := m.source
	return func() tea.Msg {
		ledger, lerr := source.FetchLedger()
		bugs, berr := source.FetchBugs()
		if lerr != nil && berr != nil {
			return nil
		}
		return MsgKnowledgeUpdated{Ledger: ledger, Bugs: bugs}
	}
}

func (m Model) cmdPostNote(req api.NoteRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostNote(req)
		return knowledgeWriteMsg("Note filed", res, err)
	}
}

func (m Model) cmdPostBug(req api.BugNewRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostBug(req)
		return knowledgeWriteMsg("Bug filed", res, err)
	}
}

func (m Model) cmdPostBugResolve(req api.BugResolveRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostBugResolve(req)
		return knowledgeWriteMsg(fmt.Sprintf("Bug #%d resolved", req.Id), res, err)
	}
}

func knowledgeWriteMsg(okToast string, res *api.KnowledgeWriteResultDto, err error) tea.Msg {
	if err != nil {
		return MsgKnowledgeWritten{Err: err.Error()}
	}
	if res != nil && !res.Ok {
		reason := "rejected"
		if res.Error != nil {
			reason = *res.Error
		}
		return MsgKnowledgeWritten{Err: reason}
	}
	return MsgKnowledgeWritten{Toast: okToast}
}

func (m Model) cmdFetchState() tea.Cmd {
	source := m.source
	return func() tea.Msg {
		state, err := source.FetchState()
		if err != nil {
			return MsgFetchError{Err: err.Error()}
		}
		return MsgStateUpdated{State: state}
	}
}

func (m Model) cmdFetchTasks() tea.Cmd {
	source := m.source
	return func() tea.Msg {
		tasks, err := source.FetchTasks()
		if err != nil {
			return nil
		}
		return MsgTasksUpdated{Tasks: tasks}
	}
}

func (m Model) cmdFetchProcesses() tea.Cmd {
	source := m.source
	return func() tea.Msg {
		procs, err := source.FetchProcesses()
		if err != nil {
			return nil
		}
		return MsgProcessesUpdated{Procs: procs}
	}
}

func (m Model) cmdFetchSessions() tea.Cmd {
	source := m.source
	return func() tea.Msg {
		sessions, err := source.FetchSessions()
		if err != nil {
			return nil
		}
		return MsgSessionsUpdated{Sessions: sessions}
	}
}

func (m Model) cmdPostControl(cmd api.ControlRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostControl(cmd)
		if err != nil {
			return MsgControlSent{Verb: cmd.Command, Success: false, Error: err.Error()}
		}
		reason := ""
		if res.Reason != nil {
			reason = *res.Reason
		}
		return MsgControlSent{Verb: cmd.Command, Success: res.Accepted, Error: reason}
	}
}

func (m Model) cmdPostProcessKill(pid int) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostProcessKill(api.ProcessKillRequestDto{Pid: pid})
		if err != nil {
			return MsgProcessKilled{Pid: pid, Success: false, Error: err.Error()}
		}
		reason := ""
		if res.Error != nil {
			reason = *res.Error
		}
		return MsgProcessKilled{Pid: pid, Success: res.Ok, Error: reason}
	}
}

func (m Model) cmdPostInject(req api.InjectRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostInject(req)
		if err != nil {
			return MsgInjectSent{Success: false, Error: err.Error()}
		}
		reason := ""
		if res.Reason != nil {
			reason = *res.Reason
		}
		return MsgInjectSent{Success: res.Accepted, Error: reason}
	}
}

func (m Model) cmdFetchTimeline() tea.Cmd {
	source := m.source
	return func() tea.Msg {
		timeline, err := source.FetchTimeline()
		if err != nil {
			return MsgTimelineUpdated{Err: err.Error()}
		}
		return MsgTimelineUpdated{Timeline: timeline}
	}
}

func (m Model) cmdFetchPromptPreview(stageId, kind string) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		preview, err := source.FetchPromptPreview(stageId, kind)
		if err != nil {
			return MsgPromptPreview{Err: err.Error()}
		}
		return MsgPromptPreview{Preview: preview}
	}
}

func (m Model) cmdFetchPlan() tea.Cmd {
	source := m.source
	return func() tea.Msg {
		plan, err := source.FetchPlan()
		if err != nil {
			return MsgPlanLoaded{Err: err.Error()}
		}
		return MsgPlanLoaded{Plan: plan}
	}
}

func (m Model) cmdPostPlanEdit(req api.PlanEditRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostPlanEdit(req)
		if err != nil {
			return MsgPlanEdited{Err: err.Error()}
		}
		return MsgPlanEdited{Result: res}
	}
}

func (m Model) cmdPostTaskUpdate(req api.TaskUpdateRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostTaskUpdate(req)
		if err != nil {
			return MsgTaskWritten{Verb: "move", Err: err.Error()}
		}
		return MsgTaskWritten{Verb: "move", Result: res}
	}
}

func (m Model) cmdPostTaskAdd(req api.TaskAddRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostTaskAdd(req)
		if err != nil {
			return MsgTaskWritten{Verb: "add", Err: err.Error()}
		}
		return MsgTaskWritten{Verb: "add", Result: res}
	}
}

func (m Model) cmdFetchPromptBlocks(taskId string) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		blocks, err := source.FetchPromptBlocks(taskId)
		if err != nil {
			return MsgPromptBlocks{Err: err.Error()}
		}
		return MsgPromptBlocks{Blocks: blocks}
	}
}

func (m Model) cmdPostTaskEdit(req api.TaskEditRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostTaskEdit(req)
		if err != nil {
			return MsgTaskWritten{Verb: "edit", Err: err.Error()}
		}
		return MsgTaskWritten{Verb: "edit", Result: res}
	}
}

func (m Model) cmdPostTaskRefine(req api.TaskRefineRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostTaskRefine(req)
		if err != nil {
			return MsgTaskRefined{Err: err.Error()}
		}
		return MsgTaskRefined{Result: res}
	}
}

func (m Model) cmdPostPlanImport(req api.PlanImportRequestDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostPlanImport(req)
		if err != nil {
			return MsgPlanImported{Err: err.Error()}
		}
		return MsgPlanImported{Result: res}
	}
}

func (m Model) cmdFetchTelegramStatus() tea.Cmd {
	source := m.source
	return func() tea.Msg {
		status, err := source.FetchTelegramStatus()
		if err != nil {
			return MsgTelegramStatusUpdated{Err: err.Error()}
		}
		return MsgTelegramStatusUpdated{Status: status}
	}
}

func (m Model) cmdPostTelegramTest() tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostTelegramTest()
		if err != nil {
			return MsgTelegramTested{Err: err.Error()}
		}
		return MsgTelegramTested{Result: res}
	}
}

func (m Model) cmdPostTelegramToken(token string) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostTelegramToken(api.TelegramSetTokenRequestDto{Token: token})
		if err != nil {
			return MsgTelegramTokenSaved{Err: err.Error()}
		}
		return MsgTelegramTokenSaved{Result: res}
	}
}

// cmdPostTelegramSettingsEdit posts a single non-secret Telegram field (chat ids/poll
// interval/two-way) through the same /plan/edit endpoint the Plan tab uses, but wraps the result
// in MsgTelegramSettingsSaved so it updates the Telegram tab's state, not the Plan tab's.
func (m Model) cmdPostTelegramSettingsEdit(edit api.PlanEditDto) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		res, err := source.PostPlanEdit(api.PlanEditRequestDto{Edits: []api.PlanEditDto{edit}})
		if err != nil {
			return MsgTelegramSettingsSaved{Err: err.Error()}
		}
		return MsgTelegramSettingsSaved{Result: res}
	}
}

func (m Model) cmdQueryReport(sql string) tea.Cmd {
	source := m.source
	return func() tea.Msg {
		result, err := source.QueryReport(sql)
		if err != nil {
			return MsgReportResult{Err: err.Error()}
		}
		return MsgReportResult{Result: result}
	}
}

// subscribeStreams starts the two persistent SSE subscriptions (events + transcript) exactly once
// per Model (called from Init). Both the live and demo DataSource implementations satisfy the same
// interface, so this wiring is identical in either mode — demo mode gets the same replay-then-stream
// behavior as a real engine connection instead of the old per-tick synthetic-line hack.
func (m Model) subscribeStreams() {
	m.source.SubscribeEvents(
		func(e api.ConductorEventDto) { m.eventCh <- e },
		func(connected bool) { m.eventsConnCh <- connected },
	)
	m.source.SubscribeTranscript(
		func(l api.TranscriptLineDto) { m.txCh <- l },
		func(connected bool) { m.txConnCh <- connected },
	)
	m.source.SubscribeConsole(
		func(l api.ConsoleLineDto) { m.consoleCh <- l },
		func(bool) {},
	)
}

func waitForEvent(ch chan api.ConductorEventDto) tea.Cmd {
	return func() tea.Msg {
		e, ok := <-ch
		if !ok {
			return nil
		}
		return MsgEventReceived{Event: e}
	}
}

func waitForTranscript(ch chan api.TranscriptLineDto) tea.Cmd {
	return func() tea.Msg {
		l, ok := <-ch
		if !ok {
			return nil
		}
		return MsgTranscriptLine{Line: l}
	}
}

func waitForConsole(ch chan api.ConsoleLineDto) tea.Cmd {
	return func() tea.Msg {
		l, ok := <-ch
		if !ok {
			return nil
		}
		return MsgConsoleLine{Line: l}
	}
}

func waitForEventsConn(ch chan bool) tea.Cmd {
	return func() tea.Msg {
		c, ok := <-ch
		if !ok {
			return nil
		}
		return MsgEventsConnChanged{Connected: c}
	}
}

func waitForTxConn(ch chan bool) tea.Cmd {
	return func() tea.Msg {
		c, ok := <-ch
		if !ok {
			return nil
		}
		return MsgTxConnChanged{Connected: c}
	}
}
