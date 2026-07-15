package widgets

import (
	"fmt"
	"strings"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

func RenderTicker(conn api.ConnectionState, state *api.StateDto, width int) string {
	style := lipgloss.NewStyle().
		Background(lipgloss.Color("#161B22")).
		Foreground(colorSubtle).
		Padding(0, 1).
		MaxHeight(1).MaxWidth(width)

	parts := []string{}

	connGlyph := dimStyle.Render("\u25C6")
	if conn.Mode == api.ModeLive {
		if conn.Connected {
			connGlyph = green("\u25CF")
		} else {
			connGlyph = red("\u25CF")
		}
	}
	modeLabel := "LIVE"
	if conn.Mode == api.ModeDemo {
		modeLabel = "DEMO"
	}
	parts = append(parts, connGlyph+" "+accent(modeLabel))

	if state != nil {
		parts = append(parts, accent(state.PlanName))
		parts = append(parts, accent(state.StageId)+" "+dimStyle.Render(state.StageTitle))

		// M5.4: live session ticker — cost/tokens that grow DURING the session (folded from tokenDelta
		// server-side), shown while the agent is active so you watch spend accrue, not jump at the end.
		if state.AgentActive {
			seg := green("●") + " " + accent(fmt.Sprintf("$%.2f", state.SessionCostUsd))
			if width >= 120 {
				seg += " " + dimStyle.Render(fmt.Sprintf("%dk/%dk tok", state.SessionTokensInput/1000, state.SessionTokensOutput/1000))
			}
			parts = append(parts, seg)
		}

		if width >= 100 {
			parts = append(parts, fmt.Sprintf("CP %d/%d", state.DoneCount, state.TotalCount))
		}

		if width >= 100 {
			parts = append(parts, fmt.Sprintf("$%.2f", state.TotalCostUsd))
		}

		if width >= 140 {
			parts = append(parts, fmt.Sprintf("in:%dk out:%dk", state.TokensInput/1000, state.TokensOutput/1000))
		}

		if width >= 120 {
			parts = append(parts, fmt.Sprintf("%.0fs", state.SessionElapsedSec))
		}
	}

	joined := strings.Join(parts, " "+dimStyle.Render("\u2502")+" ")

	// style already carries MaxWidth(width), which truncates ANSI-safely \u2014 a manual byte-slice
	// truncation here would cut mid-escape-sequence and corrupt the line's styling.
	return style.Render(joined)
}

func RenderFooter(width int, sidebarOpen bool) string {
	style := lipgloss.NewStyle().
		Background(lipgloss.Color("#161B22")).
		Foreground(colorSubtle).
		Padding(0, 1).
		MaxHeight(1).MaxWidth(width)

	segments := []string{
		key(":") + " palette",
		key("p") + " sidebar",
		key("g") + " plan",
		key("i") + " inject",
		key("e") + " templates",
		key("h") + " history",
		key("s") + " procs",
		key("r") + " query",
		key("/") + " search",
		key("?") + " help",
	}

	if sidebarOpen {
		segments = []string{
			key("p") + " close-plan",
			key("\u2191\u2193") + " navigate",
			key("enter") + " expand",
			key(":") + " palette",
		}
	}

	joined := strings.Join(segments, dimStyle.Render("  "))

	// style already carries MaxWidth(width), which truncates ANSI-safely.
	return style.Render(joined)
}

func RenderGateBar(gates []api.GateDto, width int) string {
	style := lipgloss.NewStyle().
		Foreground(colorSubtle).
		MaxHeight(1).MaxWidth(width)

	var parts []string
	for _, g := range gates {
		gGlyph, gStyle := gateGlyph(g.State)
		label := gStyle.Render(gGlyph + " " + g.Name)
		if g.State == "running" {
			label += " " + dimStyle.Render(fmt.Sprintf("(%.1fs)", g.ElapsedSec))
		}
		parts = append(parts, label)
	}

	joined := strings.Join(parts, dimStyle.Render(" \u00B7 "))
	// style already carries MaxWidth(width), which truncates ANSI-safely.
	return style.Render(joined)
}
