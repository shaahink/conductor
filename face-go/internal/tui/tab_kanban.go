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
	"conductor-face-go/internal/widgets"
)

// kanbanColumns is the board order; skipped cards live in the Done column, dimmed.
var kanbanColumns = [3]string{"todo", "in_progress", "done"}
var kanbanTitles = [3]string{"TODO", "In Progress", "Done"}

// kanbanColumn maps a task status to its board column index.
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

// kanbanCards returns the board's cards column-major (all TODO, then In Progress, then Done) —
// the ↑↓ walk order. Within a column the wire order (checkpoint → order) is kept.
func (m Model) kanbanCards() []api.TaskDto {
	var out []api.TaskDto
	for col := range kanbanColumns {
		for _, t := range m.data.Tasks {
			if kanbanColumn(t.Status) == col {
				out = append(out, t)
			}
		}
	}
	return out
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
// else the run's current checkpoint's stage, else the plan's first stage. Stage ids are the prefix
// of a checkpoint id — the same convention the engine, the tracker and the graph all read.
func (m Model) kanbanAddStageId() string {
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

	colW := max(16, (m.paneCols()-4)/3)
	cols := make([]string, 3)
	for c := range kanbanColumns {
		cols[c] = m.renderKanbanColumn(c, colW, selId)
	}
	board := lipgloss.JoinHorizontal(lipgloss.Top, cols[0], "  ", cols[1], "  ", cols[2])

	body := board
	// A dead feed over a board that still has rows: say so, or it just looks like nothing is
	// happening. The banner goes on top — this is the first thing to know about what is below it.
	if m.tasksErr != "" {
		body = m.kanbanFeedBanner() + "\n\n" + board
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

func (m Model) renderKanbanColumn(col, width int, selId string) string {
	count := 0
	var rows []string
	for _, t := range m.data.Tasks {
		if kanbanColumn(t.Status) != col {
			continue
		}
		count++
		rows = append(rows, m.renderKanbanCard(t, width, t.TaskId == selId))
	}

	header := fmt.Sprintf("%s (%d)", kanbanTitles[col], count)
	headStyle := subtleStyle
	switch col {
	case 1:
		headStyle = lipgloss.NewStyle().Foreground(widgets.Blue()).Bold(true)
	case 2:
		headStyle = lipgloss.NewStyle().Foreground(widgets.Green()).Bold(true)
	}
	lines := []string{headStyle.Render(truncate(header, width)), subtleStyle.Render(strings.Repeat("─", width))}
	if count == 0 {
		lines = append(lines, subtleStyle.Render("  —"))
	}
	lines = append(lines, rows...)
	return lipgloss.NewStyle().Width(width).Render(strings.Join(lines, "\n"))
}

// renderKanbanCard is one card: checkpoint id + title on the first line, a dim meta line under the
// selected card. Pad plain text first, then style — never %-Ns an ANSI-wrapped string (STYLE.md).
func (m Model) renderKanbanCard(t api.TaskDto, width int, selected bool) string {
	label := truncate(fmt.Sprintf("%s · %s", t.CheckpointId, t.Title), width-2)
	if t.Status == "skipped" {
		label = truncate(fmt.Sprintf("%s · %s (skipped)", t.CheckpointId, t.Title), width-2)
	}
	if selected {
		row := highlightBg.Render("▸ " + label)
		meta := subtleStyle.Render(truncate(fmt.Sprintf("  %s · %s · #%d", t.TaskId, t.Source, t.Order), width))
		return row + "\n" + meta
	}
	style := textStyle
	switch t.Status {
	case "done":
		style = safeStyle
	case "skipped":
		style = subtleStyle
	case "in_progress":
		style = lipgloss.NewStyle().Foreground(widgets.Blue())
	}
	return style.Render("• " + label)
}
