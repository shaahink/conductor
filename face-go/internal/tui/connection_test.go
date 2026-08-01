package tui

import (
	"strings"
	"testing"
	"time"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
)

// step runs one message through the model and hands the concrete type back, so a test can read the
// fields the Update loop just wrote.
func step(t *testing.T, m Model, msgs ...interface{}) Model {
	t.Helper()
	for _, msg := range msgs {
		next, _ := m.Update(msg)
		got, ok := next.(Model)
		if !ok {
			t.Fatalf("Update returned %T, not a tui.Model", next)
		}
		m = got
	}
	return m
}

func liveModel() Model {
	m := New(fakeSource{}, false, "http://127.0.0.1:4317")
	m.data.Connection.Mode = api.ModeLive
	return m
}

// THE regression this checkpoint exists for. Connected had three writers with three meanings: the
// state poll set it true, a fetch error set it false, and EITHER SSE stream re-derived it as
// events||transcript. So a run whose engine was answering every poll rendered "not connected" the
// moment both streams blipped, and a dead engine with one stale stream rendered "live". After SF2.1
// the streams move their own indicators and nothing else.
func TestConnectedIsWhetherTheEngineAnswered_NotWhetherAStreamIsUp(t *testing.T) {
	m := step(t, liveModel(),
		MsgStateUpdated{State: fixedState()},
		MsgEventsConnChanged{Connected: false},
		MsgTxConnChanged{Connected: false})

	if !m.data.Connection.Connected {
		t.Error("a healthy /state poll with both SSE streams down is CONNECTED: the engine answered")
	}
	if m.data.Connection.EventsConnected || m.data.Connection.TranscriptConnected {
		t.Error("the stream indicators must still report their own (down) state")
	}

	// And the mirror image: the engine stops answering while a stream is still nominally up.
	m = step(t, m, MsgFetchError{Err: "dial tcp 127.0.0.1:4317: connectex: refused"},
		MsgEventsConnChanged{Connected: true})
	if m.data.Connection.Connected {
		t.Error("a live SSE stream cannot make a dead poll read as connected")
	}
}

// The clocks every honest surface needs. Without them "not connected — retrying…" cannot be told
// apart from a Face that gave up ten minutes ago.
func TestSetConnectedStampsTheClocksTheBannersRead(t *testing.T) {
	base := time.Date(2026, 7, 15, 10, 0, 0, 0, time.UTC)
	now := base
	timefmt.Now = func() time.Time { return now }
	t.Cleanup(func() { timefmt.Now = time.Now })

	m := step(t, liveModel(), MsgStateUpdated{State: fixedState()})
	if !m.data.Connection.LastContactAt.Equal(base) {
		t.Errorf("last contact = %v, want the moment the engine answered (%v)", m.data.Connection.LastContactAt, base)
	}

	now = base.Add(4 * time.Minute)
	m = step(t, m, MsgFetchError{Err: "boom"})
	if !m.data.Connection.Since.Equal(now) {
		t.Errorf("Since = %v, want the moment the state CHANGED (%v)", m.data.Connection.Since, now)
	}
	if !m.data.Connection.LastContactAt.Equal(base) {
		t.Error("losing the engine must not move last-contact: that is when it last ANSWERED")
	}

	// A poll that keeps failing does not restart the clock — the age is of the state, not the poll.
	now = base.Add(9 * time.Minute)
	m = step(t, m, MsgFetchError{Err: "boom again"})
	if !m.data.Connection.Since.Equal(base.Add(4 * time.Minute)) {
		t.Errorf("Since = %v, want it pinned to the transition, not the latest failure", m.data.Connection.Since)
	}
}

// A first observation is a transition too. Without this the very first disconnect has no age at all
// and the banner says "retrying" with nothing behind it.
func TestTheFirstDisconnectStillHasAnAge(t *testing.T) {
	base := time.Date(2026, 7, 15, 10, 0, 0, 0, time.UTC)
	now := base
	timefmt.Now = func() time.Time { return now }
	t.Cleanup(func() { timefmt.Now = time.Now })

	m := step(t, liveModel(), MsgFetchError{Err: "dial tcp: connectex: actively refused"})
	if m.data.Connection.Since.IsZero() {
		t.Fatal("the first failed poll must stamp Since — it is the only clock a never-connected Face has")
	}
	now = base.Add(3 * time.Minute)
	if got := engineState(m.data.Connection).Age; got != "trying for 3m" {
		t.Errorf("age = %q, want %q: with no contact ever, the honest clock is how long we have tried", got, "trying for 3m")
	}
}

// Screenshot critique #2: `mode  live — not connected` is an oxymoron, and the raw Windows dial
// error is not a sentence anyone can act on. One clause, in English, per state.
func TestEngineStateNeverSaysLiveWhenItIsNotAndNeverEchoesTheRawError(t *testing.T) {
	raw := "dial tcp 127.0.0.1:4317: connectex: No connection could be made because the target machine actively refused it."
	cases := []struct {
		name     string
		err      string
		headline string
		detail   string
	}{
		{"refused", raw, "not running", "nothing is listening on http://127.0.0.1:4317"},
		{"dns", "dial tcp: lookup nope: no such host", "unreachable", "the host in http://127.0.0.1:4317 does not resolve"},
		{"timeout", "context deadline exceeded", "not answering", "the request to http://127.0.0.1:4317 timed out"},
		{"reset", "read tcp: connection reset by peer", "gone", "the connection dropped mid-answer"},
		{"auth", "unexpected status 401 Unauthorized", "refusing us", "the write token was rejected"},
		{"server", "unexpected status 503", "erroring", "the control plane answered with a server error"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			e := tc.err
			got := engineState(api.ConnectionState{Mode: api.ModeLive, URL: "http://127.0.0.1:4317", LastError: &e})
			if got.Headline != tc.headline {
				t.Errorf("headline = %q, want %q", got.Headline, tc.headline)
			}
			if got.Detail != tc.detail {
				t.Errorf("detail = %q, want %q", got.Detail, tc.detail)
			}
			for _, banned := range []string{"connectex", "live", "0x", "WSA"} {
				if strings.Contains(got.Headline+" "+got.Detail, banned) {
					t.Errorf("the human clause leaked %q: %q — %q", banned, got.Headline, got.Detail)
				}
			}
		})
	}

	// An error the mapping does not recognise is NOT dressed up as a diagnosis: the Face reports the
	// state it can prove ("not answering") and hands back the text rather than inventing a cause.
	odd := "something nobody has seen before\nstack line that must not reach the pane"
	got := engineState(api.ConnectionState{Mode: api.ModeLive, URL: "u", LastError: &odd})
	if got.Headline != "not answering" {
		t.Errorf("an unrecognised error must report the provable state, got %q", got.Headline)
	}
	if strings.Contains(got.Detail, "\n") {
		t.Errorf("a multi-line error must be cut to its first line, got %q", got.Detail)
	}
}
