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
	"conductor-face-go/internal/timefmt"
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

// homeSection is the plain untiered form, shared with the Report tab (which scrolls, and so never
// needs to shed).
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
		// Directly under the engine line, because it is the answer to the question that line raises:
		// the engine is not running — so what happened? (SF2.1)
		m.renderHomeLastRun(w),
		m.renderHomeRun(w),
		m.renderHomeWorkspace(w),
		m.renderHomeNextSteps(),
		// SF1.2's re-homed wiring diagnostics go last: see homeWiring for why position is what makes
		// them shed before anything Home already showed.
		m.homeWiring(w),
	}, m.paneRows())
	// Hard-clip every row to the pane. .Width() only pads short content — it never truncates — so one
	// over-long row would WRAP and push every panel below it down and out of the pane (STYLE.md).
	body = lipgloss.NewStyle().MaxWidth(w).Render(body)
	return body, "a agent · r report · : commands · ? keys"
}

// --- Server ------------------------------------------------------------------

// renderHomeServer answers "am I attached to anything, and since when" in ONE line (SF2.1).
//
// It used to answer it in three that could disagree: a mode row reading "live — not connected", a
// raw `connectex: …` dial error, and — regardless of whether a run was known — the splash's
// start-a-run instructions, which is how a screenshot came to show a COMPLETED run header above
// "No run attached. Start one:". The engine line below is now the only statement of the link, it
// carries its own age, and the instructions appear only when there is genuinely no run to show.
func (m Model) renderHomeServer(w int) []homeLine {
	c := m.data.Connection

	if c.Mode == api.ModeDemo {
		return homePanel("Server", hRow("engine",
			tealStyle.Render("demo")+subtleStyle.Render("  synthetic data · no engine · no spend"), homeEssential))
	}

	st := engineState(c)
	style := safeStyle
	if !c.Connected {
		style = destructStyle
	}
	line := style.Render(st.Headline) + subtleStyle.Render(" — "+st.Detail)
	if st.Age != "" {
		line += subtleStyle.Render(" · " + st.Age)
	}
	rows := []homeLine{
		hRow("engine", truncate(line, max(20, w-homeLabelW)), homeEssential),
		hRow("streams", homeStream("events", c.EventsConnected)+"   "+homeStream("transcript", c.TranscriptConnected), homeDetail),
	}

	if !c.Connected && !m.knowsARun() {
		// U1.1: a disconnected Home IS the landing page, so the old splash's how-to-start folds in
		// here rather than living on a separate screen nobody lands on. SF2.1 narrowed WHEN: only
		// when neither a last-known state nor a run summary exists, because telling someone to start
		// a run while showing them the run they just watched is the same lie in two panels.
		rows = append(rows,
			hLine("", homeEssential),
			hLine(subtleStyle.Render("No run attached. Start one:"), homeEssential),
			hLine("  "+textStyle.Render("conductor run -p plans/<your>.plan.json"), homeEssential),
			hLine("  "+subtleStyle.Render("conductor journey -p plans/<your>.plan.json")+subtleStyle.Render("  (map it first)"), homeUseful),
			hLine("", homeDetail),
			hLine(subtleStyle.Render("This Face finds .conductor/control-plane.json and attaches."), homeDetail),
			hLine(subtleStyle.Render("Or explore offline:  conductor-face --demo"), homeUseful))
	}
	return homePanel("Server", rows...)
}

// knowsARun reports whether the Face has anything to show about a run at all — a state snapshot it
// once polled, or a summary the engine left on disk. It is the single test behind "should the
// landing page be telling this person how to start one".
func (m Model) knowsARun() bool {
	return m.data.Plan != nil || m.lastRun != nil
}

func homeStream(name string, connected bool) string {
	dot := destructStyle.Render("●")
	if connected {
		dot = safeStyle.Render("●")
	}
	return dot + " " + subtleStyle.Render(name)
}

// --- Last run (SF2.1) ---------------------------------------------------------

// renderHomeLastRun is what Home can still say once the control plane is gone. Everything else on
// this page is polled, so a dead engine used to erase the run entirely; the engine writes
// RUN-SUMMARY.md from run.db at completion precisely so the facts outlive it, and this reads it.
//
// It renders ONLY while disconnected: with a live engine, /state is fresher than any file, and two
// panels answering "what is this run" is the failure this checkpoint exists to fix.
func (m Model) renderHomeLastRun(w int) []homeLine {
	s := m.lastRun
	if s == nil || m.data.Connection.Connected || m.data.Connection.Mode != api.ModeLive {
		return homePanel("Last run")
	}

	outcome := widgets.StatusBadge(s.Outcome)
	if age := timefmt.Age(s.EndedUtc); age != "" {
		outcome += subtleStyle.Render("  ended " + age)
	}
	rows := []homeLine{hRow("outcome", outcome, homeEssential)}
	if s.Plan != "" {
		rows = append(rows, hRow("plan", textStyle.Render(s.Plan), homeUseful))
	}
	if s.Checkpoints != "" {
		rows = append(rows, hRow("progress", textStyle.Render(s.Checkpoints), homeEssential))
	}
	if s.Sessions != "" {
		rows = append(rows, hRow("sessions", subtleStyle.Render(s.Sessions), homeDetail))
	}
	if s.Spend != "" {
		rows = append(rows, hRow("spend", peachStyle.Render(truncate(s.Spend, max(12, w-homeLabelW))), homeUseful))
	}
	// Name the source: a card that survives the engine has to say why it is allowed to.
	rows = append(rows, hRow("from", subtleStyle.Render(m.homeRelPath(s.Path, w)), homeDetail))
	return homePanel("Last run", rows...)
}

// --- wiring (re-homed from the deleted Dev tab, SF1.2) -------------------------

// homeWiringLabels is every label the Wiring panel puts in the shared gutter. homeRow pads a label to
// homeLabelW with NO separator, so a label of exactly that width butts straight against its value
// ("write tokenpresent" — the Dev pane's first cut did precisely that). Listing them here is what
// makes the rule testable: TestHomeWiringLabelsFitTheGutter checks the whole set, so the next row
// added can't reintroduce it. `mode`, `url` and `streams` are the Server panel's own labels, which
// this section extends rather than repeats.
var homeWiringLabels = []string{"token", "seq", "poll", "run id", "last error"}

// homeWiring answers "is the Face actually wired to anything, and how" — the question the deleted Dev
// tab's internals pane existed for (U2.3). SF1.2 re-homed it here rather than deleting it with the SQL
// console: Home already states mode, url, streams, last error and state dir, so only the rows Home was
// missing moved.
//
// It is a section of its own, LAST on the page, for a mechanical reason: Home cannot scroll, and
// dropOneHomeLine sheds from the last section backwards. Folded into Server (the first section) these
// diagnostics would have pushed Workspace's `tracker` and `state dir` off a 120x30 window — a
// re-homing that quietly cost two other rows. Last means they are the first thing to go, which is
// exactly what they are worth. Next steps sits above and is all homeEssential, so it never sheds
// wherever it sits.
//
// Every value is read from live client state. Nothing here is a constant dressed up as a reading,
// except the poll cadences, which ARE the code's constants (messages.go) and say so.
func (m Model) homeWiring(w int) []homeLine {
	// Demo mode has no engine to be wired to: mode already says "synthetic data · no engine".
	if m.data.Connection.Mode != api.ModeLive {
		return homePanel("Wiring")
	}
	// Token presence, never the token itself: Home is the page a developer screenshots into a bug
	// report. An ABSENT token is not detail — it is the single most common reason a Face's writes are
	// silently refused — so that row is ranked to survive one tier longer than the rest.
	tok, tokTier := safeStyle.Render("present"), homeDetail
	if !m.source.HasWriteToken() {
		tok, tokTier = destructStyle.Render("absent — writes will be refused"), homeUseful
	}
	rows := []homeLine{
		hRow("token", tok, tokTier),
		hRow("seq", subtleStyle.Render(fmt.Sprintf("events %d · transcript %d · console %d",
			m.data.LastEventSeq, m.data.LastTxSeq, m.consoleSeq)), homeDetail),
		hRow("poll", subtleStyle.Render("state 1s · spinner 120ms · toast anim 33ms"), homeDetail),
	}
	if s := m.data.Plan; s != nil && s.RunId != "" {
		rows = append(rows, hRow("run id", textStyle.Render(s.RunId), homeDetail))
	}
	// The RAW transport error, verbatim, where a developer screenshotting Home into a bug report can
	// still find it. SF2.1 took it off the engine line — `connectex: No connection could be made
	// because the target machine actively refused it.` is not a sentence a user can act on — but
	// deleting it would trade one kind of dishonesty for a Face that hides what it knows.
	if e := m.data.Connection.LastError; e != nil && *e != "" {
		rows = append(rows, hRow("last error",
			destructStyle.Render(truncate(firstLine(*e), max(10, w-homeLabelW))), homeDetail))
	}
	return homePanel("Wiring", rows...)
}

// --- Run ---------------------------------------------------------------------

func (m Model) renderHomeRun(w int) []homeLine {
	s := m.data.Plan
	if s == nil {
		return homePanel("Run", hLine(subtleStyle.Render("no run detected"), homeEssential))
	}

	rows := []homeLine{
		hRow("plan", textStyle.Render(s.PlanName), homeUseful),
		hRow("status", m.homeRunStatus(s), homeEssential),
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

// homeRunStatus badges the run's status and, when the engine has stopped answering, says out loud
// that the badge is a memory. Everything in this panel was polled; without the qualifier a run that
// died mid-stage keeps rendering "RUNNING" forever, which is the same class of lie as a connection
// line that says "live" while nothing is listening.
func (m Model) homeRunStatus(s *api.StateDto) string {
	badge := widgets.StatusBadge(s.Status)
	c := m.data.Connection
	if c.Mode != api.ModeLive || c.Connected {
		return badge
	}
	stale := "as last seen"
	if age := timefmt.Age(c.LastContactAt); age != "" {
		stale = "as last seen " + age
	}
	return badge + subtleStyle.Render("  "+stale)
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
			tracker = textStyle.Render(m.homeRelPath(s.Tracker, w))
		}
		// Engine-served (PlanConfig.StateDir, rooted at Repo). Never joined from PlanDir here: a plan
		// file outside the repo root would make that a confident lie.
		if s.StateDir != "" {
			stateDir = subtleStyle.Render(m.homeRelPath(s.StateDir, w))
		}
	}
	if m.plan != nil && m.plan.PlanFile != "" {
		planFile = subtleStyle.Render(m.homeRelPath(m.plan.PlanFile, w))
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
// you can afford to lose, the folder you are in is not. Every path Home prints goes through here, so
// the normalisation below applies to all of them and cannot be forgotten by a new row.
func homePath(p string, w int) string {
	p = normPath(p)
	avail := w - homeLabelW
	r := []rune(p)
	if avail < 12 || len(r) <= avail {
		return p
	}
	return "…" + string(r[len(r)-avail+1:])
}

// normPath renders a path ONE way: forward slashes (the separator the engine's own JSON already
// uses), an upper-case drive letter, no trailing slash. Home showed `C:/code/…` beside `C:\Code\…`
// for the same machine because the two strings came from different writers — the plan file as the
// owner typed it, the repo as the engine resolved it — and nothing normalised either.
func normPath(p string) string {
	p = strings.TrimSpace(strings.ReplaceAll(p, "\\", "/"))
	if len(p) >= 2 && p[1] == ':' {
		p = strings.ToUpper(p[:1]) + p[1:]
	}
	if len(p) > 1 {
		p = strings.TrimSuffix(p, "/")
	}
	return p
}

// homeRelPath renders a path INSIDE the repo relative to it. Normalising separators fixes half the
// mix; the other half is folder-name casing (`C:/code` vs `C:\Code`), which no string rule can
// resolve because both spellings open the same directory on Windows. Dropping the shared prefix
// removes the disagreement instead of picking a winner — and the repo row directly above already
// states the root, so ".conductor" is more readable than a second copy of the absolute path.
func (m Model) homeRelPath(p string, w int) string {
	p = normPath(p)
	repo := ""
	if s := m.data.Plan; s != nil {
		repo = normPath(s.Repo)
	}
	if repo != "" && len(p) > len(repo) && strings.EqualFold(p[:len(repo)], repo) && p[len(repo)] == '/' {
		return p[len(repo)+1:]
	}
	return homePath(p, w)
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
