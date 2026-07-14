package api

import "time"

// --- DataSource interface ---

type DataSource interface {
	FetchState() (*StateDto, error)
	FetchTasks() (*TasksDto, error)
	FetchProcesses() (*ProcessesDto, error)
	FetchSessions() (*SessionsDto, error)
	QueryReport(sql string) (*QueryResultDto, error)
	PostControl(cmd ControlRequestDto) (*ControlAcceptedDto, error)
	PostInject(req InjectRequestDto) (*InjectAcceptedDto, error)

	SubscribeEvents(onEvent func(ConductorEventDto), onConnected func(bool)) (stop func())
	SubscribeTranscript(onLine func(TranscriptLineDto), onConnected func(bool)) (stop func())

	Close()
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
}

type TasksDto struct {
	Tasks []TaskDto `json:"tasks"`
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
	LastEventSeq int64
	LastTxSeq    int64

	ReportResult  *QueryResultDto
	ReportLoading bool
}
