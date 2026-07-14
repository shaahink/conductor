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

type MsgPollResult struct {
	State       *api.StateDto
	Transcripts []api.TranscriptLineDto
}

type MsgConnectionChanged struct {
	EventsConnected     bool
	TranscriptConnected bool
}

type MsgFetchError struct {
	Err string
}

type MsgSidebarToggle struct{}
type MsgSidebarOpen struct{}
type MsgSidebarClose struct{}

type MsgModalOpen struct{ Kind ModalKind }
type MsgModalClose struct{}

type MsgTranscriptScrollUp struct{}
type MsgTranscriptScrollDown struct{}
type MsgTranscriptScrollHome struct{}
type MsgTranscriptScrollEnd struct{}

type MsgPaletteSelect struct{ Index int }
type MsgPaletteConfirm struct{}
type MsgPaletteFilter struct{ Query string }

type MsgControlSent struct {
	Verb    string
	Success bool
	Error   string
}

type MsgInjectSent struct {
	Success bool
	Error   string
}

func DoTick() tea.Msg {
	return MsgTick(time.Now())
}

func CmdTick() tea.Cmd {
	return tea.Tick(1*time.Second, func(t time.Time) tea.Msg {
		return MsgTick(t)
	})
}
