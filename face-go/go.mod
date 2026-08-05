module conductor-face-go

go 1.26.0

// K6.2: bubbles/v2 joins this block. v2.1.1 is the version whose module path is charm.land/bubbles/v2
// -- the one that matches bubbletea v2 and lipgloss v2, and the one soft-serve imports. The Face uses
// its viewport (adr/0006) and its key.Binding type; list and table are deliberately NOT adopted.
//
// K1.3: this block is what the Face imports itself. glamour and harmonica used to sit below marked
// `// indirect` while `internal/tui/markdown.go` and `internal/tui/anim.go` imported them directly --
// `go mod tidy` moved them up here, and module_intent_test.go now fails the build if the file drifts
// back into misdescribing its own graph.
require (
	charm.land/bubbles/v2 v2.1.1
	charm.land/bubbletea/v2 v2.0.8
	charm.land/lipgloss/v2 v2.0.5
	github.com/charmbracelet/glamour v1.0.0
	github.com/charmbracelet/harmonica v0.2.0
	github.com/charmbracelet/x/term v0.2.2
)

// The graph carries TWO lipgloss majors on purpose, and that is not a bug to "fix" by unifying them:
// the Face styles with charm.land/lipgloss/v2 (above), while glamour v1.0.0 still renders through
// github.com/charmbracelet/lipgloss v1 (below). v1 is therefore INDIRECT and must stay that way --
// a direct v1 import would give the same widget two incompatible Style types. The guard test pins it.

require (
	github.com/alecthomas/chroma/v2 v2.20.0 // indirect
	github.com/aymanbagabas/go-osc52/v2 v2.0.1 // indirect
	github.com/aymerick/douceur v0.2.0 // indirect
	github.com/charmbracelet/colorprofile v0.4.3 // indirect
	github.com/charmbracelet/lipgloss v1.1.1-0.20250404203927-76690c660834 // indirect
	github.com/charmbracelet/ultraviolet v0.0.0-20260703014108-f5a850f9c2b7 // indirect
	github.com/charmbracelet/x/ansi v0.11.7 // indirect
	github.com/charmbracelet/x/cellbuf v0.0.15 // indirect
	github.com/charmbracelet/x/exp/slice v0.0.0-20250327172914-2fdc97757edf // indirect
	github.com/charmbracelet/x/termios v0.1.1 // indirect
	github.com/charmbracelet/x/windows v0.2.2 // indirect
	github.com/clipperhouse/displaywidth v0.11.0 // indirect
	github.com/clipperhouse/uax29/v2 v2.7.0 // indirect
	github.com/dlclark/regexp2 v1.11.5 // indirect
	github.com/gorilla/css v1.0.1 // indirect
	github.com/lucasb-eyer/go-colorful v1.4.0 // indirect
	github.com/mattn/go-isatty v0.0.20 // indirect
	github.com/mattn/go-runewidth v0.0.24 // indirect
	github.com/microcosm-cc/bluemonday v1.0.27 // indirect
	github.com/muesli/cancelreader v0.2.2 // indirect
	github.com/muesli/reflow v0.3.0 // indirect
	github.com/muesli/termenv v0.16.0 // indirect
	github.com/rivo/uniseg v0.4.7 // indirect
	github.com/xo/terminfo v0.0.0-20220910002029-abceb7e1c41e // indirect
	github.com/yuin/goldmark v1.7.13 // indirect
	github.com/yuin/goldmark-emoji v1.0.6 // indirect
	golang.org/x/net v0.38.0 // indirect
	golang.org/x/sync v0.21.0 // indirect
	golang.org/x/sys v0.46.0 // indirect
	golang.org/x/term v0.36.0 // indirect
	golang.org/x/text v0.30.0 // indirect
)
