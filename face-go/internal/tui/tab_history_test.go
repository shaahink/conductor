package tui

// SF1.3's live proof. Everything else in this package drives `fakeSource` — a fixture written for
// tests. This file drives `api.NewDemoSource()`, the source `conductor-face --demo` actually
// constructs, through the model `--demo` actually builds, and prints the frames. That is the closest
// this repo can get to "what a reviewer sees": the shipped binary refuses to start when stdout is
// not a TTY (`conductor-face needs an interactive terminal`), by design, so a frame captured from
// the real demo source through the real View() is the honest substitute for a screenshot — and
// unlike a screenshot it stays green forever.

import (
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// demoModel builds what `--demo` builds — New(api.NewDemoSource(), true, "(demo)") — and pumps it
// with the demo source's OWN data through the same messages the real fetch commands produce. The
// real program does this from Init()'s command batch, which needs a running tea.Program; the fetches
// themselves are the same calls.
func demoModel(t *testing.T, w, h int) tea.Model {
	t.Helper()
	src := api.NewDemoSource()
	t.Cleanup(src.Close)

	var m tea.Model = New(src, true, "(demo)")
	m, _ = m.Update(tea.WindowSizeMsg{Width: w, Height: h})

	state, err := src.FetchState()
	if err != nil {
		t.Fatalf("demo FetchState: %v", err)
	}
	m, _ = m.Update(MsgStateUpdated{State: state})

	plan, err := src.FetchPlan()
	if err != nil {
		t.Fatalf("demo FetchPlan: %v", err)
	}
	m, _ = m.Update(MsgPlanLoaded{Plan: plan})

	sessions, err := src.FetchSessions()
	if err != nil {
		t.Fatalf("demo FetchSessions: %v", err)
	}
	m, _ = m.Update(MsgSessionsUpdated{Sessions: sessions})

	timeline, err := src.FetchTimeline()
	if err != nil {
		t.Fatalf("demo FetchTimeline: %v", err)
	}
	m, _ = m.Update(MsgTimelineUpdated{Timeline: timeline})

	// The raw stream arrives over SSE, so take it the way the Face does — through the subscription.
	src.SubscribeConsole(func(l api.ConsoleLineDto) {
		m, _ = m.Update(MsgConsoleLine{Line: l})
	}, func(bool) {})
	m, _ = m.Update(MsgEventsConnChanged{Connected: true})
	m, _ = m.Update(MsgTxConnChanged{Connected: true})
	return m
}

// TestDemoDriveOfTheConsolidatedTabs is the SF1.3 evidence artifact: run with -v and the log IS the
// proof. It asserts the four claims the checkpoint makes, each against a real rendered frame.
func TestDemoDriveOfTheConsolidatedTabs(t *testing.T) {
	// 200x50 is the only size where the strip renders every tab's full name — which is the frame
	// that can actually show a reader there are ten of them.
	m := demoModel(t, 200, 50)
	strip := func(tm tea.Model) string {
		return strings.TrimSpace(strings.Split(stripANSI(asModel(tm).View().Content), "\n")[1])
	}

	// (1) Ten tabs, named, in the strip the user reads — not just tabCount in a test.
	t.Logf("--demo tab strip at 200x50:\n    %s", strip(m))
	for _, want := range []string{"h Home", "a Agent", "s History", "o Procs", "e Templates",
		"p Plan", "r Report", "k Knowledge", "g Telegram", "b Kanban"} {
		if !strings.Contains(strip(m), want) {
			t.Errorf("the --demo tab strip does not show %q:\n%s", want, strip(m))
		}
	}
	for _, gone := range []string{"Console", "Timeline", "Sessions", "Dev"} {
		if strings.Contains(strip(m), gone) {
			t.Errorf("the --demo tab strip still shows the folded/deleted %q tab:\n%s", gone, strip(m))
		}
	}

	pane := func(tm tea.Model) string {
		body, help := asModel(tm).paneView()
		return stripANSI(body) + "\n[help] " + help
	}

	// (2) `c` shows the raw agent stdout the Console tab used to — WITH the agent strip over it,
	// which is the thing folding bought and tabbing away used to cost.
	raw := asModel(mustHandle(asModel(m).handleKey("c")))
	t.Logf("--demo `c` (Agent raw stream):\n%s", indentBlock(pane(raw)))
	if !raw.agent.raw || raw.tab != TabAgent {
		t.Fatalf("c did not open Agent's raw stream: tab=%v raw=%v", raw.tab, raw.agent.raw)
	}
	rawBody := stripANSI(mustBody(raw))
	if !strings.Contains(rawBody, `{"type":"system"`) {
		t.Error("the raw stream does not show the demo source's raw agent stdout")
	}
	if !strings.Contains(rawBody, "s12 Deliver") {
		t.Error("the raw stream lost the agent strip — that context is the entire reason this folded " +
			"into Agent instead of being deleted")
	}
	if !strings.Contains(rawBody, "raw lines") {
		t.Error("the raw stream lost its line counter / live-tail marker")
	}

	// (3) `s` and `t` each land on their half of History, and both show the switcher.
	sessions := asModel(mustHandle(asModel(m).handleKey("s")))
	t.Logf("--demo `s` (History · sessions):\n%s", indentBlock(pane(sessions)))
	if sessions.tab != TabHistory || sessions.history.view != historySessions {
		t.Fatalf("s did not open History's sessions view: tab=%v view=%v", sessions.tab, sessions.history.view)
	}

	var spine tea.Model = asModel(mustHandle(asModel(m).handleKey("t")))
	spine, _ = spine.Update(MsgTimelineUpdated{Timeline: mustTimeline(t)})
	spineM := asModel(spine)
	t.Logf("--demo `t` (History · spine):\n%s", indentBlock(pane(spineM)))
	if spineM.tab != TabHistory || spineM.history.view != historyTimeline {
		t.Fatalf("t did not open History's spine view: tab=%v view=%v", spineM.tab, spineM.history.view)
	}
	for _, view := range []Model{sessions, spineM} {
		body := stripANSI(mustBody(view))
		if !strings.Contains(body, "Sessions s") || !strings.Contains(body, "Spine t") {
			t.Errorf("a History view rendered without the switcher — the other view is then folklore:\n%s", body)
		}
	}
	// The two views must actually differ, or "merged" would mean "one of them was deleted".
	if stripANSI(mustBody(sessions)) == stripANSI(mustBody(spineM)) {
		t.Error("History's two views render identically — one of them is not there")
	}

	// (4) The help card is the only place that says where the folded keys went.
	helpM, _ := m.Update(keyMsg("?"))
	helpFrame := stripANSI(asModel(helpM).View().Content)
	t.Logf("--demo `?` (help card):\n%s", indentBlock(helpFrame))
	if !strings.Contains(helpFrame, "folded") {
		t.Error("the help card does not carry the folded row — c and t look deleted to anyone reading it")
	}
}

func mustBody(m Model) string {
	body, _ := m.paneView()
	return body
}

func mustTimeline(t *testing.T) *api.TimelineDto {
	t.Helper()
	src := api.NewDemoSource()
	defer src.Close()
	tl, err := src.FetchTimeline()
	if err != nil {
		t.Fatalf("demo FetchTimeline: %v", err)
	}
	return tl
}

func indentBlock(s string) string {
	return "    " + strings.ReplaceAll(strings.TrimRight(s, "\n"), "\n", "\n    ")
}
