package api

// SF3.2: the Kanban had nothing to print under an unselected card, and grouped nothing, because the
// Face threw the card's identity away at the decoder. The engine has served kind/stageId/confirmed
// since W1.4, and serves sessionNumber/statusSinceUtc/attempts since SF3.2's fold change; this pins
// that the Face reads all six off the wire rather than re-deriving any of them.

import (
	"encoding/json"
	"testing"
)

// The payload is the shape GET /tasks emits (ControlPlaneDto.FromTasks): camelCase, statusSinceUtc
// in the "O" round-trip layout DateTimeOffset writes.
const tasksWire = `{"tasks":[
 {"taskId":"SF3.2","checkpointId":"SF3.2","title":"The kanban groups by stage","status":"in_progress",
  "source":"planner","order":2,"context":"","paths":[],"kind":"checkpoint","stageId":"SF3",
  "confirmed":false,"sessionNumber":14,"statusSinceUtc":"2026-08-01T01:42:07.2522675+00:00","attempts":1},
 {"taskId":"SF2.3","checkpointId":"SF2.3","title":"Over-budget renders as OVER","status":"done",
  "source":"planner","order":1,"context":"","paths":[],"kind":"checkpoint","stageId":"SF2",
  "confirmed":true,"sessionNumber":12,"statusSinceUtc":"2026-08-01T01:13:21.4495125+00:00","attempts":1},
 {"taskId":"T9","checkpointId":"SF9.9","title":"A card an older engine served","status":"todo",
  "source":"agent","order":9,"context":"","paths":[]}
]}`

func TestTasksWireCarriesTheCardIdentityAndMeta(t *testing.T) {
	var dto TasksDto
	if err := json.Unmarshal([]byte(tasksWire), &dto); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if len(dto.Tasks) != 3 {
		t.Fatalf("expected 3 cards, got %d", len(dto.Tasks))
	}

	live := dto.Tasks[0]
	if live.StageId != "SF3" || live.Kind != "checkpoint" {
		t.Errorf("identity dropped: kind=%q stageId=%q", live.Kind, live.StageId)
	}
	if live.SessionNumber != 14 || live.Attempts != 1 {
		t.Errorf("meta dropped: session=%d attempts=%d", live.SessionNumber, live.Attempts)
	}
	if live.StatusSinceUtc != "2026-08-01T01:42:07.2522675+00:00" {
		t.Errorf("statusSinceUtc dropped: %q", live.StatusSinceUtc)
	}
	if live.Confirmed {
		t.Error("an in-flight card must not decode as confirmed")
	}

	// Claimed and confirmed are different facts, and the board is the surface that has to tell them
	// apart — this is the only field on the wire that does.
	if !dto.Tasks[1].Confirmed {
		t.Error("a confirmed card decoded as unconfirmed")
	}
}

func TestTaskStageFallsBackToTheIdConventionOnlyWhenTheWireIsSilent(t *testing.T) {
	var dto TasksDto
	if err := json.Unmarshal([]byte(tasksWire), &dto); err != nil {
		t.Fatalf("decode: %v", err)
	}
	// Served: authoritative, even though splitting the id would give the same answer here.
	if got := dto.Tasks[0].Stage(); got != "SF3" {
		t.Errorf("served stageId ignored, got %q", got)
	}
	// Not served (an older engine): the tracker's split-on-first-dot convention, not an empty stage.
	if got := dto.Tasks[2].Stage(); got != "SF9" {
		t.Errorf("fallback stage wrong, got %q", got)
	}
	// A checkpoint id with no dot is its own stage rather than "".
	if got := (TaskDto{CheckpointId: "SF3"}).Stage(); got != "SF3" {
		t.Errorf("dotless id: got %q", got)
	}
}
