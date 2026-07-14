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
	}
}

func RenderToasts(toasts []Toast) string {
	var sb strings.Builder
	for _, t := range toasts {
		var style lipgloss.Style
		var prefix string
		switch t.Kind {
		case ToastSuccess:
			style = lipgloss.NewStyle().Foreground(colorDone)
			prefix = "\u2713 "
		case ToastError:
			style = lipgloss.NewStyle().Foreground(colorFail)
			prefix = "\u2717 "
		case ToastWarn:
			style = lipgloss.NewStyle().Foreground(colorWarn)
			prefix = "\u26A0 "
		default:
			style = lipgloss.NewStyle().Foreground(colorAccent)
			prefix = "\u2139 "
		}
		sb.WriteString(style.Render(prefix + t.Text))
		sb.WriteByte('\n')
	}
	return strings.TrimRight(sb.String(), "\n")
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
