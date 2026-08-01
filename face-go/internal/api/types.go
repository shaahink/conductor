package api

import (
	"strings"
	"time"
)

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
	// W4.3: ask the advisor to break one card into children. Proposal only — each child is
	// confirmed through PostTaskAdd, exactly as a refine is confirmed through PostTaskEdit.
	PostTaskSplit(req TaskSplitRequestDto) (*TaskSplitResultDto, error)
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

	// SF1.1: the Report tab's verifier scores, typed. This is the endpoint that let a rendered report
	// stop depending on the SQL console — and SF1.2 then deleted that console, so there is no longer
	// any ad-hoc SQL on this interface at all. Every read here is a typed DTO.
	FetchScores() (*ScoresDto, error)

	// HasWriteToken reports whether this source carries the per-run write token every POST needs
	// (U2.3). Home surfaces it because "my writes are silently refused" has exactly one common
	// cause — attaching with --url but no token — and nothing in the Face said so.
	HasWriteToken() bool

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
	// U1.1: the rest of the workspace identity Home names. Engine-computed, never re-derived here —
	// StateDir is rooted at Repo, not PlanDir, so it cannot be guessed by joining PlanDir.
	// "" = older engine that doesn't serve them yet; Home degrades to "—" rather than inventing a path.
	Tracker  string `json:"tracker"`
	StateDir string `json:"stateDir"`
	// U3.3: the RESOLVED agent provider for the current stage — "claude" | "opencode" | "text".
	// Engine-resolved (AgentProviderFactory.ResolveName) rather than the raw plan field, which is
	// nullable and unset on most plans. "" = an older engine that does not serve it; callers must
	// treat that as unknown and fall back, never as "not claude".
	Provider string `json:"provider"`

	// ── SC2.3's budget block, read rather than re-derived (SF2.3). The engine has served all of
	// these since SC2.3 (ControlPlaneDto.cs) and the Face ignored every one of them, subtracting its
	// own numbers instead — which is precisely how "$224.21 / $125.00 · 0% headroom" got on screen.
	// The cap is measured against the WINDOW; an owner approval past a budget park restarts that
	// window while LifetimeCostUsd keeps counting the whole run, so the two can never be subtracted.
	//
	// How the in-flight session's cost is known. Closed vocabulary, defined by the engine's
	// LiveCostEstimator: "measured" | "streamed" | "estimated-from-run-rate" | "no-rate-yet" |
	// "none". "" = an older engine that does not serve the basis at all.
	SessionCostBasis string `json:"sessionCostBasis"`
	// Spend against the cap: the current budget window, in-flight session included.
	CostSpent float64 `json:"costSpent"`
	// limits.maxRunCostUsd, or nil when the plan sets no cost cap. A nil cap is NOT an infinite one:
	// "this plan set no ceiling" and "there is loads left" are different facts and must not render
	// the same. CostRemaining is nil for the same reason, and goes NEGATIVE when the window is over.
	CostCap       *float64 `json:"costCap"`
	CostRemaining *float64 `json:"costRemaining"`
	// Mean cost of the run's finished, priced sessions — the honest input to "how many more fit".
	MeanSessionCost      float64 `json:"meanSessionCost"`
	CheckpointsRemaining int     `json:"checkpointsRemaining"`
	// Window vs lifetime. Equal until an owner approves past a budget park; after that the window
	// restarts at that instant and the lifetime keeps counting.
	WindowCostUsd   float64 `json:"windowCostUsd"`
	LifetimeCostUsd float64 `json:"lifetimeCostUsd"`
	// When the current window opened — the instant of the approval. "" = never approved past a park.
	BudgetWindowStartedUtc string `json:"budgetWindowStartedUtc"`
	BudgetApprovals        int    `json:"budgetApprovals"`

	// ── SF3.3: the repo's git state, served since the engine half of SF3.3.
	//
	// NIL AND EMPTY ARE DIFFERENT FACTS, and the whole block exists to keep them apart. A nil Git is
	// an engine that predates SF3.3 — it knows nothing about the repo and the Face must say nothing
	// rather than paint a wrong "clean". A non-nil Git with IsRepo false is an engine that looked and
	// found a directory that is not a git repo at all, which is worth saying out loud.
	Git *GitDto `json:"git"`

	// ── FU-OWNER-10: which build is serving this run. The owner spent four out-of-band checks
	// answering "did my reinstall take?" because no surface named the engine it was attached to.
	// "" = an engine that predates the field; render nothing rather than "unknown", which reads like
	// a failed lookup rather than an old engine.
	EngineVersion string `json:"engineVersion"`
	EngineCommit  string `json:"engineCommit"`
	FaceBuild     string `json:"faceBuild"`
}

// GitDto mirrors Core/Http/ControlPlaneDto.Git.cs.
//
// Upstream, Ahead and Behind are POINTERS because the engine OMITS them when the branch has no
// upstream, and nil is not zero: a never-pushed branch and a branch level with its remote are
// different facts that must never render the same. Decoding them as plain ints would turn "you have
// never pushed this" into a confident "↑0 ↓0 — you are in sync".
type GitDto struct {
	// False with the rest of the block empty = a workspace that is genuinely not a git repo. That is
	// not the same as a nil StateDto.Git, which means an engine too old to know.
	IsRepo bool `json:"isRepo"`
	// "" with Detached true = a detached HEAD. The engine never serves the literal string "HEAD".
	Branch   string  `json:"branch"`
	Detached bool    `json:"detached"`
	Upstream *string `json:"upstream"`
	Ahead    *int    `json:"ahead"`
	Behind   *int    `json:"behind"`
	// HeadSha is the full 40; HeadShortSha is the 7 an operator pastes into `git show`.
	HeadSha      string `json:"headSha"`
	HeadShortSha string `json:"headShortSha"`
	HeadSubject  string `json:"headSubject"`
	// DirtyCount counts porcelain rows (a modified file and an untracked one are one row each), so
	// it is what the status strip renders; DirtySummary is the human sentence Home renders.
	Dirty         bool           `json:"dirty"`
	DirtyCount    int            `json:"dirtyCount"`
	DirtySummary  string         `json:"dirtySummary"`
	RecentCommits []GitCommitDto `json:"recentCommits"`
}

// HasUpstream reports whether the branch tracks anything at all. Every ahead/behind renderer goes
// through this rather than testing the pointers itself, so "no upstream" cannot be spelled two ways.
func (g *GitDto) HasUpstream() bool {
	return g != nil && g.Upstream != nil && *g.Upstream != ""
}

type GitCommitDto struct {
	Sha     string `json:"sha"`
	Subject string `json:"subject"`
}

// The cost-basis vocabulary, verbatim from the engine's Core/Events/LiveCostEstimator.cs. It is
// CLOSED — the engine says so — and it lives HERE, in the package that owns the wire, so that the
// renderer and the demo source speak the same five strings instead of two hand-copied sets.
//
// It exists because a dollar figure is only worth what the way it was arrived at is worth, and
// rendering all five the same way is what put "$0.00" in the Agent footer under a pane reading
// $13.07: the engine could not price the session yet, and the Face printed the zero as a fact.
const (
	// Nothing in flight — there is no session cost to state at all.
	BasisNone = "none"
	// The CLI's own recorded total, session over. The most trustworthy figure there is.
	BasisMeasured = "measured"
	// The provider put money on the wire per step (opencode). Just as trustworthy.
	BasisStreamed = "streamed"
	// Real tokens priced at this run's observed dollars-per-token. A number, but an inferred one.
	BasisRunRate = "estimated-from-run-rate"
	// Tokens are real; the cost is NOT knowable yet — no money on the wire and no rate to infer it
	// from. The one basis whose dollar field is meaningless and must never render as a figure.
	BasisNoRate = "no-rate-yet"
)

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
	// W4.4: this item's QA override — "" (inherit), "verify", or "off". It beats the stage and
	// plan dials for the session that claims this card.
	Qa string `json:"qa"`
	// W1.4's work-graph identity, served by the engine since W1.4 and dropped by the Face until
	// SF3.2 — which is why the board grouped nothing and split checkpoint ids on a dot to guess a
	// stage. Kind is "checkpoint" | "subtask"; StageId is the OWNING stage, authoritative;
	// Confirmed is the verdict engine's flag, and the difference between a card an agent CLAIMED
	// and one the engine agreed with.
	Kind      string `json:"kind"`
	StageId   string `json:"stageId"`
	Confirmed bool   `json:"confirmed"`
	// SF3.2's card meta, folded by the engine's TaskGraph so every reader gets one answer: the
	// session whose work last moved this card (0 = none), when it entered its current status
	// ("" = never moved / older engine), and how many times it has been picked up into in_progress.
	SessionNumber  int    `json:"sessionNumber"`
	StatusSinceUtc string `json:"statusSinceUtc"`
	Attempts       int    `json:"attempts"`
}

// Stage returns the card's owning stage. The wire's stageId is authoritative (W1.4); the split on
// the first dot is the LEGACY fallback for an engine that does not serve it — the convention the
// tracker parser uses — and never overrides a served value.
func (t TaskDto) Stage() string {
	if t.StageId != "" {
		return t.StageId
	}
	if i := strings.IndexByte(t.CheckpointId, '.'); i > 0 {
		return t.CheckpointId[:i]
	}
	return t.CheckpointId
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
// W4.3: set StageId instead of CheckpointId to add a rough card at STAGE level — it lands as a
// checkpoint-kind item the engine schedules, so work realised mid-run has somewhere to go.
type TaskAddRequestDto struct {
	CheckpointId string `json:"checkpointId"`
	Title        string `json:"title"`
	Order        int    `json:"order"`
	StageId      string `json:"stageId,omitempty"`
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
	// W4.4: "inherit" | "verify" | "off". Omitted = leave the item's QA override unchanged.
	Qa string `json:"qa,omitempty"`
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

// TaskSplitRequestDto (W4.3, POST /tasks/split): ask the plan's advisor to break one card into
// children. The server only PROPOSES — nothing mutates until the owner confirms each child.
type TaskSplitRequestDto struct {
	TaskId      string `json:"taskId"`
	Instruction string `json:"instruction,omitempty"`
	Count       int    `json:"count,omitempty"`
}

type TaskSplitChildDto struct {
	Title   string  `json:"title"`
	Context *string `json:"context"`
}

type TaskSplitResultDto struct {
	Ok           bool                `json:"ok"`
	Error        *string             `json:"error"`
	TaskId       *string             `json:"taskId"`
	CheckpointId *string             `json:"checkpointId"`
	Subtasks     []TaskSplitChildDto `json:"subtasks"`
	Interpreter  *string             `json:"interpreter"`
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
	// U2.2/U2.3: per-session cost + tokens, SUMMED server-side from the `costs` table (many rows
	// per session, one per category). Absent on an older engine, which lands as 0 — and 0 tokens
	// against a real cost is also what a pre-bug-#5 session honestly recorded, so neither the
	// Report digest nor the Dev stats table may invent a number here.
	CostUsd     float64 `json:"costUsd"`
	TokensIn    int64   `json:"tokensIn"`
	TokensOut   int64   `json:"tokensOut"`
	TokensThink int64   `json:"tokensThink"`
	TokensCache int64   `json:"tokensCache"`
	// SC7.2/SF3.1: what the session actually DID, computed by the engine from its structured tool
	// events and served on this same row since SC7.2. Nil on a session that predates the digest or
	// captured no tool calls — never an empty digest standing in for one, so a pane can tell "this
	// session did nothing we recorded" from "this engine does not send digests".
	Digest *SessionDigestDto `json:"digest"`
	// SF3.3: the session's own commits as `<short sha> <subject>` lines, read by the engine out of
	// the event log (the sessions table only ever persisted CommitCount). Empty on a session that
	// landed nothing OR predates the event — so a reader falls back to CommitCount, which is the
	// number that was always there, rather than reporting "no commits" for an old session.
	Commits []string `json:"commits"`
}

// SessionDigestDto mirrors Core/Http/ControlPlaneDto.Digest.cs. Mix and FilesTouched arrive ALREADY
// RANKED (count descending, then name) — the engine flattens its maps on purpose so two readers
// cannot sort the same session's tools into two different orders. Nothing here is re-derived here.
type SessionDigestDto struct {
	ToolCalls      int              `json:"toolCalls"`
	DistinctTools  int              `json:"distinctTools"`
	Mix            []DigestCountDto `json:"mix"`
	FilesTouched   []DigestCountDto `json:"filesTouched"`
	FileWrites     int              `json:"fileWrites"`
	Claims         []string         `json:"claims"`
	BackgroundJobs []string         `json:"backgroundJobs"`
	Commands       []string         `json:"commands"`
}

type DigestCountDto struct {
	Name  string `json:"name"`
	Count int    `json:"count"`
}

type SessionsDto struct {
	Sessions []SessionRowDto `json:"sessions"`
}

// ScoreDto mirrors the C# record (GET /scores): one verifier verdict. Passed and Threshold are the
// ENGINE's answer, resolved per stage from the same QA dial the run judged with — the Report tab
// used to read these rows through a canned SELECT and had no way to know a stage's own bar, so it
// could only show the raw number and hope the reader knew what 74 meant.
type ScoreDto struct {
	SessionNumber int      `json:"sessionNumber"`
	StageId       *string  `json:"stageId"`
	Score         int      `json:"score"`
	Verdict       string   `json:"verdict"`
	Passed        bool     `json:"passed"`
	Threshold     int      `json:"threshold"`
	Findings      []string `json:"findings"`
}

type ScoresDto struct {
	Scores []ScoreDto `json:"scores"`
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
	// CarriedFromPlan (SF0.4) names the plan of the EARLIER run that filed this bug; nil when the
	// current run filed it. Open bugs outlive the run that found them, so the ledger no longer
	// resets to empty every time a new plan starts in the same repo.
	CarriedFromPlan *string `json:"carriedFromPlan"`
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
	// SC7.1/SF3.1: the transcript's schema version, and the structured payload of a `tool` line.
	// Text is already the engine's one-liner (Providers/ToolLine.Render) — Tool is the structure
	// BESIDE it, not a second copy to re-render from: the Face uses the name for folding and mix
	// counting, where a rendered string would have to be parsed back apart. V is 1 on a line written
	// before the structured era (the server upgrades those on the way out, name recovered, fields
	// usually not) and 0 only on a line no engine sent.
	V    int          `json:"v"`
	Tool *ToolCallDto `json:"tool"`
}

// ToolCallDto mirrors Core/Events/ToolCall.cs: the tool's name as the wire gave it plus the fields
// the extractor kept (`path`, `command`, `taskId`, `purpose`, `bytes`/`lines`, …), each value
// truncated on its own so the object is always complete JSON.
type ToolCallDto struct {
	Name   string            `json:"name"`
	Fields map[string]string `json:"fields"`
}

// ShortName is the tool's own name with any MCP server prefix stripped — `bg_start`, not
// `mcp__conductor-tasks__bg_start`. It mirrors Providers/ToolLine.ShortName so a fold summary counts
// the same logical tool under the same label the engine's digest counts it under.
func (t *ToolCallDto) ShortName() string {
	if t == nil {
		return ""
	}
	s := strings.TrimSpace(t.Name)
	if s == "" {
		return ""
	}
	if i := strings.LastIndex(s, "__"); i >= 0 && i+2 < len(s) {
		return s[i+2:]
	}
	return s
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
// SC1.2/SC1.3: the first six fields are each a PRECONDITION for delivery, and the Face read them as
// if any of them were the verdict — "connected" on Started && HasToken, over an engine that could not
// notify anybody. WillDeliver is the engine's own derived verdict (block AND token AND a chat id AND
// a running service); WillDeliverReason carries doctor's sentence for the missing half;
// RestartRequired is the single case a live save cannot fix, because this engine process holds no
// Telegram service at all.
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
	WillDeliver         bool     `json:"willDeliver"`
	WillDeliverReason   *string  `json:"willDeliverReason"`
	RestartRequired     bool     `json:"restartRequired"`
}

// ViaQueue is what makes a green test mean anything: true = the message travelled the same send queue
// every run push travels; false = it was sent directly and proved only that Telegram is reachable,
// which is exactly what the old always-green Test button proved over a dead feature. Detail says which.
type TelegramTestResultDto struct {
	Ok          bool    `json:"ok"`
	BotUsername *string `json:"botUsername"`
	Error       *string `json:"error"`
	ViaQueue    bool    `json:"viaQueue"`
	Detail      *string `json:"detail"`
}

type TelegramSetTokenRequestDto struct {
	Token string `json:"token"`
}

// Ok = the token was saved; WillDeliver = the running engine can now actually notify somebody with
// it. Two questions, and one green tick used to answer both wrongly.
type TelegramSetTokenResultDto struct {
	Ok          bool    `json:"ok"`
	Message     *string `json:"message"`
	WillDeliver bool    `json:"willDeliver"`
}

// --- Session-local state for connection management ---

type ConnectionMode string

const (
	ModeLive ConnectionMode = "live"
	ModeDemo ConnectionMode = "demo"
)

// ConnectionState is what the Face knows about its own link to the engine.
//
// SF2.1: Connected has exactly ONE meaning — the engine answered our last /state poll — and exactly
// one writer (tui.Model.setConnected). It used to have three: the state poll set it true, a fetch
// error set it false, and either SSE stream re-derived it as events||transcript, so a live poll with
// both streams down read "disconnected" and a dead engine with a stale stream read "connected".
// EventsConnected and TranscriptConnected are still tracked — Home shows them as their own row —
// but they describe the STREAMS, and no longer redefine the link.
type ConnectionState struct {
	Mode                ConnectionMode
	URL                 string
	EventsConnected     bool
	TranscriptConnected bool
	Connected           bool
	LastError           *string
	// LastContactAt is when the engine last answered; zero means it never has in this session. It is
	// what lets a disconnected surface say "since when" instead of just "not connected".
	LastContactAt time.Time
	// Since is when the CURRENT value of Connected began, so a banner can age itself.
	Since time.Time
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
}
