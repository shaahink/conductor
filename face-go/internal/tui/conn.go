package tui

import (
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
	)
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
