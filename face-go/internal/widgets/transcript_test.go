package widgets

import (
	"fmt"
	"regexp"
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

var ansiRe = regexp.MustCompile(`\x1b\[[0-9;?]*[a-zA-Z]`)

func plainView(m TranscriptModel) string {
	return ansiRe.ReplaceAllString(m.View(), "")
}

func filledTranscript(n, height int) TranscriptModel {
	m := NewTranscript()
	m.Width = 80
	m.Height = height
	for i := 0; i < n; i++ {
		m = m.Update(MsgAppendLine{Line: api.TranscriptLineDto{Seq: int64(i), Kind: "agent", Text: fmt.Sprintf("line %03d", i)}})
	}
	return m
}

// Scroll-up must step back from the tail — not teleport to the top of the buffer (the old
// offset-from-top semantics did exactly that on the first keypress).
func TestScrollUpStepsBackFromTail(t *testing.T) {
	m := filledTranscript(50, 10)

	if v := plainView(m); !strings.Contains(v, "line 049") {
		t.Fatalf("fresh transcript should tail at the newest line, got:\n%s", v)
	}

	m = m.Update(MsgScrollUp)
	v := plainView(m)
	if strings.Contains(v, "line 049") {
		t.Errorf("after one scroll-up the newest line should be out of view:\n%s", v)
	}
	if strings.Contains(v, "line 000") {
		t.Errorf("one scroll-up must NOT jump to the top of the buffer:\n%s", v)
	}
	if !strings.Contains(v, "line 046") {
		t.Errorf("expected the window to sit a few lines above the tail:\n%s", v)
	}
	if !strings.Contains(v, "lines below") {
		t.Errorf("expected a scrolled-back indicator:\n%s", v)
	}

	m = m.Update(MsgScrollEnd)
	if v := plainView(m); !strings.Contains(v, "line 049") || strings.Contains(v, "lines below") {
		t.Errorf("end should re-pin to the live tail:\n%s", v)
	}
}

func TestScrollUpClampsAtTop(t *testing.T) {
	m := filledTranscript(20, 10)
	for i := 0; i < 100; i++ {
		m = m.Update(MsgScrollUp)
	}
	v := plainView(m)
	if !strings.Contains(v, "line 000") {
		t.Errorf("scrolling far back should reach (and stop at) the oldest line:\n%s", v)
	}
}

func TestAutoScrollResumesOnNewLinesAtTail(t *testing.T) {
	m := filledTranscript(30, 10)
	m = m.Update(MsgScrollUp)
	m = m.Update(MsgScrollDown) // back to the tail → autoscroll re-arms
	m = m.Update(MsgAppendLine{Line: api.TranscriptLineDto{Seq: 99, Kind: "agent", Text: "line NEW"}})
	if v := plainView(m); !strings.Contains(v, "line NEW") {
		t.Errorf("returning to the tail should re-enable live tailing:\n%s", v)
	}
}

func TestSearchJumpCentresMatch(t *testing.T) {
	m := filledTranscript(60, 10)
	m = m.Update(MsgSetSearch{Query: "line 005"})
	if len(m.SearchMatches) != 1 {
		t.Fatalf("expected exactly one match, got %d", len(m.SearchMatches))
	}
	if v := plainView(m); !strings.Contains(v, "line 005") {
		t.Errorf("setting a search should scroll its first match into view:\n%s", v)
	}
}

func TestSearchHighlightMarksMatch(t *testing.T) {
	m := filledTranscript(5, 10)
	m = m.Update(MsgSetSearch{Query: "line 003"})
	raw := m.View()
	if !strings.Contains(ansiRe.ReplaceAllString(raw, ""), "line 003") {
		t.Fatal("match line should be visible")
	}
	// The highlight style paints a background — assert the raw output styles the match differently
	// from its neighbours (an ANSI background sequence adjacent to the matched text).
	if !regexp.MustCompile(`\x1b\[[0-9;]*m?line 003`).MatchString(raw) {
		t.Errorf("expected the matched text to carry its own style run:\n%q", raw)
	}
}
