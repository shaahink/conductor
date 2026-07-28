package tui

import (
	"strings"
	"testing"
)

func TestRenderMarkdownEmptyPassthrough(t *testing.T) {
	if got := renderMarkdown("", 60); got != "" {
		t.Errorf("expected empty input to pass through unchanged, got %q", got)
	}
	if got := renderMarkdown("   ", 60); got != "   " {
		t.Errorf("expected whitespace-only input to pass through unchanged, got %q", got)
	}
}

func TestRenderMarkdownStylesBoldText(t *testing.T) {
	got := renderMarkdown("wired the **caching layer** in RunDb", 60)
	if got == "" {
		t.Fatal("expected non-empty rendered output")
	}
	if !strings.Contains(got, "caching layer") {
		t.Errorf("expected the bold text's content to still be present, got %q", got)
	}
	// Glamour strips the markdown syntax markers and re-encodes emphasis as ANSI —
	// the literal "**" markers must not survive into rendered output.
	if strings.Contains(got, "**") {
		t.Errorf("expected markdown syntax markers to be stripped, got %q", got)
	}
}

func TestRenderMarkdownNeverErrorsOnPlainText(t *testing.T) {
	got := renderMarkdown("plain sentence, no markdown syntax at all.", 60)
	if !strings.Contains(got, "plain sentence") {
		t.Errorf("expected plain text content to survive rendering, got %q", got)
	}
}
