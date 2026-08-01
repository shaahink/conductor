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
