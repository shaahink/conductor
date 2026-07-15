package main

import (
	"flag"
	"fmt"
	"os"

	tea "charm.land/bubbletea/v2"
	"github.com/charmbracelet/x/term"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/tui"
)

const help = `conductor-face — Go + Bubble Tea TUI for the Conductor control plane

Usage:
  conductor-face [--url <base>] [--demo] [--host <ip>] [--port <n>]

Options:
  --url <base>   Full control-plane base URL (default http://127.0.0.1:4317)
  --host <ip>    Host only, combined with --port (default 127.0.0.1)
  --port <n>     Port only, combined with --host (default 4317)
  --demo         Run fully offline against synthetic data — no conductor process needed.
                 Everything (plan tree, transcript, processes, sessions, palette, inject,
                 report) is interactive so you can review the whole UI cold.
  -h, --help     Show this help and exit

Requires --control-plane on the conductor side for live mode:
  conductor run -p <plan> --control-plane [--control-plane-port <n>]

Set FACE_FORCE_TTY=1 to bypass the interactive-terminal check (e.g. under a PTY wrapper
that doesn't report itself as one).
`

func main() {
	flag.Usage = func() { fmt.Fprint(os.Stderr, help) }

	demo := flag.Bool("demo", false, "Run with synthetic demo data (no engine required)")
	url := flag.String("url", "", "Base URL of the conductor control plane (default: http://127.0.0.1:4317)")
	host := flag.String("host", "127.0.0.1", "Control plane host")
	port := flag.Int("port", 4317, "Control plane port")
	flag.Parse()

	if !term.IsTerminal(os.Stdout.Fd()) && os.Getenv("FACE_FORCE_TTY") == "" {
		fmt.Fprintln(os.Stderr, "conductor-face needs an interactive terminal (stdout is not a TTY).")
		os.Exit(1)
	}

	var source api.DataSource
	var baseURL string

	if *demo {
		source = api.NewDemoSource()
		baseURL = "(demo)"
	} else {
		if *url != "" {
			baseURL = *url
		} else {
			baseURL = fmt.Sprintf("http://%s:%d", *host, *port)
		}
		source = api.NewLiveSource(baseURL)
	}
	defer source.Close()

	model := tui.New(source, *demo, baseURL)

	p := tea.NewProgram(model)

	if _, err := p.Run(); err != nil {
		fmt.Fprintf(os.Stderr, "conductor-face: %v\n", err)
		os.Exit(1)
	}
}
