package api

import (
	"encoding/json"
	"fmt"
	"math/rand"
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
}

func NewDemoSource() DataSource {
	now := time.Now()
	s := &demoSource{
		startTime: now,
		stopCh:    make(chan struct{}),
		processes: makeFakeProcesses(now),
		sessions:  makeFakeSessions(),
		state:     makeFakeState(),
	}

	go s.runSimulation()

	return s
}

func (s *demoSource) FetchState() (*StateDto, error) {
	return s.state, nil
}

func (s *demoSource) FetchTasks() (*TasksDto, error) {
	return &TasksDto{
		Tasks: []TaskDto{
			{TaskId: "T1", CheckpointId: "F7.4", Title: "Implement gate caching by SHA", Status: "done", Source: "planner", Order: 1},
			{TaskId: "T2", CheckpointId: "F7.4", Title: "Add per-stage truth gate config", Status: "done", Source: "agent", Order: 2},
			{TaskId: "T3", CheckpointId: "F7.4", Title: "Wire RunDb.GetLastPassingGateResult", Status: "in_progress", Source: "agent", Order: 3},
			{TaskId: "T4", CheckpointId: "F7.5", Title: "Add SkipIfFresh file-timestamp check", Status: "todo", Source: "planner", Order: 4},
		},
	}, nil
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
	return &ControlAcceptedDto{Accepted: true}, nil
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
		cp := (s.tickCount / 3) % 5
		s.state.SessionElapsedSec = elapsed
		s.state.SessionCostUsd = cost
		s.state.SessionTokensInput = tokIn
		s.state.SessionTokensOutput = tokOut
		s.state.TotalCostUsd = cost + 0.42
		s.state.TokensInput = tokIn + 2500
		s.state.TokensOutput = tokOut + 1800
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
		PlanDir:                "plans",
		SessionNumber:          12,
		SessionKind:            "Deliver",
		Attempt:                1,
		MaxAttempts:            3,
		SessionElapsedSec:      0,
		AgentActive:            true,
		SessionCostUsd:         0,
		SessionTokensInput:     0,
		SessionTokensOutput:    0,
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
	return []ProcessDto{
		{Pid: 4512, Purpose: "session", StageId: strPtr("F7"), Alive: true, LastOutputLine: strPtr("[agent] Working on gate caching...")},
		{Pid: 8723, Purpose: "gate:test", StageId: strPtr("F7"), Alive: true, LastOutputLine: strPtr("Running GateCacheTests... (12/12)")},
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

func makeFakeSessions() []SessionRowDto {
	return []SessionRowDto{
		{Number: 1, StageId: "F0", Kind: "Deliver", Outcome: strPtr("completed"), Attempt: 1, CommitCount: 3, GateSummary: strPtr("build ✓ test ✓")},
		{Number: 2, StageId: "F1", Kind: "Deliver", Outcome: strPtr("completed"), Attempt: 1, CommitCount: 4, GateSummary: strPtr("build ✓ test ✓ lint ✓")},
		{Number: 8, StageId: "F6", Kind: "Deliver", Outcome: strPtr("completed"), Attempt: 1, CommitCount: 1, GateSummary: strPtr("build ✓ test ✓")},
		{Number: 11, StageId: "F7", Kind: "Deliver", Outcome: strPtr("needsRetry"), Attempt: 1, CommitCount: 2, GateSummary: strPtr("build ✓ test ✗ lint ○")},
		{Number: 12, StageId: "F7", Kind: "Deliver", Outcome: nil, Attempt: 2, CommitCount: 0},
	}
}

func strPtr(s string) *string {
	return &s
}
