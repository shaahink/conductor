package tui

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

// KS2.4 — switching runs inside one Face.
//
// Three things are worth pinning here and none of them is "the picker draws":
//
//  1. THE TOKEN GOES WITH THE RUN. The Face holds one run's write token and then another's, in the
//     same process, and reads never need one — so a token that failed to change hands is invisible
//     until a write is refused. Pinned against two real servers, each of which refuses the other's.
//  2. THE SESSION SURVIVES AND THE RUN DOES NOT. Theme, tab, sidebar and window carry over; the
//     previous run's state, plan and transcript do not, because a switch that kept them would paint
//     one run's numbers under another run's name.
//  3. ESC IS NOT QUIT. In the pre-flight picker `esc` ends the process. Shown over a live dashboard
//     the same key must mean "back to the run I am on", or the switcher is a way to lose your place.

// switchFleet is a two-run machine: the run this directory holds and another repo's.
func switchFleet(urlA, urlB string) Fleet {
	return Fleet{
		Runs: []FleetRun{
			{
				Repo: "C:/code/conductor", PlanName: "core", RunID: "8cefa5de8f16", Status: "Running",
				Port: 4317, Pid: 35412, StageID: "KS2", Done: 3, Total: 8, CostUsd: 12.34,
				BaseURL: urlA, StateDir: "C:/code/conductor/.conductor", Token: "tok-a", Self: true,
			},
			{
				Repo: "C:/Code/sk-studio", PlanName: "NINE STREETS", RunID: "7951c3ca149a", Status: "Running",
				Port: 4318, Pid: 19056, StageID: "E", Done: 29, Total: 46, CostUsd: 280.81,
				BaseURL: urlB, StateDir: `C:/Code/sk-studio\.conductor`, Token: "tok-b",
			},
		},
		Past:      pastFleet(),
		PastTotal: len(pastFleet()),
	}
}

// attachedModel is a Face already looking at `url`, with a fleet to switch within.
func attachedModel(t *testing.T, url, token string, fleet Fleet) Model {
	t.Helper()
	src := api.NewLiveSourceWithToken(url, token)
	t.Cleanup(src.Close)
	m := New(src, false, url).WithFleet(fleet)
	m.width, m.height = 100, 30
	m.recalcDimensions()
	return m
}

// openSwitcherThroughThePalette drives the real router — `:`, the query, enter — rather than setting
// the flag, because the palette's Local dispatch is the half that can break without the switcher
// noticing (STYLE.md: pin a key through handleKey, never through the handler it should reach).
func openSwitcherThroughThePalette(t *testing.T, m Model) Model {
	t.Helper()
	m = asModel(mustHandle(m.handleKey(":")))
	for _, ch := range switchVerb {
		m = asModel(mustHandle(m.handlePaletteKey(string(ch))))
	}
	return asModel(mustHandle(m.handlePaletteKey("enter")))
}

// tokenServer answers /control only for the token it was given, and records every token it saw.
func tokenServer(t *testing.T, want string, seen *[]string) *httptest.Server {
	t.Helper()
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/control" {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		got := r.Header.Get("X-Conductor-Token")
		*seen = append(*seen, got)
		if got != want {
			w.WriteHeader(http.StatusUnauthorized)
			_, _ = w.Write([]byte(`{"error":"bad token"}`))
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(map[string]any{"accepted": true})
	}))
	t.Cleanup(s.Close)
	return s
}

// TestSwitchingRunsCarriesTheNewRunsTokenAndOnlyThat is the falsifiable half of "write tokens never
// cross runs": a control POST is accepted by the run switched TO and refused by the run switched
// FROM, with the very token the switch handed over.
func TestSwitchingRunsCarriesTheNewRunsTokenAndOnlyThat(t *testing.T) {
	var seenA, seenB []string
	a := tokenServer(t, "tok-a", &seenA)
	b := tokenServer(t, "tok-b", &seenB)
	fleet := switchFleet(a.URL, b.URL)

	m := attachedModel(t, a.URL, "tok-a", fleet)

	// Attached to A: A takes A's token.
	if sent := m.cmdPostControl(api.ControlRequestDto{Command: "pause"})().(MsgControlSent); !sent.Success {
		t.Fatalf("precondition: run A refused its own token: %+v", sent)
	}

	m = openSwitcherThroughThePalette(t, m)
	if !m.switcher.open {
		t.Fatal("the palette's switch verb did not open the switcher")
	}
	next := asModel(mustHandle(m.handleSwitcherKey("down")))
	next = asModel(mustHandle(next.handleSwitcherKey("enter")))

	if next.baseURL != b.URL {
		t.Fatalf("after the switch the Face is on %q, want %q", next.baseURL, b.URL)
	}
	if sent := next.cmdPostControl(api.ControlRequestDto{Command: "pause"})().(MsgControlSent); !sent.Success {
		t.Fatalf("run B refused the token that came with it: %+v", sent)
	}
	for _, got := range seenB {
		if got != "tok-b" {
			t.Errorf("run B was sent %q — a token from another run reached it", got)
		}
	}
	for _, got := range seenA {
		if got == "tok-b" {
			t.Error("run A was sent run B's token")
		}
	}

	// And the other direction, with the same value the switch handed over: B's token at A is a 401,
	// so a Face that had failed to swap it would have been writing into a refusal all along.
	other := api.NewLiveSourceWithToken(a.URL, fleet.Runs[1].Token)
	defer other.Close()
	if _, err := other.PostControl(api.ControlRequestDto{Command: "pause"}); err == nil {
		t.Error("run A accepted run B's token — the tokens are not per-run at all")
	}
}

// TestSwitchingPreservesTheSessionAndDropsTheOldRun: what a person would call their session carries
// over; everything that described the run they left does not.
func TestSwitchingPreservesTheSessionAndDropsTheOldRun(t *testing.T) {
	restoreDefaultTheme(t)
	isolateConfig(t)
	if err := ApplyTheme("nord"); err != nil {
		t.Fatal(err)
	}

	fleet := switchFleet("http://127.0.0.1:4317", "http://127.0.0.1:4318")
	m := attachedModel(t, "http://127.0.0.1:4317", "tok-a", fleet)
	m.tab = TabReport
	m.sidebarCollapsed = true
	m.data.Plan = &api.StateDto{PlanName: "core", StageId: "KS2"}
	m.data.Sessions = []api.SessionRowDto{{Number: 12}}

	m = openSwitcherThroughThePalette(t, m)
	next := asModel(mustHandle(m.handleSwitcherKey("down")))
	next = asModel(mustHandle(next.handleSwitcherKey("enter")))
	t.Cleanup(next.source.Close)

	if got := widgets.CurrentTheme().Name; got != "nord" {
		t.Errorf("theme after a switch is %q — the Face restarted into defaults", got)
	}
	if next.tab != TabReport {
		t.Errorf("tab after a switch is %v, want TabReport", next.tab)
	}
	if !next.sidebarCollapsed {
		t.Error("the collapsed sidebar came back")
	}
	if next.width != 100 || next.height != 30 {
		t.Errorf("window after a switch is %dx%d, want 100x30", next.width, next.height)
	}
	if len(next.fleet.Runs) != len(fleet.Runs) {
		t.Error("the fleet did not survive the switch — one switch and there is no second one")
	}

	// …and nothing of the run that was left.
	if next.data.Plan != nil || len(next.data.Sessions) != 0 {
		t.Error("the previous run's state survived the switch — the new run would be painted with it")
	}
	if next.stateDir != fleet.Runs[1].StateDir {
		t.Errorf("state dir is %q, want the run switched to", next.stateDir)
	}
	if next.baseURL != fleet.Runs[1].BaseURL {
		t.Errorf("base url is %q, want the run switched to", next.baseURL)
	}
}

// TestSwitcherEscReturnsToTheRunAndDoesNotQuit. In the pre-flight picker esc quits the process; the
// same key over a live dashboard must peel one layer, or the switcher is a trapdoor.
func TestSwitcherEscReturnsToTheRunAndDoesNotQuit(t *testing.T) {
	m := attachedModel(t, "http://127.0.0.1:4317", "tok-a", switchFleet("http://127.0.0.1:4317", "http://127.0.0.1:4318"))
	m.tab = TabKanban
	m = openSwitcherThroughThePalette(t, m)

	for _, key := range []string{"esc", "q"} {
		open := m
		back, cmd := open.handleSwitcherKey(key)
		if cmd != nil {
			t.Errorf("%q in the switcher returned a command — the Face quits on the key that cancels", key)
		}
		got := asModel(back)
		if got.switcher.open {
			t.Errorf("%q did not close the switcher", key)
		}
		if got.tab != TabKanban {
			t.Errorf("%q left the Face on %v — cancelling moved the user", key, got.tab)
		}
		if got.baseURL != "http://127.0.0.1:4317" {
			t.Errorf("%q changed the attached run", key)
		}
	}
}

// ctrl+c is the one key a sub-state may not swallow (handleKey's own rule).
func TestSwitcherStillAnswersCtrlC(t *testing.T) {
	m := attachedModel(t, "http://127.0.0.1:4317", "tok-a", switchFleet("http://127.0.0.1:4317", "http://127.0.0.1:4318"))
	m = openSwitcherThroughThePalette(t, m)

	armed, cmd := m.handleSwitcherKey("ctrl+c")
	if cmd == nil {
		t.Fatal("the first ctrl+c did not arm the quit hint")
	}
	if !asModel(armed).quitArmed {
		t.Error("ctrl+c in the switcher did not reach the global quit affordance")
	}
}

// A finished run has no plane for this process to point at, so the switcher records the choice and
// quits; main.go hands the selector back and the engine serves it read-only (KS2.2).
func TestSwitchingToAFinishedRunHandsItBackToTheEngine(t *testing.T) {
	fleet := switchFleet("http://127.0.0.1:4317", "http://127.0.0.1:4318")
	m := attachedModel(t, "http://127.0.0.1:4317", "tok-a", fleet)
	m = openSwitcherThroughThePalette(t, m)

	if _, handed := m.Handoff(); handed {
		t.Fatal("a freshly opened switcher already claims a finished run")
	}

	m = asModel(mustHandle(m.handleSwitcherKey("end")))
	after, cmd := m.handleSwitcherKey("enter")
	if cmd == nil {
		t.Fatal("enter on a finished run did not end the Face — nothing would open it")
	}
	got := asModel(after)
	past, handed := got.Handoff()
	if !handed {
		t.Fatal("the finished run was not recorded for the engine")
	}
	want := fleet.Past[len(fleet.Past)-1]
	if past.OpenWith() != want.OpenWith() {
		t.Errorf("handed back %q, want %q", past.OpenWith(), want.OpenWith())
	}
	if got.baseURL != "http://127.0.0.1:4317" {
		t.Error("the live attachment was swapped on the way out")
	}
}

// A Face with no fleet (started with --url alone, or an archive Face whose credentials the engine
// strips) says so instead of opening an empty list.
func TestSwitcherWithNoFleetSaysSoRatherThanOpeningEmpty(t *testing.T) {
	m := attachedModel(t, "http://127.0.0.1:4317", "", Fleet{})

	m = openSwitcherThroughThePalette(t, m)
	if m.switcher.open {
		t.Fatal("the switcher opened with nothing to switch to")
	}
	if len(m.toasts) == 0 {
		t.Fatal("nothing was said about why the switcher did not open")
	}
	if !strings.Contains(m.toasts[len(m.toasts)-1].Text, "--pick") {
		t.Errorf("the toast does not say how to reach the other runs: %q", m.toasts[len(m.toasts)-1].Text)
	}
}

// The switcher REPLACES the frame rather than floating over it, so the window clamp is its own
// Render's — the same invariant every dashboard frame carries, at the documented 80x24 floor.
func TestSwitcherFrameFitsTheWindow(t *testing.T) {
	base := attachedModel(t, "http://127.0.0.1:4317", "tok-a", switchFleet("http://127.0.0.1:4317", "http://127.0.0.1:4318"))
	base = openSwitcherThroughThePalette(t, base)

	for _, size := range []struct{ w, h int }{{80, 24}, {100, 30}, {132, 40}, {200, 50}} {
		m := base
		m.width, m.height = size.w, size.h
		frame := m.View().Content
		lines := strings.Split(frame, "\n")
		if len(lines) > size.h {
			t.Errorf("%dx%d: switcher frame is %d rows", size.w, size.h, len(lines))
		}
		for i, ln := range lines {
			if w := lipgloss.Width(ln); w > size.w {
				t.Errorf("%dx%d: row %d is %d cols\n%s", size.w, size.h, i, w, frame)
			}
		}
		plain := stripANSI(frame)
		if !strings.Contains(plain, "switch this face to which run?") {
			t.Errorf("%dx%d: the switcher does not say it is switching:\n%s", size.w, size.h, plain)
		}
		if !strings.Contains(plain, "esc cancel") {
			t.Errorf("%dx%d: the way out is missing or still reads as quit:\n%s", size.w, size.h, plain)
		}
	}
}

// The run you are ON is marked, and it is the row the switcher starts on. Without it the list of
// "which run?" cannot answer "which one am I looking at now?".
func TestSwitcherMarksAndStartsOnTheAttachedRun(t *testing.T) {
	fleet := switchFleet("http://127.0.0.1:4317", "http://127.0.0.1:4318")
	m := attachedModel(t, fleet.Runs[1].BaseURL, "tok-b", fleet)
	m = openSwitcherThroughThePalette(t, m)

	if m.switcher.picker.cursor != 1 {
		t.Errorf("the switcher opened on row %d, want the run it is attached to", m.switcher.picker.cursor)
	}
	plain := stripANSI(m.View().Content)
	if !strings.Contains(plain, "●") {
		t.Errorf("the attached run is not marked:\n%s", plain)
	}
	if !strings.Contains(plain, "attached now") {
		t.Errorf("the detail line does not say which run is attached:\n%s", plain)
	}
	// Choosing the run already attached is a no-op, not a reconnect that drops the transcript.
	same := asModel(mustHandle(m.handleSwitcherKey("enter")))
	if same.baseURL != fleet.Runs[1].BaseURL {
		t.Error("re-choosing the attached run moved the Face")
	}
	if same.data.Connection.URL != fleet.Runs[1].BaseURL {
		t.Error("re-choosing the attached run rebuilt the connection")
	}
}

// The switcher is peeled in Update at the same precedence as the command bar: a tab mnemonic pressed
// while it is up must move the CURSOR, not the tab underneath.
func TestSwitcherOwnsEveryKeyWhileItIsUp(t *testing.T) {
	m := attachedModel(t, "http://127.0.0.1:4317", "tok-a", switchFleet("http://127.0.0.1:4317", "http://127.0.0.1:4318"))
	m.tab = TabHome
	m = openSwitcherThroughThePalette(t, m)

	var tm tea.Model = m
	tm, _ = tm.Update(keyMsg("k")) // Knowledge's mnemonic; here it is the picker's "up"
	got := asModel(tm)
	if got.tab != TabHome {
		t.Errorf("a tab mnemonic reached the dashboard through the switcher: tab is now %v", got.tab)
	}
	if !got.switcher.open {
		t.Error("the switcher closed on a tab mnemonic")
	}
}
