// Package timefmt is the Face's one clock vocabulary. Every surface that prints a time or an age
// goes through here, so "how long ago" reads the same on Home, in the Agent banner and in the
// history spine instead of each pane inventing its own phrasing.
//
// SF2.1 lands the relative half (Span/Ago/Age) because the connection line cannot be honest without
// it: "not connected" with no age is a state, not a fact. SF2.2 grows this package into the absolute
// half — local wall-clocks with a date when it is not today — and moves the panes that still format
// their own timestamps onto it.
package timefmt

import (
	"fmt"
	"time"
)

// Now is the clock this package reads. A var, not a call, so a test can pin it: an age rendered
// from the real wall clock is a golden that fails a minute after it is written.
var Now = time.Now

// Span renders a duration as one short token — "12s", "4m", "2h", "3d". No sign and no "ago": a
// caller that means "ago" says so, and a caller that means "for" (uptime, a stall) says that.
// Negative spans (a clock skew between engine and face) clamp to zero rather than printing "-3m",
// which reads as a bug in the face rather than in the clock.
func Span(d time.Duration) string {
	if d < 0 {
		d = 0
	}
	switch {
	case d < time.Minute:
		return fmt.Sprintf("%ds", int(d.Seconds()))
	case d < time.Hour:
		return fmt.Sprintf("%dm", int(d.Minutes()))
	case d < 24*time.Hour:
		return fmt.Sprintf("%dh", int(d.Hours()))
	default:
		return fmt.Sprintf("%dd", int(d.Hours())/24)
	}
}

// Ago renders a span as "4m ago". Under five seconds it is "just now" — a second-by-second counter
// on a 1s poll is motion, not information.
func Ago(d time.Duration) string {
	if d < 5*time.Second {
		return "just now"
	}
	return Span(d) + " ago"
}

// Age is Ago measured against Now. A ZERO time renders "" — the Face must never invent an age for a
// timestamp it does not have, and "" is what lets a caller drop the whole clause instead of printing
// "last contact 56y ago" from an unset field.
func Age(t time.Time) string {
	if t.IsZero() {
		return ""
	}
	return Ago(Now().Sub(t))
}
