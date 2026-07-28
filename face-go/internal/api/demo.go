package api

import (
	"encoding/json"
	"fmt"
	"math/rand"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

type demoSource struct {
	mu          sync.Mutex
	startTime   time.Time
	eventsSeq   int64
	txSeq       int64
	eventSubs   []chan json.RawMessage
	txSubs      []chan json.RawMessage
	stopCh      chan struct{}
	transcripts []TranscriptLineDto
	events      []json.RawMessage
	processes   []ProcessDto
	tickCount   int
	state       *StateDto
	sessions    []SessionRowDto
	plan        *PlanDto
	telegram    *TelegramStatusDto
	tasks       []TaskDto
}

func NewDemoSource() DataSource {
	now := time.Now()
	s := &demoSource{
		startTime: now,
		stopCh:    make(chan struct{}),
		processes: makeFakeProcesses(now),
		sessions:  makeFakeSessions(),
		state:     makeFakeState(),
		plan:      makeFakePlan(),
		telegram:  makeFakeTelegramStatus(),
		tasks:     makeFakeTasks(),
	}

	go s.runSimulation()

	return s
}

func (s *demoSource) FetchState() (*StateDto, error) {
	return s.state, nil
}

func (s *demoSource) FetchTasks() (*TasksDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]TaskDto, len(s.tasks))
	copy(out, s.tasks)
	return &TasksDto{Tasks: out}, nil
}

// makeFakeTasks seeds a board with all three Kanban columns populated — the state a reviewer
// should see (and the goldens capture) by default. T3 carries owner context so the card detail
// (P3) shows a filled extra-context block out of the box.
func makeFakeTasks() []TaskDto {
	return []TaskDto{
		{TaskId: "T1", CheckpointId: "F7.4", Title: "Implement gate caching by SHA", Status: "done", Source: "planner", Order: 1},
		{TaskId: "T2", CheckpointId: "F7.4", Title: "Add per-stage truth gate config", Status: "done", Source: "agent", Order: 2},
		{TaskId: "T3", CheckpointId: "F7.4", Title: "Wire RunDb.GetLastPassingGateResult", Status: "in_progress", Source: "agent", Order: 3,
			Context: "Reuse the SHA cache from F7.4-a1; the miss path must stay allocation-free.",
			Paths:   []string{"src/Conductor/Core/Gating/GateCache.cs", "src/Conductor/Core/Store/RunDb.cs"}},
		{TaskId: "T4", CheckpointId: "F7.5", Title: "Add SkipIfFresh file-timestamp check", Status: "todo", Source: "planner", Order: 4},
	}
}

// FetchPromptBlocks mirrors GET /prompt/blocks?task= — the same block order and editability the
// server's PromptComposer emits, composed from the demo's own plan + task data.
func (s *demoSource) FetchPromptBlocks(taskId string) (*PromptBlocksDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, t := range s.tasks {
		if t.TaskId != taskId {
			continue
		}
		stageId := t.CheckpointId
		if i := strings.IndexByte(stageId, '.'); i > 0 {
			stageId = stageId[:i]
		}
		blocks := []PromptBlockDto{
			{Kind: "persona", Label: "Persona — deliver", Editable: false,
				Content: "You are a delivery engineer: land the checkpoint with proof, never weaken the measurement."},
			{Kind: "stageNotes", Label: "Stage notes — " + stageId, Editable: false,
				Content: "Gate caching stage: reuse GateRunner, key results by content SHA."},
			{Kind: "taskTitle", Label: "Task title", Content: t.Title, Editable: true},
			{Kind: "taskContext", Label: "Extra context (task-scoped)", Content: t.Context, Editable: true},
			{Kind: "knowledge", Label: "Injected knowledge", Editable: false,
				Content: "## Ledger\n- goldens live in internal/tui/testdata\n## Open bugs\n- #3 flaky stall detector on wake"},
			{Kind: "tools", Label: "Tool contract", Editable: false,
				Content: "conductor note / bg / task --done <id> --evidence <path>"},
		}
		return &PromptBlocksDto{Ok: true, TaskId: t.TaskId, CheckpointId: t.CheckpointId, StageId: stageId, Blocks: blocks}, nil
	}
	msg := "task not found: " + taskId
	return &PromptBlocksDto{Ok: false, Error: &msg}, nil
}

// PostTaskEdit mirrors POST /tasks/edit: nil = unchanged, blank title refused, empty context
// clears, and PF3 declared paths follow the same nil/empty contract (entries cleaned like C#).
func (s *demoSource) PostTaskEdit(req TaskEditRequestDto) (*TaskWriteResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if req.Title == nil && req.Context == nil && req.Paths == nil {
		msg := "nothing to edit — give a title, a context, and/or paths"
		return &TaskWriteResultDto{Ok: false, Error: &msg}, nil
	}
	if req.Title != nil && strings.TrimSpace(*req.Title) == "" {
		msg := "title cannot be blank"
		return &TaskWriteResultDto{Ok: false, Error: &msg}, nil
	}
	for i := range s.tasks {
		if s.tasks[i].TaskId != req.TaskId {
			continue
		}
		if req.Title != nil {
			s.tasks[i].Title = strings.TrimSpace(*req.Title)
		}
		if req.Context != nil {
			s.tasks[i].Context = *req.Context
		}
		if req.Paths != nil {
			clean := make([]string, 0, len(req.Paths))
			for _, p := range req.Paths {
				if p = strings.TrimSpace(p); p != "" {
					clean = append(clean, p)
				}
			}
			s.tasks[i].Paths = clean
		}
		t := s.tasks[i]
		return &TaskWriteResultDto{Ok: true, TaskId: &t.TaskId, Status: &t.Status, CheckpointId: &t.CheckpointId, Title: &t.Title, Order: t.Order}, nil
	}
	msg := "task not found: " + req.TaskId
	return &TaskWriteResultDto{Ok: false, Error: &msg}, nil
}

// PostTaskRefine mirrors POST /tasks/refine: a deterministic canned proposal (the demo's "advisor"),
// so the preview→confirm flow is fully exercisable offline.
func (s *demoSource) PostTaskRefine(req TaskRefineRequestDto) (*TaskRefineResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, t := range s.tasks {
		if t.TaskId != req.TaskId {
			continue
		}
		title := t.Title + " (with eviction test)"
		context := "Demo advisor: start from the smallest end-to-end slice; cover the cache-miss and eviction paths before wiring the UI."
		interpreter := "demo-advisor"
		return &TaskRefineResultDto{Ok: true, TaskId: &t.TaskId, Title: &title, Context: &context, Interpreter: &interpreter}, nil
	}
	msg := "task not found: " + req.TaskId
	return &TaskRefineResultDto{Ok: false, Error: &msg}, nil
}

// PostTaskSplit mirrors POST /tasks/split (W4.3): a canned two-child proposal, so the
// propose→confirm split flow is fully exercisable offline.
func (s *demoSource) PostTaskSplit(req TaskSplitRequestDto) (*TaskSplitResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, t := range s.tasks {
		if t.TaskId != req.TaskId {
			continue
		}
		readCtx := "Demo advisor: cover the cache-miss path first."
		interpreter := "demo-advisor"
		return &TaskSplitResultDto{
			Ok: true, TaskId: &t.TaskId, CheckpointId: &t.CheckpointId, Interpreter: &interpreter,
			Subtasks: []TaskSplitChildDto{
				{Title: t.Title + " — read path", Context: &readCtx},
				{Title: t.Title + " — write path", Context: nil},
			},
		}, nil
	}
	msg := "task not found: " + req.TaskId
	return &TaskSplitResultDto{Ok: false, Error: &msg}, nil
}

// PostTaskUpdate mirrors the server contract: transition legality lives in the fold, so an illegal
// move is an accepted no-op and Status echoes what actually happened (see TaskGraph.IsValidTransition).
func (s *demoSource) PostTaskUpdate(req TaskUpdateRequestDto) (*TaskWriteResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for i := range s.tasks {
		if s.tasks[i].TaskId != req.TaskId {
			continue
		}
		if isValidTaskTransition(s.tasks[i].Status, req.Status) {
			s.tasks[i].Status = req.Status
		}
		actual := s.tasks[i].Status
		return &TaskWriteResultDto{Ok: true, TaskId: &s.tasks[i].TaskId, Status: &actual}, nil
	}
	msg := "task not found: " + req.TaskId
	return &TaskWriteResultDto{Ok: false, Error: &msg}, nil
}

func (s *demoSource) PostTaskAdd(req TaskAddRequestDto) (*TaskWriteResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if strings.TrimSpace(req.Title) == "" {
		msg := "title is required"
		return &TaskWriteResultDto{Ok: false, Error: &msg}, nil
	}
	if strings.TrimSpace(req.CheckpointId) == "" {
		msg := "checkpointId is required"
		return &TaskWriteResultDto{Ok: false, Error: &msg}, nil
	}
	order := req.Order
	if order <= 0 {
		for _, t := range s.tasks {
			if t.CheckpointId == req.CheckpointId && t.Order >= order {
				order = t.Order + 1
			}
		}
		if order == 0 {
			order = 1
		}
	}
	task := TaskDto{
		TaskId:       fmt.Sprintf("%s-a%d", req.CheckpointId, order),
		CheckpointId: req.CheckpointId,
		Title:        strings.TrimSpace(req.Title),
		Status:       "todo",
		Source:       "human",
		Order:        order,
	}
	s.tasks = append(s.tasks, task)
	status := "todo"
	return &TaskWriteResultDto{Ok: true, TaskId: &task.TaskId, Status: &status, CheckpointId: &task.CheckpointId, Title: &task.Title, Order: order}, nil
}

// isValidTaskTransition mirrors Core/Events/TaskGraph.IsValidTransition, including the G2 reopen
// moves back out of done/skipped.
func isValidTaskTransition(from, to string) bool {
	switch from + "→" + to {
	case "todo→in_progress", "in_progress→done", "in_progress→todo", "todo→done",
		"todo→skipped", "in_progress→skipped",
		"done→in_progress", "done→todo", "skipped→todo", "skipped→in_progress":
		return true
	}
	return false
}

func (s *demoSource) FetchProcesses() (*ProcessesDto, error) {
	return &ProcessesDto{Processes: s.processes}, nil
}

func (s *demoSource) FetchSessions() (*SessionsDto, error) {
	return &SessionsDto{Sessions: s.sessions}, nil
}

func (s *demoSource) FetchTimeline() (*TimelineDto, error) {
	return &TimelineDto{Entries: makeFakeTimeline()}, nil
}

func (s *demoSource) FetchLedger() (*LedgerDto, error) {
	return &LedgerDto{Entries: makeFakeLedger()}, nil
}

func (s *demoSource) FetchBugs() (*BugsDto, error) {
	return &BugsDto{Bugs: makeFakeBugs()}, nil
}

// Demo write-side knowledge: accept the write (so the tab's success toast fires) without persisting —
// the demo's ledger/bugs are regenerated each poll, so there's nothing durable to append to.
func (s *demoSource) PostNote(NoteRequestDto) (*KnowledgeWriteResultDto, error) {
	return &KnowledgeWriteResultDto{Ok: true}, nil
}

func (s *demoSource) PostBug(BugNewRequestDto) (*KnowledgeWriteResultDto, error) {
	id := int64(9)
	return &KnowledgeWriteResultDto{Ok: true, Id: &id}, nil
}

func (s *demoSource) PostBugResolve(req BugResolveRequestDto) (*KnowledgeWriteResultDto, error) {
	return &KnowledgeWriteResultDto{Ok: true, Id: &req.Id}, nil
}

func (s *demoSource) FetchPromptPreview(stageId, kind string) (*PromptPreviewDto, error) {
	return &PromptPreviewDto{
		Model: "deepseek/deepseek-v4-pro",
		Kind:  kind,
		Prompt: fmt.Sprintf("# %s session — stage %s\n\nYou are the conductor's delivery agent. Land the "+
			"checkpoints for stage %s.\n\n## Tools\nconductor note / bg / task --done <id> --evidence <path>\n\n"+
			"## Rules\nEvidence or it did not happen. Never weaken the measurement.", kind, stageId, stageId),
	}, nil
}

func (s *demoSource) QueryReport(sql string) (*QueryResultDto, error) {
	// The Report tab's scores section (U2.2) runs a canned query against `scores`, so the demo has to
	// answer by SHAPE — a source that returns stage/cost columns to every query would render the
	// verifier section as nonsense offline, which is exactly what --demo exists to catch.
	if strings.Contains(strings.ToLower(sql), "from scores") {
		return &QueryResultDto{
			Columns: []string{"session_number", "score", "verdict"},
			Rows: []QueryRowDto{
				{Values: []string{"11", "66", "WARN"}},
				{Values: []string{"8", "90", "PASS"}},
				{Values: []string{"2", "88", "PASS"}},
			},
		}, nil
	}
	return &QueryResultDto{
		Columns: []string{"stage", "cost"},
		Rows: []QueryRowDto{
			{Values: []string{"F1", "$0.42"}},
			{Values: []string{"F6", "$0.18"}},
			{Values: []string{"F7", "$0.08"}},
		},
		Truncated: false,
	}, nil
}

func (s *demoSource) PostControl(cmd ControlRequestDto) (*ControlAcceptedDto, error) {
	// set-rollover mutates the demo run state (mirrors ControlDispatcher.ParseRolloverValue) so
	// the Settings row's active-override display round-trips offline like every other demo edit.
	if cmd.Command == "set-rollover" {
		s.mu.Lock()
		defer s.mu.Unlock()
		switch v := strings.ToLower(strings.TrimSpace(cmd.Value)); {
		case v == "" || v == "clear":
			s.state.MaxSessionTokensThisRun = nil
		case v == "off" || v == "0":
			off := int64(0)
			s.state.MaxSessionTokensThisRun = &off
		default:
			if n, err := strconv.ParseInt(v, 10, 64); err == nil && n > 0 {
				s.state.MaxSessionTokensThisRun = &n
			}
		}
	}
	return &ControlAcceptedDto{Accepted: true}, nil
}

func (s *demoSource) PostProcessKill(req ProcessKillRequestDto) (*ProcessKillResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for i := range s.processes {
		if s.processes[i].Pid != req.Pid {
			continue
		}
		if !s.processes[i].Alive {
			msg := fmt.Sprintf("pid %d has already exited", req.Pid)
			return &ProcessKillResultDto{Ok: false, Error: &msg, Pid: req.Pid}, nil
		}
		now := time.Now().UTC().Format(time.RFC3339)
		s.processes[i].Alive = false
		s.processes[i].ExitedUtc = &now
		return &ProcessKillResultDto{Ok: true, Pid: req.Pid}, nil
	}
	msg := fmt.Sprintf("pid %d is not a tracked process of this run", req.Pid)
	return &ProcessKillResultDto{Ok: false, Error: &msg, Pid: req.Pid}, nil
}

func (s *demoSource) PostInject(req InjectRequestDto) (*InjectAcceptedDto, error) {
	now := time.Now().UTC().Format(time.RFC3339)
	return &InjectAcceptedDto{
		Accepted:    true,
		RunId:       strPtr("demo-run-id"),
		StageId:     strPtr("F7"),
		RecordedUtc: &now,
	}, nil
}

func (s *demoSource) FetchPlan() (*PlanDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	clone := *s.plan
	return &clone, nil
}

func (s *demoSource) PostPlanEdit(req PlanEditRequestDto) (*PlanMutationResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, e := range req.Edits {
		if e.Target == "telegram" {
			applyDemoTelegramEdit(s.telegram, e)
			continue
		}
		applyDemoEdit(s.plan, e)
	}
	s.plan.PlanVersion++
	return &PlanMutationResultDto{Ok: true, PlanVersion: s.plan.PlanVersion}, nil
}

func (s *demoSource) PostPlanImport(req PlanImportRequestDto) (*PlanImportResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	// Synthesise a plausible diff: one new stage + a title tweak on an existing one.
	newTitle := "Knowledge that compounds"
	diff := PlanDiffDto{
		AddedStages: []PlanStageDto{
			{Id: "M7", Title: newTitle, Sessions: 3, Kind: "deliver", DependsOn: []string{"M6"}},
		},
		ChangedStages: []PlanStageChangeDto{
			{Id: "F7", Fields: []PlanFieldChangeDto{{Field: "sessions", Old: strPtr("5"), New: strPtr("6")}}},
		},
	}
	if req.Apply {
		s.plan.Stages = append(s.plan.Stages, diff.AddedStages...)
		s.plan.PlanVersion++
	}
	// Mirror the server's interpreter surface: a path/markdown source parses structurally; anything
	// else reads as prose the advisor model interpreted (G1.1).
	interpreter := "structured"
	if !strings.Contains(req.Source, "/") && !strings.Contains(req.Source, "\\") && !strings.Contains(req.Source, "#") {
		interpreter = "claude-fable-5"
	}
	return &PlanImportResultDto{Ok: true, Diff: diff, Applied: req.Apply, PlanVersion: s.plan.PlanVersion, Interpreter: &interpreter}, nil
}

func (s *demoSource) FetchTelegramStatus() (*TelegramStatusDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	clone := *s.telegram
	return &clone, nil
}

func (s *demoSource) PostTelegramTest() (*TelegramTestResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.telegram.HasToken {
		return &TelegramTestResultDto{Ok: false, Error: strPtr("no bot token — save one below first")}, nil
	}
	name := "conductor_demo_bot"
	s.telegram.Started = true
	s.telegram.BotUsername = &name
	now := time.Now().UTC().Format(time.RFC3339)
	s.telegram.LastPollUtc = &now
	s.telegram.LastError = nil
	return &TelegramTestResultDto{Ok: true, BotUsername: &name}, nil
}

func (s *demoSource) PostTelegramToken(req TelegramSetTokenRequestDto) (*TelegramSetTokenResultDto, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if strings.TrimSpace(req.Token) == "" {
		return &TelegramSetTokenResultDto{Ok: false, Message: strPtr("token is empty")}, nil
	}
	s.telegram.HasToken = true
	msg := "saved — restart conductor to connect with the new token"
	return &TelegramSetTokenResultDto{Ok: true, Message: &msg}, nil
}

// applyDemoTelegramEdit mirrors ControlPlaneServer.Plan.cs's ApplyTelegramEdit — same field names,
// same comma-joined chat-id list convention as the C# side.
func applyDemoTelegramEdit(t *TelegramStatusDto, e PlanEditDto) {
	val := ""
	if e.Value != nil {
		val = *e.Value
	}
	switch e.Field {
	case "allowedchatids":
		if val == "" {
			t.AllowedChatIds = nil
		} else {
			t.AllowedChatIds = strings.Split(val, ",")
		}
	case "pollintervalseconds":
		if n, err := strconv.Atoi(val); err == nil {
			t.PollIntervalSeconds = n
		}
	case "enabletwoway":
		t.EnableTwoWay = val == "true"
	}
}

func applyDemoEdit(plan *PlanDto, e PlanEditDto) {
	val := ""
	if e.Value != nil {
		val = *e.Value
	}
	// Structural ops mirror ControlPlaneServer.Plan.cs's ApplyStructuralEdit: add/delete a whole
	// stage or gate; new objects take the same schema defaults (stage: 1 session, deliver kind;
	// gate: full tier).
	switch e.Op {
	case "add":
		if e.Target == "stage" {
			title := val
			if title == "" {
				title = e.Id
			}
			plan.Stages = append(plan.Stages, PlanStageDto{Id: e.Id, Title: title, Sessions: 1, Kind: "deliver"})
		} else if e.Target == "gate" {
			plan.Gates = append(plan.Gates, PlanGateDto{Name: e.Id, Command: val, Tier: "full", TimeoutMinutes: 20})
		}
		return
	case "delete":
		if e.Target == "stage" {
			plan.Stages = removeStageById(plan.Stages, e.Id)
		} else if e.Target == "gate" {
			plan.Gates = removeGateByName(plan.Gates, e.Id)
		}
		return
	}
	switch e.Target {
	case "stage":
		for i := range plan.Stages {
			if plan.Stages[i].Id != e.Id {
				continue
			}
			switch e.Field {
			case "title":
				plan.Stages[i].Title = val
			case "kind":
				plan.Stages[i].Kind = val
			case "model":
				plan.Stages[i].Model = &val
			case "workflow":
				plan.Stages[i].Workflow = &val
			case "notes":
				plan.Stages[i].Notes = &val
			case "qamode": // P2 per-stage QA dial — empty inherits the plan dial
				if val == "" {
					plan.Stages[i].QaMode = nil
				} else {
					plan.Stages[i].QaMode = &val
				}
			}
		}
	case "gate":
		for i := range plan.Gates {
			if plan.Gates[i].Name != e.Id {
				continue
			}
			switch e.Field {
			case "command":
				plan.Gates[i].Command = val
			case "tier":
				plan.Gates[i].Tier = val
			}
		}
	case "plan":
		switch e.Field {
		case "gatepolicy":
			plan.GatePolicy = val
		case "defaultworkflow":
			plan.DefaultWorkflow = val
		}
	case "limits": // G3.3 live limits — mirrors ApplyLimitsEdit (empty clears a nullable cap)
		switch e.Field {
		case "maxsessions":
			if n, err := strconv.Atoi(val); err == nil && n > 0 {
				plan.Limits.MaxSessions = &n
			} else {
				plan.Limits.MaxSessions = nil
			}
		case "maxruncostusd":
			if f, err := strconv.ParseFloat(val, 64); err == nil && f > 0 {
				plan.Limits.MaxRunCostUsd = &f
			} else {
				plan.Limits.MaxRunCostUsd = nil
			}
		case "maxruntokens":
			if n, err := strconv.ParseInt(val, 10, 64); err == nil && n > 0 {
				plan.Limits.MaxRunTokens = &n
			} else {
				plan.Limits.MaxRunTokens = nil
			}
		case "stallminutes":
			if n, err := strconv.Atoi(val); err == nil && n > 0 {
				plan.Limits.StallMinutes = n
			}
		case "sessiontimeoutminutes":
			if n, err := strconv.Atoi(val); err == nil && n > 0 {
				plan.Limits.SessionTimeoutMinutes = n
			}
		case "verifierthreshold": // P2: the base verifier bar is editable now
			if n, err := strconv.Atoi(val); err == nil && n >= 1 && n <= 100 {
				plan.Limits.VerifierThreshold = n
			}
		case "maxsessiontokens": // P5: session-token rollover — empty/0 = OFF, the default
			if n, err := strconv.ParseInt(val, 10, 64); err == nil && n > 0 {
				plan.Limits.MaxSessionTokens = &n
			} else {
				plan.Limits.MaxSessionTokens = nil
			}
		case "softbreakratio":
			if f, err := strconv.ParseFloat(val, 64); err == nil && f > 0 && f <= 1 {
				plan.Limits.SoftBreakRatio = &f
			} else {
				plan.Limits.SoftBreakRatio = nil
			}
		}
	case "qa": // P2 plan-wide QA dial — mirrors ApplyQaEdit (empty mode clears the dial)
		switch e.Field {
		case "mode":
			if val == "" {
				plan.Qa = nil
			} else {
				if plan.Qa == nil {
					plan.Qa = &PlanQaDto{AuditCoversPriorSessions: true}
				}
				plan.Qa.Mode = val
			}
		case "verifierthreshold":
			if plan.Qa != nil {
				if n, err := strconv.Atoi(val); err == nil && n >= 1 && n <= 100 {
					plan.Qa.VerifierThreshold = &n
				} else {
					plan.Qa.VerifierThreshold = nil
				}
			}
		}
	}
}

func removeStageById(stages []PlanStageDto, id string) []PlanStageDto {
	out := stages[:0:0]
	for _, s := range stages {
		if s.Id != id {
			out = append(out, s)
		}
	}
	return out
}

func removeGateByName(gates []PlanGateDto, name string) []PlanGateDto {
	out := gates[:0:0]
	for _, g := range gates {
		if g.Name != name {
			out = append(out, g)
		}
	}
	return out
}

func makeFakePlan() *PlanDto {
	sp := func(s string) *string { return &s }
	return &PlanDto{
		Name:            "conductor-foreman",
		PlanVersion:     7,
		PlanFile:        `C:\Code\conductor\plans\conductor-foreman.plan.json`,
		GatePolicy:      "perPhase",
		DefaultWorkflow: "deliver-verify",
		DefaultModel:    "claude-opus-4-8",
		Workflows:       []string{"deliver-verify", "big-dev-then-big-audit", "docs-only", "spike"},
		Stages: []PlanStageDto{
			{Id: "F5", Title: "Control plane", Sessions: 3, Kind: "deliver", Model: sp("claude-sonnet-5"), Workflow: sp("deliver-verify"), DependsOn: []string{"F4"}},
			{Id: "F6", Title: "Ink TUI v1", Sessions: 5, Kind: "deliver", Model: sp("claude-opus-4-8"), Workflow: sp("deliver-verify"), DependsOn: []string{"F5"}},
			{Id: "F7", Title: "Gate caching + truth gates", Sessions: 5, Kind: "deliver", Model: sp("claude-opus-4-8"), Workflow: sp("big-dev-then-big-audit"), Notes: sp("Persona: deliver"), DependsOn: []string{"F6"}},
			{Id: "F8", Title: "conductor chat + Telegram v2", Sessions: 4, Kind: "deliver", Model: sp("claude-sonnet-5"), Workflow: sp("deliver-verify"), DependsOn: []string{"F7"}},
			{Id: "F9", Title: "Dogfood close", Sessions: 3, Kind: "review", Workflow: sp("big-dev-then-big-audit"), DependsOn: []string{"F8"}},
		},
		Gates: []PlanGateDto{
			{Name: "build", Command: "dotnet build Conductor.slnx", Tier: "fast", TimeoutMinutes: 10},
			{Name: "test", Command: "dotnet test Conductor.slnx", Tier: "full", TimeoutMinutes: 20},
			{Name: "ratchet", Command: "dotnet test --filter Category=Architecture", Tier: "truth", TimeoutMinutes: 15},
		},
		// U1.1: caps are set here so the demo tour actually shows Home's budget/headroom rows — an
		// uncapped demo would render a Run panel the product mostly doesn't have.
		Limits: PlanLimitsDto{
			StallMinutes: 12, SessionTimeoutMinutes: 240, VerifierThreshold: 80,
			MaxRunCostUsd: f64Ptr(10), MaxRunTokens: i64Ptr(2_000_000),
		},
	}
}

func f64Ptr(f float64) *float64 { return &f }
func i64Ptr(n int64) *int64     { return &n }

func (s *demoSource) SubscribeEvents(onEvent func(ConductorEventDto), onConnected func(bool)) func() {
	ch := make(chan json.RawMessage, 64)
	s.mu.Lock()
	s.eventSubs = append(s.eventSubs, ch)
	s.mu.Unlock()

	onConnected(true)
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			select {
			case raw, ok := <-ch:
				if !ok {
					return
				}
				var event ConductorEventDto
				if err := json.Unmarshal(raw, &event); err == nil {
					onEvent(event)
				}
			case <-s.stopCh:
				return
			}
		}
	}()
	return func() {
		close(ch)
		<-done
	}
}

func (s *demoSource) SubscribeTranscript(onLine func(TranscriptLineDto), onConnected func(bool)) func() {
	ch := make(chan json.RawMessage, 256)
	s.mu.Lock()
	s.txSubs = append(s.txSubs, ch)
	transcripts := make([]TranscriptLineDto, len(s.transcripts))
	copy(transcripts, s.transcripts)
	s.mu.Unlock()

	onConnected(true)

	for _, tx := range transcripts {
		onLine(tx)
	}

	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			select {
			case raw, ok := <-ch:
				if !ok {
					return
				}
				var tx TranscriptLineDto
				if err := json.Unmarshal(raw, &tx); err == nil {
					onLine(tx)
				}
			case <-s.stopCh:
				return
			}
		}
	}()
	return func() {
		close(ch)
		<-done
	}
}

func (s *demoSource) SubscribeConsole(onLine func(ConsoleLineDto), onConnected func(bool)) func() {
	onConnected(true)
	// Replay a few raw agent-stdout lines (the real thing is the agent CLI's raw JSON stream).
	raw := []string{
		`{"type":"system","subtype":"init","session_id":"s12","model":"deepseek-v4-pro"}`,
		`{"type":"assistant","message":{"content":[{"type":"text","text":"Examining GateCache..."}]}}`,
		`{"type":"assistant","message":{"content":[{"type":"tool_use","name":"read","input":{"path":"GateCache.cs"}}]}}`,
		`{"type":"user","message":{"content":[{"type":"tool_result","content":"GateCache.cs:142 lines"}]}}`,
		`{"type":"result","subtype":"success","total_cost_usd":0.12,"num_turns":4}`,
	}
	for i, line := range raw {
		onLine(ConsoleLineDto{Seq: int64(i + 1), Text: line})
	}
	return func() {}
}

func (s *demoSource) Close() {
	close(s.stopCh)
}

func (s *demoSource) runSimulation() {
	ticker := time.NewTicker(1200 * time.Millisecond)
	defer ticker.Stop()

	transcriptLines := []struct {
		kind string
		text string
	}{
		{"system", "Session #12 started · Deliver · Stage F7 · Attempt 1"},
		{"thinking", "Let me examine the GateCache implementation to understand the caching pattern..."},
		{"tool", "read src/Conductor/Core/Gating/GateCache.cs"},
		{"result", "GateCache.cs:142 lines — caches by (name, tier, sha)"},
		{"thinking", "I see the pattern. GateResult is stored with a composite key. I need to expose the last-passing result via RunDb so the orchestrator can skip re-running green gates."},
		{"agent", "Found the caching layer. I'll add GetLastPassingGateResult to RunDb and wire it into GateRunner.RunTrackedAsync."},
		{"tool", "write src/Conductor/Core/Store/RunDb.Gates.cs"},
		{"result", "Created RunDb.Gates.cs with GetLastPassingGateResult query"},
		{"thinking", "The query needs to join gates with attempts to find the most recent pass. Let me verify with a test."},
		{"tool", "run dotnet test --filter GateCacheTests"},
		{"result", "12/12 tests pass. 0w/0e. 2.3s elapsed"},
		{"agent", "All tests pass. Ready for the next checkpoint."},
		{"system", "Gate build ✓ (2.3s)"},
		{"tool", "run dotnet build Conductor.slnx"},
		{"result", "Build succeeded. 0 Error(s), 0 Warning(s)"},
		{"system", "Gate test ✓ (4.1s)"},
		{"agent", "Running gate battery: build ✓, test ✓, lint is next."},
	}

	processNames := []string{"dotnet test", "dotnet build", "git status", "npm test"}
	gates := []string{"build", "test", "lint", "audit"}

	for range ticker.C {
		s.mu.Lock()
		s.tickCount++

		if s.tickCount <= len(transcriptLines) {
			tx := transcriptLines[s.tickCount-1]
			seq := atomic.AddInt64(&s.txSeq, 1)
			ts := time.Now()
			line := TranscriptLineDto{
				Seq:       seq,
				Ts:        ts,
				SessionId: "s12",
				Kind:      tx.kind,
				Text:      tx.text,
			}
			s.transcripts = append(s.transcripts, line)

			raw, _ := json.Marshal(line)
			for _, ch := range s.txSubs {
				select {
				case ch <- raw:
				default:
				}
			}
		}

		if s.tickCount%3 == 0 {
			proc := ProcessDto{
				Pid:            1000 + rand.Intn(9000),
				Purpose:        processNames[rand.Intn(len(processNames))],
				StageId:        strPtr("F7"),
				Alive:          s.tickCount%6 != 0,
				LastOutputLine: strPtr(fmt.Sprintf("... running (%d lines)", s.tickCount*10)),
			}
			s.processes = append(s.processes, proc)
			if len(s.processes) > 5 {
				s.processes = s.processes[1:]
			}
		}

		if s.tickCount%5 == 0 {
			gate := gates[(s.tickCount/5)%len(gates)]
			eventSeq := atomic.AddInt64(&s.eventsSeq, 1)
			gateStates := []string{"pass", "pass", "pass", "fail", "running"}
			state := gateStates[rand.Intn(len(gateStates))]
			event := ConductorEventDto{
				Type:  "gateFinished",
				Seq:   eventSeq,
				Ts:    time.Now(),
				RunId: "demo-run",
			}
			raw, _ := json.Marshal(map[string]any{
				"type":       event.Type,
				"seq":        event.Seq,
				"ts":         event.Ts,
				"runId":      event.RunId,
				"gateName":   gate,
				"passed":     state == "pass",
				"elapsedSec": fmt.Sprintf("%.1f", 1.0+rand.Float64()*5),
			})
			s.events = append(s.events, raw)
			if len(s.events) > 400 {
				s.events = s.events[1:]
			}

			for _, ch := range s.eventSubs {
				select {
				case ch <- raw:
				default:
				}
			}
		}

		elapsed := time.Since(s.startTime).Seconds()
		cost := elapsed * 0.0001
		tokIn := int64(s.tickCount * 150)
		tokOut := int64(s.tickCount * 80)
		tokReason := int64(s.tickCount * 40)
		cp := (s.tickCount / 3) % 5
		s.state.SessionElapsedSec = elapsed
		s.state.SessionCostUsd = cost
		s.state.SessionTokensInput = tokIn
		s.state.SessionTokensOutput = tokOut
		s.state.SessionTokensReasoning = tokReason
		s.state.OverheadCostUsd = elapsed * 0.00003
		s.state.TotalCostUsd = cost + 0.42
		s.state.TokensInput = tokIn + 2500
		s.state.TokensOutput = tokOut + 1800
		s.state.TokensReasoning = tokReason + 900
		s.state.CurrentCheckpoint = fmt.Sprintf("F7.%d", cp+1)
		s.state.CurrentCheckpointTitle = fmt.Sprintf("F7 Checkpoint %d", cp+1)
		s.state.DoneCount = cp
		s.state.GateSummary = fmt.Sprintf("build ✓ test ✓ lint ○ (%.0fs)", elapsed)

		for i := range s.state.Gates {
			s.state.Gates[i].ElapsedSec = 1.0 + rand.Float64()*3
		}

		s.mu.Unlock()
	}
}

func makeFakeState() *StateDto {
	return &StateDto{
		PlanName:               "conductor-foreman",
		Status:                 "Running",
		StageId:                "F7",
		StageTitle:             "Gate caching + truth gates + speed program",
		Persona:                strPtr("deliver"),
		DoneCount:              2,
		TotalCount:             40,
		TotalCostUsd:           0.42,
		TokensInput:            2500,
		TokensOutput:           1800,
		CurrentCheckpoint:      "F7.3",
		CurrentCheckpointTitle: "Wire caching layer",
		GateSummary:            "build ✓ test ● lint ○",
		RunId:                  "demo-run-id",
		Repo:                   "C:\\Code\\conductor",
		PlanDir:                "C:\\Code\\conductor\\plans",
		// U1.1: Home names the whole workspace, so the demo has to carry the whole workspace. StateDir
		// is repo-rooted (PlanConfig.StateDir), NOT planDir-rooted — the demo must mirror that or it
		// teaches the layout wrong.
		Tracker:             "CONDUCTOR-VNEXT-PLAN.md",
		StateDir:            "C:\\Code\\conductor\\.conductor",
		SessionNumber:       12,
		SessionKind:         "Deliver",
		Model:               "claude-opus-4-8",
		Provider:            "claude",
		Attempt:             1,
		MaxAttempts:         3,
		SessionElapsedSec:   0,
		AgentActive:         true,
		SessionCostUsd:      0,
		SessionTokensInput:  0,
		SessionTokensOutput: 0,
		Stages: []StageDto{
			{Id: "F0", Title: "Foundations", Done: 3, Total: 3, State: "confirmed", Depth: 0},
			{Id: "F1", Title: "run.db task store", Done: 4, Total: 4, State: "confirmed", Depth: 0},
			{Id: "F2", Title: "ProcessSupervisor + bg primitives", Done: 4, Total: 4, State: "confirmed", Depth: 0},
			{Id: "F3", Title: "Stall v2 + failure breaker", Done: 4, Total: 4, State: "confirmed", Depth: 0},
			{Id: "F4", Title: "Verifier role", Done: 5, Total: 5, State: "confirmed", Depth: 0},
			{Id: "F5", Title: "Control plane", Done: 3, Total: 3, State: "confirmed", Depth: 0},
			{Id: "F6", Title: "Ink TUI v1", Done: 5, Total: 5, State: "confirmed", Depth: 0,
				Checkpoints: []CheckpointDto{
					{Id: "F6.1", Title: "TS+Ink project scaffold", Status: "done"},
					{Id: "F6.2", Title: "Plan pane", Status: "done"},
					{Id: "F6.3", Title: "Agent pane", Status: "done"},
					{Id: "F6.4", Title: "Process pane + palette", Status: "done"},
					{Id: "F6.5", Title: "Golden snapshot tests", Status: "done"},
				},
			},
			{Id: "F7", Title: "Gate caching + truth gates", Done: 2, Total: 5, State: "active", Depth: 0, Attempts: 1,
				Checkpoints: []CheckpointDto{
					{Id: "F7.1", Title: "Plan import", Status: "done"},
					{Id: "F7.2", Title: "Re-import diff", Status: "done"},
					{Id: "F7.3", Title: "Truth-gate tier", Status: "in_progress"},
					{Id: "F7.4", Title: "Gate caching by SHA", Status: "todo"},
					{Id: "F7.5", Title: "Speed program", Status: "todo"},
				},
			},
			{Id: "F8", Title: "conductor chat + Telegram v2", Done: 0, Total: 4, State: "todo", Depth: 0},
			{Id: "F9", Title: "Dogfood close", Done: 0, Total: 3, State: "todo", Depth: 0},
		},
		Gates: []GateDto{
			{Name: "build", State: "pass", ElapsedSec: 2.3},
			{Name: "test", State: "running", ElapsedSec: 4.1},
			{Name: "lint", State: "pending", ElapsedSec: 0},
			{Name: "audit", State: "pending", ElapsedSec: 0},
		},
	}
}

func makeFakeProcesses(now time.Time) []ProcessDto {
	started := now.Add(-95 * time.Second).UTC().Format(time.RFC3339)
	return []ProcessDto{
		{Pid: 4512, Purpose: "session", StageId: strPtr("F7"), Alive: true, StartedUtc: started, LastOutputLine: strPtr("[agent] Working on gate caching...")},
		{Pid: 8723, Purpose: "gate:test", StageId: strPtr("F7"), Alive: true, StartedUtc: started, LastOutputLine: strPtr("Running GateCacheTests... (12/12)")},
	}
}

func makeFakeTimeline() []TimelineEntryDto {
	cost := func(f float64) *float64 { return &f }
	num := func(n int) *int { return &n }
	return []TimelineEntryDto{
		{Utc: "2026-07-15T10:00:00Z", Kind: "stage", Description: "stage F7 entered", StageId: strPtr("F7")},
		{Utc: "2026-07-15T10:00:05Z", Kind: "session", Description: "session #11 Deliver started", StageId: strPtr("F7"), SessionNumber: num(11)},
		{Utc: "2026-07-15T10:03:40Z", Kind: "gate", Description: "gate test: FAIL (4100ms)", StageId: strPtr("F7"), Outcome: strPtr("fail")},
		{Utc: "2026-07-15T10:03:55Z", Kind: "session", Description: "session #11 finished: NeedsRetry", StageId: strPtr("F7"), SessionNumber: num(11), CostUsd: cost(0.18), Outcome: strPtr("NeedsRetry")},
		{Utc: "2026-07-15T10:04:10Z", Kind: "session", Description: "session #12 Deliver started", StageId: strPtr("F7"), SessionNumber: num(12)},
		{Utc: "2026-07-15T10:06:30Z", Kind: "gate", Description: "gate build: pass (2300ms)", StageId: strPtr("F7"), Outcome: strPtr("pass")},
		{Utc: "2026-07-15T10:07:00Z", Kind: "attention", Description: "needs human: verifier score 74 < 80"},
	}
}

// makeFakeLedger mirrors GET /ledger: recent `conductor note` entries injected into later prompts.
func makeFakeLedger() []LedgerEntryDto {
	sess := func(n int) *int { return &n }
	return []LedgerEntryDto{
		{Id: 5, SessionNumber: sess(12), StageId: strPtr("F7"), Kind: "finding", Content: "GateCache keys by (name, tier, sha) — the last-passing lookup must join attempts to find the most recent pass.", CreatedAt: "2026-07-15T10:05:00Z"},
		{Id: 4, SessionNumber: sess(11), StageId: strPtr("F7"), Kind: "trap", Content: "Never Stop-Process dotnet by name — it kills unrelated builds. Use conductor bg stop <pid>.", CreatedAt: "2026-07-15T10:03:10Z"},
		{Id: 3, SessionNumber: sess(11), StageId: strPtr("F7"), Kind: "decision", Content: "Cost of the verifier session is folded into the stage total under category='verify', not the deliver cost.", CreatedAt: "2026-07-15T10:02:40Z"},
		{Id: 2, SessionNumber: sess(8), StageId: strPtr("F6"), Kind: "observation", Content: "lipgloss v2 counts the border inside .Width(): inner content width is width−3 for a single-side border.", CreatedAt: "2026-07-15T09:40:00Z"},
	}
}

// makeFakeBugs mirrors GET /bugs (open-by-default): tracked bugs that outlive the session that filed them.
func makeFakeBugs() []BugDto {
	sess := func(n int) *int { return &n }
	return []BugDto{
		{Id: 3, Title: "console SSE resets line counter when a new session log appears", Detail: strPtr("StreamConsoleAsync resets `since=0` on path change — a reconnecting client re-replays the whole log."), Severity: "medium", Status: "open", StageId: strPtr("F7"), FoundSession: sess(12), CreatedAt: "2026-07-15T10:06:00Z"},
		{Id: 2, Title: "verifier double-counts session cost on resume", Detail: strPtr("TokenDelta folded twice when a session resumes after a stall."), Severity: "high", Status: "open", StageId: strPtr("F7"), FoundSession: sess(11), CreatedAt: "2026-07-15T10:01:00Z"},
	}
}

// makeFakeTelegramStatus starts "configured but not yet connected" — the most useful demo state,
// since it's the one that exercises the guided onboarding flow (paste token → add chat id → test)
// rather than a dashboard that's already fully wired up.
func makeFakeTelegramStatus() *TelegramStatusDto {
	return &TelegramStatusDto{
		Configured:          true,
		Started:             false,
		HasToken:            false,
		AllowedChatIds:      []string{},
		PollIntervalSeconds: 4,
		EnableTwoWay:        false,
	}
}

// makeFakeSessions mirrors the real wire order: GET /sessions returns newest-first
// (SqliteRunStore ORDER BY number DESC), so index 0 is the current session.
func makeFakeSessions() []SessionRowDto {
	// Costs/tokens mirror the real wire: SUMMED per session server-side, and session 8 deliberately
	// carries a real cost with ZERO tokens — the shape a pre-bug-#5 session honestly recorded — so
	// the Report digest and the Dev stats table are reviewed against that case, not only happy rows.
	return []SessionRowDto{
		{Number: 12, StageId: "F7", Kind: "Deliver", Outcome: nil, Attempt: 2, CommitCount: 0,
			StartedUtc: "2026-07-15T09:14:02Z", CostUsd: 0.12,
			TokensIn: 41213, TokensOut: 3187, TokensThink: 1024, TokensCache: 188420},
		{Number: 11, StageId: "F7", Kind: "Deliver", Outcome: strPtr("needsRetry"), Attempt: 1, CommitCount: 2, GateSummary: strPtr("build ✓ test ✗ lint ○"),
			ResultSummary: strPtr("Wired the **caching layer** in `RunDb` but `test` is still red — see the gate output."),
			StartedUtc:    "2026-07-15T08:31:10Z", EndedUtc: strPtr("2026-07-15T09:12:55Z"), CostUsd: 0.1408,
			TokensIn: 52881, TokensOut: 4402, TokensThink: 2310, TokensCache: 201338},
		{Number: 8, StageId: "F6", Kind: "Deliver", Outcome: strPtr("completed"), Attempt: 1, CommitCount: 1, GateSummary: strPtr("build ✓ test ✓"),
			StartedUtc: "2026-07-15T07:48:20Z", EndedUtc: strPtr("2026-07-15T08:29:44Z"), CostUsd: 0.0912},
		{Number: 2, StageId: "F1", Kind: "Deliver", Outcome: strPtr("completed"), Attempt: 1, CommitCount: 4, GateSummary: strPtr("build ✓ test ✓ lint ✓"),
			StartedUtc: "2026-07-15T07:02:05Z", EndedUtc: strPtr("2026-07-15T07:46:31Z"), CostUsd: 0.0405,
			TokensIn: 18904, TokensOut: 2733, TokensCache: 60112},
		{Number: 1, StageId: "F0", Kind: "Deliver", Outcome: strPtr("completed"), Attempt: 1, CommitCount: 3, GateSummary: strPtr("build ✓ test ✓"),
			StartedUtc: "2026-07-15T06:40:12Z", EndedUtc: strPtr("2026-07-15T07:00:58Z"), CostUsd: 0.0275,
			TokensIn: 9871, TokensOut: 1508, TokensCache: 22440},
	}
}

func strPtr(s string) *string {
	return &s
}

// HasWriteToken: the demo source accepts every write locally — there is no control plane to refuse
// one — so this is true rather than a fake "absent" that would send a reviewer token-hunting.
func (s *demoSource) HasWriteToken() bool { return true }
