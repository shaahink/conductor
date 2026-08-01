package tui

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
	"conductor-face-go/internal/widgets"
)

// The History tab is the run's past, in two views of one chronology (SF1.3, docs/dev/adr/0004): the
// SESSIONS list — every session with its outcome and result summary — and the SPINE, the timeline of
// sessions, gates, stalls and verdicts as they happened. They were two tabs asking one question,
// because a session IS a timeline span: the spine's `session` entries carry the very SessionNumber
// the sessions list renders. `←/→` switches views (planTab's idiom), `s` and `t` jump straight to one.

// handleHistoryKey routes to the active view, after taking the two keys the tab itself owns.
func (m Model) handleHistoryKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "left", "right":
		return m.openHistory(1 - m.historyView) // two views: the other one
	}
	if m.historyView == historyTimeline {
		return m.handleTimelineKey(key)
	}
	return m.handleSessionsKey(key)
}

// renderHistoryPane draws the view switcher, then the active view under it.
func (m Model) renderHistoryPane() (body, help string) {
	if m.historyView == historyTimeline {
		body, help = m.renderTimelineView()
	} else {
		body, help = m.renderSessionsView()
	}
	return m.historySwitcher() + "\n" + body, help + " · ←/→ view"
}

// historySwitcher names both views and marks the active one. Without it the second view is folklore:
// a merged tab that shows one of its halves and nothing else is just the other half, deleted.
func (m Model) historySwitcher() string {
	cell := func(v historyView, k, name string) string {
		if m.historyView == v {
			return accentStyle.Render("● "+name) + subtleStyle.Render(" "+k)
		}
		return subtleStyle.Render("○ " + name + " " + k)
	}
	return cell(historySessions, "s", "Sessions") + subtleStyle.Render("   ") + cell(historyTimeline, "t", "Spine")
}

func (m Model) handleTimelineKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "r":
		m.timelineLoading, m.timelineErr = true, ""
		return m, m.cmdFetchTimeline()
	case "up", "k":
		if m.timelineSelected > 0 {
			m.timelineSelected--
		}
	case "down", "j":
		if m.timelineSelected < len(m.timelineEntries)-1 {
			m.timelineSelected++
		}
	}
	return m, nil
}

// renderTimelineView shows the run's spine — sessions, gates, stalls, verdicts, cost over time.
// It refreshes itself whenever a new engine event lands (see Update), so it is live while open.
func (m Model) renderTimelineView() (string, string) {
	if m.timelineLoading && len(m.timelineEntries) == 0 {
		return subtleStyle.Render("loading…"), "r refresh"
	}
	if m.timelineErr != "" {
		return destructStyle.Render("error: " + m.timelineErr), "r retry"
	}
	if len(m.timelineEntries) == 0 {
		return subtleStyle.Render("(no events on the run's spine yet)"), "r refresh"
	}
	// Reserve the bottom of the pane for the selected entry's detail (full description + meta).
	// historyRows, not paneRows: the view switcher above costs a row in every History view.
	detail := m.timelineDetail()
	window := m.historyRows() - lipgloss.Height(detail) - 1
	if window < 3 {
		window = 3
	}
	start := 0
	if m.timelineSelected >= window {
		start = m.timelineSelected - window + 1
	}
	end := start + window
	if end > len(m.timelineEntries) {
		end = len(m.timelineEntries)
	}
	// The rule costs a row inside the window, so the window holds one fewer event. Take it from the
	// OLDEST end: this pane is a live tail, and dropping the newest event to make room for the line
	// that says "the live ones start here" would defeat the point of drawing it.
	if b := m.timelineLiveBoundary(); b > start && b < end && start+1 < end {
		start++
	}
	var lines []string
	for i := start; i < end; i++ {
		if i == m.timelineLiveBoundary() {
			lines = append(lines, m.timelineLiveRule())
		}
		e := m.timelineEntries[i]
		glyph, gs := timelineGlyph(e)
		clock := timelineClock(e.Utc)
		desc := truncate(e.Description, m.paneCols()-16)
		cost := ""
		if e.CostUsd != nil && *e.CostUsd > 0 {
			cost = peachStyle.Render(fmt.Sprintf("  $%.2f", *e.CostUsd))
		}
		line := fmt.Sprintf("%s %s %s%s", subtleStyle.Render(clock), gs.Render(glyph), textStyle.Render(desc), cost)
		if i == m.timelineSelected {
			line = highlightBg.Render(fmt.Sprintf("%s %s %s", clock, glyph, desc))
		}
		lines = append(lines, line)
	}
	// Just the count. The "· live" this used to carry now lives on the rule, where it marks WHERE
	// live begins instead of restating it under a pane that already said it.
	count := subtleStyle.Render(fmt.Sprintf("%d events", len(m.timelineEntries)))
	return strings.Join(lines, "\n") + "\n" + count + "\n" + detail, "↑↓ navigate · r refresh"
}

// timelineLiveBoundary is the index of the first event that arrived AFTER this Face attached, or -1
// when there is no line worth drawing: nothing fetched yet, an attach to a run with no history (the
// whole pane is live — a rule at the top would say nothing), or nothing live since.
func (m Model) timelineLiveBoundary() int {
	if !m.timelineHistorySet || m.timelineHistoryCount <= 0 {
		return -1
	}
	if len(m.timelineEntries) <= m.timelineHistoryCount {
		return -1
	}
	return m.timelineHistoryCount
}

// timelineLiveRule separates replayed history from the live tail. Without it, attaching to a run
// pours its whole spine in at once and reads as an event storm you have just missed (dogfood
// appendix item 6) — the rule is what makes the top half legible as "before you got here".
func (m Model) timelineLiveRule() string {
	const label = " live "
	w := max(1, m.paneCols())
	side := (w - len(label)) / 2
	if side < 1 {
		return accentStyle.Render(strings.TrimSpace(label))
	}
	dash := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(strings.Repeat("─", side))
	return dash + accentStyle.Render(label) + dash
}

// timelineDetail renders the selected entry in full — the row truncates the description, so drilling
// in shows the whole thing plus stage/session/outcome/cost/time that don't fit on one row.
func (m Model) timelineDetail() string {
	if m.timelineSelected >= len(m.timelineEntries) {
		return ""
	}
	e := m.timelineEntries[m.timelineSelected]
	glyph, gs := timelineGlyph(e)
	rule := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(strings.Repeat("─", max(1, m.paneCols())))

	var meta []string
	if e.StageId != nil && *e.StageId != "" {
		meta = append(meta, "stage "+*e.StageId)
	}
	if e.SessionNumber != nil {
		meta = append(meta, fmt.Sprintf("session #%d", *e.SessionNumber))
	}
	if e.Outcome != nil && *e.Outcome != "" {
		meta = append(meta, "outcome "+*e.Outcome)
	}
	if e.CostUsd != nil && *e.CostUsd > 0 {
		meta = append(meta, fmt.Sprintf("$%.2f", *e.CostUsd))
	}
	meta = append(meta, timelineStamp(e.Utc))

	title := gs.Render(glyph+" ") + accentStyle.Render(e.Kind)
	desc := lipgloss.NewStyle().MaxWidth(m.paneCols()).Render(textStyle.Render(e.Description))
	return rule + "\n" + title + "\n" + desc + "\n" + subtleStyle.Render(strings.Join(meta, " · "))
}

func timelineGlyph(e api.TimelineEntryDto) (string, lipgloss.Style) {
	switch e.Kind {
	case "session":
		return "▸", accentStyle
	case "gate":
		if e.Outcome != nil && *e.Outcome == "fail" {
			return "✗", destructStyle
		}
		return "✓", safeStyle
	case "stage":
		return "◆", accentStyle
	case "attention":
		return "⚠", warnStyle
	default:
		return "·", subtleStyle
	}
}

// timelineClock is the spine's per-row clock: seconds included, because two gate events one second
// apart are a different story from two a minute apart, and this is the pane where that ordering is
// the whole point. It is LOCAL time — see timelineStamp for what the detail says about it.
func timelineClock(utc string) string {
	t, ok := timefmt.Parse(utc)
	if !ok {
		return "--:--:--"
	}
	return t.In(timefmt.Location).Format("15:04:05")
}

// timelineStamp is the detail line's timestamp. The row above it renders a bare clock, so an event
// from yesterday used to be indistinguishable from one an hour ago — this one carries the date when
// there is one to carry, plus the age.
//
// It also ends a two-line lie: the detail used to print `timelineClock(e.Utc) + " UTC"` over a clock
// that timelineClock had already converted into local time, so every timestamp in the pane was
// labelled with a timezone it was not in. The label is gone rather than corrected, because the honest
// version ("14:32 local") is noise on every row of a face whose clocks are all local by policy.
func timelineStamp(utc string) string {
	t, ok := timefmt.Parse(utc)
	if !ok {
		return "--:--"
	}
	return timefmt.StampAge(t)
}

// --- sessions view (the folded Sessions tab, SF1.3) -----------------------------

// sessionOutcomeStyle colours a session outcome against the engine's real outcome vocabulary
// (RunLoop verdicts). Normalised so "NeedsHuman", "needs-human", "needshuman" all match — the old
// map missed AgentError/TimedOut/NeedsHuman/RolledOver, so an error session rendered plain grey.
func sessionOutcomeStyle(outcome string) lipgloss.Style {
	norm := strings.Map(func(r rune) rune {
		if r >= 'a' && r <= 'z' {
			return r
		}
		if r >= 'A' && r <= 'Z' {
			return r + 32
		}
		return -1 // drop digits, spaces, dashes
	}, outcome)
	switch norm {
	case "advanced", "progress", "completed", "confirmed", "done":
		return safeStyle // green — real forward motion
	case "needshuman":
		return peachStyle // attention — the run parked for a human
	case "limitbackoff", "rolledover", "backoff", "ratelimited":
		return warnStyle // transient — no attempt burned, the loop retries itself
	case "gatesred", "stalled", "timedout", "agenterror", "authfailed", "noprogress", "needsretry", "failed", "error", "interrupted":
		return destructStyle // red — a failure the loop had to react to
	default:
		return subtleStyle
	}
}

// sessionWhen is a session's span in one sentence — "14:32 · 2h ago → 15:18 (46m 12s)" for a finished
// one, "14:32 · 2h ago → still running" for the live one. The end clock is bare on purpose: it is
// read against the start clock three words to its left, and repeating the date there would be noise
// on every row except the rare session that crosses midnight.
func (m Model) sessionWhen(s api.SessionRowDto) string {
	start, ok := timefmt.Parse(s.StartedUtc)
	if !ok {
		return ""
	}
	when := timefmt.StampAge(start)
	if s.EndedUtc == nil {
		return when + " → still running"
	}
	end, ok := timefmt.Parse(*s.EndedUtc)
	if !ok {
		return when
	}
	when += " → " + timefmt.Clock(end)
	if d, ok := m.sessionDuration(s); ok {
		when += "  (" + timefmt.Duration(d) + ")"
	}
	return when
}

func (m Model) handleSessionsKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k":
		if m.sessionSelected > 0 {
			m.sessionSelected--
		}
	case "down", "j":
		if m.sessionSelected < len(m.data.Sessions)-1 {
			m.sessionSelected++
		}
	}
	return m, nil
}

// renderSessionsView lists sessions newest-first (the wire order) with the selected one's detail
// inline underneath — history and drill-down on one page.
func (m Model) renderSessionsView() (string, string) {
	if len(m.data.Sessions) == 0 {
		return subtleStyle.Render("(no sessions yet — they appear as the engine runs)"), ""
	}
	var lines []string
	for i, s := range m.data.Sessions {
		outcome, oStyle := "running", warnStyle
		if s.Outcome != nil {
			outcome = *s.Outcome
			oStyle = sessionOutcomeStyle(outcome)
		}
		// Pad each cell as plain text first, then colour — ANSI-safe alignment (STYLE.md).
		num := fmt.Sprintf("#%-3d", s.Number)
		stage := fmt.Sprintf("%-6s", s.StageId)
		kind := fmt.Sprintf("%-8s", s.Kind)
		out := fmt.Sprintf("%-12s", outcome)
		commits := fmt.Sprintf("%-10s", fmt.Sprintf("%d commits", s.CommitCount))
		// SF2.2: WHEN each session ran, which this list never said. A history spine you cannot date is
		// a list of anonymous rows — and started_utc has been on the wire the whole time.
		when := ""
		if t, ok := timefmt.Parse(s.StartedUtc); ok {
			when = timefmt.Stamp(t)
		}
		if i == m.sessionSelected {
			lines = append(lines, highlightBg.Render("› "+num+" "+stage+" "+kind+" "+out+" "+commits+" "+when))
			continue
		}
		lines = append(lines, "  "+subtleStyle.Render(num)+" "+accentStyle.Render(stage)+" "+
			textStyle.Render(kind)+" "+oStyle.Render(out)+" "+subtleStyle.Render(commits)+" "+
			subtleStyle.Render(when))
	}

	if m.sessionSelected < len(m.data.Sessions) {
		s := m.data.Sessions[m.sessionSelected]
		detail := fmt.Sprintf("\n%s #%d · %s %s · %s",
			accentStyle.Render("Session"), s.Number, accentStyle.Render(s.StageId), textStyle.Render(s.Kind),
			subtleStyle.Render(fmt.Sprintf("attempt %d, %d resumes", s.Attempt, s.ResumeCount)))
		if when := m.sessionWhen(s); when != "" {
			detail += "\n" + subtleStyle.Render("Ran:   ") + textStyle.Render(when)
		}
		if s.GateSummary != nil {
			detail += "\n" + subtleStyle.Render("Gates: ") + textStyle.Render(*s.GateSummary)
		}
		// SF3.1: what the session DID, above what it SAID. The result summary below is the agent's own
		// account of the session; this is the engine's, computed from the tool events it captured — so
		// it is the half a reader can check the other half against.
		if dg := renderSessionDigest(s.Digest, m.paneCols()); len(dg) > 0 {
			detail += "\n" + strings.Join(dg, "\n")
		}
		// SF3.3: what it LANDED, between what it did and what it said. The row above this detail has
		// carried a commit COUNT since the first version of this list, and a count is the one fact
		// about a session's commits that cannot be checked — "2 commits" is true of a session that
		// fixed the bug and of one that rewrote a comment twice.
		if cm := renderSessionCommits(s, m.paneCols()); len(cm) > 0 {
			detail += "\n" + strings.Join(cm, "\n")
		}
		if s.ResultSummary != nil {
			detail += "\n" + subtleStyle.Render("Result:") + "\n" + indent(renderMarkdown(*s.ResultSummary, m.paneCols()-4), "  ")
		}
		lines = append(lines, detail)
	}
	return strings.Join(lines, "\n"), "↑↓ navigate"
}

// How many commit subjects the detail shows before it says how many it is holding back. Sessions in
// this repo land one to three commits; a session that landed twelve is a story the reader can go read
// in git, and the overflow line is what tells them there is one.
const historyCommitsMax = 5

// renderSessionCommits is the session's own commits as `<short sha> <subject>` lines, ready to join
// with "\n". It takes the "Result:" shape above it — a header with the body indented under it —
// rather than the digest's seven-wide label column, because a commit line is a sha plus a conventional
// -commit subject and the label column would spend a fifth of a narrow pane on a word the header
// already said.
//
// The empty case is deliberately two cases. A session that landed nothing renders NOTHING. A session
// whose CommitCount says it landed something while the subjects are missing is an engine older than
// SF3.3 (the subjects are read from an event the engine only started writing then) — and rendering
// nothing there would silently contradict the "2 commits" on the row three lines up.
func renderSessionCommits(s api.SessionRowDto, w int) []string {
	body := max(4, w-2) // the two-space indent under the header
	if len(s.Commits) == 0 {
		if s.CommitCount <= 0 {
			return nil
		}
		return []string{
			subtleStyle.Render("Commits:"),
			"  " + subtleStyle.Render(truncate(plural(s.CommitCount, "commit")+
				" — subjects not recorded for this session", body)),
		}
	}
	shown := s.Commits
	if len(shown) > historyCommitsMax {
		shown = shown[:historyCommitsMax]
	}
	lines := make([]string, 0, len(shown)+2)
	lines = append(lines, subtleStyle.Render("Commits:"))
	for _, c := range shown {
		lines = append(lines, "  "+textStyle.Render(truncate(c, body)))
	}
	// The overflow counts against the COUNT, not against the list that arrived: the engine caps how
	// many subjects it serves, so a session with twenty commits and four subjects on the wire must
	// still say "+16 more" rather than pretending the four are all of them.
	if rest := max(s.CommitCount, len(s.Commits)) - len(shown); rest > 0 {
		lines = append(lines, "  "+subtleStyle.Render(fmt.Sprintf("+%d more", rest)))
	}
	return lines
}
