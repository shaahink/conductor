package widgets

import (
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

func toolLine(text string, name string) api.TranscriptLineDto {
	l := api.TranscriptLineDto{Kind: "tool", Text: text}
	if name != "" {
		l.V, l.Tool = 2, &api.ToolCallDto{Name: name}
	}
	return l
}

// The fold summary used to cut the last line at BYTE 47. Any multi-byte glyph straddling that
// boundary came out as U+FFFD — silent corruption in the one row that stands for a dozen hidden
// ones. The clip is rune-aware now; this pins it with a line that is all multi-byte.
func TestFoldSummaryNeverSplitsARune(t *testing.T) {
	long := "Read " + strings.Repeat("測", 80) // every rune 3 bytes, far past the 50-rune tail
	out := foldTools([]api.TranscriptLineDto{toolLine(long, "Read")})
	if len(out) != 1 {
		t.Fatalf("expected one folded row, got %d", len(out))
	}
	if strings.ContainsRune(out[0].Text, '�') {
		t.Errorf("fold summary corrupted a rune: %q", out[0].Text)
	}
	for _, r := range out[0].Text {
		if r != '測' && !strings.ContainsRune("Read ·×:0123456789 tolcasfd…", r) {
			t.Errorf("unexpected rune %q in %q", r, out[0].Text)
		}
	}
}

// The fold names the MIX now, from the v2 wire's tool names — "12 tool calls folded · Bash ×7 …" —
// instead of only the tail of the last call. Counting is by tool name, ties broken by name so the
// same run always folds to the same string.
func TestFoldSummaryNamesTheToolMix(t *testing.T) {
	var src []api.TranscriptLineDto
	for i := 0; i < 7; i++ {
		src = append(src, toolLine("Bash grep -rn foo", "Bash"))
		src = append(src, api.TranscriptLineDto{Kind: "result", Text: "ok"})
	}
	src = append(src, toolLine("Edit face-go/internal/tui/digest.go (+12/-3)", "Edit"))
	src = append(src, toolLine("conductor task_update SF3.1 -> in_progress", "mcp__conductor-tasks__task_update"))

	out := foldTools(src)
	if len(out) != 1 {
		t.Fatalf("expected one folded row, got %d", len(out))
	}
	got := out[0].Text
	for _, want := range []string{"9 tool calls folded", "Bash ×7", "Edit", "task_update", "last: conductor task_update"} {
		if !strings.Contains(got, want) {
			t.Errorf("fold summary %q missing %q", got, want)
		}
	}
	if strings.Contains(got, "mcp__") {
		t.Errorf("fold summary kept the MCP server prefix: %q", got)
	}
}

// A v1 line carries no structure — the name still comes off the front of the engine's one-liner,
// which is where it has always been.
func TestFoldSummaryFallsBackToTheLineTextForV1(t *testing.T) {
	out := foldTools([]api.TranscriptLineDto{
		toolLine("read src/Conductor/Core/Gating/GateCache.cs", ""),
		toolLine("read src/Conductor/Core/Store/RunDb.cs", ""),
	})
	if len(out) != 1 {
		t.Fatalf("expected one folded row, got %d", len(out))
	}
	if !strings.Contains(out[0].Text, "read ×2") {
		t.Errorf("v1 fold lost the tool name: %q", out[0].Text)
	}
}

// One call is one call: the summary must not say "1 tool calls", and a fold of one still tells you
// which one it was.
func TestFoldSummaryOfOneCallReadsSingular(t *testing.T) {
	out := foldTools([]api.TranscriptLineDto{toolLine("Bash dotnet build Conductor.slnx", "Bash")})
	if !strings.HasPrefix(out[0].Text, "1 tool call folded") {
		t.Errorf("singular fold: %q", out[0].Text)
	}
}
