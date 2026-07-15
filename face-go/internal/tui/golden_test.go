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
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

var updateGolden = flag.Bool("update", false, "update golden files")

var ansiRe = regexp.MustCompile(`\x1b\[[0-9;?]*[a-zA-Z]`)

func stripANSI(s string) string {
	return ansiRe.ReplaceAllString(s, "")
}

// fakeSource is a static, non-networked api.DataSource — golden output must never depend on
// wall-clock time, random ports, or the ticking demo simulation.
type fakeSource struct{}

func (fakeSource) FetchState() (*api.StateDto, error)         { return nil, nil }
func (fakeSource) FetchTasks() (*api.TasksDto, error)         { return nil, nil }
func (fakeSource) FetchProcesses() (*api.ProcessesDto, error) { return nil, nil }
func (fakeSource) FetchSessions() (*api.SessionsDto, error)   { return nil, nil }
func (fakeSource) FetchTimeline() (*api.TimelineDto, error)   { return nil, nil }
func (fakeSource) FetchPromptPreview(_, _ string) (*api.PromptPreviewDto, error) {
	return nil, nil
}
func (fakeSource) QueryReport(sql string) (*api.QueryResultDto, error) { return nil, nil }
func (fakeSource) PostControl(api.ControlRequestDto) (*api.ControlAcceptedDto, error) {
	return &api.ControlAcceptedDto{Accepted: true}, nil
}
func (fakeSource) PostInject(api.InjectRequestDto) (*api.InjectAcceptedDto, error) {
	return &api.InjectAcceptedDto{Accepted: true}, nil
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

func fixedState() *api.StateDto {
	return &api.StateDto{
		PlanName:               "conductor-foreman",
		Status:                 "Running",
		StageId:                "F7",
		StageTitle:             "Gate caching + truth gates + speed program",
		DoneCount:              2,
		TotalCount:             40,
		TotalCostUsd:           0.42,
		TokensInput:            2500,
		TokensOutput:           1800,
		CurrentCheckpoint:      "F7.3",
		CurrentCheckpointTitle: "Wire caching layer",
		RunId:                  "demo-run-id",
		Repo:                   `C:\Code\conductor`,
		PlanDir:                "plans",
		SessionNumber:          12,
		SessionKind:            "Deliver",
		Attempt:                1,
		MaxAttempts:            3,
		SessionElapsedSec:      41,
		AgentActive:            true,
		SessionCostUsd:         0.12,
		SessionTokensInput:     3400,
		SessionTokensOutput:    1900,
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
		Limits: api.PlanLimitsDto{StallMinutes: 12, SessionTimeoutMinutes: 240, VerifierThreshold: 80},
	}
}

func fixedTranscript() []api.TranscriptLineDto {
	return []api.TranscriptLineDto{
		{Seq: 1, SessionId: "12", Kind: "system", Text: "Session #12 started · Deliver · Stage F7 · Attempt 1"},
		{Seq: 2, SessionId: "12", Kind: "thinking", Text: "Let me examine the GateCache implementation..."},
		{Seq: 3, SessionId: "12", Kind: "tool", Text: "read src/Conductor/Core/Gating/GateCache.cs"},
		{Seq: 4, SessionId: "12", Kind: "result", Text: "GateCache.cs:142 lines — caches by (name, tier, sha)"},
		{Seq: 5, SessionId: "12", Kind: "agent", Text: "Found the caching layer. Adding GetLastPassingGateResult to RunDb."},
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
	m, _ = m.Update(MsgProcessesUpdated{Procs: &api.ProcessesDto{Processes: []api.ProcessDto{
		// ExitedUtc is set on both (even the "alive" one) purely so the rendered runtime is a
		// fixed value — formatProcessRuntime falls back to time.Now() for genuinely-alive
		// processes, which would make this golden file flake with wall-clock time otherwise.
		{Pid: 4512, Purpose: "session", StageId: strPtr("F7"), Alive: true, StartedUtc: "2026-07-15T10:00:00Z", ExitedUtc: strPtr("2026-07-15T10:04:32Z"), LastOutputLine: strPtr("[agent] Working on gate caching...")},
		{Pid: 8723, Purpose: "gate:test", StageId: strPtr("F7"), Alive: false, StartedUtc: "2026-07-15T10:01:00Z", ExitedUtc: strPtr("2026-07-15T10:01:19Z"), LastOutputLine: strPtr("Running GateCacheTests... (12/12)")},
	}}})
	m, _ = m.Update(MsgSessionsUpdated{Sessions: &api.SessionsDto{Sessions: []api.SessionRowDto{
		{Number: 11, StageId: "F7", Kind: "Deliver", Outcome: strPtr("needsRetry"), Attempt: 1, CommitCount: 2,
			GateSummary:   strPtr("build ✓ test ✗ lint ○"),
			ResultSummary: strPtr("Wired the **caching layer** in `RunDb` but `test` still red — see gate output.")},
		{Number: 12, StageId: "F7", Kind: "Deliver", Outcome: nil, Attempt: 2, CommitCount: 0},
	}}})
	for _, tx := range fixedTranscript() {
		m, _ = m.Update(MsgTranscriptLine{Line: tx})
	}
	return m
}

func strPtr(s string) *string { return &s }

func TestGolden(t *testing.T) {
	cases := []struct {
		name string
		do   func(m tea.Model) tea.Model
	}{
		{"default", func(m tea.Model) tea.Model { return m }},
		{"sidebar_collapsed", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("p"))
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
		{"sessions", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("h"))
			return m
		}},
		{"report", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("r"))
			m, _ = m.Update(MsgReportResult{Result: &api.QueryResultDto{
				Columns: []string{"stage_id", "cost_usd"},
				Rows: []api.QueryRowDto{
					{Values: []string{"F1", "0.42"}},
					{Values: []string{"F7", "0.08"}},
				},
			}})
			return m
		}},
		{"processes", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("s"))
			return m
		}},
		{"timeline", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("t"))
			m, _ = m.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: fixedTimeline()}})
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
			m, _ = m.Update(keyMsg("g"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			return m
		}},
		{"plan_stage_fields", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("g"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyDown)) // select F7
			m, _ = m.Update(specialKey(tea.KeyEnter)) // drill into fields
			return m
		}},
		{"plan_stage_model_edit", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("g"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyEnter))  // drill F6
			m, _ = m.Update(specialKey(tea.KeyDown))   // → model field
			m, _ = m.Update(specialKey(tea.KeyEnter))  // begin editing the enum
			return m
		}},
		{"plan_gates", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("g"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyRight)) // → Gates section
			return m
		}},
		{"plan_settings", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("g"))
			m, _ = m.Update(MsgPlanLoaded{Plan: fixedPlan()})
			m, _ = m.Update(specialKey(tea.KeyRight))
			m, _ = m.Update(specialKey(tea.KeyRight))
			return m
		}},
		{"plan_import_diff", func(m tea.Model) tea.Model {
			m, _ = m.Update(keyMsg("g"))
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
		{"search", func(m tea.Model) tea.Model {
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
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			m := newGoldenModel(110, 34)
			m = tc.do(m)
			checkGolden(t, tc.name, stripANSI(m.View().Content))
		})
	}
}

// TestGoldenSplash renders the empty state (no run attached) that live mode shows before data arrives.
func TestGoldenSplash(t *testing.T) {
	var m tea.Model = New(fakeSource{}, false, "http://127.0.0.1:4317")
	m, _ = m.Update(tea.WindowSizeMsg{Width: 110, Height: 34})
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
