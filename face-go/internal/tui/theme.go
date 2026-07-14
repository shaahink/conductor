package tui

import "charm.land/lipgloss/v2"

var (
	ColorBg       = lipgloss.Color("#0D1117")
	ColorText     = lipgloss.Color("#C9D1D9")
	ColorSubtle   = lipgloss.Color("#484F58")
	ColorBorder   = lipgloss.Color("#30363D")
	ColorAccent   = lipgloss.Color("#58A6FF")
	ColorDone     = lipgloss.Color("#3FB950")
	ColorActive   = lipgloss.Color("#58A6FF")
	ColorFail     = lipgloss.Color("#F85149")
	ColorWarn     = lipgloss.Color("#D29922")
	ColorPending  = lipgloss.Color("#484F58")
	ColorSkipped  = lipgloss.Color("#8B949E")
	ColorThinking = lipgloss.Color("#8B949E")
	ColorTool     = lipgloss.Color("#A371F7")
	ColorResult   = lipgloss.Color("#3FB950")
	ColorStderr   = lipgloss.Color("#F85149")
	ColorSystem   = lipgloss.Color("#79C0FF")

	ColorConnected    = lipgloss.Color("#3FB950")
	ColorDisconnected = lipgloss.Color("#F85149")
	ColorDemo         = lipgloss.Color("#A371F7")
)

var (
	BaseStyle = lipgloss.NewStyle().
			Foreground(ColorText).
			Background(ColorBg)

	TickerStyle = lipgloss.NewStyle().
			Background(lipgloss.Color("#161B22")).
			Foreground(ColorSubtle).
			Padding(0, 1).
			MaxHeight(1)

	FooterStyle = lipgloss.NewStyle().
			Background(lipgloss.Color("#161B22")).
			Foreground(ColorSubtle).
			Padding(0, 1).
			MaxHeight(1)
)
