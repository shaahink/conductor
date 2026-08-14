package tui

import (
	"encoding/json"
	"fmt"
	"image/color"
	"os"
	"path/filepath"
	"strings"
	"testing"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/widgets"
)

// rgbSeq is how a colour actually reaches the terminal: lipgloss emits truecolor SGR with decimal
// channels. Searching a rendered frame for this substring is the only way to assert what a pane is
// PAINTED in — the styles themselves are package vars a state-only assertion would happily agree
// with while the frame stayed the old scheme.
func rgbSeq(c color.Color) string {
	r, g, b, _ := c.RGBA()
	return fmt.Sprintf("%d;%d;%d", r>>8, g>>8, b>>8)
}

func restoreDefaultTheme(t *testing.T) {
	t.Helper()
	t.Cleanup(func() {
		if err := ApplyTheme(widgets.DefaultThemeName); err != nil {
			t.Fatal(err)
		}
	})
}

// isolateConfig points os.UserConfigDir() at a temp dir, so these tests never read or clobber the
// developer's real ~/.config/conductor-face/config.json — and so a machine that happens to have a
// persisted `nord` cannot turn the goldens red.
func isolateConfig(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	// os.UserConfigDir reads %AppData% on Windows and $XDG_CONFIG_HOME elsewhere. Set both so this
	// exercises the real ConfigPath rather than a test seam that could drift from it.
	t.Setenv("AppData", dir)
	t.Setenv("XDG_CONFIG_HOME", dir)
	return dir
}

// TestLiveThemeSwitchRepaintsTheWholeFrame is the regression for the one thing that makes theming
// hard here: the shared style vars in `widgets` and in `tui` each capture their colour by value at
// init, and neither package can rebuild the other's. Rebuild only one and the frame renders in two
// schemes at once — a bug no state assertion can see, because the accessors would report the new
// theme either way. So: render, switch, render, and assert NO trace of the old scheme survives
// anywhere in the frame.
func TestLiveThemeSwitchRepaintsTheWholeFrame(t *testing.T) {
	restoreDefaultTheme(t)
	isolateConfig(t)

	mocha, _ := widgets.ThemeByName("mocha")
	latte, _ := widgets.ThemeByName("latte")

	if err := ApplyTheme("mocha"); err != nil {
		t.Fatal(err)
	}
	var tm tea.Model = newGoldenModel(132, 40)
	before := asModel(tm).View().Content
	if !strings.Contains(before, rgbSeq(mocha.Text)) {
		t.Fatalf("fixture frame does not paint mocha's Text (%s) — the test cannot prove anything",
			rgbSeq(mocha.Text))
	}

	if err := ApplyTheme("latte"); err != nil {
		t.Fatal(err)
	}
	after := asModel(tm).View().Content

	// widgets' half: the sidebar/top bar are rendered from widgets' own style vars.
	// tui's half: textStyle/subtleStyle/accentStyle live in view.go.
	// Both packages paint Text somewhere in a full frame, so one absence check covers both halves
	// only if we also check a colour each package owns exclusively — hence the two below.
	for role, c := range map[string]color.Color{
		"Text":    mocha.Text,
		"Accent":  mocha.Accent,
		"Overlay": mocha.Overlay,
		"Base":    mocha.Base,
	} {
		if strings.Contains(after, rgbSeq(c)) {
			t.Errorf("after switching to latte the frame still paints mocha's %s (%s) — a package's "+
				"style vars were not rebuilt, so the frame is in two themes at once", role, rgbSeq(c))
		}
	}
	if !strings.Contains(after, rgbSeq(latte.Text)) {
		t.Errorf("after switching to latte the frame never paints latte's Text (%s)", rgbSeq(latte.Text))
	}

	// The window background is part of the repaint, not an afterthought: a light scheme on a dark
	// terminal is unreadable if this stays behind.
	if got := asModel(tm).View().BackgroundColor; got != latte.Base {
		t.Errorf("View().BackgroundColor = %v after switching to latte, want %v", got, latte.Base)
	}
}

// TestPaletteThemeVerbSwitchesAndPersists drives the switch the way a user does — through the
// palette, via handleKey, NOT by calling the pane handler. The ledger's TEST-BLIND-SPOT note is
// exactly this: plan_test.go's drive() called pane handlers directly and so never saw the global
// key precedence that actually routes them.
func TestPaletteThemeVerbSwitchesAndPersists(t *testing.T) {
	restoreDefaultTheme(t)
	dir := isolateConfig(t)

	var tm tea.Model = newGoldenModel(132, 40)
	tm, _ = tm.Update(keyMsg(":"))
	for _, ch := range "theme nord" {
		tm, _ = tm.Update(keyMsg(string(ch)))
	}
	// The query filtered to exactly one row; enter runs it.
	if got := len(asModel(tm).filteredVerbs()); got != 1 {
		t.Fatalf("query %q matched %d verbs, want exactly 1", "theme nord", got)
	}
	tm, _ = tm.Update(specialKey('\r'))

	if got := widgets.CurrentTheme().Name; got != "nord" {
		t.Errorf("after :theme nord the live theme is %q", got)
	}
	if asModel(tm).cmd != CmdNone {
		t.Error("the palette stayed open after running a theme verb")
	}
	// It must have repainted, not just recorded.
	nord, _ := widgets.ThemeByName("nord")
	if !strings.Contains(asModel(tm).View().Content, rgbSeq(nord.Text)) {
		t.Error("the frame does not paint nord's Text after :theme nord")
	}

	// …and it must STICK: the whole point of persisting is the next launch.
	data, err := os.ReadFile(filepath.Join(dir, "conductor-face", "config.json"))
	if err != nil {
		t.Fatalf("no config written after a palette theme switch: %v", err)
	}
	var c Config
	if err := json.Unmarshal(data, &c); err != nil {
		t.Fatalf("config.json is not valid JSON: %v", err)
	}
	if c.Theme != "nord" {
		t.Errorf("config.json says theme=%q, want nord", c.Theme)
	}
}

// TestThemeVerbNeverReachesTheControlPlane pins the Local contract. Theme rows sit in the palette
// beside verbs that POST; if one were dispatched as a control verb it would 404 a real engine and
// do nothing at all in --demo, which is where most people will try it.
func TestThemeVerbNeverReachesTheControlPlane(t *testing.T) {
	for _, v := range allVerbs {
		if strings.HasPrefix(v.Key, themeVerbPrefix) && v.Group != groupFace {
			t.Errorf("verb %q is a theme row outside the Face group", v.Key)
		}
		if v.Group == groupFace && !v.Local {
			t.Errorf("verb %q is in the Face group but not Local — it would be POSTed to the engine", v.Key)
		}
		if v.Local && v.Group != groupFace {
			t.Errorf("verb %q is Local but not in the Face group — the group is what tells a reader "+
				"of the palette that it never reaches the engine", v.Key)
		}
		if v.Local && !v.Safe {
			t.Errorf("verb %q is Local and unsafe; the confirm path posts a control verb and would "+
				"drop the local action", v.Key)
		}
		// KS2.4 put a second kind of verb in the Face group (the run switcher), so "Face group" no
		// longer means "theme row". What must still hold — and is the claim this test was written for
		// — is that every Local verb is ANSWERED locally: a Local key nobody dispatches falls through
		// runLocalVerb's CutPrefix into "unknown command", silently, in the one group where nothing
		// reaches an engine that could complain.
		if v.Local && v.Key != switchVerb && !strings.HasPrefix(v.Key, themeVerbPrefix) {
			t.Errorf("verb %q is Local but no local dispatch answers it", v.Key)
		}
	}
}

// TestPaletteOffersEveryTheme pins the derivation: the Face group is built from the theme registry,
// so a scheme added in widgets must appear here without anyone editing this package.
func TestPaletteOffersEveryTheme(t *testing.T) {
	for _, name := range widgets.ThemeNames() {
		want := themeVerbPrefix + name
		found := false
		for _, v := range allVerbs {
			if v.Key == want {
				found = true
				if v.Desc == "" {
					t.Errorf("palette row %q has no description", want)
				}
			}
		}
		if !found {
			t.Errorf("theme %q has no palette row (%q)", name, want)
		}
	}
}

// TestPaletteMarksActiveTheme: which scheme is live belongs where you switch it, and the marker
// reuses the ⚠ gutter, so it must survive a switch rather than being baked at init.
func TestPaletteMarksActiveTheme(t *testing.T) {
	restoreDefaultTheme(t)
	isolateConfig(t)

	if err := ApplyTheme("gruvbox"); err != nil {
		t.Fatal(err)
	}
	var tm tea.Model = newGoldenModel(132, 40)
	tm, _ = tm.Update(keyMsg(":"))
	for _, ch := range "theme" {
		tm, _ = tm.Update(keyMsg(string(ch)))
	}
	frame := stripANSI(asModel(tm).View().Content)
	if !strings.Contains(frame, "● theme gruvbox") {
		t.Error("the palette does not mark gruvbox as the live theme while it is active")
	}
	if strings.Contains(frame, "● theme mocha") {
		t.Error("the palette marks mocha as live while gruvbox is active")
	}
}

func TestResolveStartupThemeOrder(t *testing.T) {
	restoreDefaultTheme(t)

	t.Run("flag wins over persisted", func(t *testing.T) {
		isolateConfig(t)
		if err := SaveConfig(Config{Theme: "nord"}); err != nil {
			t.Fatal(err)
		}
		if err := ResolveStartupTheme("gruvbox"); err != nil {
			t.Fatal(err)
		}
		if got := widgets.CurrentTheme().Name; got != "gruvbox" {
			t.Errorf("--theme gruvbox lost to the persisted nord (got %q)", got)
		}
	})

	t.Run("flag does not rewrite the saved choice", func(t *testing.T) {
		isolateConfig(t)
		if err := SaveConfig(Config{Theme: "nord"}); err != nil {
			t.Fatal(err)
		}
		if err := ResolveStartupTheme("gruvbox"); err != nil {
			t.Fatal(err)
		}
		// --theme is a one-launch override. If it persisted, a single `--theme x` would silently
		// destroy the user's saved preference with no way back to it.
		if got := LoadConfig().Theme; got != "nord" {
			t.Errorf("--theme overwrote the persisted choice (config now %q, want nord)", got)
		}
	})

	t.Run("persisted wins over default", func(t *testing.T) {
		isolateConfig(t)
		if err := SaveConfig(Config{Theme: "latte"}); err != nil {
			t.Fatal(err)
		}
		if err := ResolveStartupTheme(""); err != nil {
			t.Fatal(err)
		}
		if got := widgets.CurrentTheme().Name; got != "latte" {
			t.Errorf("persisted latte lost to the default (got %q)", got)
		}
	})

	t.Run("empty everything is the default", func(t *testing.T) {
		isolateConfig(t)
		if err := ResolveStartupTheme(""); err != nil {
			t.Fatal(err)
		}
		if got := widgets.CurrentTheme().Name; got != widgets.DefaultThemeName {
			t.Errorf("no flag + no config landed on %q, want %q", got, widgets.DefaultThemeName)
		}
	})

	t.Run("bad flag is a hard error", func(t *testing.T) {
		isolateConfig(t)
		if err := ResolveStartupTheme("dracula"); err == nil {
			t.Error("--theme dracula started anyway; a named scheme that does not exist is a typo " +
				"worth failing on, not worth silently ignoring")
		}
	})

	t.Run("corrupt persisted theme falls back rather than bricking", func(t *testing.T) {
		isolateConfig(t)
		if err := SaveConfig(Config{Theme: "no-such-scheme"}); err != nil {
			t.Fatal(err)
		}
		if err := ResolveStartupTheme(""); err != nil {
			t.Fatalf("a stale config must not stop the Face from starting: %v", err)
		}
		if got := widgets.CurrentTheme().Name; got != widgets.DefaultThemeName {
			t.Errorf("stale config landed on %q, want the default %q", got, widgets.DefaultThemeName)
		}
	})
}

func TestConfigRoundTripsAndToleratesGarbage(t *testing.T) {
	dir := isolateConfig(t)

	if got := LoadConfig().Theme; got != "" {
		t.Errorf("a missing config should read as no preference, got %q", got)
	}
	if err := SaveConfig(Config{Theme: "nord"}); err != nil {
		t.Fatal(err)
	}
	if got := LoadConfig().Theme; got != "nord" {
		t.Errorf("round-trip lost the theme: %q", got)
	}

	path := filepath.Join(dir, "conductor-face", "config.json")
	if err := os.WriteFile(path, []byte("{not json"), 0o644); err != nil {
		t.Fatal(err)
	}
	if got := LoadConfig().Theme; got != "" {
		t.Errorf("a corrupt config should read as no preference, got %q", got)
	}
}
