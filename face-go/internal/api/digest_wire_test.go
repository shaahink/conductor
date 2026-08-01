package api

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
)

// testdata/sessions_wire.json is a REAL capture: GET /sessions off this repo's own live control
// plane (SF3.1, session 13), three rows kept and the long strings clipped, nothing renamed. That
// matters more than it sounds — a hand-written fixture proves a decoder agrees with whoever wrote
// the fixture, which is how the Face went five stages decoding no digest at all while the engine
// served one on every row since SC7.2.
func loadWireSessions(t *testing.T) SessionsDto {
	t.Helper()
	raw, err := os.ReadFile("testdata/sessions_wire.json")
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	var dto SessionsDto
	if err := json.Unmarshal(raw, &dto); err != nil {
		t.Fatalf("decode /sessions: %v", err)
	}
	return dto
}

func TestSessionsWireCarriesTheEngineDigest(t *testing.T) {
	dto := loadWireSessions(t)
	if len(dto.Sessions) == 0 {
		t.Fatal("fixture decoded to zero sessions")
	}
	for _, s := range dto.Sessions {
		if s.Digest == nil {
			t.Fatalf("session #%d: digest dropped on decode — the wire carries one", s.Number)
		}
		d := s.Digest
		if d.ToolCalls == 0 {
			t.Errorf("session #%d: toolCalls decoded as 0", s.Number)
		}
		if d.DistinctTools == 0 {
			t.Errorf("session #%d: distinctTools decoded as 0", s.Number)
		}
		if len(d.Mix) == 0 {
			t.Errorf("session #%d: mix decoded empty", s.Number)
		}
		if len(d.FilesTouched) == 0 || d.FileWrites == 0 {
			t.Errorf("session #%d: filesTouched/fileWrites decoded empty (%d files, %d writes)",
				s.Number, len(d.FilesTouched), d.FileWrites)
		}
		for _, c := range d.Mix {
			if c.Name == "" || c.Count == 0 {
				t.Errorf("session #%d: mix entry decoded blank: %+v", s.Number, c)
			}
		}
	}
}

// The engine ranks Mix and FilesTouched (count descending, then name) so that two readers cannot
// order the same session's tools two ways. The Face must therefore NOT re-sort — this pins that the
// order it receives is already the order it should render.
func TestSessionDigestArrivesRankedFromTheEngine(t *testing.T) {
	for _, s := range loadWireSessions(t).Sessions {
		for _, list := range [][]DigestCountDto{s.Digest.Mix, s.Digest.FilesTouched} {
			for i := 1; i < len(list); i++ {
				prev, cur := list[i-1], list[i]
				if cur.Count > prev.Count {
					t.Errorf("session #%d: wire order is not ranked: %s ×%d before %s ×%d",
						s.Number, prev.Name, prev.Count, cur.Name, cur.Count)
				}
			}
		}
	}
}

// A tool line on the v2 wire, verbatim out of this repo's .conductor/transcript.jsonl. The Face
// reads `text` (the engine's one-liner) for display and `tool` for structure; decoding neither used
// to be possible, because TranscriptLineDto declared neither field.
const v2ToolLine = `{"seq":1895,"ts":"2026-08-01T01:22:33.1773353+00:00","sessionId":"13","kind":"tool",` +
	`"text":"Bash grep -rn ToolLine.Render src/Conductor --include=*.cs | head -20",` +
	`"v":2,"tool":{"name":"Bash","fields":{"command":"grep -rn ToolLine.Render src/Conductor --include=*.cs | head -20",` +
	`"purpose":"Where ToolLine.Render is used"}}}`

func TestTranscriptLineDecodesTheV2Structure(t *testing.T) {
	var line TranscriptLineDto
	if err := json.Unmarshal([]byte(v2ToolLine), &line); err != nil {
		t.Fatalf("decode transcript line: %v", err)
	}
	if line.V != 2 {
		t.Errorf("schema version: got %d, want 2", line.V)
	}
	if line.Tool == nil {
		t.Fatal("tool structure dropped on decode")
	}
	if line.Tool.Name != "Bash" {
		t.Errorf("tool name: got %q, want Bash", line.Tool.Name)
	}
	if got := line.Tool.Fields["purpose"]; got != "Where ToolLine.Render is used" {
		t.Errorf("tool field purpose: got %q", got)
	}
	// The one-liner is the engine's, not something the Face rebuilds from the fields.
	if !strings.HasPrefix(line.Text, "Bash grep -rn") {
		t.Errorf("text: got %q", line.Text)
	}
}

// A v1 line (no `v`, no `tool`) still decodes — the server upgrades what it can on the way out, and
// what it cannot the Face must render anyway rather than dropping the row.
func TestTranscriptLineWithoutStructureStillDecodes(t *testing.T) {
	var line TranscriptLineDto
	raw := `{"seq":7,"ts":"2026-08-01T01:22:33Z","sessionId":"2","kind":"tool","text":"Edit {\"file_path\":\"C:\\\\code"}`
	if err := json.Unmarshal([]byte(raw), &line); err != nil {
		t.Fatalf("decode v1 line: %v", err)
	}
	if line.V != 0 || line.Tool != nil {
		t.Errorf("v1 line invented structure: v=%d tool=%+v", line.V, line.Tool)
	}
	if line.Tool.ShortName() != "" {
		t.Errorf("ShortName on a nil tool must be empty, got %q", line.Tool.ShortName())
	}
}

func TestToolShortNameStripsTheMcpServerPrefix(t *testing.T) {
	cases := map[string]string{
		"mcp__conductor-tasks__bg_start":    "bg_start",
		"mcp__conductor-tasks__task_update": "task_update",
		"Bash":                              "Bash",
		"":                                  "",
		"trailing__":                        "trailing__",
	}
	for in, want := range cases {
		tool := &ToolCallDto{Name: in}
		if got := tool.ShortName(); got != want {
			t.Errorf("ShortName(%q) = %q, want %q", in, got, want)
		}
	}
}
