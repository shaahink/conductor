package api

import (
	"testing"
	"time"
)

// The demo IS the product tour (CONDUCTOR-UX.md ground rules), so --demo has to fill every field
// Home renders — a tour with dashes where the workspace should be teaches the product wrong. Driven
// against the REAL demo source, not the goldens' fakeSource, which is where these fields would
// otherwise go untested.
func TestDemoSourceFillsHomeWorkspaceAndBudgets(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	state, err := src.FetchState()
	if err != nil || state == nil {
		t.Fatalf("FetchState failed: %v", err)
	}
	for _, f := range []struct{ name, got string }{
		{"Repo", state.Repo},
		{"PlanDir", state.PlanDir},
		{"Tracker", state.Tracker},
		{"StateDir", state.StateDir},
	} {
		if f.got == "" {
			t.Errorf("demo state must fill %s — Home renders it", f.name)
		}
	}
	// StateDir is repo-rooted (PlanConfig.StateDir), never planDir-rooted. The demo has to mirror the
	// engine's real layout or the tour teaches a path that does not exist.
	if want := state.Repo + `\.conductor`; state.StateDir != want {
		t.Errorf("demo StateDir = %q, want %q (repo-rooted, not under planDir)", state.StateDir, want)
	}

	plan, err := src.FetchPlan()
	if err != nil || plan == nil {
		t.Fatalf("FetchPlan failed: %v", err)
	}
	if plan.PlanFile == "" {
		t.Error("demo plan must carry PlanFile — Home's Workspace panel names it")
	}
	// Caps set = Home's budget/headroom rows are part of the tour.
	if plan.Limits.MaxRunCostUsd == nil || *plan.Limits.MaxRunCostUsd <= 0 {
		t.Error("demo plan must set limits.maxRunCostUsd so Home shows a budget row")
	}
	if plan.Limits.MaxRunTokens == nil || *plan.Limits.MaxRunTokens <= 0 {
		t.Error("demo plan must set limits.maxRunTokens so Home shows a token-cap row")
	}
}

func TestDemoSourceFetchState(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	state, err := src.FetchState()
	if err != nil {
		t.Fatalf("FetchState failed: %v", err)
	}
	if state == nil {
		t.Fatal("expected non-nil state")
	}
	if state.StageId != "F7" {
		t.Errorf("expected StageId F7, got %s", state.StageId)
	}
	if len(state.Stages) < 5 {
		t.Errorf("expected at least 5 stages, got %d", len(state.Stages))
	}
	if len(state.Gates) < 3 {
		t.Errorf("expected at least 3 gates, got %d", len(state.Gates))
	}
	if state.Status != "Running" {
		t.Errorf("expected Running status, got %s", state.Status)
	}
}

func TestDemoSourceFetchSessions(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	sessions, err := src.FetchSessions()
	if err != nil {
		t.Fatalf("FetchSessions failed: %v", err)
	}
	if len(sessions.Sessions) < 3 {
		t.Errorf("expected at least 3 sessions, got %d", len(sessions.Sessions))
	}
}

func TestDemoSourceFetchProcesses(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	procs, err := src.FetchProcesses()
	if err != nil {
		t.Fatalf("FetchProcesses failed: %v", err)
	}
	if len(procs.Processes) < 1 {
		t.Errorf("expected at least 1 process, got %d", len(procs.Processes))
	}
}

func TestDemoSourceControl(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	result, err := src.PostControl(ControlRequestDto{Command: "pause"})
	if err != nil {
		t.Fatalf("PostControl failed: %v", err)
	}
	if !result.Accepted {
		t.Error("expected control to be accepted")
	}
}

func TestDemoSourceInject(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	result, err := src.PostInject(InjectRequestDto{
		Content: "test injection",
		StageId: "F7",
	})
	if err != nil {
		t.Fatalf("PostInject failed: %v", err)
	}
	if !result.Accepted {
		t.Error("expected injection to be accepted")
	}
}

func TestDemoSourceSimulation(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	state1, _ := src.FetchState()
	time.Sleep(100 * time.Millisecond)
	state2, _ := src.FetchState()

	_ = state1
	if state2 == nil {
		t.Fatal("expected non-nil state after tick")
	}
}

func TestDemoSourceQuery(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	result, err := src.QueryReport("SELECT * FROM stages")
	if err != nil {
		t.Fatalf("QueryReport failed: %v", err)
	}
	if len(result.Columns) < 1 {
		t.Error("expected at least 1 column")
	}
}
