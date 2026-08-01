package tui

// SF2.1: what Home is allowed to say once the control plane is gone, and what it must stop saying.
//
// The screenshot that started this checkpoint showed a COMPLETED run header directly above "No run
// attached. Start one:" — two states in one frame. These drive the real Update loop rather than
// poking fields, because the bug was never in a renderer: it was in which panel believed what.

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/lastrun"
	"conductor-face-go/internal/timefmt"
)

// finishedRun writes a state dir holding a real engine-shaped RUN-SUMMARY.md and returns its path.
func finishedRun(t *testing.T) string {
	t.Helper()
	dir := filepath.Join(t.TempDir(), ".conductor")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	const summary = "# Run summary — sarban\n" +
		"\n" +
		"_Written 2026-07-15 09:30 UTC when the run reached Completed._\n" +
		"\n" +
		"- **Plan:** sarban · run `57d95596e175451c978557813cb5b889`\n" +
		"- **Repo:** C:/code/conductor · branch `feat/sarban` · HEAD `0230036`\n" +
		"- **Outcome:** Completed\n" +
		"- **Wall clock:** 2026-07-14 22:48 UTC → 2026-07-15 09:30 UTC · 10h 42m\n" +
		"- **Sessions:** 9 (7 deliver, 2 verify)\n" +
		"- **Checkpoints:** 7/24 done\n" +
		"- **Spend:** $12.40 total (agent $12.11 + gates $0.29) · cap $80.00 (16% used)\n" +
		"\n" +
		"## Stages\n"
	if err := os.WriteFile(filepath.Join(dir, lastrun.FileName), []byte(summary), 0o644); err != nil {
		t.Fatal(err)
	}
	return dir
}

// pinClock freezes the Face's clock so an age is a fact in a test rather than a race with wall time.
func pinClock(t *testing.T, at time.Time) {
	t.Helper()
	timefmt.Now = func() time.Time { return at }
	t.Cleanup(func() { timefmt.Now = time.Now })
}

// The end-to-end path, through the real handlers: a Face that was watching, the engine dies, and the
// card appears carrying facts the dead engine can no longer serve. Nothing here pokes m.lastRun.
func TestHomeShowsTheLastRunOnceTheEngineIsGone(t *testing.T) {
	pinClock(t, time.Date(2026, 7, 15, 11, 30, 0, 0, time.UTC))
	dir := finishedRun(t)
	m := liveModel()
	m.width, m.height = 110, 40
	// The engine names its own state dir on every /state, and that answer outranks the walk-up guess
	// in main.go — so the realistic wire is the served one, pointed at this fixture's run.
	state := fixedState()
	state.StateDir = dir
	m = step(t, m, MsgStateUpdated{State: state})
	if m.stateDir != dir {
		t.Fatalf("the engine-served state dir must win, got %q", m.stateDir)
	}

	// While the engine answers, the card must NOT render: /state is fresher than any file, and two
	// panels answering "what is this run" is the failure this checkpoint exists to fix.
	if got := stripANSI(homeText(m.renderHomeLastRun(m.paneCols()))); strings.Contains(got, "Completed") {
		t.Errorf("the last-run card rendered while the engine was still answering:\n%s", got)
	}

	// The engine goes away. MsgFetchError returns the disk read as a command; run it and feed the
	// result back exactly as the program loop would.
	next, cmd := m.Update(MsgFetchError{Err: "dial tcp 127.0.0.1:4317: connectex: actively refused"})
	m = next.(Model)
	if cmd == nil {
		t.Fatal("losing the engine must schedule the summary read — that is the only thing left to read")
	}
	msg := cmd()
	loaded, ok := msg.(MsgLastRunLoaded)
	if !ok {
		t.Fatalf("the summary read returned %T, want MsgLastRunLoaded", msg)
	}
	if loaded.Summary == nil {
		t.Fatal("the summary the engine wrote was not parsed; the card has nothing to show")
	}
	m = step(t, m, loaded)

	got := stripANSI(homeText(m.renderHomeLastRun(m.paneCols())))
	for _, want := range []string{
		"COMPLETED",      // the outcome the engine recorded, through the shared status badge
		"ended 2h ago",   // 09:30 UTC read at 11:30 UTC
		"sarban",         // the plan
		"7/24 done",      // how far it got
		"$12.40 total",   // what it cost
		"RUN-SUMMARY.md", // and where every one of those facts came from
	} {
		if !strings.Contains(got, want) {
			t.Errorf("the last-run card is missing %q:\n%s", want, got)
		}
	}
}

// Screenshot critique #1. The instructions are correct on a truly cold Face and a lie on one that
// has been watching a run all morning; the gate is "do we know of a run at all", nothing else.
func TestStartARunInstructionsOnlyWhenThereIsNoRunToShow(t *testing.T) {
	pinClock(t, time.Date(2026, 7, 15, 11, 30, 0, 0, time.UTC))
	const cta = "No run attached. Start one:"

	cold := liveModel()
	cold.width, cold.height = 110, 40
	cold = step(t, cold, MsgFetchError{Err: "connectex: actively refused"})
	if !strings.Contains(stripANSI(homeText(cold.renderHomeServer(cold.paneCols()))), cta) {
		t.Error("a Face that knows of no run at all must still say how to start one")
	}

	// (a) it polled a run before the engine died.
	watched := step(t, cold, MsgStateUpdated{State: fixedState()},
		MsgFetchError{Err: "connectex: actively refused"})
	if got := stripANSI(homeText(watched.renderHomeServer(watched.paneCols()))); strings.Contains(got, cta) {
		t.Errorf("Home told a watcher to start the run it is already showing:\n%s", got)
	}

	// (b) it never polled, but found a finished run's summary on disk. Same answer: there IS a run.
	found := liveModel().WithStateDir(finishedRun(t))
	found.width, found.height = 110, 40
	found = step(t, found, MsgFetchError{Err: "connectex: actively refused"})
	summary, err := lastrun.Load(found.stateDir)
	if err != nil || summary == nil {
		t.Fatalf("cold-start read of the summary failed: %v", err)
	}
	found = step(t, found, MsgLastRunLoaded{Summary: summary})
	if got := stripANSI(homeText(found.renderHomeServer(found.paneCols()))); strings.Contains(got, cta) {
		t.Errorf("Home told a reader to start a run while showing that run's summary:\n%s", got)
	}
}

// A run that died mid-stage must not keep rendering "RUNNING" forever off a stale poll. The badge is
// allowed to be a memory; it is not allowed to be silent about being one.
func TestHomeQualifiesAStaleRunStatus(t *testing.T) {
	pinClock(t, time.Date(2026, 7, 15, 10, 0, 0, 0, time.UTC))
	m := liveModel()
	m.width, m.height = 110, 40
	m = step(t, m, MsgStateUpdated{State: fixedState()})
	if got := stripANSI(m.homeRunStatus(m.data.Plan)); strings.Contains(got, "last seen") {
		t.Errorf("a live run's status needs no qualifier: %q", got)
	}

	pinClock(t, time.Date(2026, 7, 15, 10, 7, 0, 0, time.UTC))
	m = step(t, m, MsgFetchError{Err: "connectex: actively refused"})
	got := stripANSI(m.homeRunStatus(m.data.Plan))
	if !strings.Contains(got, "as last seen 7m ago") {
		t.Errorf("a status held over from a dead engine must say so, with its age: %q", got)
	}
}

// The raw transport error is not deleted, only demoted: a developer screenshotting Home into a bug
// report still needs the verbatim string, and it belongs with the other wiring diagnostics.
func TestTheRawDialErrorSurvivesInWiringNotOnTheEngineLine(t *testing.T) {
	m := liveModel()
	m.width, m.height = 110, 40
	raw := "dial tcp 127.0.0.1:4317: connectex: No connection could be made because the target machine actively refused it."
	m = step(t, m, MsgStateUpdated{State: fixedState()}, MsgFetchError{Err: raw})

	server := stripANSI(homeText(m.renderHomeServer(m.paneCols())))
	if strings.Contains(server, "connectex") {
		t.Errorf("the raw dial error is back on the engine line:\n%s", server)
	}
	if !strings.Contains(server, "not running") {
		t.Errorf("the engine line must name the state in English:\n%s", server)
	}
	if wiring := stripANSI(homeText(m.homeWiring(m.paneCols()))); !strings.Contains(wiring, "connectex") {
		t.Errorf("the verbatim error must survive in Wiring for a bug report:\n%s", wiring)
	}
}

// Home cannot scroll (U3.2), so anything new has to declare what it sheds. The card is a mix: its
// outcome and progress are the point of the page when offline, its provenance rows are not.
func TestTheLastRunCardDeclaresItsShedTiers(t *testing.T) {
	pinClock(t, time.Date(2026, 7, 15, 11, 30, 0, 0, time.UTC))
	m := liveModel().WithStateDir(finishedRun(t))
	m.width, m.height = 110, 40
	summary, _ := lastrun.Load(m.stateDir)
	m = step(t, m, MsgFetchError{Err: "refused"}, MsgLastRunLoaded{Summary: summary})

	rows := m.renderHomeLastRun(m.paneCols())
	if len(rows) < 2 {
		t.Fatal("the card rendered nothing to tier")
	}
	var detail int
	for _, l := range rows[1:] { // rows[0] is the header homePanel makes essential
		if l.tier == homeDetail {
			detail++
		}
	}
	if detail == 0 {
		t.Error("no row of the card sheds first — a non-scrolling page cannot afford that")
	}
}

// Demo mode has no engine and no disk state. It must never read a real run's summary into the tour.
func TestDemoNeverShowsARealRunsSummary(t *testing.T) {
	m := New(api.NewDemoSource(), true, "(demo)").WithStateDir(finishedRun(t))
	m.width, m.height = 110, 40
	summary, _ := lastrun.Load(m.stateDir)
	m = step(t, m, MsgLastRunLoaded{Summary: summary})
	if got := stripANSI(homeText(m.renderHomeLastRun(m.paneCols()))); strings.Contains(got, "Completed") {
		t.Errorf("the demo leaked a real run's summary:\n%s", got)
	}
}
