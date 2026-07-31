package api

import (
	"encoding/json"
	"strings"
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

// SF1.2 deleted TestDemoSourceQuery with the QueryReport it exercised. The demo source's typed
// replacement is covered by TestDemoSourceScores — a demo Face must still render a full Report tab
// offline, which is the behaviour that mattered.
func TestDemoSourceScores(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	scores, err := src.FetchScores()
	if err != nil {
		t.Fatalf("FetchScores failed: %v", err)
	}
	if len(scores.Scores) == 0 {
		t.Fatal("the demo source must serve verifier scores — the Report tab renders them offline")
	}
	for _, sc := range scores.Scores {
		if sc.Threshold == 0 {
			t.Errorf("session #%d has no threshold; the Report tab renders score/threshold and would "+
				"print \"88/0\"", sc.SessionNumber)
		}
	}
}

// SC1.3: the Face's Telegram DTOs are hand-written mirrors of the engine's records, and a field
// renamed on one side is invisible until an operator stares at a pane that says nothing. So this
// decodes the EXACT bytes a live engine returned during SC1.3's proof run (dotnet run, control plane
// on 127.0.0.1, real api.telegram.org) rather than bytes this package made up.
func TestTelegramStatusDecodesTheEnginesRealBytes(t *testing.T) {
	const beforeTheToken = `{"configured":true,"started":false,"hasToken":false,` +
		`"allowedChatIds":["99205495"],"pollIntervalSeconds":4,"enableTwoWay":true,"willDeliver":false,` +
		`"willDeliverReason":"configured but no bot token — set CONDUCTOR_TELEGRAM_TOKEN, or save one from the Face's Telegram tab",` +
		`"restartRequired":false}`
	const afterTheToken = `{"configured":true,"started":true,"hasToken":true,"allowedChatIds":["99205495"],` +
		`"pollIntervalSeconds":4,"enableTwoWay":true,"willDeliver":true,"restartRequired":false}`

	var before TelegramStatusDto
	if err := json.Unmarshal([]byte(beforeTheToken), &before); err != nil {
		t.Fatalf("decoding the engine's status: %v", err)
	}
	if before.WillDeliver {
		t.Error("willDeliver must be false before the token — the Face renders this as 'delivering'")
	}
	if before.WillDeliverReason == nil || !strings.Contains(*before.WillDeliverReason, "no bot token") {
		t.Errorf("willDeliverReason did not decode: %v", before.WillDeliverReason)
	}
	if before.RestartRequired {
		t.Error("restartRequired must be false when a live service exists — it means 'nothing you save here can work'")
	}

	var after TelegramStatusDto
	if err := json.Unmarshal([]byte(afterTheToken), &after); err != nil {
		t.Fatalf("decoding the engine's post-token status: %v", err)
	}
	if !after.WillDeliver || !after.Started {
		t.Errorf("the late token's effect did not decode: started=%v willDeliver=%v", after.Started, after.WillDeliver)
	}

	// And the token endpoint's reply, whose WillDeliver is what stops the Face ticking green over a
	// save that cannot deliver.
	const tokenReply = `{"ok":true,"message":"the running engine picked it up — Telegram is delivering to 1 chat id(s) now, no restart needed","willDeliver":true}`
	var saved TelegramSetTokenResultDto
	if err := json.Unmarshal([]byte(tokenReply), &saved); err != nil {
		t.Fatalf("decoding the token reply: %v", err)
	}
	if !saved.Ok || !saved.WillDeliver {
		t.Errorf("token reply did not decode: ok=%v willDeliver=%v", saved.Ok, saved.WillDeliver)
	}

	// The test button's reply: ViaQueue is the difference between proof and theatre.
	const testReply = `{"ok":true,"botUsername":"conductor_app_bot","viaQueue":true,"detail":"sent through the live send queue — the same path every run push takes"}`
	var tested TelegramTestResultDto
	if err := json.Unmarshal([]byte(testReply), &tested); err != nil {
		t.Fatalf("decoding the test reply: %v", err)
	}
	if !tested.ViaQueue || tested.Detail == nil {
		t.Errorf("test reply did not decode: viaQueue=%v detail=%v", tested.ViaQueue, tested.Detail)
	}
}

// The demo is where this flow gets reviewed, so it must not tick green where the engine now refuses:
// a token saved with no chat id still cannot notify anybody.
func TestDemoTelegramTokenSaveIsHonestAboutDelivery(t *testing.T) {
	src := NewDemoSource()
	defer src.Close()

	saved, err := src.PostTelegramToken(TelegramSetTokenRequestDto{Token: "123:abc"})
	if err != nil {
		t.Fatalf("PostTelegramToken: %v", err)
	}
	if !saved.Ok {
		t.Fatal("the save itself should succeed")
	}
	if saved.WillDeliver {
		t.Error("no chat id is configured in the demo yet — the save cannot make it deliver")
	}

	tested, err := src.PostTelegramTest()
	if err != nil {
		t.Fatalf("PostTelegramTest: %v", err)
	}
	if tested.Ok {
		t.Error("a test that sends nothing is not a passing test")
	}

	// Add a chat id the way the Face does (plan edit, target telegram) and the verdict flips.
	val := "99205495"
	if _, err := src.PostPlanEdit(PlanEditRequestDto{Edits: []PlanEditDto{{Target: "telegram", Field: "allowedchatids", Value: &val}}}); err != nil {
		t.Fatalf("PostPlanEdit: %v", err)
	}
	tested, err = src.PostTelegramTest()
	if err != nil {
		t.Fatalf("PostTelegramTest: %v", err)
	}
	if !tested.Ok || !tested.ViaQueue {
		t.Errorf("with a token and a chat id the demo test must pass through the queue: ok=%v viaQueue=%v", tested.Ok, tested.ViaQueue)
	}
	status, err := src.FetchTelegramStatus()
	if err != nil || !status.WillDeliver {
		t.Errorf("demo status must report willDeliver once it can: %v", err)
	}
}
