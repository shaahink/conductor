package tui

// P3: the Kanban card detail — `enter` on a card opens its prompt as labeled building blocks
// (GET /prompt/blocks?task=), instead of the compiled wall of text. The task-scoped blocks are
// editable as STRUCTURED task data (`t` title, `c` extra context → POST /tasks/edit); `a` asks the
// plan's advisor for a refinement (a PROPOSAL — applied only when the owner confirms with enter);
// `h` hands the card to the next session by writing an injection (POST /inject) after a y/n confirm.

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

func (m *Model) kanbanOpenDetail(taskId string) tea.Cmd {
	m.kanbanDetail = true
	m.kanbanBlocks = nil
	m.kanbanBlocksErr = ""
	m.kanbanProposal = nil
	m.kanbanRefining = false
	m.kanbanHandConfirm = false
	m.kanbanEditingTitle = false
	m.kanbanEditingCtx = false
	m.kanbanEditingPaths = false
	m.kanbanStatus = ""
	return m.cmdFetchPromptBlocks(taskId)
}

func (m *Model) kanbanCloseDetail() {
	m.kanbanDetail = false
	m.kanbanBlocks = nil
	m.kanbanBlocksErr = ""
	m.kanbanProposal = nil
	m.kanbanRefining = false
	m.kanbanHandConfirm = false
	m.kanbanEditingTitle = false
	m.kanbanEditingCtx = false
	m.kanbanEditingPaths = false
}

// kanbanDetailTask resolves the open card from the board data (title/context may be fresher there
// than in the blocks snapshot after an edit round-trip).
func (m Model) kanbanDetailTask() *api.TaskDto {
	if m.kanbanBlocks == nil {
		return nil
	}
	for i := range m.data.Tasks {
		if m.data.Tasks[i].TaskId == m.kanbanBlocks.TaskId {
			return &m.data.Tasks[i]
		}
	}
	return nil
}

func (m Model) kanbanBlock(kind string) *api.PromptBlockDto {
	if m.kanbanBlocks == nil {
		return nil
	}
	for i := range m.kanbanBlocks.Blocks {
		if m.kanbanBlocks.Blocks[i].Kind == kind {
			return &m.kanbanBlocks.Blocks[i]
		}
	}
	return nil
}

func (m *Model) handleKanbanDetailKey(key string) (tea.Model, tea.Cmd) {
	// Modal-ish sub-states first: editors, proposal, hand-off confirm.
	if m.kanbanEditingTitle {
		return m.handleKanbanTitleKey(key)
	}
	if m.kanbanEditingCtx {
		return m.handleKanbanCtxKey(key)
	}
	if m.kanbanEditingPaths {
		return m.handleKanbanPathsKey(key)
	}
	if m.kanbanProposal != nil {
		return m.handleKanbanProposalKey(key)
	}
	if m.kanbanHandConfirm {
		return m.handleKanbanHandKey(key)
	}

	task := m.kanbanDetailTask()
	switch key {
	case "esc":
		m.kanbanCloseDetail()
		return m, nil
	case "t":
		if task != nil {
			m.kanbanEditingTitle = true
			m.kanbanTitleBuf = task.Title
			m.kanbanStatus = ""
		}
		return m, nil
	case "c":
		if task != nil {
			m.kanbanEditingCtx = true
			w := max(10, m.paneCols()-8)
			m.kanbanCtxEditor = widgets.NewTextArea(task.Context, w, max(3, min(8, m.paneRows()-6)))
			m.kanbanStatus = ""
		}
		return m, nil
	case "p":
		// PF3: edit the card's declared paths — one line, comma-separated; empty clears.
		if task != nil {
			m.kanbanEditingPaths = true
			m.kanbanPathsBuf = strings.Join(task.Paths, ", ")
			m.kanbanStatus = ""
		}
		return m, nil
	case "a":
		if task != nil && !m.kanbanRefining {
			m.kanbanRefining = true
			m.kanbanStatus = "asking the advisor…"
			return m, m.cmdPostTaskRefine(api.TaskRefineRequestDto{TaskId: task.TaskId})
		}
		return m, nil
	case "h":
		if task != nil {
			m.kanbanHandConfirm = true
			m.kanbanStatus = ""
		}
		return m, nil
	}
	return m, nil
}

func (m *Model) handleKanbanTitleKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.kanbanEditingTitle = false
		return m, nil
	case "enter":
		title := strings.TrimSpace(m.kanbanTitleBuf)
		if title == "" {
			return m, nil // a card must stay nameable — stay in the editor
		}
		m.kanbanEditingTitle = false
		m.kanbanStatus = "saving…"
		return m, m.cmdPostTaskEdit(api.TaskEditRequestDto{TaskId: m.kanbanBlocks.TaskId, Title: &title})
	case "backspace":
		if len(m.kanbanTitleBuf) > 0 {
			m.kanbanTitleBuf = m.kanbanTitleBuf[:len(m.kanbanTitleBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			m.kanbanTitleBuf += ch
		}
	}
	return m, nil
}

func (m *Model) handleKanbanCtxKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.kanbanEditingCtx = false
		return m, nil
	case "ctrl+s":
		ctx := m.kanbanCtxEditor.Value()
		m.kanbanEditingCtx = false
		m.kanbanStatus = "saving…"
		return m, m.cmdPostTaskEdit(api.TaskEditRequestDto{TaskId: m.kanbanBlocks.TaskId, Context: &ctx})
	default:
		m.kanbanCtxEditor = m.kanbanCtxEditor.Update(key)
	}
	return m, nil
}

// handleKanbanPathsKey edits the PF3 declared paths as one comma-separated line. Saving posts the
// split-and-trimmed list (empty = clear) through the same structured /tasks/edit as title/context.
func (m *Model) handleKanbanPathsKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.kanbanEditingPaths = false
		return m, nil
	case "enter":
		m.kanbanEditingPaths = false
		paths := []string{}
		for _, p := range strings.Split(m.kanbanPathsBuf, ",") {
			if p = strings.TrimSpace(p); p != "" {
				paths = append(paths, p)
			}
		}
		m.kanbanStatus = "saving…"
		return m, m.cmdPostTaskEdit(api.TaskEditRequestDto{TaskId: m.kanbanBlocks.TaskId, Paths: paths})
	case "backspace":
		if len(m.kanbanPathsBuf) > 0 {
			m.kanbanPathsBuf = m.kanbanPathsBuf[:len(m.kanbanPathsBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			m.kanbanPathsBuf += ch
		}
	}
	return m, nil
}

func (m *Model) handleKanbanProposalKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "enter":
		p := m.kanbanProposal
		m.kanbanProposal = nil
		m.kanbanStatus = "applying the proposal…"
		// The confirm step: the proposal lands through the same structured edit as a manual one.
		return m, m.cmdPostTaskEdit(api.TaskEditRequestDto{TaskId: m.kanbanBlocks.TaskId, Title: p.Title, Context: p.Context})
	case "esc":
		m.kanbanProposal = nil
		m.kanbanStatus = "proposal discarded"
		return m, nil
	}
	return m, nil
}

func (m *Model) handleKanbanHandKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "y", "enter":
		m.kanbanHandConfirm = false
		task := m.kanbanDetailTask()
		if task == nil {
			return m, nil
		}
		content := fmt.Sprintf("Owner hand-off: prioritise task %s — %s.", task.TaskId, task.Title)
		if strings.TrimSpace(task.Context) != "" {
			content += " Context: " + strings.TrimSpace(task.Context)
		}
		m.kanbanStatus = "injecting hand-off…"
		return m, m.cmdPostInject(api.InjectRequestDto{Content: content, StageId: m.kanbanBlocks.StageId})
	case "n", "esc":
		m.kanbanHandConfirm = false
		return m, nil
	}
	return m, nil
}

// --- rendering ---

func (m Model) renderKanbanDetailPane() (string, string) {
	if m.kanbanBlocksErr != "" {
		return destructStyle.Render("✗ "+m.kanbanBlocksErr) + m.kanbanStatusLine(), "esc back"
	}
	if m.kanbanBlocks == nil {
		return subtleStyle.Render("loading card…"), "esc back"
	}

	var b strings.Builder
	head := fmt.Sprintf("%s · %s · stage %s", m.kanbanBlocks.TaskId, m.kanbanBlocks.CheckpointId, m.kanbanBlocks.StageId)
	b.WriteString(accentStyle.Render(head) + "\n")

	width := max(20, m.paneCols()-4)
	for _, blk := range m.kanbanBlocks.Blocks {
		b.WriteString("\n" + m.renderKanbanBlock(blk, width))
	}

	// PF3: declared paths are claim metadata, not prompt content — their own line under the blocks.
	if task := m.kanbanDetailTask(); task != nil {
		b.WriteString("\n\n" + subtleStyle.Render("── ") + accentStyle.Render("✎ declared paths") + subtleStyle.Render(" ") +
			subtleStyle.Render(strings.Repeat("─", max(0, width-lipgloss.Width("declared paths")-6))) + "\n")
		if len(task.Paths) == 0 {
			b.WriteString(subtleStyle.Render("  (none — press p to declare what this card touches)"))
		} else {
			b.WriteString(textStyle.Render("  " + truncate(strings.Join(task.Paths, " · "), width-2)))
		}
	}

	if m.kanbanEditingPaths {
		b.WriteString("\n\n" + accentStyle.Render("✎ paths (comma-separated): ") + textStyle.Render(m.kanbanPathsBuf) + accentStyle.Render("▏"))
		return b.String() + m.kanbanStatusLine(), "type · enter save (empty clears) · esc cancel"
	}
	if m.kanbanEditingCtx {
		b.WriteString("\n\n" + accentStyle.Render("✎ extra context") + "\n" + m.kanbanCtxEditor.View())
		return b.String() + m.kanbanStatusLine(), "type · ctrl+s save · esc cancel"
	}
	if m.kanbanEditingTitle {
		b.WriteString("\n\n" + accentStyle.Render("✎ title: ") + textStyle.Render(m.kanbanTitleBuf) + accentStyle.Render("▏"))
		return b.String() + m.kanbanStatusLine(), "type · enter save · esc cancel"
	}
	if m.kanbanProposal != nil {
		b.WriteString("\n\n" + m.renderKanbanProposal(width))
		return b.String() + m.kanbanStatusLine(), "enter apply · esc discard"
	}
	if m.kanbanHandConfirm {
		b.WriteString("\n\n  " + accentStyle.Render("hand this card to the next session (writes an injection)? ") +
			key("y") + subtleStyle.Render(" yes · ") + key("n") + subtleStyle.Render(" no"))
		return b.String() + m.kanbanStatusLine(), "y confirm · n cancel"
	}
	return b.String() + m.kanbanStatusLine(), "t title · c context · p paths · a advisor refine · h hand off · esc back"
}

// renderKanbanBlock renders one building block: label line (✎ marks editable), then the content —
// dim when empty, truncated to keep the panel scannable.
func (m Model) renderKanbanBlock(blk api.PromptBlockDto, width int) string {
	label := blk.Label
	if blk.Editable {
		label = "✎ " + label
	}
	head := subtleStyle.Render("── ") + accentStyle.Render(label) + subtleStyle.Render(" ") +
		subtleStyle.Render(strings.Repeat("─", max(0, width-lipgloss.Width(label)-4)))
	content := blk.Content
	if strings.TrimSpace(content) == "" {
		return head + "\n" + subtleStyle.Render("  (empty — press "+editKeyFor(blk.Kind)+" to fill)")
	}
	var rows []string
	for i, line := range strings.Split(content, "\n") {
		if i >= 4 {
			rows = append(rows, subtleStyle.Render("  …"))
			break
		}
		rows = append(rows, textStyle.Render("  "+truncate(line, width-2)))
	}
	return head + "\n" + strings.Join(rows, "\n")
}

func (m Model) renderKanbanProposal(width int) string {
	p := m.kanbanProposal
	interpreter := "advisor"
	if p.Interpreter != nil {
		interpreter = *p.Interpreter
	}
	var b strings.Builder
	b.WriteString(accentStyle.Render(fmt.Sprintf("proposal from %s", interpreter)) + "\n")
	if p.Title != nil {
		b.WriteString(subtleStyle.Render("  title:   ") + textStyle.Render(truncate(*p.Title, width-11)) + "\n")
	}
	if p.Context != nil {
		b.WriteString(subtleStyle.Render("  context: ") + textStyle.Render(truncate(*p.Context, width-11)) + "\n")
	}
	b.WriteString(subtleStyle.Render("  nothing is saved until you confirm"))
	return b.String()
}

// editKeyFor names the key that edits a given editable block kind (footer + empty-state hint).
func editKeyFor(kind string) string {
	if kind == "taskTitle" {
		return "t"
	}
	return "c"
}
