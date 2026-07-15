package widgets

import (
	"strings"
	"time"

	"charm.land/lipgloss/v2"
)

type Toast struct {
	ID        int
	Text      string
	Kind      ToastKind
	CreatedAt time.Time
	// Reveal is a spring-animated 0..1 fraction of Text to show (typewriter-style entrance,
	// driven by the tui package). 0 means "just appeared", 1 means fully settled. Toasts built
	// directly via NewToast start at 0; a caller that doesn't drive the animation forward will
	// simply never reveal any text, so anything appending a Toast for real display should go
	// through tui.Model.addToast, which owns advancing this value every animation tick.
	Reveal float64
}

type ToastKind int

const (
	ToastInfo ToastKind = iota
	ToastSuccess
	ToastError
	ToastWarn
)

var (
	toastID int
)

func NewToast(text string, kind ToastKind) Toast {
	toastID++
	return Toast{
		ID:        toastID,
		Text:      text,
		Kind:      kind,
		CreatedAt: time.Now(),
		Reveal:    0,
	}
}

func RenderToasts(toasts []Toast) string {
	var sb strings.Builder
	for _, t := range toasts {
		var style lipgloss.Style
		var prefix string
		switch t.Kind {
		case ToastSuccess:
			style = lipgloss.NewStyle().Foreground(colGreen)
			prefix = "✓ "
		case ToastError:
			style = lipgloss.NewStyle().Foreground(colRed)
			prefix = "✗ "
		case ToastWarn:
			style = lipgloss.NewStyle().Foreground(colYellow)
			prefix = "⚠ "
		default:
			style = lipgloss.NewStyle().Foreground(colMauve)
			prefix = "ℹ "
		}
		sb.WriteString(style.Render(prefix + revealedText(t)))
		sb.WriteByte('\n')
	}
	return strings.TrimRight(sb.String(), "\n")
}

func revealedText(t Toast) string {
	if t.Reveal >= 1 {
		return t.Text
	}
	frac := t.Reveal
	if frac < 0 {
		frac = 0
	}
	runes := []rune(t.Text)
	n := int(float64(len(runes)) * frac)
	if n > len(runes) {
		n = len(runes)
	}
	return string(runes[:n])
}

func PruneToasts(toasts []Toast, maxAge time.Duration) []Toast {
	cutoff := time.Now().Add(-maxAge)
	var result []Toast
	for _, t := range toasts {
		if t.CreatedAt.After(cutoff) {
			result = append(result, t)
		}
	}
	return result
}
