package tui

import (
	"testing"

	"conductor-face-go/internal/widgets"
)

func TestAddToastStartsAtZeroRevealAndArmsTicker(t *testing.T) {
	m := newTestModel()

	cmd := m.addToast("hello", widgets.ToastInfo)
	if len(m.toasts) != 1 {
		t.Fatalf("expected 1 toast, got %d", len(m.toasts))
	}
	if m.toasts[0].Reveal != 0 {
		t.Errorf("expected fresh toast to start at Reveal 0, got %v", m.toasts[0].Reveal)
	}
	if cmd == nil {
		t.Fatal("expected the first toast to arm the animation ticker")
	}
	if _, ok := cmd().(MsgAnimTick); !ok {
		t.Fatal("expected the ticker command to produce MsgAnimTick")
	}
}

func TestAddToastDoesNotDoubleArmTicker(t *testing.T) {
	m := newTestModel()
	m.addToast("first", widgets.ToastInfo)

	cmd := m.addToast("second", widgets.ToastInfo)
	if cmd != nil {
		t.Error("expected no new ticker command while one is already animating")
	}
	if len(m.toastAnims) != 2 {
		t.Errorf("expected both toasts tracked as animating, got %d", len(m.toastAnims))
	}
}

func TestToastAnimationSettlesAndStopsReArming(t *testing.T) {
	m := newTestModel()
	m.addToast("settle me", widgets.ToastInfo)

	const maxTicks = 200 // spring settles in well under this at 30fps
	settled := false
	for i := 0; i < maxTicks; i++ {
		if !m.advanceToastAnims() {
			settled = true
			break
		}
	}
	if !settled {
		t.Fatal("expected the toast animation to settle within a bounded number of ticks")
	}
	if len(m.toastAnims) != 0 {
		t.Errorf("expected no animations left once settled, got %d", len(m.toastAnims))
	}
	if m.toasts[0].Reveal != 1 {
		t.Errorf("expected fully-settled toast to reveal at 1, got %v", m.toasts[0].Reveal)
	}
}
