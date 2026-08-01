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
		planIdx := -1
		if width >= 124 {
			planIdx = len(parts)
			parts = append(parts, lipgloss.NewStyle().Foreground(colText).Render(state.PlanName))
		}
		// The stage TITLE is what yields to the repo chip on a narrow bar: it is already spelled out in
		// the sidebar and the Agent strip, whereas the repo is nowhere else on an 80-col screen. The
		// stage ID always stays.
		stageBare := lipgloss.NewStyle().Foreground(colMauve).Bold(true).Render(state.StageId)
		stage := stageBare
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
		stageIdx := len(parts)
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
		// SF3.3 + FU-OWNER-10, appended LAST and fitted by MEASUREMENT rather than by a width
		// threshold. Every field above tiers on a hand-picked `width >= N`, which works while the
		// numbers to the left are fixed-ish — but this bar is already full at 120 columns, and the
		// outer style's MaxWidth clips what does not fit SILENTLY. A guessed threshold would therefore
		// not fail loudly; it would eat the branch chip on exactly the wide-but-busy frame the owner
		// is watching. So: assemble what the bar already had, measure it, and take the richest chip
		// that still fits in what is left.
		chips := gitChips(state.Git)
		var used int
		before := len(parts)
		parts, used = fitTail(parts, chips, width)
		if len(parts) == before && stage != stageBare {
			// Nothing git-shaped fit, and the bar is still carrying a TRUNCATED stage title. At 120
			// columns — the width the owner actually runs — that title costs 21 columns to say
			// "Gate caching + trut…", which the sidebar and the Agent strip both already say in full,
			// while the branch is nowhere else on the screen at all. So shed it and try once more.
			// The stage ID never goes.
			parts[stageIdx] = stageBare
			parts, used = fitTail(parts, chips, width)
		}
		// The chip outranks the stamp for the last columns: which branch is being written to is
		// operational, which build is serving is identity you check occasionally. So a bar too narrow
		// for the chip does not get to spend those columns on the stamp instead — it stays quiet and
		// Home answers both. (A workspace with no chip to render at all is not "too narrow": there
		// the stamp is competing with nothing.)
		if len(chips) > 0 && len(parts) == before {
			return style.Render(strings.Join(parts, sep))
		}
		before = len(parts)
		parts, _ = fitTail(parts, buildStamps(state), width, used)
		if len(parts) == before && planIdx >= 0 {
			// FU-OWNER-10 takes the plan NAME's columns when it needs them, and only then. The plan
			// name is the most repeated fact on the whole screen — Home's Run panel, the Report header
			// and the Kanban ribbon all carry it — and it cannot change while a run is alive. Which
			// BUILD is serving is the opposite on both counts: nowhere else on the strip, and it
			// changes at exactly the moment the owner needs to read it, which is the reinstall that
			// cost four out-of-band checks to confirm.
			parts = append(parts[:planIdx], parts[planIdx+1:]...)
			parts, _ = fitTail(parts, buildStamps(state), width)
		}
	} else if conn.Mode == api.ModeLive {
		parts = append(parts, dimStyle.Render("waiting for a run at "+conn.URL))
	}

	return style.Render(strings.Join(parts, sep))
}

// topBarSepW and topBarPadW are the two costs a caller of fitTail cannot see in `parts`: the
// two-column separator each extra segment brings with it, and the one column of padding the outer
// style adds on each side.
const (
	topBarSepW = 2
	topBarPadW = 2
)

// fitTail appends the first candidate that still fits inside width and returns the parts plus the
// columns now spent. Candidates are ordered richest-first, and an empty list (or nothing that fits)
// appends nothing — a chip that cannot be rendered honestly is not rendered at all.
//
// The optional `spent` argument lets a second call reuse the first's measurement instead of
// re-joining the whole bar.
func fitTail(parts []string, candidates []string, width int, spent ...int) ([]string, int) {
	used := 0
	if len(spent) > 0 {
		used = spent[0]
	} else {
		used = lipgloss.Width(strings.Join(parts, "  ")) + topBarPadW
	}
	for _, c := range candidates {
		if c == "" {
			continue
		}
		if cost := topBarSepW + lipgloss.Width(c); used+cost <= width {
			return append(parts, c), used + cost
		}
	}
	return parts, used
}

// gitChips renders the SF3.3 branch chip at every width it is willing to be rendered at, richest
// first, for fitTail to choose between.
//
// What tiers and what does NOT. The branch NAME shortens and the dirty COUNT drops, because a
// shortened branch is still a branch and "●" still says dirty. The divergence marker and the dirty
// dot themselves never drop from a chip that renders at all: the whole point of the chip is that
// its silences are readable — no dot means clean, and that reading is only safe if no tier is
// allowed to hide a dot it had. When nothing fits, the chip disappears WHOLE, which claims nothing.
//
// nil Git = an engine older than SF3.3, which knows nothing about the repo. IsRepo false = an engine
// that looked and found no repo. Neither belongs in a status strip; Home says the second one out
// loud, because "this run is not editing a git repo" is worth a sentence and not a glyph.
func gitChips(g *api.GitDto) []string {
	if g == nil || !g.IsRepo {
		return nil
	}
	glyph := dimStyle.Render("⎇ ")
	var out []string
	for _, t := range []struct {
		nameW    int
		longWord bool // spell "unpushed" out rather than abbreviating it to "?"
		count    bool // show the dirty file count next to the dot
	}{
		{28, true, true},
		{16, false, true},
		{10, false, false},
		{6, false, false},
	} {
		chip := glyph + lipgloss.NewStyle().Foreground(colSky).Render(truncate(gitChipName(g), t.nameW))
		if d := gitDivergence(g, t.longWord); d != "" {
			chip += " " + d
		}
		if g.Dirty {
			dot := lipgloss.NewStyle().Foreground(colYellow).Render("●")
			if t.count && g.DirtyCount > 0 {
				dot += dimStyle.Render(fmt.Sprintf("%d", g.DirtyCount))
			}
			chip += " " + dot
		}
		out = append(out, chip)
	}
	return out
}

// gitChipName is what the chip calls the current position. A detached HEAD has no branch — the
// engine serves Branch as "" and never the literal string "HEAD" — so the sha becomes the name
// rather than leaving the chip blank.
func gitChipName(g *api.GitDto) string {
	if g.Branch != "" {
		return g.Branch
	}
	if g.HeadShortSha != "" {
		return "@" + g.HeadShortSha
	}
	return "detached"
}

// gitDivergence renders the relationship to the upstream, and exists because "no upstream" and
// "level with the upstream" must not render the same. A never-pushed branch rendered as a blank
// (or, worse, as "↑0 ↓0") tells an owner their work is safely on the remote when it has never left
// the machine. So: level is an explicit "≡", unpushed is an explicit marker of its own, and the
// blank is reserved for an engine that served no upstream field at all.
func gitDivergence(g *api.GitDto, longWord bool) string {
	if !g.HasUpstream() {
		if longWord {
			return dimStyle.Render("unpushed")
		}
		return dimStyle.Render("?")
	}
	ahead, behind := 0, 0
	if g.Ahead != nil {
		ahead = *g.Ahead
	}
	if g.Behind != nil {
		behind = *g.Behind
	}
	if ahead == 0 && behind == 0 {
		return dimStyle.Render("≡")
	}
	s := ""
	if ahead > 0 {
		s += fmt.Sprintf("↑%d", ahead)
	}
	if behind > 0 {
		s += fmt.Sprintf("↓%d", behind)
	}
	return lipgloss.NewStyle().Foreground(colPeach).Render(s)
}

// buildStamps is FU-OWNER-10's short form: which engine build is serving this run, in the one place
// that is on screen no matter which tab you are on. The owner spent four out-of-band checks
// answering "did my reinstall take?" because no surface named the build it was attached to.
//
// An engine that predates the field serves "", and "" renders NOTHING — not "unknown", which reads
// like a lookup that failed rather than an engine too old to answer. Home carries the full line
// including the face's own build, which is the half this strip has no room for.
func buildStamps(s *api.StateDto) []string {
	if s.EngineVersion == "" {
		return nil
	}
	v := dimStyle.Render("v" + s.EngineVersion)
	if s.EngineCommit == "" {
		return []string{v}
	}
	sha := shortSha(s.EngineCommit)
	// The narrowest tier keeps the COMMIT and drops the version, not the other way round: two builds
	// of the same prerelease version are exactly the pair the owner cannot tell apart, and the sha is
	// what tells them apart. It is labelled `eng` because a bare sha next to a branch chip reads as a
	// git sha, which is a different fact about a different thing.
	return []string{v + dimStyle.Render(" "+sha), v, dimStyle.Render("eng " + sha)}
}

// shortSha cuts a sha to the 7 an operator pastes into `git show`, and leaves anything already
// shorter alone — the engine may already have shortened it.
func shortSha(sha string) string {
	if len(sha) <= 7 {
		return sha
	}
	return sha[:7]
}
