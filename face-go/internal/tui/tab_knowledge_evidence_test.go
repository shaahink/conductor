package tui

import (
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// K5.3 — the Face's evidence surface. The goldens pin the whole frame; these pin the claims the
// frame is FOR, so a future edit that keeps the layout and loses the meaning still fails.
//
// There is no eleventh tab: SF1.3 / adr/0004 caps the strip at ten, so evidence is re-homed into
// Knowledge, whose question it already answers — what does this run know, what is wrong with it,
// and what has it got to show for itself.

func withEvidence(t *testing.T, ev *api.EvidenceDto) Model {
	t.Helper()
	var tm tea.Model = newGoldenModel(140, 40)
	tm, _ = tm.Update(keyMsg("k"))
	tm, _ = tm.Update(MsgKnowledgeUpdated{Ledger: fixedLedger(), Bugs: fixedBugs(), Evidence: ev})
	return tm.(Model)
}

func TestKnowledgeShowsEveryFieldOfARegisteredArtifact(t *testing.T) {
	frame := stripANSI(withEvidence(t, fixedEvidence()).View().Content)

	for _, want := range []string{
		"◆ Evidence (2)",
		"F7.2-dashboard.png", // the artifact itself
		"[image]",            // its kind, first-class rather than "other"
		"180.0 KB",           // what a surface deciding whether to send it needs
		"watcher",            // how the engine came to know
		"(s12/F7.2)",         // which session produced it, and what it evidences
		"[text]",
		"claim",
	} {
		if !strings.Contains(frame, want) {
			t.Errorf("evidence section is missing %q\n%s", want, frame)
		}
	}
}

// The registry can be larger than the page the endpoint served. A surface that shows five of forty
// as if it were all of them is worse than one that shows nothing.
func TestKnowledgeSaysWhenItIsShowingOnlyAPageOfTheRegistry(t *testing.T) {
	ev := fixedEvidence()
	ev.Count = 40
	frame := stripANSI(withEvidence(t, ev).View().Content)
	if !strings.Contains(frame, "◆ Evidence (2 of 40)") {
		t.Errorf("a truncated registry must say so\n%s", frame)
	}
}

// An engine too old to serve /evidence, or a run that produced none: the section says so rather than
// vanishing, because "no artifact" and "this Face cannot see artifacts" both need saying.
func TestKnowledgeSaysNoneRatherThanHidingTheSection(t *testing.T) {
	frame := stripANSI(withEvidence(t, &api.EvidenceDto{}).View().Content)
	if !strings.Contains(frame, "◆ Evidence (0)") || !strings.Contains(frame, "none registered") {
		t.Errorf("empty evidence must still render its header\n%s", frame)
	}
	// And the other two sections are untouched by it.
	if !strings.Contains(frame, "◆ Open bugs") || !strings.Contains(frame, "◆ Knowledge ledger") {
		t.Errorf("evidence must not displace the sections it sits between\n%s", frame)
	}
}

func TestKnowledgeHelpLineCountsEvidence(t *testing.T) {
	m := withEvidence(t, fixedEvidence())
	_, help := m.renderKnowledgePane()
	if !strings.Contains(help, "2 evidence") {
		t.Errorf("help line should count evidence, got %q", help)
	}
}

// The one thing an evidence row must never lose is WHICH artifact it is. Every path in a run shares
// the same directory prefix, so a right-truncated row spends its width saying nothing.
func TestEvidencePathKeepsTheFileNameWhenItCannotKeepEverything(t *testing.T) {
	const p = ".conductor/evidence/K5/K5.3-dashboard.png"
	// Exactly `room` wide, and what survives is the tail: the file name is intact and the shared
	// directory prefix is what got spent.
	got := evidencePath(p, 24)
	if len([]rune(got)) != 24 || !strings.HasSuffix(got, "K5.3-dashboard.png") || !strings.HasPrefix(got, "…") {
		t.Errorf("evidencePath elided the wrong end: %q", got)
	}
	if got := evidencePath(p, 200); got != p {
		t.Errorf("a path that fits must not be touched: %q", got)
	}
	if got := evidencePath(p, 0); got != "" {
		t.Errorf("no room means no path, not a panic: %q", got)
	}

	frame := stripANSI(withEvidence(t, fixedEvidence()).View().Content)
	if !strings.Contains(frame, "F7.2-dashboard.png") {
		t.Errorf("the rendered row lost the file name\n%s", frame)
	}
}

func TestEvidenceSizeReadsAtAGlance(t *testing.T) {
	for _, c := range []struct {
		in   int64
		want string
	}{
		{512, "512 B"},
		{8814, "8.6 KB"},
		{184320, "180.0 KB"},
		{20 * 1024 * 1024, "20.0 MB"},
	} {
		if got := evidenceSize(c.in); got != c.want {
			t.Errorf("evidenceSize(%d) = %q, want %q", c.in, got, c.want)
		}
	}
}
