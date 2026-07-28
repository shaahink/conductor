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

// MsgTasksUpdated carries the task graph OR the reason it could not be fetched. The error is not
// decoration: without it an unreachable /tasks and a genuinely empty board are the same message to
// the Kanban pane, which is dogfood appendix item 5 — a board that read as silent emptiness while
// the sidebar showed a full plan.
type MsgTasksUpdated struct {
	Tasks *api.TasksDto
	Err   error
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

// MsgReportScores carries the Report tab's canned verifier-scores query (U2.2). Separate from
// MsgReportResult so the rendered report and the Dev SQL console never overwrite each other.
type MsgReportScores struct {
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

// MsgTaskWritten is the result of a Kanban move/add (G2.2) or a card-detail edit (P3).
type MsgTaskWritten struct {
	Verb   string // "move", "add", or "edit" — for the status line
	Result *api.TaskWriteResultDto
	Err    string
}

// MsgPromptBlocks carries a card's prompt composition for the detail panel (P3).
type MsgPromptBlocks struct {
	Blocks *api.PromptBlocksDto
	Err    string
}

// MsgTaskRefined is the advisor's PROPOSED edit for a card (P3) — shown for confirm, never applied
// by itself.
type MsgTaskRefined struct {
	Result *api.TaskRefineResultDto
	Err    string
}

// MsgTaskSplit is the advisor's PROPOSED breakdown of a card into children (W4.3) — shown for
// confirm; each child lands only through the ordinary add path, one at a time.
type MsgTaskSplit struct {
	Result *api.TaskSplitResultDto
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
