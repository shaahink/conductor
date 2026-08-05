package widgets

import (
	"fmt"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

// TokenGauge renders ONE session's token headroom: how far it is into the ceiling that will end it,
// how far the cooperative nudge still is, how fast it is spending and roughly when it arrives.
//
// It is a package-level function of one wire value, not a Model method, on purpose. K4.4 is the first
// widget a lane-aware Face has to MULTIPLY — one gauge per lane, several on a frame — and anything
// reaching into the Model for its numbers can be rendered exactly once. Give it a headroom block and
// it renders; give it a second one and it renders that.
//
// "" means "say nothing": no block on the wire (an engine older than K4.4), or a run that has no
// ceiling AND nothing in flight, where the row would be three words about nothing.
//
// The honesty rules it inherits from the engine: a nil cap prints "no cap" rather than a full bar, a
// missing burn rate prints no rate rather than a made-up one, and a crossed nudge says it was crossed
// instead of counting down to a moment that has passed.
func TokenGauge(h *api.TokenHeadroomDto) string {
	if h == nil {
		return ""
	}
	if h.Cap == nil {
		if !h.Live {
			return ""
		}
		// Live and uncapped. The spend is real and is worth saying; the ceiling is not, and must not
		// be implied by a percentage of a number nobody set.
		line := lipgloss.NewStyle().Foreground(Text()).Render(FmtTokens(h.Tokens))
		return line + subtleGauge().Render("  no cap"+rateSuffix(h))
	}

	text := fmt.Sprintf("%s / %s", FmtTokens(h.Tokens), FmtTokens(*h.Cap))
	return gaugeStyle(h).Render(text) + subtleGauge().Render(gaugeTail(h))
}

// gaugeStyle bands the gauge on the rail's OWN landmarks rather than on round numbers: safe below the
// nudge, warning from the nudge (the point the session is asked to wrap up), and destructive from
// halfway between the nudge and the ceiling (the point the ask has plainly not been heard). A plan
// that moves softBreakRatio moves the colours with it, which a hardcoded 0.7/0.9 pair would not.
func gaugeStyle(h *api.TokenHeadroomDto) lipgloss.Style {
	c := Green()
	if h.NudgeAt != nil && h.Cap != nil {
		nudge, ceiling := *h.NudgeAt, *h.Cap
		switch {
		case h.Tokens >= nudge+(ceiling-nudge)/2:
			c = Red()
		case h.Tokens >= nudge:
			c = Yellow()
		}
	} else if h.UsedRatio != nil && *h.UsedRatio >= 0.9 {
		c = Red()
	}
	return lipgloss.NewStyle().Foreground(c)
}

// gaugeTail is everything after the "X / Y": the distance that matters, the rate, and the projection.
func gaugeTail(h *api.TokenHeadroomDto) string {
	tail := ""
	switch {
	case h.ToCap != nil && *h.ToCap <= 0:
		tail = "  AT CEILING"
	case h.ToNudge != nil && *h.ToNudge <= 0:
		// Past the nudge. Saying "0 to nudge" here would read as "about to be asked to wrap up" when
		// the truth is it was asked a while ago; what is left to count down to is the hard ceiling.
		tail = fmt.Sprintf("  nudge passed · %s to ceiling", FmtTokens(nonNeg(h.ToCap)))
	case h.ToNudge != nil:
		tail = fmt.Sprintf("  %s to nudge", FmtTokens(*h.ToNudge))
	}
	tail += rateSuffix(h)
	if eta := gaugeEta(h); eta != "" {
		tail += " · " + eta
	}
	return tail
}

// rateSuffix is the burn rate, or nothing at all. There is no such thing as a default rate: the engine
// serves null until the session has run long enough for the arithmetic to mean something, and a Face
// that filled that gap with a zero would report a stalled session as a free one.
func rateSuffix(h *api.TokenHeadroomDto) string {
	if h.BurnPerMinute == nil || *h.BurnPerMinute <= 0 {
		return ""
	}
	return " · " + FmtTokens(int64(*h.BurnPerMinute)) + "/min"
}

// gaugeEta prefers the nudge — the thing that happens first and the only one of the two a session can
// still act on — and falls back to the ceiling once the nudge is behind. It names no destination
// because the distance printed just before it already did, and the two always agree.
//
// Prefixed "~" because the projection is a mean rate against a bill that grows every turn: it bounds
// the time left, it does not predict it.
func gaugeEta(h *api.TokenHeadroomDto) string {
	if h.MinutesToNudge != nil {
		return "~" + fmtMinutes(*h.MinutesToNudge)
	}
	if h.MinutesToCap != nil {
		return "~" + fmtMinutes(*h.MinutesToCap)
	}
	return ""
}

func fmtMinutes(m float64) string {
	switch {
	case m < 1:
		return "<1m"
	case m < 60:
		return fmt.Sprintf("%.0fm", m)
	default:
		return fmt.Sprintf("%dh%02dm", int(m)/60, int(m)%60)
	}
}

func nonNeg(v *int64) int64 {
	if v == nil || *v < 0 {
		return 0
	}
	return *v
}

// subtleGauge builds the muted style fresh so a theme switch reaches it without a package rebuild.
func subtleGauge() lipgloss.Style { return lipgloss.NewStyle().Foreground(Overlay()) }
