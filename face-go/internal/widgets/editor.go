package widgets

import (
	"fmt"
	"strings"

	"charm.land/lipgloss/v2"
)

// TextArea is a minimal multi-line editor with a real cursor — insert/delete at the caret, arrow
// navigation across lines, home/end, and page scrolling. The old face inputs were append/backspace
// -at-end only, so you could never fix a typo in the middle of a template or a SQL query; this fixes
// that everywhere it's wired (template editor, report SQL).
type TextArea struct {
	lines  []string // logical lines, no trailing newline
	cx     int      // caret column, as a rune index within the current line [0..len]
	cy     int      // caret row [0..len(lines)-1]
	scroll int      // index of the first visible row
	Width  int
	Height int
}

func NewTextArea(content string, width, height int) TextArea {
	t := TextArea{Width: width, Height: height}
	t.SetValue(content)
	return t
}

func (t *TextArea) SetValue(content string) {
	t.lines = strings.Split(strings.ReplaceAll(content, "\r\n", "\n"), "\n")
	if len(t.lines) == 0 {
		t.lines = []string{""}
	}
	t.cy = len(t.lines) - 1
	t.cx = len([]rune(t.lines[t.cy]))
	t.clampScroll()
}

// Value returns the current buffer with lines rejoined by "\n".
func (t TextArea) Value() string { return strings.Join(t.lines, "\n") }

func (t *TextArea) SetSize(width, height int) {
	t.Width, t.Height = width, height
	t.clampScroll()
}

// Update applies one key. Callers pass Bubble Tea key names ("left", "enter", "space", or a literal
// rune) and get the mutated area back. Unknown keys are ignored, so a caller can intercept its own
// keys (ctrl+s, esc) before delegating here.
func (t TextArea) Update(key string) TextArea {
	cur := []rune(t.lines[t.cy])
	switch key {
	case "left":
		if t.cx > 0 {
			t.cx--
		} else if t.cy > 0 {
			t.cy--
			t.cx = len([]rune(t.lines[t.cy]))
		}
	case "right":
		if t.cx < len(cur) {
			t.cx++
		} else if t.cy < len(t.lines)-1 {
			t.cy++
			t.cx = 0
		}
	case "up":
		if t.cy > 0 {
			t.cy--
			t.cx = min(t.cx, len([]rune(t.lines[t.cy])))
		}
	case "down":
		if t.cy < len(t.lines)-1 {
			t.cy++
			t.cx = min(t.cx, len([]rune(t.lines[t.cy])))
		}
	case "home":
		t.cx = 0
	case "end":
		t.cx = len(cur)
	case "pgup":
		t.cy = max(0, t.cy-t.pageStep())
		t.cx = min(t.cx, len([]rune(t.lines[t.cy])))
	case "pgdown":
		t.cy = min(len(t.lines)-1, t.cy+t.pageStep())
		t.cx = min(t.cx, len([]rune(t.lines[t.cy])))
	case "enter":
		head, tail := string(cur[:t.cx]), string(cur[t.cx:])
		t.lines[t.cy] = head
		t.lines = insertAt(t.lines, t.cy+1, tail)
		t.cy++
		t.cx = 0
	case "backspace":
		if t.cx > 0 {
			t.lines[t.cy] = string(cur[:t.cx-1]) + string(cur[t.cx:])
			t.cx--
		} else if t.cy > 0 {
			prev := []rune(t.lines[t.cy-1])
			t.cx = len(prev)
			t.lines[t.cy-1] = string(prev) + string(cur)
			t.lines = removeAt(t.lines, t.cy)
			t.cy--
		}
	case "delete":
		if t.cx < len(cur) {
			t.lines[t.cy] = string(cur[:t.cx]) + string(cur[t.cx+1:])
		} else if t.cy < len(t.lines)-1 {
			t.lines[t.cy] = string(cur) + t.lines[t.cy+1]
			t.lines = removeAt(t.lines, t.cy+1)
		}
	default:
		if ch, ok := typedRune(key); ok {
			t.lines[t.cy] = string(cur[:t.cx]) + ch + string(cur[t.cx:])
			t.cx++
		}
	}
	t.clampScroll()
	return t
}

func (t *TextArea) pageStep() int {
	if t.Height > 1 {
		return t.Height - 1
	}
	return 1
}

func (t *TextArea) clampScroll() {
	if t.cy < t.scroll {
		t.scroll = t.cy
	}
	if t.Height > 0 && t.cy >= t.scroll+t.Height {
		t.scroll = t.cy - t.Height + 1
	}
	if t.scroll < 0 {
		t.scroll = 0
	}
}

var editorCursor = lipgloss.NewStyle().Reverse(true)

// View renders the visible window with a reverse-video caret. The cursor line scrolls horizontally to
// keep the caret in view; other lines clip from column 0. A scroll hint sits on the last row when the
// buffer is taller than the viewport.
func (t TextArea) View() string {
	h := t.Height
	if h < 1 {
		h = 1
	}
	end := min(t.scroll+h, len(t.lines))
	var out []string
	for row := t.scroll; row < end; row++ {
		out = append(out, t.renderLine(row))
	}
	for len(out) < h {
		out = append(out, "")
	}
	if len(t.lines) > h {
		hint := dimStyle.Render(FmtLinePos(t.cy+1, len(t.lines)))
		out[len(out)-1] = clipLine(out[len(out)-1], t.Width-lipgloss.Width(hint)-1) + " " + hint
	}
	return strings.Join(out, "\n")
}

func (t TextArea) renderLine(row int) string {
	runes := []rune(t.lines[row])
	w := t.Width
	if w < 1 {
		w = 1
	}
	if row != t.cy {
		return clipLine(t.lines[row], w)
	}
	// Horizontal scroll so the caret is visible on its own line.
	left := 0
	if t.cx >= w {
		left = t.cx - w + 1
	}
	if left > len(runes) {
		left = len(runes)
	}
	seg := runes[left:]
	rel := t.cx - left
	// Split into before/at/after around the caret; the caret cell is one rune (or a trailing space).
	before, at, after := "", " ", ""
	if rel < len(seg) {
		before = string(seg[:rel])
		at = string(seg[rel])
		after = string(seg[rel+1:])
	} else {
		before = string(seg)
	}
	line := clipLine(before, w-1) + editorCursor.Render(at) + after
	return lipgloss.NewStyle().MaxWidth(w).Render(line)
}

func clipLine(s string, w int) string {
	if w < 1 {
		return ""
	}
	return lipgloss.NewStyle().MaxWidth(w).Render(s)
}

// FmtLinePos formats a "ln A/B" caret-position hint.
func FmtLinePos(line, total int) string {
	return fmt.Sprintf("ln %d/%d", line, total)
}

func insertAt(s []string, i int, v string) []string {
	s = append(s, "")
	copy(s[i+1:], s[i:])
	s[i] = v
	return s
}

func removeAt(s []string, i int) []string {
	return append(s[:i], s[i+1:]...)
}

// typedRune returns the literal character a key inserts, matching the tui package's typedChar: Bubble
// Tea reports the spacebar as "space", and only single-rune names are printable input.
func typedRune(key string) (string, bool) {
	if key == "space" {
		return " ", true
	}
	if r := []rune(key); len(r) == 1 {
		return key, true
	}
	return "", false
}
