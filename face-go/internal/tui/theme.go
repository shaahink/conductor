package tui

// Theme switching (U3.1) and the Face's own persisted preferences.
//
// A scheme is a `widgets.Theme` — a flat set of ROLES (base/mantle/surface/…/accent/semantic), never
// a hex in a pane. Switching one is two rebuilds, not one: each package's shared style vars capture
// their colour by value at construction, and this package cannot reach into `widgets`' vars (nor it
// into ours). `ApplyTheme` below is the only thing that should run both — call it at startup and on
// every switch, or a live switch repaints half the frame and leaves the other half in the old
// scheme.

import (
	"encoding/json"
	"os"
	"path/filepath"

	"conductor-face-go/internal/widgets"
)

// ApplyTheme switches the whole Face to a curated scheme: `widgets` repaints its own styles, then
// this package repaints its own. It touches package state, not the Model, because that is where the
// styles actually live — every pane renders through these vars.
func ApplyTheme(name string) error {
	if err := widgets.ApplyTheme(name); err != nil {
		return err
	}
	rebuildStyles()
	return nil
}

// ResolveStartupTheme picks the launch scheme: an explicit --theme wins, else the persisted choice,
// else the default.
//
// The two failure modes are deliberately asymmetric. A bad --theme is a hard error — the user named
// something specific and silently starting in a different scheme would hide the typo. A persisted
// name that no longer resolves (a renamed scheme, a hand-edited config) falls back to the default:
// a stale preference file must never be able to stop the Face from starting.
func ResolveStartupTheme(flagValue string) error {
	if name := widgets.NormalizeThemeName(flagValue); name != "" {
		return ApplyTheme(name)
	}
	if name := widgets.NormalizeThemeName(LoadConfig().Theme); name != "" {
		if err := ApplyTheme(name); err == nil {
			return nil
		}
	}
	return ApplyTheme(widgets.DefaultThemeName)
}

// Config is the Face's own preference file. It lives under os.UserConfigDir() rather than in the
// repo because the theme is the USER's choice, not the project's: a checkout must not carry it, and
// every repo the user attaches to should look the same.
type Config struct {
	Theme string `json:"theme"`
}

// ConfigPath is os.UserConfigDir()/conductor-face/config.json.
func ConfigPath() (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "conductor-face", "config.json"), nil
}

// LoadConfig reads the persisted preferences. Every failure returns the zero Config on purpose —
// missing, unreadable, or corrupt all mean "no preference", and none of them is worth refusing to
// start over.
func LoadConfig() Config {
	path, err := ConfigPath()
	if err != nil {
		return Config{}
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return Config{}
	}
	var c Config
	if err := json.Unmarshal(data, &c); err != nil {
		return Config{}
	}
	return c
}

// SaveConfig persists preferences, creating the directory on first write. Unlike LoadConfig it
// reports its error: the palette turns that into a toast, so a switch that will not survive a
// restart says so rather than quietly lying about having stuck.
func SaveConfig(c Config) error {
	path, err := ConfigPath()
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, append(data, '\n'), 0o644)
}
