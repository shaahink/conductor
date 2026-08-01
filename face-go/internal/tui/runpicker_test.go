package tui

import (
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"
)

// The two runs this machine actually had while SF5.4 was written — a shape, not an invention: repo
// leaf, plan, stage, one healthy and one wanting a human, one with a write token and one without.
func twoRunFleet() []FleetRun {
	return []FleetRun{
		{
			Repo: "C:/code/conductor", PlanName: "Sarban face - the watcher and the surfaces",
			RunID: "8f31c0a9d4e5", Status: "Running", Port: 4317, Pid: 35412,
			StageID: "SF5", StageTitle: "Supervision without a polling meter",
			Done: 18, Total: 24, CostUsd: 12.34,
			BaseURL: "http://127.0.0.1:4317", StateDir: "C:/code/conductor/.conductor",
			Token: "tok-conductor", Self: true,
		},
		{
			Repo: "C:/code/sk-studio", PlanName: "sk-studio site", RunID: "1a2b3c4d5e6f",
			Status: "Paused", Attention: "owner input wanted", Port: 4318, Pid: 22104,
			StageID: "B3", Done: 3, Total: 9, CostUsd: 3.1,
			BaseURL: "http://127.0.0.1:4318", StateDir: "C:/code/sk-studio/.conductor",
		},
	}
}

func TestParseFleetRejectsWhatCannotBeOffered(t *testing.T) {
	for _, tc := range []struct{ name, raw string }{
		{"empty", ""},
		{"blank", "   "},
		{"garbage", "not json"},
		{"no runs", `{"runs":[]}`},
		{"null runs", `{"runs":null}`},
	} {
		if _, err := ParseFleet(tc.raw); err == nil {
			t.Errorf("%s: expected an error, got none — the Face would show an empty picker", tc.name)
		}
	}
}

func TestParseFleetReadsTheEnvelope(t *testing.T) {
	raw := `{"runs":[{"repo":"C:/code/sk-studio","planName":"sk-studio site","runId":"1a2b3c4d5e6f",
	  "status":"Paused","port":4318,"pid":22104,"stageId":"B3","stageTitle":"Copy",
	  "attentionReason":"owner input wanted","done":3,"total":9,"costUsd":3.1,
	  "baseUrl":"http://127.0.0.1:4318","stateDir":"C:/code/sk-studio/.conductor",
	  "token":"tok-sk","self":false}]}`
	f, err := ParseFleet(raw)
	if err != nil {
		t.Fatalf("ParseFleet: %v", err)
	}
	if len(f.Runs) != 1 {
		t.Fatalf("want 1 run, got %d", len(f.Runs))
	}
	r := f.Runs[0]
	if r.BaseURL != "http://127.0.0.1:4318" || r.Token != "tok-sk" || r.Pid != 22104 || r.CostUsd != 3.1 {
		t.Fatalf("envelope decoded wrong: %+v", r)
	}
	if r.RepoLabel() != "sk-studio" {
		t.Errorf("RepoLabel = %q, want sk-studio", r.RepoLabel())
	}
	if r.StatusText() != "Paused: owner input wanted" {
		t.Errorf("StatusText = %q — the reason a picker exists must not be dropped", r.StatusText())
	}
	if r.ShortRunID() != "1a2b3c4d" {
		t.Errorf("ShortRunID = %q, want the eight-char form every other surface prints", r.ShortRunID())
	}
}

func TestRepoLabelNeverBlank(t *testing.T) {
	for _, tc := range []struct {
		run  FleetRun
		want string
	}{
		{FleetRun{Repo: `C:\code\conductor\`}, "conductor"},
		{FleetRun{Repo: "/home/me/sites/blog"}, "blog"},
		{FleetRun{PlanName: "nameless repo"}, "nameless repo"},
		{FleetRun{Port: 4319}, "port 4319"},
	} {
		if got := tc.run.RepoLabel(); got != tc.want {
			t.Errorf("RepoLabel(%+v) = %q, want %q", tc.run, got, tc.want)
		}
	}
}

func TestPickerStartsOnTheRunInThisDirectory(t *testing.T) {
	runs := twoRunFleet()
	runs[0].Self, runs[1].Self = false, true
	if got := NewPicker(runs).cursor; got != 1 {
		t.Fatalf("cursor = %d, want 1 — the self run is the row the caller most likely means", got)
	}
	runs[1].Self = false
	if got := NewPicker(runs).cursor; got != 0 {
		t.Fatalf("cursor = %d, want 0 when no run is self", got)
	}
}

// pick drives real key strings through the picker, the way a terminal would.
func pick(t *testing.T, p PickerModel, keys ...string) PickerModel {
	t.Helper()
	for _, k := range keys {
		m, _ := p.handleKey(k)
		next, ok := m.(PickerModel)
		if !ok {
			t.Fatalf("handleKey(%q) returned %T", k, m)
		}
		p = next
	}
	return p
}

func TestPickerNavigationAndSelection(t *testing.T) {
	tests := []struct {
		name    string
		keys    []string
		wantOK  bool
		wantURL string
	}{
		{"enter takes the highlighted run", []string{"enter"}, true, "http://127.0.0.1:4317"},
		{"down then enter", []string{"down", "enter"}, true, "http://127.0.0.1:4318"},
		{"j then enter", []string{"j", "enter"}, true, "http://127.0.0.1:4318"},
		{"down clamps at the last row", []string{"down", "down", "down", "enter"}, true, "http://127.0.0.1:4318"},
		{"up clamps at the first row", []string{"up", "up", "enter"}, true, "http://127.0.0.1:4317"},
		{"k walks back", []string{"j", "k", "enter"}, true, "http://127.0.0.1:4317"},
		{"end jumps to the last", []string{"end", "enter"}, true, "http://127.0.0.1:4318"},
		{"home jumps back", []string{"end", "home", "enter"}, true, "http://127.0.0.1:4317"},
		{"a digit attaches in one key", []string{"2"}, true, "http://127.0.0.1:4318"},
		{"a digit past the fleet does nothing", []string{"7"}, false, ""},
		{"esc quits without attaching", []string{"esc"}, false, ""},
		{"q quits without attaching", []string{"q"}, false, ""},
		{"ctrl+c quits without attaching", []string{"ctrl+c"}, false, ""},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			p := pick(t, NewPicker(twoRunFleet()), tc.keys...)
			run, ok := p.Chosen()
			if ok != tc.wantOK {
				t.Fatalf("Chosen() ok = %v, want %v", ok, tc.wantOK)
			}
			if ok && run.BaseURL != tc.wantURL {
				t.Fatalf("attached to %q, want %q", run.BaseURL, tc.wantURL)
			}
		})
	}
}

// The chosen run must carry ITS OWN token and state dir — attaching to sk-studio with this repo's
// token would silently 401 every write, and with this repo's state dir the last-run card would
// describe a run nobody is looking at.
func TestChosenRunCarriesItsOwnTokenAndStateDir(t *testing.T) {
	p := pick(t, NewPicker(twoRunFleet()), "2")
	run, ok := p.Chosen()
	if !ok {
		t.Fatal("nothing chosen")
	}
	if run.Token != "" {
		t.Errorf("token = %q, want empty — this fixture's second run has none", run.Token)
	}
	if run.StateDir != "C:/code/sk-studio/.conductor" {
		t.Errorf("state dir = %q, want the chosen run's own", run.StateDir)
	}

	p = pick(t, NewPicker(twoRunFleet()), "1")
	run, _ = p.Chosen()
	if run.Token != "tok-conductor" || run.StateDir != "C:/code/conductor/.conductor" {
		t.Errorf("run 1 carried %q / %q", run.Token, run.StateDir)
	}
}

func TestPickerRendersEveryRunAndItsFacts(t *testing.T) {
	p := NewPicker(twoRunFleet())
	p.width, p.height = 120, 30
	out := p.Render()

	for _, want := range []string{
		"conductor", "sk-studio", // both repos
		"SF5", "B3", // both stages
		"Running", "owner input wanted", // the status that matters
		"4317", "4318", // both ports
		"$12.34", "$3.10", // cost
		"attach the face to which run?",
		"2 runs answering on this machine",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("picker frame is missing %q\n%s", want, out)
		}
	}
	// The highlighted run's detail line, and the self marker on the run in this directory.
	for _, want := range []string{"35412", "8f31c0a9", "read/write", "18/24 checkpoints", "*"} {
		if !strings.Contains(out, want) {
			t.Errorf("picker detail is missing %q\n%s", want, out)
		}
	}
	// Move the cursor: the detail follows it, and the run with no token says so.
	moved := pick(t, p, "down")
	if got := moved.Render(); !strings.Contains(got, "read-only (no token)") || !strings.Contains(got, "22104") {
		t.Errorf("detail did not follow the cursor\n%s", got)
	}
}

// A token must never be painted. It reaches the Face in an env var precisely so it stays off screens
// and process listings; rendering it would put it in every screenshot and every terminal scrollback.
func TestPickerNeverRendersAToken(t *testing.T) {
	p := NewPicker(twoRunFleet())
	p.width, p.height = 200, 50
	if out := p.Render(); strings.Contains(out, "tok-conductor") {
		t.Fatalf("the write token is on screen:\n%s", out)
	}
}

// The frame invariant the dashboard has, applied to the pre-flight screen: never hand the renderer
// more than the window. A picker that wraps at 80 columns pushes its own hint line off the bottom.
func TestPickerFrameFitsTheWindow(t *testing.T) {
	for _, size := range []struct{ w, h int }{{80, 24}, {100, 30}, {120, 30}, {200, 50}, {60, 20}} {
		p := NewPicker(twoRunFleet())
		p.width, p.height = size.w, size.h
		out := p.Render()
		lines := strings.Split(out, "\n")
		if len(lines) > size.h {
			t.Errorf("%dx%d: %d lines, window has %d", size.w, size.h, len(lines), size.h)
		}
		for i, ln := range lines {
			if w := lipgloss.Width(ln); w > size.w {
				t.Errorf("%dx%d: line %d is %d wide\n%s", size.w, size.h, i, w, out)
			}
		}
	}
}

func TestPickerResizesFromTheWindowMessage(t *testing.T) {
	m, _ := NewPicker(twoRunFleet()).Update(tea.WindowSizeMsg{Width: 132, Height: 40})
	p, ok := m.(PickerModel)
	if !ok {
		t.Fatalf("Update returned %T", m)
	}
	if p.width != 132 || p.height != 40 {
		t.Fatalf("picker size = %dx%d, want 132x40", p.width, p.height)
	}
}

// A fleet of one still gets the screen — the engine only hands one over when it could not decide, so
// a silent attach would be answering a question it just admitted it could not answer.
func TestPickerWithOneRun(t *testing.T) {
	p := NewPicker(twoRunFleet()[:1])
	p.width, p.height = 100, 30
	out := p.Render()
	if !strings.Contains(out, "1 run answering on this machine") {
		t.Errorf("singular count missing:\n%s", out)
	}
	if run, ok := pick(t, p, "enter").Chosen(); !ok || run.Port != 4317 {
		t.Errorf("enter on a one-run fleet did not attach")
	}
}
