package tui

// SF3.2: the board answers "where are we".
//
// These pin the FACTS the rendering is built on, not the pixels — a golden proves a frame is
// unchanged, never that it is correct (STYLE.md). Every case below is a way the old board was
// quietly WRONG rather than a way it merely looked different.

import (
	"fmt"
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

// boardModel builds a Model straight from a task list and a run state — no demo source and no
// fetch, so a test states exactly the wire it is rendering.
func boardModel(stages []api.StageDto, activeStage string, tasks []api.TaskDto) Model {
	m := New(fakeSource{}, false, "http://127.0.0.1:4317")
	m.width, m.height = 110, 34
	m.data.Plan = &api.StateDto{StageId: activeStage, Stages: stages}
	m.data.Tasks = tasks
	m.tab = TabKanban
	return m
}

func stageList(ids ...string) []api.StageDto {
	var out []api.StageDto
	for _, id := range ids {
		out = append(out, api.StageDto{Id: id})
	}
	return out
}

func hasToken(ss []string, want string) bool {
	for _, s := range ss {
		if s == want {
			return true
		}
	}
	return false
}

// The wire's stageId is authoritative. Splitting the checkpoint id on the first dot — what the board
// did before SF3.2 — is a guess, and it is simply wrong for any plan whose ids do not encode their
// stage. The engine has served the real answer since W1.4.
func TestKanbanGroupsByTheWireStageNotTheCheckpointIdSplit(t *testing.T) {
	m := boardModel(stageList("PLATFORM", "F7"), "PLATFORM", []api.TaskDto{
		{TaskId: "T1", CheckpointId: "F7.3", Title: "a", Status: "todo", StageId: "PLATFORM"},
	})
	groups := m.kanbanGroups(0)
	if len(groups) != 1 || groups[0].Stage != "PLATFORM" {
		t.Fatalf("expected one PLATFORM group off the wire's stageId, got %+v", groups)
	}
	if !groups[0].Active {
		t.Error("the run's current stage must be marked active")
	}
}

// Sorting stage ids as text puts SF10 before SF2. The plan's own order is the only correct one.
func TestKanbanGroupsFollowThePlansStageOrderNotAlphabetical(t *testing.T) {
	m := boardModel(stageList("SF2", "SF10"), "SF2", []api.TaskDto{
		{TaskId: "T1", CheckpointId: "SF10.1", Title: "a", Status: "todo", StageId: "SF10"},
		{TaskId: "T2", CheckpointId: "SF2.1", Title: "b", Status: "todo", StageId: "SF2"},
	})
	groups := m.kanbanGroups(0)
	if len(groups) != 2 || groups[0].Stage != "SF2" || groups[1].Stage != "SF10" {
		t.Fatalf("expected plan order SF2 then SF10, got %+v", groups)
	}
	if groups[1].Active {
		t.Error("SF10 is not the run's stage and must not be marked active")
	}
}

// A card whose stage the /state fold has not caught up with must still appear. Dropping it loses
// work off the board entirely — the one outcome worse than showing it in an odd place.
func TestKanbanKeepsCardsWhoseStageThePlanDoesNotKnow(t *testing.T) {
	m := boardModel(stageList("SF2"), "SF2", []api.TaskDto{
		{TaskId: "T1", CheckpointId: "SF9.1", Title: "a", Status: "todo", StageId: "SF9"},
		{TaskId: "T2", CheckpointId: "SF2.1", Title: "b", Status: "todo", StageId: "SF2"},
	})
	groups := m.kanbanGroups(0)
	if len(groups) != 2 || groups[0].Stage != "SF2" || groups[1].Stage != "SF9" {
		t.Fatalf("expected the unknown stage appended after the plan's, got %+v", groups)
	}
}

// The walk order IS the render order. Up/down step through kanbanCards(), so when the two disagree
// the selection appears to jump at random — the most load-bearing invariant on this tab.
func TestKanbanWalkOrderIsTheRenderOrder(t *testing.T) {
	m := boardModel(stageList("SF2", "SF3"), "SF3", []api.TaskDto{
		{TaskId: "A", CheckpointId: "SF3.1", Status: "todo", StageId: "SF3"},
		{TaskId: "B", CheckpointId: "SF2.1", Status: "todo", StageId: "SF2"},
		{TaskId: "C", CheckpointId: "SF2.2", Status: "done", StageId: "SF2"},
		{TaskId: "D", CheckpointId: "SF3.2", Status: "skipped", StageId: "SF3"},
		{TaskId: "E", CheckpointId: "SF2.3", Status: "in_progress", StageId: "SF2"},
	})
	var render []string
	for col := range kanbanColumns {
		for _, g := range m.kanbanGroups(col) {
			for _, c := range g.Cards {
				render = append(render, c.TaskId)
			}
		}
	}
	for _, c := range m.kanbanSkipped() {
		render = append(render, c.TaskId)
	}
	var walk []string
	for _, c := range m.kanbanCards() {
		walk = append(walk, c.TaskId)
	}
	if strings.Join(walk, ",") != strings.Join(render, ",") {
		t.Fatalf("walk order %v does not match render order %v", walk, render)
	}
	// And it is genuinely column-major then stage-ordered, not merely self-consistent.
	if got := strings.Join(walk, ","); got != "B,A,E,C,D" {
		t.Errorf("expected B,A,E,C,D (todo by stage, in progress, done, skipped shelf last), got %s", got)
	}
}

// Done means finished; skipped means deliberately not done. Counting them together is how a board
// reports 5/5 on a stage that shipped three things.
func TestKanbanSkippedLeavesTheDoneGroupsForItsOwnShelf(t *testing.T) {
	m := boardModel(stageList("SF3"), "SF3", []api.TaskDto{
		{TaskId: "A", CheckpointId: "SF3.1", Status: "done", StageId: "SF3"},
		{TaskId: "B", CheckpointId: "SF3.2", Status: "skipped", StageId: "SF3"},
	})
	done := 0
	for _, g := range m.kanbanGroups(2) {
		done += len(g.Cards)
	}
	if done != 1 {
		t.Errorf("the Done column's groups must hold 1 card, not %d — a skip is not done", done)
	}
	if skipped := m.kanbanSkipped(); len(skipped) != 1 || skipped[0].TaskId != "B" {
		t.Errorf("expected B on the skipped shelf, got %+v", skipped)
	}
}

// blocked shares the TODO column with todo (three columns, so left/right keep meaning what they
// mean) and used to render identically to it — a card nobody CAN proceed on looked exactly like one
// nobody has started. The tag leads the meta line so truncation cannot eat it.
func TestKanbanBlockedAndSkippedSaySoOnTheCard(t *testing.T) {
	if kanbanColumn("blocked") != 0 {
		t.Fatal("blocked must stay in the TODO column — left/right semantics depend on three columns")
	}
	blocked := kanbanCardMeta(api.TaskDto{TaskId: "A", Status: "blocked", Source: "agent"})
	if len(blocked) == 0 || blocked[0] != "blocked" {
		t.Errorf("a blocked card's meta must LEAD with blocked, got %v", blocked)
	}
	if meta := kanbanCardMeta(api.TaskDto{Status: "skipped"}); len(meta) == 0 || meta[0] != "skipped" {
		t.Errorf("a skipped card's meta must lead with skipped, got %v", meta)
	}
	if kanbanCardStyle("blocked").GetForeground() == kanbanCardStyle("todo").GetForeground() {
		t.Error("blocked must not paint the same as todo")
	}
	if kanbanStatusTag("todo") != "" || kanbanStatusTag("done") != "" {
		t.Error("a status its own column already names must not be repeated on the card")
	}
}

// The meta is READ off the wire's SF3.2 fields, never re-derived — re-deriving is how the Face has
// been wrong before (SF2.3's budget block). claimed-vs-confirmed is a WORD because the difference
// between an agent saying it finished and the engine agreeing is not something a colour can carry.
func TestKanbanCardMetaReadsTheWireAndNamesTheVerdict(t *testing.T) {
	// TestMain pins timefmt.Now to goldenNow (2026-07-15T10:08:00Z), so the age is stable.
	got := kanbanCardMeta(api.TaskDto{
		Status: "in_progress", Source: "agent", SessionNumber: 12,
		StatusSinceUtc: "2026-07-15T10:04:00Z", Attempts: 2,
	})
	if want := "s12 · 4m · try 2 · agent"; strings.Join(got, " · ") != want {
		t.Errorf("meta = %q, want %q", strings.Join(got, " · "), want)
	}
	claimed := kanbanCardMeta(api.TaskDto{Status: "done", SessionNumber: 9})
	confirmed := kanbanCardMeta(api.TaskDto{Status: "done", SessionNumber: 9, Confirmed: true})
	if !hasToken(claimed, "claimed") || hasToken(claimed, "confirmed") {
		t.Errorf("an unconfirmed done card must read claimed, got %v", claimed)
	}
	if !hasToken(confirmed, "confirmed") {
		t.Errorf("a confirmed done card must say so, got %v", confirmed)
	}
	// An engine that serves none of it must not have any of it invented on its behalf.
	if got := kanbanCardMeta(api.TaskDto{Status: "todo", Source: "planner"}); strings.Join(got, ",") != "planner" {
		t.Errorf("with no wire meta the card must claim nothing, got %v", got)
	}
	// A first pickup is not worth a token; the SECOND one is the whole story.
	if hasToken(kanbanCardMeta(api.TaskDto{Status: "todo", Attempts: 1}), "try 1") {
		t.Error("a first pickup must not render as try 1")
	}
	// An unparseable / absent since-stamp renders no age rather than an invented one.
	if hasToken(kanbanCardMeta(api.TaskDto{Status: "todo", StatusSinceUtc: ""}), "0s") {
		t.Error("no since-stamp must mean no age, not an age of zero")
	}
}

// STYLE.md: drop whole meta tokens rather than clipping mid-value. "s1" out of "s14" is not a
// shorter truth, it is a different one.
func TestKanbanFitTokensDropsWholeTokensFromTheRight(t *testing.T) {
	toks := []string{"s14", "12m", "try 2", "agent"}
	if got := kanbanFitTokens(toks, 12); strings.Join(got, " · ") != "s14 · 12m" {
		t.Errorf("got %q, want %q", strings.Join(got, " · "), "s14 · 12m")
	}
	if got := kanbanFitTokens(toks, 100); len(got) != 4 {
		t.Errorf("a wide column must keep every token, got %v", got)
	}
	if got := kanbanFitTokens(toks, 1); len(got) != 0 {
		t.Errorf("nothing fits in one column; want no tokens, got %v", got)
	}
}

// A column taller than its budget used to run off the bottom into the frame's height clamp: cards
// vanished with nothing on screen saying they had, and the selection could walk into rows that were
// not being drawn at all.
func TestKanbanWindowKeepsTheSelectionVisibleAndStatesWhatItHid(t *testing.T) {
	body := make([]string, 20)
	for i := range body {
		body[i] = fmt.Sprintf("line %d", i)
	}
	// Selection at the very bottom: the window follows it and says what is above.
	got := kanbanWindow(body, 6, 19, 19, 40)
	if len(got) != 6 {
		t.Fatalf("the window must spend exactly its budget, got %d lines", len(got))
	}
	if !strings.Contains(got[4], "line 19") {
		t.Errorf("the selected line must be inside the window, got %v", got)
	}
	if !strings.Contains(got[5], "15 above") {
		t.Errorf("the window must state what it hid, got %q", got[5])
	}
	// Selection at the top: the note names what is below instead.
	got = kanbanWindow(body, 6, 0, 0, 40)
	if !strings.Contains(got[0], "line 0") || !strings.Contains(got[5], "15 below") {
		t.Errorf("a top-anchored window must show line 0 and name the rest, got %v", got)
	}
	// A body that fits is returned whole — no note, no clipping, nothing invented.
	if got := kanbanWindow(body[:4], 6, 0, 0, 40); len(got) != 4 {
		t.Errorf("a body that fits must be returned whole, got %v", got)
	}
}

// The ribbon reads the engine's own numbers; every field on it is already served.
func TestKanbanRibbonReadsTheStateItIsGiven(t *testing.T) {
	m := boardModel(stageList("SF1", "SF2", "SF3"), "SF3", nil)
	m.data.Plan.DoneCount, m.data.Plan.TotalCount = 8, 17
	m.data.Plan.SessionNumber, m.data.Plan.SessionKind = 15, "Deliver"
	m.data.Plan.Attempt, m.data.Plan.MaxAttempts = 1, 6
	m.data.Plan.Gates = []api.GateDto{
		{Name: "build", State: "pass"}, {Name: "test", State: "running"}, {Name: "lint", State: "pending"},
	}
	got := stripANSI(m.renderKanbanRibbon())
	// All four clauses must SURVIVE at 110 columns — the ribbon fits by dropping whole clauses from
	// the right, so wording that overflows silently costs the session clause on most terminals.
	for _, want := range []string{"SF3", "stage 3/3", "cp 8/17", "test running", "s15 Deliver", "try 1/6"} {
		if !strings.Contains(got, want) {
			t.Errorf("ribbon %q is missing %q", got, want)
		}
	}
	if strings.Contains(got, "build") {
		t.Errorf("the ribbon names the NEXT gate, not a finished one: %q", got)
	}
	// A finished battery has no next gate and says nothing rather than naming the last thing it did.
	m.data.Plan.Gates = []api.GateDto{{Name: "build", State: "pass"}}
	if got := stripANSI(m.renderKanbanRibbon()); strings.Contains(got, "gate") {
		t.Errorf("with no gate ahead the ribbon must drop the clause, got %q", got)
	}
	// No state at all: say nothing rather than render a row of zeroes as facts.
	empty := New(fakeSource{}, false, "x")
	if r := empty.renderKanbanRibbon(); r != "" {
		t.Errorf("with no state the ribbon must be empty, got %q", r)
	}
}

// n/total, not n. "Done (2)" never said whether that was 2 of 3 or 2 of 300.
func TestKanbanColumnHeaderCountsAgainstTheWholeBoard(t *testing.T) {
	m := boardModel(stageList("SF3"), "SF3", []api.TaskDto{
		{TaskId: "A", CheckpointId: "SF3.1", Status: "todo", StageId: "SF3"},
		{TaskId: "B", CheckpointId: "SF3.2", Status: "todo", StageId: "SF3"},
		{TaskId: "C", CheckpointId: "SF3.3", Status: "done", StageId: "SF3"},
		{TaskId: "D", CheckpointId: "SF3.4", Status: "skipped", StageId: "SF3"},
	})
	col := stripANSI(m.renderKanbanColumn(0, 30, 20, "A"))
	if !strings.Contains(col, "TODO 2/4") {
		t.Errorf("expected a TODO 2/4 header, got:\n%s", col)
	}
	done := stripANSI(m.renderKanbanColumn(2, 30, 20, "A"))
	if !strings.Contains(done, "Done 1/4") {
		t.Errorf("expected Done 1/4 — the skip is not done — got:\n%s", done)
	}
	if !strings.Contains(done, "skipped 1") {
		t.Errorf("expected a labelled skipped shelf, got:\n%s", done)
	}
}
