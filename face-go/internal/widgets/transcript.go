package widgets

import (
	"fmt"
	"strings"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
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
	Width          int
	Height         int
}

func NewTranscript() TranscriptModel {
	return TranscriptModel{
		AutoScroll: true,
		Width:      80,
		Height:     20,
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
			m.HideThinking = !m.HideThinking
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

	var sb strings.Builder
	for i, line := range window {
		sb.WriteString(renderTranscriptLine(line, m.Width, m.SearchQuery, start+i == matchLine))
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
	lines := m.Lines
	if m.HideThinking {
		filtered := make([]api.TranscriptLineDto, 0, len(lines))
		for _, l := range lines {
			if l.Kind == "thinking" {
				continue
			}
			filtered = append(filtered, l)
		}
		lines = filtered
	}
	if m.FoldTools {
		return foldTools(lines)
	}
	return lines
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

func renderTranscriptLine(line api.TranscriptLineDto, width int, query string, isCurrentMatch bool) string {
	var prefix string
	var style lipgloss.Style

	switch line.Kind {
	case "thinking":
		prefix = dim("⁙ ")
		style = txThinkingStyle
	case "tool":
		prefix = purple("⚙ ")
		style = txToolStyle
	case "tool-fold":
		prefix = purple("⚙▶ ")
		style = txToolStyle
	case "result":
		prefix = green("↳ ")
		style = txResultStyle
	case "stderr":
		prefix = red("! ")
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
	// producer didn't stamp a time. UTC so golden frames are timezone-independent.
	clock := ""
	if width >= 70 && !line.Ts.IsZero() {
		clock = txTimeStyle.Render(line.Ts.UTC().Format("15:04:05")) + " "
	}

	body := highlightMatches(line.Text, query, style, isCurrentMatch)

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
