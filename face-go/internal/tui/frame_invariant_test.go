package tui

// The frame invariant: View() output is never taller than the window. One overgrown pane row —
// a multi-paragraph transcript event, a grown attention banner, a disconnected notice — must clip
// inside its pane, not push the bottom bar and the pinned live tail below the fold (the owner's
// 2026-07-17 dogfood: "I don't see the footer... I don't think I see the ending").

import (
	"fmt"
	"strings"
	"testing"
	"time"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

func frameHeight(t *testing.T, m tea.Model) int {
	t.Helper()
	return len(strings.Split(strings.TrimRight(stripANSI(m.View().Content), "\n"), "\n"))
}

func TestFrameNeverExceedsWindowHeight(t *testing.T) {
	sizes := []struct{ w, h int }{{132, 40}, {110, 34}, {100, 30}, {80, 24}}
	for _, size := range sizes {
		t.Run(fmt.Sprintf("%dx%d", size.w, size.h), func(t *testing.T) {
			m := newGoldenModel(size.w, size.h)

			// Worst case on every axis at once: the strip grows an attention banner AND a
			// disconnected notice, while the transcript takes multi-paragraph thinking + text
			// events (the live-run shape that first overflowed the frame).
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

			if got := frameHeight(t, m); got > size.h {
				t.Errorf("frame is %d rows for a %d-row window — bottom bar and live tail are off-screen", got, size.h)
			}

			// The bottom bar must actually be the last visible row.
			frame := stripANSI(m.View().Content)
			rows := strings.Split(strings.TrimRight(frame, "\n"), "\n")
			last := rows[len(rows)-1]
			if !strings.Contains(last, "quit") && !strings.Contains(last, "cmd") {
				t.Errorf("last visible row is not the bottom bar: %q", last)
			}
		})
	}
}
