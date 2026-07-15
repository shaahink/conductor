package widgets

import "charm.land/lipgloss/v2"

var (
	colorText     = lipgloss.Color("#C9D1D9")
	colorSubtle   = lipgloss.Color("#484F58")
	colorAccent   = lipgloss.Color("#58A6FF")
	colorDone     = lipgloss.Color("#3FB950")
	colorActive   = lipgloss.Color("#58A6FF")
	colorFail     = lipgloss.Color("#F85149")
	colorWarn     = lipgloss.Color("#D29922")
	colorPending  = lipgloss.Color("#484F58")
	colorSkipped  = lipgloss.Color("#8B949E")
	colorThinking = lipgloss.Color("#8B949E")
	colorTool     = lipgloss.Color("#A371F7")
	colorResult   = lipgloss.Color("#3FB950")
	colorStderr   = lipgloss.Color("#F85149")
	colorSystem   = lipgloss.Color("#79C0FF")
)

var (
	brandStyle = lipgloss.NewStyle().Foreground(colorAccent).Bold(true)

	sidebarTitleStyle = lipgloss.NewStyle().Foreground(colorAccent).Bold(true)

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

	highlightStyle = lipgloss.NewStyle().
			Background(lipgloss.Color("#1F6FEB")).
			Foreground(lipgloss.Color("#FFFFFF"))
)

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
	return s[:max-1] + "\u2026"
}

func accent(s string) string {
	return lipgloss.NewStyle().Foreground(colorText).Bold(true).Render(s)
}

func keyHelp(s string) string {
	return lipgloss.NewStyle().Foreground(colorAccent).Bold(true).Render(s)
}

var key = keyHelp
