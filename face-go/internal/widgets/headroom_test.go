package widgets

import (
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

func i64(v int64) *int64     { return &v }
func f64(v float64) *float64 { return &v }

// capped builds the block the engine serves for a live, capped session: 10M ceiling, 8M nudge.
func capped(tokens int64) *api.TokenHeadroomDto {
	return &api.TokenHeadroomDto{
		Tokens:    tokens,
		Cap:       i64(10_000_000),
		NudgeAt:   i64(8_000_000),
		ToNudge:   i64(8_000_000 - tokens),
		ToCap:     i64(10_000_000 - tokens),
		UsedRatio: f64(float64(tokens) / 10_000_000),
		Live:      true,
	}
}

func TestTokenGaugeSaysNothingWithoutABlock(t *testing.T) {
	// An engine older than K4.4 serves no block. A gauge invented from the fields it DOES serve would
	// be the exact untruth this widget exists to prevent, so the row does not render at all.
	if got := TokenGauge(nil); got != "" {
		t.Fatalf("nil block should render nothing, got %q", got)
	}
}

func TestTokenGaugeShowsSpendAgainstTheCeilingAndTheDistanceToTheNudge(t *testing.T) {
	h := capped(2_000_000)
	h.BurnPerMinute, h.MinutesToNudge, h.MinutesToCap = f64(1_000_000), f64(6), f64(8)

	got := stripStyles(TokenGauge(h))

	for _, want := range []string{"2.0M / 10.0M", "6.0M to nudge", "1.0M/min", "~6m"} {
		if !strings.Contains(got, want) {
			t.Fatalf("want %q in %q", want, got)
		}
	}
}

func TestTokenGaugeSaysNoCapRatherThanImplyingOne(t *testing.T) {
	// The honesty rule the checkpoint names. An uncapped session must never render a percentage, a
	// ratio or a distance: there is no ceiling to be a fraction of.
	h := &api.TokenHeadroomDto{Tokens: 6_200_000, BurnPerMinute: f64(310_000), Live: true}

	got := stripStyles(TokenGauge(h))

	if !strings.Contains(got, "no cap") {
		t.Fatalf("want a plain 'no cap' in %q", got)
	}
	for _, never := range []string{"%", "/ ", "nudge", "~"} {
		if strings.Contains(got, never) {
			t.Fatalf("uncapped gauge must not contain %q: %q", never, got)
		}
	}
	// The spend and the rate are still true and still worth saying.
	if !strings.Contains(got, "6.2M") || !strings.Contains(got, "310.0k/min") {
		t.Fatalf("want the real spend and rate in %q", got)
	}
}

func TestTokenGaugeIsSilentWhenThereIsNeitherACeilingNorASession(t *testing.T) {
	h := &api.TokenHeadroomDto{Tokens: 6_200_000, Live: false}

	if got := TokenGauge(h); got != "" {
		t.Fatalf("idle and uncapped should render nothing, got %q", got)
	}
}

func TestTokenGaugeSaysTheNudgeIsBehindRatherThanCountingDownToIt(t *testing.T) {
	// 8.5M against an 8M nudge. "0 to nudge" would read as "about to be asked to wrap up" when the
	// truth is it was asked a while ago — and what is left to count down to is the hard ceiling.
	h := capped(8_500_000)
	h.BurnPerMinute, h.MinutesToNudge, h.MinutesToCap = f64(500_000), nil, f64(3)

	got := stripStyles(TokenGauge(h))

	if !strings.Contains(got, "nudge passed") {
		t.Fatalf("want 'nudge passed' in %q", got)
	}
	if strings.Contains(got, "to nudge") {
		t.Fatalf("must not count down to a nudge already raised: %q", got)
	}
	if !strings.Contains(got, "1.5M to ceiling") || !strings.Contains(got, "~3m") {
		t.Fatalf("want the remaining distance and eta to the ceiling in %q", got)
	}
}

func TestTokenGaugeNamesTheCeilingItHasReached(t *testing.T) {
	h := capped(10_000_000)
	h.ToCap = i64(0)

	if got := stripStyles(TokenGauge(h)); !strings.Contains(got, "AT CEILING") {
		t.Fatalf("want 'AT CEILING' in %q", got)
	}
}

func TestTokenGaugeInventsNoRateWhenTheEngineServesNone(t *testing.T) {
	// Twenty seconds of clock is the engine's floor for a rate; below it there is no rate on the wire
	// and the Face must not fill the gap. A zero here would report a stalled session as a free one.
	h := capped(2_000_000)

	got := stripStyles(TokenGauge(h))

	if strings.Contains(got, "/min") || strings.Contains(got, "~") {
		t.Fatalf("no rate on the wire must mean no rate on screen: %q", got)
	}
	if !strings.Contains(got, "6.0M to nudge") {
		t.Fatalf("the distances are still known and still shown: %q", got)
	}
}

// TestTokenGaugeBandsOnTheRailsOwnLandmarks pins the colour rule to the plan's real thresholds rather
// than to round numbers: a plan that moves softBreakRatio moves the bands with it. Goldens strip ANSI,
// so this is the only place the banding is actually checked.
func TestTokenGaugeBandsOnTheRailsOwnLandmarks(t *testing.T) {
	safe := gaugeStyle(capped(4_000_000)).GetForeground()  // well below the 8M nudge
	warn := gaugeStyle(capped(8_200_000)).GetForeground()  // past the nudge, not yet halfway to 10M
	bad := gaugeStyle(capped(9_500_000)).GetForeground()   // past 9M — the ask has plainly not landed
	edge := gaugeStyle(capped(7_999_999)).GetForeground()  // one token short of the nudge is still safe

	if safe == warn || warn == bad || safe == bad {
		t.Fatalf("the three bands must differ: safe=%v warn=%v bad=%v", safe, warn, bad)
	}
	if edge != safe {
		t.Fatalf("below the nudge is safe, got %v want %v", edge, safe)
	}
}

// stripStyles removes ANSI so the assertions above are about words, not escape bytes.
func stripStyles(s string) string {
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		if s[i] == 0x1b {
			for i < len(s) && s[i] != 'm' {
				i++
			}
			continue
		}
		b.WriteByte(s[i])
	}
	return b.String()
}
