package tui

import (
	"testing"

	tea "charm.land/bubbletea/v2"
)

func isQuit(cmd tea.Cmd) bool {
	if cmd == nil {
		return false
	}
	_, ok := cmd().(tea.QuitMsg)
	return ok
}

// U3.3: ctrl+c is a double-tap. The first tap does NOT quit — it arms and drops a hint toast; the
// second, while armed, quits. `q` stays the unguarded, single-press quit.
func TestCtrlCDoubleTapToQuit(t *testing.T) {
	m := newTestModel()

	tm, cmd := m.handleKey("ctrl+c")
	m = asModel(tm)
	if isQuit(cmd) {
		t.Fatal("a single ctrl+c must not quit")
	}
	if !m.quitArmed {
		t.Fatal("the first ctrl+c should arm quit")
	}
	if len(m.toasts) != 1 {
		t.Fatalf("the first ctrl+c should show a hint toast, got %d toasts", len(m.toasts))
	}

	_, cmd = m.handleKey("ctrl+c")
	if !isQuit(cmd) {
		t.Fatal("a second ctrl+c while armed must quit")
	}
}

// Any other key between the two taps disarms — a stray ctrl+c never leaves the app one keystroke
// from exit.
func TestOtherKeyDisarmsQuit(t *testing.T) {
	m := newTestModel()

	m = asModel(mustHandle(m.handleKey("ctrl+c")))
	if !m.quitArmed {
		t.Fatal("first ctrl+c should arm")
	}
	m = asModel(mustHandle(m.handleKey("j"))) // any real key
	if m.quitArmed {
		t.Fatal("a keystroke of real work must disarm the pending quit")
	}

	_, cmd := m.handleKey("ctrl+c")
	if isQuit(cmd) {
		t.Fatal("after a disarm, one ctrl+c must arm again — not quit")
	}
}

// `q` is the explicit, unguarded quit — no double-tap.
func TestQKeyQuitsImmediately(t *testing.T) {
	m := newTestModel()
	if _, cmd := m.handleKey("q"); !isQuit(cmd) {
		t.Fatal("q should quit on the first press")
	}
}
