package tui

import (
	"strings"

	"github.com/charmbracelet/glamour"
)

// renderMarkdown renders md as terminal markdown (dark theme, wrapped to width) for detail
// panes that show free-text prose an agent likely wrote — e.g. a session's result summary —
// as opposed to the dense, single-line-per-row lists elsewhere in this UI, which stay plain
// text on purpose. Falls back to the raw text if Glamour can't render it; a markdown-rendering
// hiccup must never blank out a modal.
func renderMarkdown(md string, width int) string {
	if strings.TrimSpace(md) == "" {
		return md
	}
	if width < 20 {
		width = 20
	}
	r, err := glamour.NewTermRenderer(
		glamour.WithStandardStyle("dark"),
		glamour.WithWordWrap(width),
	)
	if err != nil {
		return md
	}
	out, err := r.Render(md)
	if err != nil {
		return md
	}
	return strings.TrimRight(out, "\n")
}
