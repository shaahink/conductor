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

	tm, cmd := m.handleKey("p")
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

	// Drill into the first stage's fields, move to "kind" (title,model,persona,workflow,kind → index
	// 4), begin editing.
	m = drive(m, "enter")
	for range 4 {
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

	// 'a' → apply (routed via handlePlanKey — the diff view owns the keys wherever it came from).
	tm, cmd = m.handlePlanKey("a")
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

// G1.2: the Prompt section sends prose to the advisor (via POST /plan/import) and lands on the same
// diff/confirm/apply view the Import section uses.
func TestPlanPromptSendsProseAndAppliesTheDiff(t *testing.T) {
	m, _ := openPlanEditor(t)

	for range 4 { // Stages → Gates → Settings → Import → Prompt
		m = drive(m, "right")
	}
	if m.planTab != planTabPrompt {
		t.Fatalf("expected Prompt section, got %v", m.planTab)
	}

	for _, ch := range "add a lint gate" {
		m = drive(m, string(ch))
	}
	if m.planPromptEditor.Value() != "add a lint gate" {
		t.Fatalf("prompt not accumulated: %q", m.planPromptEditor.Value())
	}

	tm, cmd := m.handlePlanKey("ctrl+s")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("ctrl+s should send the prompt to the advisor")
	}
	if !m.planImportBusy {
		t.Error("a prompt in flight should mark the import busy")
	}
	tm, _ = m.Update(cmd()) // MsgPlanImported (preview)
	m = asModel(tm)
	if m.planImportResult == nil || m.planImportResult.Diff.IsEmpty() {
		t.Fatal("expected a non-empty diff from the prompt")
	}
	if m.planImportResult.Interpreter == nil || *m.planImportResult.Interpreter == "structured" {
		t.Error("a prose prompt should surface the interpreting model")
	}
	if m.planImportBusy {
		t.Error("the busy flag should clear when the diff lands")
	}

	// a → apply; the same source is re-posted with apply:true.
	tm, cmd = m.handlePlanKey("a")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("'a' should apply the previewed diff")
	}
	msg := cmd()
	imported, ok := msg.(MsgPlanImported)
	if !ok || imported.Result == nil || !imported.Result.Applied {
		t.Fatalf("expected an applied import, got %#v", msg)
	}
}

func TestPlanPromptEmptyOrBusyDoesNotSend(t *testing.T) {
	m, _ := openPlanEditor(t)
	for range 4 {
		m = drive(m, "right")
	}
	if _, cmd := m.handlePlanKey("ctrl+s"); cmd != nil {
		t.Error("an empty prompt must not consult the advisor")
	}
	for _, ch := range "do things" {
		m = drive(m, string(ch))
	}
	m.planImportBusy = true
	if _, cmd := m.handlePlanKey("ctrl+s"); cmd != nil {
		t.Error("a prompt already in flight must not be re-sent")
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

func TestPlanAddStageRoundTrips(t *testing.T) {
	m, src := openPlanEditor(t)
	before := len(m.plan.Stages)

	m = drive(m, "n") // open the add-stage form (n, not a, to dodge the Agent-tab mnemonic)
	if !m.planAdding {
		t.Fatal("n should open the add-stage form")
	}
	for _, ch := range "Z9" {
		m = drive(m, string(ch))
	}
	m = drive(m, "tab") // → title field
	for _, ch := range "New stage" {
		m = drive(m, string(ch))
	}

	tm, cmd := m.handlePlanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("submitting the add form should post an edit")
	}
	if m.planAdding {
		t.Error("the add form should close on submit")
	}
	tm, _ = m.Update(cmd())
	m = asModel(tm)

	plan, _ := src.FetchPlan()
	if len(plan.Stages) != before+1 {
		t.Fatalf("expected %d stages after add, got %d", before+1, len(plan.Stages))
	}
	found := false
	for _, s := range plan.Stages {
		if s.Id == "Z9" {
			found = true
			if s.Title != "New stage" {
				t.Errorf("expected title 'New stage', got %q", s.Title)
			}
		}
	}
	if !found {
		t.Error("the added stage Z9 was not found after the round-trip")
	}
}

func TestPlanDeleteGateRoundTrips(t *testing.T) {
	m, src := openPlanEditor(t)
	m = drive(m, "right") // → Gates section
	if m.planTab != planTabGates {
		t.Fatalf("expected Gates section, got %v", m.planTab)
	}
	target := m.plan.Gates[m.planGateIdx].Name
	before := len(m.plan.Gates)

	m = drive(m, "d") // open the delete confirm
	if !m.planDeleting {
		t.Fatal("d should open the delete confirm")
	}
	tm, cmd := m.handlePlanKey("y")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("confirming delete should post an edit")
	}
	tm, _ = m.Update(cmd())
	m = asModel(tm)

	plan, _ := src.FetchPlan()
	if len(plan.Gates) != before-1 {
		t.Fatalf("expected %d gates after delete, got %d", before-1, len(plan.Gates))
	}
	for _, g := range plan.Gates {
		if g.Name == target {
			t.Errorf("gate %s should have been deleted", target)
		}
	}
}

func TestPlanDeleteCancelDoesNotPost(t *testing.T) {
	m, _ := openPlanEditor(t)
	m = drive(m, "d") // confirm prompt
	if !m.planDeleting {
		t.Fatal("d should open the delete confirm")
	}
	tm, cmd := m.handlePlanKey("n") // n = no
	m = asModel(tm)
	if cmd != nil {
		t.Error("cancelling the delete must not post an edit")
	}
	if m.planDeleting {
		t.Error("n should close the confirm prompt")
	}
}

// G3.3: the Settings limits rows post through the "limits" edit target and round-trip the source —
// the live counterpart is ApplyLimitsEdit + a plan reload at the engine's next session boundary.
func TestPlanSettingsLimitsFieldRoundTrips(t *testing.T) {
	m, src := openPlanEditor(t)

	m = drive(m, "right") // Gates
	m = drive(m, "right") // Settings
	if m.planTab != planTabSettings {
		t.Fatalf("expected Settings section, got %v", m.planTab)
	}
	for range 3 { // name, gatePolicy, defaultWorkflow → maxSessions
		m = drive(m, "down")
	}
	f := m.currentField()
	if f.Field != "maxsessions" || f.Target != "limits" {
		t.Fatalf("expected the maxsessions limits field, got %q target %q", f.Field, f.Target)
	}

	m = drive(m, "enter")
	if !m.planEditing {
		t.Fatal("expected to be editing maxSessions")
	}
	m = drive(m, "5")
	tm, cmd := m.handlePlanKey("enter") // save
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving maxSessions should post an edit")
	}
	tm, _ = m.Update(cmd()) // MsgPlanEdited
	m = asModel(tm)

	plan, _ := src.FetchPlan()
	if plan.Limits.MaxSessions == nil || *plan.Limits.MaxSessions != 5 {
		t.Fatalf("expected limits.maxSessions=5 after save, got %v", plan.Limits.MaxSessions)
	}

	// Saving an empty value clears the cap (mirrors ApplyLimitsEdit's nullable semantics).
	m = drive(m, "enter")
	m = drive(m, "backspace") // "5" → ""
	tm, cmd = m.handlePlanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving an empty maxSessions should still post (it clears the cap)")
	}
	m.Update(cmd())
	plan, _ = src.FetchPlan()
	if plan.Limits.MaxSessions != nil {
		t.Fatalf("expected limits.maxSessions cleared, got %v", *plan.Limits.MaxSessions)
	}
}

// drive applies one plan-editor key and unwraps the model.
func drive(m Model, key string) Model {
	tm, _ := m.handlePlanKey(key)
	return asModel(tm)
}
