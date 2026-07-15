package widgets

import (
	"fmt"
	"strings"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

// RenderTopBar is the persistent status line: brand, connection, plan/stage, and live cost/tokens.
func RenderTopBar(conn api.ConnectionState, state *api.StateDto, width int) string {
	style := lipgloss.NewStyle().Background(colMantle).Foreground(colText).Padding(0, 1).MaxHeight(1).MaxWidth(width)
	sep := lipgloss.NewStyle().Foreground(colSurface).Render("  ")

	parts := []string{brandStyle.Render("◆ conductor")}

	dot := lipgloss.NewStyle().Foreground(colOverlay).Render("●")
	label := "LIVE"
	if conn.Mode == api.ModeDemo {
		label = "DEMO"
		dot = lipgloss.NewStyle().Foreground(colTeal).Render("●")
	} else if conn.Connected {
		dot = lipgloss.NewStyle().Foreground(colGreen).Render("●")
	} else {
		dot = lipgloss.NewStyle().Foreground(colRed).Render("●")
	}
	parts = append(parts, dot+" "+dimStyle.Render(label))

	if state != nil {
		parts = append(parts, lipgloss.NewStyle().Foreground(colText).Render(state.PlanName))
		parts = append(parts, lipgloss.NewStyle().Foreground(colMauve).Bold(true).Render(state.StageId)+" "+dimStyle.Render(truncate(state.StageTitle, 34)))

		if state.AgentActive {
			seg := lipgloss.NewStyle().Foreground(colGreen).Render("●") + " " +
				lipgloss.NewStyle().Foreground(colPeach).Render(fmt.Sprintf("$%.2f", state.SessionCostUsd))
			if width >= 120 {
				seg += " " + dimStyle.Render(fmt.Sprintf("%dk/%dk", state.SessionTokensInput/1000, state.SessionTokensOutput/1000))
			}
			parts = append(parts, seg)
		}
		if width >= 96 {
			parts = append(parts, dimStyle.Render(fmt.Sprintf("cp %d/%d", state.DoneCount, state.TotalCount)))
			parts = append(parts, lipgloss.NewStyle().Foreground(colPeach).Render(fmt.Sprintf("$%.2f", state.TotalCostUsd)))
		}
		if width >= 130 {
			parts = append(parts, dimStyle.Render(fmt.Sprintf("%.0fs", state.SessionElapsedSec)))
		}
	}

	return style.Render(strings.Join(parts, sep))
}
