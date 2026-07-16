package tui

// G2.2: the Kanban tab drives POST /tasks/update|add through the same demo source the board reads,
// so a move/add must round-trip: key → POST → re-fetch → the board shows the new column.

import (
	"testing"

	"conductor-face-go/internal/api"
)

func openKanban(t *testing.T) (Model, api.DataSource) {
	t.Helper()
	src := api.NewDemoSource()
	m := New(src, true, "(demo)")
	m.data.Plan = &api.StateDto{StageId: "F7", CurrentCheckpoint: "F7.5", PlanDir: "."}

	tm, cmd := m.handleKey("b")
	m = asModel(tm)
	if m.tab != TabKanban {
		t.Fatalf("expected TabKanban, got %v", m.tab)
	}
	if cmd == nil {
		t.Fatal("opening the Kanban tab should fetch tasks")
	}
	tm, _ = m.Update(cmd()) // MsgTasksUpdated
	m = asModel(tm)
	if len(m.data.Tasks) == 0 {
		t.Fatal("expected demo tasks on the board")
	}
	return m, src
}

// driveKanban applies one Kanban key and unwraps the model.
func driveKanban(m Model, key string) Model {
	tm, _ := m.handleKanbanKey(key)
	return asModel(tm)
}

func taskStatus(t *testing.T, src api.DataSource, id string) string {
	t.Helper()
	tasks, err := src.FetchTasks()
	if err != nil {
		t.Fatal(err)
	}
	for _, task := range tasks.Tasks {
		if task.TaskId == id {
			return task.Status
		}
	}
	t.Fatalf("task %s not found", id)
	return ""
}

func TestKanbanMoveRightAdvancesTheCard(t *testing.T) {
	m, src := openKanban(t)

	// Default selection is the walk's first card — the first TODO card (T4 in the demo set).
	cards := m.kanbanCards()
	if cards[0].Status != "todo" {
		t.Fatalf("expected the first card to be in TODO, got %q", cards[0].Status)
	}
	first := cards[0].TaskId

	tm, cmd := m.handleKanbanKey("right")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("→ on a TODO card should post a move")
	}
	msg := cmd().(MsgTaskWritten)
	if msg.Err != "" || msg.Result == nil || !msg.Result.Ok {
		t.Fatalf("move rejected: %+v", msg)
	}
	tm, refetch := m.Update(msg)
	m = asModel(tm)
	if refetch == nil {
		t.Fatal("a successful write must re-fetch the board")
	}
	tm, _ = m.Update(refetch())
	m = asModel(tm)

	if got := taskStatus(t, src, first); got != "in_progress" {
		t.Errorf("expected %s to be in_progress after →, got %q", first, got)
	}
}

func TestKanbanMoveLeftReopensADoneCard(t *testing.T) {
	m, src := openKanban(t)
	m.kanbanSelId = "T1" // done in the demo set

	tm, cmd := m.handleKanbanKey("left")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("← on a Done card should post a move (reopen)")
	}
	msg := cmd().(MsgTaskWritten)
	if msg.Result == nil || !msg.Result.Ok || msg.Result.Status == nil || *msg.Result.Status != "in_progress" {
		t.Fatalf("expected the reopen to land in in_progress, got %+v", msg)
	}
	if got := taskStatus(t, src, "T1"); got != "in_progress" {
		t.Errorf("expected T1 reopened to in_progress, got %q", got)
	}
}

func TestKanbanMoveOffTheBoardIsNoOp(t *testing.T) {
	m, _ := openKanban(t)
	// First card is TODO — there is no column to its left.
	_, cmd := m.handleKanbanKey("left")
	if cmd != nil {
		t.Error("← on a TODO card must not post anything")
	}
}

func TestKanbanAddCardUnderSelectedCheckpoint(t *testing.T) {
	m, src := openKanban(t)

	m = driveKanban(m, "n")
	if !m.kanbanAdding {
		t.Fatal("n should open the add form")
	}
	for _, ch := range "Ship the board" {
		m = driveKanban(m, string(ch))
	}
	tm, cmd := m.handleKanbanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("submitting the add form should post the card")
	}
	if m.kanbanAdding {
		t.Error("the add form should close on submit")
	}
	msg := cmd().(MsgTaskWritten)
	if msg.Result == nil || !msg.Result.Ok {
		t.Fatalf("add rejected: %+v", msg)
	}
	tm, _ = m.Update(msg)
	m = asModel(tm)
	if msg.Result.TaskId != nil && m.kanbanSelId != *msg.Result.TaskId {
		t.Error("focus should follow the newly added card")
	}

	tasks, _ := src.FetchTasks()
	found := false
	for _, task := range tasks.Tasks {
		if task.Title == "Ship the board" && task.Status == "todo" && task.Source == "human" {
			found = true
		}
	}
	if !found {
		t.Error("the added card was not found in TODO after the round-trip")
	}
}

// --- P3: the card detail — blocks, structured edits, advisor refine, hand-off ---

// openKanbanDetail opens the detail of the demo card T3 (the one seeded with owner context) and
// runs the block fetch the way the live Update loop would.
func openKanbanDetail(t *testing.T) (Model, api.DataSource) {
	t.Helper()
	m, src := openKanban(t)
	m.kanbanSelId = "T3"
	tm, cmd := m.handleKanbanKey("enter")
	m = asModel(tm)
	if !m.kanbanDetail {
		t.Fatal("enter on a card should open the detail panel")
	}
	if cmd == nil {
		t.Fatal("opening the detail should fetch the prompt blocks")
	}
	tm, _ = m.Update(cmd()) // MsgPromptBlocks
	m = asModel(tm)
	if m.kanbanBlocks == nil || m.kanbanBlocks.TaskId != "T3" {
		t.Fatalf("expected T3's blocks, got %+v (err %q)", m.kanbanBlocks, m.kanbanBlocksErr)
	}
	return m, src
}

func TestKanbanDetailShowsLabeledBlocks(t *testing.T) {
	m, _ := openKanbanDetail(t)
	kinds := map[string]bool{}
	for _, b := range m.kanbanBlocks.Blocks {
		kinds[b.Kind] = true
		if (b.Kind == "taskTitle" || b.Kind == "taskContext") != b.Editable {
			t.Errorf("block %s: editable=%v — only the task-scoped blocks may be editable", b.Kind, b.Editable)
		}
	}
	for _, want := range []string{"persona", "stageNotes", "taskTitle", "taskContext", "knowledge", "tools"} {
		if !kinds[want] {
			t.Errorf("missing block kind %q", want)
		}
	}
}

func TestKanbanDetailContextEditRoundTrip(t *testing.T) {
	m, src := openKanbanDetail(t)

	m = driveKanban(m, "c")
	if !m.kanbanEditingCtx {
		t.Fatal("c should open the context editor")
	}
	// The editor opens seeded with the task's current context; replace it wholesale.
	m.kanbanCtxEditor.SetValue("cover the eviction path")
	tm, cmd := m.handleKanbanKey("ctrl+s")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("ctrl+s should post the structured edit")
	}
	msg := cmd().(MsgTaskWritten)
	if msg.Verb != "edit" || msg.Result == nil || !msg.Result.Ok {
		t.Fatalf("edit rejected: %+v", msg)
	}

	tasks, _ := src.FetchTasks()
	for _, task := range tasks.Tasks {
		if task.TaskId == "T3" && task.Context != "cover the eviction path" {
			t.Errorf("context did not round-trip, got %q", task.Context)
		}
		if task.TaskId == "T3" && task.Title != "Wire RunDb.GetLastPassingGateResult" {
			t.Errorf("a context-only edit must not touch the title, got %q", task.Title)
		}
	}
}

func TestKanbanDetailRefineProposesThenAppliesOnlyOnConfirm(t *testing.T) {
	m, src := openKanbanDetail(t)
	titleBefore := "Wire RunDb.GetLastPassingGateResult"

	tm, cmd := m.handleKanbanKey("a")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("a should ask the advisor")
	}
	tm, _ = m.Update(cmd()) // MsgTaskRefined
	m = asModel(tm)
	if m.kanbanProposal == nil {
		t.Fatalf("expected a proposal, status %q", m.kanbanStatus)
	}

	// The proposal alone must not have mutated anything.
	tasks, _ := src.FetchTasks()
	for _, task := range tasks.Tasks {
		if task.TaskId == "T3" && task.Title != titleBefore {
			t.Fatalf("refine mutated the task before confirm: %q", task.Title)
		}
	}

	// Confirm → the proposal lands through the same structured edit.
	tm, cmd = m.handleKanbanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("enter should apply the proposal")
	}
	msg := cmd().(MsgTaskWritten)
	if msg.Verb != "edit" || msg.Result == nil || !msg.Result.Ok {
		t.Fatalf("apply rejected: %+v", msg)
	}
	tasks, _ = src.FetchTasks()
	applied := false
	for _, task := range tasks.Tasks {
		if task.TaskId == "T3" && task.Title != titleBefore {
			applied = true
		}
	}
	if !applied {
		t.Error("confirming the proposal should change the task title")
	}
}

func TestKanbanDetailRefineDiscardOnEsc(t *testing.T) {
	m, src := openKanbanDetail(t)
	tm, cmd := m.handleKanbanKey("a")
	m = asModel(tm)
	tm, _ = m.Update(cmd())
	m = asModel(tm)
	m = driveKanban(m, "esc")
	if m.kanbanProposal != nil {
		t.Error("esc should discard the proposal")
	}
	tasks, _ := src.FetchTasks()
	for _, task := range tasks.Tasks {
		if task.TaskId == "T3" && task.Title != "Wire RunDb.GetLastPassingGateResult" {
			t.Errorf("a discarded proposal must not mutate the task, got %q", task.Title)
		}
	}
}

func TestKanbanDetailHandOffInjectsAfterConfirm(t *testing.T) {
	m, _ := openKanbanDetail(t)
	m = driveKanban(m, "h")
	if !m.kanbanHandConfirm {
		t.Fatal("h should ask for confirmation")
	}
	tm, cmd := m.handleKanbanKey("y")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("y should post the injection")
	}
	msg := cmd().(MsgInjectSent)
	if !msg.Success {
		t.Fatalf("hand-off injection failed: %s", msg.Error)
	}
}

func TestKanbanDetailEscClosesBackToTheBoard(t *testing.T) {
	m, _ := openKanbanDetail(t)
	m = driveKanban(m, "esc")
	if m.kanbanDetail {
		t.Error("esc should close the detail panel")
	}
}
