package tui

import (
	"strings"
	"testing"

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
func TestScrollingPastTheEndCostsExactlyOneKeyToComeBack(t *testing.T) {
	for _, tc := range []struct {
		name string
		open string
		down func(Model) Model
	}{
		{"report", "r", func(m Model) Model { return asModel(mustHandle(m.handleReportKey("down"))) }},
		{"knowledge", "k", func(m Model) Model { return asModel(mustHandle(m.handleKnowledgeKey("down"))) }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			m := openScrollable(t, tc.open)
			for i := 0; i < 400; i++ {
				m = tc.down(m)
			}
			atEnd := paneBody(m)

			up := func(mm Model) Model {
				switch tc.name {
				case "report":
					return asModel(mustHandle(mm.handleReportKey("up")))
				default:
					return asModel(mustHandle(mm.handleKnowledgeKey("up")))
				}
			}
			m = up(m)
			if paneBody(m) == atEnd {
				// Keep counting so the failure reports the real cost, the way the K6.1 measurement did.
				n := 1
				for ; n < 500; n++ {
					m = up(m)
					if paneBody(m) != atEnd {
						break
					}
				}
				t.Fatalf("%s: 400 downs past the end took %d ups before one line moved — the offset is "+
					"running away again (bug #30)", tc.name, n+1)
			}
		})
	}
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
}

// Every key the ADR's table names has to actually move the pane. `end` is the one Report never had:
// a document you can enter but not reach the end of is the shape of the complaint.
func TestPaneScrollSetIsBoundOnBothSurfaces(t *testing.T) {
	for _, k := range []string{"down", "j", "up", "d", "u", "pgdown", "pgup", "end", "G", "home"} {
		for _, tc := range []struct {
			name  string
			open  string
			press func(Model, string) Model
		}{
			{"report", "r", func(m Model, key string) Model { return asModel(mustHandle(m.handleReportKey(key))) }},
			{"knowledge", "k", func(m Model, key string) Model { return asModel(mustHandle(m.handleKnowledgeKey(key))) }},
		} {
			m := openScrollable(t, tc.open)
			// Downward keys are checked from the top, upward keys from the bottom, or half of them
			// would pass by doing nothing at a boundary.
			if strings.Contains("up u pgup home", k) {
				m = tc.press(m, "end")
			}
			before := paneBody(m)
			if got := paneBody(tc.press(m, k)); got == before {
				t.Errorf("%s: %q moved nothing", tc.name, k)
			}
		}
	}
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
