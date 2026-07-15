package tui

import (
	"time"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

type MsgTick time.Time

type MsgStateUpdated struct {
	State *api.StateDto
}

type MsgTasksUpdated struct {
	Tasks *api.TasksDto
}

type MsgProcessesUpdated struct {
	Procs *api.ProcessesDto
}

type MsgSessionsUpdated struct {
	Sessions *api.SessionsDto
}

type MsgEventReceived struct {
	Event api.ConductorEventDto
}

type MsgTranscriptLine struct {
	Line api.TranscriptLineDto
}

type MsgConsoleLine struct {
	Line api.ConsoleLineDto
}

type MsgEventsConnChanged struct {
	Connected bool
}

type MsgTxConnChanged struct {
	Connected bool
}

type MsgFetchError struct {
	Err string
}

type MsgControlSent struct {
	Verb    string
	Success bool
	Error   string
}

type MsgInjectSent struct {
	Success bool
	Error   string
}

type MsgReportResult struct {
	Result *api.QueryResultDto
	Err    string
}

type MsgTimelineUpdated struct {
	Timeline *api.TimelineDto
	Err      string
}

type MsgPromptPreview struct {
	Preview *api.PromptPreviewDto
	Err     string
}

func CmdTick() tea.Cmd {
	return tea.Tick(1*time.Second, func(t time.Time) tea.Msg {
		return MsgTick(t)
	})
}
