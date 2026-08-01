package widgets

import (
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

// SF3.3 — the sidebar's execution-order cue.
//
// The rail renders the plan in DECLARED order and marks each stage with its state. Before this cue
// those two facts could disagree without saying so: after a `goto`, the run sits on SF5 while SF3 is
// still a grey ○ two rows up, rendered exactly like SF6 below it — one of them is "not yet", the
// other is "we went past it", and the rail said the same thing about both.

// sidebarWith builds a rail wide enough that nothing truncates, so these tests measure the cue and
// not the width fallback (which has its own test at the bottom).
func sidebarWith(stages []api.StageDto) SidebarModel {
	m := NewSidebar()
	m.Width, m.Height = 40, 24
	m.Stages = stages
	return m
}

// The set is positional and deliberately narrow: still to-do, and above a stage the run has reached.
func TestStagesJumpedIsWhatTheRunWentPast(t *testing.T) {
	cases := []struct {
		name   string
		stages []api.StageDto
		want   []string
	}{
		{"a plan running in declared order has jumped nothing", []api.StageDto{
			{Id: "S1", State: "confirmed"}, {Id: "S2", State: "active"}, {Id: "S3", State: "todo"},
		}, nil},
		{"a goto past S2 leaves S2 behind", []api.StageDto{
			{Id: "S1", State: "confirmed"}, {Id: "S2", State: "todo"}, {Id: "S3", State: "active"},
		}, []string{"S2"}},
		{"a skip is a decision, not a jump", []api.StageDto{
			{Id: "S1", State: "skipped"}, {Id: "S2", State: "active"}, {Id: "S3", State: "todo"},
		}, nil},
		{"skips and jumps side by side: only the jump is named", []api.StageDto{
			{Id: "S1", State: "skipped"}, {Id: "S2", State: "todo"}, {Id: "S3", State: "confirmed"},
		}, []string{"S2"}},
		{"a stage the run failed is a stage the run reached", []api.StageDto{
			{Id: "S1", State: "failed"}, {Id: "S2", State: "active"}, {Id: "S3", State: "todo"},
		}, nil},
		{"the furthest stage reached is what counts, not the active one", []api.StageDto{
			{Id: "S1", State: "confirmed"}, {Id: "S2", State: "active"}, {Id: "S3", State: "todo"},
			{Id: "S4", State: "confirmed"},
		}, []string{"S3"}},
		{"a plan nobody has started has jumped nothing", []api.StageDto{
			{Id: "S1", State: "todo"}, {Id: "S2", State: "todo"},
		}, nil},
		{"an unknown state counts as not-yet, never as reached", []api.StageDto{
			{Id: "S1", State: "quarantined"}, {Id: "S2", State: "todo"},
		}, nil},
		{"two jumps are both named, in declared order", []api.StageDto{
			{Id: "S1", State: "todo"}, {Id: "S2", State: "todo"}, {Id: "S3", State: "gating"},
		}, []string{"S1", "S2"}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got := stagesJumped(c.stages)
			if strings.Join(got, ",") != strings.Join(c.want, ",") {
				t.Errorf("stagesJumped = %v, want %v", got, c.want)
			}
		})
	}
}

// An in-order plan must render byte-for-byte what it rendered before the cue existed: a cue that
// appears on the healthy case is a cue nobody reads on the unhealthy one.
func TestSidebarSaysNothingWhenExecutionFollowsTheDeclaredOrder(t *testing.T) {
	m := sidebarWith([]api.StageDto{
		{Id: "S1", Title: "first", Done: 2, Total: 2, State: "confirmed"},
		{Id: "S2", Title: "second", Done: 1, Total: 3, State: "active"},
		{Id: "S3", Title: "third", Done: 0, Total: 4, State: "todo"},
	})
	got := stripANSIw(m.View())
	if strings.Contains(got, jumpGlyph) || strings.Contains(got, "jumped") {
		t.Errorf("no divergence, so no cue and no mark:\n%s", got)
	}
}

// The cue names the stages, and the row of each one carries the mark — the line says WHAT diverged,
// the mark says WHERE, and a reader scanning the rail finds the row without re-reading the line.
func TestSidebarCuesAJumpOnBothTheLineAndTheRow(t *testing.T) {
	m := sidebarWith([]api.StageDto{
		{Id: "S1", Title: "first", Done: 2, Total: 2, State: "confirmed"},
		{Id: "S2", Title: "second", Done: 0, Total: 3, State: "todo"},
		{Id: "S3", Title: "third", Done: 1, Total: 4, State: "active"},
	})
	got := stripANSIw(m.View())
	if !strings.Contains(got, jumpGlyph+" jumped: S2") {
		t.Errorf("the cue must name the stage the run went past:\n%s", got)
	}

	var s2Row, s3Row string
	for _, line := range strings.Split(got, "\n") {
		if strings.Contains(line, "S2 0/3") {
			s2Row = line
		}
		if strings.Contains(line, "S3 1/4") {
			s3Row = line
		}
	}
	if !strings.Contains(s2Row, jumpGlyph) {
		t.Errorf("the jumped stage's own row must carry the mark: %q", s2Row)
	}
	if strings.Contains(s3Row, jumpGlyph) {
		t.Errorf("the stage the run is ON is not a jumped stage: %q", s3Row)
	}
}

// The rail self-scrolls anchored on the active row (windowRows). A cue drawn once at the top of the
// tree is therefore the FIRST thing a tall plan scrolls away — and a plan long enough to scroll is
// exactly the plan a jump hides in. It rides directly above the active row so the two are windowed
// together or not at all.
func TestSidebarCueSurvivesTheRailsSelfScroll(t *testing.T) {
	stages := []api.StageDto{{Id: "S0", Title: "jumped", Done: 0, Total: 2, State: "todo"}}
	for i := 1; i <= 24; i++ {
		stages = append(stages, api.StageDto{
			Id: "S" + string(rune('a'+i-1)), Title: "filler", Done: 1, Total: 1, State: "confirmed"})
	}
	stages = append(stages, api.StageDto{Id: "SZ", Title: "here", Done: 0, Total: 3, State: "active"})

	m := NewSidebar()
	m.Width, m.Height = 40, 10 // far shorter than the plan: the rail must window
	m.Stages = stages
	got := stripANSIw(m.View())

	if !strings.Contains(got, "SZ 0/3") {
		t.Fatalf("the active row is the scroll anchor and must be in frame:\n%s", got)
	}
	if !strings.Contains(got, "jumped: S0") {
		t.Errorf("the cue scrolled out of the window it exists to warn inside:\n%s", got)
	}
	lines := strings.Split(got, "\n")
	for i, line := range lines {
		if strings.Contains(line, "SZ 0/3") {
			if i == 0 || !strings.Contains(lines[i-1], "jumped") {
				t.Errorf("the cue belongs directly above the active row, got %q above %q",
					strings.TrimRight(lines[max(0, i-1)], " "), strings.TrimRight(line, " "))
			}
			break
		}
	}
}

// A run that parked, finished or was aborted mid-plan has no active row to hang the cue under — and
// that is precisely when someone opens the rail asking why it stopped where it did.
func TestSidebarStillCuesWithNoActiveStage(t *testing.T) {
	m := sidebarWith([]api.StageDto{
		{Id: "S1", Title: "first", Done: 0, Total: 2, State: "todo"},
		{Id: "S2", Title: "second", Done: 2, Total: 2, State: "confirmed"},
	})
	got := stripANSIw(m.View())
	if !strings.Contains(got, "jumped: S1") {
		t.Errorf("a stopped run's divergence is still divergence:\n%s", got)
	}
}

// Narrow rails degrade to the COUNT rather than to a clipped list: "↷ jumped: S2, S" reads as a stage
// id that does not exist, which is worse than not naming them at all. And the row mark outranks the
// cost/attempts meta in the width fallback — a jumped stage ran nothing, so it has neither.
func TestSidebarCueDegradesToACountAndKeepsTheMark(t *testing.T) {
	stages := []api.StageDto{
		{Id: "S1", Title: "a stage with a long title", Done: 0, Total: 2, State: "todo"},
		{Id: "S2", Title: "another long stage title", Done: 0, Total: 2, State: "todo"},
		{Id: "S3", Title: "the one the run is on", Done: 1, Total: 4, State: "active", Attempts: 3, CostUsd: 1.25},
	}
	m := sidebarWith(stages)
	m.Width = 17 // one column short of "  ↷ jumped: S1, S2"
	got := stripANSIw(m.View())

	var cue string
	for _, line := range strings.Split(got, "\n") {
		if strings.Contains(line, "jumped") {
			cue = strings.TrimSpace(line)
		}
	}
	if strings.Contains(cue, "S1") || strings.Contains(cue, "S2") {
		t.Errorf("a 17-col rail cannot hold the list; a half-named stage id is worse than none: %q", cue)
	}
	if !strings.Contains(cue, "2") || !strings.Contains(cue, jumpGlyph) {
		t.Errorf("every tier keeps the glyph and the count: %q", cue)
	}
	for _, line := range strings.Split(got, "\n") {
		if strings.Contains(line, "S1 0/2") && !strings.Contains(line, jumpGlyph) {
			t.Errorf("the mark must outlive the width fallback: %q", line)
		}
		// Every row is clipped to the rail's width, mark included — no row may overflow.
		if w := len([]rune(strings.TrimRight(line, " "))); w > m.Width {
			t.Errorf("row wider than the %d-col rail (%d): %q", m.Width, w, line)
		}
	}
}
