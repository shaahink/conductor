package tui

// KS2.2's frame capture. The checkpoint's claim is that a FINISHED run — no engine, no control plane
// of its own, nothing but a run.db — renders in the real Face: Sessions, the money, the spine, the
// Report. Proving that with a screenshot is not possible here (the shipped binary refuses to start
// when stdout is not a TTY, by design), so this does what tab_history_test.go does for --demo: drives
// the REAL model through the REAL http client against a plane that speaks the archive's contract, and
// prints the frames. Run with -v and the log is the capture.
//
// By default the plane is a stub started here, so the test is hermetic. Point CONDUCTOR_ARCHIVE_URL at
// a running `conductor face --archive <run> --serve` and the same frames are rendered against the real
// engine-served archive instead — which is how this checkpoint's evidence capture was taken.
//
// The other half of the claim is that the Face knows it cannot write: the archive hands over no token,
// so HasWriteToken() is false and Home says writes will be refused rather than offering affordances
// whose every press the plane answers 405.

import (
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// archiveBodies is the archive plane's contract in the small: the same routes, the same field names,
// the same shapes ArchiveControlPlane serves — a finished run with two sessions, a stage rail, money,
// a spine and a ledger. Values are a trimmed transcript of a real archived run.
var archiveBodies = map[string]string{
	"/state": `{"planName":"Karvan core","status":"Completed","stageId":"K7","stageTitle":"Ship the plan",
	 "doneCount":25,"totalCount":32,"totalCostUsd":317.84,"overheadCostUsd":0.28,
	 "tokensInput":4665548,"tokensOutput":2289567,"tokensReasoning":0,
	 "currentCheckpoint":"K7.3","currentCheckpointTitle":"Ship it","gateSummary":"engine-fast:OK",
	 "stages":[{"id":"K1","title":"The ledger stops lying","done":4,"total":4,"state":"confirmed",
	   "checkpoints":[{"id":"K1.1","title":"A rolled-over session records its commits","status":"DONE"}]}],
	 "runId":"df9c4af8044442cca197463d9af7e670","repo":"C:/code/conductor","planDir":"",
	 "sessionNumber":32,"sessionKind":"Deliver","attempt":1,"maxAttempts":0,"sessionElapsedSec":0,
	 "agentActive":false,"sessionCostUsd":11.22,"gates":[],"stateDir":"","provider":"",
	 "costSpent":317.84,"meanSessionCost":9.93,"checkpointsRemaining":7,
	 "windowCostUsd":317.84,"lifetimeCostUsd":317.84,"engineVersion":"0.9.0"}`,
	"/sessions": `{"sessions":[
	 {"number":1,"stageId":"K1","kind":"Deliver","startedUtc":"2026-08-04T21:49:08Z",
	  "endedUtc":"2026-08-04T22:24:50Z","outcome":"Advanced","attempt":1,"resumeCount":0,
	  "gateSummary":"engine-fast:OK","resultSummary":"Landed two K1 checkpoints","commitCount":3,
	  "costUsd":18.06,"tokensIn":219310,"tokensOut":95366,"tokensThink":null,"tokensCache":24653507,
	  "commits":["93bbae5 fix(engine): a rolled-over session records its commits"]},
	 {"number":32,"stageId":"K7","kind":"Deliver","startedUtc":"2026-08-05T13:01:00Z",
	  "endedUtc":"2026-08-05T13:31:40Z","outcome":"Advanced","attempt":1,"resumeCount":0,
	  "gateSummary":"engine-fast:OK","resultSummary":"Shipped","commitCount":1,
	  "costUsd":11.22,"tokensIn":181000,"tokensOut":40000,"tokensThink":null,"tokensCache":9000000,
	  "commits":[]}]}`,
	"/timeline": `{"entries":[
	 {"utc":"2026-08-04T21:49:08Z","kind":"stage","description":"stage K1 entered","stageId":"K1"},
	 {"utc":"2026-08-04T21:49:08Z","kind":"session","description":"session #1 Deliver started","stageId":"K1","sessionNumber":1},
	 {"utc":"2026-08-04T22:24:50Z","kind":"gate","description":"gate engine-fast: pass (1200ms)","stageId":"K1","outcome":"pass"},
	 {"utc":"2026-08-05T13:31:40Z","kind":"session","description":"session #32 finished: Advanced","stageId":"K7","sessionNumber":32,"outcome":"Advanced"}]}`,
	"/ledger": `{"entries":[{"id":198,"sessionNumber":1,"stageId":"K1","kind":"note",
	 "content":"the archive still remembers what this run learned","createdAt":"2026-08-04T22:00:00Z"}]}`,
	"/bugs": `{"bugs":[{"id":7,"title":"a bug the run filed","detail":"still on the record","severity":"high",
	 "status":"open","stageId":"K1","foundSession":1,"createdAt":"2026-08-04T22:00:00Z","updatedAt":"2026-08-04T22:00:00Z"}]}`,
	"/evidence":  `{"artifacts":[],"count":0}`,
	"/scores":    `{"scores":[]}`,
	"/tasks":     `{"tasks":[]}`,
	"/processes": `{"processes":[]}`,
	"/plan": `{"name":"Karvan core","planVersion":0,"planFile":"","gatePolicy":"","workflows":[],
	 "stages":[{"id":"K1","title":"The ledger stops lying","sessions":4}],"gates":[{"name":"engine-fast"}],
	 "limits":{"stallMinutes":0,"sessionTimeoutMinutes":0,"verifierThreshold":0}}`,
}

// stubArchivePlane answers reads and refuses every write the way ArchiveControlPlane does — 405 with
// the state of the run, never a 401 pointing at a token that does not exist.
func stubArchivePlane(t *testing.T) string {
	t.Helper()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			w.Header().Set("Allow", "GET")
			w.WriteHeader(http.StatusMethodNotAllowed)
			_, _ = w.Write([]byte(`{"accepted":false,"reason":"this run is finished"}`))
			return
		}
		body, ok := archiveBodies[r.URL.Path]
		if !ok {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(body))
	}))
	t.Cleanup(server.Close)
	return server.URL
}

// archiveModel builds what `conductor face --archive` builds: New over a live source carrying NO
// token, pumped with the archive's own answers through the same messages the fetch commands produce.
func archiveModel(t *testing.T, w, h int) tea.Model {
	t.Helper()
	url := os.Getenv("CONDUCTOR_ARCHIVE_URL")
	if url == "" {
		url = stubArchivePlane(t)
	}
	// No token, because an archive plane has none to mint. This is the whole read-only guarantee on
	// this side of the wire.
	src := api.NewLiveSourceWithToken(url, "")
	t.Cleanup(src.Close)

	var m tea.Model = New(src, false, url)
	m, _ = m.Update(tea.WindowSizeMsg{Width: w, Height: h})

	state, err := src.FetchState()
	if err != nil {
		t.Fatalf("archive FetchState: %v", err)
	}
	m, _ = m.Update(MsgStateUpdated{State: state})

	plan, err := src.FetchPlan()
	if err != nil {
		t.Fatalf("archive FetchPlan: %v", err)
	}
	m, _ = m.Update(MsgPlanLoaded{Plan: plan})

	sessions, err := src.FetchSessions()
	if err != nil {
		t.Fatalf("archive FetchSessions: %v", err)
	}
	m, _ = m.Update(MsgSessionsUpdated{Sessions: sessions})

	timeline, err := src.FetchTimeline()
	if err != nil {
		t.Fatalf("archive FetchTimeline: %v", err)
	}
	m, _ = m.Update(MsgTimelineUpdated{Timeline: timeline})

	ledger, err := src.FetchLedger()
	if err != nil {
		t.Fatalf("archive FetchLedger: %v", err)
	}
	bugs, err := src.FetchBugs()
	if err != nil {
		t.Fatalf("archive FetchBugs: %v", err)
	}
	evidence, err := src.FetchEvidence()
	if err != nil {
		t.Fatalf("archive FetchEvidence: %v", err)
	}
	m, _ = m.Update(MsgKnowledgeUpdated{Ledger: ledger, Bugs: bugs, Evidence: evidence})

	scores, err := src.FetchScores()
	if err != nil {
		t.Fatalf("archive FetchScores: %v", err)
	}
	m, _ = m.Update(MsgReportScores{Result: scores})
	return m
}

// TestArchiveFaceRendersAFinishedRun is the KS2.2 evidence artifact: -v and the log IS the capture.
func TestArchiveFaceRendersAFinishedRun(t *testing.T) {
	m := archiveModel(t, 200, 50)
	pane := func(tm tea.Model) string {
		body, help := asModel(tm).paneView()
		return stripANSI(body) + "\n[help] " + help
	}

	// Home — the money. A finished run's cost, its checkpoint count and its stage rail, out of a
	// database, with nothing running.
	home := asModel(mustHandle(asModel(m).handleKey("h")))
	homeFrame := stripANSI(homeFrameOf(home))
	t.Logf("archive `h` (Home · money and wiring):\n%s", indentBlock(homeFrame))
	for _, want := range []string{"$317.84", "COMPLETED", "25/32 checkpoints", "df9c4af8"} {
		if !strings.Contains(homeFrame, want) {
			t.Errorf("Home does not carry %q for the archived run:\n%s", want, homeFrame)
		}
	}
	// The read-only affordance: the Face states it cannot write BEFORE anyone presses anything.
	if !strings.Contains(homeFrame, "writes will be refused") {
		t.Errorf("Home does not say the archive cannot be written to:\n%s", homeFrame)
	}

	// History · sessions — every session the run lived, with its money and its commits.
	sessions := asModel(mustHandle(asModel(m).handleKey("s")))
	sessionsFrame := stripANSI(mustBody(sessions))
	t.Logf("archive `s` (History · sessions):\n%s", indentBlock(pane(sessions)))
	if sessions.tab != TabHistory || sessions.history.view != historySessions {
		t.Fatalf("s did not open History's sessions view: tab=%v view=%v", sessions.tab, sessions.history.view)
	}
	for _, want := range []string{"#1", "#32", "Advanced", "3 commits", "engine-fast:OK"} {
		if !strings.Contains(sessionsFrame, want) {
			t.Errorf("the sessions view does not show %q from the archive:\n%s", want, sessionsFrame)
		}
	}

	// History · spine — the timeline, folded from the archived event log.
	spine := asModel(mustHandle(asModel(m).handleKey("t")))
	spineFrame := stripANSI(mustBody(spine))
	t.Logf("archive `t` (History · spine):\n%s", indentBlock(pane(spine)))
	if spine.tab != TabHistory || spine.history.view != historyTimeline {
		t.Fatalf("t did not open History's spine view: tab=%v view=%v", spine.tab, spine.history.view)
	}
	if !strings.Contains(spineFrame, "stage K1 entered") {
		t.Errorf("the spine is not showing the archived timeline:\n%s", spineFrame)
	}

	// Report — the owner's page, rendered from the same archive.
	report := asModel(mustHandle(asModel(m).handleKey("r")))
	reportFrame := stripANSI(mustBody(report))
	t.Logf("archive `r` (Report):\n%s", indentBlock(pane(report)))
	if report.tab != TabReport {
		t.Fatalf("r did not open the Report tab: tab=%v", report.tab)
	}
	if !strings.Contains(reportFrame, "Karvan core") {
		t.Errorf("the Report does not name the archived run:\n%s", reportFrame)
	}

	// Knowledge — the ledger and the bugs the run recorded, which is the "money/ledger" half.
	knowledge := asModel(mustHandle(asModel(m).handleKey("k")))
	knowledgeFrame := stripANSI(mustBody(knowledge))
	t.Logf("archive `k` (Knowledge · ledger and bugs):\n%s", indentBlock(pane(knowledge)))
	if !strings.Contains(knowledgeFrame, "the archive still remembers") &&
		!strings.Contains(knowledgeFrame, "a bug the run filed") {
		t.Errorf("Knowledge is not showing the archived ledger or bugs:\n%s", knowledgeFrame)
	}
}

// The guarantee, stated on its own: a Face on an archive holds no write token. Every affordance in
// this TUI that offers a write is gated on this one answer.
func TestArchiveFaceHoldsNoWriteToken(t *testing.T) {
	m := asModel(archiveModel(t, 120, 40))
	if m.source.HasWriteToken() {
		t.Fatal("a Face attached to a finished run claims it can write")
	}
}

func homeFrameOf(m Model) string {
	body, _ := m.paneView()
	return body
}
