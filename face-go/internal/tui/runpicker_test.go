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

// liveFleet is the fleet this machine ACTUALLY had at 2026-08-01T13:26Z, copied field for field from
// `conductor ps --json` (.conductor/evidence/SF5/SF5.4-picker-live.log): two engines, two ports, and
// the detail the whole design turns on — the run in THIS repo has no discovery file, so it has no
// write token and the picker must say so, while the other repo's run does.
//
// The tokens are the one thing not copied: a real write token is never written to a test fixture, a
// golden, or an evidence file. The picker never renders it either — TestPickerNeverRendersAToken.
func liveFleet() []FleetRun {
	return []FleetRun{
		{
			Repo: "C:/code/conductor", PlanName: "Sarban face - the watcher and the surfaces",
			RunID: "8cefa5de8f164848bd42b275e14ba9cf", Status: "Running", Port: 4317, Pid: 35412,
			StageID: "SF5", StageTitle: "Supervision without a polling meter",
			Done: 18, Total: 24, CostUsd: 243.47,
			BaseURL: "http://127.0.0.1:4317", StateDir: `C:/code/conductor\.conductor`,
			Token: "", Self: true,
		},
		{
			Repo: "C:/Code/sk-studio", PlanName: "NINE STREETS",
			RunID: "7951c3ca149a4c12a5a7fb973bbea1bf", Status: "Running", Port: 4318, Pid: 19056,
			StageID: "E", StageTitle: "The three that mean it is not a game yet",
			Done: 29, Total: 46, CostUsd: 280.81,
			BaseURL: "http://127.0.0.1:4318", StateDir: `C:/Code/sk-studio\.conductor`,
			Token: "redacted", Self: false,
		},
	}
}

// The frame a reviewer would see, pinned. checkGolden prints it, which is how a frame gets read on a
// machine with no TTY to run the Face in.
func TestGoldenRunPicker(t *testing.T) {
	p := NewPicker(liveFleet())
	p.width, p.height = 100, 24
	checkGolden(t, "run_picker_100x24", p.Render())

	checkGolden(t, "run_picker_second_row", pick(t, p, "down").Render())

	narrow := NewPicker(liveFleet())
	narrow.width, narrow.height = 80, 24
	checkGolden(t, "run_picker_80x24", narrow.Render())
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

// ---------------------------------------------------------------------------------------------
// K3.2 — the picker also lists what this machine remembers.

// pastFleet is the history this machine ACTUALLY had, read back from the state catalogue with
// `conductor history` against a scratch home holding an imported copy of this repo's run.db
// (.conductor/evidence/K3/K3.2-history.md). Three real runs, real costs, real checkpoint counts.
func pastFleet() []PastRun {
	return []PastRun{
		{
			Repo: "C:/code/conductor", PlanName: "Sarban face - the watcher and the surfaces",
			RunID: "8cefa5de8f164848bd42b275e14ba9cf", Status: "Completed",
			Done: 24, Total: 24, CostUsd: 297.24,
			LastActivityUtc: "2026-08-02T09:11:04Z",
			RunDb:           `C:/Users/shahi/AppData/Local/conductor/runs/conductor-sarban-face-e0536519/run.db`,
		},
		{
			Repo: "C:/code/conductor", PlanName: "Sarban core - the engine says what it knows",
			RunID: "e9e21d10aedf4390a1580ac6930bac3e", Status: "Completed",
			Done: 26, Total: 26, CostUsd: 360.14,
			LastActivityUtc: "2026-07-31T17:20:11Z",
			RunDb:           `C:/Users/shahi/AppData/Local/conductor/runs/conductor-sarban-core-a13f0b28/run.db`,
		},
	}
}

func TestParseFleetReadsPastRuns(t *testing.T) {
	raw := `{"runs":[{"repo":"C:/code/conductor","runId":"abc","port":4317,"baseUrl":"http://127.0.0.1:4317"}],
	         "past":[{"repo":"C:/code/old","planName":"Sarban core","runId":"e9e21d10","status":"Completed",
	                  "done":26,"total":26,"costUsd":360.14,"runDb":"C:/home/runs/x/run.db"}]}`
	f, err := ParseFleet(raw)
	if err != nil {
		t.Fatalf("ParseFleet: %v", err)
	}
	if len(f.Past) != 1 {
		t.Fatalf("past runs = %d, want 1", len(f.Past))
	}
	p := f.Past[0]
	if p.RunID != "e9e21d10" || p.Done != 26 || p.CostUsd != 360.14 || p.RunDb != "C:/home/runs/x/run.db" {
		t.Errorf("past run decoded wrong: %+v", p)
	}
	if p.RepoLabel() != "old" {
		t.Errorf("RepoLabel() = %q, want %q", p.RepoLabel(), "old")
	}
}

// An envelope with no past runs is the normal case on a machine that has never had one, and it must
// not change the screen at all.
func TestPickerWithoutHistoryIsUnchanged(t *testing.T) {
	plain := NewPicker(liveFleet())
	plain.width, plain.height = 100, 24
	withEmpty := NewPicker(liveFleet()).WithPast(nil)
	withEmpty.width, withEmpty.height = 100, 24
	if plain.Render() != withEmpty.Render() {
		t.Error("an empty history changed the frame")
	}
}

func TestPickerListsPastRunsUnderTheLiveOnes(t *testing.T) {
	p := NewPicker(liveFleet()).WithPast(pastFleet())
	p.width, p.height = 100, 30
	frame := p.Render()

	if !strings.Contains(frame, "2 past runs on this machine (read-only)") {
		t.Errorf("history heading missing from frame:\n%s", frame)
	}
	for _, want := range []string{"24/24", "$297.24", "26/26", "$360.14"} {
		if !strings.Contains(frame, want) {
			t.Errorf("frame is missing %q:\n%s", want, frame)
		}
	}
	// Order matters: the attachable runs come first, history after.
	if strings.Index(frame, "past runs on this machine") < strings.Index(frame, "4317") {
		t.Error("history section rendered above the live runs")
	}
}

// KS2.2 — enter on a finished run OPENS it. Before, it printed a note naming another command and did
// nothing, which is the one row in this screen that answered a keypress with a suggestion. The run has
// no control plane of its own, so the answer leaves by a different door: ChosenPast, which the engine
// turns into a read-only archive plane over that run's run.db.
func TestEnterOnAPastRunOpensTheReadOnlyArchive(t *testing.T) {
	p := NewPicker(liveFleet()).WithPast(pastFleet())
	p.width, p.height = 100, 30

	p = pick(t, p, "end")
	if p.cursor != len(p.runs)+len(p.past)-1 {
		t.Fatalf("end put the cursor at %d, want the last past row", p.cursor)
	}
	if !strings.Contains(p.Render(), "read-only archive") {
		t.Errorf("detail row does not say the row opens read-only:\n%s", p.Render())
	}

	after, cmd := p.handleKey("enter")
	next, ok := after.(PickerModel)
	if !ok {
		t.Fatalf("handleKey returned %T", after)
	}
	if cmd == nil {
		t.Error("enter on a past run did not end the picker")
	}
	if _, chosen := next.Chosen(); chosen {
		t.Error("a past run was handed over as a LIVE run to attach to")
	}
	past, opened := next.ChosenPast()
	if !opened {
		t.Fatal("enter on a past run chose nothing to open")
	}
	want := pastFleet()[len(pastFleet())-1]
	if past.RunID != want.RunID {
		t.Errorf("opened run %q, want %q", past.RunID, want.RunID)
	}
	// The whole point of the read-only route: nothing about a past row can carry a write token.
	if past.RunDb == "" {
		t.Error("the chosen past run names no database for the engine to serve")
	}
}

// KS2.2 — a run this machine remembers and can no longer READ is still a row. The engine used to drop
// those before the envelope was built, so a deleted run.db looked exactly like a run that had never
// existed, and the precise refusal ("that run's database is gone — nothing at <path>") was reachable
// only by typing the slug by hand. The row lists, says what is wrong, and hands its SLUG back.
func TestAPastRunWhoseDatabaseIsGoneStillListsAndSaysWhy(t *testing.T) {
	gone := PastRun{
		Repo: "C:/code/vanished", PlanName: "a plan whose store went away",
		RunID: "", Status: "gone", Selector: "conductor-vanished-a13f0b28",
		Problem: `that run's database is gone — nothing at C:/home/runs/conductor-vanished-a13f0b28/run.db.`,
		RunDb:   `C:/home/runs/conductor-vanished-a13f0b28/run.db`,
	}
	p := NewPicker(liveFleet()).WithPast(append(pastFleet(), gone))
	p.width, p.height = 100, 30

	if gone.Readable() {
		t.Error("a row with a problem claims to be readable")
	}
	if gone.OpenWith() != gone.Selector {
		t.Errorf("OpenWith() = %q, want the slug %q", gone.OpenWith(), gone.Selector)
	}

	frame := p.Render()
	if !strings.Contains(frame, "3 past runs on this machine (read-only)") {
		t.Errorf("the unreadable row is not listed:\n%s", frame)
	}
	if !strings.Contains(frame, "vanished") {
		t.Errorf("the unreadable row has no label:\n%s", frame)
	}

	p = pick(t, p, "end")
	detail := p.Render()
	if !strings.Contains(detail, "cannot be opened") {
		t.Errorf("the detail row does not say the run cannot be opened:\n%s", detail)
	}
	if strings.Contains(detail, "read-only archive (served from run.db)") {
		t.Errorf("the detail row promises an archive it cannot serve:\n%s", detail)
	}

	after, cmd := p.handleKey("enter")
	if cmd == nil {
		t.Error("enter on an unreadable row did not end the picker")
	}
	next := after.(PickerModel)
	chosen, opened := next.ChosenPast()
	if !opened {
		t.Fatal("enter on an unreadable row chose nothing — the refusal is unreachable again")
	}
	if chosen.OpenWith() != gone.Selector {
		t.Errorf("handed back %q, want the slug %q", chosen.OpenWith(), gone.Selector)
	}
}

// A live row must never come back through the archive door, and a picker nobody pressed enter on
// must not claim either kind of choice.
func TestChosenPastIsEmptyUntilAPastRowIsChosen(t *testing.T) {
	p := NewPicker(liveFleet()).WithPast(pastFleet())
	if _, opened := p.ChosenPast(); opened {
		t.Error("a fresh picker claims to have opened an archive")
	}
	if _, opened := pick(t, p, "enter").ChosenPast(); opened {
		t.Error("enter on a LIVE row came back as an archive choice")
	}
	if _, opened := pick(t, p, "esc").ChosenPast(); opened {
		t.Error("quitting came back as an archive choice")
	}
}

// Number keys are the attach shortcut. They must never land on a row that cannot be attached to,
// however far down the cursor can walk.
func TestNumberKeysNeverReachAPastRun(t *testing.T) {
	p := NewPicker(liveFleet()[:1]).WithPast(pastFleet())
	p.width, p.height = 100, 30
	for _, k := range []string{"2", "3", "9"} {
		if _, chosen := pick(t, p, k).Chosen(); chosen {
			t.Errorf("key %q attached to something", k)
		}
	}
	if r, chosen := pick(t, p, "1").Chosen(); !chosen || r.Port != 4317 {
		t.Error("key 1 no longer attaches to the only live run")
	}
}

func TestGoldenRunPickerWithHistory(t *testing.T) {
	p := NewPicker(liveFleet()).WithPast(pastFleet())
	p.width, p.height = 100, 30
	checkGolden(t, "run_picker_with_history", p.Render())

	onPast := pick(t, p, "end")
	checkGolden(t, "run_picker_past_row_selected", onPast.Render())
}
