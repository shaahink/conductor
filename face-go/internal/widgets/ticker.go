package widgets

import (
	"fmt"
	"strings"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

// FmtWall renders elapsed seconds as "2m03s" / "41s".
func FmtWall(sec float64) string {
	s := int(sec)
	if s < 0 {
		s = 0
	}
	if s >= 60 {
		return fmt.Sprintf("%dm%02ds", s/60, s%60)
	}
	return fmt.Sprintf("%ds", s)
}

// FmtTokens renders a token count as "3.4k" / "1.2M".
func FmtTokens(n int64) string {
	switch {
	case n >= 1_000_000:
		return fmt.Sprintf("%.1fM", float64(n)/1_000_000)
	case n >= 1_000:
		return fmt.Sprintf("%.1fk", float64(n)/1_000)
	default:
		return fmt.Sprintf("%d", n)
	}
}

// RenderTopBar is the persistent status line: brand, connection, run status, plan/stage, session,
// and live cost/tokens. Fields tier in by width so the row never wraps. spinnerFrame animates the
// liveness glyph while an agent session is actually producing output.
func RenderTopBar(conn api.ConnectionState, state *api.StateDto, width, spinnerFrame int) string {
	style := lipgloss.NewStyle().Background(colMantle).Foreground(colText).Padding(0, 1).MaxHeight(1).MaxWidth(width)
	sep := lipgloss.NewStyle().Foreground(colSurface).Render("  ")

	parts := []string{brandStyle.Render("◆ conductor")}

	label := "LIVE"
	var dot string
	switch {
	case conn.Mode == api.ModeDemo:
		label = "DEMO"
		dot = lipgloss.NewStyle().Foreground(colTeal).Render("●")
	case conn.Connected:
		dot = lipgloss.NewStyle().Foreground(colGreen).Render("●")
	default:
		label = "OFFLINE"
		dot = lipgloss.NewStyle().Foreground(colRed).Render("●")
	}
	parts = append(parts, dot+" "+dimStyle.Render(label))

	if state != nil {
		parts = append(parts, StatusBadge(state.Status))
		if width >= 124 {
			parts = append(parts, lipgloss.NewStyle().Foreground(colText).Render(state.PlanName))
		}
		titleW := 20
		if width >= 140 {
			titleW = 34
		}
		parts = append(parts, lipgloss.NewStyle().Foreground(colMauve).Bold(true).Render(state.StageId)+" "+dimStyle.Render(truncate(state.StageTitle, titleW)))

		if state.AgentActive {
			seg := lipgloss.NewStyle().Foreground(colGreen).Render(Spinner(spinnerFrame)) + " " +
				dimStyle.Render(fmt.Sprintf("s%d", state.SessionNumber)) + " " +
				lipgloss.NewStyle().Foreground(colPeach).Render(fmt.Sprintf("$%.2f", state.SessionCostUsd)) + " " +
				dimStyle.Render(FmtWall(state.SessionElapsedSec))
			if width >= 130 {
				toks := FmtTokens(state.SessionTokensInput) + "/" + FmtTokens(state.SessionTokensOutput)
				if state.SessionTokensReasoning > 0 {
					toks += " +" + FmtTokens(state.SessionTokensReasoning) + "r" // reasoning tokens
				}
				seg += " " + dimStyle.Render(toks)
			}
			parts = append(parts, seg)
		}
		if width >= 96 {
			parts = append(parts, dimStyle.Render(fmt.Sprintf("cp %d/%d", state.DoneCount, state.TotalCount)))
			run := dimStyle.Render("run ") + lipgloss.NewStyle().Foreground(colPeach).Bold(true).Render(fmt.Sprintf("$%.2f", state.TotalCostUsd))
			if width >= 140 && state.OverheadCostUsd > 0 {
				run += dimStyle.Render(fmt.Sprintf(" +$%.2f oh", state.OverheadCostUsd)) // gate/overhead cost
			}
			parts = append(parts, run)
		}
	} else if conn.Mode == api.ModeLive {
		parts = append(parts, dimStyle.Render("waiting for a run at "+conn.URL))
	}

	return style.Render(strings.Join(parts, sep))
}
