package timefmt

import (
	"testing"
	"time"
)

func TestSpanBucketsAndClamps(t *testing.T) {
	cases := []struct {
		d    time.Duration
		want string
	}{
		{-3 * time.Minute, "0s"}, // clock skew clamps rather than printing a negative age
		{0, "0s"},
		{12 * time.Second, "12s"},
		{59 * time.Second, "59s"},
		{90 * time.Second, "1m"},
		{59 * time.Minute, "59m"},
		{2*time.Hour + 30*time.Minute, "2h"},
		{47 * time.Hour, "1d"},
		{72 * time.Hour, "3d"},
	}
	for _, c := range cases {
		if got := Span(c.d); got != c.want {
			t.Errorf("Span(%v) = %q, want %q", c.d, got, c.want)
		}
	}
}

func TestAgoSaysJustNowUnderFiveSeconds(t *testing.T) {
	if got := Ago(2 * time.Second); got != "just now" {
		t.Errorf("Ago(2s) = %q, want just now", got)
	}
	if got := Ago(30 * time.Second); got != "30s ago" {
		t.Errorf("Ago(30s) = %q, want 30s ago", got)
	}
}

// A zero time is the whole reason Age exists as its own function: the connection line has to be able
// to drop the clause when the Face never had contact, not print an age from the epoch.
func TestAgeOfAZeroTimeIsEmpty(t *testing.T) {
	if got := Age(time.Time{}); got != "" {
		t.Errorf("Age(zero) = %q, want empty", got)
	}
}

func TestAgeReadsThePinnedClock(t *testing.T) {
	fixed := time.Date(2026, 8, 1, 12, 0, 0, 0, time.UTC)
	Now = func() time.Time { return fixed }
	t.Cleanup(func() { Now = time.Now })

	if got := Age(fixed.Add(-4 * time.Minute)); got != "4m ago" {
		t.Errorf("Age(-4m) = %q, want 4m ago", got)
	}
}

// --- SF2.2: the absolute half ---------------------------------------------------

// The two wire layouts, measured off this repo's own run.db rather than assumed: sessions and the
// timeline carry RFC3339 with a Z, while ledger and bug rows carry SQLite's datetime('now') — space
// separator, no zone, UTC by construction. A parser that knows only the first renders "--:--" over a
// perfectly good timestamp, which is how these columns stayed invisible for the whole feature.
func TestParseAcceptsBothWireLayouts(t *testing.T) {
	want := time.Date(2026, 8, 1, 0, 37, 30, 0, time.UTC)
	for _, in := range []string{
		"2026-08-01T00:37:30Z",         // timeline / processes
		"2026-08-01T00:37:30.0000000Z", // sessions.started_utc — .NET writes seven fractional digits
		"2026-08-01 00:37:30",          // ledger.created_at / bugs.created_at
		"2026-08-01 00:37:30.123",      // datetime('now','subsec')
		"  2026-08-01 00:37:30  ",      // padded on the wire
	} {
		got, ok := Parse(in)
		if !ok {
			t.Fatalf("Parse(%q) not ok", in)
		}
		if !got.Truncate(time.Second).Equal(want) {
			t.Errorf("Parse(%q) = %v, want %v", in, got, want)
		}
	}
}

func TestParseRejectsWhatItCannotRead(t *testing.T) {
	for _, in := range []string{"", "   ", "not a time", "2026-13-45 99:99:99"} {
		if _, ok := Parse(in); ok {
			t.Errorf("Parse(%q) reported ok", in)
		}
	}
}

func TestParseReadsTheNakedLayoutAsUtcNotLocal(t *testing.T) {
	got, ok := Parse("2026-08-01 00:37:30")
	if !ok {
		t.Fatal("not ok")
	}
	if _, offset := got.Zone(); offset != 0 {
		t.Errorf("parsed zone offset = %d, want 0 (UTC)", offset)
	}
}

// A date appears exactly when the timestamp is not today — that is the whole fix for "17:47:51 with
// no date, a run spanning midnight is unreadable".
func TestStampShowsADateOnlyWhenItIsNotToday(t *testing.T) {
	pin(t, time.Date(2026, 8, 1, 9, 0, 0, 0, time.UTC), time.UTC)
	cases := []struct {
		name string
		at   time.Time
		want string
	}{
		{"today", time.Date(2026, 8, 1, 14, 32, 0, 0, time.UTC), "14:32"},
		{"one minute past midnight, still today", time.Date(2026, 8, 1, 0, 1, 0, 0, time.UTC), "00:01"},
		{"yesterday, the midnight-crossing case", time.Date(2026, 7, 31, 23, 59, 0, 0, time.UTC), "Jul 31 23:59"},
		{"same day number, different month", time.Date(2026, 7, 1, 14, 32, 0, 0, time.UTC), "Jul 1 14:32"},
		{"a different year gets the full date", time.Date(2025, 11, 3, 14, 32, 0, 0, time.UTC), "2025-11-03 14:32"},
	}
	for _, c := range cases {
		if got := Stamp(c.at); got != c.want {
			t.Errorf("%s: Stamp = %q, want %q", c.name, got, c.want)
		}
	}
}

func TestStampAndClockRenderInLocationNotUtc(t *testing.T) {
	// A fixed +02:00 zone, so the assertion does not depend on the tester's machine.
	east := time.FixedZone("TEST+2", 2*60*60)
	pin(t, time.Date(2026, 8, 1, 9, 0, 0, 0, time.UTC), east)
	at := time.Date(2026, 8, 1, 12, 32, 0, 0, time.UTC)
	if got := Clock(at); got != "14:32" {
		t.Errorf("Clock = %q, want 14:32 (converted into Location)", got)
	}
	if got := Stamp(at); got != "14:32" {
		t.Errorf("Stamp = %q, want 14:32", got)
	}
}

// "Today" is decided in Location too. At 00:30 UTC on the 1st, a +02:00 reader is at 02:30 on the 1st
// and an event stamped 23:00 UTC on the 31st happened at 01:00 THEIR today — no date belongs on it.
func TestStampDecidesTodayInLocation(t *testing.T) {
	east := time.FixedZone("TEST+2", 2*60*60)
	pin(t, time.Date(2026, 8, 1, 0, 30, 0, 0, time.UTC), east)
	if got := Stamp(time.Date(2026, 7, 31, 23, 0, 0, 0, time.UTC)); got != "01:00" {
		t.Errorf("Stamp = %q, want 01:00 with no date", got)
	}
}

func TestStampAgePairsAbsoluteWithRelative(t *testing.T) {
	pin(t, time.Date(2026, 8, 1, 16, 32, 0, 0, time.UTC), time.UTC)
	if got := StampAge(time.Date(2026, 8, 1, 14, 32, 0, 0, time.UTC)); got != "14:32 · 2h ago" {
		t.Errorf("StampAge = %q, want the spec's 14:32 · 2h ago", got)
	}
}

func TestAbsoluteRendersOfAZeroTimeAreEmpty(t *testing.T) {
	if got := Clock(time.Time{}); got != "" {
		t.Errorf("Clock(zero) = %q", got)
	}
	if got := Stamp(time.Time{}); got != "" {
		t.Errorf("Stamp(zero) = %q", got)
	}
	if got := StampAge(time.Time{}); got != "" {
		t.Errorf("StampAge(zero) = %q", got)
	}
}

// The case that motivated collapsing three formatters into one: the two %dm%02ds copies had no hour
// bucket, so a three-hour process rendered "184m30s".
func TestDurationKeepsTwoUnitsAndHasAnHourBucket(t *testing.T) {
	cases := []struct {
		d    time.Duration
		want string
	}{
		{-5 * time.Second, "0s"},
		{0, "0s"},
		{41 * time.Second, "41s"},
		{123 * time.Second, "2m 03s"},
		{59*time.Minute + 59*time.Second, "59m 59s"},
		{time.Hour, "1h 00m"},
		{3*time.Hour + 4*time.Minute + 30*time.Second, "3h 04m"},
		{27*time.Hour + 59*time.Minute, "27h 59m"},
	}
	for _, c := range cases {
		if got := Duration(c.d); got != c.want {
			t.Errorf("Duration(%v) = %q, want %q", c.d, got, c.want)
		}
	}
}

// pin fixes both halves of the package's environment for one test and restores whatever was there
// before — restoring to time.Now/time.Local instead would silently un-pin a package-level pin set by
// a TestMain, which is exactly the trap the golden suite was carrying.
func pin(t *testing.T, now time.Time, loc *time.Location) {
	t.Helper()
	prevNow, prevLoc := Now, Location
	Now, Location = func() time.Time { return now }, loc
	t.Cleanup(func() { Now, Location = prevNow, prevLoc })
}
