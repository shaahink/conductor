package tui

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// asModel unwraps whatever concrete type a handler returned (Model or *Model) into a plain Model.
func asModel(tm tea.Model) Model {
	switch v := tm.(type) {
	case Model:
		return v
	case *Model:
		return *v
	default:
		panic("unexpected model concrete type in test")
	}
}

// mustHandle keeps the model from a (tea.Model, tea.Cmd) return when the cmd doesn't matter.
func mustHandle(tm tea.Model, _ tea.Cmd) tea.Model  { return tm }
func mustHandle2(tm tea.Model, _ tea.Cmd) tea.Model { return tm }

func newTestModel() Model {
	src := api.NewDemoSource()
	m := New(src, true, "(demo)")
	m.data.Plan = &api.StateDto{StageId: "F7", PlanDir: "."}
	return m
}

func TestPaletteSafeVerbSendsControl(t *testing.T) {
	m := newTestModel()
	m = asModel(mustHandle(m.handleKey(":")))
	if m.cmd != CmdPalette {
		t.Fatalf("expected palette command bar, got %v", m.cmd)
	}

	tm, cmd := m.handlePaletteKey("enter") // first verb: pause (safe)
	m = asModel(tm)
	if m.cmd != CmdNone {
		t.Fatalf("expected palette to close after a safe verb, got %v", m.cmd)
	}
	if cmd == nil {
		t.Fatal("expected a command for a safe verb")
	}
	sent := cmd().(MsgControlSent)
	if sent.Verb != "pause" || !sent.Success {
		t.Errorf("expected pause/success, got %+v", sent)
	}
}

func TestPaletteDestructiveConfirmFlow(t *testing.T) {
	m := newTestModel()
	m = asModel(mustHandle(m.handleKey(":")))
	for _, ch := range "abort" {
		m = asModel(mustHandle(m.handlePaletteKey(string(ch))))
	}

	tm, cmd := m.handlePaletteKey("enter")
	m = asModel(tm)
	if cmd != nil {
		t.Fatal("destructive verb should require confirmation first")
	}
	if !m.paletteConfirming || m.cmd != CmdPalette {
		t.Fatalf("expected confirm mode with palette open; confirming=%v cmd=%v", m.paletteConfirming, m.cmd)
	}

	m = asModel(mustHandle(m.handlePaletteKey("esc")))
	if m.paletteConfirming {
		t.Error("esc should cancel confirm mode")
	}
	if m.cmd != CmdPalette {
		t.Error("esc during confirm should not close the palette entirely")
	}

	m = asModel(mustHandle(m.handlePaletteKey("enter")))
	tm, cmd = m.handlePaletteKey("y")
	m = asModel(tm)
	if m.cmd != CmdNone {
		t.Error("expected palette to close after confirming")
	}
	if cmd == nil {
		t.Fatal("expected a command after confirming")
	}
	if msg := cmd().(MsgControlSent); msg.Verb != "abort" || !msg.Success {
		t.Errorf("expected abort/success, got %+v", msg)
	}
}

// U2.1: the safety contract, pinned against the verb table itself rather than a hand-listed set of
// verbs — a verb added later inherits the check instead of quietly escaping it.
func TestEveryUnsafeVerbNamesItsConsequence(t *testing.T) {
	for _, v := range allVerbs {
		if v.Safe {
			if v.Consequence != "" {
				t.Errorf("%s is Safe but carries a consequence line; it will never be shown", v.Key)
			}
			continue
		}
		if v.Consequence == "" {
			t.Errorf("unsafe verb %q has no Consequence: its confirm would read as a bare prompt", v.Key)
			continue
		}
		// The prompt already prints "<key> — "; a consequence that just repeats the key says nothing.
		if strings.EqualFold(strings.TrimSpace(v.Consequence), v.Key) {
			t.Errorf("unsafe verb %q restates itself instead of naming a consequence", v.Key)
		}
	}
}

// U2.1: no destructive verb may fire without a confirm — driven through the real handler for every
// unsafe verb, so this cannot rot the way a spot-check of `abort` alone would.
func TestNoUnsafeVerbFiresWithoutConfirm(t *testing.T) {
	for _, v := range allVerbs {
		if v.Safe {
			continue
		}
		m := newTestModel()
		m = asModel(mustHandle(m.handleKey(":")))
		for _, ch := range v.Key {
			m = asModel(mustHandle(m.handlePaletteKey(string(ch))))
		}
		tm, cmd := m.handlePaletteKey("enter")
		m = asModel(tm)
		if cmd != nil {
			t.Errorf("%s fired on enter without a confirm", v.Key)
			continue
		}
		if !m.paletteConfirming {
			t.Errorf("%s did not enter confirm mode", v.Key)
			continue
		}
		// The confirm line the owner reads must actually contain the consequence — and must still
		// contain it at the narrowest supported width (80), where renderBottomBar's MaxWidth would
		// otherwise silently clip the sentence into meaninglessness. That clipping is exactly how a
		// "named consequence" rots back into a bare prompt, so pin the narrow case, not a roomy one.
		bar := m.renderBottomBar(80, "")
		if !strings.Contains(bar, v.Consequence) {
			t.Errorf("%s confirm bar does not name its consequence at 80 cols (too long?).\n"+
				"want substring: %s\ngot: %s", v.Key, v.Consequence, bar)
		}
	}
}

// U2.1: the palette's key column must clear the longest verb, or that row's description hangs one
// column right of the rest (the pause-after-stage skew this checkpoint found and fixed).
func TestVerbKeyColumnFitsEveryVerb(t *testing.T) {
	for _, v := range allVerbs {
		if len(v.Key) >= verbKeyPad {
			t.Errorf("verb %q is %d chars but verbKeyPad is %d: its description column will skew "+
				"right of every other row — widen verbKeyPad", v.Key, len(v.Key), verbKeyPad)
		}
	}
}

// U2.2: a tab mnemonic is a GLOBAL key — handleKey runs the mnemonic loop before the active pane's
// handler whenever that pane isn't in an owning sub-state. So a duplicate mnemonic doesn't just
// pick the wrong tab, it silently makes a pane key unreachable, which is exactly what adding Dev on
// `d` did to the Plan editor's delete (it moved to `x`). Nothing else pins uniqueness.
func TestTabMnemonicsAreUnique(t *testing.T) {
	seen := map[string]MainTab{}
	for i, k := range tabKey {
		if k == "" {
			t.Errorf("tab %s (%d) has no mnemonic", tabNames[i], i)
			continue
		}
		if prev, dup := seen[k]; dup {
			t.Errorf("mnemonic %q is claimed by both %s and %s — the earlier tab wins the global "+
				"loop and the later one is unreachable", k, tabNames[prev], tabNames[i])
		}
		seen[k] = MainTab(i)
	}
}

// U2.2: the Plan editor's delete moved off `d` when Dev claimed it globally. This drives handleKey —
// the REAL router — not handlePlanKey. plan_test.go's own drive() calls the pane handler directly,
// which is exactly why nothing there could ever have caught the shadowing: the precedence that
// breaks the key only exists one level up.
func TestPlanDeleteKeyIsNotShadowedByTheDevMnemonic(t *testing.T) {
	m, _ := openPlanEditor(t)
	if m.tabHandlesAllKeys() {
		t.Fatal("precondition: the plan list must NOT own all keys, or this proves nothing")
	}
	if got := asModel(mustHandle(m.handleKey("d"))); got.tab != TabDev {
		t.Errorf("d in the Plan list must reach the Dev tab (it is a global mnemonic), got tab %v", got.tab)
	}
	if got := asModel(mustHandle(m.handleKey("x"))); !got.planDeleting {
		t.Error("x in the Plan list must open the delete confirm through the real router")
	}
}

// U2.1: grouping is presentation, so pin the thing that would actually break the overlay's
// single-pass header emission — allVerbs being ordered by group.
func TestVerbsAreOrderedByGroup(t *testing.T) {
	seen := map[verbGroup]bool{}
	last := verbGroup("")
	for _, v := range allVerbs {
		if v.Group == last {
			continue
		}
		if seen[v.Group] {
			t.Fatalf("group %q is interleaved: allVerbs must stay sorted by group or the overlay "+
				"emits a duplicate header for it", v.Group)
		}
		seen[v.Group] = true
		last = v.Group
	}
	for _, g := range groupOrder {
		if !seen[g] {
			t.Errorf("group %q in groupOrder has no verbs", g)
		}
	}
	if len(seen) != len(groupOrder) {
		t.Errorf("allVerbs uses %d groups but groupOrder names %d", len(seen), len(groupOrder))
	}
}

func TestPaletteGotoFlow(t *testing.T) {
	m := newTestModel()
	m = asModel(mustHandle(m.handleKey(":")))
	for _, ch := range "goto" {
		m = asModel(mustHandle(m.handlePaletteKey(string(ch))))
	}
	tm, cmd := m.handlePaletteKey("enter")
	m = asModel(tm)
	if cmd != nil {
		t.Fatal("goto should prompt for a stage id, not send immediately")
	}
	if !m.paletteGotoActive || m.paletteGotoInput != "F7" {
		t.Fatalf("expected goto pre-filled with F7, got active=%v input=%q", m.paletteGotoActive, m.paletteGotoInput)
	}
	for len(m.paletteGotoInput) > 0 {
		m = asModel(mustHandle(m.handlePaletteKey("backspace")))
	}
	for _, ch := range "F9" {
		m = asModel(mustHandle(m.handlePaletteKey(string(ch))))
	}
	tm, cmd = m.handlePaletteKey("enter")
	m = asModel(tm)
	if m.cmd != CmdNone || m.paletteGotoActive {
		t.Error("expected goto to close the palette")
	}
	if cmd == nil || cmd().(MsgControlSent).Verb != "goto" {
		t.Error("expected a goto command")
	}
}

func TestInjectEmptyContentGuard(t *testing.T) {
	m := newTestModel()
	m = asModel(mustHandle(m.handleKey("i")))
	if m.cmd != CmdInject {
		t.Fatalf("expected inject command bar, got %v", m.cmd)
	}

	tm, cmd := m.handleInjectKey("ctrl+s")
	m = asModel(tm)
	if cmd != nil {
		t.Fatal("ctrl+s with empty content should be a no-op")
	}
	if m.cmd != CmdInject {
		t.Error("empty inject should stay open")
	}
	for _, ch := range "note" {
		m = asModel(mustHandle(m.handleInjectKey(string(ch))))
	}
	tm, cmd = m.handleInjectKey("ctrl+s")
	m = asModel(tm)
	if m.cmd != CmdNone {
		t.Error("non-empty inject should close")
	}
	if cmd == nil || !cmd().(MsgInjectSent).Success {
		t.Error("expected inject to succeed against the demo source")
	}
}

func TestKnowledgeFileNote(t *testing.T) {
	m := newTestModel()
	m = asModel(mustHandle(m.handleKey("k")))
	if m.tab != TabKnowledge {
		t.Fatalf("expected Knowledge tab, got %v", m.tab)
	}
	m = asModel(mustHandle(m.handleKnowledgeKey("n")))
	if m.knowledgeMode != knowledgeNote {
		t.Fatalf("expected note-input mode, got %v", m.knowledgeMode)
	}
	if !m.tabHandlesAllKeys() {
		t.Fatal("note input should capture all keys so 'k'/'b' type instead of switching tabs")
	}
	for _, ch := range "warm the cache first" {
		m = asModel(mustHandle(m.handleKnowledgeKey(string(ch))))
	}
	tm, cmd := m.handleKnowledgeKey("enter")
	m = asModel(tm)
	if m.knowledgeMode != knowledgeBrowse {
		t.Fatal("submitting should return to browse mode")
	}
	if cmd == nil {
		t.Fatal("expected a post-note command")
	}
	if written, ok := cmd().(MsgKnowledgeWritten); !ok || written.Err != "" {
		t.Fatalf("expected a successful note write, got %#v", cmd())
	}
}

func TestKnowledgeResolveRejectsNonNumericId(t *testing.T) {
	m := newTestModel()
	m = asModel(mustHandle(m.handleKey("k")))
	m = asModel(mustHandle(m.handleKnowledgeKey("x")))
	for _, ch := range "nope" {
		m = asModel(mustHandle(m.handleKnowledgeKey(string(ch))))
	}
	tm, cmd := m.handleKnowledgeKey("enter")
	m = asModel(tm)
	if cmd == nil {
		t.Fatal("expected a toast command for the bad id")
	}
	// A non-numeric id must not post a resolve — it surfaces an error toast instead.
	if _, ok := cmd().(MsgKnowledgeWritten); ok {
		t.Fatal("a non-numeric bug id must not post a resolve")
	}
}

// The SQL console moved from Report to Dev in U2.2 (Report became a rendered report). Its behaviour
// is unchanged, so this test moved WITH the code — only the tab it opens and the handler it drives
// are renamed. Everything it asserts about the console still holds byte for byte.
func TestDevQuickQueryRuns(t *testing.T) {
	m := newTestModel()
	m = asModel(mustHandle(m.handleKey("d")))
	if m.tab != TabDev || !m.reportFocusQuery {
		t.Fatalf("expected Dev tab focused on SQL; tab=%v focus=%v", m.tab, m.reportFocusQuery)
	}
	m = asModel(mustHandle(m.handleDevKey("tab")))
	if m.reportFocusQuery {
		t.Fatal("tab should move focus to the quick-query list")
	}
	tm, cmd := m.handleDevKey("enter")
	m = asModel(tm)
	if !m.data.ReportLoading {
		t.Error("expected ReportLoading while the query runs")
	}
	if m.reportEditor.Value() != quickQueries[0].SQL {
		t.Errorf("expected the editor set to the quick query, got %q", m.reportEditor.Value())
	}
	if cmd == nil {
		t.Fatal("expected a query command")
	}
	m = asModel(mustHandle2(m.Update(cmd())))
	if m.data.ReportLoading || m.data.ReportResult == nil || len(m.data.ReportResult.Columns) == 0 {
		t.Error("expected results populated and loading cleared")
	}
}

func TestProcessesNavigationClamps(t *testing.T) {
	m := newTestModel()
	m.data.Processes = []api.ProcessDto{{Pid: 1}, {Pid: 2}, {Pid: 3}}
	m = asModel(mustHandle(m.handleKey("o")))
	if m.tab != TabProcesses {
		t.Fatalf("expected Procs tab, got %v", m.tab)
	}
	for i := 0; i < 5; i++ {
		m = asModel(mustHandle(m.handleProcessesKey("down")))
	}
	if m.processSelected != 2 {
		t.Errorf("expected selection to clamp at 2, got %d", m.processSelected)
	}
	m = asModel(mustHandle(m.handleKey("esc")))
	if m.tab != TabAgent {
		t.Error("expected esc to return to the Agent tab")
	}
}

func TestSearchActivateTypeAndLock(t *testing.T) {
	m := newTestModel()
	m = asModel(mustHandle(m.handleKey("a"))) // `/` searches the transcript, so it needs the Agent tab
	m = asModel(mustHandle(m.handleKey("/")))
	if !m.searchActive {
		t.Fatal("expected search to activate")
	}
	for _, ch := range "gate" {
		m = asModel(mustHandle(m.handleSearchKey(string(ch))))
	}
	if m.transcript.SearchQuery != "gate" {
		t.Errorf("expected query 'gate', got %q", m.transcript.SearchQuery)
	}
	m = asModel(mustHandle(m.handleSearchKey("enter")))
	if m.searchActive {
		t.Error("enter should stop capturing keystrokes")
	}
	if m.transcript.SearchQuery != "gate" {
		t.Error("the locked query should persist")
	}
	m = asModel(mustHandle(m.handleKey("/"))) // still on Agent from above
	m = asModel(mustHandle(m.handleSearchKey("esc")))
	if m.searchActive || m.transcript.SearchQuery != "" {
		t.Error("esc should clear search state")
	}
}

func TestTemplateEditorReadWriteRoundTrip(t *testing.T) {
	dir := t.TempDir()
	m := newTestModel()
	m.data.Plan.PlanDir = dir

	m = asModel(mustHandle(m.handleKey("e")))
	if m.tab != TabTemplates || len(m.promptEntries) == 0 {
		t.Fatal("expected Templates tab with entries listed")
	}
	if m.promptEntries[0].Exists {
		t.Fatal("fresh temp dir should have no templates on disk")
	}

	m = asModel(mustHandle(m.handleTemplatesKey("enter")))
	if m.promptMode != PromptEdit || m.promptEditor.Value() != "" {
		t.Fatalf("expected empty edit mode, got mode=%v content=%q", m.promptMode, m.promptEditor.Value())
	}
	for _, ch := range "hello" {
		m = asModel(mustHandle(m.handleTemplatesKey(string(ch))))
	}
	m = asModel(mustHandle(m.handleTemplatesKey("ctrl+s")))
	if !m.promptEntries[0].Exists {
		t.Error("entry should be marked existing after save")
	}
	saved := filepath.Join(dir, filepath.Base(m.promptEntries[0].Path))
	if data, err := os.ReadFile(saved); err != nil || string(data) != "hello" {
		t.Errorf("expected saved 'hello', got %q err=%v", string(data), err)
	}

	m = asModel(mustHandle(m.handleTemplatesKey("esc"))) // edit → list
	if m.promptMode != PromptList || m.tab != TabTemplates {
		t.Error("esc from edit should return to the list, staying on the Templates tab")
	}
	m = asModel(mustHandle(m.handleKey("esc"))) // list → Agent
	if m.tab != TabAgent {
		t.Error("esc from the list should return to the Agent tab")
	}
}

func TestConsoleTabReceivesAndScrolls(t *testing.T) {
	m := newTestModel()
	for i := 0; i < 3; i++ {
		m = asModel(mustHandle2(m.Update(MsgConsoleLine{Line: api.ConsoleLineDto{Seq: int64(i + 1), Text: "raw line"}})))
	}
	if len(m.data.RawConsole) != 3 {
		t.Fatalf("expected 3 buffered console lines, got %d", len(m.data.RawConsole))
	}
	m = asModel(mustHandle(m.handleKey("c")))
	if m.tab != TabConsole || m.consoleScroll != 0 {
		t.Fatalf("expected Console tab pinned to tail; tab=%v scroll=%d", m.tab, m.consoleScroll)
	}
	m = asModel(mustHandle(m.handleConsoleKey("up")))
	if m.consoleScroll != 1 {
		t.Errorf("up should scroll back, got %d", m.consoleScroll)
	}
	m = asModel(mustHandle(m.handleConsoleKey("end")))
	if m.consoleScroll != 0 {
		t.Error("end should re-pin to the tail")
	}
	m = asModel(mustHandle(m.handleKey("esc")))
	if m.tab != TabAgent {
		t.Error("esc should return to the Agent tab")
	}
}

func TestTimelineOpenFetchNavigate(t *testing.T) {
	m := newTestModel()
	tm, cmd := m.handleKey("t")
	m = asModel(tm)
	if m.tab != TabTimeline || !m.timelineLoading {
		t.Fatalf("expected Timeline tab loading; tab=%v loading=%v", m.tab, m.timelineLoading)
	}
	if cmd == nil {
		t.Fatal("expected a fetch command")
	}
	msg, ok := cmd().(MsgTimelineUpdated)
	if !ok {
		t.Fatalf("expected MsgTimelineUpdated, got %T", cmd())
	}
	m = asModel(mustHandle2(m.Update(msg)))
	if m.timelineLoading || len(m.timelineEntries) == 0 {
		t.Fatal("expected entries loaded and loading cleared")
	}
	n := len(m.timelineEntries)
	for i := 0; i < n+3; i++ {
		m = asModel(mustHandle(m.handleTimelineKey("down")))
	}
	if m.timelineSelected != n-1 {
		t.Errorf("expected selection clamp at %d, got %d", n-1, m.timelineSelected)
	}
	m = asModel(mustHandle(m.handleKey("esc")))
	if m.tab != TabAgent {
		t.Error("esc should return to the Agent tab")
	}
}

func TestPromptCompiledPreviewToggle(t *testing.T) {
	m := newTestModel()
	m = asModel(mustHandle(m.handleKey("e")))
	if m.tab != TabTemplates {
		t.Fatalf("expected Templates tab, got %v", m.tab)
	}
	tm, cmd := m.handleTemplatesKey("v")
	m = asModel(tm)
	if !m.promptPreviewOn || cmd == nil {
		t.Fatal("expected 'v' to toggle the compiled preview with a fetch")
	}
	msg, ok := cmd().(MsgPromptPreview)
	if !ok {
		t.Fatalf("expected MsgPromptPreview, got %T", cmd())
	}
	m = asModel(mustHandle2(m.Update(msg)))
	if m.promptPreview == nil || m.promptPreview.Kind != "Deliver" {
		t.Fatalf("expected Deliver preview populated, got %#v", m.promptPreview)
	}
	m = asModel(mustHandle(m.handleTemplatesKey("esc")))
	if m.promptPreviewOn || m.tab != TabTemplates {
		t.Error("esc should hide the preview and stay on the Templates tab")
	}
}
