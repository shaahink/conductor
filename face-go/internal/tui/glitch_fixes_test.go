package tui

// Regressions for the defects the U3.2 glitch pass found by rendering every tab at 132x40 / 100x30 /
// 80x24 and reading the frames. Each test names the dogfood-appendix item it pins, or the frame that
// exposed it.

import (
	"errors"
	"fmt"
	"strings"
	"testing"
	"time"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

// TestHomeKeepsItsAnswerAtEverySize is the one that mattered most. Home is the landing page and owns
// no keys by design, so it cannot scroll — and its body was a fixed 28 rows against a pane of 24
// (100x30) and 18 (80x24). The clamp ate the BOTTOM, so the section telling a newcomer what to press
// was missing at both, and size_80x24.golden had been pinning that clipped page since it was written.
func TestHomeKeepsItsAnswerAtEverySize(t *testing.T) {
	for _, size := range glitchSizes {
		t.Run(fmt.Sprintf("%dx%d", size.w, size.h), func(t *testing.T) {
			m := worstCaseModel(size.w, size.h)
			m = asModel(mustHandle(asModel(m).handleKey("h")))

			body, _ := asModel(m).paneView()
			if got, budget := lipgloss.Height(body), asModel(m).paneRows(); got > budget {
				t.Errorf("Home renders %d rows into a %d-row pane — the overflow is silently clipped, "+
					"and Home cannot scroll", got, budget)
			}

			plain := stripANSI(body)
			// The landing's answer, in the order it is asked: what is running, where, what next.
			for _, want := range []string{"Server", "Run", "Workspace", "Next steps", "repo"} {
				if !strings.Contains(plain, want) {
					t.Errorf("Home dropped %q at %dx%d — detail may shed, the answer may not:\n%s",
						want, size.w, size.h, plain)
				}
			}
		})
	}
}

// TestHomeShedsDetailBeforeAnswer pins the ORDER of the shedding, which is the whole design: a
// diagnostic row goes before a row the page exists to show.
func TestHomeShedsDetailBeforeAnswer(t *testing.T) {
	wide := worstCaseModel(132, 40)
	wide = asModel(mustHandle(asModel(wide).handleKey("h")))
	wideBody, _ := asModel(wide).paneView()

	narrow := worstCaseModel(80, 24)
	narrow = asModel(mustHandle(asModel(narrow).handleKey("h")))
	narrowBody, _ := asModel(narrow).paneView()

	// A tall window sheds nothing — the detail is all still there.
	for _, want := range []string{"state dir", "tokens", "streams"} {
		if !strings.Contains(stripANSI(wideBody), want) {
			t.Errorf("132x40 has room for everything but dropped %q", want)
		}
	}
	// A short one sheds exactly those, and keeps the answer.
	for _, gone := range []string{"state dir", "streams"} {
		if strings.Contains(stripANSI(narrowBody), gone) {
			t.Errorf("80x24 kept the diagnostic %q while the page was over budget", gone)
		}
	}
	if !strings.Contains(stripANSI(narrowBody), "Next steps") {
		t.Error("80x24 shed Next steps — that is the answer, not detail")
	}
}

// TestHomeSectionNeverRendersAsAnOrphanHeader: shedding must not leave a heading with nothing under
// it. An empty "Workspace" header is worse than no Workspace section.
func TestHomeSectionNeverRendersAsAnOrphanHeader(t *testing.T) {
	sections := [][]homeLine{
		{hLine("Server", homeEssential), hLine("only-row", homeDetail)},
		{hLine("Run", homeEssential), hLine("kept", homeEssential)},
	}
	got := fitHome(sections, 2)
	if strings.Contains(got, "Server") {
		t.Errorf("a section whose every row was shed must go with them, got:\n%s", got)
	}
	if !strings.Contains(got, "Run") || !strings.Contains(got, "kept") {
		t.Errorf("the surviving section is missing:\n%s", got)
	}
}

// TestKanbanEmptyStateSaysWhy pins dogfood appendix item 5. Three states used to render the same
// confident sentence — never fetched, fetch failed, genuinely no cards — and only the last was true.
func TestKanbanEmptyStateSaysWhy(t *testing.T) {
	base := func() Model {
		m := newTestModel()
		m.width, m.height = 132, 40
		m.tab = TabKanban
		return m
	}

	t.Run("never fetched", func(t *testing.T) {
		got := stripANSI(base().renderKanbanEmptyState())
		if !strings.Contains(got, "Loading") {
			t.Errorf("a board that has not fetched yet must not claim to be empty, got %q", got)
		}
	})

	t.Run("fetch failed", func(t *testing.T) {
		m := base()
		m.tasksErr = "Get \"http://127.0.0.1:4317/tasks\": connection refused"
		got := stripANSI(m.renderKanbanEmptyState())
		if !strings.Contains(got, "cannot reach /tasks") {
			t.Errorf("a failed fetch must say so in-pane, got %q", got)
		}
		if !strings.Contains(got, "connection refused") {
			t.Errorf("the reason is the useful half; it must reach the pane, got %q", got)
		}
	})

	t.Run("genuinely empty", func(t *testing.T) {
		m := base()
		m.tasksLoaded = true
		m.data.Connection.Connected = true
		got := stripANSI(m.renderKanbanEmptyState())
		if strings.Contains(got, "cannot reach") || strings.Contains(got, "Loading") {
			t.Errorf("a real empty board must read as good news, got %q", got)
		}
		if !strings.Contains(got, "seeds") {
			t.Errorf("say where cards come from, so an empty board is explained, got %q", got)
		}
	})
}

// TestFailedTaskFetchIsReported: cmdFetchTasks swallowed the error and returned a nil Msg, which is
// what made a broken /tasks indistinguishable from an empty board — the pane could not have said
// otherwise. Pin the wire, not just the render.
func TestFailedTaskFetchIsReported(t *testing.T) {
	var tm tea.Model = newGoldenModel(132, 40)
	tm, _ = tm.Update(MsgTasksUpdated{Tasks: &api.TasksDto{Tasks: kanbanFixtureTasks()}})
	before := len(asModel(tm).data.Tasks)
	if before == 0 {
		t.Fatal("fixture did not load")
	}

	tm, _ = tm.Update(MsgTasksUpdated{Err: errors.New("connection refused")})
	m := asModel(tm)
	if m.tasksErr == "" {
		t.Error("a failed /tasks fetch left no error on the model — the pane cannot report it")
	}
	// A failed poll must not blank a board that is already on screen.
	if len(m.data.Tasks) != before {
		t.Errorf("a failed poll wiped %d cards off the board", before-len(m.data.Tasks))
	}

	// …and a recovered fetch clears the error rather than leaving it stuck.
	tm, _ = tm.Update(MsgTasksUpdated{Tasks: &api.TasksDto{Tasks: kanbanFixtureTasks()}})
	if asModel(tm).tasksErr != "" {
		t.Error("the error survived a successful fetch")
	}
}

// isLiveRule identifies the drawn boundary specifically — dashes AND the label. The Timeline footer
// also says "live" ("5 events · live"), and the detail panel draws its own dashed rule, so matching
// on either alone would find the wrong row.
func isLiveRule(line string) bool {
	return strings.Contains(line, " live ") && strings.Contains(line, "─")
}

func countRules(frame string) int {
	n := 0
	for _, l := range strings.Split(frame, "\n") {
		if isLiveRule(l) {
			n++
		}
	}
	return n
}

// TestKanbanWarnsOverAStaleBoard: cards are deliberately KEPT when a poll fails, so without a banner
// a dead feed reads as a board that has merely stopped moving — appendix item 5's silent lie, just
// with rows on it. The empty-state message cannot cover this case; it never renders.
func TestKanbanWarnsOverAStaleBoard(t *testing.T) {
	var tm tea.Model = newGoldenModel(132, 40)
	tm, _ = tm.Update(keyMsg("b"))
	tm, _ = tm.Update(MsgTasksUpdated{Tasks: &api.TasksDto{Tasks: kanbanFixtureTasks()}})
	tm, _ = tm.Update(MsgTasksUpdated{Err: errors.New("connection refused")})

	body, _ := asModel(tm).paneView()
	plain := stripANSI(body)
	if !strings.Contains(plain, "cannot reach /tasks") {
		t.Errorf("a populated board with a dead feed says nothing:\n%s", plain)
	}
	if !strings.Contains(plain, "not the live graph") {
		t.Error("the banner must say the cards below are stale, not current")
	}
	// The cards are still there — the banner explains them, it does not replace them.
	if !strings.Contains(plain, "TODO") {
		t.Error("the banner blanked the board it was supposed to annotate")
	}
}

// TestTimelineRulesTheLiveBoundary pins dogfood appendix item 6: attaching poured the whole spine in
// at once with no visual break, so it read as an event storm you had just missed.
func TestTimelineRulesTheLiveBoundary(t *testing.T) {
	entry := func(n int) api.TimelineEntryDto {
		return api.TimelineEntryDto{
			Kind: "session", Description: fmt.Sprintf("event %d", n),
			Utc: time.Date(2026, 7, 17, 1, n, 0, 0, time.UTC).Format(time.RFC3339),
		}
	}
	var tm tea.Model = newGoldenModel(132, 40)
	tm, _ = tm.Update(keyMsg("t"))

	// The attach: three events already happened.
	tm, _ = tm.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: []api.TimelineEntryDto{entry(1), entry(2), entry(3)}}})
	// isRule, not a bare "live" search: the pane footer legitimately says "3 events · live", and an
	// assertion that cannot tell the two apart would pass on a rule drawn in the wrong place.
	if countRules(stripANSI(asModel(tm).View().Content)) != 0 {
		t.Error("a rule was drawn with nothing live yet — it would be marking the end of the pane")
	}

	// Two arrive live.
	tm, _ = tm.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: []api.TimelineEntryDto{
		entry(1), entry(2), entry(3), entry(4), entry(5)}}})
	m := asModel(tm)
	if got := m.timelineLiveBoundary(); got != 3 {
		t.Errorf("live boundary = %d, want 3 (the count at attach)", got)
	}

	frame := stripANSI(m.View().Content)
	if countRules(frame) != 1 {
		t.Errorf("want exactly one live rule once events arrived after attach, got %d:\n%s",
			countRules(frame), frame)
	}
	// The rule belongs BETWEEN replayed history and the live tail — not at the top, not the bottom.
	lines := strings.Split(frame, "\n")
	ruleAt, e3At, e4At := -1, -1, -1
	for i, l := range lines {
		switch {
		case isLiveRule(l):
			ruleAt = i
		case strings.Contains(l, "event 3"):
			e3At = i
		case strings.Contains(l, "event 4"):
			e4At = i
		}
	}
	if ruleAt < 0 || e3At < 0 || e4At < 0 {
		t.Fatalf("could not locate rule/history/live rows (rule=%d e3=%d e4=%d)", ruleAt, e3At, e4At)
	}
	if !(e3At < ruleAt && ruleAt < e4At) {
		t.Errorf("the live rule must sit between the last replayed event and the first live one "+
			"(event3=%d rule=%d event4=%d)", e3At, ruleAt, e4At)
	}
}

// TestTimelineHistoryCountIsSetOnceAtAttach: the timeline refetches WHOLESALE on every spine event,
// so if the boundary moved with each fetch it would always sit at the bottom and mark nothing.
func TestTimelineHistoryCountIsSetOnceAtAttach(t *testing.T) {
	var tm tea.Model = newGoldenModel(132, 40)
	e := api.TimelineEntryDto{Kind: "session", Description: "x", Utc: "2026-07-17T01:00:00Z"}

	tm, _ = tm.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: []api.TimelineEntryDto{e, e}}})
	tm, _ = tm.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: []api.TimelineEntryDto{e, e, e, e}}})

	if got := asModel(tm).timelineHistoryCount; got != 2 {
		t.Errorf("history count = %d after a refetch, want 2 — it must be fixed at the attach", got)
	}
}

// TestPadBetweenSacrificesTheLeft pins dogfood appendix item 8: MaxWidth truncates from the RIGHT, so
// `MaxWidth(left + " " + right)` ate the elapsed clock — the one segment pinned right precisely
// because it has to stay visible — while the left segments stayed whole.
func TestPadBetweenSacrificesTheLeft(t *testing.T) {
	left := "s12 Deliver · attempt 1/3 · opus-4-8 · architect  ◆ F7.3 Wire caching layer"
	right := "⠋ 41s"

	got := stripANSI(padBetween(left, right, 60))
	if !strings.HasSuffix(got, right) {
		t.Errorf("the right-pinned segment was clipped instead of the left:\n%q", got)
	}
	if lipgloss.Width(got) > 60 {
		t.Errorf("padBetween returned %d cols for a 60-col budget: %q", lipgloss.Width(got), got)
	}
	if !strings.HasPrefix(got, "s12 Deliver") {
		t.Errorf("the left should truncate from its tail, not its head: %q", got)
	}

	// Wide enough for both: right still lands on the edge, nothing is cut.
	if got := stripANSI(padBetween(left, right, 100)); lipgloss.Width(got) != 100 {
		t.Errorf("with room to spare the right must be pinned to the edge, got %d cols", lipgloss.Width(got))
	}

	// Narrower than the right segment alone: it is still the half worth keeping.
	if got := stripANSI(padBetween(left, right, 5)); !strings.Contains(got, "⠋") {
		t.Errorf("at 5 cols the right segment should survive, got %q", got)
	}
}
