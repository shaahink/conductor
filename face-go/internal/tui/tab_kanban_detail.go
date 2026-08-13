package tui

// P3: the Kanban card detail — `enter` on a card opens its prompt as labeled building blocks
// (GET /prompt/blocks?task=), instead of the compiled wall of text. The task-scoped blocks are
// editable as STRUCTURED task data (`t` title, `c` extra context → POST /tasks/edit); `a` asks the
// plan's advisor for a refinement (a PROPOSAL — applied only when the owner confirms with enter);
// `h` hands the card to the next session by writing an injection (POST /inject) after a y/n confirm.

import (
	"fmt"
	"strings"

	"charm.land/bubbles/v2/viewport"
	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

func (m *Model) kanbanOpenDetail(taskId string) tea.Cmd {
	m.kanban.detail = true
	m.kanban.blocks = nil
	m.kanban.blocksErr = ""
	m.kanban.proposal = nil
	m.kanban.refining = false
	m.kanban.split = nil
	m.kanban.splitting = false
	m.kanban.splitPending = nil
	m.kanban.handConfirm = false
	m.kanban.editingTitle = false
	m.kanban.editingCtx = false
	m.kanban.editingPaths = false
	m.kanban.status = ""
	return m.cmdFetchPromptBlocks(taskId)
}

func (m *Model) kanbanCloseDetail() {
	m.kanban.detail = false
	m.kanban.blocks = nil
	m.kanban.blocksErr = ""
	m.kanban.proposal = nil
	m.kanban.refining = false
	m.kanban.split = nil
	m.kanban.splitting = false
	m.kanban.splitPending = nil
	m.kanban.handConfirm = false
	m.kanban.editingTitle = false
	m.kanban.editingCtx = false
	m.kanban.editingPaths = false
}

// kanbanDetailTask resolves the open card from the board data (title/context may be fresher there
// than in the blocks snapshot after an edit round-trip).
func (m Model) kanbanDetailTask() *api.TaskDto {
	if m.kanban.blocks == nil {
		return nil
	}
	for i := range m.data.Tasks {
		if m.data.Tasks[i].TaskId == m.kanban.blocks.TaskId {
			return &m.data.Tasks[i]
		}
	}
	return nil
}

func (m Model) kanbanBlock(kind string) *api.PromptBlockDto {
	if m.kanban.blocks == nil {
		return nil
	}
	for i := range m.kanban.blocks.Blocks {
		if m.kanban.blocks.Blocks[i].Kind == kind {
			return &m.kanban.blocks.Blocks[i]
		}
	}
	return nil
}

func (m *Model) handleKanbanDetailKey(key string) (tea.Model, tea.Cmd) {
	// Modal-ish sub-states first: editors, proposal, hand-off confirm.
	if m.kanban.editingTitle {
		return m.handleKanbanTitleKey(key)
	}
	if m.kanban.editingCtx {
		return m.handleKanbanCtxKey(key)
	}
	if m.kanban.editingPaths {
		return m.handleKanbanPathsKey(key)
	}
	if m.kanban.proposal != nil {
		return m.handleKanbanProposalKey(key)
	}
	if m.kanban.split != nil {
		return m.handleKanbanSplitKey(key)
	}
	if m.kanban.handConfirm {
		return m.handleKanbanHandKey(key)
	}

	// This surface's own semantic keys FIRST — it captures every key (tabHandlesAllKeys), so a scroll
	// key applied before them would swallow `t`, `c`, `p`, `q`, `a`, `s`, `h` and `esc`. The one
	// pane-scroll set goes at the bottom, against a viewport that has just been sized and loaded.
	task := m.kanbanDetailTask()
	switch key {
	case "esc":
		m.kanbanCloseDetail()
		return m, nil
	case "t":
		if task != nil {
			m.kanban.editingTitle = true
			m.kanban.titleBuf = task.Title
			m.kanban.status = ""
		}
		return m, nil
	case "c":
		if task != nil {
			m.kanban.editingCtx = true
			w := max(10, m.paneCols()-8)
			m.kanban.ctxEditor = widgets.NewTextArea(task.Context, w, max(3, min(8, m.paneRows()-6)))
			m.kanban.status = ""
		}
		return m, nil
	case "p":
		// PF3: edit the card's declared paths — one line, comma-separated; empty clears.
		if task != nil {
			m.kanban.editingPaths = true
			m.kanban.pathsBuf = strings.Join(task.Paths, ", ")
			m.kanban.status = ""
		}
		return m, nil
	case "q":
		// W4.4: cycle this card's QA override — inherit → verify → off → inherit. Three values,
		// so a cycle beats a text field; it saves through the same structured edit as everything else.
		if task != nil {
			next := nextQa(task.Qa)
			m.kanban.status = "qa: " + qaLabel(next) + "…"
			return m, m.cmdPostTaskEdit(api.TaskEditRequestDto{TaskId: task.TaskId, Qa: next})
		}
		return m, nil
	case "a":
		if task != nil && !m.kanban.refining {
			m.kanban.refining = true
			m.kanban.status = "asking the advisor…"
			return m, m.cmdPostTaskRefine(api.TaskRefineRequestDto{TaskId: task.TaskId})
		}
		return m, nil
	case "s":
		// W4.3: ask the advisor to break this card into children. Proposal only.
		if task != nil && !m.kanban.splitting {
			m.kanban.splitting = true
			m.kanban.status = "asking the advisor to split it…"
			return m, m.cmdPostTaskSplit(api.TaskSplitRequestDto{TaskId: task.TaskId})
		}
		return m, nil
	case "h":
		if task != nil {
			m.kanban.handConfirm = true
			m.kanban.status = ""
		}
		return m, nil
	}
	m.kanban.detailVp = m.kanbanDetailViewport()
	applyPaneScroll(&m.kanban.detailVp, key)
	return m, nil
}

// kanbanDetailViewport is the card detail's `<surface>Viewport()` builder. The scrollable body is
// the card itself; the transient rows under it — an open editor, a proposal, the hand-off confirm,
// the status line — render OUTSIDE the viewport and are deducted from its height, because a confirm
// prompt you have to scroll to is a confirm you cannot answer.
func (m Model) kanbanDetailViewport() viewport.Model {
	rows := m.paneRows() - blockHeight(m.kanbanDetailTrailer())
	return loadPaneViewport(m.kanban.detailVp, strings.Split(m.kanbanDetailBody(), "\n"),
		m.paneCols(), max(3, rows), false)
}

// blockHeight is lipgloss.Height with the one distinction it does not make: an EMPTY block occupies
// no rows. lipgloss.Height("") is 1, and deducting a phantom row from every pane that has no trailer
// is how a body loses its last line for no reason.
func blockHeight(s string) int {
	if s == "" {
		return 0
	}
	return lipgloss.Height(s)
}

func (m *Model) handleKanbanTitleKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.kanban.editingTitle = false
		return m, nil
	case "enter":
		title := strings.TrimSpace(m.kanban.titleBuf)
		if title == "" {
			return m, nil // a card must stay nameable — stay in the editor
		}
		m.kanban.editingTitle = false
		m.kanban.status = "saving…"
		return m, m.cmdPostTaskEdit(api.TaskEditRequestDto{TaskId: m.kanban.blocks.TaskId, Title: &title})
	case "backspace":
		if len(m.kanban.titleBuf) > 0 {
			m.kanban.titleBuf = m.kanban.titleBuf[:len(m.kanban.titleBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			m.kanban.titleBuf += ch
		}
	}
	return m, nil
}

func (m *Model) handleKanbanCtxKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.kanban.editingCtx = false
		return m, nil
	case "ctrl+s":
		ctx := m.kanban.ctxEditor.Value()
		m.kanban.editingCtx = false
		m.kanban.status = "saving…"
		return m, m.cmdPostTaskEdit(api.TaskEditRequestDto{TaskId: m.kanban.blocks.TaskId, Context: &ctx})
	default:
		m.kanban.ctxEditor = m.kanban.ctxEditor.Update(key)
	}
	return m, nil
}

// handleKanbanPathsKey edits the PF3 declared paths as one comma-separated line. Saving posts the
// split-and-trimmed list (empty = clear) through the same structured /tasks/edit as title/context.
func (m *Model) handleKanbanPathsKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.kanban.editingPaths = false
		return m, nil
	case "enter":
		m.kanban.editingPaths = false
		paths := []string{}
		for _, p := range strings.Split(m.kanban.pathsBuf, ",") {
			if p = strings.TrimSpace(p); p != "" {
				paths = append(paths, p)
			}
		}
		m.kanban.status = "saving…"
		return m, m.cmdPostTaskEdit(api.TaskEditRequestDto{TaskId: m.kanban.blocks.TaskId, Paths: paths})
	case "backspace":
		if len(m.kanban.pathsBuf) > 0 {
			m.kanban.pathsBuf = m.kanban.pathsBuf[:len(m.kanban.pathsBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			m.kanban.pathsBuf += ch
		}
	}
	return m, nil
}

// handleKanbanSplitKey confirms or discards a proposed split. Enter applies the children one at a
// time through the ordinary add path — the same confirm contract as a refine, so nothing the model
// proposed reaches the board without the owner.
func (m *Model) handleKanbanSplitKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "enter":
		p := m.kanban.split
		m.kanban.split = nil
		if p == nil || len(p.Subtasks) == 0 {
			return m, nil
		}
		checkpointId := ""
		if p.CheckpointId != nil {
			checkpointId = *p.CheckpointId
		}
		m.kanban.splitPending = p.Subtasks[1:]
		m.kanban.status = fmt.Sprintf("adding %d subtask(s)…", len(p.Subtasks))
		return m, m.cmdPostTaskAdd(api.TaskAddRequestDto{CheckpointId: checkpointId, Title: p.Subtasks[0].Title})
	case "esc":
		m.kanban.split = nil
		m.kanban.status = "split discarded"
		return m, nil
	}
	return m, nil
}

func (m *Model) handleKanbanProposalKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "enter":
		p := m.kanban.proposal
		m.kanban.proposal = nil
		m.kanban.status = "applying the proposal…"
		// The confirm step: the proposal lands through the same structured edit as a manual one.
		return m, m.cmdPostTaskEdit(api.TaskEditRequestDto{TaskId: m.kanban.blocks.TaskId, Title: p.Title, Context: p.Context})
	case "esc":
		m.kanban.proposal = nil
		m.kanban.status = "proposal discarded"
		return m, nil
	}
	return m, nil
}

func (m *Model) handleKanbanHandKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "y", "enter":
		m.kanban.handConfirm = false
		task := m.kanbanDetailTask()
		if task == nil {
			return m, nil
		}
		content := fmt.Sprintf("Owner hand-off: prioritise task %s — %s.", task.TaskId, task.Title)
		if strings.TrimSpace(task.Context) != "" {
			content += " Context: " + strings.TrimSpace(task.Context)
		}
		m.kanban.status = "injecting hand-off…"
		return m, m.cmdPostInject(api.InjectRequestDto{Content: content, StageId: m.kanban.blocks.StageId})
	case "n", "esc":
		m.kanban.handConfirm = false
		return m, nil
	}
	return m, nil
}

// --- rendering ---

func (m Model) renderKanbanDetailPane() (string, string) {
	if m.kanban.blocksErr != "" {
		return destructStyle.Render("✗ "+m.kanban.blocksErr) + m.kanbanStatusLine(), "esc back"
	}
	if m.kanban.blocks == nil {
		return subtleStyle.Render("loading card…"), "esc back"
	}
	vp := m.kanbanDetailViewport()
	trailer, help := m.kanbanDetailTrailerAndHelp()
	if hint := paneScrollHint(vp, true); hint != "" && !m.kanbanDetailIsBusy() {
		help = hint + " · " + help
	}
	return vp.View() + trailer, help
}

// kanbanDetailIsBusy reports whether a sub-state owns the keys — an editor, a proposal, a confirm.
// The scroll hint is dropped there because the scroll KEYS are dropped there: `d` and `u` are going
// into a text buffer, and a bottom bar advertising a key the pane is eating is the drift adr/0006
// exists to end.
func (m Model) kanbanDetailIsBusy() bool {
	return m.kanban.editingPaths || m.kanban.editingCtx || m.kanban.editingTitle ||
		m.kanban.proposal != nil || m.kanban.split != nil || m.kanban.handConfirm
}

// kanbanDetailTrailer is the transient block under the card, without its help text.
func (m Model) kanbanDetailTrailer() string {
	t, _ := m.kanbanDetailTrailerAndHelp()
	return t
}

// kanbanDetailTrailerAndHelp is whatever sub-state is open (editor, proposal, confirm) plus the
// status line, and the bottom-bar help that goes with it. It is kept out of the viewport so it can
// never scroll away from the reader it is asking a question of.
func (m Model) kanbanDetailTrailerAndHelp() (string, string) {
	status := m.kanbanStatusLine()
	switch {
	case m.kanban.editingPaths:
		return "\n\n" + accentStyle.Render("✎ paths (comma-separated): ") + textStyle.Render(m.kanban.pathsBuf) +
			accentStyle.Render("▏") + status, "type · enter save (empty clears) · esc cancel"
	case m.kanban.editingCtx:
		return "\n\n" + accentStyle.Render("✎ extra context") + "\n" + m.kanban.ctxEditor.View() + status,
			"type · ctrl+s save · esc cancel"
	case m.kanban.editingTitle:
		return "\n\n" + accentStyle.Render("✎ title: ") + textStyle.Render(m.kanban.titleBuf) +
			accentStyle.Render("▏") + status, "type · enter save · esc cancel"
	case m.kanban.proposal != nil:
		return "\n\n" + m.renderKanbanProposal(max(20, m.paneCols()-4)) + status, "enter apply · esc discard"
	case m.kanban.split != nil:
		return "\n\n" + m.renderKanbanSplit(max(20, m.paneCols()-4)) + status, "enter apply · esc discard"
	case m.kanban.handConfirm:
		return "\n\n  " + accentStyle.Render("hand this card to the next session (writes an injection)? ") +
				key("y") + subtleStyle.Render(" yes · ") + key("n") + subtleStyle.Render(" no") + status,
			"y confirm · n cancel"
	}
	return status, "t title · c context · p paths · q qa · a advisor refine · s split · h hand off · esc back"
}

// kanbanDetailBody is the card itself — head, every prompt block, the declared paths and the QA dial.
// Built here rather than inside the renderer so the key handler can load the same bytes into the
// viewport it is about to scroll.
func (m Model) kanbanDetailBody() string {
	var b strings.Builder
	head := fmt.Sprintf("%s · %s · stage %s", m.kanban.blocks.TaskId, m.kanban.blocks.CheckpointId, m.kanban.blocks.StageId)
	b.WriteString(accentStyle.Render(head) + "\n")

	width := max(20, m.paneCols()-4)
	for _, blk := range m.kanban.blocks.Blocks {
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

		// W4.4: the per-item QA dial — pipeline control that reaches this one card.
		b.WriteString("\n\n" + subtleStyle.Render("── ") + accentStyle.Render("✎ qa") + subtleStyle.Render(" ") +
			subtleStyle.Render(strings.Repeat("─", max(0, width-lipgloss.Width("qa")-6))) + "\n")
		// Wrapped plain, then styled per line: at 80 cols the pane is ~50 wide and this sentence is
		// 60, and the viewport does not soft-wrap — an unwrapped row would be CLIPPED at the pane edge
		// with no ellipsis, deleting the "press q" that is the only reason the row is here.
		qa := "inherit — the stage/plan dial decides (press q to override)"
		if task.Qa != "" {
			qa = task.Qa + "  (this card only — press q to cycle)"
		}
		for i, l := range wrapPlain(qa, width-2) {
			if i > 0 {
				b.WriteString("\n")
			}
			b.WriteString(subtleStyle.Render("  " + l))
		}
	}

	return b.String()
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
	p := m.kanban.proposal
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

// nextQa cycles the per-item QA dial (W4.4). "" is inherit — the card follows the stage/plan dial.
func nextQa(current string) string {
	switch current {
	case "verify":
		return "off"
	case "off":
		return "inherit"
	default:
		return "verify"
	}
}

// qaLabel names a stored QA override for the panel ("" is the absence of one).
func qaLabel(qa string) string {
	if qa == "" {
		return "inherit"
	}
	return qa
}

// renderKanbanSplit shows the proposed children — nothing is on the board until enter.
func (m Model) renderKanbanSplit(width int) string {
	p := m.kanban.split
	interpreter := "advisor"
	if p.Interpreter != nil {
		interpreter = *p.Interpreter
	}
	var b strings.Builder
	b.WriteString(accentStyle.Render(fmt.Sprintf("split proposed by %s", interpreter)) + "\n")
	for _, c := range p.Subtasks {
		b.WriteString(subtleStyle.Render("  • ") + textStyle.Render(truncate(c.Title, width-6)) + "\n")
		if c.Context != nil && *c.Context != "" {
			b.WriteString(subtleStyle.Render("    "+truncate(*c.Context, width-8)) + "\n")
		}
	}
	b.WriteString(subtleStyle.Render("  nothing is added until you confirm"))
	return b.String()
}

// editKeyFor names the key that edits a given editable block kind (footer + empty-state hint).
func editKeyFor(kind string) string {
	if kind == "taskTitle" {
		return "t"
	}
	return "c"
}
