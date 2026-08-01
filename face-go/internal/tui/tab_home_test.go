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
	// SF2.1 renders every path ONE way — forward slashes, upper-case drive — and everything inside
	// the repo relative to the repo row above it. The engine served these with backslashes; the panel
	// must not print two spellings of one machine.
	for _, want := range []string{
		"C:/Code/conductor-baton",
		"CONDUCTOR-UX-START.md",
		".conductor",
		"plans/conductor-ux.plan.json",
	} {
		if !strings.Contains(got, want) {
			t.Errorf("Workspace panel is missing %q:\n%s", want, got)
		}
	}
	if strings.Contains(got, `\`) {
		t.Errorf("no backslash may survive normalisation:\n%s", got)
	}
	// The state dir must not be reported as living under plans/ — that is the spec's error, and the
	// bug this panel would ship if it derived the path instead of reading it.
	if strings.Contains(got, "plans/.conductor") {
		t.Errorf("state dir must be repo-rooted, not planDir-rooted:\n%s", got)
	}
}

// The casing half of the same rule (screenshot critique #8): the engine resolved the repo as
// `C:/code/…` while the plan file was typed `C:\Code\…`. Both open the same directory, so no string
// rule can pick a winner — rendering repo-relative removes the disagreement instead.
func TestHomeWorkspaceKillsTheMixedCasingOfOnePath(t *testing.T) {
	m := newTestModel()
	m.width, m.height = 110, 34
	m.data.Plan = &api.StateDto{
		Repo:     "C:/code/conductor",
		StateDir: `C:\Code\conductor\.conductor`,
		Tracker:  "SARBAN-FACE-TRACKER.md",
	}
	m.plan = &api.PlanDto{PlanFile: `c:\Code\conductor\plans\sarban.plan.json`}

	got := stripANSI(homeText(m.renderHomeWorkspace(m.paneCols())))
	if strings.Contains(got, "Code/conductor/.conductor") || strings.Contains(got, "Code/conductor/plans") {
		t.Errorf("a second casing of the repo root leaked into a child path:\n%s", got)
	}
	if !strings.Contains(got, ".conductor") || !strings.Contains(got, "plans/sarban.plan.json") {
		t.Errorf("children of the repo must render relative to it:\n%s", got)
	}
	if strings.Count(got, "conductor") == 0 || !strings.Contains(got, "C:/code/conductor") {
		t.Errorf("the repo itself must still render in full, as the engine resolved it:\n%s", got)
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
	if !strings.Contains(got, "C:/Code/conductor-baton") {
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
	if strings.HasPrefix(got, "C:/") {
		t.Errorf("a shortened path must drop the head, not the tail: %q", got)
	}
	if !strings.HasSuffix(got, "conductor-baton") {
		t.Errorf("a shortened path must keep the folder you are in: %q", got)
	}
	// "Left alone" now means left at its length: separators and drive case are normalised on every
	// path Home prints, including the ones short enough to survive whole (SF2.1).
	if short := homePath(`c:\Code`, 30); short != "C:/Code" {
		t.Errorf("a path that fits must keep every segment, normalised, got %q", short)
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

// --- wiring rows, re-homed from the deleted Dev tab (SF1.2) --------------------

// These tests came from tab_dev_test.go with the panel they measure. The Dev tab's internals pane was
// never the part the owner called stupid — it answered "is the Face actually wired to anything" — so
// SF1.2 moved the rows Home did not already have into Home's own Wiring section and moved their tests
// with them, rather than deleting a measurement along with the SQL console it happened to sit beside.

// Home is the page a developer screenshots into a bug report. It must say whether a write token is
// present — never what it is.
func TestHomeWiringReportsTokenPresenceWithoutLeakingIt(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	got := stripANSI(homeText(m.homeWiring(100)))
	if !strings.Contains(got, "present") {
		t.Errorf("token presence must be stated:\n%s", got)
	}
	if url := stripANSI(homeText(m.renderHomeServer(100))); !strings.Contains(url, "http://127.0.0.1:4317") {
		t.Errorf("the control-plane url must be stated:\n%s", url)
	}
	// fakeSource reports a token; the rendered rows must never contain a token-looking secret. The
	// Face only ever holds presence, so this pins that the panel can't start echoing one later.
	for _, leak := range []string{"543BCE", "X-Conductor-Token"} {
		if strings.Contains(got, leak) {
			t.Errorf("Home leaked a secret (%s):\n%s", leak, got)
		}
	}
}

// homeRow pads a label to homeLabelW with no separator, so a label of exactly that width butts
// straight against its value — "write token" rendered as "write tokenpresent". Reading the frame
// caught it; nothing else would have. Pin the rule for every label the section uses, not just that one.
func TestHomeWiringLabelsFitTheGutter(t *testing.T) {
	for _, label := range homeWiringLabels {
		if len([]rune(label)) >= homeLabelW {
			t.Errorf("label %q is %d runes; homeLabelW is %d, so homeRow pads it to nothing and the "+
				"value collides with it — keep gutter labels under %d",
				label, len([]rune(label)), homeLabelW, homeLabelW)
		}
	}
	// And prove it end-to-end on the rendered rows: every label must be followed by a space. The
	// fixture's run has an id, so `run id` renders too. The model is put in the DISCONNECTED state
	// with an error, because SF2.1 made two of these rows conditional on exactly that: the engine
	// line's wording and the raw `last error` row that moved down here from Server.
	m := newGoldenModel(120, 30).(Model)
	m.data.Connection.Connected = false
	raw := "dial tcp 127.0.0.1:4317: connectex: No connection could be made"
	m.data.Connection.LastError = &raw
	got := stripANSI(homeText(m.homeWiring(100)) + "\n" + homeText(m.renderHomeServer(100)))
	for _, label := range append([]string{"engine", "streams"}, homeWiringLabels...) {
		if !strings.Contains(got, label+" ") {
			t.Errorf("label %q is not followed by a gap in the rendered rows:\n%s", label, got)
		}
	}
}

// Home cannot scroll (STYLE.md), so every row SF1.2 added has to be sheddable — otherwise the wiring
// diagnostics push "Next steps" off the bottom of a short window, which is the exact regression the
// tier system exists to prevent.
func TestHomeWiringRowsShedBeforeTheLandingsAnswer(t *testing.T) {
	m := newGoldenModel(120, 30).(Model)
	rows := m.homeWiring(100)
	for _, l := range rows[1:] { // rows[0] is the section header, which homePanel makes essential
		if l.tier == homeEssential {
			t.Errorf("a wiring row is homeEssential (%q) — Home cannot scroll, so diagnostics must "+
				"shed before Next steps does", stripANSI(l.text))
		}
	}
	// And prove it on the real page: at 80x24 the wiring rows are gone and the answer is still there.
	body, _ := newGoldenModel(80, 24).(Model).renderHomePane()
	plain := stripANSI(body)
	if !strings.Contains(plain, "Next steps") {
		t.Errorf("Next steps was shed on a short window:\n%s", plain)
	}
	if strings.Contains(plain, "spinner 120ms") {
		t.Errorf("a homeDetail wiring row survived a window too short for it:\n%s", plain)
	}
	// The regression this section's POSITION exists to prevent, stated as the property rather than as
	// a guess about one window size: at EVERY budget, the page with the re-homed diagnostics must
	// still show everything the page without them showed. Folded into the Server panel instead (the
	// first section, so the last to shed) this failed at budget 24 — Workspace lost `tracker` and
	// `state dir` to make room. fitHome mutates the slices it is given, so each call gets fresh ones.
	without := func() [][]homeLine {
		return [][]homeLine{m.renderHomeServer(100), m.renderHomeRun(100), m.renderHomeWorkspace(100),
			m.renderHomeNextSteps()}
	}
	with := func() [][]homeLine { return append(without(), m.homeWiring(100)) }
	for _, budget := range []int{12, 18, 22, 24, 28, 40} {
		base := strings.Split(stripANSI(fitHome(without(), budget)), "\n")
		got := stripANSI(fitHome(with(), budget))
		for _, line := range base {
			if strings.TrimSpace(line) == "" {
				continue
			}
			if !strings.Contains(got, line) {
				t.Errorf("at budget %d the Wiring section displaced a row Home already showed: %q\n%s",
					budget, line, got)
			}
		}
	}
}
