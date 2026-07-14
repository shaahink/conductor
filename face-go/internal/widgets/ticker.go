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

	if len(joined) > width-2 {
		joined = joined[:width-5] + dimStyle.Render("\u2026")
	}

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
		key("p") + " plan",
		key("i") + " inject",
		key("e") + " edit",
		key("h") + " history",
		key("r") + " query",
		key("?") + " help",
	}

	if sidebarOpen {
		segments = []string{
			key("p") + " close-plan",
			key("\u2191\u2193") + " navigate",
			key("enter") + " expand",
			key("/") + " filter",
			key(":") + " palette",
		}
	}

	joined := strings.Join(segments, dimStyle.Render("  "))

	if len(joined) > width-2 {
		joined = joined[:width-5] + dimStyle.Render("\u2026")
	}

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
	content := style.Render(joined)
	if len(content) > width {
		if width > 3 {
			content = content[:width-3] + dimStyle.Render("\u2026")
		}
	}
	return content
}
