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
