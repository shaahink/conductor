package widgets

import "testing"

func drive(ed TextArea, keys ...string) TextArea {
	for _, k := range keys {
		ed = ed.Update(k)
	}
	return ed
}

// The whole point of the editor: fix a typo in the MIDDLE of the buffer, which the old append-only
// inputs could never do.
func TestTextAreaInsertMidString(t *testing.T) {
	ed := NewTextArea("helo", 40, 3) // caret lands at end
	ed = drive(ed, "left", "left")   // between 'e' and 'l'
	ed = ed.Update("l")
	if got := ed.Value(); got != "hello" {
		t.Fatalf("mid-string insert: got %q, want %q", got, "hello")
	}
}

func TestTextAreaBackspaceAtCaret(t *testing.T) {
	ed := NewTextArea("helllo", 40, 3)
	ed = drive(ed, "left", "left", "backspace") // delete one of the middle 'l's
	if got := ed.Value(); got != "hello" {
		t.Fatalf("backspace at caret: got %q, want %q", got, "hello")
	}
}

func TestTextAreaDeleteForward(t *testing.T) {
	ed := NewTextArea("hello", 40, 3)
	ed = drive(ed, "home", "delete") // remove the leading 'h'
	if got := ed.Value(); got != "ello" {
		t.Fatalf("forward delete: got %q, want %q", got, "ello")
	}
}

func TestTextAreaEnterSplitsAndBackspaceMerges(t *testing.T) {
	ed := NewTextArea("abcd", 40, 3)
	ed = drive(ed, "home", "right", "right", "enter") // split after "ab"
	if got := ed.Value(); got != "ab\ncd" {
		t.Fatalf("enter split: got %q, want %q", got, "ab\ncd")
	}
	// caret is at the start of line "cd"; backspace merges the two lines back.
	ed = ed.Update("backspace")
	if got := ed.Value(); got != "abcd" {
		t.Fatalf("backspace merge: got %q, want %q", got, "abcd")
	}
}

func TestTextAreaVerticalNavigation(t *testing.T) {
	ed := NewTextArea("ab\ncd", 40, 3) // caret at end of "cd"
	ed = drive(ed, "up", "home")       // to start of "ab"
	ed = ed.Update("X")
	if got := ed.Value(); got != "Xab\ncd" {
		t.Fatalf("vertical nav insert: got %q, want %q", got, "Xab\ncd")
	}
}

// SetValue puts the caret at the end so typing continues the buffer, and Value round-trips newlines.
func TestTextAreaValueRoundTrip(t *testing.T) {
	ed := NewTextArea("line1\nline2\nline3", 40, 3)
	if got := ed.Value(); got != "line1\nline2\nline3" {
		t.Fatalf("round-trip: got %q", got)
	}
	ed = ed.Update("!")
	if got := ed.Value(); got != "line1\nline2\nline3!" {
		t.Fatalf("append after load: got %q", got)
	}
}

// The caret must render without panicking at the empty buffer, end-of-line, and mid-line — this is
// what the golden frames rely on.
func TestTextAreaViewDoesNotPanic(t *testing.T) {
	for _, content := range []string{"", "x", "a longer line that exceeds width", "a\nb\nc"} {
		ed := NewTextArea(content, 12, 2)
		_ = ed.View()
		_ = drive(ed, "home", "right").View()
	}
}
