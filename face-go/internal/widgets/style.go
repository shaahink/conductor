package widgets

import (
	"image/color"
	"strings"

	"charm.land/lipgloss/v2"
)

// conductor-face v3 palette — Catppuccin Mocha. A single cohesive scheme replaces the ad-hoc GitHub
// hexes so every pane, badge, and border reads as one system in both the sidebar and the main panes.
var (
	colBase     = lipgloss.Color("#1E1E2E") // window background
	colMantle   = lipgloss.Color("#181825") // panels: top/bottom bars, tab strip
	colSurface  = lipgloss.Color("#313244") // borders, rules
	colSurface1 = lipgloss.Color("#45475A") // selection background
	colOverlay  = lipgloss.Color("#6C7086") // muted text
	colText     = lipgloss.Color("#CDD6F4") // primary text

	colMauve  = lipgloss.Color("#CBA6F7") // accent / brand / selection
	colBlue   = lipgloss.Color("#89B4FA") // active / info
	colGreen  = lipgloss.Color("#A6E3A1") // success / done
	colRed    = lipgloss.Color("#F38BA8") // fail / destructive
	colYellow = lipgloss.Color("#F9E2AF") // warn / running
	colPeach  = lipgloss.Color("#FAB387") // cost / attention
	colTeal   = lipgloss.Color("#94E2D5") // tools
	colSky    = lipgloss.Color("#89DCEB") // system

	colPending = lipgloss.Color("#585B70") // todo / pending
	colSkipped = lipgloss.Color("#7F849C") // skipped / thinking
)

var (
	brandStyle        = lipgloss.NewStyle().Foreground(colMauve).Bold(true)
	sidebarTitleStyle = lipgloss.NewStyle().Foreground(colMauve).Bold(true)

	stageDoneStyle    = lipgloss.NewStyle().Foreground(colGreen)
	stageActiveStyle  = lipgloss.NewStyle().Foreground(colBlue).Bold(true)
	stageFailStyle    = lipgloss.NewStyle().Foreground(colRed)
	stageTodoStyle    = lipgloss.NewStyle().Foreground(colPending)
	stageSkippedStyle = lipgloss.NewStyle().Foreground(colSkipped)

	txThinkingStyle = lipgloss.NewStyle().Foreground(colSkipped).Italic(true)
	txToolStyle     = lipgloss.NewStyle().Foreground(colTeal)
	txResultStyle   = lipgloss.NewStyle().Foreground(colGreen)
	txStderrStyle   = lipgloss.NewStyle().Foreground(colRed)
	txSystemStyle   = lipgloss.NewStyle().Foreground(colSky)
	txAgentStyle    = lipgloss.NewStyle().Foreground(colText)
	txRawStyle      = lipgloss.NewStyle().Foreground(colOverlay)
	txTimeStyle     = lipgloss.NewStyle().Foreground(colPending)
	txMatchStyle    = lipgloss.NewStyle().Background(colYellow).Foreground(colBase)

	gatePassStyle    = lipgloss.NewStyle().Foreground(colGreen)
	gateRunningStyle = lipgloss.NewStyle().Foreground(colBlue)
	gateFailStyle    = lipgloss.NewStyle().Foreground(colRed)
	gatePendingStyle = lipgloss.NewStyle().Foreground(colPending)
	gateSkipStyle    = lipgloss.NewStyle().Foreground(colSkipped)

	dimStyle = lipgloss.NewStyle().Foreground(colOverlay)
)

// Exported palette accessors so the tui package shares the one scheme instead of re-hardcoding hexes.
func Base() color.Color      { return colBase }
func Mantle() color.Color    { return colMantle }
func Surface() color.Color   { return colSurface }
func Selection() color.Color { return colSurface1 }
func Overlay() color.Color   { return colOverlay }
func Text() color.Color      { return colText }
func Accent() color.Color    { return colMauve }
func Blue() color.Color      { return colBlue }
func Green() color.Color     { return colGreen }
func Red() color.Color       { return colRed }
func Yellow() color.Color    { return colYellow }
func Peach() color.Color     { return colPeach }
func Teal() color.Color      { return colTeal }
func Sky() color.Color       { return colSky }
func Pending() color.Color   { return colPending }

func dim(s string) string    { return dimStyle.Render(s) }
func green(s string) string  { return lipgloss.NewStyle().Foreground(colGreen).Render(s) }
func red(s string) string    { return lipgloss.NewStyle().Foreground(colRed).Render(s) }
func blue(s string) string   { return lipgloss.NewStyle().Foreground(colSky).Render(s) }
func cyan(s string) string   { return lipgloss.NewStyle().Foreground(colMauve).Render(s) }
func purple(s string) string { return lipgloss.NewStyle().Foreground(colTeal).Render(s) }

// SpinnerFrames is the braille spinner shared by the top bar and any pane that shows liveness.
var SpinnerFrames = []string{"⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"}

// Spinner returns the frame glyph for an arbitrary monotonically increasing frame counter.
func Spinner(frame int) string {
	if frame < 0 {
		frame = -frame
	}
	return SpinnerFrames[frame%len(SpinnerFrames)]
}

// StatusColor maps an engine run status ("Running", "Paused", "NeedsAttention", …) to its
// semantic colour, so the top bar and the agent strip always agree.
func StatusColor(status string) color.Color {
	switch normalizeStatus(status) {
	case "running":
		return colBlue
	case "paused":
		return colYellow
	case "attention":
		return colRed
	case "completed":
		return colGreen
	default:
		return colOverlay
	}
}

// StatusBadge renders the run status as an upper-case coloured badge, e.g. "▶ RUNNING".
func StatusBadge(status string) string {
	norm := normalizeStatus(status)
	glyph := map[string]string{
		"running":   "▶",
		"paused":    "⏸",
		"attention": "⚠",
		"completed": "✓",
	}[norm]
	if glyph == "" {
		glyph = "·"
	}
	label := norm
	if label == "" {
		label = "unknown"
	}
	return lipgloss.NewStyle().Foreground(StatusColor(status)).Bold(true).
		Render(glyph + " " + strings.ToUpper(label))
}

func normalizeStatus(status string) string {
	s := strings.ToLower(status)
	switch {
	case strings.Contains(s, "run"):
		return "running"
	case strings.Contains(s, "pause"):
		return "paused"
	case strings.Contains(s, "attention"), strings.Contains(s, "human"), strings.Contains(s, "stall"), strings.Contains(s, "fail"):
		return "attention"
	case strings.Contains(s, "complete"), strings.Contains(s, "done"):
		return "completed"
	default:
		return s
	}
}

func truncate(s string, max int) string {
	if max < 1 {
		return ""
	}
	r := []rune(s)
	if len(r) <= max {
		return s
	}
	if max == 1 {
		return "…"
	}
	return string(r[:max-1]) + "…"
}
