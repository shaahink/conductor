// Package timefmt is the Face's one clock vocabulary. Every surface that prints a time or an age
// goes through here, so "how long ago" reads the same on Home, in the Agent banner and in the
// history spine instead of each pane inventing its own phrasing.
//
// SF2.1 lands the relative half (Span/Ago/Age) because the connection line cannot be honest without
// it: "not connected" with no age is a state, not a fact. SF2.2 lands the absolute half — local
// wall-clocks with a date when it is not today, one Parse that understands every layout the engine
// puts on the wire, and one Duration that replaced three near-identical elapsed formatters.
package timefmt

import (
	"fmt"
	"strings"
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

// Location is the timezone every rendered wall-clock is converted into. Local by default, because a
// clock the owner cannot compare to their own wrist is decoration; golden tests pin it to time.UTC so
// recorded frames do not depend on the machine that rendered them.
var Location = time.Local

// sqliteLayout is the second wire format, and the reason ledger and bug timestamps rendered nowhere
// for this feature's whole life. The engine writes ledger.created_at and bugs.created_at with SQLite's
// own datetime('now') (migrations v4_ledger_created_at.sql, v7_bugs.sql), which produces
// "2026-08-01 00:37:30" — a space instead of the T, and no zone at all even though the value IS UTC.
// time.Parse(time.RFC3339, …) rejects it outright, so a pane that assumed one layout would have shown
// "--:--" over a perfectly good timestamp and read as a Face bug.
const sqliteLayout = "2006-01-02 15:04:05"

// Parse turns a wire timestamp into a time. It accepts RFC3339 (sessions, timeline, processes — the
// C# side writes those with a Z) and the naked SQLite layout above, which it reads as UTC because
// datetime('now') is UTC by definition. ok is false for an empty or unrecognised string, which is the
// signal for a caller to drop the clause rather than render a fabricated clock.
func Parse(s string) (time.Time, bool) {
	s = strings.TrimSpace(s)
	if s == "" {
		return time.Time{}, false
	}
	if t, err := time.Parse(time.RFC3339, s); err == nil {
		return t, true
	}
	// Fractional seconds are allowed on the SQLite layout too (datetime('now','subsec')), so cut them
	// before matching rather than carrying a second layout constant.
	base := s
	if i := strings.IndexByte(base, '.'); i >= 0 {
		base = base[:i]
	}
	if t, err := time.ParseInLocation(sqliteLayout, base, time.UTC); err == nil {
		return t, true
	}
	return time.Time{}, false
}

// Clock renders the wall-clock alone — "14:32", in Location. A zero time renders "" so callers can
// drop the whole clause; see Age for why the Face never invents a timestamp it does not have.
func Clock(t time.Time) string {
	if t.IsZero() {
		return ""
	}
	return t.In(Location).Format("15:04")
}

// Stamp renders an absolute time the way a human reads one: bare "14:32" when it happened today,
// "Jul 15 14:32" when it did not, and "2025-11-03 14:32" once the year differs too. Today is measured
// in Location against Now — a run that crosses midnight is the exact case the screenshot critique
// called unreadable, and it is the case where the date must appear.
func Stamp(t time.Time) string {
	if t.IsZero() {
		return ""
	}
	local := t.In(Location)
	now := Now().In(Location)
	switch {
	case local.Year() != now.Year():
		return local.Format("2006-01-02 15:04")
	case local.YearDay() == now.YearDay():
		return local.Format("15:04")
	default:
		return local.Format("Jan 2 15:04")
	}
}

// StampAge is the pairing the spec asks every surface to speak — "14:32 · 2h ago". The absolute half
// answers "when", the relative half answers "how long ago"; either alone leaves the owner doing
// arithmetic. A zero time renders "".
func StampAge(t time.Time) string {
	if t.IsZero() {
		return ""
	}
	return Stamp(t) + " · " + Age(t)
}

// Duration is the Face's ONE elapsed-time formatter. Before SF2.2 there were three: widgets.FmtWall
// for the top bar, tui.formatProcessRuntime (a byte-identical copy of it in another package) and
// tui.fmtDuration. Only the last knew about hours, so a three-hour process rendered "184m30s" in the
// Processes tab while the same span rendered "3h 04m" two tabs away. Two largest units, always.
func Duration(d time.Duration) string {
	if d < 0 {
		d = 0
	}
	switch {
	case d < time.Minute:
		return fmt.Sprintf("%ds", int(d.Seconds()))
	case d < time.Hour:
		return fmt.Sprintf("%dm %02ds", int(d.Minutes()), int(d.Seconds())%60)
	default:
		return fmt.Sprintf("%dh %02dm", int(d.Hours()), int(d.Minutes())%60)
	}
}
