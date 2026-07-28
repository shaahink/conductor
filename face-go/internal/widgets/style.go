package widgets

import (
	"fmt"
	"image/color"
	"strings"

	"charm.land/lipgloss/v2"
)

// Theme is the complete role set a scheme must fill (U3.1). Panes never name a colour — only a role
// — so a new scheme is a new Theme value in `themes` and nothing else. STYLE.md's Themes table is
// this struct, and the role comments here are its source.
type Theme struct {
	// Name is the key this theme is selected by (--theme, the palette verb, config.json).
	Name string
	// Description is the one-line the palette row and --theme's help show. It lives here so the
	// palette's Face group can be DERIVED from this registry instead of hand-listed beside it.
	Description string

	// Structure — the frame the panes sit in.
	Base      color.Color // window background (also tea.View.BackgroundColor)
	Mantle    color.Color // panels: top/bottom bars, tab strip — one step back from Base
	Surface   color.Color // borders, rules
	Selection color.Color // selection background
	Overlay   color.Color // muted text
	Text      color.Color // primary text

	// Accent + semantics — what a colour MEANS. Each is used both as text on Base and, for Accent
	// and Yellow, as a FILL with Base painted on top (active tab, search match), so every one of
	// these must contrast with Base in both directions — TestEveryThemeIsLegibleOnItsBase pins it.
	Accent color.Color // brand / selection / active tab / keycaps
	Blue   color.Color // active / in-progress
	Green  color.Color // success / done
	Red    color.Color // fail / destructive
	Yellow color.Color // warn / running
	Peach  color.Color // cost / attention
	Teal   color.Color // tools
	Sky    color.Color // system

	// Quiet states — deliberately dimmer than Text, and ordered: Pending (a checkpoint nobody has
	// reached) recedes furthest, Skipped sits between Pending and Overlay.
	Pending color.Color // todo / pending
	Skipped color.Color // skipped / thinking
}

// DefaultThemeName is the scheme the Face starts in and the one every golden pins.
const DefaultThemeName = "mocha"

// themeOrder is the presentation order everywhere a list of themes is shown (the palette, --theme's
// help, the unknown-theme error): the dark default first, its light companion second, then the two
// imports. TestThemeOrderCoversEveryTheme pins it against `themes` so adding a scheme without
// placing it fails loudly instead of leaving it undiscoverable.
var themeOrder = []string{"mocha", "latte", "nord", "gruvbox"}

var themes = map[string]Theme{
	// Catppuccin Mocha — the scheme the Face shipped with, unchanged. The goldens pin it.
	"mocha": {
		Name:        "mocha",
		Description: "Catppuccin Mocha - dark, the default",
		Base:        lipgloss.Color("#1E1E2E"),
		Mantle:      lipgloss.Color("#181825"),
		Surface:     lipgloss.Color("#313244"),
		Selection:   lipgloss.Color("#45475A"),
		Overlay:     lipgloss.Color("#6C7086"),
		Text:        lipgloss.Color("#CDD6F4"),
		Accent:      lipgloss.Color("#CBA6F7"),
		Blue:        lipgloss.Color("#89B4FA"),
		Green:       lipgloss.Color("#A6E3A1"),
		Red:         lipgloss.Color("#F38BA8"),
		Yellow:      lipgloss.Color("#F9E2AF"),
		Peach:       lipgloss.Color("#FAB387"),
		Teal:        lipgloss.Color("#94E2D5"),
		Sky:         lipgloss.Color("#89DCEB"),
		Pending:     lipgloss.Color("#585B70"),
		Skipped:     lipgloss.Color("#7F849C"),
	},

	// Catppuccin Latte — the light companion. Structure is stock Latte; Accent/Blue/Red are stock
	// too. Green/Yellow/Peach/Teal/Sky are stock Latte DARKENED in-hue: Catppuccin tunes those for
	// syntax highlighting, where ~2.5:1 on Latte's near-white base is accepted, but this Face paints
	// them as status text and as a search-match fill with Base on top — stock Latte yellow gives
	// 2.3:1 there, which is the kind of thing U3.2 exists to catch. Darkened, they land ~4.4:1.
	"latte": {
		Name:        "latte",
		Description: "Catppuccin Latte - light",
		Base:        lipgloss.Color("#EFF1F5"),
		Mantle:      lipgloss.Color("#E6E9EF"),
		Surface:     lipgloss.Color("#CCD0DA"),
		Selection:   lipgloss.Color("#BCC0CC"),
		Overlay:     lipgloss.Color("#7C7F93"),
		Text:        lipgloss.Color("#4C4F69"),
		Accent:      lipgloss.Color("#8839EF"),
		Blue:        lipgloss.Color("#1E66F5"),
		Green:       lipgloss.Color("#2F7D1F"),
		Red:         lipgloss.Color("#D20F39"),
		Yellow:      lipgloss.Color("#9C6606"),
		Peach:       lipgloss.Color("#B54F08"),
		Teal:        lipgloss.Color("#12787E"),
		Sky:         lipgloss.Color("#0272A8"),
		Pending:     lipgloss.Color("#9CA0B0"),
		Skipped:     lipgloss.Color("#6C6F85"),
	},

	// Nord — Polar Night structure, Frost + Aurora semantics, all official nord0–nord15 except two:
	// Mantle is nord0 darkened (Nord has nothing below nord0, and every other scheme here has the
	// bars recede from the window rather than stand proud), and Overlay is nord3 lightened toward
	// nord4 — nord3 itself is 1.7:1 on nord0, fine for a comment in an editor, not for the muted
	// text this Face runs its bottom bar on. nord3 keeps its dim role as Pending.
	"nord": {
		Name:        "nord",
		Description: "Nord - dark, cool blues",
		Base:        lipgloss.Color("#2E3440"),
		Mantle:      lipgloss.Color("#272C36"),
		Surface:     lipgloss.Color("#3B4252"),
		Selection:   lipgloss.Color("#434C5E"),
		Overlay:     lipgloss.Color("#7B88A1"),
		Text:        lipgloss.Color("#D8DEE9"),
		Accent:      lipgloss.Color("#B48EAD"),
		Blue:        lipgloss.Color("#81A1C1"),
		Green:       lipgloss.Color("#A3BE8C"),
		Red:         lipgloss.Color("#BF616A"),
		Yellow:      lipgloss.Color("#EBCB8B"),
		Peach:       lipgloss.Color("#D08770"),
		Teal:        lipgloss.Color("#8FBCBB"),
		Sky:         lipgloss.Color("#88C0D0"),
		Pending:     lipgloss.Color("#4C566A"),
		Skipped:     lipgloss.Color("#616E88"),
	},

	// Gruvbox dark (medium) — stock throughout. Sky takes neutral_blue so it stays distinguishable
	// from Blue (bright_blue); Gruvbox has no separate cyan, and "system" lines are quiet anyway.
	"gruvbox": {
		Name:        "gruvbox",
		Description: "Gruvbox - dark, warm retro",
		Base:        lipgloss.Color("#282828"),
		Mantle:      lipgloss.Color("#1D2021"),
		Surface:     lipgloss.Color("#3C3836"),
		Selection:   lipgloss.Color("#504945"),
		Overlay:     lipgloss.Color("#928374"),
		Text:        lipgloss.Color("#EBDBB2"),
		Accent:      lipgloss.Color("#D3869B"),
		Blue:        lipgloss.Color("#83A598"),
		Green:       lipgloss.Color("#B8BB26"),
		Red:         lipgloss.Color("#FB4934"),
		Yellow:      lipgloss.Color("#FABD2F"),
		Peach:       lipgloss.Color("#FE8019"),
		Teal:        lipgloss.Color("#8EC07C"),
		Sky:         lipgloss.Color("#458588"),
		Pending:     lipgloss.Color("#665C54"),
		Skipped:     lipgloss.Color("#7C6F64"),
	},
}

// current is the scheme in force. Every col* var below mirrors one of its roles.
var current Theme

// The live palette. These stay package vars (rather than reads through `current`) because widgets
// across this package already render through them at draw time; ApplyTheme reassigns them.
var (
	colBase     color.Color // window background
	colMantle   color.Color // panels: top/bottom bars, tab strip
	colSurface  color.Color // borders, rules
	colSurface1 color.Color // selection background
	colOverlay  color.Color // muted text
	colText     color.Color // primary text

	colMauve  color.Color // accent / brand / selection
	colBlue   color.Color // active / info
	colGreen  color.Color // success / done
	colRed    color.Color // fail / destructive
	colYellow color.Color // warn / running
	colPeach  color.Color // cost / attention
	colTeal   color.Color // tools
	colSky    color.Color // system

	colPending color.Color // todo / pending
	colSkipped color.Color // skipped / thinking
)

// The derived styles. Unlike the col* vars these capture their colour BY VALUE at construction, so a
// palette swap alone leaves them painting the old scheme — rebuildStyles is what actually repaints
// this package, and ApplyTheme is the only thing that should call it.
var (
	brandStyle        lipgloss.Style
	sidebarTitleStyle lipgloss.Style

	stageDoneStyle    lipgloss.Style
	stageActiveStyle  lipgloss.Style
	stageFailStyle    lipgloss.Style
	stageTodoStyle    lipgloss.Style
	stageSkippedStyle lipgloss.Style

	txThinkingStyle     lipgloss.Style
	txThinkingMoreStyle lipgloss.Style
	txToolStyle         lipgloss.Style
	txToolNameStyle     lipgloss.Style
	txToolArgStyle      lipgloss.Style
	txResultStyle       lipgloss.Style
	txStderrStyle       lipgloss.Style
	txSystemStyle       lipgloss.Style
	txAgentStyle        lipgloss.Style
	txRawStyle          lipgloss.Style
	txTimeStyle         lipgloss.Style
	txMatchStyle        lipgloss.Style

	gatePassStyle    lipgloss.Style
	gateRunningStyle lipgloss.Style
	gateFailStyle    lipgloss.Style
	gatePendingStyle lipgloss.Style
	gateSkipStyle    lipgloss.Style

	dimStyle lipgloss.Style
)

func init() {
	// A built-in theme that does not resolve is a programming error, not a user error: the Face has
	// no usable styles at all until this runs, and it runs before the tui package's own var block
	// (Go initialises an imported package completely first).
	if err := ApplyTheme(DefaultThemeName); err != nil {
		panic(err)
	}
}

// ApplyTheme repaints this package: it swaps the live palette AND rebuilds every derived style var.
// It is deliberately ONE function per package — the tui package has its own (tui.ApplyTheme, which
// calls this one first). Both must run at startup and on every switch: this package alone cannot
// reach tui's style vars, so repainting only half leaves the frame in two themes at once.
func ApplyTheme(name string) error {
	t, ok := themes[NormalizeThemeName(name)]
	if !ok {
		return fmt.Errorf("unknown theme %q (choose one of: %s)", name, strings.Join(ThemeNames(), ", "))
	}
	current = t

	colBase, colMantle, colSurface = t.Base, t.Mantle, t.Surface
	colSurface1, colOverlay, colText = t.Selection, t.Overlay, t.Text
	colMauve, colBlue, colGreen, colRed = t.Accent, t.Blue, t.Green, t.Red
	colYellow, colPeach, colTeal, colSky = t.Yellow, t.Peach, t.Teal, t.Sky
	colPending, colSkipped = t.Pending, t.Skipped

	rebuildStyles()
	return nil
}

func rebuildStyles() {
	brandStyle = lipgloss.NewStyle().Foreground(colMauve).Bold(true)
	sidebarTitleStyle = lipgloss.NewStyle().Foreground(colMauve).Bold(true)

	stageDoneStyle = lipgloss.NewStyle().Foreground(colGreen)
	stageActiveStyle = lipgloss.NewStyle().Foreground(colBlue).Bold(true)
	stageFailStyle = lipgloss.NewStyle().Foreground(colRed)
	stageTodoStyle = lipgloss.NewStyle().Foreground(colPending)
	stageSkippedStyle = lipgloss.NewStyle().Foreground(colSkipped)

	txThinkingStyle = lipgloss.NewStyle().Foreground(colSkipped).Italic(true)
	// The collapse tail is quieter than the thought it hangs under — it is scaffolding, not content.
	txThinkingMoreStyle = lipgloss.NewStyle().Foreground(colOverlay).Italic(true)
	txToolStyle = lipgloss.NewStyle().Foreground(colTeal)
	// A tool call reads name-first: the name bold in the tool colour, its one-line argument dim
	// beside it (Claude Code's convention). txToolStyle stays the fold-summary / fallback style.
	txToolNameStyle = lipgloss.NewStyle().Foreground(colTeal).Bold(true)
	txToolArgStyle = lipgloss.NewStyle().Foreground(colOverlay)
	txResultStyle = lipgloss.NewStyle().Foreground(colGreen)
	txStderrStyle = lipgloss.NewStyle().Foreground(colRed)
	txSystemStyle = lipgloss.NewStyle().Foreground(colSky)
	txAgentStyle = lipgloss.NewStyle().Foreground(colText)
	txRawStyle = lipgloss.NewStyle().Foreground(colOverlay)
	txTimeStyle = lipgloss.NewStyle().Foreground(colPending)
	txMatchStyle = lipgloss.NewStyle().Background(colYellow).Foreground(colBase)

	gatePassStyle = lipgloss.NewStyle().Foreground(colGreen)
	gateRunningStyle = lipgloss.NewStyle().Foreground(colBlue)
	gateFailStyle = lipgloss.NewStyle().Foreground(colRed)
	gatePendingStyle = lipgloss.NewStyle().Foreground(colPending)
	gateSkipStyle = lipgloss.NewStyle().Foreground(colSkipped)

	dimStyle = lipgloss.NewStyle().Foreground(colOverlay)
}

// NormalizeThemeName is the one place a user-supplied name is canonicalised, so `--theme MOCHA`, the
// palette verb, and a hand-edited config.json all resolve identically.
func NormalizeThemeName(name string) string {
	return strings.ToLower(strings.TrimSpace(name))
}

// ThemeNames lists the curated schemes in presentation order.
func ThemeNames() []string {
	out := make([]string, len(themeOrder))
	copy(out, themeOrder)
	return out
}

// ThemeByName resolves a scheme without applying it (the palette renders each theme's own accent).
func ThemeByName(name string) (Theme, bool) {
	t, ok := themes[NormalizeThemeName(name)]
	return t, ok
}

// CurrentTheme is the scheme in force. Read it for its Name (the palette marks the active row);
// colours should still come from the role accessors below.
func CurrentTheme() Theme { return current }

// Exported palette accessors so the tui package shares the one scheme instead of re-hardcoding
// hexes. They read the LIVE palette, which is why every style built inside a render func follows a
// theme switch for free — only vars captured at init need a rebuild.
func Base() color.Color      { return colBase }
func Mantle() color.Color    { return colMantle }
func Surface() color.Color   { return colSurface }
func Selection() color.Color { return colSurface1 }
func Overlay() color.Color   { return colOverlay }
func Text() color.Color      { return colText }
func Accent() color.Color    { return colMauve }
func Blue() color.Color      { return colBlue }
func Green() color.Color     { return colGreen }
func Red() color.Color       { return colRed }
func Yellow() color.Color    { return colYellow }
func Peach() color.Color     { return colPeach }
func Teal() color.Color      { return colTeal }
func Sky() color.Color       { return colSky }
func Pending() color.Color   { return colPending }

func dim(s string) string    { return dimStyle.Render(s) }
func green(s string) string  { return lipgloss.NewStyle().Foreground(colGreen).Render(s) }
func red(s string) string    { return lipgloss.NewStyle().Foreground(colRed).Render(s) }
func blue(s string) string   { return lipgloss.NewStyle().Foreground(colSky).Render(s) }
func cyan(s string) string   { return lipgloss.NewStyle().Foreground(colMauve).Render(s) }
func purple(s string) string { return lipgloss.NewStyle().Foreground(colTeal).Render(s) }

// SpinnerFrames is the braille spinner shared by the top bar and any pane that shows liveness.
var SpinnerFrames = []string{"⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"}

// Spinner returns the frame glyph for an arbitrary monotonically increasing frame counter.
func Spinner(frame int) string {
	if frame < 0 {
		frame = -frame
	}
	return SpinnerFrames[frame%len(SpinnerFrames)]
}

// StatusColor maps an engine run status ("Running", "Paused", "NeedsAttention", …) to its
// semantic colour, so the top bar and the agent strip always agree.
func StatusColor(status string) color.Color {
	switch normalizeStatus(status) {
	case "running":
		return colBlue
	case "paused":
		return colYellow
	case "attention":
		return colRed
	case "completed":
		return colGreen
	default:
		return colOverlay
	}
}

// StatusBadge renders the run status as an upper-case coloured badge, e.g. "▶ RUNNING".
func StatusBadge(status string) string {
	norm := normalizeStatus(status)
	glyph := map[string]string{
		"running":   "▶",
		"paused":    "⏸",
		"attention": "⚠",
		"completed": "✓",
	}[norm]
	if glyph == "" {
		glyph = "·"
	}
	label := norm
	if label == "" {
		label = "unknown"
	}
	return lipgloss.NewStyle().Foreground(StatusColor(status)).Bold(true).
		Render(glyph + " " + strings.ToUpper(label))
}

func normalizeStatus(status string) string {
	s := strings.ToLower(status)
	switch {
	case strings.Contains(s, "run"):
		return "running"
	case strings.Contains(s, "pause"):
		return "paused"
	case strings.Contains(s, "attention"), strings.Contains(s, "human"), strings.Contains(s, "stall"), strings.Contains(s, "fail"):
		return "attention"
	case strings.Contains(s, "complete"), strings.Contains(s, "done"):
		return "completed"
	default:
		return s
	}
}

func truncate(s string, max int) string {
	if max < 1 {
		return ""
	}
	r := []rune(s)
	if len(r) <= max {
		return s
	}
	if max == 1 {
		return "…"
	}
	return string(r[:max-1]) + "…"
}
