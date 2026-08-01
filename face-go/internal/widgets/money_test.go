package widgets

import (
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

// stripANSIw is transcript_test.go's ansiRe applied to a plain string rather than a model.
func stripANSIw(s string) string { return ansiRe.ReplaceAllString(s, "") }

// The whole point of the basis: a dollar figure is only as good as the way it was arrived at, and
// the two the engine cannot stand behind must not render as measurements. "$0.00" printed for a
// session whose cost is simply not knowable yet is the lie that put two contradictory cost readouts
// in one frame — the Agent footer reading "$0.00 · ↑0 ↓0" under a pane reading $13.07.
func TestFmtSessionCostSpeaksTheEnginesBasisVocabulary(t *testing.T) {
	cases := []struct {
		name  string
		cost  float64
		basis string
		want  string
	}{
		{"nothing in flight says nothing", 0, api.BasisNone, ""},
		{"a measured total is stated flat", 13.07, api.BasisMeasured, "$13.07"},
		{"a streamed total is just as good", 13.07, api.BasisStreamed, "$13.07"},
		{"an inferred total is marked inferred", 13.07, api.BasisRunRate, "~$13.07"},
		{"real tokens with no rate is not $0.00", 0, api.BasisNoRate, "$—"},
		{"a sub-cent charge is not rounded away", 0.003, api.BasisMeasured, "<$0.01"},
		{"pre-SC2.3 engine: a zero it cannot explain says nothing", 0, "", ""},
		{"pre-SC2.3 engine: a real figure is taken at face value", 1.5, "", "$1.50"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := FmtSessionCost(c.cost, c.basis); got != c.want {
				t.Errorf("FmtSessionCost(%v, %q) = %q, want %q", c.cost, c.basis, got, c.want)
			}
		})
	}
}

// The top bar is one of the two surfaces that reads a session cost. It must drop the figure — not
// print a zero — when the engine says the cost is not knowable, which is exactly the state a fresh
// session sits in for its first minute.
func TestTopBarOmitsASessionCostItCannotStandBehind(t *testing.T) {
	base := api.StateDto{
		PlanName: "p", Status: "running", StageId: "S1", AgentActive: true, SessionNumber: 7,
		SessionElapsedSec: 90,
	}

	noRate := base
	noRate.SessionCostBasis = api.BasisNoRate
	// Scoped to the SESSION segment: the run total beside it is legitimately $0.00 here.
	if got := stripANSIw(RenderTopBar(api.ConnectionState{Connected: true}, &noRate, 160, 0)); strings.Contains(got, "s7 $0.00") {
		t.Errorf("an unpriceable session must not render $0.00 on the top bar:\n%s", got)
	} else if !strings.Contains(got, "s7 $—") {
		t.Errorf("an unpriceable session says so where its cost would have been:\n%s", got)
	}

	measured := base
	measured.SessionCostUsd, measured.SessionCostBasis = 13.07, api.BasisMeasured
	if got := stripANSIw(RenderTopBar(api.ConnectionState{Connected: true}, &measured, 160, 0)); !strings.Contains(got, "$13.07") {
		t.Errorf("a priced session's cost belongs on the top bar:\n%s", got)
	}

	estimated := base
	estimated.SessionCostUsd, estimated.SessionCostBasis = 13.07, api.BasisRunRate
	if got := stripANSIw(RenderTopBar(api.ConnectionState{Connected: true}, &estimated, 160, 0)); !strings.Contains(got, "~$13.07") {
		t.Errorf("an inferred cost must say it is inferred:\n%s", got)
	}
}

// The "3×" suffix on a stage row is a mark with two plausible readings — attempts or checkpoints —
// that differ in whether the run is in trouble. It gets a legend wherever it is rendered, and only
// where it is rendered.
func TestSidebarLegendsTheAttemptsMarker(t *testing.T) {
	m := NewSidebar()
	m.Width, m.Height = 34, 20

	m.Stages = []api.StageDto{{Id: "S1", Title: "first", Done: 1, Total: 1, State: "done"}}
	if got := stripANSIw(m.View()); strings.Contains(got, "attempts") {
		t.Errorf("no stage carries a marker, so no legend:\n%s", got)
	}

	m.Stages = []api.StageDto{{Id: "S1", Title: "first", Done: 1, Total: 2, State: "active", Attempts: 4}}
	got := stripANSIw(m.View())
	if !strings.Contains(got, "n× attempts") {
		t.Errorf("the 4× marker must be explained:\n%s", got)
	}
	if !strings.Contains(got, "4×") {
		t.Errorf("the marker itself must still render:\n%s", got)
	}
	// The rail is height-budgeted and self-scrolling: the legend rides the PLAN heading rather than
	// spending a row that a stage would otherwise have had.
	if head := strings.Split(got, "\n")[0]; !strings.Contains(head, "PLAN") || !strings.Contains(head, "attempts") {
		t.Errorf("the legend belongs on the heading row, not a row of its own: %q", head)
	}

	// A rail too narrow to hold heading + legend drops the legend rather than wrapping the heading.
	m.Width = 12
	if got := stripANSIw(m.View()); strings.Contains(got, "attempts") {
		t.Errorf("a 12-col rail has no room for the legend:\n%s", got)
	}
}
