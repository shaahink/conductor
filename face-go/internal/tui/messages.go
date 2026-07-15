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

type MsgProcessKilled struct {
	Pid     int
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

// MsgKnowledgeUpdated carries the M7 ledger + bugs snapshot (polled together).
type MsgKnowledgeUpdated struct {
	Ledger *api.LedgerDto
	Bugs   *api.BugsDto
}

// MsgKnowledgeWritten is the result of filing a note/bug or resolving a bug from the Knowledge tab.
type MsgKnowledgeWritten struct {
	Toast string // success message for the toast
	Err   string
}

type MsgPromptPreview struct {
	Preview *api.PromptPreviewDto
	Err     string
}

// M6.3 plan authoring
type MsgPlanLoaded struct {
	Plan *api.PlanDto
	Err  string
}

type MsgPlanEdited struct {
	Result *api.PlanMutationResultDto
	Err    string
}

type MsgPlanImported struct {
	Result *api.PlanImportResultDto
	Err    string
}

// M8.2 Telegram guided setup
type MsgTelegramStatusUpdated struct {
	Status *api.TelegramStatusDto
	Err    string
}

type MsgTelegramTested struct {
	Result *api.TelegramTestResultDto
	Err    string
}

type MsgTelegramTokenSaved struct {
	Result *api.TelegramSetTokenResultDto
	Err    string
}

// MsgTelegramSettingsSaved wraps a /plan/edit (target "telegram") response with its own message
// type — reusing MsgPlanEdited would route the result into the Plan tab's state (m.planStatus)
// instead of the Telegram tab's, even though both endpoints are the same POST /plan/edit call.
type MsgTelegramSettingsSaved struct {
	Result *api.PlanMutationResultDto
	Err    string
}

func CmdTick() tea.Cmd {
	return tea.Tick(1*time.Second, func(t time.Time) tea.Msg {
		return MsgTick(t)
	})
}

// MsgSpinnerTick drives the top-bar liveness spinner. It is armed only while the engine reports
// an active agent session (see Update), so an idle dashboard costs nothing.
type MsgSpinnerTick struct{}

func cmdSpinnerTick() tea.Cmd {
	return tea.Tick(120*time.Millisecond, func(time.Time) tea.Msg {
		return MsgSpinnerTick{}
	})
}
