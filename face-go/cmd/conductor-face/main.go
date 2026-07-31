package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"

	tea "charm.land/bubbletea/v2"
	"github.com/charmbracelet/x/term"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/tui"
)

const help = `conductor-face — dashboard TUI for the Conductor control plane

Usage:
  conductor-face                 Attach to a running conductor (auto-discovered), or --demo
  conductor-face --demo          Fully offline against synthetic data — no engine needed
  conductor-face --url <base>    Attach to a specific control-plane URL

Options:
  --demo         Explore the whole dashboard offline (plan editor, history, raw stream, palette …).
  --url <base>   Control-plane base URL (overrides auto-discovery).
  --host <ip>    Host, combined with --port (default 127.0.0.1).
  --port <n>     Port, combined with --host (default 4317).
  --token <t>    Per-run write token (else read from control-plane.json or CONDUCTOR_TOKEN).
  --theme <name> Colour scheme for this launch: mocha (default) | latte | nord | gruvbox.
  -h, --help     Show this help and exit.

Live mode auto-discovers a run: it walks up from the current directory for
.conductor/control-plane.json (written by 'conductor run --control-plane') and
attaches to it — so inside a repo with a live run, just type 'conductor-face'.
The discovery file also carries the write token every POST needs; reads work
without it. With --url, pass --token or set CONDUCTOR_TOKEN so writes are accepted.

--theme overrides the scheme for this launch only. To CHANGE the saved choice,
switch live from the palette (':' then 'theme') — that writes it to
<user config dir>/conductor-face/config.json, which every later launch reads.

Set FACE_FORCE_TTY=1 to bypass the interactive-terminal check under a PTY wrapper.
`

func main() {
	flag.Usage = func() { fmt.Fprint(os.Stderr, help) }

	demo := flag.Bool("demo", false, "Run with synthetic demo data (no engine required)")
	url := flag.String("url", "", "Base URL of the conductor control plane")
	host := flag.String("host", "127.0.0.1", "Control plane host")
	port := flag.Int("port", 4317, "Control plane port")
	token := flag.String("token", "", "Per-run write token (else CONDUCTOR_TOKEN or control-plane.json)")
	theme := flag.String("theme", "", "Colour scheme for this launch: mocha | latte | nord | gruvbox")
	flag.Parse()

	// Before anything renders: --theme wins for this launch, else the persisted choice, else mocha.
	// A bad --theme stops us here rather than starting in a scheme the user did not ask for.
	if err := tui.ResolveStartupTheme(*theme); err != nil {
		fmt.Fprintf(os.Stderr, "conductor-face: %v\n", err)
		os.Exit(2)
	}

	if !term.IsTerminal(os.Stdout.Fd()) && os.Getenv("FACE_FORCE_TTY") == "" {
		fmt.Fprintln(os.Stderr, "conductor-face needs an interactive terminal (stdout is not a TTY).")
		fmt.Fprintln(os.Stderr, "Try:  conductor-face --demo   (or run inside a real terminal)")
		os.Exit(1)
	}

	var source api.DataSource
	var baseURL string

	if *demo {
		source, baseURL = api.NewDemoSource(), "(demo)"
	} else {
		var discoveredToken string
		baseURL, discoveredToken = resolveBaseURL(*url, *host, *port)
		tok := firstNonEmpty(*token, os.Getenv("CONDUCTOR_TOKEN"), discoveredToken)
		source = api.NewLiveSourceWithToken(baseURL, tok)
	}
	defer source.Close()

	if _, err := tea.NewProgram(tui.New(source, *demo, baseURL)).Run(); err != nil {
		fmt.Fprintf(os.Stderr, "conductor-face: %v\n", err)
		os.Exit(1)
	}
}

// resolveBaseURL prefers an explicit --url, then an auto-discovered running control plane, then the
// host/port default (which the splash screen will explain if nothing is listening there). It returns
// the discovery file's write token alongside the URL when it found one that way.
func resolveBaseURL(url, host string, port int) (baseURL, token string) {
	if url != "" {
		// Even with an explicit --url, a matching local discovery file supplies the token so
		// `conductor face --url …` from inside the repo still writes without a manual --token.
		if u, t := discoverControlPlane(); u == url {
			return url, t
		}
		return url, ""
	}
	if u, t := discoverControlPlane(); u != "" {
		return u, t
	}
	return fmt.Sprintf("http://%s:%d", host, port), ""
}

// discoverControlPlane walks up from the working directory looking for
// .conductor/control-plane.json and returns its baseUrl + write token, or "" if none is found.
func discoverControlPlane() (baseURL, token string) {
	dir, err := os.Getwd()
	if err != nil {
		return "", ""
	}
	for {
		path := filepath.Join(dir, ".conductor", "control-plane.json")
		if data, err := os.ReadFile(path); err == nil {
			var info struct {
				BaseURL string `json:"baseUrl"`
				Token   string `json:"token"`
			}
			if json.Unmarshal(data, &info) == nil && info.BaseURL != "" {
				return info.BaseURL, info.Token
			}
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return "", ""
		}
		dir = parent
	}
}

func firstNonEmpty(vals ...string) string {
	for _, v := range vals {
		if v != "" {
			return v
		}
	}
	return ""
}
