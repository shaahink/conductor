package tui

// Golden rendering tests: render View() against fixed, deterministic state and diff the
// ANSI-stripped output against a committed snapshot. This is also how a maintainer (or an
// agent with no real TTY) can actually see what a frame looks like: run
//
//	go test ./internal/tui/ -run TestGolden -v
//
// and read the PASS output, or `-update` to refresh testdata/golden/*.golden after an
// intentional layout change.

import (
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"testing"
	"time"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

// TestMain pins the render timezone: frames stamp wall-clocks in widgets.ClockLocation (local for
// humans), which would make golden output depend on the machine running the tests.
func TestMain(m *testing.M) {
	widgets.ClockLocation = time.UTC
	os.Exit(m.Run())
}

var updateGolden = flag.Bool("update", false, "update golden files")

var ansiRe = regexp.MustCompile(`\x1b\[[0-9;?]*[a-zA-Z]`)

func stripANSI(s string) string {
	return ansiRe.ReplaceAllString(s, "")
}

// fakeSource is a static, non-networked api.DataSource — golden output must never depend on
// wall-clock time, random ports, or the ticking demo simulation.
type fakeSource struct{}

func (fakeSource) FetchState() (*api.StateDto, error) { return nil, nil }
func (fakeSource) FetchTasks() (*api.TasksDto, error) { return nil, nil }
func (fakeSource) PostTaskUpdate(req api.TaskUpdateRequestDto) (*api.TaskWriteResultDto, error) {
	return &api.TaskWriteResultDto{Ok: true, TaskId: &req.TaskId, Status: &req.Status}, nil
}
func (fakeSource) PostTaskAdd(req api.TaskAddRequestDto) (*api.TaskWriteResultDto, error) {
	id := req.CheckpointId + "-a9"
	status := "todo"
	return &api.TaskWriteResultDto{Ok: true, TaskId: &id, Status: &status, Order: 9}, nil
}
func (fakeSource) FetchPromptBlocks(taskId string) (*api.PromptBlocksDto, error) {
	// Fixed composition for the card-detail golden: every block kind, editable ones marked.
	return &api.PromptBlocksDto{
		Ok: true, TaskId: taskId, CheckpointId: "F7.4", StageId: "F7",
		Blocks: []api.PromptBlockDto{
			{Kind: "persona", Label: "Persona — deliver", Content: "You are a delivery engineer.", Editable: false},
			{Kind: "stageNotes", Label: "Stage notes — F7", Content: "Gate caching stage: reuse GateRunner.", Editable: false},
			{Kind: "taskTitle", Label: "Task title", Content: "Wire RunDb.GetLastPassingGateResult", Editable: true},
			{Kind: "taskContext", Label: "Extra context (task-scoped)", Content: "", Editable: true},
			{Kind: "knowledge", Label: "Injected knowledge", Content: "## Ledger\n- goldens live in testdata", Editable: false},
			{Kind: "tools", Label: "Tool contract", Content: "conductor note / bg / task --done", Editable: false},
		},
	}, nil
}
func (fakeSource) PostTaskEdit(req api.TaskEditRequestDto) (*api.TaskWriteResultDto, error) {
	status := "in_progress"
	return &api.TaskWriteResultDto{Ok: true, TaskId: &req.TaskId, Status: &status, Title: req.Title}, nil
}
func (fakeSource) PostTaskRefine(req api.TaskRefineRequestDto) (*api.TaskRefineResultDto, error) {
	title, context, interp := "Refined title", "Refined context", "fake-advisor"
	return &api.TaskRefineResultDto{Ok: true, TaskId: &req.TaskId, Title: &title, Context: &context, Interpreter: &interp}, nil
}
func (fakeSource) FetchProcesses() (*api.ProcessesDto, error) { return nil, nil }
func (fakeSource) FetchSessions() (*api.SessionsDto, error)   { return nil, nil }
func (fakeSource) FetchTimeline() (*api.TimelineDto, error)   { return nil, nil }
func (fakeSource) FetchLedger() (*api.LedgerDto, error)       { return fixedLedger(), nil }
func (fakeSource) FetchBugs() (*api.BugsDto, error)           { return fixedBugs(), nil }
func (fakeSource) PostNote(api.NoteRequestDto) (*api.KnowledgeWriteResultDto, error) {
	return &api.KnowledgeWriteResultDto{Ok: true}, nil
}
func (fakeSource) PostBug(api.BugNewRequestDto) (*api.KnowledgeWriteResultDto, error) {
	id := int64(9)
	return &api.KnowledgeWriteResultDto{Ok: true, Id: &id}, nil
}
func (fakeSource) PostBugResolve(api.BugResolveRequestDto) (*api.KnowledgeWriteResultDto, error) {
	return &api.KnowledgeWriteResultDto{Ok: true}, nil
}
func (fakeSource) FetchPromptPreview(_, _ string) (*api.PromptPreviewDto, error) {
	return nil, nil
}
func (fakeSource) QueryReport(sql string) (*api.QueryResultDto, error) { return nil, nil }
func (fakeSource) HasWriteToken() bool                                 { return true }
func (fakeSource) PostControl(api.ControlRequestDto) (*api.ControlAcceptedDto, error) {
	return &api.ControlAcceptedDto{Accepted: true}, nil
}
func (fakeSource) PostInject(api.InjectRequestDto) (*api.InjectAcceptedDto, error) {
	return &api.InjectAcceptedDto{Accepted: true}, nil
}
func (fakeSource) PostProcessKill(api.ProcessKillRequestDto) (*api.ProcessKillResultDto, error) {
	return &api.ProcessKillResultDto{Ok: true}, nil
}
func (fakeSource) FetchPlan() (*api.PlanDto, error) { return fixedPlan(), nil }
func (fakeSource) PostPlanEdit(api.PlanEditRequestDto) (*api.PlanMutationResultDto, error) {
	return &api.PlanMutationResultDto{Ok: true, PlanVersion: 8}, nil
}
func (fakeSource) PostPlanImport(req api.PlanImportRequestDto) (*api.PlanImportResultDto, error) {
	return &api.PlanImportResultDto{Ok: true, Applied: req.Apply, PlanVersion: 8, Diff: api.PlanDiffDto{
		AddedStages: []api.PlanStageDto{{Id: "M7", Title: "Knowledge that compounds", Sessions: 3, Kind: "deliver", DependsOn: []string{"M6"}}},
	}}, nil
}
func (fakeSource) FetchTelegramStatus() (*api.TelegramStatusDto, error) {
	return fixedTelegramStatus(), nil
}
func (fakeSource) PostTelegramTest() (*api.TelegramTestResultDto, error) {
	name := "conductor_test_bot"
	return &api.TelegramTestResultDto{Ok: true, BotUsername: &name}, nil
}
func (fakeSource) PostTelegramToken(api.TelegramSetTokenRequestDto) (*api.TelegramSetTokenResultDto, error) {
	msg := "saved — restart conductor to connect with the new token"
	return &api.TelegramSetTokenResultDto{Ok: true, Message: &msg}, nil
}
func (fakeSource) SubscribeEvents(_ func(api.ConductorEventDto), onConnected func(bool)) func() {
	onConnected(true)
	return func() {}
}
func (fakeSource) SubscribeTranscript(_ func(api.TranscriptLineDto), onConnected func(bool)) func() {
	onConnected(true)
	return func() {}
}
func (fakeSource) SubscribeConsole(_ func(api.ConsoleLineDto), onConnected func(bool)) func() {
	onConnected(true)
	return func() {}
}
func (fakeSource) Close() {}

func keyMsg(text string) tea.KeyPressMsg {
	return tea.KeyPressMsg(tea.Key{Text: text, Code: rune(text[0])})
}

func specialKey(code rune) tea.KeyPressMsg {
	return tea.KeyPressMsg(tea.Key{Code: code})
}

func ctrlKey(r rune) tea.KeyPressMsg {
	return tea.KeyPressMsg(tea.Key{Code: r, Mod: tea.ModCtrl})
}

func fixedLedger() *api.LedgerDto {
	s := func(n int) *int { return &n }
	st := func(x string) *string { return &x }
	return &api.LedgerDto{Entries: []api.LedgerEntryDto{
		{Id: 5, SessionNumber: s(12), StageId: st("F7"), Kind: "finding", Content: "GateCache keys by (name, tier, sha) — join attempts for the last pass.", CreatedAt: "2026-07-15T10:05:00Z"},
		{Id: 4, SessionNumber: s(11), StageId: st("F7"), Kind: "trap", Content: "Never Stop-Process dotnet by name — use conductor bg stop <pid>.", CreatedAt: "2026-07-15T10:03:10Z"},
	}}
}

func fixedBugs() *api.BugsDto {
	s := func(n int) *int { return &n }
	st := func(x string) *string { return &x }
	d := func(x string) *string { return &x }
	return &api.BugsDto{Bugs: []api.BugDto{
		{Id: 3, Title: "console SSE resets line counter on new session log", Detail: d("since=0 reset re-replays the whole log"), Severity: "medium", Status: "open", StageId: st("F7"), FoundSession: s(12), CreatedAt: "2026-07-15T10:06:00Z"},
		{Id: 2, Title: "verifier double-counts session cost on resume", Severity: "high", Status: "open", StageId: st("F7"), FoundSession: s(11), CreatedAt: "2026-07-15T10:01:00Z"},
	}}
}

func fixedTelegramStatus() *api.TelegramStatusDto {
	return &api.TelegramStatusDto{
		Configured:          true,
		Started:             false,
		HasToken:            false,
		AllowedChatIds:      []string{},
		PollIntervalSeconds: 4,
		EnableTwoWay:        false,
	}
}

func fixedState() *api.StateDto {
	persona := "architect"
	return &api.StateDto{
		PlanName:               "conductor-foreman",
		Status:                 "Running",
		StageId:                "F7",
		StageTitle:             "Gate caching + truth gates + speed program",
		Persona:                &persona,
		DoneCount:              2,
		TotalCount:             40,
		TotalCostUsd:           0.42,
		OverheadCostUsd:        0.02,
		TokensInput:            2500,
		TokensOutput:           1800,
		TokensReasoning:        900,
		CurrentCheckpoint:      "F7.3",
		CurrentCheckpointTitle: "Wire caching layer",
		RunId:                  "demo-run-id",
		Repo:                   `C:\Code\conductor`,
		PlanDir:                `C:\Code\conductor\plans`,
		Tracker:                "CONDUCTOR-VNEXT-PLAN.md",
		StateDir:               `C:\Code\conductor\.conductor`,
		SessionNumber:          12,
		SessionKind:            "Deliver",
		Model:                  "claude-opus-4-8",
		Provider:               "claude",
		Attempt:                1,
		MaxAttempts:            3,
		SessionElapsedSec:      41,
		AgentActive:            true,
		SessionCostUsd:         0.12,
		SessionTokensInput:     3400,
		SessionTokensOutput:    1900,
		SessionTokensReasoning: 800,
		Stages: []api.StageDto{
			{Id: "F0", Title: "Foundations", Done: 3, Total: 3, State: "confirmed"},
			{Id: "F6", Title: "Ink TUI v1", Done: 5, Total: 5, State: "confirmed"},
			{Id: "F7", Title: "Gate caching + truth gates", Done: 2, Total: 5, State: "active", Attempts: 1, CostUsd: 0.30,
				Checkpoints: []api.CheckpointDto{
					{Id: "F7.1", Title: "Plan import", Status: "done"},
					{Id: "F7.2", Title: "Re-import diff", Status: "done"},
					{Id: "F7.3", Title: "Truth-gate tier", Status: "in_progress"},
				},
			},
			{Id: "F8", Title: "conductor chat + Telegram v2", Done: 0, Total: 4, State: "todo"},
		},
		Gates: []api.GateDto{
			{Name: "build", State: "pass", ElapsedSec: 2.3},
			{Name: "test", State: "running", ElapsedSec: 4.1},
			{Name: "lint", State: "pending"},
		},
	}
}

func fixedPlan() *api.PlanDto {
	sp := func(s string) *string { return &s }
	return &api.PlanDto{
		Name:            "conductor-foreman",
		PlanVersion:     7,
		PlanFile:        `C:\Code\conductor\plans\conductor-foreman.plan.json`,
		GatePolicy:      "perPhase",
		DefaultWorkflow: "deliver-verify",
		DefaultModel:    "claude-opus-4-8",
		Workflows:       []string{"deliver-verify", "big-dev-then-big-audit", "docs-only", "spike"},
		Stages: []api.PlanStageDto{
			{Id: "F6", Title: "Ink TUI v1", Sessions: 5, Kind: "deliver", Model: sp("claude-opus-4-8"), Workflow: sp("deliver-verify"), DependsOn: []string{"F5"}},
			{Id: "F7", Title: "Gate caching + truth gates", Sessions: 5, Kind: "deliver", Model: sp("claude-opus-4-8"), Workflow: sp("big-dev-then-big-audit"), DependsOn: []string{"F6"}},
			{Id: "F8", Title: "conductor chat + Telegram v2", Sessions: 4, Kind: "deliver", Model: sp("claude-sonnet-5"), Workflow: sp("deliver-verify"), DependsOn: []string{"F7"}},
		},
		Gates: []api.PlanGateDto{
			{Name: "build", Command: "dotnet build Conductor.slnx", Tier: "fast", TimeoutMinutes: 10},
			{Name: "test", Command: "dotnet test Conductor.slnx", Tier: "full", TimeoutMinutes: 20},
			{Name: "ratchet", Command: "dotnet test --filter Category=Architecture", Tier: "truth", TimeoutMinutes: 15},
		},
		// Caps are set so Home's budget/headroom rows are actually exercised by the goldens.
		Limits: api.PlanLimitsDto{
			StallMinutes: 12, SessionTimeoutMinutes: 240, VerifierThreshold: 80,
			MaxRunCostUsd: f64Ptr(10), MaxRunTokens: i64Ptr(2_000_000),
		},
	}
}

func f64Ptr(f float64) *float64 { return &f }
func i64Ptr(n int64) *int64     { return &n }

func fixedTranscript() []api.TranscriptLineDto {
	// Fixed UTC timestamps: the transcript renders a wall-clock prefix, and goldens must not
	// depend on the machine's clock or timezone.
	at := func(sec int) time.Time { return time.Date(2026, 7, 15, 10, 4, sec, 0, time.UTC) }
	return []api.TranscriptLineDto{
		{Seq: 1, Ts: at(1), SessionId: "12", Kind: "system", Text: "Session #12 started · Deliver · Stage F7 · Attempt 1"},
		{Seq: 2, Ts: at(9), SessionId: "12", Kind: "thinking", Text: "Let me examine the GateCache implementation..."},
		{Seq: 3, Ts: at(14), SessionId: "12", Kind: "tool", Text: "read src/Conductor/Core/Gating/GateCache.cs"},
		{Seq: 4, Ts: at(15), SessionId: "12", Kind: "result", Text: "GateCache.cs:142 lines — caches by (name, tier, sha)"},
		{Seq: 5, Ts: at(22), SessionId: "12", Kind: "agent", Text: "Found the caching layer. Adding GetLastPassingGateResult to RunDb."},
	}
}

func fixedTasks() []api.TaskDto {
	return []api.TaskDto{
		{TaskId: "T1", CheckpointId: "F7.3", Title: "Add truth-tier config to GateConfig", Status: "done", Source: "planner", Order: 1},
		{TaskId: "T2", CheckpointId: "F7.3", Title: "Wire RunDb.GetLastPassingGateResult", Status: "in_progress", Source: "agent", Order: 2},
		{TaskId: "T3", CheckpointId: "F7.4", Title: "Cache gate results by (name, tier, sha)", Status: "todo", Source: "planner", Order: 3},
	}
}

// kanbanFixtureTasks populates all three board columns (plus a skipped card folded into Done) —
// the G2.2 gate wants a golden with three populated columns and the move/add affordances.
func kanbanFixtureTasks() []api.TaskDto {
	return []api.TaskDto{
		{TaskId: "T1", CheckpointId: "F7.3", Title: "Add truth-tier config to GateConfig", Status: "done", Source: "planner", Order: 1},
		{TaskId: "T2", CheckpointId: "F7.3", Title: "Wire RunDb.GetLastPassingGateResult", Status: "in_progress", Source: "agent", Order: 2},
		{TaskId: "T3", CheckpointId: "F7.4", Title: "Cache gate results by (name, tier, sha)", Status: "todo", Source: "planner", Order: 3},
		{TaskId: "T4", CheckpointId: "F7.4", Title: "Add SkipIfFresh timestamp check", Status: "todo", Source: "human", Order: 4,
			// PF3: declared paths — the detail golden shows the filled claims line.
			Paths: []string{"src/Conductor/Core/Gating/GateCache.cs", "docs/CONDUCTOR-VNEXT-PLAN.md"}},
		{TaskId: "T5", CheckpointId: "F7.5", Title: "Benchmark the warm path", Status: "skipped", Source: "agent", Order: 5},
	}
}

func fixedTimeline() []api.TimelineEntryDto {
	cost := func(f float64) *float64 { return &f }
	num := func(n int) *int { return &n }
	return []api.TimelineEntryDto{
		{Utc: "2026-07-15T10:00:00Z", Kind: "stage", Description: "stage F7 entered", StageId: strPtr("F7")},
		{Utc: "2026-07-15T10:00:05Z", Kind: "session", Description: "session #11 Deliver started", StageId: strPtr("F7"), SessionNumber: num(11)},
		{Utc: "2026-07-15T10:03:40Z", Kind: "gate", Description: "gate test: FAIL (4100ms)", StageId: strPtr("F7"), Outcome: strPtr("fail")},
		{Utc: "2026-07-15T10:03:55Z", Kind: "session", Description: "session #11 finished: NeedsRetry", StageId: strPtr("F7"), SessionNumber: num(11), CostUsd: cost(0.18), Outcome: strPtr("NeedsRetry")},
		{Utc: "2026-07-15T10:07:00Z", Kind: "attention", Description: "needs human: verifier score 74 < 80"},
	}
}

// newGoldenModel builds a Model with fixed plan/gate/transcript state and a known terminal size,
// without ever calling Init() — golden output must not depend on background polling or SSE.
func newGoldenModel(width, height int) tea.Model {
	var m tea.Model = New(fakeSource{}, false, "http://127.0.0.1:4317")
	m, _ = m.Update(tea.WindowSizeMsg{Width: width, Height: height})
	m, _ = m.Update(MsgStateUpdated{State: fixedState()})
	// Init() fetches the plan up front for the Plan tab; Home reads it too (plan file + budget caps),
	// so the fixture mirrors that rather than rendering a Home the real Face never shows.
	m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
	// Init() also subscribes to both SSE streams. Without these a "live, connected" fixture renders
	// Home's stream dots RED — an attached run that looks half-broken. Mirror the real wire.
	m, _ = m.Update(MsgEventsConnChanged{Connected: true})
	m, _ = m.Update(MsgTxConnChanged{Connected: true})
	m, _ = m.Update(MsgProcessesUpdated{Procs: &api.ProcessesDto{Processes: []api.ProcessDto{
		// ExitedUtc is set on both (even the "alive" one) purely so the rendered runtime is a
		// fixed value — formatProcessRuntime falls back to time.Now() for genuinely-alive
		// processes, which would make this golden file flake with wall-clock time otherwise.
		{Pid: 4512, Purpose: "session", StageId: strPtr("F7"), Alive: true, StartedUtc: "2026-07-15T10:00:00Z", ExitedUtc: strPtr("2026-07-15T10:04:32Z"), LastOutputLine: strPtr("[agent] Working on gate caching...")},
		{Pid: 8723, Purpose: "gate:test", StageId: strPtr("F7"), Alive: false, StartedUtc: "2026-07-15T10:01:00Z", ExitedUtc: strPtr("2026-07-15T10:01:19Z"), LastOutputLine: strPtr("Running GateCacheTests... (12/12)")},
	}}})
	// Newest-first, mirroring the real GET /sessions (ORDER BY number DESC). Started/Ended and the
	// summed cost/token columns are fixed strings, never a clock, so the Report digest (U2.2) renders
	// a real duration and cost in a golden without flaking. Session 8 carries a real cost with ZERO
	// tokens on purpose: that is what a session recorded before bug #5 honestly looks like, and the
	// report must show it rather than dress it up.
	m, _ = m.Update(MsgSessionsUpdated{Sessions: &api.SessionsDto{Sessions: []api.SessionRowDto{
		{Number: 12, StageId: "F7", Kind: "Deliver", Outcome: nil, Attempt: 2, CommitCount: 0,
			StartedUtc: "2026-07-15T10:00:00Z", CostUsd: 0.12,
			TokensIn: 41213, TokensOut: 3187, TokensThink: 1024, TokensCache: 188420},
		{Number: 11, StageId: "F7", Kind: "Deliver", Outcome: strPtr("needsRetry"), Attempt: 1, CommitCount: 2,
			GateSummary:   strPtr("build ✓ test ✗ lint ○"),
			ResultSummary: strPtr("Wired the **caching layer** in `RunDb` but `test` still red — see gate output."),
			StartedUtc:    "2026-07-15T09:12:30Z", EndedUtc: strPtr("2026-07-15T09:58:04Z"), CostUsd: 0.1408,
			TokensIn: 52881, TokensOut: 4402, TokensThink: 2310, TokensCache: 201338},
		{Number: 8, StageId: "F6", Kind: "Deliver", Outcome: strPtr("completed"), Attempt: 1, CommitCount: 1,
			GateSummary: strPtr("build ✓ test ✓"),
			StartedUtc:  "2026-07-15T08:30:00Z", EndedUtc: strPtr("2026-07-15T09:11:12Z"), CostUsd: 0.0912},
	}}})
	m, _ = m.Update(MsgTasksUpdated{Tasks: &api.TasksDto{Tasks: fixedTasks()}})
	for _, tx := range fixedTranscript() {
		m, _ = m.Update(MsgTranscriptLine{Line: tx})
	}
	return m
}

func strPtr(s string) *string { return &s }

// openKanbanDetailGolden walks to the second card and opens its detail, feeding the fixed block
// composition the way the live poll would (commands are never executed in golden runs).
func openKanbanDetailGolden(m tea.Model) tea.Model {
	m, _ = m.Update(keyMsg("b"))
	m, _ = m.Update(MsgTasksUpdated{Tasks: &api.TasksDto{Tasks: kanbanFixtureTasks()}})
	m, _ = m.Update(specialKey(tea.KeyDown)) // → T4, the human-sourced TODO card
	m, _ = m.Update(specialKey(tea.KeyEnter))
	blocks, _ := fakeSource{}.FetchPromptBlocks("T4")
	m, _ = m.Update(MsgPromptBlocks{Blocks: blocks})
	return m
}

func TestGolden(t *testing.T) {
	cases := []struct {
		name string
		do   func(m tea.Model) tea.Model
	}{
		// Home is the tab the Face opens on (U1.1), so "default" IS the landing page, connected.
		{"default", func(m tea.Model) tea.Model { return m }},
		{"agent", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("a"))
			return m
		}},
		// U3.3: the SAME transcript under the opencode provider — the tool/result/thinking glyphs
		// switch to opencode's vocabulary (◆/└/◇) where the claude frame shows ●/⎿/✻. The spec asks
		// for a golden of BOTH renderings so a change to one provider's glyphs can never silently
		// swap the other's.
		{"agent_opencode", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("a"))
			st := fixedState()
			st.Provider = "opencode"
			m, _ = m.Update(MsgStateUpdated{State: st})
			return m
		}},
		{"sidebar_collapsed", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("\\"))
			return m
		}},
		{"palette", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg(":"))
			return m
		}},
		{"palette_confirm", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg(":"))
			for _, ch := range "abort" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			m, _ = m.Update(specialKey(tea.KeyEnter))
			return m
		}},
		{"palette_goto", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg(":"))
			for _, ch := range "goto" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			m, _ = m.Update(specialKey(tea.KeyEnter))
			return m
		}},
		{"inject", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("i"))
			for _, ch := range "please retry with cache warm" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			return m
		}},
		{"templates", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("e"))
			return m
		}},
		{"templates_edit", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("e"))
			m, _ = m.Update(specialKey(tea.KeyEnter)) // edit the first template (empty on disk here)
			for _, ch := range "SESSION prompt" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			return m
		}},
		{"sessions", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("s"))
			return m
		}},
		// U2.2: Report is the rendered run report now. The scores section is the one part that comes
		// off a canned query, so it is injected the way the real fetch would land it.
		{"report", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("r"))
			m, _ = m.Update(MsgReportScores{Result: &api.QueryResultDto{
				Columns: []string{"session_number", "score", "verdict"},
				Rows: []api.QueryRowDto{
					{Values: []string{"11", "66", "WARN"}},
					{Values: []string{"8", "90", "PASS"}},
				},
			}})
			return m
		}},
		// The report is taller than the pane, so the sections the owner scrolls TO (gates, verifier
		// scores) are only pinned by a scrolled frame. Without this the scores section would sit
		// below the fold in every golden and could rot unseen.
		{"report_scrolled", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("r"))
			m, _ = m.Update(MsgReportScores{Result: &api.QueryResultDto{
				Columns: []string{"session_number", "score", "verdict"},
				Rows: []api.QueryRowDto{
					{Values: []string{"11", "66", "WARN"}},
					{Values: []string{"8", "90", "PASS"}},
				},
			}})
			for i := 0; i < 6; i++ {
				m, _ = m.Update(keyMsg("down"))
			}
			return m
		}},
		// U2.2: the SQL console, unchanged, on its new tab — this scenario is the old "report" one.
		{"dev", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("d"))
			m, _ = m.Update(MsgReportResult{Result: &api.QueryResultDto{
				Columns: []string{"stage_id", "cost_usd"},
				Rows: []api.QueryRowDto{
					{Values: []string{"F1", "0.42"}},
					{Values: []string{"F7", "0.08"}},
				},
			}})
			return m
		}},
		// U2.3: the run internals + per-session stats live below the console's result grid, so the
		// bottom of the pane is the only place they can be pinned — including the note that names
		// bug #5 for the cost-with-zero-tokens session.
		{"dev_scrolled", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("d"))
			m, _ = m.Update(MsgReportResult{Result: &api.QueryResultDto{
				Columns: []string{"stage_id", "cost_usd"},
				Rows: []api.QueryRowDto{
					{Values: []string{"F1", "0.42"}},
					{Values: []string{"F7", "0.08"}},
				},
			}})
			mm := m.(Model)
			mm.reportFocusQuery = false // the editor owns every key while focused
			m, _ = mm.Update(keyMsg("pgdown"))
			return m
		}},
		{"processes", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("o"))
			return m
		}},
		{"processes_kill", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("o")) // Procs tab
			m, _ = m.Update(keyMsg("x")) // kill confirm for the selected (alive) process
			return m
		}},
		{"timeline", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("t"))
			m, _ = m.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: fixedTimeline()}})
			return m
		}},
		// U3.2 / dogfood appendix 6: the attach fetch is history, everything after it is live, and
		// the rule between them is what stops an attach reading as an event storm.
		{"timeline_live", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("t"))
			hist := fixedTimeline()
			m, _ = m.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: hist}})
			m, _ = m.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: append(append([]api.TimelineEntryDto{}, hist...),
				api.TimelineEntryDto{Kind: "session", Description: "session #12 Deliver started", Utc: "2026-07-15T10:09:00Z"},
			)}})
			return m
		}},
		{"knowledge", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("k"))
			m, _ = m.Update(MsgKnowledgeUpdated{Ledger: fixedLedger(), Bugs: fixedBugs()})
			return m
		}},
		{"knowledge_note", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("k"))
			m, _ = m.Update(MsgKnowledgeUpdated{Ledger: fixedLedger(), Bugs: fixedBugs()})
			m, _ = m.Update(keyMsg("n")) // file-a-note input
			for _, ch := range "warm the cache first" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			return m
		}},
		{"telegram_unconfigured", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("g"))
			m, _ = m.Update(MsgTelegramStatusUpdated{Status: fixedTelegramStatus()})
			return m
		}},
		{"telegram_token_edit", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("g"))
			m, _ = m.Update(MsgTelegramStatusUpdated{Status: fixedTelegramStatus()})
			m, _ = m.Update(specialKey(tea.KeyEnter)) // begin editing the "bot token" field (row 0)
			m, _ = m.Update(keyMsg("1"))
			m, _ = m.Update(keyMsg("2"))
			m, _ = m.Update(keyMsg("3"))
			return m
		}},
		{"telegram_connected", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("g"))
			name := "conductor_bot"
			now := "2026-07-15T10:05:00Z"
			m, _ = m.Update(MsgTelegramStatusUpdated{Status: &api.TelegramStatusDto{
				Configured: true, Started: true, HasToken: true,
				AllowedChatIds: []string{"111222333"}, PollIntervalSeconds: 4,
				BotUsername: &name, LastPollUtc: &now,
			}})
			return m
		}},
		{"console", func(m tea.Model) tea.Model {
			for i, raw := range []string{
				`{"type":"system","subtype":"init","session_id":"s12","model":"deepseek-v4-pro"}`,
				`{"type":"assistant","message":{"content":[{"type":"text","text":"Examining GateCache..."}]}}`,
				`{"type":"result","subtype":"success","total_cost_usd":0.12,"num_turns":4}`,
			} {
				m, _ = m.Update(MsgConsoleLine{Line: api.ConsoleLineDto{Seq: int64(i + 1), Text: raw}})
			}
			m, _ = m.Update(keyMsg("c"))
			return m
		}},
		{"templates_preview", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("e"))
			m, _ = m.Update(keyMsg("v"))
			m, _ = m.Update(MsgPromptPreview{Preview: &api.PromptPreviewDto{
				Model: "deepseek/deepseek-v4-pro",
				Kind:  "Deliver",
				Prompt: "# Deliver session — stage F7\n\nYou are the conductor's delivery agent. Land the " +
					"checkpoints for stage F7.\n\n## Tools\nconductor note / bg / task --done <id> --evidence <path>",
			}})
			return m
		}},
		{"plan_stages", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			return m
		}},
		{"plan_stage_fields", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyDown))  // select F7
			m, _ = m.Update(specialKey(tea.KeyEnter)) // drill into fields
			return m
		}},
		{"plan_stage_model_edit", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyEnter)) // drill F6
			m, _ = m.Update(specialKey(tea.KeyDown))  // → model field
			m, _ = m.Update(specialKey(tea.KeyEnter)) // begin editing the enum
			return m
		}},
		{"plan_gates", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyRight)) // → Gates section
			return m
		}},
		{"plan_settings", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyRight))
			m, _ = m.Update(specialKey(tea.KeyRight))
			return m
		}},
		{"plan_import_diff", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyRight))
			m, _ = m.Update(specialKey(tea.KeyRight))
			m, _ = m.Update(specialKey(tea.KeyRight)) // → Import section
			m, _ = m.Update(MsgPlanImported{Result: &api.PlanImportResultDto{Ok: true, Diff: api.PlanDiffDto{
				AddedStages: []api.PlanStageDto{
					{Id: "M8", Title: "AFK and smart setup", Sessions: 3, Kind: "deliver", DependsOn: []string{"M7"}},
				},
				ChangedStages: []api.PlanStageChangeDto{
					{Id: "F7", Fields: []api.PlanFieldChangeDto{{Field: "sessions", Old: strPtr("5"), New: strPtr("6")}}},
				},
			}}})
			return m
		}},
		{"plan_stage_add", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(keyMsg("n")) // open the add-stage form
			for _, ch := range "F9" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			m, _ = m.Update(specialKey(tea.KeyTab)) // → title field
			for _, ch := range "New phase" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			return m
		}},
		{"plan_gate_add", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyRight)) // → Gates section
			m, _ = m.Update(keyMsg("n"))              // open the add-gate form
			for _, ch := range "lint" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			m, _ = m.Update(specialKey(tea.KeyTab)) // → command field
			for _, ch := range "dotnet format" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			return m
		}},
		{"plan_stage_delete", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyDown)) // select F7
			m, _ = m.Update(keyMsg("d"))             // delete confirm
			return m
		}},
		{"kanban", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("b"))
			m, _ = m.Update(MsgTasksUpdated{Tasks: &api.TasksDto{Tasks: kanbanFixtureTasks()}})
			m, _ = m.Update(specialKey(tea.KeyDown)) // select the second card
			return m
		}},
		// U3.2 / dogfood appendix 5: a board that cannot reach /tasks must say so. This used to render
		// the same "No tasks yet" as a genuinely empty board — a confident claim, and a false one.
		{"kanban_unreachable", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("b"))
			m, _ = m.Update(MsgTasksUpdated{Err: errors.New(
				"Get \"http://127.0.0.1:4317/tasks\": dial tcp 127.0.0.1:4317: connection refused")})
			return m
		}},
		{"kanban_add", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("b"))
			m, _ = m.Update(MsgTasksUpdated{Tasks: &api.TasksDto{Tasks: kanbanFixtureTasks()}})
			m, _ = m.Update(keyMsg("n"))
			for _, ch := range "wire the goldens" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			return m
		}},
		{"kanban_detail", func(m tea.Model) tea.Model {
			// P3: enter on a card → its prompt as labeled building blocks (✎ marks editable ones).
			m = openKanbanDetailGolden(m)
			return m
		}},
		{"kanban_detail_ctx_edit", func(m tea.Model) tea.Model {
			m = openKanbanDetailGolden(m)
			m, _ = m.Update(keyMsg("c")) // edit the task-scoped extra context
			for _, ch := range "cover the cache-miss path" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			return m
		}},
		{"kanban_detail_proposal", func(m tea.Model) tea.Model {
			m = openKanbanDetailGolden(m)
			title, context, interp := "Wire RunDb.GetLastPassingGateResult (with eviction test)",
				"Start from the smallest end-to-end slice.", "fake-advisor"
			m, _ = m.Update(MsgTaskRefined{Result: &api.TaskRefineResultDto{
				Ok: true, TaskId: strPtr("T4"), Title: &title, Context: &context, Interpreter: &interp,
			}})
			return m
		}},
		{"plan_prompt", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			for range 4 { // → Prompt section
				m, _ = m.Update(specialKey(tea.KeyRight))
			}
			for _, ch := range "add a lint gate that runs dotnet format" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			return m
		}},
		{"plan_prompt_diff", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			for range 4 {
				m, _ = m.Update(specialKey(tea.KeyRight))
			}
			m, _ = m.Update(MsgPlanImported{Result: &api.PlanImportResultDto{Ok: true, Interpreter: strPtr("claude-fable-5"), Diff: api.PlanDiffDto{
				AddedGates: []api.PlanGateDto{
					{Name: "lint", Command: "dotnet format --verify-no-changes", Tier: "fast", TimeoutMinutes: 5},
				},
			}}})
			return m
		}},
		{"search", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("a")) // `/` searches the transcript — an Agent-tab affordance
			m, _ = m.Update(keyMsg("/"))
			for _, ch := range "gate" {
				m, _ = m.Update(keyMsg(string(ch)))
			}
			return m
		}},
		{"help", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("?"))
			return m
		}},
		{"attention", func(m tea.Model) tea.Model {
			st := fixedState()
			st.Status = "NeedsAttention"
			reason := "verifier score 74 < 80 — see session #12 findings"
			st.AttentionReason = &reason
			st.AgentActive = false
			m, _ = m.Update(MsgStateUpdated{State: st})
			return m
		}},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			m := newGoldenModel(110, 34)
			m = tc.do(m)
			checkGolden(t, tc.name, stripANSI(m.View().Content))
		})
	}
}

// TestGoldenHomeDisconnected is the frame a person actually lands on with no engine running: Home,
// live mode, nothing attached. U1.1 folds the old splash's how-to-start into the Server panel, so this
// is the screen that has to explain itself with no data at all.
func TestGoldenHomeDisconnected(t *testing.T) {
	var m tea.Model = New(fakeSource{}, false, "http://127.0.0.1:4317")
	m, _ = m.Update(tea.WindowSizeMsg{Width: 110, Height: 34})
	checkGolden(t, "home_disconnected", stripANSI(m.View().Content))
}

// TestGoldenHomeDemo is the same landing in --demo: the tour a reviewer sees with no engine and no
// spend, which must fill every Home panel rather than degrading to dashes.
func TestGoldenHomeDemo(t *testing.T) {
	var m tea.Model = New(fakeSource{}, true, "http://127.0.0.1:4317")
	m, _ = m.Update(tea.WindowSizeMsg{Width: 110, Height: 34})
	m, _ = m.Update(MsgStateUpdated{State: fixedState()})
	m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
	checkGolden(t, "home_demo", stripANSI(m.View().Content))
}

// TestGoldenSplash renders the Agent tab's empty state (no run attached) — still reachable with `a`
// before an engine is up, even though Home is now the landing.
func TestGoldenSplash(t *testing.T) {
	var m tea.Model = New(fakeSource{}, false, "http://127.0.0.1:4317")
	m, _ = m.Update(tea.WindowSizeMsg{Width: 110, Height: 34})
	m, _ = m.Update(keyMsg("a"))
	checkGolden(t, "splash", stripANSI(m.View().Content))
}

// checkGolden diffs got against testdata/golden/<name>.golden (or writes it under -update) and always
// prints the frame so a human/agent with no TTY can read it.
func checkGolden(t *testing.T, name, got string) {
	t.Helper()
	goldenPath := filepath.Join("testdata", "golden", name+".golden")
	if *updateGolden {
		if err := os.MkdirAll(filepath.Dir(goldenPath), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(goldenPath, []byte(got), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	want, err := os.ReadFile(goldenPath)
	if err != nil {
		t.Fatalf("reading golden file (run with -update to create it): %v", err)
	}
	if got != string(want) {
		t.Errorf("golden mismatch for %s\n--- got ---\n%s\n--- want ---\n%s", name, got, string(want))
	}
	fmt.Printf("\n===== %s =====\n%s\n", name, got)
}

// TestGoldenSizes is the M5 truth gate for the Face: the ticker + plan sidebar + transcript must render
// cleanly (no truncation of ids/scores, no mid-escape corruption) across narrow/mid/wide terminals.
func TestGoldenSizes(t *testing.T) {
	sizes := []struct {
		name string
		w, h int
	}{
		{"size_80x24", 80, 24},
		{"size_120x30", 120, 30},
		{"size_200x50", 200, 50},
	}
	for _, sz := range sizes {
		t.Run(sz.name, func(t *testing.T) {
			m := newGoldenModel(sz.w, sz.h) // sidebar is on by default — exercised at each width
			checkGolden(t, sz.name, stripANSI(m.View().Content))
		})
	}
}
