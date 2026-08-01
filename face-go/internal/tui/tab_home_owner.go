package tui

// SF4.2 — the owner queue on the Face.
//
// The engine already knows every obligation only the owner can clear (SF4.1: HUMAN: lines, unapproved
// owner gates, the park it is sitting in, a blocked-until wait, task --blocked checkpoints, stages
// skipped for review) and writes them to `.conductor/OWNER-QUEUE.md` + GET /owner/queue. The half
// that made the owner's hand-written SHAHIN.md worth copying is not the list — it is that every line
// says what it UNBLOCKS and the exact command that clears it, so reading it is deciding, not
// investigating. Both halves are on every row here.
//
// It is Home's second VIEW, not an eleventh tab (SF1.3 capped the strip at ten and made the next
// surface fold; docs/dev/adr/0004). Short queues never open it at all — they are a section on the
// landing, and `w` is for when the list outgrows a page that cannot scroll.

import (
	"fmt"
	"strings"
	"time"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
)

// homeOwnerQueueMax is how many obligations the LANDING lists before it hands off to `w`. Three,
// because Home cannot scroll and every row here is two lines: the rest of the landing has to survive
// a busy queue, and a page that is nothing but the queue has stopped being a landing.
const homeOwnerQueueMax = 3

// ownerKindGlyph gives each kind of obligation a mark, so the shape of the queue reads before the
// words do. The kinds are the engine's own (Core/OwnerQueue.cs); an unknown one falls back rather
// than being dropped, because a queue that silently hides an entry it does not recognise is the
// exact failure this whole surface exists to end.
func ownerKindGlyph(kind string) (glyph string, style lipgloss.Style) {
	switch kind {
	case "park":
		return "■", destructStyle // the run is stopped: nothing moves until this clears
	case "ownerGate":
		return "◆", warnStyle
	case "human":
		return "◆", accentStyle
	case "wait":
		return "◷", subtleStyle // the engine resumes itself; the owner is not being asked for anything
	case "checkpoint":
		return "◇", warnStyle
	case "skippedStage":
		return "◇", subtleStyle
	default:
		return "•", textStyle
	}
}

// ownerAge renders an obligation's age, and it is the one thing on this pane that can lie quietly.
//
// ageSeconds is null whenever the engine cannot date the obligation — a HUMAN: line in a tracker
// handoff has no timestamp anywhere — and the wire writes that null EXPLICITLY so a client cannot
// read an absent key as 0. Zero would render "just now": the single most misleading thing a queue can
// say about an item that may have been sitting there for days. Unknown says unknown.
func ownerAge(ageSeconds *int64) string {
	if ageSeconds == nil {
		return "age unknown"
	}
	return timefmt.Ago(time.Duration(*ageSeconds) * time.Second)
}

// ownerCommand renders the command that clears an entry. Empty is not a gap in the data — a
// blocked-until wait clears itself when the clock passes — so it gets words rather than a blank,
// which would read as "the engine forgot to tell you".
func ownerCommand(cmd string) string {
	if strings.TrimSpace(cmd) == "" {
		return subtleStyle.Render("clears itself — nothing to type")
	}
	return safeStyle.Render("$ " + cmd)
}

// --- the landing section -------------------------------------------------------

// renderHomeOwnerQueue is Home's owner-queue section. It sits directly under Run, so the landing
// reads "here is what the run is doing" then "here is what it needs from you" — and so that the
// tier shed (which scans from the LAST section backwards) takes Workspace, Git and the wiring
// diagnostics before it takes an obligation.
//
// Before the first poll answers there is no section at all: an empty header is worse than nothing,
// and an empty LIST would read as "nothing is owed", which is a claim this pane has not earned yet.
func (m Model) renderHomeOwnerQueue(w int) []homeLine {
	q := m.ownerQueue
	if q == nil {
		if m.ownerQueueErr != "" {
			return homePanel("Owner queue",
				hLine(warnStyle.Render("  owner queue unavailable")+subtleStyle.Render(" — "+m.ownerQueueErr), homeDetail))
		}
		return nil
	}

	title := "Owner queue"
	if q.Count > 0 {
		title = fmt.Sprintf("Owner queue (%d)", q.Count)
	}
	rows := []homeLine{}
	if m.ownerQueueErr != "" {
		// Showing stale rows is right; showing them as if they were fresh is not.
		rows = append(rows, hLine(subtleStyle.Render("  last poll failed — showing the previous queue"), homeDetail))
	}
	if q.Count == 0 {
		// Zero is a real answer, said out loud (SF4.1's DTO comment). It is homeDetail because it is
		// the one row on this page that a short window loses nothing by dropping.
		rows = append(rows, hLine(subtleStyle.Render("  nothing is waiting on you"), homeDetail))
		return homePanel(title, rows...)
	}

	shown := min(len(q.Items), homeOwnerQueueMax)
	for _, it := range q.Items[:shown] {
		glyph, gs := ownerKindGlyph(it.Kind)
		// The age leads: an obligation's age is the whole reason a queue beats a mental note. Rendered
		// PLAIN into a fixed gutter and styled after — padding a styled string pads its escape bytes
		// and shears the column (STYLE.md).
		age := ownerAge(it.AgeSeconds)
		head := "  " + gs.Render(glyph) + " " + subtleStyle.Render(fmt.Sprintf("%-12s", age)) +
			textStyle.Render(it.Title)
		rows = append(rows, hLine(lipgloss.NewStyle().MaxWidth(w).Render(head), homeUseful))
		// What it unblocks, on its own line and droppable: on a short window the obligation itself is
		// worth more than its consequence, and `w` has the full story either way.
		rows = append(rows, hLine(lipgloss.NewStyle().MaxWidth(w).Render(
			"      "+subtleStyle.Render("unblocks "+it.Unblocks)), homeDetail))
	}
	if rest := q.Count - shown; rest > 0 {
		rows = append(rows, hLine("  "+subtleStyle.Render(fmt.Sprintf("+%d more — ", rest))+
			accentStyle.Render("w")+subtleStyle.Render(" for the full queue"), homeUseful))
	}
	return homePanel(title, rows...)
}

// --- the full-pane view (`w`) ---------------------------------------------------

func (m Model) handleOwnerQueueKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k":
		if m.ownerQueueScroll > 0 {
			m.ownerQueueScroll--
		}
	case "down", "j":
		m.ownerQueueScroll++ // clamped by the renderer against the real body height
	case "home":
		m.ownerQueueScroll = 0
	case "pgup":
		m.ownerQueueScroll = max(0, m.ownerQueueScroll-m.paneRows())
	case "pgdown":
		m.ownerQueueScroll += m.paneRows()
	}
	return m, nil
}

// renderOwnerQueuePane is the full list: every entry, with its age, what it unblocks, whatever detail
// the engine attached, and the exact command that clears it. Nothing is capped here — this view IS
// the uncapped one, and it scrolls.
func (m Model) renderOwnerQueuePane() (string, string) {
	w := m.paneCols()
	q := m.ownerQueue

	var sections []string
	switch {
	case q == nil && m.ownerQueueErr != "":
		sections = append(sections,
			warnStyle.Render("The owner queue is unavailable.")+"\n"+subtleStyle.Render(m.ownerQueueErr))
	case q == nil:
		// Not "nothing is owed" — nothing has been ASKED yet. The distinction is the whole point.
		sections = append(sections, subtleStyle.Render("Waiting for the first /owner/queue poll…"))
	default:
		sections = append(sections, m.ownerQueueHeader(q))
		if q.Count == 0 {
			sections = append(sections, subtleStyle.Render(
				"Nothing is waiting on you. Every gate is approved, no session left a HUMAN: line,\n"+
					"and the run is not parked."))
		}
		for i, it := range q.Items {
			sections = append(sections, m.renderOwnerQueueItem(i+1, it, w))
		}
	}

	body := strings.Join(sections, "\n\n")
	body = lipgloss.NewStyle().MaxWidth(w).Render(body)

	lines := strings.Split(body, "\n")
	rows := m.paneRows()
	maxScroll := max(0, len(lines)-rows)
	scroll := min(m.ownerQueueScroll, maxScroll)
	if scroll > 0 || maxScroll > 0 {
		end := min(scroll+rows, len(lines))
		lines = lines[scroll:end]
	}
	help := "w/esc back to Home"
	if maxScroll > 0 {
		help = fmt.Sprintf("↑↓ scroll (%d/%d) · w/esc back to Home", scroll, maxScroll)
	}
	return strings.Join(lines, "\n"), help
}

func (m Model) ownerQueueHeader(q *api.OwnerQueueDto) string {
	head := accentStyle.Render("Owner queue") + "  " +
		textStyle.Render(plural(q.Count, "item")+" only you can clear")
	// The queue is DERIVED at request time, never stored, so its generated stamp is also the answer to
	// "is this current" — the one question a stale-looking list always raises.
	if t, ok := timefmt.Parse(q.GeneratedUtc); ok {
		head += subtleStyle.Render("  ·  read " + timefmt.Age(t))
	}
	if m.ownerQueueErr != "" {
		head += "\n" + warnStyle.Render("last poll failed") + subtleStyle.Render(" — "+m.ownerQueueErr)
	}
	return head
}

func (m Model) renderOwnerQueueItem(n int, it api.OwnerQueueItemDto, w int) string {
	glyph, gs := ownerKindGlyph(it.Kind)
	lines := []string{
		subtleStyle.Render(fmt.Sprintf("%2d.", n)) + " " + gs.Render(glyph) + " " +
			textStyle.Render(it.Title),
		"     " + subtleStyle.Render(it.Kind+" · "+ownerAge(it.AgeSeconds)),
		"     " + subtleStyle.Render("unblocks ") + textStyle.Render(it.Unblocks),
	}
	if it.Detail != nil && strings.TrimSpace(*it.Detail) != "" {
		lines = append(lines, "     "+subtleStyle.Render(*it.Detail))
	}
	lines = append(lines, "     "+ownerCommand(it.Command))
	for i, l := range lines {
		lines[i] = lipgloss.NewStyle().MaxWidth(w).Render(l)
	}
	return strings.Join(lines, "\n")
}
