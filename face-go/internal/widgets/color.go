package widgets

import (
	"fmt"
	"image/color"
	"math"
)

// Colour arithmetic on the theme roles (K6.4).
//
// These exist for the one consumer that cannot take a `color.Color`: glamour styles markdown from a
// JSON-shaped config whose colours are hex STRINGS, so a themed markdown renderer has to project the
// palette back down to "#RRGGBB". Everything else in this Face passes `color.Color` around and must
// keep doing so — a hex string in a pane is the thing the theme system exists to stop.

// Hex renders a theme colour as "#RRGGBB". `color.Color.RGBA` returns 16-bit alpha-PREMULTIPLIED
// components, so the high byte of each is the 8-bit channel for any opaque colour, which every theme
// role is. A nil colour is the empty string rather than "#000000": glamour treats an unset colour as
// "inherit", and inheriting is the honest answer to "this role has no colour".
func Hex(c color.Color) string {
	if c == nil {
		return ""
	}
	r, g, b, _ := c.RGBA()
	return fmt.Sprintf("#%02X%02X%02X", uint8(r>>8), uint8(g>>8), uint8(b>>8))
}

// Luminance is the WCAG 2.x relative luminance of a colour, 0 (black) to 1 (white). It is the same
// formula the contrast gate in theme_test.go uses; both must agree, or a theme could pass the legibility
// test while being classified as the wrong polarity here.
func Luminance(c color.Color) float64 {
	r, g, b, _ := c.RGBA()
	f := func(v uint32) float64 {
		s := float64(v>>8) / 255.0
		if s <= 0.03928 {
			return s / 12.92
		}
		return math.Pow((s+0.055)/1.055, 2.4)
	}
	return 0.2126*f(r) + 0.7152*f(g) + 0.0722*f(b)
}

// IsLight reports whether a scheme paints dark text on a light ground. It is asked of a theme's Base
// and it decides which of glamour's two built-in style configs a themed markdown renderer starts
// from — the parts we do NOT override (chroma's syntax theme, the checkbox glyphs) have to match the
// polarity of the parts we do.
func (t Theme) IsLight() bool { return Luminance(t.Base) > 0.5 }
