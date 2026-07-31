package tui

// The Agent tab is mission control: a live status strip (session · checkpoint · gates · current
// task · attention) over the streaming transcript. Everything about "what is happening right now"
// is on this one screen — no tab-hopping to see the process.

import (
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

func (m Model) handleAgentKey(key string) (tea.Model, tea.Cmd) {
	// Raw mode is the old Console tab's pane, so it keeps the old Console tab's keys — the parsed
	// stream's fold/thinking/search keys mean nothing against undecorated stdout.
	if m.agentRaw {
		return m.handleAgentRawKey(key)
	}
	switch key {
	case "up", "k":
		m.transcript = m.transcript.Update(widgets.MsgScrollUp)
	case "down", "j":
		m.transcript = m.transcript.Update(widgets.MsgScrollDown)
	case "pgup":
		m.transcript = m.transcript.Update(widgets.MsgScrollPageUp)
	case "pgdown":
		m.transcript = m.transcript.Update(widgets.MsgScrollPageDown)
	case "end", "l":
		m.transcript = m.transcript.Update(widgets.MsgScrollEnd)
	case "f":
		m.transcript = m.transcript.Update(widgets.MsgToggleFold)
	case "T":
		m.transcript = m.transcript.Update(widgets.MsgToggleThinking)
	case "n":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.transcript.Update(widgets.MsgNextMatch)
		}
	case "N":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.transcript.Update(widgets.MsgPrevMatch)
		}
	}
	return m, nil
}

func (m Model) renderAgentPane() (string, string) {
	// Kept under ~50 cols on purpose: the bottom bar HARD-CLIPS this line with no ellipsis, so the
	// last hint in a longer string is silently deleted rather than marked (that is how the old
	// "…/ search · end live-tail" had been rendering as "end l"). `/ search` is dropped from this side
	// because the bar's own left half already advertises it.
	help := "↑↓ scroll · f fold · T thinking · c raw · end tail"
	if m.agentRaw {
		help = "↑↓ scroll · pgup/pgdn · home/end · c parsed"
	}
	if m.data.Plan == nil {
		return m.renderSplash(), help
	}

	strip := m.renderAgentStrip()
	// Raw mode keeps the strip and drops the footer. The strip is mission control — session,
	// checkpoint, gates, the attention banner — and losing it was the actual cost of tabbing away to
	// the old Console. The footer (model · elapsed · tokens · cost) is the PARSED view's status line;
	// under undecorated stdout it is furniture, and the rows are better spent on output.
	if m.agentRaw {
		body := strip + "\n" + m.renderAgentRawBody(m.paneRows()-lipgloss.Height(strip))
		return body, help
	}

	footer := m.renderAgentFooter()
	// The transcript takes whatever rows the strip and footer leave. Subtracting the footer here is
	// what keeps it on screen: the pane is height-clamped by View(), so a footer the transcript did
	// not make room for would be the row that clips (owner dogfood: "I don't see the footer").
	m.transcript.Height -= lipgloss.Height(strip)
	if footer != "" {
		m.transcript.Height -= lipgloss.Height(footer) // footer's own rule separates it from the stream
	}
	if m.transcript.Height < 3 {
		m.transcript.Height = 3
	}
	body := strip + "\n" + m.transcript.View()
	if footer != "" {
		body += "\n" + footer
	}
	return body, help
}

// --- raw stream (the folded Console tab, SF1.3) ---------------------------------
//
// These two moved here whole from tab_console.go when TabConsole was folded into Agent: one file per
// tab (STYLE.md), and the raw stream is no longer a tab of its own.

// handleAgentRawKey is the old Console tab's key handler. Offsets count back FROM THE TAIL, so 0 is
// pinned-live and `end` re-pins — never offset-from-top on a live stream (STYLE.md).
func (m Model) handleAgentRawKey(key string) (tea.Model, tea.Cmd) {
	page := m.paneRows() - 1
	if page < 1 {
		page = 1
	}
	maxScroll := len(m.data.RawConsole)
	switch key {
	case "up", "k":
		m.consoleScroll++
	case "down", "j":
		if m.consoleScroll > 0 {
			m.consoleScroll--
		}
	case "pgup":
		m.consoleScroll += page
	case "pgdown":
		m.consoleScroll -= page
		if m.consoleScroll < 0 {
			m.consoleScroll = 0
		}
	case "home":
		m.consoleScroll = maxScroll // oldest line (renderer clamps)
	case "end":
		m.consoleScroll = 0
	}
	if m.consoleScroll > maxScroll {
		m.consoleScroll = maxScroll
	}
	return m, nil
}

// renderAgentRawBody is the native console: the agent CLI's raw stdout, exactly as it prints. `rows`
// is what the strip left over — the pane is height-clamped by View(), so a body that sized itself
// against the whole pane would push its own tail below the fold.
func (m Model) renderAgentRawBody(rows int) string {
	lines := m.data.RawConsole
	if len(lines) == 0 {
		return subtleStyle.Render("(no raw output yet — the agent tees stdout to .conductor/logs/session-NNN.jsonl)")
	}
	window := rows - 1 // the counter row below costs one
	if window < 3 {
		window = 3
	}
	end := len(lines) - m.consoleScroll
	if end < 1 {
		end = 1
	}
	if end > len(lines) {
		end = len(lines)
	}
	start := end - window
	if start < 0 {
		start = 0
	}
	var out []string
	for i := start; i < end; i++ {
		out = append(out, subtleStyle.Render(truncate(lines[i].Text, m.paneCols())))
	}
	pos := safeStyle.Render("● live tail")
	if m.consoleScroll > 0 {
		pos = warnStyle.Render(fmt.Sprintf("↕ scrolled back %d — end to live-tail", m.consoleScroll))
	}
	out = append(out, subtleStyle.Render(fmt.Sprintf("%d raw lines · ", len(lines)))+pos)
	return strings.Join(out, "\n")
}

// renderAgentFooter is the Claude-Code-style status line pinned under the transcript: which CLI +
// model is driving, session elapsed, tokens, and cost so far. The top bar carries the same figures
// tiered by width; this one lives WHERE THE USER IS LOOKING — at the foot of the stream they are
// reading — the way Claude Code's own status line sits under its transcript. "" when no session is
// live (nothing to report), so the transcript reclaims the row.
func (m Model) renderAgentFooter() string {
	s := m.data.Plan
	if s == nil || s.SessionNumber == 0 {
		return ""
	}
	w := m.paneCols()

	// Model only — which CLI is driving is already announced in the strip above and by the
	// transcript's own glyphs; the footer is Claude Code's status line, and that shows the model.
	var segs []string
	if s.Model != "" {
		segs = append(segs, tealStyle.Render(shortModel(s.Model)))
	}
	if s.AgentActive || s.SessionElapsedSec > 0 {
		segs = append(segs, subtleStyle.Render(widgets.FmtWall(s.SessionElapsedSec)))
	}
	toks := "↑" + widgets.FmtTokens(s.SessionTokensInput) + " ↓" + widgets.FmtTokens(s.SessionTokensOutput)
	if s.SessionTokensReasoning > 0 {
		toks += " +" + widgets.FmtTokens(s.SessionTokensReasoning) + "r"
	}
	segs = append(segs, subtleStyle.Render(toks))
	segs = append(segs, peachStyle.Render(fmt.Sprintf("$%.2f", s.SessionCostUsd)))

	sep := subtleStyle.Render(" · ")
	line := strings.Join(segs, sep)
	rule := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(strings.Repeat("─", max(1, w)))
	return rule + "\n" + lipgloss.NewStyle().MaxWidth(w).Render(line)
}

// renderAgentStrip is the glanceable header: status+session on line 1, gates+task on line 2,
// an attention banner when the engine needs a human, then a hairline rule.
// providerLabel names the agent CLI behind the transcript (U3.3). The engine serves the RESOLVED
// provider, so "" means an older engine that does not serve it at all — which is not the same as
// "not claude", and must render nothing rather than a guess. "text" is the generic adapter: it names
// no particular CLI, so there is no convention to announce.
func providerLabel(provider string) string {
	switch strings.ToLower(strings.TrimSpace(provider)) {
	case "claude":
		return "claude code"
	case "opencode":
		return "opencode"
	default:
		return ""
	}
}

func (m Model) renderAgentStrip() string {
	s := m.data.Plan
	w := m.paneCols()

	// Line 1 — session · checkpoint, elapsed pinned right. (Run status lives in the top bar.)
	var segs []string
	if s.SessionNumber > 0 {
		seg := accentStyle.Render("s"+fmt.Sprint(s.SessionNumber)) + " " +
			textStyle.Render(s.SessionKind)
		// "attempt 0/0" is pre-first-attempt noise, not information — render only when real.
		if s.MaxAttempts > 0 {
			seg += subtleStyle.Render(fmt.Sprintf(" · attempt %d/%d", s.Attempt, s.MaxAttempts))
		}
		if label := providerLabel(s.Provider); label != "" {
			// Which CLI is driving, next to which model it drives — the transcript below follows
			// this provider's conventions, so naming it is what makes those conventions legible.
			seg += subtleStyle.Render(" · ") + tealStyle.Render(label)
		}
		if s.Model != "" {
			seg += subtleStyle.Render(" · ") + tealStyle.Render(shortModel(s.Model))
		}
		if s.Persona != nil && *s.Persona != "" {
			seg += subtleStyle.Render(" · ") + tealStyle.Render(*s.Persona)
		}
		segs = append(segs, seg)
	}
	if s.CurrentCheckpoint != "" {
		segs = append(segs, accentStyle.Render("◆ "+s.CurrentCheckpoint)+" "+subtleStyle.Render(truncate(s.CurrentCheckpointTitle, 32)))
	}
	left := strings.Join(segs, "  ")
	right := ""
	if s.AgentActive {
		right = safeStyle.Render(widgets.Spinner(m.spinnerFrame)) + " " + subtleStyle.Render(widgets.FmtWall(s.SessionElapsedSec))
	}
	line1 := padBetween(left, right, w)

	// Line 2 — gate chips left, current task right.
	gates := widgets.GateChips(s.Gates, w)
	task := m.currentTaskSegment(w - lipgloss.Width(gates) - 3)
	line2 := padBetween(gates, task, w)

	rows := []string{line1, line2}

	if reason := attentionReason(s.Status, s.AttentionReason); reason != "" {
		banner := destructStyle.Bold(true).Render("⚠ needs human — ") + warnStyle.Render(truncate(reason, w-16))
		rows = append(rows, banner)
	}

	// Live mode only: if the poll/stream has dropped, say so loudly — the strip below is last-known
	// state, not "what's happening now". (Demo mode is always "connected".)
	if m.data.Connection.Mode == api.ModeLive && !m.data.Connection.Connected {
		rows = append(rows, peachStyle.Bold(true).Render("● disconnected")+subtleStyle.Render(" — showing last-known state; retrying…"))
	}

	rule := lipgloss.NewStyle().Foreground(widgets.Surface()).Render(strings.Repeat("─", max(1, w)))
	rows = append(rows, rule)
	return strings.Join(rows, "\n")
}

// shortModel compresses a model id for the one-line strip: "claude-opus-4-8" → "opus-4-8".
// Non-Claude ids pass through untouched — the strip should never guess at unknown vendors.
func shortModel(id string) string {
	return strings.TrimPrefix(id, "claude-")
}

// currentTaskSegment shows live MCP task progress: "task 3/4 ▸ Wire RunDb…".
func (m Model) currentTaskSegment(maxW int) string {
	tasks := m.data.Tasks
	if len(tasks) == 0 || maxW < 12 {
		return ""
	}
	done := 0
	current := ""
	for _, t := range tasks {
		if t.Status == "done" {
			done++
		} else if current == "" && t.Status == "in_progress" {
			current = t.Title
		}
	}
	seg := subtleStyle.Render(fmt.Sprintf("task %d/%d", done, len(tasks)))
	if current != "" {
		seg += tealStyle.Render(" ▸ " + truncate(current, maxW-12))
	}
	return seg
}

func attentionReason(status string, reason *string) string {
	if reason != nil && *reason != "" {
		return *reason
	}
	lower := strings.ToLower(status)
	if strings.Contains(lower, "attention") || strings.Contains(lower, "human") || strings.Contains(lower, "stall") {
		return status
	}
	return ""
}

// renderSplash is the empty state shown before a run is attached (live mode, engine not up yet).
func (m Model) renderSplash() string {
	mark := accentStyle.Render("◆ conductor") + subtleStyle.Render("  — the autonomous engineering conductor")
	how := subtleStyle.Render("No run attached.") + "\n\n" +
		textStyle.Render("Start one:") + "\n" +
		subtleStyle.Render("  conductor run --control-plane -p plans/<your>.plan.json") + "\n\n" +
		subtleStyle.Render("This face auto-discovers .conductor/control-plane.json and attaches.") + "\n" +
		subtleStyle.Render("Or explore offline:  conductor-face --demo")
	target := subtleStyle.Render("waiting on ") + tealStyle.Render(m.baseURL)

	body := mark + "\n\n" + how + "\n\n" + target
	// Center-ish vertically in the pane without a hard dependency on exact pane height.
	pad := (m.paneRows() - lipgloss.Height(body)) / 3
	if pad > 0 {
		body = strings.Repeat("\n", pad) + body
	}
	return body
}

// padBetween joins left and right with enough spaces that right lands on the pane's right edge.
//
// When they do not both fit, the LEFT is sacrificed. MaxWidth truncates from the right edge, so the
// old `MaxWidth(left + " " + right)` ate the right segment — the elapsed clock, which is pinned there
// precisely because it must stay visible — while the left segments stayed whole. That is dogfood
// appendix item 8: at ~100 cols the agent strip dropped its elapsed and kept everything else.
func padBetween(left, right string, width int) string {
	if width < 1 {
		return ""
	}
	if right == "" {
		return lipgloss.NewStyle().MaxWidth(width).Render(left)
	}
	gap := width - lipgloss.Width(left) - lipgloss.Width(right)
	if gap < 1 {
		rw := lipgloss.Width(right)
		// Not even the right half fits: it is still the more important one, so keep as much of it
		// as the width allows rather than showing a left segment that is also cut off.
		if rw+2 > width {
			return lipgloss.NewStyle().MaxWidth(width).Render(right)
		}
		left = lipgloss.NewStyle().MaxWidth(width - rw - 1).Render(left)
		return left + " " + right
	}
	return left + strings.Repeat(" ", gap) + right
}
