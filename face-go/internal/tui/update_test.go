package tui

import (
	"os"
	"path/filepath"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// asModel unwraps whatever concrete type a handler returned (Model or *Model — the codebase mixes
// value and pointer receivers across handlers) back into a plain Model for assertions.
func asModel(tm tea.Model) Model {
	switch v := tm.(type) {
	case Model:
		return v
	case *Model:
		return *v
	default:
		panic("unexpected tea.Model concrete type in test")
	}
}

func newTestModel() Model {
	src := api.NewDemoSource()
	m := New(src, true, "(demo)")
	m.data.Plan = &api.StateDto{StageId: "F7", PlanDir: "."}
	return m
}

func TestPaletteSafeVerbSendsControl(t *testing.T) {
	m := newTestModel()
	tm, _ := m.handleKey(":")
	m = asModel(tm)

	// first verb in the unfiltered list is "pause" (safe, index 0)
	tm, cmd := m.handlePaletteKey("enter")
	m = asModel(tm)

	if m.activeModal != ModalNone {
		t.Fatalf("expected palette to close after a safe verb, got modal %v", m.activeModal)
	}
	if cmd == nil {
		t.Fatal("expected a command to be returned for a safe verb")
	}
	msg := cmd()
	sent, ok := msg.(MsgControlSent)
	if !ok {
		t.Fatalf("expected MsgControlSent, got %T", msg)
	}
	if sent.Verb != "pause" || !sent.Success {
		t.Errorf("expected pause/success, got %+v", sent)
	}
}

func TestPaletteDestructiveConfirmFlow(t *testing.T) {
	m := newTestModel()
	tm, _ := m.handleKey(":")
	m = asModel(tm)

	// filter down to "abort" (destructive)
	for _, ch := range "abort" {
		tm, _ = m.handlePaletteKey(string(ch))
		m = asModel(tm)
	}

	tm, cmd := m.handlePaletteKey("enter")
	m = asModel(tm)
	if cmd != nil {
		t.Fatal("expected no command yet — destructive verb should require confirmation first")
	}
	if !m.paletteConfirming || m.activeModal != ModalPalette {
		t.Fatalf("expected confirm mode, modal still open; got confirming=%v modal=%v", m.paletteConfirming, m.activeModal)
	}

	// esc during confirm should back out to the list, not close the whole modal
	tm, _ = m.handlePaletteKey("esc")
	m = asModel(tm)
	if m.paletteConfirming {
		t.Error("esc should have cancelled confirm mode")
	}
	if m.activeModal != ModalPalette {
		t.Error("esc during confirm should not close the palette entirely")
	}

	// re-enter confirm (filter + selection are untouched by the esc-cancel) and accept
	tm, _ = m.handlePaletteKey("enter")
	m = asModel(tm)
	tm, cmd = m.handlePaletteKey("y")
	m = asModel(tm)

	if m.activeModal != ModalNone {
		t.Error("expected palette to close after confirming")
	}
	if cmd == nil {
		t.Fatal("expected a command after confirming destructive verb")
	}
	msg := cmd().(MsgControlSent)
	if msg.Verb != "abort" || !msg.Success {
		t.Errorf("expected abort/success, got %+v", msg)
	}
}

func TestPaletteGotoFlow(t *testing.T) {
	m := newTestModel()
	tm, _ := m.handleKey(":")
	m = asModel(tm)

	for _, ch := range "goto" {
		tm, _ = m.handlePaletteKey(string(ch))
		m = asModel(tm)
	}
	tm, cmd := m.handlePaletteKey("enter")
	m = asModel(tm)
	if cmd != nil {
		t.Fatal("goto should prompt for a stage id, not send immediately")
	}
	if !m.paletteGotoActive {
		t.Fatal("expected goto sub-flow to be active")
	}
	if m.paletteGotoInput != "F7" {
		t.Errorf("expected goto input pre-filled with current stage F7, got %q", m.paletteGotoInput)
	}

	// replace the pre-filled stage id
	for len(m.paletteGotoInput) > 0 {
		tm, _ = m.handlePaletteKey("backspace")
		m = asModel(tm)
	}
	for _, ch := range "F9" {
		tm, _ = m.handlePaletteKey(string(ch))
		m = asModel(tm)
	}
	tm, cmd = m.handlePaletteKey("enter")
	m = asModel(tm)

	if m.activeModal != ModalNone || m.paletteGotoActive {
		t.Error("expected goto flow to close the palette")
	}
	if cmd == nil {
		t.Fatal("expected a command for goto")
	}
	msg := cmd().(MsgControlSent)
	if msg.Verb != "goto" {
		t.Errorf("expected goto verb, got %+v", msg)
	}
}

func TestInjectEmptyContentGuard(t *testing.T) {
	m := newTestModel()
	tm, _ := m.handleKey("i")
	m = asModel(tm)

	tm, cmd := m.handleInjectKey("ctrl+s")
	m = asModel(tm)
	if cmd != nil {
		t.Fatal("expected ctrl+s with empty content to be a no-op")
	}
	if m.activeModal != ModalInject {
		t.Error("empty inject should not close the modal")
	}

	for _, ch := range "note" {
		tm, _ = m.handleInjectKey(string(ch))
		m = asModel(tm)
	}
	tm, cmd = m.handleInjectKey("ctrl+s")
	m = asModel(tm)
	if m.activeModal != ModalNone {
		t.Error("non-empty inject should close the modal")
	}
	if cmd == nil {
		t.Fatal("expected a command for non-empty inject")
	}
	msg := cmd().(MsgInjectSent)
	if !msg.Success {
		t.Errorf("expected inject to succeed against demo source, got %+v", msg)
	}
}

func TestReportQuickQueryRuns(t *testing.T) {
	m := newTestModel()
	tm, _ := m.handleKey("r")
	m = asModel(tm)
	if !m.reportFocusQuery {
		t.Fatal("report modal should default focus to the SQL box")
	}

	tm, _ = m.handleReportKey("tab")
	m = asModel(tm)
	if m.reportFocusQuery {
		t.Fatal("tab should move focus to the quick-query list")
	}

	tm, cmd := m.handleReportKey("enter")
	m = asModel(tm)
	if !m.data.ReportLoading {
		t.Error("expected ReportLoading to be set while the query runs")
	}
	if m.reportSQL != quickQueries[0].SQL {
		t.Errorf("expected reportSQL to be set to the quick query, got %q", m.reportSQL)
	}
	if cmd == nil {
		t.Fatal("expected a command to run the query")
	}

	msg := cmd().(MsgReportResult)
	tm, _ = m.Update(msg)
	m = asModel(tm)
	if m.data.ReportLoading {
		t.Error("expected ReportLoading to clear once results arrive")
	}
	if m.data.ReportResult == nil || len(m.data.ReportResult.Columns) == 0 {
		t.Error("expected report results to be populated")
	}
}

func TestProcessesNavigationClamps(t *testing.T) {
	m := newTestModel()
	m.data.Processes = []api.ProcessDto{{Pid: 1}, {Pid: 2}, {Pid: 3}}

	tm, _ := m.handleKey("s")
	m = asModel(tm)
	if m.activeModal != ModalProcesses {
		t.Fatal("expected processes modal to open")
	}

	for i := 0; i < 5; i++ {
		tm, _ = m.handleProcessesKey("down")
		m = asModel(tm)
	}
	if m.processSelected != 2 {
		t.Errorf("expected selection to clamp at 2 (last index), got %d", m.processSelected)
	}

	tm, _ = m.handleProcessesKey("esc")
	m = asModel(tm)
	if m.activeModal != ModalNone {
		t.Error("expected esc to close the processes modal")
	}
}

func TestSearchActivateTypeAndLock(t *testing.T) {
	m := newTestModel()
	tm, _ := m.handleKey("/")
	m = asModel(tm)
	if !m.searchActive {
		t.Fatal("expected search mode to activate")
	}

	for _, ch := range "gate" {
		tm, _ = m.handleSearchKey(string(ch))
		m = asModel(tm)
	}
	if m.transcript.SearchQuery != "gate" {
		t.Errorf("expected search query 'gate', got %q", m.transcript.SearchQuery)
	}

	tm, _ = m.handleSearchKey("enter")
	m = asModel(tm)
	if m.searchActive {
		t.Error("expected enter to stop capturing further keystrokes")
	}
	if m.transcript.SearchQuery != "gate" {
		t.Error("expected the locked-in query to persist after enter")
	}

	// esc from normal mode should clear the locked search entirely
	tm, _ = m.handleKey("/")
	m = asModel(tm)
	tm, _ = m.handleSearchKey("esc")
	m = asModel(tm)
	if m.searchActive || m.transcript.SearchQuery != "" {
		t.Error("expected esc to clear search state")
	}
}

func TestTemplateEditorReadWriteRoundTrip(t *testing.T) {
	dir := t.TempDir()
	m := newTestModel()
	m.data.Plan.PlanDir = dir

	tm, _ := m.handleKey("e")
	m = asModel(tm)
	if len(m.promptEntries) == 0 {
		t.Fatal("expected session templates to be listed even when none exist on disk yet")
	}
	if m.promptEntries[0].Exists {
		t.Fatal("expected fresh temp dir to have no templates on disk")
	}

	tm, _ = m.handlePromptKey("enter") // open first entry for editing
	m = asModel(tm)
	if m.promptMode != PromptEdit {
		t.Fatal("expected to enter edit mode")
	}
	if m.promptContent != "" {
		t.Errorf("expected empty content for a template not yet on disk, got %q", m.promptContent)
	}

	for _, ch := range "hello" {
		tm, _ = m.handlePromptKey(string(ch))
		m = asModel(tm)
	}
	tm, _ = m.handlePromptKey("ctrl+s")
	m = asModel(tm)

	if !m.promptEntries[0].Exists {
		t.Error("expected entry to be marked as existing after save")
	}

	saved := filepath.Join(dir, SessionTemplateName(m, 0))
	data, err := os.ReadFile(saved)
	if err != nil {
		t.Fatalf("expected file to be written to disk: %v", err)
	}
	if string(data) != "hello" {
		t.Errorf("expected saved content 'hello', got %q", string(data))
	}

	// esc from edit mode goes back to the list, not out of the modal
	tm, _ = m.handlePromptKey("esc")
	m = asModel(tm)
	if m.promptMode != PromptList || m.activeModal != ModalPrompt {
		t.Error("expected esc from edit mode to return to the template list, not close the modal")
	}
	tm, _ = m.handlePromptKey("esc")
	m = asModel(tm)
	if m.activeModal != ModalNone {
		t.Error("expected esc from the list to close the modal")
	}
}

// SessionTemplateName is a tiny test helper exposing which file the Nth entry maps to.
func SessionTemplateName(m Model, idx int) string {
	return filepath.Base(m.promptEntries[idx].Path)
}

func TestTimelineOpenFetchNavigate(t *testing.T) {
	m := newTestModel()
	tm, cmd := m.handleKey("t")
	m = asModel(tm)
	if m.activeModal != ModalTimeline {
		t.Fatalf("expected timeline modal to open, got %v", m.activeModal)
	}
	if !m.timelineLoading {
		t.Error("expected loading state while the fetch is in flight")
	}
	if cmd == nil {
		t.Fatal("expected a fetch command when opening the timeline")
	}

	msg, ok := cmd().(MsgTimelineUpdated)
	if !ok {
		t.Fatalf("expected MsgTimelineUpdated, got %T", cmd())
	}
	tm, _ = m.Update(msg)
	m = asModel(tm)
	if m.timelineLoading {
		t.Error("expected loading to clear once entries arrive")
	}
	if len(m.timelineEntries) == 0 {
		t.Fatal("expected timeline entries from the demo source")
	}

	n := len(m.timelineEntries)
	for i := 0; i < n+3; i++ {
		tm, _ = m.handleTimelineKey("down")
		m = asModel(tm)
	}
	if m.timelineSelected != n-1 {
		t.Errorf("expected selection to clamp at %d (last index), got %d", n-1, m.timelineSelected)
	}

	tm, _ = m.handleTimelineKey("esc")
	m = asModel(tm)
	if m.activeModal != ModalNone {
		t.Error("expected esc to close the timeline modal")
	}
}

func TestPromptCompiledPreviewToggle(t *testing.T) {
	m := newTestModel() // StageId F7, PlanDir "."
	tm, _ := m.handleKey("e")
	m = asModel(tm)
	if m.activeModal != ModalPrompt {
		t.Fatalf("expected template editor to open, got %v", m.activeModal)
	}

	tm, cmd := m.handlePromptKey("v")
	m = asModel(tm)
	if !m.promptPreviewOn {
		t.Fatal("expected 'v' to toggle the compiled preview on")
	}
	if cmd == nil {
		t.Fatal("expected a fetch command for the compiled preview")
	}
	msg, ok := cmd().(MsgPromptPreview)
	if !ok {
		t.Fatalf("expected MsgPromptPreview, got %T", cmd())
	}
	tm, _ = m.Update(msg)
	m = asModel(tm)
	if m.promptPreview == nil {
		t.Fatal("expected the compiled preview to populate from the demo source")
	}
	if m.promptPreview.Kind != "Deliver" {
		t.Errorf("expected preview kind Deliver, got %q", m.promptPreview.Kind)
	}

	// esc hides the preview first, keeping the editor open — not closing the whole modal.
	tm, _ = m.handlePromptKey("esc")
	m = asModel(tm)
	if m.promptPreviewOn {
		t.Error("expected esc to hide the preview")
	}
	if m.activeModal != ModalPrompt {
		t.Error("expected esc from preview to keep the template editor open")
	}
}
