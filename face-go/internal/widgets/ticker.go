package widgets

import (
	"fmt"
	"strings"
	"time"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
)

// FmtWall renders elapsed seconds for the top bar. SF2.2 emptied it out into timefmt.Duration — it
// was one of three copies of the same arithmetic, and the only reason it survives as a name is that
// it takes the wire's float seconds rather than a time.Duration.
func FmtWall(sec float64) string {
	return timefmt.Duration(time.Duration(sec * float64(time.Second)))
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

// RepoBase renders a repo path as its owning folder: `C:\Code\conductor-baton` → `…/conductor-baton`.
// It splits on BOTH separators instead of using filepath.Base, because the path arrives over the wire
// from whatever OS the engine runs on — which is not necessarily this binary's, and golden frames must
// not render differently per platform. A bare name with no separator is returned as-is.
func RepoBase(repo string) string {
	trimmed := strings.TrimRight(repo, `/\`)
	if trimmed == "" {
		return ""
	}
	if i := strings.LastIndexAny(trimmed, `/\`); i >= 0 {
		return "…/" + trimmed[i+1:]
	}
	return trimmed
}

// RenderTopBar is the persistent status line: brand, connection, workspace, run status, plan/stage,
// session, and live cost/tokens. Fields tier in by width so the row never wraps. spinnerFrame animates
// the liveness glyph while an agent session is actually producing output.
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
		// U1.2: the folder every session edits, dim, never tiered away — the owner must always be able
		// to see WHICH repo is being written to. Home carries the full path.
		if base := RepoBase(state.Repo); base != "" {
			parts = append(parts, dimStyle.Render(base))
		}
		parts = append(parts, StatusBadge(state.Status))
		if width >= 124 {
			parts = append(parts, lipgloss.NewStyle().Foreground(colText).Render(state.PlanName))
		}
		// The stage TITLE is what yields to the repo chip on a narrow bar: it is already spelled out in
		// the sidebar and the Agent strip, whereas the repo is nowhere else on an 80-col screen. The
		// stage ID always stays.
		stage := lipgloss.NewStyle().Foreground(colMauve).Bold(true).Render(state.StageId)
		titleW := 0
		switch {
		case width >= 140:
			titleW = 34
		case width >= 100:
			titleW = 20
		}
		if titleW > 0 {
			stage += " " + dimStyle.Render(truncate(state.StageTitle, titleW))
		}
		parts = append(parts, stage)

		if state.AgentActive {
			seg := lipgloss.NewStyle().Foreground(colGreen).Render(Spinner(spinnerFrame)) + " " +
				dimStyle.Render(fmt.Sprintf("s%d", state.SessionNumber))
			// The live spend of the session you are watching, priced as honestly as the engine can
			// price it — and OMITTED rather than shown as "$0.00" when it cannot price it at all.
			// The Agent footer renders the same figure through the same function for the same reason.
			if money := FmtSessionCost(state.SessionCostUsd, state.SessionCostBasis); money != "" {
				seg += " " + lipgloss.NewStyle().Foreground(colPeach).Render(money)
			}
			seg += " " + dimStyle.Render(FmtWall(state.SessionElapsedSec))
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
