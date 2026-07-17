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

// U3.3: consecutive thinking collapses to its LAST line plus a "+N lines (T to expand)" tail — the
// tail names both how much is hidden and the key that brings it back (the old counter said neither).
func TestCollapsedThinkingShowsExpandTail(t *testing.T) {
	m := NewTranscript()
	m.Width, m.Height = 80, 20
	for i := 0; i < 5; i++ {
		m = m.Update(MsgAppendLine{Line: api.TranscriptLineDto{Seq: int64(i), Kind: "thinking", Text: fmt.Sprintf("thought %d", i)}})
	}
	m = m.Update(MsgAppendLine{Line: api.TranscriptLineDto{Seq: 9, Kind: "agent", Text: "the actual message"}})

	v := plainView(m)
	if !strings.Contains(v, "thought 4") {
		t.Errorf("collapsed run should keep its LAST thought:\n%s", v)
	}
	for i := 0; i < 4; i++ {
		if strings.Contains(v, fmt.Sprintf("thought %d", i)) {
			t.Errorf("collapsed run must hide earlier thoughts, found 'thought %d':\n%s", i, v)
		}
	}
	if !strings.Contains(v, "+4 lines (T to expand)") {
		t.Errorf("expected a '+4 lines (T to expand)' tail under the collapsed run:\n%s", v)
	}
	if !strings.Contains(v, "the actual message") {
		t.Errorf("the real agent message must survive the collapse:\n%s", v)
	}

	// T expands: every thought is shown and the tail disappears.
	full := plainView(m.Update(MsgToggleThinking))
	if strings.Contains(full, "T to expand") {
		t.Errorf("expanded view must drop the collapse tail:\n%s", full)
	}
	for i := 0; i < 5; i++ {
		if !strings.Contains(full, fmt.Sprintf("thought %d", i)) {
			t.Errorf("expanded view should show every thought, missing 'thought %d':\n%s", i, full)
		}
	}
}

// A single thinking line is already the whole thought — no run to summarise, so no tail.
func TestLoneThinkingHasNoTail(t *testing.T) {
	m := NewTranscript()
	m.Width, m.Height = 80, 10
	m = m.Update(MsgAppendLine{Line: api.TranscriptLineDto{Seq: 1, Kind: "thinking", Text: "one thought"}})
	m = m.Update(MsgAppendLine{Line: api.TranscriptLineDto{Seq: 2, Kind: "agent", Text: "message"}})
	if v := plainView(m); strings.Contains(v, "to expand") {
		t.Errorf("a lone thinking line should not grow a collapse tail:\n%s", v)
	}
}

// U3.3: a tool line renders name-first — the tool name and its one-line argument as separate,
// differently-styled runs (Claude Code's convention: bold name, dim arg).
func TestToolLineSplitsNameFromArg(t *testing.T) {
	m := NewTranscript()
	m.Width, m.Height = 80, 10
	m = m.Update(MsgAppendLine{Line: api.TranscriptLineDto{Seq: 1, Kind: "tool", Text: "read src/Conductor/Foo.cs"}})
	raw := m.View()
	if !strings.Contains(plainView(m), "read src/Conductor/Foo.cs") {
		t.Fatalf("tool line text should be present:\n%s", plainView(m))
	}
	// Name and arg must be distinct style runs: an ANSI reset/sequence falls between "read" and its
	// argument. A single-run render would keep them in one uninterrupted colour.
	if !regexp.MustCompile(`read\x1b\[[0-9;]*m`).MatchString(raw) {
		t.Errorf("expected the tool name to close its own style run before the argument:\n%q", raw)
	}
}

func TestSplitToolCall(t *testing.T) {
	for _, tc := range []struct{ in, name, arg string }{
		{"read src/Foo.cs", "read", "src/Foo.cs"},
		{"bash", "bash", ""},
		{"grep  spaced   arg", "grep", "spaced   arg"},
		{"  edit  file.go ", "edit", "file.go"},
	} {
		if name, arg := splitToolCall(tc.in); name != tc.name || arg != tc.arg {
			t.Errorf("splitToolCall(%q) = (%q, %q), want (%q, %q)", tc.in, name, arg, tc.name, tc.arg)
		}
	}
}

// U3.3: the prefix vocabulary follows the RESOLVED provider — and an unknown/empty provider gets
// the neutral house set, never a guess at "probably claude".
func TestGlyphsFollowProvider(t *testing.T) {
	if glyphsFor("claude") != glyphsClaude {
		t.Error("claude provider should use the claude glyph set")
	}
	if glyphsFor("OpenCode") != glyphsOpencode {
		t.Error("provider match should be case-insensitive and pick opencode's set")
	}
	for _, p := range []string{"", "text", "some-future-cli"} {
		if glyphsFor(p) != glyphsHouse {
			t.Errorf("provider %q should fall back to the neutral house set, not guess a CLI", p)
		}
	}
}

// The provider selects glyphs at render time — switching it repaints the transcript without
// touching the buffered lines.
func TestTranscriptProviderSwitchesGlyphs(t *testing.T) {
	m := NewTranscript()
	m.Width, m.Height = 80, 10
	m = m.Update(MsgAppendLine{Line: api.TranscriptLineDto{Seq: 1, Kind: "tool", Text: "read foo"}})

	m.Provider = "claude"
	if !strings.Contains(plainView(m), glyphsClaude.tool) {
		t.Errorf("claude provider should render its tool glyph %q:\n%s", glyphsClaude.tool, plainView(m))
	}
	m.Provider = "opencode"
	oc := plainView(m)
	if !strings.Contains(oc, glyphsOpencode.tool) {
		t.Errorf("opencode provider should render its tool glyph %q:\n%s", glyphsOpencode.tool, oc)
	}
	if strings.Contains(oc, glyphsClaude.tool) {
		t.Errorf("opencode render must not carry claude's tool glyph:\n%s", oc)
	}
}
