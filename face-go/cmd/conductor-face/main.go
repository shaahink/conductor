package main

import (
	"flag"
	"fmt"
	"os"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/tui"
)

func main() {
	demo := flag.Bool("demo", false, "Run with synthetic demo data (no engine required)")
	url := flag.String("url", "", "Base URL of the conductor control plane (default: http://127.0.0.1:4317)")
	host := flag.String("host", "127.0.0.1", "Control plane host")
	port := flag.Int("port", 4317, "Control plane port")
	flag.Parse()

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

	model := tui.New(source, *demo, baseURL)

	p := tea.NewProgram(model)

	if _, err := p.Run(); err != nil {
		fmt.Fprintf(os.Stderr, "conductor-face: %v\n", err)
		os.Exit(1)
	}
}
