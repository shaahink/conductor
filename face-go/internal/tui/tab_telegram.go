package tui

import (
	"fmt"
	"strconv"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
)

// telegramModel is the Telegram tab's own state (K6.3, M8.2): the fetched status, the in-pane field
// editor, and the one-line result of the last write. STYLE.md: a persistent settings form, not the
// transient bottom command bar.
type telegramModel struct {
	status     *api.TelegramStatusDto
	fieldIdx   int
	editing    bool
	editBuf    string
	enumIdx    int
	statusLine string
}

// updateTelegram handles this tab's four write/poll results. Every one of them ends in a re-fetch of
// the status the pane renders, which is why they belong to the pane and not the shell.
func (m Model) updateTelegram(msg tea.Msg) (Model, tea.Cmd, bool) {
	switch msg := msg.(type) {

	case MsgTelegramStatusUpdated:
		if msg.Err != "" {
			return m, nil, true // status is never load-bearing — same as knowledge/tasks/processes polls
		}
		m.telegram.status = msg.Status
		return m, nil, true

	case MsgTelegramTested:
		if msg.Err != "" {
			m.telegram.statusLine = "✗ " + msg.Err
			return m, nil, true
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "test failed"
			if msg.Result.Error != nil {
				reason = *msg.Result.Error
			}
			m.telegram.statusLine = "✗ " + reason
			return m, m.cmdFetchTelegramStatus(), true
		}
		name := "bot"
		if msg.Result != nil && msg.Result.BotUsername != nil {
			name = "@" + *msg.Result.BotUsername
		}
		// SC1.3: a test that bypassed the send queue proved Telegram is reachable, not that this run
		// can notify anybody — the distinction the old always-green tick erased. Say which one it was.
		if msg.Result != nil && !msg.Result.ViaQueue {
			detail := "it did NOT go through the run's push queue"
			if msg.Result.Detail != nil && *msg.Result.Detail != "" {
				detail = *msg.Result.Detail
			}
			m.telegram.statusLine = "⚠ sent by " + name + ", but " + detail
			return m, m.cmdFetchTelegramStatus(), true
		}
		m.telegram.statusLine = "✓ sent — " + name + " delivered through the run's own push queue"
		return m, m.cmdFetchTelegramStatus(), true

	case MsgTelegramTokenSaved:
		if msg.Err != "" {
			m.telegram.statusLine = "✗ " + msg.Err
			return m, nil, true
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "rejected"
			if msg.Result.Message != nil {
				reason = *msg.Result.Message
			}
			m.telegram.statusLine = "✗ " + reason
			return m, nil, true
		}
		msgText := "saved"
		if msg.Result != nil && msg.Result.Message != nil {
			msgText = *msg.Result.Message
		}
		// SC1.3: the save succeeding and the engine being able to deliver are different facts. The
		// engine now says which it is (WillDeliver plus a sentence naming what is still missing, or
		// that a restart is required), and the Face stops rendering both as one green tick.
		if msg.Result != nil && !msg.Result.WillDeliver {
			m.telegram.statusLine = "⚠ " + msgText
		} else {
			m.telegram.statusLine = "✓ " + msgText
		}
		m.telegram.editing = false
		return m, m.cmdFetchTelegramStatus(), true

	case MsgTelegramSettingsSaved:
		if msg.Err != "" {
			m.telegram.statusLine = "✗ " + msg.Err
			return m, nil, true
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "rejected"
			if msg.Result.Error != nil {
				reason = *msg.Result.Error
			}
			m.telegram.statusLine = "✗ " + reason
			return m, nil, true
		}
		m.telegram.statusLine = "✓ saved"
		m.telegram.editing = false
		return m, m.cmdFetchTelegramStatus(), true
	}
	return m, nil, false
}

// M8.2: Telegram guided setup. Written to read like an onboarding wizard — live status up top, a
// numbered guide while incomplete, then an editable field list (bot token, allowed chat ids, poll
// interval, two-way control) and a one-shot "send test message" action, all in-pane per STYLE.md
// (a persistent settings form, not the transient bottom command bar). The bot token is never
// echoed back by the server (TelegramStatusDto only exposes HasToken bool) — the field always
// starts blank on entering edit mode, and the rendered/typed value is always masked.

type telegramFieldKind int

const (
	tgToken telegramFieldKind = iota
	tgChatIds
	tgPollInterval
	tgTwoWay
	tgTestAction
)

type telegramFieldSpec struct {
	Label string
	Kind  telegramFieldKind
}

func telegramFieldsList() []telegramFieldSpec {
	return []telegramFieldSpec{
		{Label: "bot token", Kind: tgToken},
		{Label: "allowed chat ids", Kind: tgChatIds},
		{Label: "poll interval", Kind: tgPollInterval},
		{Label: "two-way control", Kind: tgTwoWay},
		{Label: "send test", Kind: tgTestAction},
	}
}

func (m *Model) handleTelegramKey(key string) (tea.Model, tea.Cmd) {
	if m.telegram.editing {
		return m.handleTelegramFieldEdit(key)
	}
	fields := telegramFieldsList()
	switch key {
	case "esc":
		return m.openTab(TabAgent)
	case "up", "k":
		m.telegram.fieldIdx = clamp(m.telegram.fieldIdx-1, 0, len(fields)-1)
		return m, nil
	case "down", "j":
		m.telegram.fieldIdx = clamp(m.telegram.fieldIdx+1, 0, len(fields)-1)
		return m, nil
	case "enter":
		return m.telegramEnter()
	}
	return m, nil
}

// telegramEnter starts editing the selected field, or — for the test-action row — fires the test
// POST directly (a one-shot action, not something that needs an editor sub-state).
func (m *Model) telegramEnter() (tea.Model, tea.Cmd) {
	fields := telegramFieldsList()
	if m.telegram.fieldIdx >= len(fields) {
		return m, nil
	}
	f := fields[m.telegram.fieldIdx]
	if f.Kind == tgTestAction {
		m.telegram.statusLine = "testing…"
		return m, m.cmdPostTelegramTest()
	}

	m.telegram.editing = true
	m.telegram.statusLine = ""
	switch f.Kind {
	case tgToken:
		m.telegram.editBuf = "" // never prefilled — the server never echoes the token back
	case tgChatIds:
		m.telegram.editBuf = strings.Join(m.telegramChatIds(), ",")
	case tgPollInterval:
		m.telegram.editBuf = strconv.Itoa(m.telegramPollInterval())
	case tgTwoWay:
		m.telegram.enumIdx = 0
		if m.telegram.status != nil && m.telegram.status.EnableTwoWay {
			m.telegram.enumIdx = 1
		}
	}
	return m, nil
}

func (m *Model) handleTelegramFieldEdit(key string) (tea.Model, tea.Cmd) {
	fields := telegramFieldsList()
	f := fields[m.telegram.fieldIdx]
	switch key {
	case "esc":
		m.telegram.editing = false
		return m, nil
	case "enter":
		return m.saveTelegramField(f)
	}

	if f.Kind == tgTwoWay {
		switch key {
		case "left", "h", "right", "l", "space":
			m.telegram.enumIdx = 1 - m.telegram.enumIdx
		}
		return m, nil
	}

	switch key {
	case "backspace":
		if len(m.telegram.editBuf) > 0 {
			m.telegram.editBuf = m.telegram.editBuf[:len(m.telegram.editBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			if f.Kind == tgPollInterval && (ch < "0" || ch > "9") {
				return m, nil // seconds accept digits only
			}
			m.telegram.editBuf += ch
		}
	}
	return m, nil
}

func (m *Model) saveTelegramField(f telegramFieldSpec) (tea.Model, tea.Cmd) {
	switch f.Kind {
	case tgToken:
		token := strings.TrimSpace(m.telegram.editBuf)
		if token == "" {
			m.telegram.editing = false
			return m, nil
		}
		m.telegram.statusLine = "saving…"
		return m, m.cmdPostTelegramToken(token)
	case tgChatIds:
		v := m.telegram.editBuf
		m.telegram.statusLine = "saving…"
		return m, m.cmdPostTelegramSettingsEdit(api.PlanEditDto{Target: "telegram", Field: "allowedchatids", Value: &v})
	case tgPollInterval:
		v := m.telegram.editBuf
		if v == "" {
			v = "4"
		}
		m.telegram.statusLine = "saving…"
		return m, m.cmdPostTelegramSettingsEdit(api.PlanEditDto{Target: "telegram", Field: "pollintervalseconds", Value: &v})
	case tgTwoWay:
		v := "false"
		if m.telegram.enumIdx == 1 {
			v = "true"
		}
		m.telegram.statusLine = "saving…"
		return m, m.cmdPostTelegramSettingsEdit(api.PlanEditDto{Target: "telegram", Field: "enabletwoway", Value: &v})
	}
	return m, nil
}

func (m Model) telegramChatIds() []string {
	if m.telegram.status == nil {
		return nil
	}
	return m.telegram.status.AllowedChatIds
}

func (m Model) telegramPollInterval() int {
	if m.telegram.status == nil {
		return 4
	}
	return m.telegram.status.PollIntervalSeconds
}

// --- rendering ---

func (m Model) renderTelegramPane() (string, string) {
	if m.telegram.status == nil {
		return subtleStyle.Render("loading Telegram status…"), ""
	}
	s := m.telegram.status

	lines := []string{m.renderTelegramStatusLine(s)}

	if !s.Configured {
		// FU-OWNER-13 again, one layer down from the status line. This paragraph reads the SAME
		// Configured=false that a queued reload leaves behind, so between a saved plan edit and the
		// next session boundary it told the owner to save a token or chat id — the edit the engine had
		// just accepted and was holding. Fixing only the head line above would have left the advice
		// intact and the frame still self-contradicting; the frame is what the owner reads.
		//
		// The wording stays careful about what the flag actually means. reloadPending is the engine's
		// general "a plan reload is queued" (ControlPlaneServer.ReloadPending => _reloadQueued), not
		// "your telegram block is queued" — a reload queued by an unrelated plan edit sets it too. So
		// this says what is certainly true (an edit is held, these fields are the pre-edit plan) and
		// lets the owner who just pressed save draw the obvious conclusion, rather than asserting
		// whose edit it was.
		if s.ReloadPending {
			lines = append(lines, "",
				subtleStyle.Render("A plan edit is saved and queued — the engine applies it at the next session"),
				subtleStyle.Render("boundary. The fields below are still the pre-edit plan, not a missing one."))
		} else {
			lines = append(lines, "",
				subtleStyle.Render("Not configured on this plan yet — that's fine, just start below;"),
				subtleStyle.Render("saving a token or chat id here configures it for you."))
		}
	}

	// SC1.3: the guide stays up until the engine says it will deliver — not until the last
	// precondition is ticked, which is how a setup that could never notify anybody looked finished.
	if !s.WillDeliver {
		lines = append(lines, "", m.renderTelegramGuide(s))
	}

	lines = append(lines, "")
	lines = append(lines, m.renderTelegramFields()...)

	if m.telegram.statusLine != "" {
		st := safeStyle
		switch {
		case strings.HasPrefix(m.telegram.statusLine, "✗"):
			st = destructStyle
		// SC1.3: "saved, but it still cannot deliver" and "sent, but not through the queue" are
		// warnings, not successes — rendering them in the success colour is the same lie in paint.
		case strings.HasPrefix(m.telegram.statusLine, "⚠"):
			st = warnStyle
		case m.telegram.statusLine == "saving…" || m.telegram.statusLine == "testing…":
			st = warnStyle
		}
		lines = append(lines, "", st.Render(m.telegram.statusLine))
	}

	if s.LastError != nil && *s.LastError != "" {
		lines = append(lines, "", subtleStyle.Render("last poll error: ")+destructStyle.Render(*s.LastError))
	}

	help := "↑↓ field · enter edit/send · esc back"
	if m.telegram.editing {
		if telegramFieldsList()[m.telegram.fieldIdx].Kind == tgTwoWay {
			help = "←→ toggle · enter save · esc cancel"
		} else {
			help = "type · enter save · esc cancel"
		}
	}
	return strings.Join(lines, "\n"), help
}

// SC1.3: this line used to say "connected" whenever Started && HasToken — a claim about two
// preconditions, printed as if it were a claim about delivery. On the engine's own dead feature both
// were true and nothing was ever delivered. It now reports the engine's verdict (WillDeliver), and
// when that is false it says which half is missing in doctor's words, on its own line so a long
// sentence cannot shove the pane sideways.
func (m Model) renderTelegramStatusLine(s *api.TelegramStatusDto) string {
	head := ""
	switch {
	case s.WillDeliver:
		name := "bot"
		if s.BotUsername != nil {
			name = "@" + *s.BotUsername
		}
		head = safeStyle.Render("● delivering") + " " + textStyle.Render("as "+name)
		// SF2.2: "delivering" is a claim about a poll loop, and the age of the last poll is the only
		// thing on this pane that can contradict it. A bot that last polled 40m ago on a 4s interval
		// is not delivering, however green the dot is. lastPollUtc was on the DTO and rendered nowhere.
		if s.LastPollUtc != nil {
			if t, ok := timefmt.Parse(*s.LastPollUtc); ok {
				head += subtleStyle.Render(" · last poll " + timefmt.Age(t))
			}
		}
	case s.RestartRequired:
		head = warnStyle.Render("◐ saved — restart required")
	case s.Configured:
		head = warnStyle.Render("◐ will not deliver yet")
		// A queued reload does not fix a missing token or a missing chat id, so the verdict above
		// stands and the engine's reason below it is still the right sentence. This only records that
		// the save landed — the thing an owner who just pressed save is looking for.
		if s.ReloadPending {
			head += subtleStyle.Render(" · plan reload queued")
		}
	// FU-OWNER-13: BELOW Configured on purpose. This engine's in-memory PlanConfig has no telegram
	// block yet, so every field above reads exactly as it would for a plan nobody ever configured —
	// which is how a block saved thirty seconds ago came back as "not configured", under a reason
	// telling the owner to add the block they had just added. A queued reload is the one thing that
	// distinguishes the two, and it means WAITING, not unconfigured.
	case s.ReloadPending:
		head = warnStyle.Render("◐ saved — waiting for the next session boundary")
	default:
		head = subtleStyle.Render("○ not configured")
	}
	if s.WillDeliver || s.WillDeliverReason == nil || *s.WillDeliverReason == "" {
		return head
	}
	return head + "\n" + m.telegramReasonLine(*s.WillDeliverReason)
}

// One reason, wrapped to the pane and dimmed so the verdict above it stays what the eye lands on.
// Wrapped as PLAIN text and styled per line afterwards — width-formatting an already-styled string
// measures the escape bytes and misaligns the pane (STYLE.md), and truncating would cut doctor's
// sentence exactly where it names the thing that is missing.
func (m Model) telegramReasonLine(reason string) string {
	wrapped := lipgloss.NewStyle().Width(max(20, m.paneCols()-2)).Render(reason)
	lines := strings.Split(wrapped, "\n")
	for i, l := range lines {
		lines[i] = subtleStyle.Render(strings.TrimRight(l, " "))
	}
	return strings.Join(lines, "\n")
}

func (m Model) renderTelegramGuide(s *api.TelegramStatusDto) string {
	step := func(n int, done bool, text string) string {
		glyph := subtleStyle.Render(fmt.Sprintf("%d.", n))
		if done {
			glyph = safeStyle.Render(fmt.Sprintf("%d. ✓", n))
		}
		return "  " + glyph + " " + textStyle.Render(text)
	}
	lines := []string{
		accentStyle.Render("Guided setup"),
		step(1, s.HasToken, "Create a bot via @BotFather on Telegram, then paste the token below."),
		step(2, len(s.AllowedChatIds) > 0, "Message your bot once, then get the chat id from @userinfobot."),
		// SC1.3: the other way to bootstrap a chat id, said here because it is the one an owner with
		// a token but no chat id needs and cannot guess.
		"     " + subtleStyle.Render("(or read it from the bot API's /getUpdates after messaging it)"),
		step(3, s.WillDeliver, "Send a test message — it goes through the run's own push queue."),
	}
	return strings.Join(lines, "\n")
}

func (m Model) renderTelegramFields() []string {
	fields := telegramFieldsList()
	var lines []string
	for i, f := range fields {
		if m.telegram.editing && i == m.telegram.fieldIdx {
			lines = append(lines, "  "+m.renderTelegramFieldEditor(f))
			continue
		}
		disp := m.telegramFieldDisplay(f)
		row := fmt.Sprintf("  %-18s %s", f.Label, disp)
		if i == m.telegram.fieldIdx && !m.telegram.editing {
			lines = append(lines, highlightBg.Render(fmt.Sprintf("  %-18s %s", f.Label, m.telegramFieldDisplayPlain(f))))
			continue
		}
		lines = append(lines, row)
	}
	return lines
}

func (m Model) telegramFieldDisplay(f telegramFieldSpec) string {
	s := m.telegram.status
	switch f.Kind {
	case tgToken:
		if s != nil && s.HasToken {
			return safeStyle.Render("•••••••• (set)")
		}
		return subtleStyle.Render("(not set)")
	case tgChatIds:
		if s == nil || len(s.AllowedChatIds) == 0 {
			return subtleStyle.Render("(none)")
		}
		return textStyle.Render(strings.Join(s.AllowedChatIds, ", "))
	case tgPollInterval:
		return textStyle.Render(fmt.Sprintf("%ds", m.telegramPollInterval()))
	case tgTwoWay:
		if s != nil && s.EnableTwoWay {
			return safeStyle.Render("true")
		}
		return subtleStyle.Render("false")
	case tgTestAction:
		return tealStyle.Render("▶ press enter to send a real message")
	}
	return ""
}

// telegramFieldDisplayPlain is the same as telegramFieldDisplay but unstyled, for the selected
// row where highlightBg.Render wraps the whole line (STYLE.md: never nest ANSI styles inside a
// background-styled row — style the plain text once, at the outer level).
func (m Model) telegramFieldDisplayPlain(f telegramFieldSpec) string {
	s := m.telegram.status
	switch f.Kind {
	case tgToken:
		if s != nil && s.HasToken {
			return "•••••••• (set)"
		}
		return "(not set)"
	case tgChatIds:
		if s == nil || len(s.AllowedChatIds) == 0 {
			return "(none)"
		}
		return strings.Join(s.AllowedChatIds, ", ")
	case tgPollInterval:
		return fmt.Sprintf("%ds", m.telegramPollInterval())
	case tgTwoWay:
		if s != nil && s.EnableTwoWay {
			return "true"
		}
		return "false"
	case tgTestAction:
		return "▶ press enter to send a real message"
	}
	return ""
}

func (m Model) renderTelegramFieldEditor(f telegramFieldSpec) string {
	if f.Kind == tgTwoWay {
		opts := []string{"false", "true"}
		sel := opts[m.telegram.enumIdx]
		carousel := accentStyle.Render("‹") + highlightBg.Render(" "+sel+" ") + accentStyle.Render("›")
		return fmt.Sprintf("%-18s %s", f.Label, carousel)
	}
	disp := m.telegram.editBuf
	if f.Kind == tgToken && disp != "" {
		disp = strings.Repeat("•", len(disp))
	}
	return fmt.Sprintf("%-18s %s", f.Label, accentStyle.Render(disp)+accentStyle.Render("▏"))
}
