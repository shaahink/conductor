package api

import "time"

// --- DataSource interface ---

type DataSource interface {
	FetchState() (*StateDto, error)
	FetchTasks() (*TasksDto, error)

	// G2: Kanban writes — move a card / add a card. The server emits the very same events the
	// MCP task tools do, so the board and the agent drive one task graph.
	PostTaskUpdate(req TaskUpdateRequestDto) (*TaskWriteResultDto, error)
	PostTaskAdd(req TaskAddRequestDto) (*TaskWriteResultDto, error)

	// P3: the Kanban card detail — a task's prompt as labeled building blocks, the structured
	// title/context edit (the confirm step), and the advisor's proposed refinement (proposal only).
	FetchPromptBlocks(taskId string) (*PromptBlocksDto, error)
	PostTaskEdit(req TaskEditRequestDto) (*TaskWriteResultDto, error)
	PostTaskRefine(req TaskRefineRequestDto) (*TaskRefineResultDto, error)
	FetchProcesses() (*ProcessesDto, error)
	FetchSessions() (*SessionsDto, error)
	FetchTimeline() (*TimelineDto, error)
	FetchLedger() (*LedgerDto, error)
	FetchBugs() (*BugsDto, error)

	// Write-side knowledge: file a note/bug and resolve a bug from the Face (POST /note, /bug,
	// /bug/resolve) — the same run.db rows the CLI `note`/`bug` verbs write.
	PostNote(req NoteRequestDto) (*KnowledgeWriteResultDto, error)
	PostBug(req BugNewRequestDto) (*KnowledgeWriteResultDto, error)
	PostBugResolve(req BugResolveRequestDto) (*KnowledgeWriteResultDto, error)
	FetchPromptPreview(stageId, kind string) (*PromptPreviewDto, error)
	QueryReport(sql string) (*QueryResultDto, error)
	PostControl(cmd ControlRequestDto) (*ControlAcceptedDto, error)
	PostInject(req InjectRequestDto) (*InjectAcceptedDto, error)
	PostProcessKill(req ProcessKillRequestDto) (*ProcessKillResultDto, error)

	// M6.3 plan authoring
	FetchPlan() (*PlanDto, error)
	PostPlanEdit(req PlanEditRequestDto) (*PlanMutationResultDto, error)
	PostPlanImport(req PlanImportRequestDto) (*PlanImportResultDto, error)

	// M8.2 Telegram guided setup — chat ids/poll interval/two-way go through PostPlanEdit
	// (target "telegram"); the bot token never does, see PostTelegramToken.
	FetchTelegramStatus() (*TelegramStatusDto, error)
	PostTelegramTest() (*TelegramTestResultDto, error)
	PostTelegramToken(req TelegramSetTokenRequestDto) (*TelegramSetTokenResultDto, error)

	SubscribeEvents(onEvent func(ConductorEventDto), onConnected func(bool)) (stop func())
	SubscribeTranscript(onLine func(TranscriptLineDto), onConnected func(bool)) (stop func())
	SubscribeConsole(onLine func(ConsoleLineDto), onConnected func(bool)) (stop func())

	Close()
}

// ConsoleLineDto mirrors the C# record (GET /console/current): one raw agent-stdout line from the
// current session's log — the "native console", i.e. exactly what the CLI is printing.
type ConsoleLineDto struct {
	Seq  int64  `json:"seq"`
	Text string `json:"text"`
}

// --- Top-level DTOs ---

type StateDto struct {
	PlanName               string     `json:"planName"`
	Status                 string     `json:"status"`
	AttentionReason        *string    `json:"attentionReason"`
	StageId                string     `json:"stageId"`
	StageTitle             string     `json:"stageTitle"`
	Persona                *string    `json:"persona"`
	DoneCount              int        `json:"doneCount"`
	TotalCount             int        `json:"totalCount"`
	TotalCostUsd           float64    `json:"totalCostUsd"`
	OverheadCostUsd        float64    `json:"overheadCostUsd"`
	TokensInput            int64      `json:"tokensInput"`
	TokensOutput           int64      `json:"tokensOutput"`
	TokensReasoning        int64      `json:"tokensReasoning"`
	CurrentCheckpoint      string     `json:"currentCheckpoint"`
	CurrentCheckpointTitle string     `json:"currentCheckpointTitle"`
	GateSummary            string     `json:"gateSummary"`
	Stages                 []StageDto `json:"stages"`
	RunId                  string     `json:"runId"`
	Repo                   string     `json:"repo"`
	PlanDir                string     `json:"planDir"`
	SessionNumber          int        `json:"sessionNumber"`
	SessionKind            string     `json:"sessionKind"`
	Attempt                int        `json:"attempt"`
	MaxAttempts            int        `json:"maxAttempts"`
	SessionElapsedSec      float64    `json:"sessionElapsedSec"`
	AgentActive            bool       `json:"agentActive"`
	SessionCostUsd         float64    `json:"sessionCostUsd"`
	SessionTokensInput     int64      `json:"sessionTokensInput"`
	SessionTokensOutput    int64      `json:"sessionTokensOutput"`
	SessionTokensReasoning int64      `json:"sessionTokensReasoning"`
	Gates                  []GateDto  `json:"gates"`
	// P5 follow-up: the set-rollover this-run override. nil = no override (the plan's
	// limits.maxSessionTokens decides); 0 = rollover forced OFF this run; >0 = the cap this run.
	MaxSessionTokensThisRun *int64 `json:"maxSessionTokensThisRun"`
	// The model the current/last session's resolved agent runs (stage + assignment overrides
	// applied) — the "what model is working" answer. "" = unknown / older engine.
	Model string `json:"model"`
}

type StageDto struct {
	Id          string          `json:"id"`
	Title       string          `json:"title"`
	Done        int             `json:"done"`
	Total       int             `json:"total"`
	State       string          `json:"state"`
	Attempts    int             `json:"attempts"`
	LastOutcome *string         `json:"lastOutcome"`
	CostUsd     float64         `json:"costUsd"`
	ParentId    *string         `json:"parentId"`
	Depth       int             `json:"depth"`
	Checkpoints []CheckpointDto `json:"checkpoints"`
}

type CheckpointDto struct {
	Id     string `json:"id"`
	Title  string `json:"title"`
	Status string `json:"status"`
}

type GateDto struct {
	Name       string  `json:"name"`
	State      string  `json:"state"`
	ElapsedSec float64 `json:"elapsedSec"`
}

type TaskDto struct {
	TaskId       string `json:"taskId"`
	CheckpointId string `json:"checkpointId"`
	Title        string `json:"title"`
	Status       string `json:"status"`
	Source       string `json:"source"`
	Order        int    `json:"order"`
	Context      string `json:"context"` // P3: owner-editable per-task extra context
	// PF3: repo-relative paths this card declares it will touch — the data behind multi-item
	// claim conflicts. Empty = no declared claims.
	Paths []string `json:"paths"`
}

type TasksDto struct {
	Tasks []TaskDto `json:"tasks"`
}

// --- G2.1: task writes (mirror Core/Http/ControlPlaneDto.TaskWrite.cs) ---

type TaskUpdateRequestDto struct {
	TaskId string `json:"taskId"`
	Status string `json:"status"`
}

// Order 0 means "append after the checkpoint's last task" (the server computes it).
type TaskAddRequestDto struct {
	CheckpointId string `json:"checkpointId"`
	Title        string `json:"title"`
	Order        int    `json:"order"`
}

// TaskEditRequestDto (P3, POST /tasks/edit): edit a task's own data. nil = leave unchanged; an
// empty context clears it. This is also the confirm step of the advisor-refine flow.
type TaskEditRequestDto struct {
	TaskId  string  `json:"taskId"`
	Title   *string `json:"title"`
	Context *string `json:"context"`
	// PF3: nil = leave the declared paths unchanged (marshals as null); an empty non-nil slice
	// clears them — mirrors the C# null/empty contract, so no omitempty here.
	Paths []string `json:"paths"`
}

// TaskRefineRequestDto (P3, POST /tasks/refine): ask the plan's advisor to refine one task.
// The server only PROPOSES — nothing mutates until the owner posts /tasks/edit.
type TaskRefineRequestDto struct {
	TaskId      string `json:"taskId"`
	Instruction string `json:"instruction,omitempty"`
}

type TaskRefineResultDto struct {
	Ok          bool    `json:"ok"`
	Error       *string `json:"error"`
	TaskId      *string `json:"taskId"`
	Title       *string `json:"title"`
	Context     *string `json:"context"`
	Interpreter *string `json:"interpreter"`
}

// PromptBlockDto (P3, GET /prompt/blocks?task=): one labeled building block of a task's prompt.
// Editable marks the task-scoped blocks (title, extra context) the card detail lets the owner edit.
type PromptBlockDto struct {
	Kind     string `json:"kind"`
	Label    string `json:"label"`
	Content  string `json:"content"`
	Editable bool   `json:"editable"`
}

type PromptBlocksDto struct {
	Ok           bool             `json:"ok"`
	Error        *string          `json:"error"`
	TaskId       string           `json:"taskId"`
	CheckpointId string           `json:"checkpointId"`
	StageId      string           `json:"stageId"`
	Blocks       []PromptBlockDto `json:"blocks"`
}

// TaskWriteResultDto: Status echoes the task's actual post-fold status — an illegal transition is
// a recorded no-op, so render from what happened, not from what was asked.
type TaskWriteResultDto struct {
	Ok           bool    `json:"ok"`
	Error        *string `json:"error"`
	TaskId       *string `json:"taskId"`
	Status       *string `json:"status"`
	CheckpointId *string `json:"checkpointId"`
	Title        *string `json:"title"`
	Order        int     `json:"order"`
}

type ProcessDto struct {
	Pid            int     `json:"pid"`
	Purpose        string  `json:"purpose"`
	StageId        *string `json:"stageId"`
	SessionNumber  *int    `json:"sessionNumber"`
	StartedUtc     string  `json:"startedUtc"`
	ExitedUtc      *string `json:"exitedUtc"`
	ExitCode       *int    `json:"exitCode"`
	Alive          bool    `json:"alive"`
	LastOutputLine *string `json:"lastOutputLine"`
}

type ProcessesDto struct {
	Processes []ProcessDto `json:"processes"`
}

// ProcessKillRequestDto / ProcessKillResultDto: kill a supervised child process from the Procs tab
// (POST /processes/kill). Only a PID this run tracked and still alive can be killed — see ProcessKiller.cs.
type ProcessKillRequestDto struct {
	Pid int `json:"pid"`
}

type ProcessKillResultDto struct {
	Ok    bool    `json:"ok"`
	Error *string `json:"error"`
	Pid   int     `json:"pid"`
}

type SessionRowDto struct {
	Number        int     `json:"number"`
	StageId       string  `json:"stageId"`
	Kind          string  `json:"kind"`
	StartedUtc    string  `json:"startedUtc"`
	EndedUtc      *string `json:"endedUtc"`
	Outcome       *string `json:"outcome"`
	Attempt       int     `json:"attempt"`
	ResumeCount   int     `json:"resumeCount"`
	GateSummary   *string `json:"gateSummary"`
	ResultSummary *string `json:"resultSummary"`
	CommitCount   int     `json:"commitCount"`
}

type SessionsDto struct {
	Sessions []SessionRowDto `json:"sessions"`
}

type QueryResultDto struct {
	Columns   []string      `json:"columns"`
	Rows      []QueryRowDto `json:"rows"`
	Truncated bool          `json:"truncated"`
	Error     *string       `json:"error"`
}

type QueryRowDto struct {
	Values []string `json:"values"`
}

// TimelineEntryDto mirrors the C# record (GET /timeline): one folded event on the run's spine —
// sessions, gates, stalls, verdicts, cost over time.
type TimelineEntryDto struct {
	Utc           string   `json:"utc"`
	Kind          string   `json:"kind"`
	Description   string   `json:"description"`
	StageId       *string  `json:"stageId"`
	SessionNumber *int     `json:"sessionNumber"`
	CostUsd       *float64 `json:"costUsd"`
	Outcome       *string  `json:"outcome"`
}

type TimelineDto struct {
	Entries []TimelineEntryDto `json:"entries"`
}

// --- M7: knowledge that compounds (mirror Core/Http/ControlPlaneDto.Ledger.cs / .Bugs.cs) ---

// LedgerEntryDto is one knowledge-ledger row (a `conductor note`), surfaced by GET /ledger.
type LedgerEntryDto struct {
	Id            int64   `json:"id"`
	SessionNumber *int    `json:"sessionNumber"`
	StageId       *string `json:"stageId"`
	Kind          string  `json:"kind"`
	Content       string  `json:"content"`
	CreatedAt     string  `json:"createdAt"`
}

type LedgerDto struct {
	Entries []LedgerEntryDto `json:"entries"`
}

// BugDto is one tracked bug (a `conductor bug new`), surfaced by GET /bugs.
type BugDto struct {
	Id           int64   `json:"id"`
	Title        string  `json:"title"`
	Detail       *string `json:"detail"`
	Severity     string  `json:"severity"`
	Status       string  `json:"status"`
	StageId      *string `json:"stageId"`
	FoundSession *int    `json:"foundSession"`
	FixedSession *int    `json:"fixedSession"`
	CreatedAt    string  `json:"createdAt"`
	UpdatedAt    string  `json:"updatedAt"`
}

type BugsDto struct {
	Bugs []BugDto `json:"bugs"`
}

// Write-side knowledge DTOs (mirror Core/Http/ControlPlaneDto.KnowledgeWrite.cs).
type NoteRequestDto struct {
	Content string `json:"content"`
	StageId string `json:"stageId,omitempty"`
	Kind    string `json:"kind,omitempty"`
}

type BugNewRequestDto struct {
	Title    string `json:"title"`
	Detail   string `json:"detail,omitempty"`
	Severity string `json:"severity,omitempty"`
	StageId  string `json:"stageId,omitempty"`
}

type BugResolveRequestDto struct {
	Id     int64  `json:"id"`
	Status string `json:"status,omitempty"`
}

type KnowledgeWriteResultDto struct {
	Ok    bool    `json:"ok"`
	Id    *int64  `json:"id"`
	Error *string `json:"error"`
}

// PromptPreviewDto mirrors the C# record (GET /prompt/preview?stage=&kind=): the exact compiled
// prompt that would be sent for a given stage + session kind.
type PromptPreviewDto struct {
	Prompt string `json:"prompt"`
	Model  string `json:"model"`
	Kind   string `json:"kind"`
}

type ControlRequestDto struct {
	Command   string `json:"command"`
	Confirmed bool   `json:"confirmed"`
	Force     bool   `json:"force"`
	StageId   string `json:"stageId"`
	Value     string `json:"value"`
}

type ControlAcceptedDto struct {
	Accepted bool    `json:"accepted"`
	Reason   *string `json:"reason"`
}

type InjectRequestDto struct {
	Content string `json:"content"`
	StageId string `json:"stageId"`
}

type InjectAcceptedDto struct {
	Accepted    bool    `json:"accepted"`
	Reason      *string `json:"reason"`
	RunId       *string `json:"runId"`
	StageId     *string `json:"stageId"`
	RecordedUtc *string `json:"recordedUtc"`
}

type ConductorEventDto struct {
	Type      string         `json:"type"`
	Seq       int64          `json:"seq"`
	Ts        time.Time      `json:"ts"`
	RunId     string         `json:"runId"`
	SessionId *string        `json:"sessionId"`
	Extra     map[string]any `json:"-"`
}

type TranscriptLineDto struct {
	Seq       int64     `json:"seq"`
	Ts        time.Time `json:"ts"`
	SessionId string    `json:"sessionId"`
	Kind      string    `json:"kind"`
	Text      string    `json:"text"`
}

// --- M6.3: plan authoring DTOs (mirror Core/Http/ControlPlaneDto.Plan*.cs) ---

type PlanDto struct {
	Name            string         `json:"name"`
	PlanVersion     int            `json:"planVersion"`
	PlanFile        string         `json:"planFile"`
	GatePolicy      string         `json:"gatePolicy"`
	DefaultWorkflow string         `json:"defaultWorkflow"`
	DefaultModel    string         `json:"defaultModel"`
	Workflows       []string       `json:"workflows"`
	Stages          []PlanStageDto `json:"stages"`
	Gates           []PlanGateDto  `json:"gates"`
	Limits          PlanLimitsDto  `json:"limits"`
	Qa              *PlanQaDto     `json:"qa"` // P2: plan-wide QA dial; nil = classic workflow selection
}

type PlanStageDto struct {
	Id          string   `json:"id"`
	Title       string   `json:"title"`
	Sessions    int      `json:"sessions"`
	Kind        string   `json:"kind"`
	Model       *string  `json:"model"`
	Workflow    *string  `json:"workflow"`
	Persona     *string  `json:"persona"`
	Notes       *string  `json:"notes"`
	DependsOn   []string `json:"dependsOn"`
	QaMode      *string  `json:"qaMode"`      // P2: per-stage QA dial; nil = inherit the plan dial
	QaThreshold *int     `json:"qaThreshold"` // P2: per-stage verifier bar riding the stage dial
}

// PlanQaDto is the plan-wide QA dial (pipeline.qa) — edited via the "qa" target on /plan/edit,
// live-applied at the engine's next session boundary (P2).
type PlanQaDto struct {
	Mode                     string `json:"mode"`
	VerifierThreshold        *int   `json:"verifierThreshold"`
	AuditCoversPriorSessions bool   `json:"auditCoversPriorSessions"`
}

type PlanGateDto struct {
	Name           string `json:"name"`
	Command        string `json:"command"`
	Tier           string `json:"tier"`
	TimeoutMinutes int    `json:"timeoutMinutes"`
	Optional       bool   `json:"optional"`
}

type PlanLimitsDto struct {
	StallMinutes          int      `json:"stallMinutes"`
	SessionTimeoutMinutes int      `json:"sessionTimeoutMinutes"`
	MaxRunCostUsd         *float64 `json:"maxRunCostUsd"`
	MaxRunTokens          *int64   `json:"maxRunTokens"`
	VerifierThreshold     int      `json:"verifierThreshold"`
	MaxSessions           *int     `json:"maxSessions"`      // G3.3: live session cap; nil = no cap
	MaxSessionTokens      *int64   `json:"maxSessionTokens"` // P5: session-token rollover; nil = OFF (default)
	SoftBreakRatio        *float64 `json:"softBreakRatio"`   // P5: wind-down nudge point; nil = 0.8 default
}

type PlanEditDto struct {
	Target string  `json:"target"`
	Id     string  `json:"id"`
	Field  string  `json:"field"`
	Value  *string `json:"value"`
	Op     string  `json:"op,omitempty"` // set (default) | add | delete — see ControlPlaneDto.PlanEdit.cs
}

type PlanEditRequestDto struct {
	Edits []PlanEditDto `json:"edits"`
}

type PlanMutationResultDto struct {
	Ok          bool    `json:"ok"`
	Error       *string `json:"error"`
	PlanVersion int     `json:"planVersion"`
}

// PlanImportRequestDto: a structured doc parses deterministically; freeform prose routes through
// the plan's advisor model (G1.1) — same endpoint, the server decides.
type PlanImportRequestDto struct {
	Source string `json:"source"`
	Apply  bool   `json:"apply"`
}

type PlanFieldChangeDto struct {
	Field string  `json:"field"`
	Old   *string `json:"old"`
	New   *string `json:"new"`
}

type PlanStageChangeDto struct {
	Id     string               `json:"id"`
	Fields []PlanFieldChangeDto `json:"fields"`
}

type PlanDiffDto struct {
	AddedStages   []PlanStageDto       `json:"addedStages"`
	ChangedStages []PlanStageChangeDto `json:"changedStages"`
	AddedGates    []PlanGateDto        `json:"addedGates"`
	ChangedGates  []PlanStageChangeDto `json:"changedGates"`
}

func (d PlanDiffDto) IsEmpty() bool {
	return len(d.AddedStages) == 0 && len(d.ChangedStages) == 0 &&
		len(d.AddedGates) == 0 && len(d.ChangedGates) == 0
}

func (d PlanDiffDto) TotalChanges() int {
	return len(d.AddedStages) + len(d.ChangedStages) + len(d.AddedGates) + len(d.ChangedGates)
}

type PlanImportResultDto struct {
	Ok          bool        `json:"ok"`
	Error       *string     `json:"error"`
	Diff        PlanDiffDto `json:"diff"`
	Applied     bool        `json:"applied"`
	PlanVersion int         `json:"planVersion"`
	// What turned the source into a plan: "structured" (deterministic parse) or the advisor
	// model that interpreted the prose (G1.1).
	Interpreter *string `json:"interpreter"`
}

// --- M8.2: Telegram guided setup (mirror Core/Http/ControlPlaneDto.Telegram*.cs) ---

// TelegramStatusDto is GET /telegram/status: everything the guided-setup tab needs to show live
// connection health, not just "configured or not".
type TelegramStatusDto struct {
	Configured          bool     `json:"configured"`
	Started             bool     `json:"started"`
	HasToken            bool     `json:"hasToken"`
	AllowedChatIds      []string `json:"allowedChatIds"`
	PollIntervalSeconds int      `json:"pollIntervalSeconds"`
	EnableTwoWay        bool     `json:"enableTwoWay"`
	BotUsername         *string  `json:"botUsername"`
	LastError           *string  `json:"lastError"`
	LastPollUtc         *string  `json:"lastPollUtc"`
}

type TelegramTestResultDto struct {
	Ok          bool    `json:"ok"`
	BotUsername *string `json:"botUsername"`
	Error       *string `json:"error"`
}

type TelegramSetTokenRequestDto struct {
	Token string `json:"token"`
}

type TelegramSetTokenResultDto struct {
	Ok      bool    `json:"ok"`
	Message *string `json:"message"`
}

// --- Session-local state for connection management ---

type ConnectionMode string

const (
	ModeLive ConnectionMode = "live"
	ModeDemo ConnectionMode = "demo"
)

type ConnectionState struct {
	Mode                ConnectionMode
	URL                 string
	EventsConnected     bool
	TranscriptConnected bool
	Connected           bool
	LastError           *string
}

// --- AppState: the single source of truth for the TUI ---

type AppState struct {
	Connection   ConnectionState
	Plan         *StateDto
	Tasks        []TaskDto
	Processes    []ProcessDto
	Sessions     []SessionRowDto
	Events       []ConductorEventDto
	Transcript   []TranscriptLineDto
	RawConsole   []ConsoleLineDto
	Ledger       []LedgerEntryDto
	Bugs         []BugDto
	LastEventSeq int64
	LastTxSeq    int64

	ReportResult  *QueryResultDto
	ReportLoading bool
}
