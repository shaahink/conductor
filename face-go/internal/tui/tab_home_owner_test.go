package tui

import (
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// ownerQueueFixture is one of each shape that matters: a dated park, and a HUMAN: line the engine
// CANNOT date. The undated one is the whole reason ageSeconds is a pointer on the wire.
func ownerQueueFixture() *api.OwnerQueueDto {
	age := int64(3120)
	detail := "window spend $18.40 of $18.00"
	return &api.OwnerQueueDto{
		Count:        2,
		GeneratedUtc: "2026-08-01T10:00:00Z",
		Items: []api.OwnerQueueItemDto{
			{Id: "park", Kind: "park", Title: "Run is parked: budget window exhausted",
				Unblocks: "the whole run", Command: "conductor resume", AgeSeconds: &age, Detail: &detail},
			{Id: "human-1", Kind: "human", Title: "HUMAN: pick the release channel",
				Unblocks: "checkpoint F9.1", Command: "delete the HUMAN: line from the handoff"},
		},
	}
}

func withOwnerQueue(t *testing.T, w, h int, q *api.OwnerQueueDto) Model {
	t.Helper()
	var tm tea.Model = newGoldenModel(w, h)
	tm, _ = tm.Update(MsgOwnerQueueUpdated{Queue: q})
	return tm.(Model)
}

// SF4.2's central claim about this pane, and the one thing it can get wrong SILENTLY: an obligation
// the engine cannot date must read "age unknown", never "just now". A plain int64 on the DTO would
// decode the wire's explicit null as 0 and print the single most misleading thing a queue can say
// about an item that may have been sitting there for days.
func TestOwnerQueueRendersUnknownAgeAsUnknown(t *testing.T) {
	m := withOwnerQueue(t, 140, 40, ownerQueueFixture())
	tm, _ := m.Update(keyMsg("w"))
	frame := stripANSI(tm.(Model).View().Content)

	if !strings.Contains(frame, "age unknown") {
		t.Errorf("the undated HUMAN: entry must render \"age unknown\":\n%s", frame)
	}
	// Scoped to the ENTRY's own age line, not the whole frame: the header's "read just now" is the
	// age of the queue READ, which is genuinely just now and must not be confused with an item's age.
	for _, line := range strings.Split(frame, "\n") {
		if strings.Contains(line, "human ·") && !strings.Contains(line, "age unknown") {
			t.Errorf("an entry with no timestamp did not render as unknown — that is the lie this "+
				"pane exists to avoid: %q", strings.TrimSpace(line))
		}
	}
	// And the dated one still reads as an age, so the branch above did not swallow both.
	if !strings.Contains(frame, "52m ago") {
		t.Errorf("the dated park entry must render its real age (52m ago):\n%s", frame)
	}
}

// Every entry carries what it unblocks and the exact command that clears it — the half that made the
// owner's hand-written list worth productising. A queue that lists obligations without them is a
// to-do list, which is what this replaced.
func TestOwnerQueueShowsUnblocksAndCommand(t *testing.T) {
	m := withOwnerQueue(t, 140, 40, ownerQueueFixture())
	tm, _ := m.Update(keyMsg("w"))
	frame := stripANSI(tm.(Model).View().Content)

	for _, want := range []string{"unblocks the whole run", "$ conductor resume", "checkpoint F9.1"} {
		if !strings.Contains(frame, want) {
			t.Errorf("the owner-queue view does not show %q:\n%s", want, frame)
		}
	}
}

// A blocked-until wait has no command: the clock clears it. A blank there would read as data the
// engine forgot to send, so it gets words.
func TestOwnerQueueSaysWhenNothingClearsAnEntry(t *testing.T) {
	q := &api.OwnerQueueDto{Count: 1, GeneratedUtc: "2026-08-01T10:00:00Z", Items: []api.OwnerQueueItemDto{
		{Id: "wait", Kind: "wait", Title: "Waiting until 15:12Z", Unblocks: "checkpoint F7.3", Command: ""},
	}}
	m := withOwnerQueue(t, 140, 40, q)
	tm, _ := m.Update(keyMsg("w"))
	if frame := stripANSI(tm.(Model).View().Content); !strings.Contains(frame, "clears itself") {
		t.Errorf("an entry with no clearing command must say so:\n%s", frame)
	}
}

// Zero is a real answer — the queue was computed and nothing is owed — and the pane says it out loud
// rather than looking empty or broken.
func TestOwnerQueueSaysNothingIsOwedOutLoud(t *testing.T) {
	m := withOwnerQueue(t, 140, 40, &api.OwnerQueueDto{Count: 0, GeneratedUtc: "2026-08-01T10:00:00Z"})
	tm, _ := m.Update(keyMsg("w"))
	if frame := stripANSI(tm.(Model).View().Content); !strings.Contains(frame, "Nothing is waiting on you") {
		t.Errorf("an empty queue must say so:\n%s", frame)
	}
}

// Before the first poll answers, the pane must NOT claim the queue is empty — "nothing has been
// asked yet" and "nothing is owed" are different sentences and only one of them is true here.
func TestOwnerQueueDoesNotClaimEmptyBeforeFirstPoll(t *testing.T) {
	var tm tea.Model = newGoldenModel(140, 40)
	tm, _ = tm.Update(keyMsg("w"))
	frame := stripANSI(tm.(Model).View().Content)
	if strings.Contains(frame, "Nothing is waiting on you") {
		t.Errorf("an unanswered queue must not read as an empty one:\n%s", frame)
	}
	if !strings.Contains(frame, "Waiting for the first") {
		t.Errorf("the pane must say it has not been answered yet:\n%s", frame)
	}
}

// STYLE.md, twice over: `w` must be resolved by handleKey's GLOBAL switch, before any pane handler —
// that is the precedence a letter opening a surface needs to work from every pane. Driven through
// Update (not the pane handler) for exactly the reason plan_test.go's drive() missed a live
// collision. Home is also the wrong tab to test from, so this starts on Report.
func TestOwnerQueueKeyIsGlobalAndToggles(t *testing.T) {
	var tm tea.Model = newGoldenModel(140, 40)
	tm, _ = tm.Update(MsgOwnerQueueUpdated{Queue: ownerQueueFixture()})
	tm, _ = tm.Update(keyMsg("r")) // Report — a pane with its own up/down keys
	if tm.(Model).tab != TabReport {
		t.Fatalf("precondition: r must open Report, got %v", tm.(Model).tab)
	}
	tm, _ = tm.Update(keyMsg("w"))
	if m := tm.(Model); m.tab != TabHome || m.homeView != homeOwnerQueue {
		t.Fatalf("w from Report must open Home's owner queue: tab=%v view=%v", m.tab, m.homeView)
	}
	// Pressing it again closes it — a full-pane list needs a way back that is not "go somewhere else".
	tm, _ = tm.Update(keyMsg("w"))
	if m := tm.(Model); m.tab != TabHome || m.homeView != homeLanding {
		t.Fatalf("w must toggle back to the landing: tab=%v view=%v", m.tab, m.homeView)
	}
	// esc peels the queue layer before it leaves Home, or `w` would be the only way off the list.
	tm, _ = tm.Update(keyMsg("w"))
	tm, _ = tm.Update(keyMsg("esc"))
	if m := tm.(Model); m.tab != TabHome || m.homeView != homeLanding {
		t.Fatalf("esc must back out of the queue to the landing, not to Agent: tab=%v view=%v", m.tab, m.homeView)
	}
	// And `h` lands on the landing, not on whatever view was last open.
	tm, _ = tm.Update(keyMsg("w"))
	tm, _ = tm.Update(keyMsg("h"))
	if m := tm.(Model); m.homeView != homeLanding {
		t.Fatalf("h must land on the landing, got view=%v", m.homeView)
	}
}

// SF1.3 capped the strip at ten tabs and made the next surface fold instead. The owner queue IS that
// next surface, so this pins that it did not quietly become an eleventh tab.
func TestOwnerQueueDidNotAddAnEleventhTab(t *testing.T) {
	if tabCount != 10 {
		t.Fatalf("the tab strip is capped at ten (SF1.3, adr/0004) — tabCount is %d", tabCount)
	}
	for i := 0; i < int(tabCount); i++ {
		if tabKey[i] == "w" {
			t.Fatalf("w became tab mnemonic %d (%s) — it is Home's owner-queue fold, not a tab", i, tabNames[i])
		}
	}
	if _, folded := foldedTabKey["w"]; folded {
		t.Fatal("w is not a folded TAB mnemonic — it is a view inside Home")
	}
}

// Trap 11: tabKey is the single source for mnemonics but the help legend is hand-maintained string
// concatenation, so a key that is not in the legend is a key nobody finds.
func TestHelpLegendNamesTheOwnerQueueKey(t *testing.T) {
	var tm tea.Model = newGoldenModel(120, 40)
	tm, _ = tm.Update(keyMsg("?"))
	if help := stripANSI(tm.(Model).View().Content); !strings.Contains(help, "w owner queue") {
		t.Errorf("the help card does not document w — the owner queue is unreachable-looking:\n%s", help)
	}
}

// The landing shows the queue itself when it is short, hands off to `w` when it is not, and never
// lets it eat the page: Home cannot scroll, so the cap is what keeps the rest of the landing alive.
func TestHomeLandingShowsQueueAndHandsOffWhenLong(t *testing.T) {
	items := make([]api.OwnerQueueItemDto, 0, 6)
	for i := 0; i < 6; i++ {
		items = append(items, api.OwnerQueueItemDto{
			Id: "g", Kind: "ownerGate", Title: "Stage F" + string(rune('1'+i)) + " needs approval",
			Unblocks: "stage F" + string(rune('1'+i)), Command: "conductor approve F" + string(rune('1'+i))})
	}
	m := withOwnerQueue(t, 160, 50, &api.OwnerQueueDto{Count: 6, GeneratedUtc: "2026-08-01T10:00:00Z", Items: items})
	frame := stripANSI(m.View().Content)

	if !strings.Contains(frame, "Owner queue (6)") {
		t.Errorf("the landing must carry the queue with its count:\n%s", frame)
	}
	if !strings.Contains(frame, "+3 more") {
		t.Errorf("a queue longer than the landing's cap must hand off to w, got:\n%s", frame)
	}
	// The landing is still a landing: Next steps is the section the height clamp used to eat.
	if !strings.Contains(frame, "Next steps") {
		t.Errorf("the owner queue pushed Next steps off the landing:\n%s", frame)
	}
}

// A failed poll must not blank a queue already on screen — the owner's obligations do not stop
// existing because one fetch timed out, and a section that empties itself on a hiccup teaches people
// to stop trusting it.
func TestOwnerQueueSurvivesAFailedPoll(t *testing.T) {
	m := withOwnerQueue(t, 140, 40, ownerQueueFixture())
	tm, _ := m.Update(MsgOwnerQueueUpdated{Err: "dial tcp 127.0.0.1:4317: connection refused"})
	tm, _ = tm.(Model).Update(keyMsg("w"))
	frame := stripANSI(tm.(Model).View().Content)
	if !strings.Contains(frame, "Run is parked") {
		t.Errorf("a failed poll blanked a queue that was already on screen:\n%s", frame)
	}
	if !strings.Contains(frame, "last poll failed") {
		t.Errorf("stale rows must be labelled stale:\n%s", frame)
	}
}

// FU-OWNER-13, the Go half. Between a saved telegram block and the next session boundary the engine's
// in-memory PlanConfig is still the pre-edit one, so every other field on this payload reads exactly
// as it would for a plan nobody ever configured. reloadPending is the only thing that tells the two
// apart, and it means WAITING — not unconfigured, which is how a block saved thirty seconds ago came
// back under a reason advising the owner to add the block they had just added.
func TestTelegramReloadPendingReadsAsWaitingNotUnconfigured(t *testing.T) {
	var tm tea.Model = newGoldenModel(140, 40)
	tm, _ = tm.Update(keyMsg("g"))
	reason := "a plan reload is queued and takes effect at the next session boundary"
	tm, _ = tm.Update(MsgTelegramStatusUpdated{Status: &api.TelegramStatusDto{
		Configured: false, Started: false, HasToken: true, PollIntervalSeconds: 4,
		WillDeliver: false, WillDeliverReason: &reason, ReloadPending: true,
	}})
	frame := stripANSI(tm.(Model).View().Content)

	if strings.Contains(frame, "not configured") {
		t.Errorf("a saved-but-queued telegram block still reads as \"not configured\" — that is "+
			"FU-OWNER-13 verbatim:\n%s", frame)
	}
	if !strings.Contains(frame, "waiting for the next session boundary") {
		t.Errorf("a pending reload must render as waiting:\n%s", frame)
	}
	if !strings.Contains(frame, "reload is queued") {
		t.Errorf("the engine's own reason must still be shown:\n%s", frame)
	}
}

// The same flag over a LIVE block does not become a verdict: a queued reload does not supply a
// missing token, so "will not deliver yet" stands and only the save is acknowledged.
func TestTelegramReloadPendingDoesNotMaskARealBlocker(t *testing.T) {
	var tm tea.Model = newGoldenModel(140, 40)
	tm, _ = tm.Update(keyMsg("g"))
	reason := "no bot token — set it here or in CONDUCTOR_TELEGRAM_BOT_TOKEN"
	tm, _ = tm.Update(MsgTelegramStatusUpdated{Status: &api.TelegramStatusDto{
		Configured: true, Started: true, HasToken: false, PollIntervalSeconds: 4,
		WillDeliver: false, WillDeliverReason: &reason, ReloadPending: true,
	}})
	frame := stripANSI(tm.(Model).View().Content)

	if !strings.Contains(frame, "will not deliver yet") {
		t.Errorf("a pending reload must not overwrite a real blocker's verdict:\n%s", frame)
	}
	if !strings.Contains(frame, "no bot token") {
		t.Errorf("the real reason must survive:\n%s", frame)
	}
	if !strings.Contains(frame, "plan reload queued") {
		t.Errorf("the save should still be acknowledged:\n%s", frame)
	}
}
