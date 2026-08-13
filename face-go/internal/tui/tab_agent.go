package tui

// The Agent tab is mission control: a live status strip (session · checkpoint · gates · current
// task · attention) over the streaming transcript. Everything about "what is happening right now"
// is on this one screen — no tab-hopping to see the process.

import (
	"fmt"
	"strings"

	"charm.land/bubbles/v2/viewport"
	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
	"conductor-face-go/internal/widgets"
)

// agentModel is the Agent tab's own state (K6.3). It has no async messages of its own — the
// transcript and console streams land in the shell, which owns the channels — but it does own which
// of its two bodies is up and how far each is scrolled.
type agentModel struct {
	// raw swaps the body from the parsed transcript to the raw agent stdout that used to be the
	// Console tab (SF1.3). The strip stays in both modes — keeping mission control on screen while
	// reading raw output is the whole reason this folded instead of staying its own tab.
	raw bool
	// rawVp is the raw stream's pane viewport (KS2.7). It replaced `consoleScroll int`: an INVERTED
	// offset counting back from the tail, incremented without a bound in Update, clamped partly here
	// and partly in the renderer, and reset from a third file (update.go's openFolded). That is bug
	// #30's shape with a minus sign in front of it.
	rawVp viewport.Model
	// searchActive is the inline transcript search (non-blocking). It lives here, not on the shell,
	// because `/` only opens it on this tab; the root Update still peels it before tab dispatch,
	// the way it peels the command bar.
	searchActive bool
}

// handleAgentKey is the parsed transcript's handler: this tab's own semantic keys FIRST, then the
// one pane-scroll set. The scroll set comes last (adr/0006 §2) so a surface key can never be
// shadowed by a pane key — and it is applied to a viewport that has just been sized and loaded, so
// the clamp lands at the mutation.
//
// `k`, `l` and the old MsgScroll* vocabulary are gone. `k` was unreachable (the Knowledge mnemonic
// resolves in update.go's loop before any pane handler ever sees it) and `l` re-pinned the tail on a
// key the help card never mentioned — the fourth key namespace adr/0006 was written to end.
func (m Model) handleAgentKey(key string) (tea.Model, tea.Cmd) {
	// Raw mode is the old Console tab's pane, so it keeps the old Console tab's semantics — the
	// parsed stream's fold/thinking/search keys mean nothing against undecorated stdout.
	if m.agent.raw {
		return m.handleAgentRawKey(key)
	}
	switch key {
	case "f":
		m.transcript = m.sizedTranscript().Update(widgets.MsgToggleFold)
		return m, nil
	case "T":
		m.transcript = m.sizedTranscript().Update(widgets.MsgToggleThinking)
		return m, nil
	case "n":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.sizedTranscript().Update(widgets.MsgNextMatch)
		}
		return m, nil
	case "N":
		if m.transcript.SearchQuery != "" {
			m.transcript = m.sizedTranscript().Update(widgets.MsgPrevMatch)
		}
		return m, nil
	}
	// Size and content FIRST, then move — see handleReportKey. The transcript's viewport lives on the
	// widget (it is the thing that knows the lines and the search index), but the ordering rule is
	// the surface's, so the builder is here.
	m.transcript.Vp = m.agentTranscriptViewport()
	applyPaneScroll(&m.transcript.Vp, key)
	return m, nil
}

// sizedTranscript is the transcript widget with THIS pane's geometry on it. renderAgentPane used to
// do `m.transcript.Height -= …` on the View path against a value receiver, so the height the offset
// was clamped against was never the height the pane was drawn at — precisely the shape that makes a
// clamp unwritable-back (bug #30). Sizing is a pure function of the layout, so it is one, and both
// the key handler and the renderer call it.
func (m Model) sizedTranscript() widgets.TranscriptModel {
	t := m.transcript
	t.Width, t.Height = m.paneCols(), m.agentTranscriptRows()
	return t
}

// agentTranscriptRows is what the strip and the footer leave the stream. Subtracting the footer is
// what keeps it on screen: the pane is height-clamped by View(), so a footer the transcript did not
// make room for would be the row that clips (owner dogfood: "I don't see the footer").
func (m Model) agentTranscriptRows() int {
	rows := m.paneRows()
	if m.data.Plan != nil {
		rows -= lipgloss.Height(m.renderAgentStrip())
		if footer := m.renderAgentFooter(); footer != "" {
			rows -= lipgloss.Height(footer) // the footer's own rule separates it from the stream
		}
	}
	return max(3, rows)
}

// agentTranscriptViewport is the parsed stream's `<surface>Viewport()` builder.
func (m Model) agentTranscriptViewport() viewport.Model { return m.sizedTranscript().Viewport() }

func (m Model) renderAgentPane() (string, string) {
	if m.data.Plan == nil {
		return m.renderSplash(), "f fold · T thinking · c raw"
	}

	strip := m.renderAgentStrip()
	// Raw mode keeps the strip and drops the footer. The strip is mission control — session,
	// checkpoint, gates, the attention banner — and losing it was the actual cost of tabbing away to
	// the old Console. The footer (model · elapsed · tokens · cost) is the PARSED view's status line;
	// under undecorated stdout it is furniture, and the rows are better spent on output.
	if m.agent.raw {
		vp := m.agentRawViewport()
		return strip + "\n" + m.renderAgentRawBody(vp), agentHelp(vp, "c parsed")
	}

	t := m.sizedTranscript()
	body := strip + "\n" + t.View()
	if footer := m.renderAgentFooter(); footer != "" {
		body += "\n" + footer
	}
	return body, agentHelp(t.Viewport(), "f fold · T thinking · c raw")
}

// agentHelp is the Agent tab's bottom-bar line: the one pane-scroll hint (with its percent, only
// when the body outgrows the pane) plus this mode's own keys. Kept under ~50 cols on purpose — the
// bottom bar HARD-CLIPS this line with no ellipsis, so the last hint in a longer string is silently
// deleted rather than marked (that is how the old "…/ search · end live-tail" had been rendering as
// "end l"). `/ search` is dropped from this side because the bar's own left half advertises it.
func agentHelp(vp viewport.Model, own string) string {
	if hint := paneScrollHint(vp, true); hint != "" {
		return hint + " · " + own
	}
	return "↑↓ scroll · " + own
}

// --- raw stream (the folded Console tab, SF1.3) ---------------------------------
//
// These two moved here whole from tab_console.go when TabConsole was folded into Agent: one file per
// tab (STYLE.md), and the raw stream is no longer a tab of its own.

// handleAgentRawKey is the old Console tab's key handler. It owns no semantic keys of its own — raw
// stdout is a document, not a surface with actions — so it is the pane-scroll set and nothing else.
// Size and content FIRST, then move (adr/0006 §1): the clamp lives at the mutation, and a live
// stream is exactly the case where the body changed between the last keypress and this one.
func (m Model) handleAgentRawKey(key string) (tea.Model, tea.Cmd) {
	m.agent.rawVp = m.agentRawViewport()
	applyPaneScroll(&m.agent.rawVp, key)
	return m, nil
}

// agentRawViewport is the raw stream's `<surface>Viewport()` builder. It is TAIL-anchored: a reader
// on the newest line stays there as stdout arrives, and one who has scrolled back is left where they
// are. That is the behaviour STYLE.md pins, now expressed as GotoBottom + AtBottom rather than as an
// inverted integer (adr/0006 decision 1).
func (m Model) agentRawViewport() viewport.Model {
	rows := m.paneRows()
	if m.data.Plan != nil {
		rows -= lipgloss.Height(m.renderAgentStrip())
	}
	rows-- // the counter row under the body costs one
	lines := make([]string, 0, len(m.data.RawConsole))
	for _, l := range m.data.RawConsole {
		lines = append(lines, subtleStyle.Render(truncate(l.Text, m.paneCols())))
	}
	return loadPaneViewport(m.agent.rawVp, lines, m.paneCols(), max(3, rows), true)
}

// renderAgentRawBody is the native console: the agent CLI's raw stdout, exactly as it prints, with
// one counter row under it.
//
// The position readout is the percent every other pane carries plus an at-bottom live-tail marker
// (paneTailReadout). It used to be `↕ scrolled back 137 — end to live-tail`: an inverted line count
// that answered neither "how much is left" nor "am I still live", and whose number came from the
// same unclamped field the window did.
func (m Model) renderAgentRawBody(vp viewport.Model) string {
	if len(m.data.RawConsole) == 0 {
		return subtleStyle.Render("(no raw output yet — the agent tees stdout to .conductor/logs/session-NNN.jsonl)")
	}
	text, live := paneTailReadout(vp)
	pos := warnStyle.Render(text)
	if live {
		pos = safeStyle.Render(text)
	}
	counter := subtleStyle.Render(fmt.Sprintf("%d raw lines · ", len(m.data.RawConsole))) + pos
	return vp.View() + "\n" + counter
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
	// Same figure, same formatter, same rules as the top bar (widgets.FmtSessionCost). These two
	// readouts sat in one frame disagreeing — "$0.00" here beside the session's real cost above —
	// because each did its own fmt.Sprintf on a number whose basis neither of them looked at.
	if money := widgets.FmtSessionCost(s.SessionCostUsd, s.SessionCostBasis); money != "" {
		segs = append(segs, peachStyle.Render(money))
	}

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
		// SF2.1: with its age. "retrying…" with no clock cannot be told apart from a Face that gave
		// up ten minutes ago, and the strip below it is last-known state whose staleness IS this age.
		since := ""
		if age := timefmt.Span(timefmt.Now().Sub(m.data.Connection.Since)); !m.data.Connection.Since.IsZero() {
			since = " for " + age
		}
		rows = append(rows, peachStyle.Bold(true).Render("● disconnected")+
			subtleStyle.Render(" — showing last-known state; retrying"+since+"…"))
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
