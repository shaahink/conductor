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
// SF1.3: tabKey and foldedTabKey are ONE namespace — both are consumed by the same global loop in
// handleKey, so an alias that collides with a tab mnemonic is unreachable in exactly the way a
// duplicate tabKey entry is (tabKey is checked first, so the alias is the one that dies).
func TestTabMnemonicsAreUnique(t *testing.T) {
	seen := map[string]string{}
	for i, k := range tabKey {
		if k == "" {
			t.Errorf("tab %s (%d) has no mnemonic", tabNames[i], i)
			continue
		}
		if prev, dup := seen[k]; dup {
			t.Errorf("mnemonic %q is claimed by both %s and %s — the earlier tab wins the global "+
				"loop and the later one is unreachable", k, prev, tabNames[i])
		}
		seen[k] = tabNames[i]
	}
	for k, tab := range foldedTabKey {
		if prev, dup := seen[k]; dup {
			t.Errorf("folded-tab alias %q is already the mnemonic of %s — tabKey is checked first, so "+
				"the alias into %s is dead code", k, prev, tabNames[tab])
		}
		seen[k] = "folded → " + tabNames[tab]
	}
	// KS2.8: the reader's open key is bound by the browse surfaces themselves, but it behaves as a
	// global — the same letter on every pane — so it joins the namespace: a future tab mnemonic on
	// this letter would shadow it everywhere (the mnemonic loop resolves first), and a reader key on
	// a tab mnemonic would never be reachable at all.
	if prev, dup := seen[readerOpenKey]; dup {
		t.Errorf("the reader's open key %q is already %s — the mnemonic loop resolves first, so the "+
			"reader would be unreachable from every pane", readerOpenKey, prev)
	}
	seen[readerOpenKey] = "the reader"
}

// U2.2 moved the Plan editor's delete off `d` when the Dev tab claimed `d` globally. SF1.2 deleted
// that tab, and `d` stayed unbound — so delete stays on `x`. This drives handleKey — the REAL router —
// not handlePlanKey. plan_test.go's own drive() calls the pane handler directly, which is exactly why
// nothing there could ever have caught the shadowing: the precedence lives one level up.
func TestPlanDeleteStaysOnXNowThatTheDevMnemonicIsGone(t *testing.T) {
	m, _ := openPlanEditor(t)
	if m.tabHandlesAllKeys() {
		t.Fatal("precondition: the plan list must NOT own all keys, or this proves nothing")
	}
	if got := asModel(mustHandle(m.handleKey("x"))); !got.plan.deleting {
		t.Error("x in the Plan list must open the delete confirm through the real router")
	}
	// `d` must stay inert rather than silently becoming plan-delete again: a key that used to open the
	// SQL console must never turn into a destructive one.
	got := asModel(mustHandle(m.handleKey("d")))
	if got.plan.deleting {
		t.Error("d must not open the plan delete confirm — it was the SQL console's mnemonic, and a " +
			"freed key becoming destructive is the worst possible reuse")
	}
	if got.tab != TabPlan {
		t.Errorf("d is unbound since SF1.2 deleted the Dev tab; it must leave the tab alone, got %v", got.tab)
	}
}

// SF1.3: the Face is down to ten tabs — SF1.2 deleted Dev (13→12), SF1.3 folded Console into Agent
// and merged Sessions+Timeline into History (12→10). This pins BOTH halves of trap 11: tabKey is the
// single source for mnemonics, and every one of them must still land on the tab its own name claims.
func TestTabsConsolidatedToTen(t *testing.T) {
	if tabCount != 10 {
		t.Errorf("SF1.3 took the tab count from 12 to 10; got %d. If a tab was ADDED, it was supposed "+
			"to fold into an existing one — see docs/dev/adr/0004", tabCount)
	}
	if len(tabNames) != int(tabCount) || len(tabKey) != int(tabCount) {
		t.Fatalf("tabNames (%d) and tabKey (%d) must both be tabCount (%d)",
			len(tabNames), len(tabKey), tabCount)
	}
	for _, gone := range []string{"Dev", "Console", "Timeline", "Sessions"} {
		for i, n := range tabNames {
			if n == gone {
				t.Errorf("tab %d is named %q — that surface was deleted or folded; it must not be a tab",
					i, gone)
			}
		}
	}
	for i, k := range tabKey {
		if k == "d" {
			t.Errorf("tab %q took over the freed `d` mnemonic; SF1.2 left it deliberately unbound",
				tabNames[i])
		}
		m := asModel(mustHandle(newTestModel().handleKey(k)))
		if m.tab != MainTab(i) {
			t.Errorf("mnemonic %q must open %s, got %s", k, tabNames[i], tabNames[m.tab])
		}
	}
}

// Ten tabs is not a round number picked for a brief: `1`-`9` plus `0` addresses exactly ten, so at
// ten every tab is reachable by digit for the first time. That is the reason to stop at ten, so pin
// it — an eleventh tab would silently make one unreachable by digit again.
func TestEveryTabHasADigit(t *testing.T) {
	digits := []string{"1", "2", "3", "4", "5", "6", "7", "8", "9", "0"}
	if int(tabCount) > len(digits) {
		t.Fatalf("%d tabs but only %d digits — a tab is unreachable by number; fold one (ADR-0004)",
			tabCount, len(digits))
	}
	for i, d := range digits[:tabCount] {
		m := asModel(mustHandle(newTestModel().handleKey(d)))
		if m.tab != MainTab(i) {
			t.Errorf("digit %q must open %s (tab %d), got %s", d, tabNames[i], i, tabNames[m.tab])
		}
	}
}

// The promise SF1.3 made in exchange for deleting two tabs: every keystroke that reached a surface
// before still reaches it. Driven through handleKey, from a tab that is NOT the destination, because
// these are global keys and the fold is worthless if it only works from where you already are.
func TestFoldedTabKeysStillReachTheirSurface(t *testing.T) {
	// `s` is History's real mnemonic and must land on the sessions list, not wherever it was left.
	m := asModel(mustHandle(newTestModel().handleKey("t")))
	if m.history.view != historyTimeline {
		t.Fatalf("precondition: t must select the spine, got %v", m.history.view)
	}
	m = asModel(mustHandle(m.handleKey("s")))
	if m.tab != TabHistory || m.history.view != historySessions {
		t.Errorf("s must open History's sessions list; tab=%v view=%v", m.tab, m.history.view)
	}
	// ←/→ switches between the two views, this codebase's sub-section idiom.
	m = asModel(mustHandle(m.handleKey("right")))
	if m.history.view != historyTimeline {
		t.Errorf("→ must switch to the other History view, got %v", m.history.view)
	}
	m = asModel(mustHandle(m.handleKey("left")))
	if m.history.view != historySessions {
		t.Errorf("← must switch back, got %v", m.history.view)
	}
	// And `d` stays inert — its surface was DELETED, which is the case the aliases are NOT.
	m = asModel(mustHandle(newTestModel().handleKey("r")))
	before := m.tab
	m = asModel(mustHandle(m.handleKey("d")))
	if m.tab != before {
		t.Errorf("d must stay unbound (SF1.2), got a jump to %s", tabNames[m.tab])
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
	if m.knowledge.mode != knowledgeNote {
		t.Fatalf("expected note-input mode, got %v", m.knowledge.mode)
	}
	if !m.tabHandlesAllKeys() {
		t.Fatal("note input should capture all keys so 'k'/'b' type instead of switching tabs")
	}
	for _, ch := range "warm the cache first" {
		m = asModel(mustHandle(m.handleKnowledgeKey(string(ch))))
	}
	tm, cmd := m.handleKnowledgeKey("enter")
	m = asModel(tm)
	if m.knowledge.mode != knowledgeBrowse {
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

// SF1.2 deleted TestDevQuickQueryRuns along with the console it drove (quick-query list, SQL editor,
// MsgReportResult round trip). Nothing it asserted survives to be re-pointed: the whole mechanism is
// gone. What replaced it as the Report tab's only fetch is TestReportOpensWithTypedScores below.
func TestReportOpensWithTypedScoresAndNoSql(t *testing.T) {
	m := newTestModel()
	tm, cmd := m.handleKey("r")
	m = asModel(mustHandle(tm, cmd))
	if m.tab != TabReport {
		t.Fatalf("expected the Report tab, got %v", m.tab)
	}
	if cmd == nil {
		t.Fatal("opening Report must fetch something — the scores endpoint")
	}
	// The one command Report issues must be the typed scores fetch. Before SF1.1 this was a canned
	// SELECT, and before SF1.2 the console's MsgReportResult could still land here.
	if _, ok := cmd().(MsgReportScores); !ok {
		t.Errorf("opening Report must issue GET /scores and produce MsgReportScores, got %T", cmd())
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
	if m.processes.selected != 2 {
		t.Errorf("expected selection to clamp at 2, got %d", m.processes.selected)
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
	if !m.agent.searchActive {
		t.Fatal("expected search to activate")
	}
	for _, ch := range "gate" {
		m = asModel(mustHandle(m.handleSearchKey(string(ch))))
	}
	if m.transcript.SearchQuery != "gate" {
		t.Errorf("expected query 'gate', got %q", m.transcript.SearchQuery)
	}
	m = asModel(mustHandle(m.handleSearchKey("enter")))
	if m.agent.searchActive {
		t.Error("enter should stop capturing keystrokes")
	}
	if m.transcript.SearchQuery != "gate" {
		t.Error("the locked query should persist")
	}
	m = asModel(mustHandle(m.handleKey("/"))) // still on Agent from above
	m = asModel(mustHandle(m.handleSearchKey("esc")))
	if m.agent.searchActive || m.transcript.SearchQuery != "" {
		t.Error("esc should clear search state")
	}
}

func TestTemplateEditorReadWriteRoundTrip(t *testing.T) {
	dir := t.TempDir()
	m := newTestModel()
	m.data.Plan.PlanDir = dir

	m = asModel(mustHandle(m.handleKey("e")))
	if m.tab != TabTemplates || len(m.tmpl.entries) == 0 {
		t.Fatal("expected Templates tab with entries listed")
	}
	if m.tmpl.entries[0].Exists {
		t.Fatal("fresh temp dir should have no templates on disk")
	}

	m = asModel(mustHandle(m.handleTemplatesKey("enter")))
	if m.tmpl.mode != PromptEdit || m.tmpl.editor.Value() != "" {
		t.Fatalf("expected empty edit mode, got mode=%v content=%q", m.tmpl.mode, m.tmpl.editor.Value())
	}
	for _, ch := range "hello" {
		m = asModel(mustHandle(m.handleTemplatesKey(string(ch))))
	}
	m = asModel(mustHandle(m.handleTemplatesKey("ctrl+s")))
	if !m.tmpl.entries[0].Exists {
		t.Error("entry should be marked existing after save")
	}
	saved := filepath.Join(dir, filepath.Base(m.tmpl.entries[0].Path))
	if data, err := os.ReadFile(saved); err != nil || string(data) != "hello" {
		t.Errorf("expected saved 'hello', got %q err=%v", string(data), err)
	}

	m = asModel(mustHandle(m.handleTemplatesKey("esc"))) // edit → list
	if m.tmpl.mode != PromptList || m.tab != TabTemplates {
		t.Error("esc from edit should return to the list, staying on the Templates tab")
	}
	m = asModel(mustHandle(m.handleKey("esc"))) // list → Agent
	if m.tab != TabAgent {
		t.Error("esc from the list should return to the Agent tab")
	}
}

// SF1.3: the Console tab folded into Agent as a raw-stream mode. Every assertion here is driven
// through handleKey — the REAL router — because the whole point of the fold is that `c` still works
// from anywhere, and a test that called the pane handler directly could not tell.
func TestRawStreamFoldsIntoTheAgentTab(t *testing.T) {
	m := newTestModel()
	for i := 0; i < 3; i++ {
		m = asModel(mustHandle2(m.Update(MsgConsoleLine{Line: api.ConsoleLineDto{Seq: int64(i + 1), Text: "raw line"}})))
	}
	if len(m.data.RawConsole) != 3 {
		t.Fatalf("expected 3 buffered console lines, got %d", len(m.data.RawConsole))
	}
	// From another tab, `c` lands on Agent showing raw output pinned to the tail — what opening the
	// Console tab used to do.
	m = asModel(mustHandle(m.handleKey("p")))
	m = asModel(mustHandle(m.handleKey("c")))
	if m.tab != TabAgent || !m.agent.raw || !m.agentRawViewport().AtBottom() {
		t.Fatalf("c must open Agent's raw stream pinned to the tail; tab=%v raw=%v atBottom=%v",
			m.tab, m.agent.raw, m.agentRawViewport().AtBottom())
	}
	// Raw mode owns the pane-scroll set, through the router. KS2.7: the assertion reads the
	// VIEWPORT's own position rather than a bespoke `consoleScroll` integer — there is no second
	// number left to check, which is the point.
	longRaw := m
	for i := 0; i < 200; i++ {
		longRaw = asModel(mustHandle2(longRaw.Update(MsgConsoleLine{
			Line: api.ConsoleLineDto{Seq: int64(100 + i), Text: "raw line " + itoa(i)}})))
	}
	if longRaw.agentRawViewport().TotalLineCount() <= longRaw.agentRawViewport().Height() {
		t.Fatal("the raw fixture fits its pane — a scroll assertion on it would pass vacuously")
	}
	tailVp := longRaw.agentRawViewport()
	atTail := tailVp.YOffset()
	longRaw = asModel(mustHandle(longRaw.handleKey("up")))
	if got := longRaw.agent.rawVp.YOffset(); got != atTail-1 {
		t.Errorf("up should scroll the raw stream back one line, went from %d to %d", atTail, got)
	}
	longRaw = asModel(mustHandle(longRaw.handleKey("end")))
	if !longRaw.agent.rawVp.AtBottom() {
		t.Error("end should re-pin to the tail")
	}
	// …and the parsed transcript's own keys must NOT be reachable while raw output is showing: `f`
	// folds tool calls in the transcript, which is not a thing raw stdout has.
	before := m.transcript.FoldTools
	m = asModel(mustHandle(m.handleKey("f")))
	if m.transcript.FoldTools != before {
		t.Error("f must not toggle transcript folding while the raw stream is showing")
	}
	// On Agent, `c` toggles back — the raw view needs a way out that is not "leave and come back".
	m = asModel(mustHandle(m.handleKey("c")))
	if m.tab != TabAgent || m.agent.raw {
		t.Fatalf("c on Agent must toggle raw off; tab=%v raw=%v", m.tab, m.agent.raw)
	}
	// esc backs out of raw mode too: it is a layer over the transcript, and esc peels layers.
	m = asModel(mustHandle(m.handleKey("c")))
	m = asModel(mustHandle(m.handleKey("esc")))
	if m.tab != TabAgent || m.agent.raw {
		t.Errorf("esc should drop the raw stream and leave the parsed Agent view; tab=%v raw=%v",
			m.tab, m.agent.raw)
	}
}

func TestTimelineOpenFetchNavigate(t *testing.T) {
	m := newTestModel()
	tm, cmd := m.handleKey("t")
	m = asModel(tm)
	if m.tab != TabHistory || m.history.view != historyTimeline || !m.history.loading {
		t.Fatalf("t must open History on the spine, loading; tab=%v view=%v loading=%v",
			m.tab, m.history.view, m.history.loading)
	}
	if cmd == nil {
		t.Fatal("expected a fetch command")
	}
	msg, ok := cmd().(MsgTimelineUpdated)
	if !ok {
		t.Fatalf("expected MsgTimelineUpdated, got %T", cmd())
	}
	m = asModel(mustHandle2(m.Update(msg)))
	if m.history.loading || len(m.history.entries) == 0 {
		t.Fatal("expected entries loaded and loading cleared")
	}
	n := len(m.history.entries)
	for i := 0; i < n+3; i++ {
		m = asModel(mustHandle(m.handleTimelineKey("down")))
	}
	if m.history.selected != n-1 {
		t.Errorf("expected selection clamp at %d, got %d", n-1, m.history.selected)
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
	if !m.tmpl.previewOn || cmd == nil {
		t.Fatal("expected 'v' to toggle the compiled preview with a fetch")
	}
	msg, ok := cmd().(MsgPromptPreview)
	if !ok {
		t.Fatalf("expected MsgPromptPreview, got %T", cmd())
	}
	m = asModel(mustHandle2(m.Update(msg)))
	if m.tmpl.preview == nil || m.tmpl.preview.Kind != "Deliver" {
		t.Fatalf("expected Deliver preview populated, got %#v", m.tmpl.preview)
	}
	m = asModel(mustHandle(m.handleTemplatesKey("esc")))
	if m.tmpl.previewOn || m.tab != TabTemplates {
		t.Error("esc should hide the preview and stay on the Templates tab")
	}
}
