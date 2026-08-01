package tui

// SF2.2: the panes are on one clock vocabulary, and the timestamps the wire has always carried are
// finally on screen.
//
// These drive the real renderers rather than asserting against timefmt directly — timefmt has its own
// tests, and the defects this checkpoint exists to kill were all WIRING: a formatter duplicated into a
// pane that then drifted, a label naming a timezone the value was not in, and three DTO columns that
// no renderer ever read.

import (
	"strings"
	"testing"
	"time"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/timefmt"
	"conductor-face-go/internal/widgets"
)

// The three elapsed formatters were near-copies: widgets.FmtWall and tui.formatProcessRuntime were
// byte-identical in different packages, and tui.fmtDuration was the same arithmetic with a space and
// an hour bucket. Now every surface renders the same span the same way.
func TestOneElapsedFormatterAcrossEverySurface(t *testing.T) {
	pinClock(t, time.Date(2026, 7, 15, 10, 0, 0, 0, time.UTC))
	start := "2026-07-15T09:00:00Z"
	for _, sec := range []float64{7, 41, 60, 123, 3599, 3600, 11070, 100740} {
		d := time.Duration(sec * float64(time.Second))
		exited := time.Date(2026, 7, 15, 9, 0, 0, 0, time.UTC).Add(d).Format(time.RFC3339)
		proc := formatProcessRuntime(api.ProcessDto{StartedUtc: start, ExitedUtc: &exited})
		if bar := widgets.FmtWall(sec); bar != proc {
			t.Errorf("%vs: top bar renders %q, Processes renders %q", sec, bar, proc)
		}
		if rep := fmtDuration(d); rep != proc {
			t.Errorf("%vs: Report renders %q, Processes renders %q", sec, rep, proc)
		}
	}
}

// The regression the collapse fixes: neither %dm%02ds copy had an hour bucket, so a gate that had been
// running since breakfast rendered "184m30s" in Processes while Report called the same span "3h 04m".
func TestAProcessPastAnHourRendersHoursNotAHundredMinutes(t *testing.T) {
	pinClock(t, time.Date(2026, 7, 15, 12, 4, 30, 0, time.UTC))
	got := formatProcessRuntime(api.ProcessDto{StartedUtc: "2026-07-15T09:00:00Z", Alive: true})
	if got != "3h 04m" {
		t.Errorf("runtime = %q, want 3h 04m", got)
	}
	if strings.Contains(got, "184") {
		t.Errorf("the minutes-only formatter is still in the Processes tab: %q", got)
	}
}

// A live process is measured against the Face's clock, and that clock has to be the pinnable one —
// otherwise the Processes frame re-dates itself on every render and can never be a golden.
func TestALiveProcessIsMeasuredAgainstThePinnableClock(t *testing.T) {
	p := api.ProcessDto{StartedUtc: "2026-07-15T09:58:40Z", Alive: true}
	pinClock(t, time.Date(2026, 7, 15, 10, 0, 0, 0, time.UTC))
	if got := formatProcessRuntime(p); got != "1m 20s" {
		t.Errorf("runtime = %q, want 1m 20s", got)
	}
	pinClock(t, time.Date(2026, 7, 15, 10, 30, 0, 0, time.UTC))
	if got := formatProcessRuntime(p); got != "31m 20s" {
		t.Errorf("runtime = %q, want 31m 20s — the clock moved and the runtime did not", got)
	}
}

// Screenshot critique #6, in the pane that had it worst. The spine's detail line used to append the
// literal " UTC" to a clock it had already converted into the viewer's local zone: every timestamp in
// the tab was labelled with a timezone it was not in.
func TestTheSpineDetailNoLongerLabelsLocalTimeAsUtc(t *testing.T) {
	east := time.FixedZone("TEST+2", 2*60*60)
	prevLoc := timefmt.Location
	timefmt.Location = east
	t.Cleanup(func() { timefmt.Location = prevLoc })
	pinClock(t, time.Date(2026, 7, 15, 10, 8, 0, 0, time.UTC))

	m := liveModel()
	m.width, m.height = 110, 40
	m = step(t, m, MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: []api.TimelineEntryDto{
		{Utc: "2026-07-15T10:00:00Z", Kind: "stage", Description: "stage F7 entered"},
	}}})

	got := stripANSI(m.timelineDetail())
	if strings.Contains(got, "UTC") {
		t.Errorf("the spine detail still claims UTC over a local clock:\n%s", got)
	}
	// 10:00Z renders as 12:00 for a +02:00 reader, and the pinned now is 10:08Z — eight minutes on.
	if !strings.Contains(got, "12:00") {
		t.Errorf("the detail must render the local wall-clock:\n%s", got)
	}
	if !strings.Contains(got, "8m ago") {
		t.Errorf("the detail must say how long ago it was:\n%s", got)
	}
}

// An event from yesterday and an event from an hour ago rendered identically before SF2.2 — the whole
// of critique #6 ("a run spanning midnight is unreadable").
func TestTheSpineDetailDatesAnEventThatIsNotFromToday(t *testing.T) {
	pinClock(t, time.Date(2026, 7, 15, 0, 30, 0, 0, time.UTC))
	m := liveModel()
	m.width, m.height = 110, 40
	m = step(t, m, MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: []api.TimelineEntryDto{
		{Utc: "2026-07-14T23:50:00Z", Kind: "gate", Description: "gate test: pass"},
	}}})
	if got := stripANSI(m.timelineDetail()); !strings.Contains(got, "Jul 14 23:50") {
		t.Errorf("an event from before midnight must carry its date:\n%s", got)
	}
}

// startedUtc/endedUtc have been on /sessions since the tab existed and were rendered nowhere: the
// history list was a column of anonymous rows you could not place in the day.
func TestTheSessionsViewSaysWhenEachSessionRan(t *testing.T) {
	pinClock(t, time.Date(2026, 7, 15, 10, 8, 0, 0, time.UTC))
	ended := "2026-07-15T09:58:04Z"
	m := liveModel()
	m.width, m.height = 110, 40
	m = step(t, m, MsgSessionsUpdated{Sessions: &api.SessionsDto{Sessions: []api.SessionRowDto{
		{Number: 12, StageId: "F7", Kind: "Deliver", Attempt: 2, StartedUtc: "2026-07-15T10:00:00Z"},
		{Number: 11, StageId: "F7", Kind: "Deliver", Attempt: 1, StartedUtc: "2026-07-15T09:12:30Z", EndedUtc: &ended},
	}}})
	m = m.openSessionsForTest()

	body, _ := m.renderSessionsView()
	got := stripANSI(body)
	if !strings.Contains(got, "10:00") || !strings.Contains(got, "09:12") {
		t.Errorf("every session row must carry its start time:\n%s", got)
	}
	// #12 is selected (index 0) and still running.
	if !strings.Contains(got, "10:00 · 8m ago → still running") {
		t.Errorf("a live session's detail must say it has not ended:\n%s", got)
	}

	m.sessionSelected = 1
	got = stripANSI(sessionsBody(m))
	if !strings.Contains(got, "09:12 · 55m ago → 09:58  (45m 34s)") {
		t.Errorf("a finished session's detail must render start, end and span:\n%s", got)
	}
}

// The measured trap: ledger.created_at and bugs.created_at come off SQLite's datetime('now'), not
// RFC3339. This is the test that would have caught an RFC3339-only reader rendering nothing at all.
func TestKnowledgeRowsRenderTheirAgeFromTheSqliteWireFormat(t *testing.T) {
	pinClock(t, time.Date(2026, 8, 1, 0, 40, 30, 0, time.UTC))
	m := liveModel()
	m.width, m.height = 110, 40
	m = step(t, m, MsgKnowledgeUpdated{
		Ledger: &api.LedgerDto{Entries: []api.LedgerEntryDto{
			{Id: 119, Kind: "trap", Content: "truncate() cuts raw runes", CreatedAt: "2026-08-01 00:37:30"},
		}},
		Bugs: &api.BugsDto{Bugs: []api.BugDto{
			{Id: 18, Title: "bottom bar offers a live agent while offline", Severity: "medium",
				Status: "open", CreatedAt: "2026-07-31 23:26:39"},
		}},
	})

	got := stripANSI(strings.Join(m.knowledgeLines(), "\n"))
	if !strings.Contains(got, "3m ago") {
		t.Errorf("the ledger row lost its age — the SQLite wire format was rejected:\n%s", got)
	}
	if !strings.Contains(got, "1h ago") {
		t.Errorf("the bug row lost its age; how long a bug has been open is the point of the list:\n%s", got)
	}
}

// A row whose timestamp the Face cannot read renders with no clock rather than an invented one.
func TestAnUnreadableKnowledgeTimestampRendersNoClockAtAll(t *testing.T) {
	m := liveModel()
	m.width, m.height = 110, 40
	m = step(t, m, MsgKnowledgeUpdated{
		Ledger: &api.LedgerDto{Entries: []api.LedgerEntryDto{{Id: 1, Kind: "note", Content: "content", CreatedAt: ""}}},
		Bugs:   &api.BugsDto{},
	})
	got := stripANSI(strings.Join(m.knowledgeLines(), "\n"))
	if strings.Contains(got, "ago") || strings.Contains(got, "56y") {
		t.Errorf("an unset created_at produced a fabricated age:\n%s", got)
	}
}

// "delivering" is a claim about a poll loop; lastPollUtc is the only thing on the pane that can
// contradict it, and it was on the DTO and rendered nowhere.
func TestTelegramSaysHowLongAgoItLastPolled(t *testing.T) {
	pinClock(t, time.Date(2026, 7, 15, 10, 8, 0, 0, time.UTC))
	name, poll := "conductor_bot", "2026-07-15T10:05:00Z"
	m := liveModel()
	m.width, m.height = 110, 40
	m = step(t, m, MsgTelegramStatusUpdated{Status: &api.TelegramStatusDto{
		Configured: true, Started: true, HasToken: true, WillDeliver: true,
		AllowedChatIds: []string{"111222333"}, PollIntervalSeconds: 4,
		BotUsername: &name, LastPollUtc: &poll,
	}})
	body, _ := m.renderTelegramPane()
	if got := stripANSI(body); !strings.Contains(got, "last poll 3m ago") {
		t.Errorf("a delivering bot must say when it last polled:\n%s", got)
	}
}

// A status with no poll on record says nothing rather than "last poll 56y ago".
func TestTelegramWithNoPollOnRecordSaysNothingAboutPolling(t *testing.T) {
	name := "conductor_bot"
	m := liveModel()
	m.width, m.height = 110, 40
	m = step(t, m, MsgTelegramStatusUpdated{Status: &api.TelegramStatusDto{
		Configured: true, Started: true, HasToken: true, WillDeliver: true,
		AllowedChatIds: []string{"111222333"}, BotUsername: &name,
	}})
	body, _ := m.renderTelegramPane()
	if got := stripANSI(body); strings.Contains(got, "last poll") {
		t.Errorf("the pane invented a poll that never happened:\n%s", got)
	}
}

// openSessionsForTest puts the History tab on its Sessions view through the real key path, so the
// assertions above go through the same routing a keypress does.
func (m Model) openSessionsForTest() Model {
	var t tea.Model = m
	t, _ = t.Update(keyMsg("h"))
	t, _ = t.Update(keyMsg("s"))
	return t.(Model)
}

// sessionsBody is renderSessionsView's body half; the help string is not what these tests read.
func sessionsBody(m Model) string {
	body, _ := m.renderSessionsView()
	return body
}
