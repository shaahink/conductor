package tui

import (
	"strings"
	"testing"

	"charm.land/bubbles/v2/viewport"
	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// K6.2 / bug #30. This is the owner's "I can't read long text", written down as a test.
//
// The old Report kept its offset in a bare int that Update incremented without a bound; the only
// clamp was a throwaway copy inside View. Held on `down` for two seconds past the end of the report,
// the field read 400 while the body stopped at 12 — so the next 389 `up` presses moved nothing at all
// and the pane looked frozen. The measurement that named it: 400 downs, then count the ups needed
// before one line changes.
//
// With the offset inside a viewport, that count is ONE. This test asserts the count, not the absence
// of a blank screen: a renderer-side clamp already passed "the pane is not blank", which is exactly
// why the bug survived to reach the owner.
// KS2.7 extends it to every surface converted in this checkpoint, and drives all of them through the
// REAL router (Update(keyMsg(...))) rather than a pane handler — STYLE.md records twice that calling
// pane handlers directly is how two regression tests came to pass on frames that could not exhibit
// their bug.
func TestScrollingPastTheEndCostsExactlyOneKeyToComeBack(t *testing.T) {
	for _, tc := range scrollSurfaces() {
		t.Run(tc.name, func(t *testing.T) {
			m := tc.open(t)
			for i := 0; i < 400; i++ {
				m = press(m, "down")
			}
			// The invariant the bug actually broke, read off the MODEL rather than the frame: after
			// 400 presses past the end the offset is still inside the body. A frame that merely looks
			// right passed the old renderer-side clamp too, which is why that clamp survived to reach
			// the owner.
			vp := tc.vp(m)
			if got, limit := vp.YOffset(), vp.TotalLineCount()-vp.Height(); got > limit {
				t.Errorf("%s: offset is %d against a body that stops at %d — Update let it run",
					tc.name, got, limit)
			}
			if !vp.AtBottom() {
				t.Errorf("%s: 400 downs did not reach the end of the body (%d%%)",
					tc.name, int(vp.ScrollPercent()*100))
			}

			// …and the cost of coming back is ONE key. Compared on the STYLED frame, because on a
			// surface whose ↑↓ walk a cursor the first press back moves the CURSOR — the highlight is
			// the line that moved, and stripANSI would throw away exactly the evidence.
			atEnd := paneFrame(m)
			m = press(m, "up")
			if paneFrame(m) == atEnd {
				// Keep counting so the failure reports the real cost, the way the K6.1 measurement did.
				n := 1
				for ; n < 500; n++ {
					m = press(m, "up")
					if paneFrame(m) != atEnd {
						break
					}
				}
				t.Fatalf("%s: 400 downs past the end took %d ups before anything moved — the offset is "+
					"running away again (bug #30)", tc.name, n+1)
			}
		})
	}
}

// press sends one key through the real router, exactly as a terminal would.
func press(m Model, key string) Model {
	tm, _ := m.Update(keyMsg(key))
	return asModel(tm)
}

// scrollSurface is one converted surface: how to open it with a body longer than its pane, and which
// viewport it scrolls. It reuses the sweep's fixtures (scrollsweep_test.go), so the two acceptance
// measurements cannot disagree about what "a long body" is.
type scrollSurface struct {
	name string
	open func(*testing.T) Model
	vp   func(Model) viewport.Model
	raw  func(Model) viewport.Model
}

// scrollSurfaces is every surface KS2.7 put on the one scroll idiom, plus the three K6.2/K6.4
// already had. Built from the sweep's case table so a surface added there is measured here too —
// the alternative is two lists that drift, which is the failure this whole checkpoint is about.
func scrollSurfaces() []scrollSurface {
	var out []scrollSurface
	// A body a little longer than the pane is all bug #30's measurement needs — the runaway offset
	// happens PAST the end, and 500 rows of rendered markdown per keypress would only make the test
	// slow. The 500-line figure is the sweep's job (scrollsweep_test.go).
	for _, c := range scrollSweepCases(60) {
		c := c
		out = append(out, scrollSurface{
			name: c.name,
			vp:   c.vp,
			raw:  c.raw,
			open: func(t *testing.T) Model {
				t.Helper()
				m := c.grow(t, newGoldenModel(120, 30))
				for _, k := range c.keys {
					tm, _ := m.Update(keyMsg(k))
					m = tm
				}
				got := asModel(m)
				vp := c.vp(got)
				if vp.TotalLineCount() <= vp.Height() {
					t.Fatalf("fixture body is %d lines in a %d-row pane — it does not scroll, so this "+
						"proves nothing", vp.TotalLineCount(), vp.Height())
				}
				return got
			},
		})
	}
	return out
}

// The offset must be clamped where it is CHANGED, not where it is drawn (adr/0006 decision 1). Read
// the model's own state rather than the frame: a body that merely looks right can still be sitting on
// an impossible number, which is precisely how this shipped.
func TestPaneOffsetIsClampedInUpdateNotInTheRenderer(t *testing.T) {
	m := openScrollable(t, "r")
	for i := 0; i < 400; i++ {
		m = asModel(mustHandle(m.handleReportKey("down")))
	}
	vp := m.reportViewport()
	if got, max := m.report.vp.YOffset(), vp.TotalLineCount()-vp.Height(); got > max {
		t.Errorf("Report offset is %d against a body that stops at %d — Update let it run", got, max)
	}
	if !m.reportViewport().AtBottom() {
		t.Error("400 downs did not land at the bottom of the report")
	}

	k := openScrollable(t, "k")
	for i := 0; i < 400; i++ {
		k = asModel(mustHandle(k.handleKnowledgeKey("down")))
	}
	if got, max := k.knowledge.vp.YOffset(), k.knowledgeViewport().TotalLineCount()-k.knowledgeViewport().Height(); got > max {
		t.Errorf("Knowledge offset is %d against a body that stops at %d — Update let it run", got, max)
	}

	// KS2.7 extends the same measurement to every converted surface, through the REAL router — and
	// reads the STORED viewport (`raw`) rather than the builder's copy. That distinction is the whole
	// test: `<surface>Viewport()` re-sizes and re-loads on every call, so it re-clamps, and an offset
	// that only Update failed to bound would look perfectly sane through the builder while the field
	// on the model ran to 400. Reading the field is how K6.1 caught it the first time.
	for _, tc := range scrollSurfaces() {
		t.Run(tc.name, func(t *testing.T) {
			m := tc.open(t)
			for i := 0; i < 400; i++ {
				m = press(m, "down")
			}
			built := tc.vp(m)
			stored := tc.raw(m)
			limit := built.TotalLineCount() - built.Height()
			if got := stored.YOffset(); got > limit {
				t.Errorf("%s: the STORED offset is %d against a body that stops at %d — the clamp is "+
					"in the renderer, not in Update", tc.name, got, limit)
			}
			if stored.YOffset() == 0 {
				t.Errorf("%s: 400 downs left the stored offset at 0 — the key never reached the field, "+
					"so this measures nothing", tc.name)
			}
			// …and the RENDERER may not write it back either. A View that moved the offset would make
			// the position depend on how often the frame was drawn, which is the same defect wearing
			// the opposite sign.
			before := stored.YOffset()
			_ = m.View()
			redrawn := tc.raw(m)
			if after := redrawn.YOffset(); after != before {
				t.Errorf("%s: drawing a frame moved the stored offset %d → %d — View has a value "+
					"receiver and must not be where the position lives", tc.name, before, after)
			}
		})
	}
}

// Every key the ADR's table names has to actually move the pane, on EVERY surface. `end` is the one
// Report never had: a document you can enter but not reach the end of is the shape of the complaint.
//
// KS2.7 renamed it from …OnBothSurfaces — there were two when K6.2 wrote it and there are eleven
// now, which is the whole point of the checkpoint.
func TestPaneScrollSetIsBoundOnEveryConvertedSurface(t *testing.T) {
	for _, tc := range scrollSurfaces() {
		t.Run(tc.name, func(t *testing.T) {
			for _, k := range []string{"down", "j", "up", "d", "u", "pgdown", "pgup", "end", "G", "home"} {
				m := tc.open(t)
				// Each key is checked from the OPPOSITE end, or half of them would pass by doing
				// nothing at a boundary. `home` first for the downward keys matters: a live-tail pane
				// (Agent's transcript, the raw stream) OPENS at the bottom, so `down` there is
				// legitimately a no-op and testing it from the open position would prove nothing.
				if strings.Contains("up u pgup home", k) {
					m = press(m, "end")
				} else {
					m = press(m, "home")
				}
				before := paneFrame(m)
				if got := paneFrame(press(m, k)); got == before {
					t.Errorf("%s: %q moved nothing", tc.name, k)
				}
			}
		})
	}
}

// A8. Every converted surface's bottom bar carries the position readout, and it appears ONLY when
// the body outgrows the pane — a permanent "0%" on a pane that fits is noise dressed as information,
// which is the rule paneScrollStatus already encodes and which each surface has to actually honour.
func TestEveryConvertedSurfaceShowsItsScrollPercent(t *testing.T) {
	for _, tc := range scrollSurfaces() {
		t.Run(tc.name, func(t *testing.T) {
			m := tc.open(t)
			_, help := m.paneView()
			help = stripANSI(help)
			if !strings.Contains(help, "%") {
				t.Errorf("%s: a body longer than its pane carries no percent readout; help = %q",
					tc.name, help)
			}
			m = press(m, "end")
			if _, help := m.paneView(); !strings.Contains(stripANSI(help), "100%") {
				t.Errorf("%s: at the end of the body the readout should say 100%%, help = %q",
					tc.name, stripANSI(help))
			}
		})
	}
}

// A8, the raw stream's half: its `↕ scrolled back N — end to live-tail` integer is replaced by the
// percent plus an at-bottom live-tail marker. The old readout reported an INVERTED line count taken
// from the same unclamped field the window came from — so when the field ran away, the number the
// pane showed was the runaway itself.
func TestAgentRawReadsOutAPercentAndALiveTailMarker(t *testing.T) {
	var m tea.Model = newGoldenModel(120, 30)
	for i := 0; i < 300; i++ {
		m, _ = m.Update(MsgConsoleLine{Line: api.ConsoleLineDto{
			Seq: int64(i + 1), Text: "raw stdout line " + itoa(i)}})
	}
	m, _ = m.Update(keyMsg("c"))
	body := stripANSI(mustPaneBody(asModel(m)))
	if !strings.Contains(body, "● live tail") {
		t.Errorf("opening the raw stream lands on the tail and must say so:\n%s", body)
	}
	if strings.Contains(body, "scrolled back") {
		t.Errorf("the inverted integer readout is still here:\n%s", body)
	}

	m2 := press(asModel(m), "home")
	body = stripANSI(mustPaneBody(m2))
	if !strings.Contains(body, "0% — end to live-tail") {
		t.Errorf("scrolled to the top the raw stream should read 0%% and name the key back:\n%s", body)
	}
	if strings.Contains(body, "live tail") && !strings.Contains(body, "end to live-tail") {
		t.Errorf("a scrolled-back raw stream must not claim it is live:\n%s", body)
	}
}

func mustPaneBody(m Model) string {
	body, _ := m.paneView()
	return body
}

// `k` must NOT scroll, and this is a load-bearing absence. update.go's mnemonic loop is an exact
// string match that resolves before any pane handler, so a `k` bound here would be a key the help
// card advertises and the router eats — the exact defect K6.1 measured on Knowledge.
func TestPaneScrollNeverBindsATabMnemonic(t *testing.T) {
	claimed := map[string]string{}
	for i, k := range tabKey {
		claimed[k] = "tab " + tabNames[i]
	}
	for k, t2 := range foldedTabKey {
		claimed[k] = "folded → " + tabNames[t2]
	}
	for _, b := range paneScrollBindings() {
		for _, k := range b.Keys() {
			if owner, dup := claimed[k]; dup {
				t.Errorf("pane scroll binds %q, which is already %s — the mnemonic loop resolves first, "+
					"so this key can never reach a pane", k, owner)
			}
		}
	}
}

// The position readout is a percent (adr/0006 decision 1, glow's pager). A pane that fits carries no
// readout at all: a permanent "100%" on a report that never scrolled is noise dressed as information.
func TestPaneScrollStatusIsAPercentAndOnlyWhenItScrolls(t *testing.T) {
	m := openScrollable(t, "r")
	if got := paneScrollStatus(m.reportViewport()); got != "0%" {
		t.Errorf("a fresh long report should read 0%%, got %q", got)
	}
	for i := 0; i < 400; i++ {
		m = asModel(mustHandle(m.handleReportKey("down")))
	}
	if got := paneScrollStatus(m.reportViewport()); got != "100%" {
		t.Errorf("scrolled to the end the report should read 100%%, got %q", got)
	}

	short := m
	short.data.Plan = &api.StateDto{PlanName: "tiny"}
	short.data.Sessions, short.report.scores = nil, nil
	if got := paneScrollStatus(short.reportViewport()); got != "" {
		t.Errorf("a body that fits its pane must carry no percent, got %q", got)
	}
}

// K6.4. The owner queue's full view was the LAST surviving instance of bug #30's exact shape:
// `m.home.queueScroll++` in Update with the comment "clamped by the renderer", and a throwaway
// `min(queueScroll, maxScroll)` inside View that a value receiver could never write back. K6.2 closed
// that on Report and Knowledge; adr/0006 §5 names this view too, and it had been left behind.
//
// Same measurement, same surface-independent rule: past the end, ONE key comes back.
func TestOwnerQueuePastTheEndCostsExactlyOneKeyToComeBack(t *testing.T) {
	m := openLongOwnerQueue(t)
	for i := 0; i < 400; i++ {
		m = asModel(mustHandle(m.handleOwnerQueueKey("down")))
	}
	vp := m.ownerQueueViewport()
	if got, max := m.home.queueVp.YOffset(), vp.TotalLineCount()-vp.Height(); got > max {
		t.Errorf("owner queue offset is %d against a body that stops at %d — Update let it run", got, max)
	}
	atEnd := paneBody(m)
	if paneBody(asModel(mustHandle(m.handleOwnerQueueKey("up")))) == atEnd {
		t.Error("400 downs past the end took more than one `up` before a line moved (bug #30's shape)")
	}
}

// The whole scroll set, on the owner's own list. `end`/`G` and the half-page pair are the keys this
// pane never had — it bound up/down/home/pgup/pgdown by hand and stopped there, so a long queue could
// be entered but not reached the end of.
func TestOwnerQueueBindsTheOneScrollSet(t *testing.T) {
	for _, k := range []string{"down", "j", "up", "d", "u", "pgdown", "pgup", "end", "G", "home"} {
		m := openLongOwnerQueue(t)
		if strings.Contains("up u pgup home", k) {
			m = asModel(mustHandle(m.handleOwnerQueueKey("end")))
		}
		before := paneBody(m)
		if got := paneBody(asModel(mustHandle(m.handleOwnerQueueKey(k)))); got == before {
			t.Errorf("owner queue: %q moved nothing", k)
		}
	}
}

// openLongOwnerQueue opens `w` through the real router with a queue longer than the pane.
func openLongOwnerQueue(t *testing.T) Model {
	t.Helper()
	items := make([]api.OwnerQueueItemDto, 0, 40)
	for i := 0; i < 40; i++ {
		items = append(items, api.OwnerQueueItemDto{
			Id: "g" + itoa(i), Kind: "ownerGate", Title: "Stage K" + itoa(i) + " needs approval",
			Unblocks: "stage K" + itoa(i), Command: "conductor approve K" + itoa(i)})
	}
	tm, _ := newGoldenModel(120, 30).(Model).Update(MsgOwnerQueueUpdated{
		Queue: &api.OwnerQueueDto{Count: 40, GeneratedUtc: "2026-08-01T10:00:00Z", Items: items}})
	tm, _ = tm.(Model).Update(keyMsg("w"))
	m := asModel(tm)
	vp := m.ownerQueueViewport()
	if vp.TotalLineCount() <= vp.Height() {
		t.Fatalf("fixture queue is %d lines in a %d-row pane — it does not scroll, so this proves nothing",
			vp.TotalLineCount(), vp.Height())
	}
	return m
}

// --- helpers ------------------------------------------------------------------

// openScrollable opens a tab through the REAL router (so the mnemonic precedence is exercised) and
// fails the test if the body already fits — every assertion below is about a body that outgrows its
// pane, and a fitting one would pass them all vacuously.
func openScrollable(t *testing.T, mnemonic string) Model {
	t.Helper()
	tm, _ := newGoldenModel(120, 30).(Model).Update(keyMsg(mnemonic))
	if mnemonic == "k" {
		tm, _ = tm.Update(MsgKnowledgeUpdated{Ledger: longLedger(), Bugs: fixedBugs(), Evidence: fixedEvidence()})
	} else {
		tm, _ = tm.Update(MsgReportScores{Result: &api.ScoresDto{Scores: goldenScores()}})
	}
	m := asModel(tm)
	vp := m.reportViewport()
	if mnemonic == "k" {
		vp = m.knowledgeViewport()
	}
	if vp.TotalLineCount() <= vp.Height() {
		t.Fatalf("fixture body is %d lines in a %d-row pane — it does not scroll, so this proves nothing",
			vp.TotalLineCount(), vp.Height())
	}
	return m
}

// longLedger is the golden ledger grown past the pane. The golden fixture is two entries long on
// purpose — it pins a readable frame — so it cannot also be the fixture for "this document is longer
// than the window", which is the only condition under which a scroll bug exists at all.
func longLedger() *api.LedgerDto {
	l := fixedLedger()
	s := func(n int) *int { return &n }
	for i := 0; i < 60; i++ {
		l.Entries = append(l.Entries, api.LedgerEntryDto{
			Id:            int64(100 + i),
			SessionNumber: s(1 + i%9),
			Kind:          "note",
			Content:       "a ledger line long enough to be worth reading, number " + itoa(i),
			CreatedAt:     "2026-07-15T10:00:00Z",
		})
	}
	return l
}

func itoa(n int) string {
	if n < 10 {
		return string(rune('0' + n))
	}
	return itoa(n/10) + string(rune('0'+n%10))
}

func paneBody(m Model) string {
	body, _ := m.paneView()
	return stripANSI(body)
}

// paneFrame is the pane body WITH its styling. Use it when the thing that must move may be a
// selection highlight rather than a line of text — stripANSI deletes a cursor move entirely, and a
// test that cannot see the cursor move would call a working key broken.
func paneFrame(m Model) string {
	body, _ := m.paneView()
	return body
}
