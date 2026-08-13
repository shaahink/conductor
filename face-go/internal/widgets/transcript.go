package widgets

import (
	"fmt"
	"sort"
	"strings"
	"time"

	"charm.land/bubbles/v2/viewport"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
)

// TranscriptModel renders the agent's live transcript.
//
// KS2.7: the scrollback is a viewport, not a pair of bespoke fields. `ScrollOffset` counted lines
// back from the live tail and `AutoScroll` said whether it was pinned there — an inverted offset
// clamped in three places (scrollBack, scrollForward, and again inside View) and written raw by
// jumpToMatch, which is bug #30's exact shape one surface further along. adr/0006 decision 1
// supersedes the MECHANISM, not the BEHAVIOUR: tail-anchoring is now GotoBottom + AtBottom, so one
// `↑` still steps one line into history and a live 4000-line buffer still opens on its newest line
// rather than teleporting to its oldest (STYLE.md, "Scrollback counts from the tail").
type TranscriptModel struct {
	Lines []api.TranscriptLineDto
	// Vp is this transcript's pane viewport, built by NewPaneViewport like every other scrollable
	// body in the Face. It is EXPORTED because two packages move the same position and a second copy
	// behind a method pair is how two clamps come to disagree: the pane-scroll key set lives in
	// tui/panescroll.go (applyPaneScroll) and the search jump lives here (jumpToMatch).
	Vp             viewport.Model
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
		Vp:               NewPaneViewport(),
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
		case MsgToggleFold:
			m.FoldTools = !m.FoldTools
			if m.SearchQuery != "" {
				m.SearchMatches = m.findMatches(m.SearchQuery)
				m.SearchMatchIdx = 0
			}
			m.gotoTail()
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
			if m.SearchQuery != "" {
				m.SearchMatches = m.findMatches(m.SearchQuery)
				m.SearchMatchIdx = 0
			}
			m.gotoTail()
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
			m.gotoTail()
		}
		return m
	}
	return m
}

// Viewport is this transcript's `<surface>Viewport()` builder — the same shape as the tui panes'
// (tab_report.go's reportViewport): size FIRST, content SECOND, then re-anchor. Both the key handler
// (through tui's applyPaneScroll) and View go through it, so an offset that survived a resize, a
// fold toggle or a fresh stream line is re-clamped against the body that is actually on screen.
//
// A folded/filtered transcript is a different body of a different length, which is why the offset
// cannot simply be carried across: the clamp lives at the mutation, and this IS the mutation.
func (m TranscriptModel) Viewport() viewport.Model {
	vp := m.Vp
	atBottom := vp.AtBottom()
	vp.SetWidth(max(1, m.Width))
	vp.SetHeight(max(1, m.Height))
	vp.SetContentLines(m.ContentLines())
	// Tail-anchored, and only when the reader was already on the tail. A live stream that yanked a
	// scrolled-back reader forward on every new line would be unreadable; one that never followed the
	// tail would need a keypress per line to watch a run.
	if atBottom {
		vp.GotoBottom()
	}
	return vp
}

// ContentLines renders every visible line once, at the pane's width. The viewport does the
// windowing, so this is the whole body and not a slice of it — which is also what makes the offset
// meaningful across a fold toggle rather than an index into a list that just changed length.
func (m TranscriptModel) ContentLines() []string {
	visible := m.visibleLines()
	matchLine := -1
	if len(m.SearchMatches) > 0 && m.SearchMatchIdx < len(m.SearchMatches) {
		matchLine = m.SearchMatches[m.SearchMatchIdx]
	}
	g := glyphsFor(m.Provider)
	out := make([]string, 0, len(visible))
	for i, line := range visible {
		out = append(out, renderTranscriptLine(line, m.Width, m.SearchQuery, i == matchLine, g))
	}
	return out
}

// AtTail reports whether the pane is showing the newest line — the fact the Agent tab's status
// readout is built from (tui's paneTailReadout).
func (m TranscriptModel) AtTail() bool { return m.Viewport().AtBottom() }

// gotoTail re-pins to the live tail. It reloads first, because GotoBottom is only meaningful against
// the body it is about to show, and a fold toggle has just changed that body.
func (m *TranscriptModel) gotoTail() {
	m.Vp = m.Viewport()
	m.Vp.GotoBottom()
}

func (m TranscriptModel) View() string {
	vp := m.Viewport()
	if vp.AtBottom() {
		return vp.View()
	}
	// Scrolled back: the last row becomes a "N lines below" note — inside the height budget, so the
	// pane never grows and pushes the layout down. The count is read off the viewport rather than
	// stored, which is the whole point: there is no second number left to disagree with the view.
	vp.SetHeight(max(1, m.Height-1))
	below := max(0, vp.TotalLineCount()-vp.Height()-vp.YOffset())
	note := dimStyle.Render(fmt.Sprintf("↕ %d lines below · ", below)) +
		lipgloss.NewStyle().Foreground(colYellow).Render("end") + dimStyle.Render(" to live-tail")
	return vp.View() + "\n" + note
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

// foldToolTail is how much of the last call's one-liner survives the fold. The fold exists to get a
// run of tool calls out of the reader's way, so the tail is a reminder of where the run got to, not
// a second rendering of it — `f` unfolds for the rest.
const foldToolTail = 50

// foldTools collapses a run of tool calls (and the results between them) into one summary row.
//
// SF3.1 spends SC7.2 here twice. The summary used to be "N tools (last: <the last line, byte-cut at
// 47>)" — two defects in one string. The cut was on BYTES: a path with an accented character or a
// CJK glyph landing on the boundary rendered as a U+FFFD replacement char, because Go slices a
// string by index and half a rune is not a rune. And naming only the last call was the least
// informative thing a fold could say: the reader who folded twelve calls away wants to know they
// were eleven greps and an Edit, which the v2 wire now tells us per line (`tool.name`).
func foldTools(src []api.TranscriptLineDto) []api.TranscriptLineDto {
	var result []api.TranscriptLineDto
	i := 0
	for i < len(src) {
		line := src[i]
		if line.Kind == "tool" {
			count := 1
			names := []string{toolLineName(line)}
			last := line.Text
			j := i + 1
			for j < len(src) && (src[j].Kind == "tool" || src[j].Kind == "result") {
				if src[j].Kind == "tool" {
					count++
					names = append(names, toolLineName(src[j]))
					last = src[j].Text
				}
				j++
			}
			summary := fmt.Sprintf("%s folded", plural(count, "tool call"))
			if mix := foldMix(names); mix != "" {
				summary += " · " + mix
			}
			// truncate is rune-aware and appends its own ellipsis — the byte slice it replaces is the
			// whole reason this line exists.
			if tail := truncate(strings.TrimSpace(last), foldToolTail); tail != "" {
				summary += " · last: " + tail
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

// toolLineName is the tool's short name for a folded line: the v2 structure when the engine sent it
// (`mcp__conductor-tasks__bg_start` → `bg_start`, the same name the session digest counts under),
// falling back to the first word of the rendered one-liner for a v1 line, which is exactly where the
// name has always been.
func toolLineName(line api.TranscriptLineDto) string {
	if n := line.Tool.ShortName(); n != "" {
		return n
	}
	if i := strings.IndexAny(line.Text, " \t"); i > 0 {
		return line.Text[:i]
	}
	return strings.TrimSpace(line.Text)
}

// foldMix counts the names in a folded run and renders them most-frequent first, ties broken by
// name so the same run always folds to the same string. Capped: a fold summary that itself needs
// folding has missed the point.
func foldMix(names []string) string {
	counts := map[string]int{}
	var order []string
	for _, n := range names {
		if n == "" {
			continue
		}
		if _, seen := counts[n]; !seen {
			order = append(order, n)
		}
		counts[n]++
	}
	if len(order) == 0 {
		return ""
	}
	sort.SliceStable(order, func(a, b int) bool {
		if counts[order[a]] != counts[order[b]] {
			return counts[order[a]] > counts[order[b]]
		}
		return order[a] < order[b]
	})
	const maxCells = 4
	shown := order
	if len(shown) > maxCells {
		shown = shown[:maxCells]
	}
	cells := make([]string, 0, len(shown)+1)
	for _, n := range shown {
		if counts[n] > 1 {
			cells = append(cells, fmt.Sprintf("%s ×%d", n, counts[n]))
			continue
		}
		cells = append(cells, n)
	}
	if rest := len(order) - len(shown); rest > 0 {
		cells = append(cells, fmt.Sprintf("+%d more", rest))
	}
	return strings.Join(cells, " ")
}

// plural is this package's copy of the report's one — "1 tool call", "12 tool calls".
func plural(n int, unit string) string {
	if n == 1 {
		return fmt.Sprintf("%d %s", n, unit)
	}
	return fmt.Sprintf("%d %ss", n, unit)
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
//
// KS2.7: it reloads the viewport and then moves it with SetYOffset — a CLAMPED viewport method. It
// used to assign `m.ScrollOffset = total - end` directly, computing its own bound from its own idea
// of the body's length; a match found in one filter state and jumped to in another left the offset
// outside the body, and the only thing standing between that and a stranded pane was the renderer's
// throwaway copy. That is bug #30, reached through the search key instead of the arrow key.
func (m *TranscriptModel) jumpToMatch() {
	if len(m.SearchMatches) == 0 || m.SearchMatchIdx >= len(m.SearchMatches) {
		return
	}
	m.Vp = m.Viewport()
	m.Vp.SetYOffset(m.SearchMatches[m.SearchMatchIdx] - m.Vp.Height()/2)
}
