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
	if m.telegramEditing {
		return m.handleTelegramFieldEdit(key)
	}
	fields := telegramFieldsList()
	switch key {
	case "esc":
		return m.openTab(TabAgent)
	case "up", "k":
		m.telegramFieldIdx = clamp(m.telegramFieldIdx-1, 0, len(fields)-1)
		return m, nil
	case "down", "j":
		m.telegramFieldIdx = clamp(m.telegramFieldIdx+1, 0, len(fields)-1)
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
	if m.telegramFieldIdx >= len(fields) {
		return m, nil
	}
	f := fields[m.telegramFieldIdx]
	if f.Kind == tgTestAction {
		m.telegramStatusLine = "testing…"
		return m, m.cmdPostTelegramTest()
	}

	m.telegramEditing = true
	m.telegramStatusLine = ""
	switch f.Kind {
	case tgToken:
		m.telegramEditBuf = "" // never prefilled — the server never echoes the token back
	case tgChatIds:
		m.telegramEditBuf = strings.Join(m.telegramChatIds(), ",")
	case tgPollInterval:
		m.telegramEditBuf = strconv.Itoa(m.telegramPollInterval())
	case tgTwoWay:
		m.telegramEnumIdx = 0
		if m.telegramStatus != nil && m.telegramStatus.EnableTwoWay {
			m.telegramEnumIdx = 1
		}
	}
	return m, nil
}

func (m *Model) handleTelegramFieldEdit(key string) (tea.Model, tea.Cmd) {
	fields := telegramFieldsList()
	f := fields[m.telegramFieldIdx]
	switch key {
	case "esc":
		m.telegramEditing = false
		return m, nil
	case "enter":
		return m.saveTelegramField(f)
	}

	if f.Kind == tgTwoWay {
		switch key {
		case "left", "h", "right", "l", "space":
			m.telegramEnumIdx = 1 - m.telegramEnumIdx
		}
		return m, nil
	}

	switch key {
	case "backspace":
		if len(m.telegramEditBuf) > 0 {
			m.telegramEditBuf = m.telegramEditBuf[:len(m.telegramEditBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			if f.Kind == tgPollInterval && (ch < "0" || ch > "9") {
				return m, nil // seconds accept digits only
			}
			m.telegramEditBuf += ch
		}
	}
	return m, nil
}

func (m *Model) saveTelegramField(f telegramFieldSpec) (tea.Model, tea.Cmd) {
	switch f.Kind {
	case tgToken:
		token := strings.TrimSpace(m.telegramEditBuf)
		if token == "" {
			m.telegramEditing = false
			return m, nil
		}
		m.telegramStatusLine = "saving…"
		return m, m.cmdPostTelegramToken(token)
	case tgChatIds:
		v := m.telegramEditBuf
		m.telegramStatusLine = "saving…"
		return m, m.cmdPostTelegramSettingsEdit(api.PlanEditDto{Target: "telegram", Field: "allowedchatids", Value: &v})
	case tgPollInterval:
		v := m.telegramEditBuf
		if v == "" {
			v = "4"
		}
		m.telegramStatusLine = "saving…"
		return m, m.cmdPostTelegramSettingsEdit(api.PlanEditDto{Target: "telegram", Field: "pollintervalseconds", Value: &v})
	case tgTwoWay:
		v := "false"
		if m.telegramEnumIdx == 1 {
			v = "true"
		}
		m.telegramStatusLine = "saving…"
		return m, m.cmdPostTelegramSettingsEdit(api.PlanEditDto{Target: "telegram", Field: "enabletwoway", Value: &v})
	}
	return m, nil
}

func (m Model) telegramChatIds() []string {
	if m.telegramStatus == nil {
		return nil
	}
	return m.telegramStatus.AllowedChatIds
}

func (m Model) telegramPollInterval() int {
	if m.telegramStatus == nil {
		return 4
	}
	return m.telegramStatus.PollIntervalSeconds
}

// --- rendering ---

func (m Model) renderTelegramPane() (string, string) {
	if m.telegramStatus == nil {
		return subtleStyle.Render("loading Telegram status…"), ""
	}
	s := m.telegramStatus

	lines := []string{m.renderTelegramStatusLine(s)}

	if !s.Configured {
		lines = append(lines, "",
			subtleStyle.Render("Not configured on this plan yet — that's fine, just start below;"),
			subtleStyle.Render("saving a token or chat id here configures it for you."))
	}

	// SC1.3: the guide stays up until the engine says it will deliver — not until the last
	// precondition is ticked, which is how a setup that could never notify anybody looked finished.
	if !s.WillDeliver {
		lines = append(lines, "", m.renderTelegramGuide(s))
	}

	lines = append(lines, "")
	lines = append(lines, m.renderTelegramFields()...)

	if m.telegramStatusLine != "" {
		st := safeStyle
		switch {
		case strings.HasPrefix(m.telegramStatusLine, "✗"):
			st = destructStyle
		// SC1.3: "saved, but it still cannot deliver" and "sent, but not through the queue" are
		// warnings, not successes — rendering them in the success colour is the same lie in paint.
		case strings.HasPrefix(m.telegramStatusLine, "⚠"):
			st = warnStyle
		case m.telegramStatusLine == "saving…" || m.telegramStatusLine == "testing…":
			st = warnStyle
		}
		lines = append(lines, "", st.Render(m.telegramStatusLine))
	}

	if s.LastError != nil && *s.LastError != "" {
		lines = append(lines, "", subtleStyle.Render("last poll error: ")+destructStyle.Render(*s.LastError))
	}

	help := "↑↓ field · enter edit/send · esc back"
	if m.telegramEditing {
		if telegramFieldsList()[m.telegramFieldIdx].Kind == tgTwoWay {
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
		if m.telegramEditing && i == m.telegramFieldIdx {
			lines = append(lines, "  "+m.renderTelegramFieldEditor(f))
			continue
		}
		disp := m.telegramFieldDisplay(f)
		row := fmt.Sprintf("  %-18s %s", f.Label, disp)
		if i == m.telegramFieldIdx && !m.telegramEditing {
			lines = append(lines, highlightBg.Render(fmt.Sprintf("  %-18s %s", f.Label, m.telegramFieldDisplayPlain(f))))
			continue
		}
		lines = append(lines, row)
	}
	return lines
}

func (m Model) telegramFieldDisplay(f telegramFieldSpec) string {
	s := m.telegramStatus
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
	s := m.telegramStatus
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
		sel := opts[m.telegramEnumIdx]
		carousel := accentStyle.Render("‹") + highlightBg.Render(" "+sel+" ") + accentStyle.Render("›")
		return fmt.Sprintf("%-18s %s", f.Label, carousel)
	}
	disp := m.telegramEditBuf
	if f.Kind == tgToken && disp != "" {
		disp = strings.Repeat("•", len(disp))
	}
	return fmt.Sprintf("%-18s %s", f.Label, accentStyle.Render(disp)+accentStyle.Render("▏"))
}
