package tui

import (
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// K6.4. The compiled-prompt preview is the longest single document this Face shows and, until this
// checkpoint, the one you could not read to the end: `truncateLines(prompt, paneRows-5)` cut it at
// the pane height with no scroll of any kind. adr/0006 §5 says every read-not-select body is a
// viewport; this is the last one that was not.

// openPreview drives the REAL router — `e` for Templates, `v` for the compiled preview — and feeds
// it a prompt of `lines` numbered lines. It fails if the fixture fits the pane, because every
// assertion below is about a document that outgrows its window.
func openPreview(t *testing.T, lines int) Model {
	t.Helper()
	var b strings.Builder
	b.WriteString("# Deliver session — stage F7\n\n")
	for i := 0; i < lines; i++ {
		b.WriteString("line " + itoa(i) + " of the compiled prompt\n")
	}
	b.WriteString("LAST-LINE: task --done <id> --evidence <path>\n")

	var m tea.Model = newGoldenModel(120, 30)
	m, _ = m.Update(keyMsg("e"))
	m, _ = m.Update(keyMsg("v"))
	m, _ = m.Update(MsgPromptPreview{Preview: &api.PromptPreviewDto{
		Model: "claude-opus-5", Kind: "Deliver", Prompt: b.String(),
	}})
	mm := asModel(m)
	vp := mm.templatesPreviewViewport()
	if vp.TotalLineCount() <= vp.Height() {
		t.Fatalf("fixture prompt is %d lines in a %d-row preview — it does not scroll, so this proves nothing",
			vp.TotalLineCount(), vp.Height())
	}
	return mm
}

// The owner's complaint, on the surface that still had it: open a long prompt, press `end`, and the
// last line has to be on screen. Before K6.4 no key moved this pane at all.
func TestTemplatesPreviewReachesTheEndOfALongPrompt(t *testing.T) {
	m := openPreview(t, 120)
	if strings.Contains(paneBody(m), "LAST-LINE") {
		t.Fatal("the fixture's last line is visible without scrolling — the pane is not full")
	}
	m = asModel(mustHandle(m.handleTemplatesKey("end")))
	if !strings.Contains(paneBody(m), "LAST-LINE") {
		t.Error("`end` did not reach the bottom of the compiled prompt")
	}
	if !m.templatesPreviewViewport().AtBottom() {
		t.Error("the preview viewport does not report itself at the bottom after `end`")
	}
}

// Every key in the one scroll set has to work here too, or the ADR's "one scroll idiom" is a document
// rather than a behaviour. `h`/`l` stay with the kind picker and are not in that set.
func TestTemplatesPreviewBindsTheOneScrollSet(t *testing.T) {
	for _, k := range []string{"down", "j", "up", "d", "u", "pgdown", "pgup", "end", "G", "home"} {
		m := openPreview(t, 120)
		if strings.Contains("up u pgup home", k) {
			m = asModel(mustHandle(m.handleTemplatesKey("end")))
		}
		before := paneBody(m)
		if got := paneBody(asModel(mustHandle(m.handleTemplatesKey(k)))); got == before {
			t.Errorf("preview: %q moved nothing", k)
		}
	}
}

// The offset is clamped where it is CHANGED (adr/0006 §1), so running off the end costs exactly one
// key to come back — the measurement bug #30 was found by, applied to the new viewport before it can
// grow the same defect.
func TestTemplatesPreviewOffsetIsClampedInUpdate(t *testing.T) {
	m := openPreview(t, 120)
	for i := 0; i < 400; i++ {
		m = asModel(mustHandle(m.handleTemplatesKey("down")))
	}
	vp := m.templatesPreviewViewport()
	if got, max := m.tmpl.previewVp.YOffset(), vp.TotalLineCount()-vp.Height(); got > max {
		t.Errorf("preview offset is %d against a body that stops at %d — Update let it run", got, max)
	}
	atEnd := paneBody(m)
	if paneBody(asModel(mustHandle(m.handleTemplatesKey("up")))) == atEnd {
		t.Error("400 downs past the end left the pane needing more than one `up` to move")
	}
}

// Switching kind is a new document: it must start at the top, not at wherever the previous kind was
// left. A prompt that opens half-read is worse than one that does not scroll.
func TestTemplatesPreviewStartsANewKindAtTheTop(t *testing.T) {
	m := openPreview(t, 120)
	m = asModel(mustHandle(m.handleTemplatesKey("end")))
	if m.tmpl.previewVp.YOffset() == 0 {
		t.Fatal("`end` left the offset at zero — the rest of this test would be vacuous")
	}
	tm, _ := m.Update(MsgPromptPreview{Preview: &api.PromptPreviewDto{
		Model: "claude-opus-5", Kind: "Fix", Prompt: "# Fix session\n\nshort.\n",
	}})
	next := asModel(tm)
	if got := next.tmpl.previewVp.YOffset(); got != 0 {
		t.Errorf("a freshly compiled prompt opened at offset %d, want 0", got)
	}
}

// The fidelity guard, and the reason this pane is the one surface K6.4 deliberately does NOT render
// as markdown. glamour sanitises anything that parses as an HTML tag, so the placeholders a prompt is
// full of — `<id>`, `<path>`, `<stage>` — are silently deleted. A preview whose whole job is to show
// an owner the exact bytes an agent will be handed cannot afford that, however much prettier it
// looks. The test asserts BOTH halves: the pane keeps them, and markdown really would eat them.
func TestTemplatesPreviewKeepsPlaceholdersMarkdownWouldEat(t *testing.T) {
	const src = "conductor task --done <id> --evidence <path>"

	m := openPreview(t, 60)
	if !strings.Contains(stripANSI(m.templatesPreviewBody()), "<id>") {
		t.Error("the preview dropped the <id> placeholder — the prompt it shows is not the prompt sent")
	}

	if got := stripANSI(renderMarkdown(src, 60)); strings.Contains(got, "<id>") {
		t.Skip("glamour no longer strips angle-bracket placeholders — re-evaluate rendering this pane " +
			"as markdown, the reason it stays plain has gone away")
	}
}
