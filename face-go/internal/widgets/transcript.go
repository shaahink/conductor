package widgets

import (
	"fmt"
	"strings"
	"time"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
)

// TranscriptModel renders the agent's live transcript. ScrollOffset counts lines back from the
// live tail (0 = pinned to the newest line), matching how a human thinks about scrollback — one
// ↑ press moves one step into history, never teleports to the top of a 4000-line buffer.
type TranscriptModel struct {
	Lines          []api.TranscriptLineDto
	ScrollOffset   int
	AutoScroll     bool
	SearchQuery    string
	SearchMatchIdx int
	SearchMatches  []int
	FoldTools      bool
	HideThinking   bool
	// CollapseThinking (the default) shows only the LAST row of each consecutive thinking run with
	// a "(+N)" counter — live reasoning reads as one quiet, current thought instead of a wall that
	// drowns the agent's actual messages. T cycles collapsed → full → hidden.
	CollapseThinking bool
	// Provider is the RESOLVED agent provider driving this transcript ("claude" | "opencode" |
	// "text"), straight off /state. It selects the prefix vocabulary — see glyphsFor. "" means an
	// older engine that does not serve it: unknown, NOT "not claude".
	Provider string
	Width    int
	Height   int
}

func NewTranscript() TranscriptModel {
	return TranscriptModel{
		AutoScroll:       true,
		CollapseThinking: true,
		Width:            80,
		Height:           20,
	}
}

// transcriptGlyphs is the per-line prefix vocabulary the transcript borrows from the CLI it is
// mirroring. Users arrive here from Claude Code and opencode and read those terminals all day; the
// point of U3.3 is that the pane looks like the one actually driving, not a third dialect.
//
// An unrecognised provider gets the neutral house set on purpose. "" is an older engine that does
// not serve the field and "text" is the generic adapter that names no CLI — in both cases there is
// no convention to honour, and guessing "probably claude" would put Claude Code's vocabulary on an
// opencode run. Generic is the honest rendering.
type transcriptGlyphs struct {
	tool     string
	result   string
	thinking string
}

var (
	glyphsClaude   = transcriptGlyphs{tool: "●", result: "⎿", thinking: "✻"}
	glyphsOpencode = transcriptGlyphs{tool: "◆", result: "└", thinking: "◇"}
	glyphsHouse    = transcriptGlyphs{tool: "⚙", result: "↳", thinking: "⁙"}
)

func glyphsFor(provider string) transcriptGlyphs {
	switch strings.ToLower(strings.TrimSpace(provider)) {
	case "claude":
		return glyphsClaude
	case "opencode":
		return glyphsOpencode
	default:
		return glyphsHouse
	}
}

func (m TranscriptModel) Update(msg any) TranscriptModel {
	switch msg := msg.(type) {
	case MsgAppendLine:
		if line, ok := msg.Line.(api.TranscriptLineDto); ok {
			m.Lines = append(m.Lines, line)
			if len(m.Lines) > 4000 {
				m.Lines = m.Lines[len(m.Lines)-4000:]
			}
		}
		return m

	case WidgetMsg:
		switch msg {
		case MsgScrollUp:
			m.scrollBack(3)
			return m

		case MsgScrollDown:
			m.scrollForward(3)
			return m

		case MsgScrollPageUp:
			m.scrollBack(m.Height)
			return m

		case MsgScrollPageDown:
			m.scrollForward(m.Height)
			return m

		case MsgScrollEnd:
			m.AutoScroll = true
			m.ScrollOffset = 0
			return m

		case MsgToggleFold:
			m.FoldTools = !m.FoldTools
			m.ScrollOffset = 0
			m.AutoScroll = true
			if m.SearchQuery != "" {
				m.SearchMatches = m.findMatches(m.SearchQuery)
				m.SearchMatchIdx = 0
			}
			return m

		case MsgToggleThinking:
			// Three states: collapsed (default) → full → hidden → collapsed.
			switch {
			case m.CollapseThinking:
				m.CollapseThinking = false
			case !m.HideThinking:
				m.HideThinking = true
			default:
				m.HideThinking = false
				m.CollapseThinking = true
			}
			m.ScrollOffset = 0
			m.AutoScroll = true
			if m.SearchQuery != "" {
				m.SearchMatches = m.findMatches(m.SearchQuery)
				m.SearchMatchIdx = 0
			}
			return m

		case MsgNextMatch:
			if len(m.SearchMatches) > 0 {
				m.SearchMatchIdx = (m.SearchMatchIdx + 1) % len(m.SearchMatches)
				m.jumpToMatch()
			}
			return m

		case MsgPrevMatch:
			if len(m.SearchMatches) > 0 {
				m.SearchMatchIdx--
				if m.SearchMatchIdx < 0 {
					m.SearchMatchIdx = len(m.SearchMatches) - 1
				}
				m.jumpToMatch()
			}
			return m
		}

	case MsgSetSearch:
		m.SearchQuery = msg.Query
		m.SearchMatchIdx = 0
		m.SearchMatches = nil
		if msg.Query != "" {
			m.SearchMatches = m.findMatches(msg.Query)
			if len(m.SearchMatches) > 0 {
				m.jumpToMatch()
			}
		} else {
			m.ScrollOffset = 0
			m.AutoScroll = true
		}
		return m
	}
	return m
}

func (m *TranscriptModel) scrollBack(step int) {
	m.ScrollOffset += step
	if maxOff := m.maxScrollOffset(); m.ScrollOffset > maxOff {
		m.ScrollOffset = maxOff
	}
	m.AutoScroll = m.ScrollOffset == 0
}

// maxScrollOffset accounts for the "N lines below" note row that appears once scrolled, so the
// oldest line remains reachable.
func (m TranscriptModel) maxScrollOffset() int {
	rows := m.Height - 1
	if rows < 1 {
		rows = 1
	}
	maxOff := len(m.visibleLines()) - rows
	if maxOff < 0 {
		maxOff = 0
	}
	return maxOff
}

func (m *TranscriptModel) scrollForward(step int) {
	m.ScrollOffset -= step
	if m.ScrollOffset <= 0 {
		m.ScrollOffset = 0
	}
	m.AutoScroll = m.ScrollOffset == 0
}

func (m TranscriptModel) View() string {
	visible := m.visibleLines()
	total := len(visible)

	off := m.ScrollOffset
	if m.AutoScroll {
		off = 0
	}
	if maxOff := m.maxScrollOffset(); off > maxOff {
		off = maxOff
	}

	// When scrolled back, the last row becomes a "N lines below" note — inside the height
	// budget, so the pane never grows and pushes the layout down.
	h := m.Height
	scrolled := off > 0
	if scrolled && h > 1 {
		h--
	}

	end := total - off
	start := end - h
	if start < 0 {
		start = 0
	}
	window := visible[start:end]

	matchLine := -1
	if len(m.SearchMatches) > 0 && m.SearchMatchIdx < len(m.SearchMatches) {
		matchLine = m.SearchMatches[m.SearchMatchIdx]
	}

	g := glyphsFor(m.Provider)
	var sb strings.Builder
	for i, line := range window {
		sb.WriteString(renderTranscriptLine(line, m.Width, m.SearchQuery, start+i == matchLine, g))
		sb.WriteByte('\n')
	}

	content := strings.TrimRight(sb.String(), "\n")
	linesRendered := len(strings.Split(content, "\n"))
	for i := linesRendered; i < h; i++ {
		content += "\n"
	}

	if scrolled {
		note := dimStyle.Render(fmt.Sprintf("↕ %d lines below · ", off)) +
			lipgloss.NewStyle().Foreground(colYellow).Render("end") + dimStyle.Render(" to live-tail")
		content += "\n" + note
	}
	return content
}

func (m TranscriptModel) visibleLines() []api.TranscriptLineDto {
	// One row per terminal line, FIRST: multi-paragraph text/thinking events arrive with embedded
	// newlines, and a row taller than one breaks every height calculation downstream — the pane
	// overflows the frame and the footer + live tail slip below the fold (owner dogfood 2026-07-17).
	// Continuation rows drop the timestamp (zero Ts) so the clock column stays clean.
	lines := make([]api.TranscriptLineDto, 0, len(m.Lines))
	for _, l := range m.Lines {
		if !strings.Contains(l.Text, "\n") {
			lines = append(lines, l)
			continue
		}
		for i, part := range strings.Split(strings.ReplaceAll(l.Text, "\r", ""), "\n") {
			row := l
			row.Text = part
			if i > 0 {
				row.Ts = time.Time{}
			}
			lines = append(lines, row)
		}
	}
	switch {
	case m.HideThinking:
		filtered := lines[:0]
		for _, l := range lines {
			if l.Kind == "thinking" {
				continue
			}
			filtered = append(filtered, l)
		}
		lines = filtered
	case m.CollapseThinking:
		lines = collapseThinking(lines)
	}
	if m.FoldTools {
		return foldTools(lines)
	}
	return lines
}

// collapseThinking keeps only the last row of each consecutive thinking run and hangs a
// "+N lines (T to expand)" tail under it — the live tail shows the CURRENT thought, and the rows it
// stands for announce both their number and the key that brings them back.
//
// U3.3's spec asked for the first ~3 lines plus that tail; keeping the LAST line instead is
// deliberate, and is the newer of the two decisions (owner dogfood 2026-07-17: a wall of
// un-collapsed reasoning drowned the agent's real messages). Under a live stream the opening rows
// of an in-progress run are stale the moment they land, so a 3-line head would pin the pane to
// thoughts the agent has already moved past. The tail is the half of the spec that was missing: the
// old "(+7)" counter said how much was hidden but never how to get it back.
func collapseThinking(src []api.TranscriptLineDto) []api.TranscriptLineDto {
	out := make([]api.TranscriptLineDto, 0, len(src))
	run := 0
	for i, l := range src {
		if l.Kind == "thinking" {
			run++
			if i+1 < len(src) && src[i+1].Kind == "thinking" {
				continue
			}
			out = append(out, l)
			if run > 1 {
				// Zero Ts: the tail is not its own event, and the clock column pads it into line
				// under the thought it belongs to.
				out = append(out, api.TranscriptLineDto{
					Seq:       l.Seq,
					SessionId: l.SessionId,
					Kind:      "thinking-more",
					Text:      fmt.Sprintf("+%d lines (T to expand)", run-1),
				})
			}
			run = 0
			continue
		}
		out = append(out, l)
	}
	return out
}

func foldTools(src []api.TranscriptLineDto) []api.TranscriptLineDto {
	var result []api.TranscriptLineDto
	i := 0
	for i < len(src) {
		line := src[i]
		if line.Kind == "tool" {
			count := 1
			tools := []string{line.Text}
			j := i + 1
			for j < len(src) && (src[j].Kind == "tool" || src[j].Kind == "result") {
				if src[j].Kind == "tool" {
					count++
					tools = append(tools, src[j].Text)
				}
				j++
			}
			summary := fmt.Sprintf("%d tool calls", count)
			if count > 0 {
				last := tools[len(tools)-1]
				if len(last) > 50 {
					last = last[:47] + "..."
				}
				summary = fmt.Sprintf("%d tools (last: %s)", count, last)
			}
			result = append(result, api.TranscriptLineDto{
				Kind: "tool-fold",
				Text: summary,
				Ts:   line.Ts,
			})
			i = j
		} else {
			result = append(result, line)
			i++
		}
	}
	return result
}

// splitToolCall splits a tool line's "<name> <argument>" into its two halves. The engine's adapters
// emit tool lines in exactly that shape ("read src/Foo.cs"), and Claude Code renders the name bold
// with its one-line argument dim beside it — the name is what you scan for, the argument is detail.
func splitToolCall(text string) (name, arg string) {
	text = strings.TrimSpace(text)
	if i := strings.IndexAny(text, " \t"); i >= 0 {
		return text[:i], strings.TrimSpace(text[i+1:])
	}
	return text, ""
}

func renderTranscriptLine(line api.TranscriptLineDto, width int, query string, isCurrentMatch bool, g transcriptGlyphs) string {
	var prefix string
	var style lipgloss.Style

	switch line.Kind {
	case "thinking":
		prefix = dim(g.thinking + " ")
		style = txThinkingStyle
	case "thinking-more":
		prefix = "  "
		style = txThinkingMoreStyle
	case "tool":
		prefix = purple(g.tool + " ")
		style = txToolStyle
	case "tool-fold":
		prefix = purple(g.tool + "▶ ")
		style = txToolStyle
	case "result":
		// Results hang under the call that produced them. The indent is the whole point: a run of
		// calls stays scannable AS calls, with their output visibly subordinate rather than
		// competing for the same left edge.
		prefix = "  " + green(g.result+" ")
		style = txResultStyle
	case "stderr":
		prefix = "  " + red("! ")
		style = txStderrStyle
	case "system":
		prefix = blue("▸ ")
		style = txSystemStyle
	case "agent":
		prefix = cyan("▸ ")
		style = txAgentStyle
	default:
		prefix = dim("  ")
		style = txRawStyle
	}

	// A wall-clock prefix (like the Ink face had) — skipped at narrow widths and for lines whose
	// producer didn't stamp a time. Continuation rows of a split multi-line event carry no stamp,
	// so they pad to the same column instead: an unpadded continuation starts at the far left and
	// reads as a new event rather than the same one still talking.
	clock := ""
	if width >= 70 {
		if line.Ts.IsZero() {
			clock = strings.Repeat(" ", len("15:04:05")+1)
		} else {
			clock = txTimeStyle.Render(line.Ts.In(timefmt.Location).Format("15:04:05")) + " "
		}
	}

	var body string
	if line.Kind == "tool" {
		name, arg := splitToolCall(line.Text)
		body = highlightMatches(name, query, txToolNameStyle, isCurrentMatch)
		if arg != "" {
			body += " " + highlightMatches(arg, query, txToolArgStyle, isCurrentMatch)
		}
	} else {
		body = highlightMatches(line.Text, query, style, isCurrentMatch)
	}

	// MaxWidth truncates ANSI-safely (via ansi.Truncate internally) — a manual byte-slice here
	// would cut mid-escape-sequence and corrupt the rest of the line's styling.
	return lipgloss.NewStyle().MaxWidth(width).Render(clock + prefix + body)
}

// highlightMatches paints every occurrence of query inside text, keeping the line's own style for
// the rest. The current match's line gets a bolder treatment via isCurrentMatch.
func highlightMatches(text, query string, style lipgloss.Style, isCurrentMatch bool) string {
	if query == "" {
		return style.Render(text)
	}
	lowText, lowQuery := strings.ToLower(text), strings.ToLower(query)
	// Case-folding can change byte length for exotic Unicode; byte offsets into lowText would
	// then mis-slice text. Highlighting is best-effort — skip it rather than corrupt the line.
	if len(lowText) != len(text) {
		return style.Render(text)
	}
	idx := strings.Index(lowText, lowQuery)
	if idx < 0 {
		return style.Render(text)
	}
	match := txMatchStyle
	if isCurrentMatch {
		match = match.Bold(true)
	}
	var sb strings.Builder
	for idx >= 0 {
		sb.WriteString(style.Render(text[:idx]))
		sb.WriteString(match.Render(text[idx : idx+len(query)]))
		text = text[idx+len(query):]
		lowText = lowText[idx+len(query):]
		idx = strings.Index(lowText, lowQuery)
	}
	sb.WriteString(style.Render(text))
	return sb.String()
}

func (m TranscriptModel) findMatches(query string) []int {
	query = strings.ToLower(query)
	var matches []int
	for i, line := range m.visibleLines() {
		if strings.Contains(strings.ToLower(line.Text), query) {
			matches = append(matches, i)
		}
	}
	return matches
}

// jumpToMatch scrolls so the current match sits mid-window.
func (m *TranscriptModel) jumpToMatch() {
	if len(m.SearchMatches) == 0 || m.SearchMatchIdx >= len(m.SearchMatches) {
		return
	}
	total := len(m.visibleLines())
	matchIdx := m.SearchMatches[m.SearchMatchIdx]
	end := matchIdx + m.Height/2
	if end < m.Height {
		end = m.Height
	}
	if end > total {
		end = total
	}
	m.ScrollOffset = total - end
	m.AutoScroll = m.ScrollOffset == 0
}
