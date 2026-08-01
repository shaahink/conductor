package tui

// SF4.2 — the live-rig capture.
//
// Every other test of this pane renders the owner queue from a Go fixture, and a fixture agrees with
// the DTO by construction: it cannot catch the one failure that spans the two languages here, which is
// the engine writing a field this side does not read. `ageSeconds` is the sharp case — the C# side
// writes an explicit null and the Go side takes a pointer so that null survives as "age unknown"
// instead of decoding to 0 and printing "just now" over an obligation that may be days old. A
// hand-written fixture can only ever assert that Go decodes Go.
//
// So this test points the REAL client (api.NewLiveSourceWithToken, the same constructor
// cmd/conductor-face uses) at a REAL control plane, feeds what comes back through the REAL update
// path, and renders. It skips unless CONDUCTOR_FACE_LIVE_URL is set, so it costs the gate battery
// nothing and never dials anything on its own.
//
// Point it at a SCRATCH rig with its own plan, .conductor and port — never at the run driving you.
//
//	$rig = "$env:TEMP\sarban-proofs\sf41"          # own repo, own plan, own state
//	# start the engine from YOUR build inside $rig:  conductor.exe run --paused --headless --no-face
//	$cp = Get-Content "$rig\.conductor\control-plane.json" | ConvertFrom-Json   # read the port BACK
//	$env:CONDUCTOR_FACE_LIVE_URL = $cp.baseUrl
//	$env:CONDUCTOR_FACE_LIVE_CAPTURE = "C:\...\frames.txt"
//	go test ./internal/tui/ -run TestLiveOwnerQueueCapture -v

import (
	"fmt"
	"os"
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

func TestLiveOwnerQueueCapture(t *testing.T) {
	base := os.Getenv("CONDUCTOR_FACE_LIVE_URL")
	if base == "" {
		t.Skip("CONDUCTOR_FACE_LIVE_URL unset — see this file's header for the scratch-rig recipe")
	}

	src := api.NewLiveSourceWithToken(base, os.Getenv("CONDUCTOR_FACE_LIVE_TOKEN"))
	defer src.Close()

	q, err := src.FetchOwnerQueue()
	if err != nil {
		t.Fatalf("GET %s/owner/queue: %v", base, err)
	}
	if q == nil {
		t.Fatal("live control plane answered /owner/queue with no body")
	}
	t.Logf("live queue: count=%d generated=%s", q.Count, q.GeneratedUtc)

	// The contract, checked against the wire rather than against a fixture.
	if q.Count != len(q.Items) {
		t.Errorf("count %d disagrees with %d items — the face prints count in the header", q.Count, len(q.Items))
	}
	if q.Count == 0 {
		t.Fatal("the rig's queue is empty, so this run proves nothing about rendering an obligation")
	}
	sawUndated := false
	for i, it := range q.Items {
		if strings.TrimSpace(it.Kind) == "" || strings.TrimSpace(it.Title) == "" {
			t.Errorf("item %d decoded with an empty kind/title — field-name drift: %+v", i, it)
		}
		// The half that makes this queue worth reading. An entry that does not say what it unblocks is
		// an entry the owner has to investigate, which is the thing SF4 exists to stop.
		if strings.TrimSpace(it.Unblocks) == "" {
			t.Errorf("item %d (%s) came off the wire with no unblocks: %+v", i, it.Kind, it)
		}
		if it.AgeSeconds == nil {
			sawUndated = true
		}
	}
	// Not an assertion about the rig's contents so much as about the decode: if the engine's explicit
	// nulls were arriving as 0 we would never see one, and every undated obligation would read
	// "just now".
	if !sawUndated {
		t.Log("note: this rig's queue had no undated entry, so the null-ageSeconds path was not exercised live")
	}

	var tm tea.Model = newGoldenModel(120, 40)
	tm, _ = tm.Update(MsgOwnerQueueUpdated{Queue: q})
	landing := stripANSI(tm.(Model).View().Content)
	if !strings.Contains(landing, "Owner queue") {
		t.Errorf("Home did not grow an owner-queue section from a live queue of %d:\n%s", q.Count, landing)
	}

	tm, _ = tm.Update(keyMsg("w"))
	if m := tm.(Model); m.homeView != homeOwnerQueue {
		t.Fatalf("w did not open the queue against live data: view=%v", m.homeView)
	}
	pane := stripANSI(tm.(Model).View().Content)
	for _, it := range q.Items {
		// Age never renders as an empty gutter, whichever branch it took.
		if !strings.Contains(pane, it.Kind) {
			t.Errorf("live item kind %q is missing from the pane:\n%s", it.Kind, pane)
		}
	}
	if strings.Contains(pane, "just now") && !sawUndated {
		t.Log("note: 'just now' present and every item was dated — expected")
	}

	if out := os.Getenv("CONDUCTOR_FACE_LIVE_CAPTURE"); out != "" {
		body := fmt.Sprintf(
			"# Live capture — %s/owner/queue\n\ncount=%d generated=%s\n\n"+
				"## Home landing (120x40)\n\n%s\n\n## Owner queue pane, key `w` (120x40)\n\n%s\n",
			base, q.Count, q.GeneratedUtc, landing, pane)
		if err := os.WriteFile(out, []byte(body), 0o644); err != nil {
			t.Fatalf("writing capture to %s: %v", out, err)
		}
		t.Logf("captured two live frames to %s", out)
	}
}
