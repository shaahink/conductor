package tui

import (
	"fmt"
	"strconv"
	"strings"

	"charm.land/bubbles/v2/viewport"
	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
	"conductor-face-go/internal/widgets"
)

// knowledgeModel is the Knowledge tab's own state (K6.3): the ledger/bug body's viewport and the
// one-line write mode that turns the pane into an input.
type knowledgeModel struct {
	// K6.2: the same viewport as Report, for the same reason. This is the surface the owner reported
	// as unreadable — it could not page, could not jump, and answered `k` with nothing because `k`
	// opens this very tab.
	vp    viewport.Model
	mode  knowledgeInputMode // browse, or entering a note / bug title / bug id to resolve
	input widgets.TextArea
}

// updateKnowledge handles the ledger poll and the write-side result. Both land in data.Ledger /
// data.Bugs / data.Evidence, which nothing outside this tab reads — that is why they belong here and
// the task poll (read by the sidebar too) stays in the shell.
func (m Model) updateKnowledge(msg tea.Msg) (Model, tea.Cmd, bool) {
	switch msg := msg.(type) {

	case MsgKnowledgeUpdated:
		if msg.Ledger != nil {
			m.data.Ledger = msg.Ledger.Entries
		}
		if msg.Bugs != nil {
			m.data.Bugs = msg.Bugs.Bugs
		}
		if msg.Evidence != nil {
			m.data.Evidence = msg.Evidence.Artifacts
			m.data.EvidenceAll = msg.Evidence.Count
		}
		return m, nil, true

	case MsgKnowledgeWritten:
		if msg.Err != "" {
			return m, m.addToast("Failed: "+msg.Err, widgets.ToastError), true
		}
		// Re-poll so the new note/bug (or the resolved bug dropping off) shows immediately.
		return m, tea.Batch(m.addToast(msg.Toast, widgets.ToastSuccess), m.cmdFetchKnowledge()), true
	}
	return m, nil, false
}

type knowledgeInputMode int

const (
	knowledgeBrowse knowledgeInputMode = iota
	knowledgeNote
	knowledgeBug
	knowledgeResolve
)

// The Knowledge tab (M7): the run's memory made visible — OPEN tracked bugs on top, the knowledge
// ledger below. These are the same run.db rows the engine injects into the next session's prompt, so
// the owner can see exactly what the next agent will be told not to re-discover or re-find. Write
// side (n/b/x): file a note, file a bug, or resolve one — captured while watching, so it compounds
// into the next prompt without dropping to a second terminal.
func (m Model) handleKnowledgeKey(key string) (tea.Model, tea.Cmd) {
	if m.knowledge.mode != knowledgeBrowse {
		return m.handleKnowledgeInput(key)
	}
	switch key {
	case "r":
		return m, m.cmdFetchKnowledge()
	case "n":
		return m.beginKnowledgeInput(knowledgeNote), nil
	case "b":
		return m.beginKnowledgeInput(knowledgeBug), nil
	case "x":
		return m.beginKnowledgeInput(knowledgeResolve), nil
	case readerOpenKey:
		// KS2.8: the pane's rows are one line each by design (a ledger is scanned, not read), so a
		// bug detail or a multi-line note is truncated with no way in. The reader opens the whole
		// ledger — every bug detail, evidence path and note in full, real newlines restored.
		title, body := m.knowledgeReaderDoc()
		return m.openReader(title, body, false), nil
	}
	// Size and content FIRST, then move (adr/0006 decision 1) — see handleReportKey. This surface is
	// the one the owner could not read: before K6.2 it bound `up` and `down`/`j` only, could not page,
	// could not jump to either end, and its unbounded offset stranded the pane exactly like Report's.
	m.knowledge.vp = m.knowledgeViewport()
	applyPaneScroll(&m.knowledge.vp, key)
	return m, nil
}

// knowledgeViewport is this model's Knowledge viewport, sized to the pane and loaded with the current
// body — the same construction Report uses, so the two surfaces cannot drift apart again.
func (m Model) knowledgeViewport() viewport.Model {
	vp := m.knowledge.vp
	vp.SetWidth(m.paneCols())
	vp.SetHeight(m.knowledgeRows())
	vp.SetContentLines(m.knowledgeLines())
	return vp
}

// knowledgeRows is the body height. Two rows go to the note/bug prompt while one is open, so the body
// shrinks rather than the prompt being pushed past the frame's MaxHeight clamp — a viewport pads to
// its full height, where the old renderer returned a short slice and left the room by accident.
func (m Model) knowledgeRows() int {
	if m.knowledge.mode != knowledgeBrowse {
		return max(3, m.paneRows()-2)
	}
	return m.paneRows()
}

func (m Model) beginKnowledgeInput(mode knowledgeInputMode) Model {
	m.knowledge.mode = mode
	m.knowledge.input = widgets.NewTextArea("", max(10, m.paneCols()-16), 1)
	return m
}

func (m Model) handleKnowledgeInput(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.knowledge.mode = knowledgeBrowse
		return m, nil
	case "enter":
		val := strings.TrimSpace(m.knowledge.input.Value())
		mode := m.knowledge.mode
		m.knowledge.mode = knowledgeBrowse
		if val == "" {
			return m, nil
		}
		switch mode {
		case knowledgeNote:
			return m, m.cmdPostNote(api.NoteRequestDto{Content: val, StageId: m.currentStageId(), Kind: "note"})
		case knowledgeBug:
			return m, m.cmdPostBug(api.BugNewRequestDto{Title: val, StageId: m.currentStageId(), Severity: "medium"})
		case knowledgeResolve:
			id, err := strconv.ParseInt(val, 10, 64)
			if err != nil {
				return m, m.addToast("not a bug id: "+val, widgets.ToastError)
			}
			return m, m.cmdPostBugResolve(api.BugResolveRequestDto{Id: id})
		}
		return m, nil
	default:
		m.knowledge.input = m.knowledge.input.Update(key)
		return m, nil
	}
}

// renderKnowledgePane has no whole-pane empty state, and that is not an omission: knowledgeLines
// always emits its three section headers, so the "No knowledge recorded yet" branch that used to sit
// here could never run. Each section carries its own empty line instead ("none open — clean").
func (m Model) renderKnowledgePane() (string, string) {
	vp := m.knowledgeViewport()
	shown := vp.View()

	if m.knowledge.mode != knowledgeBrowse {
		labels := map[knowledgeInputMode]string{
			knowledgeNote:    "note",
			knowledgeBug:     "bug title",
			knowledgeResolve: "resolve bug #",
		}
		ed := m.knowledge.input
		ed.SetSize(max(10, m.paneCols()-16), 1)
		input := "\n" + accentStyle.Render(labels[m.knowledge.mode]+"› ") + ed.View()
		return shown + input, "type · enter submit · esc cancel"
	}

	help := fmt.Sprintf("%d bugs · %d evidence · %d ledger · n note · b bug · x resolve · z read · %s · r refresh",
		len(m.data.Bugs), len(m.data.Evidence), len(m.data.Ledger), paneScrollHelp(vp))
	return shown, help
}

// knowledgeReaderDoc is the whole Knowledge surface as one plain document for the reader: the same
// three sections the pane shows, with nothing held to one row. The pane flattens every "\n" in a
// note or a bug detail into a space so its rows stay rows; here the newlines are the content.
func (m Model) knowledgeReaderDoc() (title, body string) {
	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("Open bugs (%d)\n", len(m.data.Bugs)))
	for _, b := range m.data.Bugs {
		stage := ""
		if b.StageId != nil && *b.StageId != "" {
			stage = ", " + *b.StageId
		}
		sb.WriteString(fmt.Sprintf("\n#%d (%s%s) %s\n", b.Id, b.Severity, stage, b.Title))
		if b.Detail != nil && strings.TrimSpace(*b.Detail) != "" {
			sb.WriteString(*b.Detail + "\n")
		}
	}
	total := m.data.EvidenceAll
	if total < len(m.data.Evidence) {
		total = len(m.data.Evidence)
	}
	sb.WriteString(fmt.Sprintf("\nEvidence (%d)\n", total))
	for _, a := range m.data.Evidence {
		where := ""
		if a.CheckpointId != nil && *a.CheckpointId != "" {
			where = " · " + *a.CheckpointId
		}
		sb.WriteString(fmt.Sprintf("\n[%s] %s\n%s · %s%s\n", a.Kind, a.Path, evidenceSize(a.Bytes), a.Source, where))
	}
	sb.WriteString(fmt.Sprintf("\nKnowledge ledger (%d)\n", len(m.data.Ledger)))
	for _, e := range m.data.Ledger {
		where := ""
		if e.SessionNumber != nil {
			where = fmt.Sprintf(" (s%d", *e.SessionNumber)
			if e.StageId != nil && *e.StageId != "" {
				where += "/" + *e.StageId
			}
			where += ")"
		} else if e.StageId != nil && *e.StageId != "" {
			where = " (" + *e.StageId + ")"
		}
		sb.WriteString("\n[" + e.Kind + "]" + where + "\n" + e.Content + "\n")
	}
	return "knowledge — the full ledger", sb.String()
}

// knowledgeLines builds the full styled body (bugs section, then ledger) as a flat slice so the pane
// can window it with a single scroll offset.
func (m Model) knowledgeLines() []string {
	width := m.paneCols()
	var lines []string

	// ── Open bugs ──
	lines = append(lines, accentStyle.Render(fmt.Sprintf("◆ Open bugs (%d)", len(m.data.Bugs))))
	if len(m.data.Bugs) == 0 {
		lines = append(lines, safeStyle.Render("  none open — clean"))
	}
	for _, b := range m.data.Bugs {
		stage := ""
		if b.StageId != nil && *b.StageId != "" {
			stage = subtleStyle.Render(" [" + *b.StageId + "]")
		}
		// SF2.2: how long this bug has been open. A bug ledger with no clock cannot answer the one
		// question an owner asks it — "has this been sitting here for three sessions?" — and created_at
		// has been on the DTO, unrendered, since the tab existed.
		age := knowledgeAge(b.CreatedAt)
		head := fmt.Sprintf("  %s %s%s %s",
			peachStyle.Render(fmt.Sprintf("#%d", b.Id)),
			severityStyle(b.Severity).Render("("+b.Severity+")"),
			stage,
			textStyle.Render(truncate(b.Title, width-18-lipgloss.Width(age))))
		if age != "" {
			head += subtleStyle.Render(" " + age)
		}
		lines = append(lines, head)
		if b.Detail != nil && strings.TrimSpace(*b.Detail) != "" {
			detail := strings.ReplaceAll(strings.ReplaceAll(*b.Detail, "\r", " "), "\n", " ")
			lines = append(lines, subtleStyle.Render("     "+truncate(detail, width-6)))
		}
	}

	lines = append(lines, "")
	lines = append(lines, m.evidenceLines(width)...)
	lines = append(lines, "")

	// ── Knowledge ledger ──
	lines = append(lines, accentStyle.Render(fmt.Sprintf("◆ Knowledge ledger (%d)", len(m.data.Ledger))))
	if len(m.data.Ledger) == 0 {
		lines = append(lines, subtleStyle.Render("  empty — nothing noted this run"))
	}
	for _, e := range m.data.Ledger {
		where := ""
		if e.SessionNumber != nil {
			where = fmt.Sprintf(" (s%d", *e.SessionNumber)
			if e.StageId != nil && *e.StageId != "" {
				where += "/" + *e.StageId
			}
			where += ")"
		} else if e.StageId != nil && *e.StageId != "" {
			where = " (" + *e.StageId + ")"
		}
		content := strings.ReplaceAll(strings.ReplaceAll(e.Content, "\r", " "), "\n", " ")
		age := knowledgeAge(e.CreatedAt)
		line := fmt.Sprintf("  %s %s%s",
			tealStyle.Render("["+e.Kind+"]"),
			textStyle.Render(truncate(content, width-len(e.Kind)-len(where)-6-lipgloss.Width(age))),
			subtleStyle.Render(where))
		if age != "" {
			line += subtleStyle.Render(" " + age)
		}
		lines = append(lines, line)
	}
	return lines
}

// evidenceLines is K5.3's surface: what the run has to show for itself. It sits between the bugs and
// the ledger deliberately — the ledger grows every session and would bury it, and the eleventh tab
// folds rather than being added (SF1.3, adr/0004), so evidence is re-homed where its question already
// lives: what does this run know, what is wrong with it, what did it produce.
//
// A visual artifact is marked, because a screenshot nobody forwards is the case the registry exists
// for and it must not read like one more log file. `visual` comes from the engine rather than being
// re-derived from the kind string, so this cannot disagree with what the notifier will send.
func (m Model) evidenceLines(width int) []string {
	total := m.data.EvidenceAll
	if total < len(m.data.Evidence) {
		total = len(m.data.Evidence)
	}
	header := fmt.Sprintf("◆ Evidence (%d)", total)
	if total > len(m.data.Evidence) {
		header = fmt.Sprintf("◆ Evidence (%d of %d)", len(m.data.Evidence), total)
	}
	lines := []string{accentStyle.Render(header)}
	if len(m.data.Evidence) == 0 {
		return append(lines, subtleStyle.Render("  none registered — no artifact this run"))
	}
	for _, a := range m.data.Evidence {
		where := ""
		if a.SessionNumber != nil {
			where = fmt.Sprintf(" (s%d", *a.SessionNumber)
			if a.CheckpointId != nil && *a.CheckpointId != "" {
				where += "/" + *a.CheckpointId
			}
			where += ")"
		} else if a.CheckpointId != nil && *a.CheckpointId != "" {
			where = " (" + *a.CheckpointId + ")"
		}
		meta := fmt.Sprintf(" %s · %s%s", evidenceSize(a.Bytes), a.Source, where)
		age := knowledgeAge(a.CreatedAt)
		kind := "[" + a.Kind + "]"
		// Plain text is measured and truncated BEFORE any style is applied — styling first and
		// width-formatting after is how an ANSI string gets cut mid-escape (STYLE.md).
		room := width - lipgloss.Width(kind) - len(meta) - 4 - lipgloss.Width(age)
		line := fmt.Sprintf("  %s %s%s",
			evidenceKindStyle(a.Visual).Render(kind),
			textStyle.Render(evidencePath(a.Path, room)),
			subtleStyle.Render(meta))
		if age != "" {
			line += subtleStyle.Render(" " + age)
		}
		lines = append(lines, line)
	}
	return lines
}

// evidencePath elides from the LEFT, which is the opposite of every other truncation in this file
// and is right for exactly one reason: the identifying part of an evidence path is its file name.
// The ordinary truncate turns ".conductor/evidence/K5/K5.3-dashboard.png" into
// ".conductor/evidence/K5/K5.3-d…" — a row that cannot tell the owner WHICH screenshot it is, while
// spending its whole width on a directory every artifact shares.
func evidencePath(p string, room int) string {
	if room < 1 {
		return ""
	}
	r := []rune(p)
	if len(r) <= room {
		return p
	}
	if room == 1 {
		return "…"
	}
	return "…" + string(r[len(r)-(room-1):])
}

// evidenceKindStyle: a visual artifact is the one a chat can show inline, and the one this whole
// checkpoint exists for. It gets the peach the bug ids use, not the ledger's teal.
func evidenceKindStyle(visual bool) lipgloss.Style {
	if visual {
		return peachStyle
	}
	return tealStyle
}

// evidenceSize renders bytes the way a surface deciding whether to SEND something needs to read it:
// two significant places under 10, none above, so 8.6 KB and 184 KB line up at the same width.
func evidenceSize(b int64) string {
	switch {
	case b >= 1<<20:
		return fmt.Sprintf("%.1f MB", float64(b)/(1<<20))
	case b >= 1<<10:
		return fmt.Sprintf("%.1f KB", float64(b)/(1<<10))
	default:
		return fmt.Sprintf("%d B", b)
	}
}

// knowledgeAge renders a ledger or bug created_at as its age. It goes through timefmt.Parse rather
// than time.Parse because these two columns do NOT arrive as RFC3339: the engine writes them with
// SQLite's datetime('now'), so they land as "2026-08-01 00:37:30". An RFC3339-only reader rejects
// that and renders nothing, which is a fair guess at why these timestamps were never surfaced.
// An unparseable or missing stamp renders "" and the row simply carries no clock.
func knowledgeAge(createdAt string) string {
	t, ok := timefmt.Parse(createdAt)
	if !ok {
		return ""
	}
	return timefmt.Age(t)
}

func severityStyle(sev string) lipgloss.Style {
	switch sev {
	case "high":
		return destructStyle
	case "low":
		return subtleStyle
	default:
		return warnStyle
	}
}
