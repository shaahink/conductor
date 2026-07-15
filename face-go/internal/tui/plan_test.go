package tui

import (
	"testing"

	"conductor-face-go/internal/api"
)

// openPlanEditor opens the plan modal and loads the plan synchronously (executing the fetch cmd), so
// tests start from a ready editor over the same demo source the model will post edits to.
func openPlanEditor(t *testing.T) (Model, api.DataSource) {
	t.Helper()
	src := api.NewDemoSource()
	m := New(src, true, "(demo)")
	m.data.Plan = &api.StateDto{StageId: "F7", PlanDir: "."}

	tm, cmd := m.handleKey("g")
	m = asModel(tm)
	if m.tab != TabPlan {
		t.Fatalf("expected TabPlan, got %v", m.tab)
	}
	if cmd == nil {
		t.Fatal("opening the plan editor should fetch the plan")
	}
	tm, _ = m.Update(cmd()) // MsgPlanLoaded
	m = asModel(tm)
	if m.plan == nil {
		t.Fatal("plan should be loaded after the fetch cmd runs")
	}
	return m, src
}

func TestPlanEditStageEnumFieldRoundTrips(t *testing.T) {
	m, src := openPlanEditor(t)
	firstStage := m.plan.Stages[0].Id

	// Drill into the first stage's fields, move to "kind" (index 3), begin editing.
	m = drive(m, "enter")
	for range 3 {
		m = drive(m, "down")
	}
	if got := m.currentField().Field; got != "kind" {
		t.Fatalf("expected to be on the kind field, got %q", got)
	}
	m = drive(m, "enter") // begin enum edit
	if !m.planEditing {
		t.Fatal("expected to be editing the enum field")
	}
	m = drive(m, "right") // deliver -> review

	tm, cmd := m.handlePlanKey("enter") // save
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving a field should post an edit")
	}
	tm, _ = m.Update(cmd()) // MsgPlanEdited
	m = asModel(tm)

	// The edit must have round-tripped through the source and mutated the stage's kind.
	plan, _ := src.FetchPlan()
	for _, s := range plan.Stages {
		if s.Id == firstStage && s.Kind != "review" {
			t.Errorf("expected stage %s kind=review after save, got %q", firstStage, s.Kind)
		}
	}
}

func TestPlanTabCyclesAndImportPreviewApplies(t *testing.T) {
	m, _ := openPlanEditor(t)

	// right three times → Import section.
	m = drive(m, "right")
	m = drive(m, "right")
	m = drive(m, "right")
	if m.planTab != planTabImport {
		t.Fatalf("expected Import section, got %v", m.planTab)
	}

	for _, ch := range "docs/PLAN.md" {
		m = drive(m, string(ch))
	}
	if m.planImportInput != "docs/PLAN.md" {
		t.Fatalf("import source not accumulated: %q", m.planImportInput)
	}

	// enter → preview (apply=false). Demo returns a non-empty diff.
	tm, cmd := m.handlePlanImportKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("enter in the import tab should request a diff")
	}
	tm, _ = m.Update(cmd())
	m = asModel(tm)
	if m.planImportResult == nil || m.planImportResult.Diff.IsEmpty() {
		t.Fatal("expected a non-empty diff preview")
	}
	if m.planImportResult.Applied {
		t.Fatal("preview must not apply")
	}

	// 'a' → apply.
	tm, cmd = m.handlePlanImportKey("a")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("'a' should apply the diff")
	}
	msg := cmd()
	imported, ok := msg.(MsgPlanImported)
	if !ok || imported.Result == nil || !imported.Result.Applied {
		t.Fatalf("expected an applied import, got %#v", msg)
	}
}

func TestPlanEscBacksOutOneLevel(t *testing.T) {
	m, _ := openPlanEditor(t)
	m = drive(m, "enter") // drill into stage fields
	if !m.planDrill {
		t.Fatal("expected to be drilled in")
	}
	m = drive(m, "esc") // back to stage list, not closed
	if m.planDrill {
		t.Error("esc should leave the field view")
	}
	if m.tab != TabPlan {
		t.Error("esc from the field view should stay on the Plan tab")
	}
	m = drive(m, "esc") // now leave the Plan tab
	if m.tab != TabAgent {
		t.Error("esc from the stage list should return to the Agent tab")
	}
}

// drive applies one plan-editor key and unwraps the model.
func drive(m Model, key string) Model {
	tm, _ := m.handlePlanKey(key)
	return asModel(tm)
}