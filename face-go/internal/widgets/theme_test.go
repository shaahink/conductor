package widgets

import (
	"image/color"
	"math"
	"testing"

	"charm.land/lipgloss/v2"
)

// lipglossColor is a hex literal used only as a contrast REFERENCE in these tests — never a palette
// entry. Real colours belong in the registry.
func lipglossColor(hex string) color.Color { return lipgloss.Color(hex) }

// relLuminance is WCAG 2.x relative luminance. color.Color.RGBA returns 16-bit alpha-premultiplied
// channels; every palette entry is an opaque lipgloss.Color, so scaling by 65535 is exact here.
func relLuminance(c color.Color) float64 {
	r16, g16, b16, _ := c.RGBA()
	lin := func(v uint32) float64 {
		s := float64(v) / 65535.0
		if s <= 0.04045 {
			return s / 12.92
		}
		return math.Pow((s+0.055)/1.055, 2.4)
	}
	return 0.2126*lin(r16) + 0.7152*lin(g16) + 0.0722*lin(b16)
}

// contrast is the WCAG contrast ratio, 1.0 (identical) … 21.0 (black on white). It is symmetric,
// which is why one check covers both "X as text on Base" and "Base as text on a fill of X".
func contrast(a, b color.Color) float64 {
	la, lb := relLuminance(a), relLuminance(b)
	if la < lb {
		la, lb = lb, la
	}
	return (la + 0.05) / (lb + 0.05)
}

// TestEveryThemeIsLegibleOnItsBase is the gate a new scheme has to clear. The thresholds are not
// WCAG AA — they are the floor below which this Face's own frames stop being readable, chosen so
// that all four curated schemes clear them as published (Nord's red, at 3.05, is the tightest).
// It earns its keep on the fills: the active tab paints Base ON Accent and a search match paints
// Base ON Yellow, so a scheme whose Yellow sits near its Base renders invisible matches — which is
// exactly what stock Catppuccin Latte does (2.3:1), and why this file's latte darkens it.
func TestEveryThemeIsLegibleOnItsBase(t *testing.T) {
	const (
		minText     = 4.5 // primary text carries the frame
		minSemantic = 3.0 // status colours, and the Accent/Yellow fills
		minQuiet    = 1.5 // deliberately receding, but never invisible
	)
	t.Cleanup(resetTheme)

	for _, name := range ThemeNames() {
		th, ok := ThemeByName(name)
		if !ok {
			t.Fatalf("ThemeNames() offers %q but ThemeByName cannot resolve it", name)
		}
		check := func(role string, c color.Color, min float64) {
			t.Helper()
			if got := contrast(c, th.Base); got < min {
				t.Errorf("theme %s: %s is %.2f:1 against Base, want >= %.2f:1", name, role, got, min)
			}
		}
		check("Text", th.Text, minText)
		check("Accent", th.Accent, minSemantic)
		check("Blue", th.Blue, minSemantic)
		check("Green", th.Green, minSemantic)
		check("Red", th.Red, minSemantic)
		check("Yellow", th.Yellow, minSemantic)
		check("Peach", th.Peach, minSemantic)
		check("Teal", th.Teal, minSemantic)
		check("Sky", th.Sky, minSemantic)
		check("Overlay", th.Overlay, minSemantic)
		check("Pending", th.Pending, minQuiet)
		check("Skipped", th.Skipped, minQuiet)

		// The quiet ladder: Pending is the checkpoint nobody has reached and must recede furthest.
		// A scheme that made it louder than muted text or a skipped stage would inarguably read
		// wrong, and no absolute threshold catches that — only the ordering does.
		if contrast(th.Pending, th.Base) >= contrast(th.Skipped, th.Base) {
			t.Errorf("theme %s: Pending is not quieter than Skipped", name)
		}
		if contrast(th.Pending, th.Base) >= contrast(th.Overlay, th.Base) {
			t.Errorf("theme %s: Pending is not quieter than Overlay", name)
		}
	}
}

// TestContrastGateIsNotVacuous proves the floor above can actually fail, using the exact colour that
// motivated it: stock Catppuccin Latte's yellow (#DF8E1D) on Latte's base. A search match paints
// Base on a Yellow fill, so shipping that pair renders matches all but invisible — and a threshold
// test nothing can trip is just decoration, so pin both ends against a known reference (black on
// white is 21:1 by definition).
func TestContrastGateIsNotVacuous(t *testing.T) {
	latteBase := themes["latte"].Base
	stockLatteYellow := lipglossColor("#DF8E1D")

	if got := contrast(stockLatteYellow, latteBase); got >= 3.0 {
		t.Errorf("stock Latte yellow measures %.2f:1 on Latte base — the semantic floor would not "+
			"have caught it, so either the floor or this helper is wrong", got)
	}
	if got := contrast(themes["latte"].Yellow, latteBase); got < 3.0 {
		t.Errorf("the shipped Latte yellow measures %.2f:1 — the darkening did not take", got)
	}
	if got := contrast(lipglossColor("#000000"), lipglossColor("#FFFFFF")); math.Abs(got-21.0) > 0.01 {
		t.Errorf("contrast(black, white) = %.4f, want 21.0 — the luminance helper is miscalibrated", got)
	}
}

// TestEveryThemeFillsEveryRole catches the actual failure mode of a struct-of-colours registry: a
// role left off a new theme's literal is the zero Value, which lipgloss renders as transparent —
// silently, and only on that one theme's frames.
func TestEveryThemeFillsEveryRole(t *testing.T) {
	for _, name := range ThemeNames() {
		th, _ := ThemeByName(name)
		roles := map[string]color.Color{
			"Base": th.Base, "Mantle": th.Mantle, "Surface": th.Surface, "Selection": th.Selection,
			"Overlay": th.Overlay, "Text": th.Text, "Accent": th.Accent, "Blue": th.Blue,
			"Green": th.Green, "Red": th.Red, "Yellow": th.Yellow, "Peach": th.Peach,
			"Teal": th.Teal, "Sky": th.Sky, "Pending": th.Pending, "Skipped": th.Skipped,
		}
		for role, c := range roles {
			if c == nil {
				t.Errorf("theme %s: role %s is nil", name, role)
			}
		}
		if th.Name != name {
			t.Errorf("theme %s: registry key and Name disagree (Name=%q)", name, th.Name)
		}
		if th.Description == "" {
			t.Errorf("theme %s: no Description — the palette row would render blank", name)
		}
	}
}

// TestThemeOrderCoversEveryTheme pins themeOrder against the registry: a scheme added to `themes`
// but not to themeOrder would exist and be selectable while appearing in no list anywhere.
func TestThemeOrderCoversEveryTheme(t *testing.T) {
	if len(themeOrder) != len(themes) {
		t.Fatalf("themeOrder has %d entries, themes has %d", len(themeOrder), len(themes))
	}
	for _, name := range themeOrder {
		if _, ok := themes[name]; !ok {
			t.Errorf("themeOrder names %q, which is not in themes", name)
		}
	}
	if themeOrder[0] != DefaultThemeName {
		t.Errorf("themeOrder starts with %q, want the default %q", themeOrder[0], DefaultThemeName)
	}
}

// TestApplyThemeRebuildsDerivedStyles is the regression for the whole point of ApplyTheme. The
// derived styles capture their colour BY VALUE at construction, so swapping the palette without
// rebuilding them leaves them rendering the previous scheme — invisibly, since the accessors would
// meanwhile report the new one. Asserting on the accessor alone would pass against that bug.
func TestApplyThemeRebuildsDerivedStyles(t *testing.T) {
	t.Cleanup(resetTheme)

	if err := ApplyTheme("mocha"); err != nil {
		t.Fatal(err)
	}
	before := dimStyle.Render("x")

	if err := ApplyTheme("latte"); err != nil {
		t.Fatal(err)
	}
	if after := dimStyle.Render("x"); after == before {
		t.Errorf("dimStyle rendered identically across a mocha->latte switch (%q) — "+
			"ApplyTheme swapped the palette but did not rebuild the derived styles", after)
	}
	if got := CurrentTheme().Name; got != "latte" {
		t.Errorf("CurrentTheme() = %q after ApplyTheme(latte)", got)
	}
	// Accessors must move too, or the tui package rebuilds itself from a stale palette.
	if Base() != themes["latte"].Base {
		t.Error("Base() still reports the old scheme after a switch")
	}
}

func TestApplyThemeRejectsUnknownAndKeepsCurrent(t *testing.T) {
	t.Cleanup(resetTheme)

	if err := ApplyTheme("nord"); err != nil {
		t.Fatal(err)
	}
	err := ApplyTheme("solarized")
	if err == nil {
		t.Fatal("ApplyTheme(solarized) succeeded; want an error naming the curated set")
	}
	// A rejected switch must be a no-op, not a half-applied palette.
	if got := CurrentTheme().Name; got != "nord" {
		t.Errorf("CurrentTheme() = %q after a rejected switch, want nord", got)
	}
}

func TestNormalizeThemeName(t *testing.T) {
	t.Cleanup(resetTheme)
	for _, in := range []string{"MOCHA", " mocha ", "Mocha", "mocha"} {
		if err := ApplyTheme(in); err != nil {
			t.Errorf("ApplyTheme(%q): %v", in, err)
		}
		if got := CurrentTheme().Name; got != "mocha" {
			t.Errorf("ApplyTheme(%q) landed on %q", in, got)
		}
	}
}

// resetTheme restores the default so a theme test cannot leak into the goldens, which pin mocha.
func resetTheme() {
	if err := ApplyTheme(DefaultThemeName); err != nil {
		panic(err)
	}
}
