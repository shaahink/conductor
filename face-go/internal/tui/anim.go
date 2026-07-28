package tui

import (
	"math"
	"time"

	tea "charm.land/bubbletea/v2"
	"github.com/charmbracelet/harmonica"

	"conductor-face-go/internal/widgets"
)

// toastSpring: fast, near-critically-damped (0.9) so a toast reveals itself briskly with no
// distracting bounce. FPS(30) is plenty smooth for a short text reveal and keeps the extra
// tick cheap while it's running.
var toastSpring = harmonica.NewSpring(harmonica.FPS(30), 8.0, 0.9)

type toastAnimState struct {
	pos, vel float64
}

type MsgAnimTick struct{}

func cmdAnimTick() tea.Cmd {
	return tea.Tick(33*time.Millisecond, func(time.Time) tea.Msg {
		return MsgAnimTick{}
	})
}

// addToast appends a toast and starts its spring-driven reveal animation, returning a Cmd to
// (re)arm the animation ticker if this is the only thing currently animating. Every toast
// creation site should go through this rather than appending widgets.NewToast directly.
func (m *Model) addToast(text string, kind widgets.ToastKind) tea.Cmd {
	t := widgets.NewToast(text, kind)
	m.toasts = append(m.toasts, t)

	wasEmpty := len(m.toastAnims) == 0
	if m.toastAnims == nil {
		m.toastAnims = make(map[int]*toastAnimState)
	}
	m.toastAnims[t.ID] = &toastAnimState{}

	if wasEmpty {
		return cmdAnimTick()
	}
	return nil
}

// advanceToastAnims steps every active toast's reveal spring one frame and reports whether any
// animation is still in flight (so the caller knows whether to re-arm the tick).
func (m *Model) advanceToastAnims() bool {
	for id, anim := range m.toastAnims {
		pos, vel := toastSpring.Update(anim.pos, anim.vel, 1.0)
		anim.pos, anim.vel = pos, vel
		settled := math.Abs(pos-1) < 0.01 && math.Abs(vel) < 0.01

		reveal := pos
		if settled {
			reveal = 1
		}
		for i := range m.toasts {
			if m.toasts[i].ID == id {
				m.toasts[i].Reveal = reveal
				break
			}
		}

		if settled {
			delete(m.toastAnims, id)
		}
	}
	return len(m.toastAnims) > 0
}
