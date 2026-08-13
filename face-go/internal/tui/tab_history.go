package tui

import (
	"fmt"
	"strings"

	"charm.land/bubbles/v2/viewport"
	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
	"conductor-face-go/internal/widgets"
)

// historyModel is the History tab's own state (K6.3): which of its two views is up, the sessions
// list's cursor, and the spine it fetches. SF1.3 merged Sessions and Timeline into one surface —
// this struct is that merge finally expressed in the state, instead of two field blocks four
// hundred lines apart on the root struct.
type historyModel struct {
	view            historyView
	sessionSelected int
	// sessionsVp / spineVp are the two views' pane viewports (KS2.7). Neither view had one: the
	// sessions list emitted EVERY row plus the selected detail with no window at all — overflow was
	// eaten silently by frameContent's MaxHeight — and the spine hand-rolled a start/end slice around
	// its cursor. `sessionSelected` and `selected` stay: they are SELECTION cursors, not scroll
	// offsets (they address a row of data and survive a resize), and the viewport follows them
	// through ensurePaneRow rather than replacing them.
	sessionsVp viewport.Model
	spineVp    viewport.Model

	entries  []api.TimelineEntryDto
	selected int
	loading  bool
	err      string
	// attachCount is how many events already existed when this Face attached; everything at or past
	// that index arrived live, and the pane rules a "live" line there (dogfood appendix 6). attachSet
	// distinguishes "attached to a run with zero events" from "never fetched" — a plain 0 cannot.
	attachCount int
	attachSet   bool
}

// updateHistory handles the spine fetch. The sessions poll stays in the shell: data.Sessions has a
// second reader (the Report tab's per-session table), and shared state is the shell's to land.
func (m Model) updateHistory(msg tea.Msg) (Model, tea.Cmd, bool) {
	switch msg := msg.(type) {

	case MsgTimelineUpdated:
		m.history.loading = false
		if msg.Err != "" {
			m.history.err = msg.Err
		} else if msg.Timeline != nil {
			m.history.entries = msg.Timeline.Entries
			m.history.err = ""
			// The FIRST fetch is the attach: everything in it already happened, and everything after
			// it is happening now. Recording where that line falls (once) is what lets the pane draw
			// it — see dogfood appendix item 6, where an attach poured history in and read as an
			// event storm. The timeline refetches wholesale on every spine event, so this cannot be
			// inferred later.
			if !m.history.attachSet {
				m.history.attachSet = true
				m.history.attachCount = len(m.history.entries)
			}
			if m.history.selected >= len(m.history.entries) {
				m.history.selected = max(0, len(m.history.entries)-1)
			}
		}
		return m, nil, true
	}
	return m, nil, false
}

// The History tab is the run's past, in two views of one chronology (SF1.3, docs/dev/adr/0004): the
// SESSIONS list — every session with its outcome and result summary — and the SPINE, the timeline of
// sessions, gates, stalls and verdicts as they happened. They were two tabs asking one question,
// because a session IS a timeline span: the spine's `session` entries carry the very SessionNumber
// the sessions list renders. `←/→` switches views (planTab's idiom), `s` and `t` jump straight to one.

// handleHistoryKey routes to the active view, after taking the two keys the tab itself owns.
func (m Model) handleHistoryKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "left", "right":
		return m.openHistory(1 - m.history.view) // two views: the other one
	}
	if m.history.view == historyTimeline {
		return m.handleTimelineKey(key)
	}
	return m.handleSessionsKey(key)
}

// renderHistoryPane draws the view switcher, then the active view under it.
func (m Model) renderHistoryPane() (body, help string) {
	if m.history.view == historyTimeline {
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
		if m.history.view == v {
			return accentStyle.Render("● "+name) + subtleStyle.Render(" "+k)
		}
		return subtleStyle.Render("○ " + name + " " + k)
	}
	return cell(historySessions, "s", "Sessions") + subtleStyle.Render("   ") + cell(historyTimeline, "t", "Spine")
}

// handleTimelineKey: the spine's own semantic keys first (`r` refresh, ↑↓ move the SELECTION), then
// the one pane-scroll set against a viewport that has just been sized and loaded (adr/0006 §1).
// ↑↓ stay on the selection because the detail pane under the list renders the selected entry — this
// is lazygit's split, where item movement and content scrolling are different key sets.
func (m Model) handleTimelineKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "r":
		m.history.loading, m.history.err = true, ""
		return m, m.cmdFetchTimeline()
	case readerOpenKey:
		// KS2.8: the row truncates the description and the detail pane width-clips it; the reader is
		// where a long gate line or a stall reason is read whole.
		title, body := m.spineReaderDoc()
		return m.openReader(title, body, false), nil
	case "up", "k":
		if m.history.selected > 0 {
			m.history.selected--
			m.history.spineVp = m.followSpineSelection()
			return m, nil
		}
	case "down", "j":
		if m.history.selected < len(m.history.entries)-1 {
			m.history.selected++
			m.history.spineVp = m.followSpineSelection()
			return m, nil
		}
	}
	m.history.spineVp = m.historySpineViewport()
	applyPaneScroll(&m.history.spineVp, key)
	return m, nil
}

// spineLines is the whole spine as rendered rows, with the live rule as a row of its own, plus the
// index of the SELECTED row inside that slice. Built here rather than inside the renderer so the key
// handler can load the same bytes into the viewport it is about to scroll.
//
// The live rule is content now, not a window adjustment: it sits at the boundary index and scrolls
// with the entries either side of it, which is what "keep the rule inside the window" has to mean
// once the window is a viewport (dogfood appendix item 6 — an attach pours the whole spine in and
// reads as an event storm without it).
func (m Model) spineLines() (lines []string, selRow int) {
	boundary := m.timelineLiveBoundary()
	for i := range m.history.entries {
		if i == boundary {
			lines = append(lines, m.timelineLiveRule())
		}
		if i == m.history.selected {
			selRow = len(lines)
		}
		e := m.history.entries[i]
		glyph, gs := timelineGlyph(e)
		clock := timelineClock(e.Utc)
		desc := truncate(e.Description, m.paneCols()-16)
		cost := ""
		if e.CostUsd != nil && *e.CostUsd > 0 {
			cost = peachStyle.Render(fmt.Sprintf("  $%.2f", *e.CostUsd))
		}
		line := fmt.Sprintf("%s %s %s%s", subtleStyle.Render(clock), gs.Render(glyph), textStyle.Render(desc), cost)
		if i == m.history.selected {
			line = highlightBg.Render(fmt.Sprintf("%s %s %s", clock, glyph, desc))
		}
		lines = append(lines, line)
	}
	return lines, selRow
}

// historySpineViewport is the spine's `<surface>Viewport()` builder. The count row and the selected
// entry's detail render BELOW it and are subtracted from its height — a view that sized itself
// against the whole pane would lose the detail it exists to show to the frame's height clamp.
func (m Model) historySpineViewport() viewport.Model {
	lines, _ := m.spineLines()
	// historyRows, not paneRows: the view switcher above costs a row in every History view, and it
	// has already been deducted there — do not double-deduct.
	rows := m.historyRows() - lipgloss.Height(m.timelineDetail()) - 1
	return loadPaneViewport(m.history.spineVp, lines, m.paneCols(), max(3, rows), false)
}

// followSpineSelection is the builder plus the cursor follow, called ONLY from the arms that moved
// the cursor (see ensurePaneRow for why it may not live in the builder).
func (m Model) followSpineSelection() viewport.Model {
	_, selRow := m.spineLines()
	vp := m.historySpineViewport()
	ensurePaneRow(&vp, selRow)
	return vp
}

// renderTimelineView shows the run's spine — sessions, gates, stalls, verdicts, cost over time.
// It refreshes itself whenever a new engine event lands (see Update), so it is live while open.
func (m Model) renderTimelineView() (string, string) {
	if m.history.loading && len(m.history.entries) == 0 {
		return subtleStyle.Render("loading…"), "r refresh"
	}
	if m.history.err != "" {
		return destructStyle.Render("error: " + m.history.err), "r retry"
	}
	if len(m.history.entries) == 0 {
		return subtleStyle.Render("(no events on the run's spine yet)"), "r refresh"
	}
	// The bottom of the pane is the selected entry's detail (full description + meta); the viewport
	// above it holds the whole spine.
	vp := m.historySpineViewport()
	// Just the count. The "· live" this used to carry now lives on the rule, where it marks WHERE
	// live begins instead of restating it under a pane that already said it.
	count := subtleStyle.Render(fmt.Sprintf("%d events", len(m.history.entries)))
	help := "↑↓ select · z read · r refresh"
	if hint := paneScrollHint(vp, false); hint != "" {
		help = "↑↓ select · " + hint + " · z read · r refresh"
	}
	return vp.View() + "\n" + count + "\n" + m.timelineDetail(), help
}

// spineReaderDoc is the selected spine entry as one plain document for the reader: the full
// description — the wire's one long-text field here — over the same meta the detail line carries.
func (m Model) spineReaderDoc() (title, body string) {
	if m.history.selected >= len(m.history.entries) {
		return "", ""
	}
	e := m.history.entries[m.history.selected]
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
	return "spine · " + e.Kind, e.Description + "\n\n" + strings.Join(meta, " · ") + "\n"
}

// timelineLiveBoundary is the index of the first event that arrived AFTER this Face attached, or -1
// when there is no line worth drawing: nothing fetched yet, an attach to a run with no history (the
// whole pane is live — a rule at the top would say nothing), or nothing live since.
func (m Model) timelineLiveBoundary() int {
	if !m.history.attachSet || m.history.attachCount <= 0 {
		return -1
	}
	if len(m.history.entries) <= m.history.attachCount {
		return -1
	}
	return m.history.attachCount
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
	if m.history.selected >= len(m.history.entries) {
		return ""
	}
	e := m.history.entries[m.history.selected]
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

// handleSessionsKey: ↑↓ move the SELECTION (the detail under the list is the selected session), then
// the one pane-scroll set — the same split the spine uses, applied after this view's own keys.
func (m Model) handleSessionsKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case readerOpenKey:
		// KS2.8: the session's two long-text cells — the gate summary and the agent's own result
		// summary (markdown) — read whole, soft-wrapped, in the theme's markdown.
		title, body := m.sessionReaderDoc()
		return m.openReader(title, body, true), nil
	case "up", "k":
		if m.history.sessionSelected > 0 {
			m.history.sessionSelected--
			m.history.sessionsVp = m.followSessionSelection()
			return m, nil
		}
	case "down", "j":
		if m.history.sessionSelected < len(m.data.Sessions)-1 {
			m.history.sessionSelected++
			m.history.sessionsVp = m.followSessionSelection()
			return m, nil
		}
	}
	m.history.sessionsVp = m.historySessionsViewport()
	applyPaneScroll(&m.history.sessionsVp, key)
	return m, nil
}

// historySessionsViewport is the sessions list's `<surface>Viewport()` builder. Before KS2.7 this
// view emitted every row AND the selected session's whole detail — result summary, gate summary,
// commits, digest — with no window at all, so on a run of thirty sessions everything past the pane's
// last row was eaten silently by frameContent's MaxHeight. There was no scroll bug to fix here
// because there was no scroll.
func (m Model) historySessionsViewport() viewport.Model {
	return loadPaneViewport(m.history.sessionsVp, m.sessionsLines(), m.paneCols(), m.historyRows(), false)
}

// followSessionSelection is the builder plus the cursor follow, called ONLY from the arms that moved
// the cursor. Row index == session index: one row per session, and the selected session's detail
// hangs below all of them.
func (m Model) followSessionSelection() viewport.Model {
	vp := m.historySessionsViewport()
	ensurePaneRow(&vp, min(m.history.sessionSelected, max(0, len(m.data.Sessions)-1)))
	return vp
}

// renderSessionsView lists sessions newest-first (the wire order) with the selected one's detail
// inline underneath — history and drill-down on one page.
func (m Model) renderSessionsView() (string, string) {
	if len(m.data.Sessions) == 0 {
		return subtleStyle.Render("(no sessions yet — they appear as the engine runs)"), ""
	}
	vp := m.historySessionsViewport()
	help := "↑↓ select · z read"
	if hint := paneScrollHint(vp, false); hint != "" {
		help = "↑↓ select · " + hint + " · z read"
	}
	return vp.View(), help
}

// sessionReaderDoc is the selected session as one MARKDOWN document for the reader. Markdown
// because its longest cell — the result summary — is markdown the agent wrote (the pane already
// renders it through renderMarkdown); the gate summary and commits ride along as sections, so every
// long cell this row owns is one `z` away.
func (m Model) sessionReaderDoc() (title, body string) {
	if m.history.sessionSelected >= len(m.data.Sessions) {
		return "", ""
	}
	s := m.data.Sessions[m.history.sessionSelected]
	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("# Session #%d — %s %s\n\n", s.Number, s.StageId, s.Kind))
	sb.WriteString(fmt.Sprintf("attempt %d, %d resumes", s.Attempt, s.ResumeCount))
	if when := m.sessionWhen(s); when != "" {
		sb.WriteString(" · " + when)
	}
	sb.WriteString("\n")
	if s.GateSummary != nil && strings.TrimSpace(*s.GateSummary) != "" {
		sb.WriteString("\n## Gates\n\n" + *s.GateSummary + "\n")
	}
	if len(s.Commits) > 0 {
		sb.WriteString("\n## Commits\n\n")
		for _, c := range s.Commits {
			sb.WriteString("- " + c + "\n")
		}
	}
	if s.ResultSummary != nil && strings.TrimSpace(*s.ResultSummary) != "" {
		sb.WriteString("\n## Result\n\n" + *s.ResultSummary + "\n")
	}
	return fmt.Sprintf("session #%d · %s %s", s.Number, s.StageId, s.Kind), sb.String()
}

// sessionsLines is every session row followed by the selected session's detail, ready for the
// viewport. Built here rather than inside the renderer so the key handler can load the same bytes
// into the viewport it is about to scroll.
func (m Model) sessionsLines() []string {
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
		if i == m.history.sessionSelected {
			lines = append(lines, highlightBg.Render("› "+num+" "+stage+" "+kind+" "+out+" "+commits+" "+when))
			continue
		}
		lines = append(lines, "  "+subtleStyle.Render(num)+" "+accentStyle.Render(stage)+" "+
			textStyle.Render(kind)+" "+oStyle.Render(out)+" "+subtleStyle.Render(commits)+" "+
			subtleStyle.Render(when))
	}

	if m.history.sessionSelected < len(m.data.Sessions) {
		s := m.data.Sessions[m.history.sessionSelected]
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
		// One entry per RENDERED row: the viewport counts lines, and a multi-line string smuggled in
		// as one entry would make every row index after it wrong (SetContentLines splits it anyway,
		// but the selection arithmetic above would still be counting the wrong thing).
		lines = append(lines, strings.Split(detail, "\n")...)
	}
	return lines
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
