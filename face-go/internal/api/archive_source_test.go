package api

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// KS2.2 — attaching to a FINISHED run. The engine serves it through a read-only archive plane that
// mints no write token, so `conductor face` hands the Face a url and nothing else. These tests pin the
// two halves of what that has to mean on this side of the wire:
//
//   - the source says it cannot write, which is what every write affordance in the TUI keys off
//     (tab_home.go:475 and its siblings), so nothing offers a button that could only fail; and
//   - the reads all still work, because a token was never needed for them.
//
// The refusal itself is pinned on the engine side (KS2_2ArchiveControlPlaneTests): 405 with "this run
// is finished", never the live plane's 401 token hint.

// archivePlane is the shape of the read-only plane: reads answer, everything else is refused with the
// state of the run rather than a credential hint.
func archivePlane(t *testing.T) *httptest.Server {
	t.Helper()
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			w.Header().Set("Allow", "GET")
			w.WriteHeader(http.StatusMethodNotAllowed)
			_ = json.NewEncoder(w).Encode(map[string]any{
				"accepted": false,
				"error":    "this run is finished — the archive is read-only, so nothing can be written to it",
			})
			return
		}
		switch r.URL.Path {
		case "/state":
			_ = json.NewEncoder(w).Encode(map[string]any{
				"planName": "archived plan", "status": "completed", "runId": "run-archive",
				"doneCount": 3, "totalCount": 3,
			})
		case "/sessions":
			_ = json.NewEncoder(w).Encode(map[string]any{"sessions": []any{}})
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
}

func TestArchiveSourceCannotWrite(t *testing.T) {
	server := archivePlane(t)
	defer server.Close()

	// No token, because an archive plane has none to give.
	source := NewLiveSourceWithToken(server.URL, "")
	defer source.Close()

	if source.HasWriteToken() {
		t.Fatal("a source attached to a finished run claims it can write")
	}
}

func TestArchiveSourceStillReads(t *testing.T) {
	server := archivePlane(t)
	defer server.Close()

	source := NewLiveSourceWithToken(server.URL, "")
	defer source.Close()

	state, err := source.FetchState()
	if err != nil {
		t.Fatalf("reading /state without a token failed: %v", err)
	}
	if state.PlanName != "archived plan" || state.Status != "completed" {
		t.Errorf("state came back as %+v", state)
	}
	if _, err := source.FetchSessions(); err != nil {
		t.Errorf("reading /sessions without a token failed: %v", err)
	}
}

// A write attempted anyway must surface the run's state, not an invitation to find a token — the
// difference between "look harder" and "this cannot be done".
func TestArchiveRefusalNamesTheFinishedRunNotAToken(t *testing.T) {
	server := archivePlane(t)
	defer server.Close()

	source := NewLiveSourceWithToken(server.URL, "")
	defer source.Close()

	_, err := source.PostControl(ControlRequestDto{Command: "pause"})
	if err == nil {
		t.Fatal("a control command against a finished run was accepted")
	}
	got := strings.ToLower(err.Error())
	if strings.Contains(got, "token") || strings.Contains(got, "control-plane.json") {
		t.Errorf("the refusal points at a credential: %v", err)
	}
}
