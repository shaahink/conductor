package tui

// U1.1 behaviour, driven through the real handlers rather than by poking Model fields: Home is the
// tab the Face lands on, it reports the workspace the engine actually serves, and it never invents a
// value it wasn't given.

import (
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

// homeText flattens tiered Home rows back into the plain block these assertions read. The tiers are
// only about what to SHED when the window is too short (U3.2); what each row says is unchanged.
func homeText(lines []homeLine) string {
	out := make([]string, 0, len(lines))
	for _, l := range lines {
		out = append(out, l.text)
	}
	return strings.Join(out, "\n")
}

func TestHomeIsTheStartupTab(t *testing.T) {
	m := New(api.NewDemoSource(), true, "(demo)")
	if m.tab != TabHome {
		t.Fatalf("the Face must open on Home, got tab %v", m.tab)
	}
	if tabKey[TabHome] != "h" || tabNames[TabHome] != "Home" {
		t.Fatalf("Home must be the h/Home first tab, got %q/%q", tabKey[TabHome], tabNames[TabHome])
	}
}

// Home is index 0, so the digit row shifts by one. "1" must land on Home and "0" still has to reach a
// real tab — the arrays are fixed-size and drift silently if one is grown without the others.
func TestHomeTabKeysAndDigits(t *testing.T) {
	for _, key := range []string{"h", "1"} {
		m := newTestModel()
		m = asModel(mustHandle(m.handleKey("a"))) // leave Home first, so a no-op would fail
		m = asModel(mustHandle(m.handleKey(key)))
		if m.tab != TabHome {
			t.Errorf("key %q should open Home, landed on %v", key, m.tab)
		}
	}

	m := newTestModel()
	m = asModel(mustHandle(m.handleKey("2")))
	if m.tab != TabAgent {
		t.Errorf(`"2" should open Agent now that Home took the first digit, landed on %v`, m.tab)
	}

	if len(tabKey) != int(tabCount) || len(tabNames) != int(tabCount) {
		t.Fatalf("tabKey/tabNames must both be length tabCount (%d)", tabCount)
	}
	seen := map[string]bool{}
	for i, k := range tabKey {
		if seen[k] {
			t.Errorf("duplicate tab mnemonic %q at index %d", k, i)
		}
		seen[k] = true
	}
}

// The Workspace panel is the whole point of U1: it must render what the engine served, and the state
// dir is REPO-rooted (PlanConfig.StateDir), never joined from planDir.
func TestHomeWorkspaceRendersEngineServedPaths(t *testing.T) {
	m := newTestModel()
	m.width, m.height = 110, 34
	m.data.Plan = &api.StateDto{
		Repo:     `C:\Code\conductor-baton`,
		PlanDir:  `C:\Code\conductor-baton\plans`,
		Tracker:  "CONDUCTOR-UX-START.md",
		StateDir: `C:\Code\conductor-baton\.conductor`,
	}
	m.plan = &api.PlanDto{PlanFile: `C:\Code\conductor-baton\plans\conductor-ux.plan.json`}

	got := stripANSI(homeText(m.renderHomeWorkspace(m.paneCols())))
	for _, want := range []string{
		`C:\Code\conductor-baton`,
		"CONDUCTOR-UX-START.md",
		`C:\Code\conductor-baton\.conductor`,
		"conductor-ux.plan.json",
	} {
		if !strings.Contains(got, want) {
			t.Errorf("Workspace panel is missing %q:\n%s", want, got)
		}
	}
	// The state dir must not be reported as living under plans/ — that is the spec's error, and the
	// bug this panel would ship if it derived the path instead of reading it.
	if strings.Contains(got, `plans\.conductor`) {
		t.Errorf("state dir must be repo-rooted, not planDir-rooted:\n%s", got)
	}
}

// An older engine serves no tracker/stateDir. Home must say "—" rather than print a confident path it
// guessed.
func TestHomeWorkspaceDegradesWhenTheEngineServesNothing(t *testing.T) {
	m := newTestModel()
	m.width, m.height = 110, 34
	m.data.Plan = &api.StateDto{Repo: `C:\Code\conductor-baton`}
	m.plan = nil

	got := stripANSI(homeText(m.renderHomeWorkspace(m.paneCols())))
	if strings.Count(got, "—") != 3 { // tracker, state dir, plan file
		t.Errorf("expected the three unknown fields to render as em-dashes:\n%s", got)
	}
	if !strings.Contains(got, `C:\Code\conductor-baton`) {
		t.Errorf("the repo it DID serve must still render:\n%s", got)
	}
}

// "budget caps with headroom when set" — an uncapped run must not be dressed up with a fake ceiling.
func TestHomeBudgetsOnlyWhenCapped(t *testing.T) {
	m := newTestModel()
	state := &api.StateDto{TotalCostUsd: 4, TokensInput: 100, TokensOutput: 100}

	m.plan = &api.PlanDto{Limits: api.PlanLimitsDto{}}
	if rows := m.homeBudgets(state); len(rows) != 0 {
		t.Errorf("no caps set → no budget rows, got %v", rows)
	}

	m.plan = nil
	if rows := m.homeBudgets(state); len(rows) != 0 {
		t.Errorf("no plan loaded → no budget rows, got %v", rows)
	}

	cost := 10.0
	m.plan = &api.PlanDto{Limits: api.PlanLimitsDto{MaxRunCostUsd: &cost}}
	rows := m.homeBudgets(state)
	if len(rows) != 1 {
		t.Fatalf("a cost cap → exactly one budget row, got %d", len(rows))
	}
	if got := stripANSI(rows[0].text); !strings.Contains(got, "$4.00 / $10.00") || !strings.Contains(got, "60% headroom") {
		t.Errorf("budget row should state spend, cap and remaining headroom, got %q", got)
	}
}

// Headroom is what is LEFT — the number the owner actually acts on — and its colour has to escalate
// as the run approaches the cap that will stop it.
func TestHomeHeadroomReportsRemainingNotSpent(t *testing.T) {
	if got := stripANSI(homeHeadroom("x", 0.25)); !strings.Contains(got, "75% headroom") {
		t.Errorf("25%% spent → 75%% headroom, got %q", got)
	}
	// Over-cap must clamp rather than print a negative headroom.
	if got := stripANSI(homeHeadroom("x", 1.4)); !strings.Contains(got, "0% headroom") {
		t.Errorf("over cap → 0%% headroom, got %q", got)
	}
	safe, warn, dead := homeHeadroom("x", 0.1), homeHeadroom("x", 0.75), homeHeadroom("x", 0.95)
	if safe == warn || warn == dead || safe == dead {
		t.Error("headroom colour must escalate across the safe/warn/critical bands")
	}
}

// Paths are informative at the tail: shortening must keep the folder you are in, not the drive letter.
func TestHomePathKeepsTheTail(t *testing.T) {
	long := `C:\Users\dev\very\deep\tree\conductor-baton`
	got := homePath(long, 30)
	if strings.HasPrefix(got, `C:\`) {
		t.Errorf("a shortened path must drop the head, not the tail: %q", got)
	}
	if !strings.HasSuffix(got, "conductor-baton") {
		t.Errorf("a shortened path must keep the folder you are in: %q", got)
	}
	if short := homePath(`C:\Code`, 30); short != `C:\Code` {
		t.Errorf("a path that fits must be left alone, got %q", short)
	}
}

// Next steps is contextual, not a legend: it names the thing worth pressing given the run's state.
func TestHomeHintsAreContextual(t *testing.T) {
	m := newTestModel()
	m.data.Plan = nil
	m.data.Connection.Mode = api.ModeLive
	if got := stripANSI(homeText(m.homeHints())); !strings.Contains(got, "conductor run -p <plan>") {
		t.Errorf("no run → the hint must be how to start one, got:\n%s", got)
	}

	reason := "verifier score 74 < 80"
	m.data.Plan = &api.StateDto{Status: "NeedsAttention", AttentionReason: &reason}
	if got := stripANSI(homeText(m.homeHints())); !strings.Contains(got, "needs a human") {
		t.Errorf("a blocked run must say so, got:\n%s", got)
	}

	m.data.Plan = &api.StateDto{Status: "Paused"}
	if got := stripANSI(homeText(m.homeHints())); !strings.Contains(got, "paused") {
		t.Errorf("a paused run must offer resume, got:\n%s", got)
	}

	m.data.Plan = &api.StateDto{Status: "Running", AgentActive: true}
	if got := stripANSI(homeText(m.homeHints())); !strings.Contains(got, "working right now") {
		t.Errorf("a live agent must be the first thing offered, got:\n%s", got)
	}
}
