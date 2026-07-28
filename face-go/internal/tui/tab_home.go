package tui

// The Home tab is the landing page (U1.1) and the tab the Face opens on. It answers, without a
// keypress and before a token is spent: where am I (Server), what is running (Run), which folder
// does it edit (Workspace), what do I do next (Next steps). Every value is read from the /state
// and /plan the Face already polls — Home fetches nothing of its own and owns no keys.

import (
	"fmt"
	"strings"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

// homeLabelW is the gutter every panel's labels share, so values line up down the whole page.
const homeLabelW = 11

// homeTier ranks a row by how badly the landing page needs it when the window is short.
//
// Home owns no keys by design (STYLE.md), so it cannot scroll — fitting is not optional. Its body is
// a fixed 28 rows, which overflowed the pane at 100x30 (by 4) and 80x24 (by 10), and what the clamp
// silently ate was the BOTTOM: Next steps, the one section that tells a newcomer what to press. A
// landing page that sheds diagnostics is strictly better than one that loses its own answer.
type homeTier int

const (
	homeEssential homeTier = iota // the landing's answer: what is running, where, what to do next
	homeUseful                    // the numbers you came to read
	homeDetail                    // diagnostics — first to go
)

// homeLine is one rendered row plus how droppable it is.
type homeLine struct {
	text string
	tier homeTier
}

// homeRow renders one "label   value" line. The label is padded as PLAIN text and styled after —
// padding an already-styled string pads its escape bytes and misaligns the column (STYLE.md).
func homeRow(label, value string) string {
	return subtleStyle.Render(fmt.Sprintf("%-*s", homeLabelW, label)) + value
}

// hRow / hLine build a homeLine; the tier-less forms are essential, since anything droppable should
// have to say so explicitly.
func hRow(label, value string, tier homeTier) homeLine {
	return homeLine{text: homeRow(label, value), tier: tier}
}

func hLine(text string, tier homeTier) homeLine { return homeLine{text: text, tier: tier} }

// homeSection is the plain untiered form, shared with the Dev tab (which has its own scroll and so
// never needs to shed).
func homeSection(title string, rows ...string) string {
	return strings.Join(append([]string{accentStyle.Render(title)}, rows...), "\n")
}

// homePanel is Home's tiered form: a header (never dropped while it still has rows) plus its rows.
func homePanel(title string, rows ...homeLine) []homeLine {
	return append([]homeLine{hLine(accentStyle.Render(title), homeEssential)}, rows...)
}

// homeHeight is what the assembled page will actually measure: every surviving row, plus one blank
// between sections that survive.
func homeHeight(sections [][]homeLine) int {
	rows, shown := 0, 0
	for _, sec := range sections {
		if len(sec) <= 1 {
			continue // header with nothing under it — dropped, so it costs nothing
		}
		rows += len(sec)
		shown++
	}
	if shown > 1 {
		rows += shown - 1
	}
	return rows
}

// dropOneHomeLine removes a single row of the given tier, scanning from the LAST section backwards,
// and reports whether it found one. Bottom-up because the page is ordered by importance already:
// Workspace's state dir goes before Run's cost, and Next steps (all essential) never goes at all.
func dropOneHomeLine(sections [][]homeLine, tier homeTier) bool {
	for i := len(sections) - 1; i >= 0; i-- {
		for j := len(sections[i]) - 1; j >= 0; j-- {
			if sections[i][j].tier == tier {
				sections[i] = append(sections[i][:j], sections[i][j+1:]...)
				return true
			}
		}
	}
	return false
}

// fitHome sheds the least-important rows until the page fits its budget, one ROW at a time rather
// than a whole tier at once — dropping every "useful" row the moment the page is one line over would
// trade a clipped footer for a screen of dead space.
func fitHome(sections [][]homeLine, budget int) string {
	for _, tier := range []homeTier{homeDetail, homeUseful} {
		for homeHeight(sections) > budget && dropOneHomeLine(sections, tier) {
		}
		if homeHeight(sections) <= budget {
			break
		}
	}

	var out []string
	for _, sec := range sections {
		// A header with every row under it dropped is an orphan — worse than the rows it lost.
		if len(sec) <= 1 {
			continue
		}
		lines := make([]string, 0, len(sec))
		for _, l := range sec {
			lines = append(lines, l.text)
		}
		out = append(out, strings.Join(lines, "\n"))
	}
	return strings.Join(out, "\n\n")
}

func (m Model) renderHomePane() (string, string) {
	w := m.paneCols()
	body := fitHome([][]homeLine{
		m.renderHomeServer(w),
		m.renderHomeRun(w),
		m.renderHomeWorkspace(w),
		m.renderHomeNextSteps(),
	}, m.paneRows())
	// Hard-clip every row to the pane. .Width() only pads short content — it never truncates — so one
	// over-long row would WRAP and push every panel below it down and out of the pane (STYLE.md).
	body = lipgloss.NewStyle().MaxWidth(w).Render(body)
	return body, "a agent · r report · : commands · ? keys"
}

// --- Server ------------------------------------------------------------------

func (m Model) renderHomeServer(w int) []homeLine {
	c := m.data.Connection

	var mode string
	switch {
	case c.Mode == api.ModeDemo:
		mode = tealStyle.Render("demo") + subtleStyle.Render("  synthetic data · no engine · no spend")
	case c.Connected:
		mode = safeStyle.Render("live")
	default:
		mode = destructStyle.Render("live — not connected")
	}
	rows := []homeLine{hRow("mode", mode, homeEssential)}

	if c.Mode == api.ModeLive {
		rows = append(rows,
			hRow("url", tealStyle.Render(c.URL), homeUseful),
			hRow("streams", homeStream("events", c.EventsConnected)+"   "+homeStream("transcript", c.TranscriptConnected), homeDetail))
		if c.LastError != nil && *c.LastError != "" {
			// An error is never detail: it is the reason the page looks wrong.
			rows = append(rows, hRow("last error", destructStyle.Render(truncate(*c.LastError, max(10, w-homeLabelW))), homeEssential))
		}
		if !c.Connected {
			// U1.1: a disconnected Home IS the landing page, so the old splash's how-to-start folds
			// in here rather than living on a separate screen nobody lands on. It is the whole point
			// of the page in that state, so it never sheds.
			rows = append(rows,
				hLine("", homeEssential),
				hLine(subtleStyle.Render("No run attached. Start one:"), homeEssential),
				hLine("  "+textStyle.Render("conductor run -p plans/<your>.plan.json"), homeEssential),
				hLine("  "+subtleStyle.Render("conductor journey -p plans/<your>.plan.json")+subtleStyle.Render("  (map it first)"), homeUseful),
				hLine("", homeDetail),
				hLine(subtleStyle.Render("This Face finds .conductor/control-plane.json and attaches."), homeDetail),
				hLine(subtleStyle.Render("Or explore offline:  conductor-face --demo"), homeUseful))
		}
	}
	return homePanel("Server", rows...)
}

func homeStream(name string, connected bool) string {
	dot := destructStyle.Render("●")
	if connected {
		dot = safeStyle.Render("●")
	}
	return dot + " " + subtleStyle.Render(name)
}

// --- Run ---------------------------------------------------------------------

func (m Model) renderHomeRun(w int) []homeLine {
	s := m.data.Plan
	if s == nil {
		return homePanel("Run", hLine(subtleStyle.Render("no run detected"), homeEssential))
	}

	rows := []homeLine{
		hRow("plan", textStyle.Render(s.PlanName), homeUseful),
		hRow("status", widgets.StatusBadge(s.Status), homeEssential),
		hRow("stage", accentStyle.Render(s.StageId)+" "+subtleStyle.Render(truncate(s.StageTitle, max(8, w-homeLabelW-len(s.StageId)-1))), homeEssential),
	}
	if s.SessionNumber > 0 {
		seg := textStyle.Render(fmt.Sprintf("s%d %s", s.SessionNumber, s.SessionKind))
		if s.MaxAttempts > 0 { // "attempt 0/0" is pre-first-attempt noise, not information
			seg += subtleStyle.Render(fmt.Sprintf(" · attempt %d/%d", s.Attempt, s.MaxAttempts))
		}
		if s.Model != "" {
			seg += subtleStyle.Render(" · ") + tealStyle.Render(shortModel(s.Model))
		}
		rows = append(rows, hRow("session", seg, homeUseful))
	}

	cost := peachStyle.Render(fmt.Sprintf("$%.2f", s.TotalCostUsd))
	if s.OverheadCostUsd > 0 {
		cost += subtleStyle.Render(fmt.Sprintf("   +$%.2f overhead", s.OverheadCostUsd))
	}
	rows = append(rows,
		hRow("progress", homeProgress(s), homeUseful),
		hRow("cost", cost, homeUseful),
		hRow("tokens", subtleStyle.Render(fmt.Sprintf("%s in · %s out · %s reasoning",
			widgets.FmtTokens(s.TokensInput), widgets.FmtTokens(s.TokensOutput), widgets.FmtTokens(s.TokensReasoning))), homeDetail))

	// Budget rows appear only when the plan actually caps the run — an uncapped run must not be
	// dressed up with a fake ceiling.
	rows = append(rows, m.homeBudgets(s)...)
	return homePanel("Run", rows...)
}

func homeProgress(s *api.StateDto) string {
	const barW = 24
	filled := 0
	if s.TotalCount > 0 {
		filled = s.DoneCount * barW / s.TotalCount
	}
	filled = min(max(filled, 0), barW)
	bar := safeStyle.Render(strings.Repeat("▰", filled)) +
		lipgloss.NewStyle().Foreground(widgets.Surface()).Render(strings.Repeat("▱", barW-filled))
	return bar + " " + textStyle.Render(fmt.Sprintf("%d/%d", s.DoneCount, s.TotalCount)) +
		subtleStyle.Render(" checkpoints")
}

// homeBudgets renders the run's caps with remaining headroom, one row per cap that is actually set
// (limits.maxRunCostUsd / limits.maxRunTokens). No cap set = no row.
func (m Model) homeBudgets(s *api.StateDto) []homeLine {
	if m.plan == nil {
		return nil
	}
	var rows []homeLine
	if lim := m.plan.Limits.MaxRunCostUsd; lim != nil && *lim > 0 {
		rows = append(rows, hRow("budget",
			homeHeadroom(fmt.Sprintf("$%.2f / $%.2f", s.TotalCostUsd, *lim), s.TotalCostUsd / *lim), homeDetail))
	}
	if lim := m.plan.Limits.MaxRunTokens; lim != nil && *lim > 0 {
		used := s.TokensInput + s.TokensOutput + s.TokensReasoning
		rows = append(rows, hRow("tokens cap",
			homeHeadroom(fmt.Sprintf("%s / %s", widgets.FmtTokens(used), widgets.FmtTokens(*lim)),
				float64(used)/float64(*lim)), homeDetail))
	}
	return rows
}

// homeHeadroom states what is LEFT, not what is spent — the cap is only interesting as the distance
// to the run stopping. Colour tracks how close that is.
func homeHeadroom(text string, usedRatio float64) string {
	pct := int((1 - usedRatio) * 100)
	pct = min(max(pct, 0), 100)
	st := safeStyle
	switch {
	case usedRatio >= 0.9:
		st = destructStyle
	case usedRatio >= 0.7:
		st = warnStyle
	}
	return st.Render(text) + subtleStyle.Render(fmt.Sprintf("  %d%% headroom", pct))
}

// --- Workspace ---------------------------------------------------------------

func (m Model) renderHomeWorkspace(w int) []homeLine {
	dash := subtleStyle.Render("—")
	repo, tracker, stateDir, planFile := dash, dash, dash, dash

	if s := m.data.Plan; s != nil {
		if s.Repo != "" {
			repo = textStyle.Render(homePath(s.Repo, w))
		}
		if s.Tracker != "" {
			tracker = textStyle.Render(s.Tracker)
		}
		// Engine-served (PlanConfig.StateDir, rooted at Repo). Never joined from PlanDir here: a plan
		// file outside the repo root would make that a confident lie.
		if s.StateDir != "" {
			stateDir = subtleStyle.Render(homePath(s.StateDir, w))
		}
	}
	if m.plan != nil && m.plan.PlanFile != "" {
		planFile = subtleStyle.Render(homePath(m.plan.PlanFile, w))
	}

	// repo is the answer to "which folder does this edit" — the reason U1.2 put the section here at
	// all, and the one row that must survive a short window.
	return homePanel("Workspace",
		hLine(subtleStyle.Render("the working directory every session edits"), homeDetail),
		hRow("repo", repo, homeEssential),
		hRow("plan file", planFile, homeUseful),
		hRow("tracker", tracker, homeDetail),
		hRow("state dir", stateDir, homeDetail))
}

// homePath keeps a path's tail when it has to be shortened — the leading drive/prefix is the part
// you can afford to lose, the folder you are in is not.
func homePath(p string, w int) string {
	avail := w - homeLabelW
	r := []rune(p)
	if avail < 12 || len(r) <= avail {
		return p
	}
	return "…" + string(r[len(r)-avail+1:])
}

// --- Next steps --------------------------------------------------------------

// renderHomeNextSteps is the landing's point: every row is essential, so a short window sheds
// diagnostics above it rather than the answer itself.
func (m Model) renderHomeNextSteps() []homeLine {
	return homePanel("Next steps", m.homeHints()...)
}

// homeHints is contextual, not a fixed menu: it names what is worth pressing given the run's actual
// state, so the landing page reads as an answer rather than a legend.
func (m Model) homeHints() []homeLine {
	hint := func(k, text string) homeLine {
		return hLine("  "+key(k)+"  "+subtleStyle.Render(text), homeEssential)
	}
	s := m.data.Plan

	if s == nil {
		if m.data.Connection.Mode == api.ModeDemo {
			return []homeLine{
				hint("a", "watch the synthetic agent transcript"),
				hint("p", "explore the plan editor"),
				hint("?", "see every key"),
			}
		}
		return []homeLine{
			hLine("  "+subtleStyle.Render("no run detected — start one with ")+textStyle.Render("conductor run -p <plan>"), homeEssential),
			hLine("  "+subtleStyle.Render("map it first with ")+textStyle.Render("conductor journey -p <plan>"), homeUseful),
			hint("?", "see every key"),
		}
	}

	var out []homeLine
	if s.AgentActive {
		out = append(out, hint("a", "watch the live agent — it is working right now"))
	} else {
		out = append(out, hint("a", "open the agent transcript"))
	}
	if attentionReason(s.Status, s.AttentionReason) != "" {
		out = append(out, hLine("  "+key(":")+"  "+destructStyle.Render("needs a human — approve or inject from the palette"), homeEssential))
	} else if strings.Contains(strings.ToLower(s.Status), "pause") {
		out = append(out, hint(":", "the run is paused — resume it from the palette"))
	}
	out = append(out, hint("r", "read the run report"))
	if len(out) < 4 {
		out = append(out, hint("?", "see every key"))
	}
	return out
}
