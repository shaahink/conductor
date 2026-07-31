package tui

// U3.2's sweep: EVERY tab, at each of the spec's three sizes, under worst-case state.
//
// On sizes — this ADDS, it does not replace. `TestGoldenSizes` keeps 80x24 / 120x30 / 200x50 (the M5
// truth gate; 200x50 is the only wide coverage there is, and the spec's parenthetical "the goldens'
// sizes" was simply wrong about what they are). This file adds the spec's 132x40 / 100x30 / 80x24
// across every tab (thirteen when it was written, ten since SF1.3 — the loop is driven by tabCount,
// so it never needed the number), which is the axis nothing covered: every size test before this
// rendered the DEFAULT tab only. SF1.3 added a second loop for the folded MODES, which are panes the
// tabCount loop can no longer reach.
//
// That gap was not theoretical. TestFrameNeverExceedsWindowHeight builds 30 multi-paragraph
// transcript events as its worst case and asserts the frame still fits — but newGoldenModel opens on
// Home and nothing in that test switches tabs, so the transcript it is testing was never drawn. The
// regression test for dogfood appendix item 13 has been passing on a frame that cannot exhibit the
// bug. This sweep drives each tab by its real mnemonic through handleKey, so what it measures is
// what a user sees.

import (
	"fmt"
	"strings"
	"testing"
	"time"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// glitchSizes are the spec's three: the owner's terminal, a laptop split, and the narrow floor.
var glitchSizes = []struct{ w, h int }{{132, 40}, {100, 30}, {80, 24}}

// worstCaseModel is the state that has historically broken layout: an attention banner AND a
// disconnected notice growing the agent strip, over a transcript of multi-paragraph events.
func worstCaseModel(w, h int) tea.Model {
	m := newGoldenModel(w, h)

	st := fixedState()
	st.Status = "NeedsAttention"
	reason := "verifier score 74 < 80 — see session #12 findings and the gate output for details"
	st.AttentionReason = &reason
	m, _ = m.Update(MsgStateUpdated{State: st})
	m, _ = m.Update(MsgEventsConnChanged{Connected: false})

	at := time.Date(2026, 7, 17, 1, 0, 0, 0, time.UTC)
	for i := 0; i < 30; i++ {
		m, _ = m.Update(MsgTranscriptLine{Line: api.TranscriptLineDto{
			Seq: int64(100 + i), Ts: at, SessionId: "1", Kind: "thinking",
			Text: "First paragraph of a long thought.\n\nSecond paragraph after a blank line.\nThird line of reasoning that keeps going for a while to make the row long.",
		}})
		m, _ = m.Update(MsgTranscriptLine{Line: api.TranscriptLineDto{
			Seq: int64(200 + i), Ts: at, SessionId: "1", Kind: "text",
			Text: "An actual agent message.\nWith a second line.",
		}})
	}
	return m
}

// TestEveryTabFitsEverySize is the mechanical half of the glitch pass. A frame that overflows its
// window, or a row wider than the terminal, is not a judgement call — it corrupts the display, and
// no amount of reading frames catches it at every size by eye.
func TestEveryTabFitsEverySize(t *testing.T) {
	for _, size := range glitchSizes {
		for i := 0; i < int(tabCount); i++ {
			t.Run(fmt.Sprintf("%s_%dx%d", tabNames[i], size.w, size.h), func(t *testing.T) {
				m := worstCaseModel(size.w, size.h)
				m = asModel(mustHandle(asModel(m).handleKey(tabKey[i])))
				if got := asModel(m).tab; got != MainTab(i) {
					t.Fatalf("key %q landed on tab %v, want %v — the sweep is not testing what it names",
						tabKey[i], tabNames[got], tabNames[i])
				}

				frame := stripANSI(asModel(m).View().Content)
				rows := strings.Split(strings.TrimRight(frame, "\n"), "\n")

				if len(rows) > size.h {
					t.Errorf("frame is %d rows for a %d-row window — the bottom bar and the live "+
						"tail are off-screen", len(rows), size.h)
				}
				for n, row := range rows {
					if got := len([]rune(strings.TrimRight(row, " "))); got > size.w {
						t.Errorf("row %d is %d cols wide in a %d-col window: %q", n, got, size.w, row)
					}
				}
				if last := rows[len(rows)-1]; !strings.Contains(last, "quit") && !strings.Contains(last, "cmd") {
					t.Errorf("last visible row is not the bottom bar: %q", last)
				}
			})
		}
	}
}

// SF1.3 folded two tabs into modes of other tabs, and the sweep above is driven by tabKey — so a
// folded mode is a pane the sweep stopped covering the moment its tab stopped existing. The old
// Console tab WAS in that loop; Agent's raw stream and History's spine must not quietly fall out of
// it. Same mechanical checks, reached by the same real router.
func TestEveryFoldedModeFitsEverySize(t *testing.T) {
	modes := []struct {
		name string
		keys []string
	}{
		{"AgentRaw", []string{"c"}},              // the folded Console
		{"HistorySpine", []string{"t"}},          // the folded Timeline
		{"HistoryArrow", []string{"s", "right"}}, // …and reached the documented other way
	}
	for _, size := range glitchSizes {
		for _, mode := range modes {
			t.Run(fmt.Sprintf("%s_%dx%d", mode.name, size.w, size.h), func(t *testing.T) {
				m := worstCaseModel(size.w, size.h)
				// Worst case for a raw stream is a full buffer of over-wide lines.
				for i := 0; i < 200; i++ {
					m, _ = m.Update(MsgConsoleLine{Line: api.ConsoleLineDto{Seq: int64(i + 1),
						Text: strings.Repeat("raw stdout that is far wider than any terminal ", 6)}})
				}
				m, _ = m.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: fixedTimeline()}})
				for _, k := range mode.keys {
					m = asModel(mustHandle(asModel(m).handleKey(k)))
				}

				frame := stripANSI(asModel(m).View().Content)
				rows := strings.Split(strings.TrimRight(frame, "\n"), "\n")
				if len(rows) > size.h {
					t.Errorf("%s frame is %d rows for a %d-row window", mode.name, len(rows), size.h)
				}
				for n, row := range rows {
					if got := len([]rune(strings.TrimRight(row, " "))); got > size.w {
						t.Errorf("row %d is %d cols wide in a %d-col window: %q", n, got, size.w, row)
					}
				}
				body, _ := asModel(m).paneView()
				if strings.TrimSpace(stripANSI(body)) == "" {
					t.Errorf("the %s pane renders completely blank", mode.name)
				}
			})
		}
	}
}

// TestNoTabRendersEmptyAtAnySize: a pane that renders blank is indistinguishable from a broken fetch
// (dogfood appendix item 5 — the Kanban board read as silent emptiness while the sidebar showed a
// full plan). Every pane owes the reader SOMETHING, even when it has no data.
func TestNoTabRendersEmptyAtAnySize(t *testing.T) {
	for _, size := range glitchSizes {
		for i := 0; i < int(tabCount); i++ {
			t.Run(fmt.Sprintf("%s_%dx%d", tabNames[i], size.w, size.h), func(t *testing.T) {
				// No plan, no tasks, no sessions: the "attached but nothing has happened yet" state
				// a real run genuinely passes through, and the one that rendered blank.
				var m tea.Model = New(fakeSource{}, false, "http://127.0.0.1:4317")
				m, _ = m.Update(tea.WindowSizeMsg{Width: size.w, Height: size.h})
				m = asModel(mustHandle(asModel(m).handleKey(tabKey[i])))

				body, _ := asModel(m).paneView()
				if strings.TrimSpace(stripANSI(body)) == "" {
					t.Errorf("the %s pane renders completely blank with no data — a reader cannot "+
						"tell that from a failed fetch; say what is missing in-pane", tabNames[i])
				}
			})
		}
	}
}
