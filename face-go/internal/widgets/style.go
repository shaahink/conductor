package widgets

import (
	"image/color"

	"charm.land/lipgloss/v2"
)

// conductor-face v3 palette — Catppuccin Mocha. A single cohesive scheme replaces the ad-hoc GitHub
// hexes so every pane, badge, and border reads as one system in both the sidebar and the main panes.
var (
	colBase    = lipgloss.Color("#1E1E2E") // window background
	colMantle  = lipgloss.Color("#181825") // panels: top/bottom bars, tab strip
	colSurface = lipgloss.Color("#313244") // borders, rules
	colSurface1 = lipgloss.Color("#45475A") // selection background
	colOverlay = lipgloss.Color("#6C7086") // muted text
	colText    = lipgloss.Color("#CDD6F4") // primary text

	colMauve = lipgloss.Color("#CBA6F7") // accent / brand / selection
	colBlue  = lipgloss.Color("#89B4FA") // active / info
	colGreen = lipgloss.Color("#A6E3A1") // success / done
	colRed   = lipgloss.Color("#F38BA8") // fail / destructive
	colYellow = lipgloss.Color("#F9E2AF") // warn / running
	colPeach = lipgloss.Color("#FAB387") // attention
	colTeal  = lipgloss.Color("#94E2D5") // tools
	colSky   = lipgloss.Color("#89DCEB") // system
)

// Semantic aliases kept for the widgets that referenced the old names.
var (
	colorText     = colText
	colorSubtle   = colOverlay
	colorAccent   = colMauve
	colorDone     = colGreen
	colorActive   = colBlue
	colorFail     = colRed
	colorWarn     = colYellow
	colorPending  = lipgloss.Color("#585B70")
	colorSkipped  = lipgloss.Color("#7F849C")
	colorThinking = lipgloss.Color("#7F849C")
	colorTool     = colTeal
	colorResult   = colGreen
	colorStderr   = colRed
	colorSystem   = colSky
)

var (
	brandStyle        = lipgloss.NewStyle().Foreground(colMauve).Bold(true)
	sidebarTitleStyle = lipgloss.NewStyle().Foreground(colMauve).Bold(true)

	stageDoneStyle    = lipgloss.NewStyle().Foreground(colorDone)
	stageActiveStyle  = lipgloss.NewStyle().Foreground(colorActive).Bold(true)
	stageFailStyle    = lipgloss.NewStyle().Foreground(colorFail)
	stageTodoStyle    = lipgloss.NewStyle().Foreground(colorPending)
	stageSkippedStyle = lipgloss.NewStyle().Foreground(colorSkipped)

	cpDoneStyle    = lipgloss.NewStyle().Foreground(colorDone)
	cpActiveStyle  = lipgloss.NewStyle().Foreground(colorActive).Bold(true)
	cpTodoStyle    = lipgloss.NewStyle().Foreground(colorPending)
	cpSkippedStyle = lipgloss.NewStyle().Foreground(colorSkipped)

	txThinkingStyle = lipgloss.NewStyle().Foreground(colorThinking).Italic(true)
	txToolStyle     = lipgloss.NewStyle().Foreground(colorTool)
	txResultStyle   = lipgloss.NewStyle().Foreground(colorResult)
	txStderrStyle   = lipgloss.NewStyle().Foreground(colorStderr)
	txSystemStyle   = lipgloss.NewStyle().Foreground(colorSystem)
	txAgentStyle    = lipgloss.NewStyle().Foreground(colorText)
	txRawStyle      = lipgloss.NewStyle().Foreground(colorSubtle)

	gatePassStyle    = lipgloss.NewStyle().Foreground(colorDone)
	gateRunningStyle = lipgloss.NewStyle().Foreground(colorActive)
	gateFailStyle    = lipgloss.NewStyle().Foreground(colorFail)
	gatePendingStyle = lipgloss.NewStyle().Foreground(colorPending)
	gateSkipStyle    = lipgloss.NewStyle().Foreground(colorSkipped)

	dimStyle = lipgloss.NewStyle().Foreground(colorSubtle)

	highlightStyle = lipgloss.NewStyle().Background(colSurface1).Foreground(colText)
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

func dim(s string) string    { return lipgloss.NewStyle().Foreground(colorSubtle).Render(s) }
func green(s string) string  { return lipgloss.NewStyle().Foreground(colorDone).Render(s) }
func red(s string) string    { return lipgloss.NewStyle().Foreground(colorFail).Render(s) }
func blue(s string) string   { return lipgloss.NewStyle().Foreground(colorSystem).Render(s) }
func cyan(s string) string   { return lipgloss.NewStyle().Foreground(colorAccent).Render(s) }
func purple(s string) string { return lipgloss.NewStyle().Foreground(colorTool).Render(s) }

func truncate(s string, max int) string {
	if len(s) <= max {
		return s
	}
	if max <= 3 {
		return s[:max]
	}
	return s[:max-1] + "…"
}

func accent(s string) string {
	return lipgloss.NewStyle().Foreground(colorText).Bold(true).Render(s)
}

func keyHelp(s string) string {
	return lipgloss.NewStyle().Foreground(colorAccent).Bold(true).Render(s)
}

var key = keyHelp
