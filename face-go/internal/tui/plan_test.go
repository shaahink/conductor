package tui

import (
	"strings"
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
	for range 5 { // name, gatePolicy, defaultWorkflow, qa, verifierThreshold → maxSessions
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

// P2: the Settings QA dial posts through the "qa" edit target and round-trips the source; picking
// the inherit sentinel clears the dial. The live counterpart is ApplyQaEdit + a plan reload at the
// engine's next session boundary.
func TestPlanSettingsQaDialRoundTrips(t *testing.T) {
	m, src := openPlanEditor(t)

	m = drive(m, "right") // Gates
	m = drive(m, "right") // Settings
	for range 3 {         // name, gatePolicy, defaultWorkflow → qa
		m = drive(m, "down")
	}
	f := m.currentField()
	if f.Field != "mode" || f.Target != "qa" {
		t.Fatalf("expected the qa dial field, got %q target %q", f.Field, f.Target)
	}

	m = drive(m, "enter") // begin enum edit at "(workflow decides)"
	m = drive(m, "right") // → off
	tm, cmd := m.handlePlanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving the qa dial should post an edit")
	}
	// MsgPlanEdited answers with a plan re-fetch — run it so the editor sees the saved dial.
	tm, refetch := m.Update(cmd())
	m = asModel(tm)
	if refetch != nil {
		tm, _ = m.Update(refetch())
		m = asModel(tm)
	}

	plan, _ := src.FetchPlan()
	if plan.Qa == nil || plan.Qa.Mode != "off" {
		t.Fatalf("expected qa.mode=off after save, got %v", plan.Qa)
	}

	// Back to the inherit sentinel — the dial clears whole (nil, classic workflow selection).
	m = drive(m, "enter")
	m = drive(m, "left") // off → "(workflow decides)"
	tm, cmd = m.handlePlanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving the inherit sentinel should still post (it clears the dial)")
	}
	m.Update(cmd())
	plan, _ = src.FetchPlan()
	if plan.Qa != nil {
		t.Fatalf("expected the qa dial cleared, got %v", plan.Qa)
	}
}

// P2: the per-stage QA dial rides the stage target (field qamode) and round-trips the source.
func TestPlanStageQaDialRoundTrips(t *testing.T) {
	m, src := openPlanEditor(t)
	firstStage := m.plan.Stages[0].Id

	m = drive(m, "enter") // drill into the first stage
	for range 5 {         // title, model, persona, workflow, kind → qa
		m = drive(m, "down")
	}
	if got := m.currentField().Field; got != "qamode" {
		t.Fatalf("expected to be on the stage qa field, got %q", got)
	}
	m = drive(m, "enter") // begin enum edit
	m = drive(m, "right") // "(workflow decides)" → off
	m = drive(m, "right") // off → everySession
	tm, cmd := m.handlePlanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving the stage qa dial should post an edit")
	}
	m.Update(cmd())

	plan, _ := src.FetchPlan()
	for _, s := range plan.Stages {
		if s.Id == firstStage && (s.QaMode == nil || *s.QaMode != "everySession") {
			t.Errorf("expected stage %s qaMode=everySession after save, got %v", firstStage, s.QaMode)
		}
	}
}

// drive applies one plan-editor key and unwraps the model.
func drive(m Model, key string) Model {
	tm, _ := m.handlePlanKey(key)
	return asModel(tm)
}

// P5: the sessionRollover row surfaces limits.maxSessionTokens honestly — OFF by default — and
// round-trips through the "limits" edit target; the "rollover (run)" row posts the set-rollover
// control verb instead of a plan edit, so the plan file (and PlanVersion) is never touched.
func TestPlanSettingsSessionRolloverRoundTrips(t *testing.T) {
	m, src := openPlanEditor(t)

	m = drive(m, "right") // Gates
	m = drive(m, "right") // Settings
	for range 10 {        // → sessionRollover
		m = drive(m, "down")
	}
	f := m.currentField()
	if f.Field != "maxsessiontokens" || f.Target != "limits" {
		t.Fatalf("expected the maxsessiontokens limits field, got %q target %q", f.Field, f.Target)
	}
	// OFF by default, and the browse view must say so honestly.
	if got := f.Display(m.currentFieldValue(f.Field)); !strings.Contains(got, "OFF") {
		t.Fatalf("rollover must render as OFF by default, got %q", got)
	}

	m = drive(m, "enter")
	for _, ch := range "250000" {
		m = drive(m, string(ch))
	}
	tm, cmd := m.handlePlanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving sessionRollover should post an edit")
	}
	tm, refetch := m.Update(cmd())
	m = asModel(tm)
	if refetch != nil {
		tm, _ = m.Update(refetch())
		m = asModel(tm)
	}

	plan, _ := src.FetchPlan()
	if plan.Limits.MaxSessionTokens == nil || *plan.Limits.MaxSessionTokens != 250000 {
		t.Fatalf("expected limits.maxSessionTokens=250000 after save, got %v", plan.Limits.MaxSessionTokens)
	}
	if got := f.Display(m.currentFieldValue(f.Field)); !strings.Contains(got, "ON") {
		t.Fatalf("rollover must render as ON once set, got %q", got)
	}

	// Empty clears — back to OFF (mirrors ApplyLimitsEdit's nullable semantics).
	m = drive(m, "enter")
	for range 6 {
		m = drive(m, "backspace")
	}
	tm, cmd = m.handlePlanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving an empty rollover should still post (it clears the cap)")
	}
	m.Update(cmd())
	plan, _ = src.FetchPlan()
	if plan.Limits.MaxSessionTokens != nil {
		t.Fatalf("expected rollover cleared (OFF), got %v", *plan.Limits.MaxSessionTokens)
	}
}

func TestPlanSettingsRolloverThisRunPostsAControlVerbNotAPlanEdit(t *testing.T) {
	m, src := openPlanEditor(t)
	versionBefore := m.plan.PlanVersion

	m = drive(m, "right") // Gates
	m = drive(m, "right") // Settings
	for range 12 {        // → rollover (run)
		m = drive(m, "down")
	}
	f := m.currentField()
	if f.Field != "set-rollover" || f.Target != "control" {
		t.Fatalf("expected the set-rollover control row, got %q target %q", f.Field, f.Target)
	}

	m = drive(m, "enter")
	for _, ch := range "180000" {
		m = drive(m, string(ch))
	}
	tm, cmd := m.handlePlanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving the this-run row should post a control command")
	}
	msg, ok := cmd().(MsgControlSent)
	if !ok {
		t.Fatalf("expected a control post (MsgControlSent), got %T — the this-run knob must not be a plan edit", cmd())
	}
	if msg.Verb != "set-rollover" || !msg.Success {
		t.Fatalf("expected an accepted set-rollover, got %+v", msg)
	}

	// The plan file is untouched: same version, rollover still OFF in the plan.
	plan, _ := src.FetchPlan()
	if plan.PlanVersion != versionBefore {
		t.Fatalf("the this-run override must never bump the plan (v%d → v%d)", versionBefore, plan.PlanVersion)
	}
	if plan.Limits.MaxSessionTokens != nil {
		t.Fatal("the this-run override must never write limits.maxSessionTokens")
	}
}

// P5 follow-up: /state carries maxSessionTokensThisRun, so the "rollover (run)" row renders the
// ACTIVE override instead of a blind hint — none = the plan decides, off = forced OFF this run,
// a number = the cap this run. The demo source round-trips the verb like the real dispatcher.
func TestPlanSettingsRolloverThisRunShowsTheActiveOverride(t *testing.T) {
	m, src := openPlanEditor(t)

	m = drive(m, "right") // Gates
	m = drive(m, "right") // Settings
	for range 12 {        // → rollover (run)
		m = drive(m, "down")
	}
	f := m.currentField()
	if f.Field != "set-rollover" {
		t.Fatalf("expected the set-rollover control row, got %q", f.Field)
	}
	if got := f.Display(m.currentFieldValue(f.Field)); !strings.Contains(got, "the plan decides") {
		t.Fatalf("no override on /state must read as the plan deciding, got %q", got)
	}

	// Post the verb through the editor; the demo source mutates its state like the dispatcher.
	m = drive(m, "enter")
	for _, ch := range "180000" {
		m = drive(m, string(ch))
	}
	tm, cmd := m.handlePlanKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("saving the this-run row should post a control command")
	}
	m.Update(cmd())
	state, _ := src.FetchState()
	if state.MaxSessionTokensThisRun == nil || *state.MaxSessionTokensThisRun != 180000 {
		t.Fatalf("expected the demo state to carry the 180000 override, got %v", state.MaxSessionTokensThisRun)
	}
	m.data.Plan = state // what the next poll delivers
	if got := f.Display(m.currentFieldValue(f.Field)); !strings.Contains(got, "ON at 180000 tokens this run") {
		t.Fatalf("an active cap must render as the this-run override, got %q", got)
	}

	off := int64(0)
	m.data.Plan = &api.StateDto{MaxSessionTokensThisRun: &off}
	if got := f.Display(m.currentFieldValue(f.Field)); !strings.Contains(got, "OFF this run") {
		t.Fatalf("a forced-off override must render as OFF this run, got %q", got)
	}
}
