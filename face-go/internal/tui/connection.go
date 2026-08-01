package tui

// SF2.1 — one definition of "connected", and one sentence that says what that means to a human.
//
// Two separate lies lived here. The first was structural: three places wrote Connection.Connected
// with three different meanings (see api.ConnectionState). The second was the wording — a Face that
// could not reach the engine rendered "mode  live — not connected", an oxymoron, followed by the raw
// Windows dial error ("connectex: No connection could be made because the target machine actively
// refused it."), which tells a user nothing they can act on. Both are fixed in this file: setConnected
// is the only writer of the state, and engineState is the only place that turns it into words.

import (
	"strings"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
)

// setConnected is the ONE writer of Connection.Connected. It also stamps the two clocks every honest
// surface needs: when we last heard from the engine, and when the current state began.
func (m *Model) setConnected(connected bool) {
	c := &m.data.Connection
	now := timefmt.Now()
	if connected {
		c.LastContactAt = now
	}
	// A first observation is a transition too: without this the very first disconnect has no age and
	// the banner would say "retrying" with nothing behind it.
	if c.Connected != connected || c.Since.IsZero() {
		c.Since = now
	}
	c.Connected = connected
}

// engineState is the connection rendered as one honest clause: what the engine is doing, why we say
// so, and how long that has been true. Callers add their own styling and label.
//
// It never returns the word "live" for a state that is not live, and never the raw transport error.
type engineStateText struct {
	Headline string // "not running", "answering", "not answering" — the state itself
	Detail   string // why we believe it, in English
	Age      string // "last contact 4m ago" / "for 4m", or "" when we have no clock for it
}

func engineState(c api.ConnectionState) engineStateText {
	if c.Connected {
		return engineStateText{Headline: "running", Detail: "answering on " + c.URL}
	}
	t := engineStateText{Headline: "not running", Detail: "nothing is listening on " + c.URL}
	if c.LastError != nil {
		t.Headline, t.Detail = humanizeConnError(*c.LastError, c.URL)
	}
	switch {
	case !c.LastContactAt.IsZero():
		t.Age = "last contact " + timefmt.Age(c.LastContactAt)
	case !c.Since.IsZero():
		// Never reached the engine at all this session: the age that means something is how long we
		// have been trying, not a contact that never happened.
		t.Age = "trying for " + timefmt.Span(timefmt.Now().Sub(c.Since))
	}
	return t
}

// humanizeConnError turns a transport error into (state, reason). The mapping is deliberately small
// and keyed on substrings the Go/Windows stacks actually emit; anything unrecognised keeps the raw
// text as its reason rather than being dressed up as a diagnosis the Face cannot make. The raw string
// is never lost either way — Home's Wiring section still carries it verbatim for a bug report.
func humanizeConnError(raw, url string) (state, reason string) {
	l := strings.ToLower(raw)
	switch {
	case strings.Contains(l, "connectex"), strings.Contains(l, "connection refused"),
		strings.Contains(l, "actively refused"), strings.Contains(l, "econnrefused"):
		return "not running", "nothing is listening on " + url
	case strings.Contains(l, "no such host"), strings.Contains(l, "dns"):
		return "unreachable", "the host in " + url + " does not resolve"
	case strings.Contains(l, "timeout"), strings.Contains(l, "deadline exceeded"),
		strings.Contains(l, "timed out"):
		return "not answering", "the request to " + url + " timed out"
	case strings.Contains(l, "connection reset"), strings.Contains(l, "eof"),
		strings.Contains(l, "broken pipe"):
		return "gone", "the connection dropped mid-answer"
	case strings.Contains(l, "401"), strings.Contains(l, "403"), strings.Contains(l, "unauthorized"),
		strings.Contains(l, "forbidden"):
		return "refusing us", "the write token was rejected"
	case strings.Contains(l, "500"), strings.Contains(l, "502"), strings.Contains(l, "503"):
		return "erroring", "the control plane answered with a server error"
	}
	return "not answering", firstLine(raw)
}

// firstLine keeps an unrecognised error to its first line: Go's dial errors are one line, but a
// wrapped one can carry a stack-ish tail that would push every row below it off the page.
func firstLine(s string) string {
	if i := strings.IndexAny(s, "\r\n"); i >= 0 {
		return strings.TrimSpace(s[:i])
	}
	return strings.TrimSpace(s)
}
