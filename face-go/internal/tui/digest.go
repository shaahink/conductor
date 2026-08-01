package tui

import (
	"fmt"
	"strings"

	"conductor-face-go/internal/api"
)

// SF3.1 — the per-session digest panel: what a session DID, before you read a word of what it said.
//
// The numbers here are the ENGINE's (SC7.2, `SessionDigestDto` on every `/sessions` row): tool calls
// and their mix, the files it wrote and how often, the board claims it made, the background jobs it
// started with the purposes it gave them, and the build/test commands worth a reviewer's eye. The
// Face computes none of it and re-sorts none of it — `Mix` and `FilesTouched` arrive ranked, and two
// surfaces sorting one session's tools into two different orders is exactly the kind of quiet
// disagreement this era exists to stop.
//
// The panel is shared by History (the session detail, where it is the whole point) and Report (one
// compressed line per row, where it is context beside the cost). Both read the same fields through
// the same caps, so a session cannot look busier on one tab than the other.

// How much of each list the panel shows before it says how much it is holding back. A digest is a
// skim surface: past these counts a reader is reading a log again, which is what they came here to
// avoid. Every overflow announces itself — a silently-cut list reads as a complete one.
const (
	digestMixMax      = 8
	digestFilesMax    = 4
	digestClaimsMax   = 4
	digestJobsMax     = 4
	digestCommandsMax = 3
)

// digestLabelWidth matches the History detail's existing label column ("Ran:   ", "Gates: "), so the
// digest lands as more of the same block rather than as a second, differently-aligned one.
const digestLabelWidth = 7

// renderSessionDigest is the full panel, as lines ready to join with "\n". Empty when the session has
// no digest — a session that predates SC7.2, or one that made no tool calls at all. It renders
// NOTHING rather than a row of zeros: "0 tool calls" and "this engine never told us" are different
// facts, and only one of them is worth a line.
func renderSessionDigest(d *api.SessionDigestDto, w int) []string {
	if d == nil || d.ToolCalls == 0 {
		return nil
	}
	var lines []string
	row := func(label, value string) {
		if value == "" {
			return
		}
		lines = append(lines, subtleStyle.Render(pad(label, digestLabelWidth))+
			textStyle.Render(truncate(value, max(4, w-digestLabelWidth))))
	}
	cont := func(value string) {
		lines = append(lines, strings.Repeat(" ", digestLabelWidth)+
			textStyle.Render(truncate(value, max(4, w-digestLabelWidth))))
	}

	row("Did:", digestHeadline(d))
	if mix := digestMix(d.Mix, digestMixMax); mix != "" {
		row("Tools:", mix)
	}
	for i, f := range digestList(d.FilesTouched, digestFilesMax) {
		if i == 0 {
			row("Files:", f)
			continue
		}
		cont(f)
	}
	// "Board:", not "Claims:", for two reasons: the column is seven wide to align with the detail's
	// existing "Ran:"/"Gates:" labels above it, and a claim IS a board write — `SF3.1 -> done` is the
	// row it moved, which is what a reader is checking when they look here.
	for i, c := range digestStrings(d.Claims, digestClaimsMax) {
		if i == 0 {
			row("Board:", c)
			continue
		}
		cont(c)
	}
	// The bg-purpose storyline, one purpose per line in the order they were started. devcontext #10
	// singled these out because agents write genuinely descriptive purposes there — read top to
	// bottom they are already a written narrative of the session, and joining them onto one clipped
	// line would throw away the half that does not fit.
	for i, j := range digestStrings(d.BackgroundJobs, digestJobsMax) {
		if i == 0 {
			row("Bg:", j)
			continue
		}
		cont("→ " + j)
	}
	// "Cmds:", never "Ran:" — the History detail one block up already spends "Ran:" on WHEN the
	// session ran, and two labels reading the same word for a time span and a shell command in one
	// pane is the kind of small lie this era keeps finding.
	for i, c := range digestStrings(d.Commands, digestCommandsMax) {
		if i == 0 {
			row("Cmds:", c)
			continue
		}
		cont(c)
	}
	return lines
}

// digestHeadline is the one sentence a reader needs if they read nothing else: how much work, of how
// many kinds, over how many files. The file half is dropped when the session wrote nothing — a
// research session that read fifty files and changed none should say so by omission, not by
// claiming "0 edits over 0 files".
func digestHeadline(d *api.SessionDigestDto) string {
	parts := []string{plural(d.ToolCalls, "tool call")}
	if d.DistinctTools > 0 {
		parts = append(parts, plural(d.DistinctTools, "tool"))
	}
	if d.FileWrites > 0 {
		parts = append(parts, plural(d.FileWrites, "edit")+" over "+plural(len(d.FilesTouched), "file"))
	}
	return strings.Join(parts, " · ")
}

// digestMix renders the ranked tool mix as "Bash ×18  Read ×11  …", keeping the wire's order.
func digestMix(counts []api.DigestCountDto, limit int) string {
	if len(counts) == 0 {
		return ""
	}
	shown := counts
	if len(shown) > limit {
		shown = shown[:limit]
	}
	cells := make([]string, 0, len(shown)+1)
	for _, c := range shown {
		cells = append(cells, digestCount(c))
	}
	if rest := len(counts) - len(shown); rest > 0 {
		cells = append(cells, fmt.Sprintf("+%d more", rest))
	}
	return strings.Join(cells, "  ")
}

// digestList renders a ranked name/count list one entry per line, with an overflow line when the
// list is longer than the panel shows.
func digestList(counts []api.DigestCountDto, limit int) []string {
	if len(counts) == 0 {
		return nil
	}
	shown := counts
	if len(shown) > limit {
		shown = shown[:limit]
	}
	out := make([]string, 0, len(shown)+1)
	for _, c := range shown {
		out = append(out, digestCount(c))
	}
	if rest := len(counts) - len(shown); rest > 0 {
		out = append(out, fmt.Sprintf("+%d more", rest))
	}
	return out
}

// digestCount is "name ×3", or bare "name" for a single occurrence — a "×1" on every row is noise
// that makes the rows that genuinely repeat harder to spot.
func digestCount(c api.DigestCountDto) string {
	if c.Count > 1 {
		return fmt.Sprintf("%s ×%d", c.Name, c.Count)
	}
	return c.Name
}

func digestStrings(src []string, limit int) []string {
	if len(src) == 0 {
		return nil
	}
	shown := src
	if len(shown) > limit {
		shown = shown[:limit]
	}
	out := make([]string, 0, len(shown)+1)
	out = append(out, shown...)
	if rest := len(src) - len(shown); rest > 0 {
		out = append(out, fmt.Sprintf("+%d more", rest))
	}
	return out
}

// digestOneLine is the Report's compression of the same facts onto a single row under the session's
// numbers: enough to tell a session that edited nine files from one that spun on a red gate, without
// turning the report's table into a second History tab.
func digestOneLine(d *api.SessionDigestDto) string {
	if d == nil || d.ToolCalls == 0 {
		return ""
	}
	parts := []string{plural(d.ToolCalls, "call")}
	if mix := digestMix(d.Mix, 3); mix != "" {
		parts = append(parts, mix)
	}
	if d.FileWrites > 0 {
		parts = append(parts, plural(d.FileWrites, "edit")+" / "+plural(len(d.FilesTouched), "file"))
	}
	if n := len(d.Claims); n > 0 {
		parts = append(parts, plural(n, "claim"))
	}
	return strings.Join(parts, " · ")
}
