package widgets

import (
	"fmt"
	"strings"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

// SidebarModel is the always-on glanceable rail: plan tree, gate battery, and the live MCP task
// list. It is deliberately not focusable — all interaction happens through tabs and the command
// bar (see STYLE.md).
type SidebarModel struct {
	Stages   []api.StageDto
	Gates    []api.GateDto
	Tasks    []api.TaskDto
	Expanded map[string]bool
	Width    int
	Height   int
}

func NewSidebar() SidebarModel {
	return SidebarModel{
		Expanded: make(map[string]bool),
		Width:    30,
		Height:   20,
	}
}

func (m SidebarModel) Update(msg any) SidebarModel {
	switch msg := msg.(type) {
	case MsgSetData:
		if stages, ok := msg.Stages.([]api.StageDto); ok {
			m.Stages = stages
			for _, s := range stages {
				if s.State == "active" || s.State == "gating" {
					m.Expanded[s.Id] = true
				}
			}
		}
		if gates, ok := msg.Gates.([]api.GateDto); ok {
			m.Gates = gates
		}
		if tasks, ok := msg.Tasks.([]api.TaskDto); ok {
			m.Tasks = tasks
		}
		return m
	}
	return m
}

func (m SidebarModel) View() string {
	rows := []string{sidebarTitleStyle.Render("▶ PLAN") + m.attemptsLegend()}
	activeRow := 0 // the row to keep in view when the plan is taller than the rail

	// SF3.3: the stages the run went past without running. Computed once, before the loop, because
	// both halves of the cue — the mark on each jumped row and the line that names them — have to
	// agree, and computing it twice is how one of them survives an edit the other doesn't.
	jumped := stagesJumped(m.Stages)
	jumpedSet := make(map[string]bool, len(jumped))
	for _, id := range jumped {
		jumpedSet[id] = true
	}
	cued := false

	for _, stage := range m.Stages {
		glyph, style := stageGlyph(stage.State)
		if stage.State == "active" || stage.State == "gating" {
			// The cue rides DIRECTLY above the active row. windowRows anchors the rail's self-scroll on
			// that row, so a cue drawn once at the top of the tree is the first thing a tall plan
			// scrolls away — and a plan long enough to scroll is exactly the plan a jump hides in.
			if line := jumpedCue(jumped, m.Width); line != "" && !cued {
				rows, cued = append(rows, line), true
			}
			activeRow = len(rows)
		}
		rows = append(rows, style.Render(m.stageLine(glyph, stage, jumpedSet[stage.Id])))

		if m.Expanded[stage.Id] && len(stage.Checkpoints) > 0 {
			for _, cp := range stage.Checkpoints {
				cg, cs := checkpointGlyph(cp.Status)
				rows = append(rows, "  "+cs.Render(cg+cp.Id+" "+truncate(cp.Title, m.Width-10)))
			}
		}
	}

	// No active stage to hang it under — a run that has parked, finished or been aborted mid-plan is
	// still a run whose order diverged, and that is precisely when someone is reading the rail asking
	// why. Falls to the foot of the tree, which is where the eye lands when nothing is highlighted.
	if !cued {
		if line := jumpedCue(jumped, m.Width); line != "" {
			rows = append(rows, line)
		}
	}

	if len(m.Gates) > 0 {
		rows = append(rows, "", dimStyle.Render("── GATES ──"))
		for _, gate := range m.Gates {
			g, gs := gateGlyph(gate.State)
			suffix := ""
			if gate.State == "running" && gate.ElapsedSec > 0 {
				suffix = dimStyle.Render(fmt.Sprintf(" %.0fs", gate.ElapsedSec))
			}
			rows = append(rows, "  "+gs.Render(g+gate.Name)+suffix)
		}
	}

	if len(m.Tasks) > 0 {
		rows = append(rows, "", dimStyle.Render("── TASKS ──"))
		for _, task := range m.tasksWindow(6) {
			tg, ts := taskGlyph(task.Status)
			rows = append(rows, "  "+ts.Render(tg+" "+truncate(task.Title, m.Width-6)))
		}
	}

	rows = m.windowRows(rows, activeRow)

	// Clip each row ANSI-safely: a row that still overflows (tight widths) must truncate, never
	// wrap — a wrapped sidebar row pushes every row below it down a line.
	clip := lipgloss.NewStyle().MaxWidth(m.Width)
	for i, r := range rows {
		rows[i] = clip.Render(r)
	}

	return lipgloss.NewStyle().
		Width(m.Width).Height(m.Height).
		Render(strings.Join(rows, "\n"))
}

// windowRows scrolls a too-tall rail so the active stage stays visible (the sidebar isn't focusable,
// so it self-scrolls). On a 30-checkpoint plan the raw list overflows m.Height and lipgloss would
// silently clip the bottom — including the active stage if it sits low. A clipped edge shows a
// "↑/↓ N more" marker so it never looks like the plan just ends.
func (m SidebarModel) windowRows(rows []string, anchor int) []string {
	if m.Height < 3 || len(rows) <= m.Height {
		return rows
	}
	start := anchor - m.Height/2
	if start < 0 {
		start = 0
	}
	if start+m.Height > len(rows) {
		start = len(rows) - m.Height
	}
	end := start + m.Height
	win := append([]string{}, rows[start:end]...)
	if start > 0 {
		win[0] = dimStyle.Render(fmt.Sprintf("  ↑ %d more", start))
	}
	if end < len(rows) {
		win[len(win)-1] = dimStyle.Render(fmt.Sprintf("  ↓ %d more", len(rows)-end))
	}
	return win
}

// tasksWindow keeps the list short: everything in flight plus the most recent context around it.
func (m SidebarModel) tasksWindow(max int) []api.TaskDto {
	if len(m.Tasks) <= max {
		return m.Tasks
	}
	// Center the window on the first non-done task so "what's next" is always visible.
	first := 0
	for i, t := range m.Tasks {
		if t.Status != "done" {
			first = i
			break
		}
	}
	start := first - 1
	if start < 0 {
		start = 0
	}
	if start+max > len(m.Tasks) {
		start = len(m.Tasks) - max
	}
	return m.Tasks[start : start+max]
}

// attemptsLegend explains the "3×" suffix stageLine hangs off a stage row. The critique that opened
// this era named the marker "unexplained", and it is: a bare "4×" beside a stage title reads as a
// count of SOMETHING, and the two readings a reasonable person lands on — four attempts, four
// checkpoints — differ in whether the run is healthy. It rides the PLAN heading rather than taking a
// row of its own (the rail is height-budgeted and self-scrolling), and it appears only once a stage
// has actually retried: a legend for a mark that is nowhere on screen is just noise in the gutter.
func (m SidebarModel) attemptsLegend() string {
	// The trigger is "a marker is on screen", not "a stage retried": a legend that vanishes while a
	// "1×" is still rendered leaves exactly the unexplained mark it exists to explain.
	retried := false
	for _, s := range m.Stages {
		if s.Attempts > 0 {
			retried = true
			break
		}
	}
	const legend = "  n× attempts"
	if !retried || m.Width < lipgloss.Width("▶ PLAN")+lipgloss.Width(legend) {
		return ""
	}
	return dimStyle.Render(legend)
}

// SF3.3 — the execution-order cue. The plan declares an order; a run does not have to follow it
// (`goto`, `retry-stage`, `rollback` all move the run somewhere the declared order did not point),
// and the rail used to render both cases identically: a grey ○ beside a stage means "not yet" whether
// the run is about to reach it or walked past it three stages ago.
//
// jumpGlyph is that difference, and it is a MARK rather than a colour so it survives the row's own
// status style, a mono theme and a screenshot.
const jumpGlyph = "↷"

// stagesJumped names the stages the run went past without running: still to-do, but sitting above a
// stage the run has already reached. This positional reading is the only divergence the rail can see
// honestly — the wire carries no per-stage start time, so "declared order" is the order of these rows
// and "execution order" is which of them have been entered.
//
// A SKIPPED stage is never in the set. A skip is a decision someone made and the rail already renders
// it with its own glyph; marking it as jumped would turn every deliberate retirement into a warning
// and teach the reader to ignore the mark that matters.
func stagesJumped(stages []api.StageDto) []string {
	last := -1
	for i, s := range stages {
		if stageReached(s.State) {
			last = i
		}
	}
	if last < 1 {
		return nil
	}
	var jumped []string
	for _, s := range stages[:last] {
		if !stageReached(s.State) && s.State != "skipped" {
			jumped = append(jumped, s.Id)
		}
	}
	return jumped
}

// stageReached reports whether the run has been INTO this stage — running it now, finished it, or
// failed it. It is deliberately not the inverse of "todo": the engine's vocabulary has states this
// rail renders but never anticipates ("gating" is a stage mid-battery, "confirmed" is one Conductor
// itself signed off), and an unknown state counts as not-yet rather than as reached, so a state added
// later downgrades the cue to silence instead of inventing a jump.
func stageReached(state string) bool {
	switch state {
	case "active", "gating", "confirmed", "done", "failed":
		return true
	}
	return false
}

// jumpedCue is the one line that says the run is not where the declared order says it should be. It
// TIERS like the top bar's git chip rather than clipping: "↷ jumped: SF2, SF" reads as a stage id
// that does not exist, so a rail too narrow for the list states the count instead and a rail too
// narrow for that states the bare number. Every tier still carries the glyph and a digit, which is
// what makes the marks up the rail legible.
func jumpedCue(jumped []string, width int) string {
	if len(jumped) == 0 {
		return ""
	}
	head := "  " + jumpGlyph + " "
	for _, tier := range []string{
		"jumped: " + strings.Join(jumped, ", "),
		plural(len(jumped), "stage") + " jumped",
		fmt.Sprintf("%d jumped", len(jumped)),
	} {
		if line := head + tier; lipgloss.Width(line) <= width {
			return stageJumpedStyle.Render(line)
		}
	}
	return stageJumpedStyle.Render(truncate(head+fmt.Sprintf("%d", len(jumped)), max(1, width)))
}

// stageLine composes one plan row: the id and progress score (done/total) always survive; the title
// takes whatever width is left; a cost/attempts suffix shows for stages that have actually run. The
// whole line is later rendered in the stage's status colour, so this returns plain text — pre-truncated
// so the caller's single Render() never slices a styled string mid-escape (M5.2: state/score/cost/attempts).
func (m SidebarModel) stageLine(glyph string, stage api.StageDto, jumped bool) string {
	score := fmt.Sprintf("%d/%d", stage.Done, stage.Total)

	attempts, cost := "", ""
	if stage.Attempts > 0 {
		attempts = fmt.Sprintf(" %d×", stage.Attempts) // "3×" attempts
	}
	if stage.CostUsd > 0 {
		cost = fmt.Sprintf(" $%.2f", stage.CostUsd)
	}
	// The jump mark rides in the meta suffix so the title truncation reserves room for it, and it
	// leads every meta variant below so the width fallback drops cost and attempts BEFORE it — a
	// jumped stage has neither (nothing ran), and the mark is the whole reason the row differs.
	mark := ""
	if jumped {
		mark = " " + jumpGlyph
	}

	// Reserve: glyph (2 cols) + id + space + score + space + meta + a little margin. When the
	// column is tight, whole meta tokens drop (cost first, then attempts) — never a clipped "$0".
	compose := func(meta string) string {
		reserve := 2 + len(stage.Id) + 1 + len(score) + 1 + lipgloss.Width(meta) + 1
		titleW := m.Width - reserve
		if titleW < 4 {
			titleW = 4
		}
		return glyph + stage.Id + " " + score + " " + truncate(stage.Title, titleW) + meta
	}
	for _, meta := range []string{mark + attempts + cost, mark + attempts, mark} {
		if line := compose(meta); lipgloss.Width(line) <= m.Width {
			return line
		}
	}
	return compose(mark)
}

func stageGlyph(state string) (string, lipgloss.Style) {
	g, s := StageGlyph(state)
	return g + " ", s // the sidebar's rows carry the trailing space; other panes space themselves
}

// StageGlyph maps a stage state to its glyph + colour. Exported because the Report tab (U2.2) renders
// the same states, and a second copy of this switch is how the same stage ends up ✓ in the sidebar
// and ○ two panes over — which is exactly what a hand-written copy did before this was shared. The
// vocabulary is the ENGINE's ("confirmed"/"gating"/"skipped" are real states, and were the ones the
// duplicate got wrong), so extend it here or nowhere.
func StageGlyph(state string) (string, lipgloss.Style) {
	switch state {
	case "confirmed", "done":
		return "✓", stageDoneStyle
	case "active", "gating":
		return "●", stageActiveStyle
	case "failed":
		return "✗", stageFailStyle
	case "skipped":
		return "⊘", stageSkippedStyle
	default:
		return "○", stageTodoStyle
	}
}

func checkpointGlyph(status string) (string, lipgloss.Style) {
	switch status {
	case "done", "confirmed":
		return "✓ ", stageDoneStyle
	case "in_progress":
		return "● ", stageActiveStyle
	case "skipped":
		return "⊘ ", stageSkippedStyle
	default:
		return "○ ", stageTodoStyle
	}
}

func taskGlyph(status string) (string, lipgloss.Style) {
	switch status {
	case "done":
		return "✓", stageDoneStyle
	case "in_progress":
		return "●", stageActiveStyle
	default:
		return "○", stageTodoStyle
	}
}

func gateGlyph(state string) (string, lipgloss.Style) {
	g, s := GateGlyph(state)
	return g + " ", s
}

// GateGlyph maps a gate state to its glyph + colour — shared with the Report tab for the same reason
// as StageGlyph. The engine's gate vocabulary is "pass"/"running"/"fail"/"skip", NOT "passed"/
// "failed": a near-miss synonym here renders every green gate as pending.
func GateGlyph(state string) (string, lipgloss.Style) {
	switch state {
	case "pass":
		return "✓", gatePassStyle
	case "running":
		return "●", gateRunningStyle
	case "fail":
		return "✗", gateFailStyle
	case "skip":
		return "⊘", gateSkipStyle
	default:
		return "○", gatePendingStyle
	}
}

// GateChips renders the gate battery as one compact line — "build ✓ · test ● 4s · lint ○" — shared
// by the agent strip so gates are visible without opening anything.
func GateChips(gates []api.GateDto, maxWidth int) string {
	if len(gates) == 0 {
		return dimStyle.Render("no gates")
	}
	var parts []string
	for _, g := range gates {
		glyph, gs := gateGlyph(g.State)
		chip := gs.Render(g.Name + " " + strings.TrimSpace(glyph))
		if g.State == "running" && g.ElapsedSec > 0 {
			chip += dimStyle.Render(fmt.Sprintf(" %.0fs", g.ElapsedSec))
		}
		parts = append(parts, chip)
	}
	line := strings.Join(parts, dimStyle.Render(" · "))
	return lipgloss.NewStyle().MaxWidth(maxWidth).Render(line)
}
