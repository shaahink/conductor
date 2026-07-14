package widgets

import (
	"fmt"
	"strings"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

type TranscriptModel struct {
	Lines          []api.TranscriptLineDto
	ScrollOffset   int
	AutoScroll     bool
	SearchQuery    string
	SearchMatchIdx int
	SearchMatches  []int
	FoldTools      bool
	Width          int
	Height         int
	Focused        bool
}

func NewTranscript() TranscriptModel {
	return TranscriptModel{
		AutoScroll: true,
		Width:      80,
		Height:     20,
	}
}

func (m TranscriptModel) Init() TranscriptModel { return m }

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

	case MsgSetLines:
		if lines, ok := msg.Lines.([]api.TranscriptLineDto); ok {
			m.Lines = lines
		}
		return m

	case WidgetMsg:
		switch msg {
		case MsgScrollUp:
			m.AutoScroll = false
			m.ScrollOffset += 5
			return m

		case MsgScrollDown:
			m.ScrollOffset -= 5
			if m.ScrollOffset <= 0 {
				m.ScrollOffset = 0
				m.AutoScroll = true
			}
			return m

		case MsgScrollPageUp:
			m.AutoScroll = false
			m.ScrollOffset += m.Height
			return m

		case MsgScrollPageDown:
			m.ScrollOffset -= m.Height
			if m.ScrollOffset <= 0 {
				m.ScrollOffset = 0
				m.AutoScroll = true
			}
			return m

		case MsgScrollEnd:
			m.AutoScroll = true
			m.ScrollOffset = 0
			return m

		case MsgToggleFold:
			m.FoldTools = !m.FoldTools
			m.ScrollOffset = 0
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
		}
		return m
	}
	return m
}

func (m TranscriptModel) View() string {
	visible := m.visibleLines()
	if m.AutoScroll && len(visible) > m.Height {
		start := len(visible) - m.Height
		if start < 0 {
			start = 0
		}
		visible = visible[start:]
	} else if m.ScrollOffset > 0 {
		start := m.ScrollOffset
		if start > len(visible) {
			start = len(visible) - 1
		}
		if start < 0 {
			start = 0
		}
		visible = visible[start:]
		if len(visible) > m.Height {
			visible = visible[:m.Height]
		}
	} else if len(visible) > m.Height {
		visible = visible[len(visible)-m.Height:]
	}

	var sb strings.Builder

	for _, line := range visible {
		rendered := renderTranscriptLine(line, m.Width)
		sb.WriteString(rendered)
		sb.WriteByte('\n')
	}

	content := strings.TrimRight(sb.String(), "\n")
	linesRendered := len(strings.Split(content, "\n"))
	for i := linesRendered; i < m.Height; i++ {
		content += "\n"
	}

	return content
}

func (m TranscriptModel) visibleLines() []api.TranscriptLineDto {
	if m.FoldTools {
		return m.foldedLines()
	}
	return m.Lines
}

func (m TranscriptModel) foldedLines() []api.TranscriptLineDto {
	var result []api.TranscriptLineDto
	i := 0
	for i < len(m.Lines) {
		line := m.Lines[i]
		if line.Kind == "tool" {
			count := 1
			tools := []string{line.Text}
			j := i + 1
			for j < len(m.Lines) && (m.Lines[j].Kind == "tool" || m.Lines[j].Kind == "result") {
				if m.Lines[j].Kind == "tool" {
					count++
					tools = append(tools, m.Lines[j].Text)
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
			})
			i = j
		} else {
			result = append(result, line)
			i++
		}
	}
	return result
}

func renderTranscriptLine(line api.TranscriptLineDto, width int) string {
	var prefix string
	var style lipgloss.Style

	switch line.Kind {
	case "thinking":
		prefix = dim("\u2059 ")
		style = txThinkingStyle
	case "tool":
		prefix = purple("\u2699 ")
		style = txToolStyle
	case "tool-fold":
		prefix = purple("\u2699\u25B6 ")
		style = txToolStyle
	case "result":
		prefix = green("\u21B3 ")
		style = txResultStyle
	case "stderr":
		prefix = red("! ")
		style = txStderrStyle
	case "system":
		prefix = blue("\u25B8 ")
		style = txSystemStyle
	case "agent":
		prefix = cyan("\u25B8 ")
		style = txAgentStyle
	default:
		prefix = dim("  ")
		style = txRawStyle
	}

	rendered := style.Render(prefix + line.Text)
	if len(rendered) > width {
		rendered = rendered[:width]
	}
	return rendered
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

func (m *TranscriptModel) jumpToMatch() {
	if len(m.SearchMatches) > 0 && m.SearchMatchIdx < len(m.SearchMatches) {
		lineIdx := m.SearchMatches[m.SearchMatchIdx]
		m.ScrollOffset = lineIdx
		if m.ScrollOffset < 0 {
			m.ScrollOffset = 0
		}
		m.AutoScroll = false
	}
}
