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

// homeRow renders one "label   value" line. The label is padded as PLAIN text and styled after —
// padding an already-styled string pads its escape bytes and misaligns the column (STYLE.md).
func homeRow(label, value string) string {
	return subtleStyle.Render(fmt.Sprintf("%-*s", homeLabelW, label)) + value
}

func homeSection(title string, rows ...string) string {
	return strings.Join(append([]string{accentStyle.Render(title)}, rows...), "\n")
}

func (m Model) renderHomePane() (string, string) {
	w := m.paneCols()
	body := strings.Join([]string{
		m.renderHomeServer(w),
		m.renderHomeRun(w),
		m.renderHomeWorkspace(w),
		m.renderHomeNextSteps(),
	}, "\n\n")
	// Hard-clip every row to the pane. .Width() only pads short content — it never truncates — so one
	// over-long row would WRAP and push every panel below it down and out of the pane (STYLE.md).
	body = lipgloss.NewStyle().MaxWidth(w).Render(body)
	return body, "a agent · r report · : commands · ? keys"
}

// --- Server ------------------------------------------------------------------

func (m Model) renderHomeServer(w int) string {
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
	rows := []string{homeRow("mode", mode)}

	if c.Mode == api.ModeLive {
		rows = append(rows,
			homeRow("url", tealStyle.Render(c.URL)),
			homeRow("streams", homeStream("events", c.EventsConnected)+"   "+homeStream("transcript", c.TranscriptConnected)))
		if c.LastError != nil && *c.LastError != "" {
			rows = append(rows, homeRow("last error", destructStyle.Render(truncate(*c.LastError, max(10, w-homeLabelW)))))
		}
		if !c.Connected {
			// U1.1: a disconnected Home IS the landing page, so the old splash's how-to-start folds
			// in here rather than living on a separate screen nobody lands on.
			rows = append(rows, "",
				subtleStyle.Render("No run attached. Start one:"),
				"  "+textStyle.Render("conductor run -p plans/<your>.plan.json"),
				"  "+subtleStyle.Render("conductor journey -p plans/<your>.plan.json")+subtleStyle.Render("  (map it first)"),
				"",
				subtleStyle.Render("This Face finds .conductor/control-plane.json and attaches."),
				subtleStyle.Render("Or explore offline:  conductor-face --demo"))
		}
	}
	return homeSection("Server", rows...)
}

func homeStream(name string, connected bool) string {
	dot := destructStyle.Render("●")
	if connected {
		dot = safeStyle.Render("●")
	}
	return dot + " " + subtleStyle.Render(name)
}

// --- Run ---------------------------------------------------------------------

func (m Model) renderHomeRun(w int) string {
	s := m.data.Plan
	if s == nil {
		return homeSection("Run", subtleStyle.Render("no run detected"))
	}

	rows := []string{
		homeRow("plan", textStyle.Render(s.PlanName)),
		homeRow("status", widgets.StatusBadge(s.Status)),
		homeRow("stage", accentStyle.Render(s.StageId)+" "+subtleStyle.Render(truncate(s.StageTitle, max(8, w-homeLabelW-len(s.StageId)-1)))),
	}
	if s.SessionNumber > 0 {
		seg := textStyle.Render(fmt.Sprintf("s%d %s", s.SessionNumber, s.SessionKind))
		if s.MaxAttempts > 0 { // "attempt 0/0" is pre-first-attempt noise, not information
			seg += subtleStyle.Render(fmt.Sprintf(" · attempt %d/%d", s.Attempt, s.MaxAttempts))
		}
		if s.Model != "" {
			seg += subtleStyle.Render(" · ") + tealStyle.Render(shortModel(s.Model))
		}
		rows = append(rows, homeRow("session", seg))
	}

	cost := peachStyle.Render(fmt.Sprintf("$%.2f", s.TotalCostUsd))
	if s.OverheadCostUsd > 0 {
		cost += subtleStyle.Render(fmt.Sprintf("   +$%.2f overhead", s.OverheadCostUsd))
	}
	rows = append(rows,
		homeRow("progress", homeProgress(s)),
		homeRow("cost", cost),
		homeRow("tokens", subtleStyle.Render(fmt.Sprintf("%s in · %s out · %s reasoning",
			widgets.FmtTokens(s.TokensInput), widgets.FmtTokens(s.TokensOutput), widgets.FmtTokens(s.TokensReasoning)))))

	// Budget rows appear only when the plan actually caps the run — an uncapped run must not be
	// dressed up with a fake ceiling.
	for _, b := range m.homeBudgets(s) {
		rows = append(rows, b)
	}
	return homeSection("Run", rows...)
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
func (m Model) homeBudgets(s *api.StateDto) []string {
	if m.plan == nil {
		return nil
	}
	var rows []string
	if lim := m.plan.Limits.MaxRunCostUsd; lim != nil && *lim > 0 {
		rows = append(rows, homeRow("budget",
			homeHeadroom(fmt.Sprintf("$%.2f / $%.2f", s.TotalCostUsd, *lim), s.TotalCostUsd / *lim)))
	}
	if lim := m.plan.Limits.MaxRunTokens; lim != nil && *lim > 0 {
		used := s.TokensInput + s.TokensOutput + s.TokensReasoning
		rows = append(rows, homeRow("tokens cap",
			homeHeadroom(fmt.Sprintf("%s / %s", widgets.FmtTokens(used), widgets.FmtTokens(*lim)),
				float64(used)/float64(*lim))))
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

func (m Model) renderHomeWorkspace(w int) string {
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

	return homeSection("Workspace",
		subtleStyle.Render("the working directory every session edits"),
		homeRow("repo", repo),
		homeRow("plan file", planFile),
		homeRow("tracker", tracker),
		homeRow("state dir", stateDir))
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

func (m Model) renderHomeNextSteps() string {
	return homeSection("Next steps", m.homeHints()...)
}

// homeHints is contextual, not a fixed menu: it names what is worth pressing given the run's actual
// state, so the landing page reads as an answer rather than a legend.
func (m Model) homeHints() []string {
	hint := func(k, text string) string { return "  " + key(k) + "  " + subtleStyle.Render(text) }
	s := m.data.Plan

	if s == nil {
		if m.data.Connection.Mode == api.ModeDemo {
			return []string{
				hint("a", "watch the synthetic agent transcript"),
				hint("p", "explore the plan editor"),
				hint("?", "see every key"),
			}
		}
		return []string{
			"  " + subtleStyle.Render("no run detected — start one with ") + textStyle.Render("conductor run -p <plan>"),
			"  " + subtleStyle.Render("map it first with ") + textStyle.Render("conductor journey -p <plan>"),
			hint("?", "see every key"),
		}
	}

	var out []string
	if s.AgentActive {
		out = append(out, hint("a", "watch the live agent — it is working right now"))
	} else {
		out = append(out, hint("a", "open the agent transcript"))
	}
	if attentionReason(s.Status, s.AttentionReason) != "" {
		out = append(out, "  "+key(":")+"  "+destructStyle.Render("needs a human — approve or inject from the palette"))
	} else if strings.Contains(strings.ToLower(s.Status), "pause") {
		out = append(out, hint(":", "the run is paused — resume it from the palette"))
	}
	out = append(out, hint("r", "read the run report"))
	if len(out) < 4 {
		out = append(out, hint("?", "see every key"))
	}
	return out
}
