package tui

// G2.2: the Kanban tab (`b` for board) — a live view of the run's task graph, the same graph the
// engine's MCP task tools drive. Three columns (TODO · In Progress · Done) from GET /tasks; ↑↓ walk
// the cards across columns, ←→ move the selected card (POST /tasks/update), `n` adds a card under
// the selected checkpoint (POST /tasks/add). Every write re-fetches so the board shows what the
// engine actually recorded, and the 1s poll keeps it live while the agent works.

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
	"conductor-face-go/internal/widgets"
)

// kanbanColumns is the board order; skipped cards live in the Done column, on their own shelf.
var kanbanColumns = [3]string{"todo", "in_progress", "done"}
var kanbanTitles = [3]string{"TODO", "In Progress", "Done"}

// kanbanColumn maps a task status to its board column index. Three columns, deliberately: ←→ move a
// card one column over, and a fourth would change what those keys mean. `blocked` and `skipped` are
// therefore column-mates of todo and done — SF3.2's job is that they stop *rendering* like them.
func kanbanColumn(status string) int {
	switch status {
	case "in_progress":
		return 1
	case "done", "skipped":
		return 2
	default:
		return 0
	}
}

// kanbanStageOrder is the plan's own stage order, which is the only correct one: sorting stage ids
// as text puts SF10 before SF2. Stages the plan does not know about (a card whose stage was added
// mid-run, or an engine that serves a stage the /state fold has not caught up with) are appended in
// the order the wire first mentions them rather than dropped.
func (m Model) kanbanStageOrder(cards []api.TaskDto) []string {
	var order []string
	seen := map[string]bool{}
	if m.data.Plan != nil {
		for _, s := range m.data.Plan.Stages {
			if s.Id != "" && !seen[s.Id] {
				seen[s.Id] = true
				order = append(order, s.Id)
			}
		}
	}
	for _, t := range cards {
		if s := t.Stage(); s != "" && !seen[s] {
			seen[s] = true
			order = append(order, s)
		}
	}
	return order
}

// kanbanGroup is one stage's cards inside one column.
type kanbanGroup struct {
	Stage  string
	Active bool // the run's current stage — the "you are here" mark inside the board
	Cards  []api.TaskDto
}

// kanbanGroups buckets a column's cards by their OWNING stage (TaskDto.Stage(), i.e. the wire's
// stageId, with the dot-split only as a legacy fallback) and returns the buckets in plan order.
// Empty stages are not returned — a column full of headings with no cards under them is noise.
//
// The Done column excludes skipped cards: they get their own shelf, so "Done" counts work actually
// finished rather than work abandoned. That distinction is the whole point of the separation.
func (m Model) kanbanGroups(col int) []kanbanGroup {
	byStage := map[string][]api.TaskDto{}
	for _, t := range m.data.Tasks {
		if kanbanColumn(t.Status) != col || t.Status == "skipped" {
			continue
		}
		byStage[t.Stage()] = append(byStage[t.Stage()], t)
	}
	active := ""
	if m.data.Plan != nil {
		active = m.data.Plan.StageId
	}
	var out []kanbanGroup
	for _, id := range m.kanbanStageOrder(m.data.Tasks) {
		if cards := byStage[id]; len(cards) > 0 {
			out = append(out, kanbanGroup{Stage: id, Active: id != "" && id == active, Cards: cards})
		}
	}
	return out
}

// kanbanSkipped is the Done column's shelf: work deliberately not done, kept on the board (it is
// still part of the plan's story) but out of the Done count.
func (m Model) kanbanSkipped() []api.TaskDto {
	var out []api.TaskDto
	for _, t := range m.data.Tasks {
		if t.Status == "skipped" {
			out = append(out, t)
		}
	}
	return out
}

// kanbanCards returns the board's cards in RENDER order — column-major, and within a column grouped
// by stage exactly as renderKanbanColumn lays them out, with the skipped shelf last. This ordering
// is load-bearing: ↑↓ walk this slice, so if it disagrees with the screen the selection appears to
// jump around at random. Any change to the column layout has to be made here too.
func (m Model) kanbanCards() []api.TaskDto {
	var out []api.TaskDto
	for col := range kanbanColumns {
		for _, g := range m.kanbanGroups(col) {
			out = append(out, g.Cards...)
		}
	}
	return append(out, m.kanbanSkipped()...)
}

// kanbanSelected resolves the selected card's index in the walk order; selection is by task id so
// a card keeps focus while it changes columns or the poll refreshes the board underneath.
func (m Model) kanbanSelected(cards []api.TaskDto) int {
	for i, t := range cards {
		if t.TaskId == m.kanbanSelId {
			return i
		}
	}
	return 0
}

func (m *Model) handleKanbanKey(key string) (tea.Model, tea.Cmd) {
	if m.kanbanDetail {
		return m.handleKanbanDetailKey(key)
	}
	if m.kanbanAdding {
		return m.handleKanbanAddKey(key)
	}

	cards := m.kanbanCards()
	if len(cards) == 0 {
		if key == "N" {
			m.kanbanBeginAddStage()
			return m, nil
		}
		if key == "n" {
			m.kanbanBeginAdd()
		}
		return m, nil
	}
	sel := m.kanbanSelected(cards)

	switch key {
	case "up", "k":
		m.kanbanSelId = cards[max(0, sel-1)].TaskId
	case "down", "j":
		m.kanbanSelId = cards[min(len(cards)-1, sel+1)].TaskId
	case "left", "right":
		return m.kanbanMove(cards[sel], key == "right")
	case "N":
		m.kanbanBeginAddStage()
	case "n":
		m.kanbanBeginAdd()
	case "enter":
		// P3: open the card's detail — its prompt as labeled building blocks.
		m.kanbanSelId = cards[sel].TaskId
		return m, m.kanbanOpenDetail(cards[sel].TaskId)
	}
	return m, nil
}

// kanbanMove posts the selected card one column over. The server folds the event and answers with
// the card's actual status — an illegal move is a recorded no-op the re-fetch simply won't show.
func (m *Model) kanbanMove(card api.TaskDto, right bool) (tea.Model, tea.Cmd) {
	col := kanbanColumn(card.Status)
	target := col + 1
	if !right {
		target = col - 1
	}
	if target < 0 || target > 2 {
		return m, nil
	}
	status := kanbanColumns[target]
	m.kanbanStatus = fmt.Sprintf("moving %s → %s…", card.TaskId, status)
	return m, m.cmdPostTaskUpdate(api.TaskUpdateRequestDto{TaskId: card.TaskId, Status: status})
}

// kanbanBeginAdd opens the one-line title input. The new card lands under the selected card's
// checkpoint, or the run's current checkpoint when the board is empty.
func (m *Model) kanbanBeginAdd() {
	if m.kanbanAddCheckpoint() == "" {
		m.kanbanStatus = "✗ no checkpoint to add under — press N for a stage-level card"
		return
	}
	m.kanbanAdding = true
	m.kanbanAddStage = false
	m.kanbanAddBuf = ""
	m.kanbanStatus = ""
}

// kanbanBeginAddStage opens the same one-line input for a STAGE-level card (W4.3). The result is a
// checkpoint the engine will schedule — the answer to "we've realised there's another requirement"
// mid-run, which previously had nowhere to land because every add needed an existing parent.
func (m *Model) kanbanBeginAddStage() {
	if m.kanbanAddStageId() == "" {
		m.kanbanStatus = "✗ no stage to add to (no cards, no active checkpoint, no plan stages)"
		return
	}
	m.kanbanAdding = true
	m.kanbanAddStage = true
	m.kanbanAddBuf = ""
	m.kanbanStatus = ""
}

// kanbanAddStageId resolves the stage a new stage-level card belongs to: the selected card's stage,
// else the run's current checkpoint's stage, else the plan's first stage. The selected card answers
// off the wire's stageId (TaskDto.Stage()) since SF3.2 — splitting its checkpoint id on the dot was
// only ever a guess, and it guessed wrong for any plan whose ids do not encode their stage.
func (m Model) kanbanAddStageId() string {
	cards := m.kanbanCards()
	if len(cards) > 0 {
		if s := cards[m.kanbanSelected(cards)].Stage(); s != "" {
			return s
		}
	}
	if cp := m.kanbanAddCheckpoint(); cp != "" {
		if i := strings.Index(cp, "."); i > 0 {
			return cp[:i]
		}
		return cp
	}
	if m.data.Plan != nil && len(m.data.Plan.Stages) > 0 {
		return m.data.Plan.Stages[0].Id
	}
	return ""
}

func (m Model) kanbanAddCheckpoint() string {
	cards := m.kanbanCards()
	if len(cards) > 0 {
		return cards[m.kanbanSelected(cards)].CheckpointId
	}
	if m.data.Plan != nil {
		return m.data.Plan.CurrentCheckpoint
	}
	return ""
}

func (m *Model) handleKanbanAddKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.kanbanAdding = false
		return m, nil
	case "enter":
		title := strings.TrimSpace(m.kanbanAddBuf)
		if title == "" {
			return m, nil // a title is required — stay in the form
		}
		m.kanbanAdding = false
		m.kanbanStatus = "adding…"
		if m.kanbanAddStage {
			return m, m.cmdPostTaskAdd(api.TaskAddRequestDto{StageId: m.kanbanAddStageId(), Title: title})
		}
		return m, m.cmdPostTaskAdd(api.TaskAddRequestDto{CheckpointId: m.kanbanAddCheckpoint(), Title: title})
	case "backspace":
		if len(m.kanbanAddBuf) > 0 {
			m.kanbanAddBuf = m.kanbanAddBuf[:len(m.kanbanAddBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			m.kanbanAddBuf += ch
		}
	}
	return m, nil
}

// --- rendering ---

// kanbanFeedBanner names a broken /tasks feed. It rides ABOVE a populated board too, not just the
// empty one: cards are kept when a poll fails (blanking a board on one dropped request would be
// worse), which means a dead feed otherwise shows as a board that has simply stopped moving — the
// same silent lie as appendix item 5, just with rows on it.
func (m Model) kanbanFeedBanner() string {
	return destructStyle.Render("⚠ cannot reach /tasks: ") +
		textStyle.Render(truncate(m.tasksErr, max(20, m.paneCols()-24))) + "\n" +
		subtleStyle.Render("  showing the last cards fetched — not the live graph")
}

// renderKanbanEmptyState says WHY the board is empty. Dogfood appendix item 5: an empty board beside
// a sidebar full of plan is alarming, and "No tasks yet" was asserted in all three cases — never
// fetched, fetch failed, genuinely no cards. Only the last of those is good news, and it was the one
// the pane always claimed.
func (m Model) renderKanbanEmptyState() string {
	switch {
	case m.tasksErr != "":
		return m.kanbanFeedBanner()
	case !m.tasksLoaded:
		return subtleStyle.Render("Loading the task graph from /tasks…")
	case m.data.Connection.Mode == api.ModeLive && !m.data.Connection.Connected:
		return subtleStyle.Render("No cards, and nothing is attached — start a run, or explore with ") +
			key("--demo") + subtleStyle.Render(".")
	default:
		return subtleStyle.Render("No cards yet — the engine seeds one per checkpoint at run start, "+
			"and the agent files more via task_add. Press ") +
			key("n") + subtleStyle.Render(" to add one yourself.")
	}
}

func (m Model) renderKanbanPane() (string, string) {
	if m.kanbanDetail {
		return m.renderKanbanDetailPane()
	}
	cards := m.kanbanCards()
	if len(cards) == 0 && !m.kanbanAdding {
		return m.renderKanbanEmptyState() + m.kanbanStatusLine(), "n add · N stage card · esc back"
	}
	selId := ""
	if len(cards) > 0 {
		selId = cards[m.kanbanSelected(cards)].TaskId
	}

	// Row budget. Every line the pane spends above or below the board comes out of the columns'
	// share, or the frame's height clamp eats the bottom of the board silently (STYLE.md: prefer an
	// invariant over a pinned frame for anything the clamp can eat).
	ribbon := m.renderKanbanRibbon()
	rows := m.paneRows()
	if ribbon != "" {
		rows -= 2 // the ribbon and the blank line under it
	}
	if m.tasksErr != "" {
		rows -= 3 // two banner lines and a blank
	}
	if m.kanbanStatus != "" {
		rows -= 2
	}
	if m.kanbanAdding {
		rows -= 4
	}

	colW := max(16, (m.paneCols()-4)/3)
	cols := make([]string, 3)
	for c := range kanbanColumns {
		cols[c] = m.renderKanbanColumn(c, colW, max(3, rows), selId)
	}
	board := lipgloss.JoinHorizontal(lipgloss.Top, cols[0], "  ", cols[1], "  ", cols[2])

	body := board
	if ribbon != "" {
		body = ribbon + "\n\n" + board
	}
	// A dead feed over a board that still has rows: say so, or it just looks like nothing is
	// happening. The banner goes on top — this is the first thing to know about what is below it.
	if m.tasksErr != "" {
		body = m.kanbanFeedBanner() + "\n\n" + body
	}
	if m.kanbanAdding {
		body += "\n\n  " + accentStyle.Render("+ new card") + subtleStyle.Render(" under ") +
			accentStyle.Render(m.kanbanAddCheckpoint()) + "\n  " +
			subtleStyle.Render("title: ") + textStyle.Render(m.kanbanAddBuf) + accentStyle.Render("▏")
		return body + m.kanbanStatusLine(), "type · enter add · esc cancel"
	}
	return body + m.kanbanStatusLine(), "↑↓ card · ←→ move · enter detail · n add · N stage card · esc back"
}

func (m Model) kanbanStatusLine() string {
	if m.kanbanStatus == "" {
		return ""
	}
	st := safeStyle
	if strings.HasPrefix(m.kanbanStatus, "✗") {
		st = destructStyle
	}
	return "\n\n  " + st.Render(m.kanbanStatus)
}

// renderKanbanRibbon is the you-are-here line above the board: which stage of how many, how many
// checkpoints are behind you, what gate is next, and which session is doing it. The board answers
// "what is on the plan"; without this it never answered "and where are we in it" — you had to leave
// the tab to find out, which is exactly the question the board is opened to settle.
//
// Every number is READ, not re-derived: the stage index comes from where state.stageId sits in
// state.stages, the checkpoint counts are the engine's own doneCount/totalCount.
func (m Model) renderKanbanRibbon() string {
	s := m.data.Plan
	if s == nil {
		return ""
	}
	var parts []string
	if s.StageId != "" {
		at := ""
		for i, st := range s.Stages {
			if st.Id == s.StageId {
				at = fmt.Sprintf(" %d/%d", i+1, len(s.Stages))
				break
			}
		}
		parts = append(parts, accentStyle.Render("▸ "+s.StageId)+subtleStyle.Render(" stage"+at))
	}
	// "cp" and "s15", not "checkpoints" and "session #15": the status bar and the card meta already
	// speak that shorthand, and spelling it out here cost the ribbon its last clause at 110 columns —
	// the widest terminal most people have. A dropped fact is worse than a terse one.
	if s.TotalCount > 0 {
		parts = append(parts, subtleStyle.Render("cp ")+
			textStyle.Render(fmt.Sprintf("%d/%d", s.DoneCount, s.TotalCount)))
	}
	// The next gate is the one running, else the first still pending. A battery that has finished
	// has no "next" and says nothing rather than naming the last thing it did as if it were ahead.
	if g, ok := kanbanNextGate(s.Gates); ok {
		st := subtleStyle
		if g.State == "running" {
			st = lipgloss.NewStyle().Foreground(widgets.Blue())
		}
		parts = append(parts, subtleStyle.Render("gate ")+st.Render(g.Name+" "+g.State))
	}
	if s.SessionNumber > 0 {
		sess := fmt.Sprintf("s%d", s.SessionNumber)
		if s.SessionKind != "" {
			sess += " " + s.SessionKind
		}
		if s.Attempt > 1 || s.MaxAttempts > 1 {
			sess += fmt.Sprintf(" try %d/%d", max(1, s.Attempt), max(1, s.MaxAttempts))
		}
		parts = append(parts, textStyle.Render(sess))
	}
	if len(parts) == 0 {
		return ""
	}
	// Fit by dropping whole clauses from the right, least important first — same discipline as the
	// card meta, and for the same reason: "session #12 Deli" is not a shorter fact, it is a broken
	// one. MaxWidth stays as the backstop because it is the only ANSI-SAFE clip; truncate() rune-
	// slices, which cuts an escape sequence in half and printed "[38;2;108;112;13" onto this very
	// ribbon the first time it was rendered (STYLE.md: never byte- or rune-slice a styled string).
	sep := subtleStyle.Render(" · ")
	for len(parts) > 1 && lipgloss.Width(strings.Join(parts, sep)) > m.paneCols() {
		parts = parts[:len(parts)-1]
	}
	return lipgloss.NewStyle().MaxWidth(m.paneCols()).Render(strings.Join(parts, sep))
}

// kanbanNextGate picks the gate the ribbon names: the running one, else the first pending one.
func kanbanNextGate(gates []api.GateDto) (api.GateDto, bool) {
	for _, g := range gates {
		if g.State == "running" {
			return g, true
		}
	}
	for _, g := range gates {
		if g.State == "pending" {
			return g, true
		}
	}
	return api.GateDto{}, false
}

// renderKanbanColumn draws one column: a fixed header and rule, then a scrolling body of stage
// groups. `rows` is the column's whole height budget — the header, the rule and, when the body does
// not fit, the line that says how much is hidden all come out of it.
func (m Model) renderKanbanColumn(col, width, rows int, selId string) string {
	groups := m.kanbanGroups(col)
	count := 0
	for _, g := range groups {
		count += len(g.Cards)
	}
	skipped := []api.TaskDto{}
	if col == 2 {
		skipped = m.kanbanSkipped()
	}

	// n/total: how much of the whole board is sitting in this column. "Done (2)" never said whether
	// that was 2 of 3 or 2 of 300, and the board's job is to answer exactly that.
	total := len(m.data.Tasks)
	header := fmt.Sprintf("%s %d/%d", kanbanTitles[col], count, total)
	headStyle := subtleStyle
	switch col {
	case 1:
		headStyle = lipgloss.NewStyle().Foreground(widgets.Blue()).Bold(true)
	case 2:
		headStyle = lipgloss.NewStyle().Foreground(widgets.Green()).Bold(true)
	}

	// The body, and the line indices the selected card occupies, so the scroll window can keep it on
	// screen. Built plain-then-styled per line; nothing here is width-formatted after styling.
	var body []string
	selFrom, selTo := -1, -1
	for _, g := range groups {
		body = append(body, m.renderKanbanGroupHeader(g, width))
		for _, t := range g.Cards {
			if t.TaskId == selId {
				selFrom = len(body)
			}
			body = append(body, m.renderKanbanCard(t, width, t.TaskId == selId)...)
			if t.TaskId == selId {
				selTo = len(body) - 1
			}
		}
	}
	if count == 0 && len(skipped) == 0 {
		body = append(body, subtleStyle.Render("  —"))
	}
	if len(skipped) > 0 {
		body = append(body, subtleStyle.Render(truncate(fmt.Sprintf("skipped %d", len(skipped)), width)))
		for _, t := range skipped {
			if t.TaskId == selId {
				selFrom = len(body)
			}
			body = append(body, m.renderKanbanCard(t, width, t.TaskId == selId)...)
			if t.TaskId == selId {
				selTo = len(body) - 1
			}
		}
	}

	lines := []string{headStyle.Render(truncate(header, width)), subtleStyle.Render(strings.Repeat("─", width))}
	lines = append(lines, kanbanWindow(body, rows-2, selFrom, selTo, width)...)
	return lipgloss.NewStyle().Width(width).Render(strings.Join(lines, "\n"))
}

// kanbanWindow clips a column body to `budget` lines, keeping lines[selFrom..selTo] inside the
// window, and spends one of those lines saying what it hid. A column that simply stopped at the
// frame's height clamp was the worst of both: cards vanished with nothing to say they had, and the
// selection could walk off the bottom into rows that were not on screen.
func kanbanWindow(body []string, budget, selFrom, selTo, width int) []string {
	if budget < 1 {
		budget = 1
	}
	if len(body) <= budget {
		return body
	}
	avail := budget - 1 // one line for the hidden-count note
	if avail < 1 {
		avail = 1
	}
	start := 0
	if selTo >= avail {
		start = selTo - avail + 1
	}
	if selFrom >= 0 && start > selFrom {
		start = selFrom
	}
	if start > len(body)-avail {
		start = len(body) - avail
	}
	if start < 0 {
		start = 0
	}
	hiddenBelow := len(body) - start - avail
	note := ""
	switch {
	case start > 0 && hiddenBelow > 0:
		note = fmt.Sprintf("↕ %d above · %d below", start, hiddenBelow)
	case start > 0:
		note = fmt.Sprintf("↑ %d above", start)
	default:
		note = fmt.Sprintf("↓ %d below", hiddenBelow)
	}
	out := append([]string{}, body[start:start+avail]...)
	return append(out, subtleStyle.Render(truncate(note, width)))
}

// renderKanbanGroupHeader is a stage heading inside a column. The run's CURRENT stage is marked —
// this is the second half of "where are we": the ribbon says which stage, this says which of the
// cards in front of you belong to it.
func (m Model) renderKanbanGroupHeader(g kanbanGroup, width int) string {
	if g.Active {
		return lipgloss.NewStyle().Foreground(widgets.Accent()).Bold(true).
			Render(truncate("▸ "+g.Stage, width))
	}
	return subtleStyle.Render(truncate("  "+g.Stage, width))
}

// renderKanbanCard is one card, as TWO lines: checkpoint id + title, then a meta line — for every
// card, not just the selected one. What a card costs you to look at was previously "select it and
// see"; the whole point of the meta is answering "who is on this and since when" across the board at
// a glance. Pad plain text first, then style — never %-Ns an ANSI-wrapped string (STYLE.md).
func (m Model) renderKanbanCard(t api.TaskDto, width int, selected bool) []string {
	label := truncate(fmt.Sprintf("%s · %s", t.CheckpointId, t.Title), width-2)

	var row string
	if selected {
		row = highlightBg.Render("▸ " + label)
	} else {
		row = kanbanCardStyle(t.Status).Render("• " + label)
	}
	meta := subtleStyle.Render(truncate("  "+strings.Join(kanbanFitTokens(kanbanCardMeta(t), width-2), " · "), width))
	return []string{row, meta}
}

// kanbanStatusTag names a status the column heading cannot, because it shares a column with another
// one: `blocked` sits in TODO and `skipped` in Done. It leads the meta line rather than trailing the
// title, because the title is what truncation eats first — a "(blocked)" suffix on a 24-column card
// is invisible in exactly the case it matters, and that is how the first cut of this frame rendered.
func kanbanStatusTag(status string) string {
	switch status {
	case "blocked", "skipped":
		return status
	}
	return ""
}

func kanbanCardStyle(status string) lipgloss.Style {
	switch status {
	case "done":
		return safeStyle
	case "skipped":
		return subtleStyle
	case "blocked":
		return warnStyle
	case "in_progress":
		return lipgloss.NewStyle().Foreground(widgets.Blue())
	}
	return textStyle
}

// kanbanCardMeta is the card's meta tokens, MOST IMPORTANT FIRST — kanbanFitTokens drops from the
// right, so the order here is the order they survive a narrow column in.
//
// All three of the first tokens are read off the wire (sessionNumber, statusSinceUtc, attempts),
// folded by the engine's TaskGraph so every reader gets the same answer instead of the Face
// inventing one. `claimed` vs `confirmed` is the verdict engine's flag, and it is the difference
// between an agent saying it finished and the engine agreeing — a distinction the board could not
// draw at all before SF3.2.
func kanbanCardMeta(t api.TaskDto) []string {
	var toks []string
	if tag := kanbanStatusTag(t.Status); tag != "" {
		toks = append(toks, tag)
	}
	if t.SessionNumber > 0 {
		toks = append(toks, fmt.Sprintf("s%d", t.SessionNumber))
	}
	if since, ok := timefmt.Parse(t.StatusSinceUtc); ok {
		toks = append(toks, timefmt.Span(timefmt.Now().Sub(since)))
	}
	if t.Attempts > 1 {
		toks = append(toks, fmt.Sprintf("try %d", t.Attempts))
	}
	if t.Status == "done" {
		if t.Confirmed {
			toks = append(toks, "confirmed")
		} else {
			toks = append(toks, "claimed")
		}
	}
	if t.Source != "" {
		toks = append(toks, t.Source)
	}
	return toks
}

// kanbanFitTokens drops WHOLE tokens from the right until the joined line fits (STYLE.md: never clip
// a meta value mid-way — "s1" from "s14" is not a shorter truth, it is a different one).
func kanbanFitTokens(toks []string, width int) []string {
	for len(toks) > 0 {
		if lipgloss.Width(strings.Join(toks, " · ")) <= width {
			return toks
		}
		toks = toks[:len(toks)-1]
	}
	return nil
}
