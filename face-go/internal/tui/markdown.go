package tui

import (
	"strings"
	"sync"

	"github.com/charmbracelet/glamour"
	"github.com/charmbracelet/glamour/ansi"
	"github.com/charmbracelet/glamour/styles"

	"conductor-face-go/internal/widgets"
)

// The Face's one markdown renderer (K6.4, adr/0006 §6).
//
// Two things were wrong with the old one and both are fixed here. It hard-coded
// `glamour.WithStandardStyle("dark")`, so a user on `latte` read near-white prose on a near-white
// pane and the theme system stopped at the edge of every markdown block; and it was called from
// inside `View`, which re-ran goldmark plus chroma on every keystroke, every tick and every window
// resize for text that had not changed.
//
// So: the style config is PROJECTED from the active `widgets.Theme` (markdownStyle below), and the
// rendered output is memoised on (theme, width, source). Call sites may still sit on the View path —
// that is where a pane's width is known — but glamour itself runs only when the content, the width
// or the theme actually changes, which is the behaviour the ADR asked for and what
// TestMarkdownRendersOncePerFrameStorm measures.

// mdCacheKey identifies one rendered result. The theme name is part of it, so a live theme switch
// simply misses rather than serving prose in the old palette.
type mdCacheKey struct {
	theme string
	width int
	src   string
}

var (
	mdMu sync.Mutex
	// mdOut is the memo. It is bounded rather than LRU'd: this is a TUI showing a handful of
	// documents at a handful of widths, and a plain cap is a page of code less to be wrong about.
	mdOut = map[mdCacheKey]string{}
	// mdRenderCount counts REAL glamour invocations. Tests read it; nothing in production does.
	mdRenderCount int
)

const mdCacheMax = 64

// renderMarkdown renders md as terminal markdown in the ACTIVE theme, wrapped to width, for panes
// that show free-text prose a person or an agent wrote — a session's result summary, a compiled
// agent prompt — as opposed to the dense, single-line-per-row lists elsewhere in this UI, which stay
// plain text on purpose (adr/0006 §5).
//
// Falls back to the raw text if glamour can't render it: a markdown hiccup must never blank out a
// pane.
func renderMarkdown(md string, width int) string {
	if strings.TrimSpace(md) == "" {
		return md
	}
	if width < 20 {
		width = 20
	}
	theme := widgets.CurrentTheme()
	key := mdCacheKey{theme: theme.Name, width: width, src: md}

	mdMu.Lock()
	defer mdMu.Unlock()
	if out, ok := mdOut[key]; ok {
		return out
	}

	out := renderMarkdownUncached(md, width, theme)
	if len(mdOut) >= mdCacheMax {
		mdOut = map[mdCacheKey]string{}
	}
	mdOut[key] = out
	return out
}

// renderMarkdownUncached is the slow path. Callers hold mdMu.
func renderMarkdownUncached(md string, width int, theme widgets.Theme) string {
	mdRenderCount++
	r, err := glamour.NewTermRenderer(
		glamour.WithStyles(markdownStyle(theme)),
		glamour.WithWordWrap(width),
	)
	if err != nil {
		return md
	}
	out, err := r.Render(md)
	if err != nil {
		return md
	}
	return strings.TrimRight(out, "\n")
}

// markdownStyle projects a theme's ROLES onto glamour's style config — the same discipline as every
// pane: no hex is named here, only a role.
//
// It starts from glamour's own dark or light config chosen by the theme's polarity, because the
// parts left alone (chroma's syntax theme for fenced code, the task-list glyphs, indents and
// margins) have to match the parts overridden. Every override REPLACES a pointer; nothing is written
// THROUGH one, so glamour's package-level configs are not mutated by this.
func markdownStyle(theme widgets.Theme) ansi.StyleConfig {
	sc := styles.DarkStyleConfig
	if theme.IsLight() {
		sc = styles.LightStyleConfig
	}

	hex := widgets.Hex
	text, accent, base := hex(theme.Text), hex(theme.Accent), hex(theme.Base)
	overlay, surface := hex(theme.Overlay), hex(theme.Surface)

	// Body prose is Text, and the document loses glamour's default left margin: panes here own their
	// own indentation (the history detail indents by two, the preview by none) and a margin baked
	// into the renderer fights that at every call site.
	sc.Document.Color = &text
	sc.Document.Margin = ptr(uint(0))
	sc.Text.Color = &text
	sc.Paragraph.Color = &text

	// Headings carry the accent. H1 is the only FILL — Base painted on Accent, the same figure/ground
	// flip the active tab and the search match use, so a document's title reads as a title.
	sc.H1.Color = &base
	sc.H1.BackgroundColor = &accent
	for _, h := range []*ansi.StyleBlock{&sc.Heading, &sc.H2, &sc.H3, &sc.H4, &sc.H5, &sc.H6} {
		h.Color = &accent
		h.BackgroundColor = nil
	}

	// Emphasis stays Text and leans on bold/italic, which the base config already sets: an agent's
	// **bold** is emphasis, not a new semantic colour, and colouring it would compete with the
	// headings.
	sc.Strong.Color = &text
	sc.Emph.Color = &text
	sc.Strikethrough.Color = &overlay

	// Quiet furniture: rules, list markers and block quotes recede.
	sc.HorizontalRule.Color = &surface
	sc.Item.Color = &overlay
	sc.Enumeration.Color = &overlay
	sc.BlockQuote.Color = &overlay

	// Links use the role that already means "elsewhere" everywhere else in this Face.
	blue, teal := hex(theme.Blue), hex(theme.Teal)
	sc.Link.Color = &blue
	sc.LinkText.Color = &teal
	sc.Image.Color = &blue
	sc.ImageText.Color = &teal

	// Code: Peach is this palette's "attention on a value". Inline code sits on Surface so a path or
	// an identifier reads as a chip, not just another colour.
	peach := hex(theme.Peach)
	sc.Code.Color = &peach
	sc.Code.BackgroundColor = &surface
	sc.CodeBlock.Color = &text

	// Definition lists and tables are furniture around content.
	sc.Table.Color = &text
	sc.DefinitionTerm.Color = &accent
	sc.DefinitionDescription.Color = &text

	// A task-list checkbox is the same green the Kanban gives a done card.
	green := hex(theme.Green)
	sc.Task.Color = &green

	return sc
}

func ptr[T any](v T) *T { return &v }

// markdownRenders is the number of times glamour has actually run this process. Only tests read it —
// it is how "rendered on content change, not per frame" is measured rather than asserted.
func markdownRenders() int {
	mdMu.Lock()
	defer mdMu.Unlock()
	return mdRenderCount
}

// resetMarkdownCache drops every memoised render. Called when the styles are rebuilt so a theme
// switch can never serve prose in the old palette, and by tests that want a cold start.
func resetMarkdownCache() {
	mdMu.Lock()
	defer mdMu.Unlock()
	mdOut = map[mdCacheKey]string{}
}
