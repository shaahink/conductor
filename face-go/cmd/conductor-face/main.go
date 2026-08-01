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

When several runs are live on one machine, 'conductor face' probes the control
plane ports itself and — when it cannot tell which run you mean, or you passed
--pick — hands them over in CONDUCTOR_FLEET. A run picker then runs before the
dashboard: ↑↓ or 1-9 to choose, enter to attach, esc to quit. That envelope
carries each run's write token, which is why it is an env var and not a flag.

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

	// The fleet, if the engine handed one over (SF5.4). Read before the TTY check so a Face that
	// cannot paint still says WHICH runs it was about to offer — that message is the only thing a
	// non-interactive caller (a log capture, a wrapper script) would otherwise get.
	fleet, fleetErr := tui.ParseFleet(os.Getenv(fleetEnv))

	if !term.IsTerminal(os.Stdout.Fd()) && os.Getenv("FACE_FORCE_TTY") == "" {
		fmt.Fprintln(os.Stderr, "conductor-face needs an interactive terminal (stdout is not a TTY).")
		if fleetErr == nil {
			fmt.Fprintf(os.Stderr, "The run picker had %d runs to offer:\n", len(fleet.Runs))
			for i, r := range fleet.Runs {
				fmt.Fprintf(os.Stderr, "  %d) %-16s %-10s %-24s %s  pid %d  %s\n",
					i+1, r.RepoLabel(), stageOrDash(r), r.StatusText(), r.BaseURL, r.Pid, writeMode(r))
			}
		}
		fmt.Fprintln(os.Stderr, "Try:  conductor-face --demo   (or run inside a real terminal)")
		os.Exit(1)
	}

	var source api.DataSource
	var baseURL string
	stateDir := ""

	switch {
	case *demo:
		source, baseURL = api.NewDemoSource(), "(demo)"
	case fleetErr == nil && *url == "":
		// The engine could not pick for us: run the picker FIRST, then attach to what came back.
		// An explicit --url outranks it — the caller already named the run they mean.
		chosen, ok := runPicker(fleet.Runs)
		if !ok {
			return // looked at the fleet, attached to nothing — a normal exit, not a failure
		}
		baseURL, stateDir = chosen.BaseURL, chosen.StateDir
		source = api.NewLiveSourceWithToken(baseURL,
			firstNonEmpty(*token, chosen.Token, os.Getenv("CONDUCTOR_TOKEN")))
	default:
		var discoveredToken string
		baseURL, discoveredToken = resolveBaseURL(*url, *host, *port)
		tok := firstNonEmpty(*token, os.Getenv("CONDUCTOR_TOKEN"), discoveredToken)
		source = api.NewLiveSourceWithToken(baseURL, tok)
	}
	defer source.Close()

	model := tui.New(source, *demo, baseURL)
	if !*demo {
		// SF2.1: find this run's state dir on disk BEFORE anything is polled, so a Face opened after
		// the engine exited can still say what the run did. Demo mode is excluded because it has no
		// disk state and must never read a real run's summary into a synthetic tour.
		//
		// A run chosen from the picker names its own state dir, which is the one that matters: walking
		// up from the working directory would find THIS repo's .conductor while showing another repo's
		// run, and the last-run card would describe a run nobody is looking at.
		model = model.WithStateDir(firstNonEmpty(stateDir, discoverStateDir()))
	}

	if _, err := tea.NewProgram(model).Run(); err != nil {
		fmt.Fprintf(os.Stderr, "conductor-face: %v\n", err)
		os.Exit(1)
	}
}

// fleetEnv carries the runs `conductor face` found by probing the control-plane ports. It is an
// environment variable and not a flag because it contains each run's write token, and argv is visible
// to every process on the machine.
const fleetEnv = "CONDUCTOR_FLEET"

// runPicker shows the pre-flight run picker and returns the chosen run. A fleet of one still gets the
// screen: the engine only hands one over when it could NOT decide (or when the user asked to choose),
// so showing a list of one is the honest answer to "which run?" rather than a silent attach.
func runPicker(runs []tui.FleetRun) (tui.FleetRun, bool) {
	final, err := tea.NewProgram(tui.NewPicker(runs)).Run()
	if err != nil {
		fmt.Fprintf(os.Stderr, "conductor-face: %v\n", err)
		os.Exit(1)
	}
	picker, ok := final.(tui.PickerModel)
	if !ok {
		return tui.FleetRun{}, false
	}
	return picker.Chosen()
}

// stageOrDash and writeMode format the no-TTY fleet listing above; the picker itself renders them.
func stageOrDash(r tui.FleetRun) string {
	if r.StageID != "" {
		return r.StageID
	}
	return "-"
}

func writeMode(r tui.FleetRun) string {
	if r.Token != "" {
		return "read/write"
	}
	return "read-only"
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

// discoverStateDir walks up from the working directory for a `.conductor` directory and returns it,
// or "" when there is none above us.
//
// It deliberately does NOT look for control-plane.json, the way discoverControlPlane above does. The
// engine DELETES that file as it shuts down (ControlPlaneServer.Dispose: "a client that reads it must
// never be pointed at a dead port"), so keying on it would leave the Face blind in exactly the case
// SF2.1's last-run card exists to serve — a run that has finished. The directory outlives the port;
// the file does not.
//
// This is the cold-start answer, not the authoritative one: the moment /state answers, the engine's
// own PlanConfig.StateDir replaces it (update.go), which also covers a plan whose state dir is not
// named `.conductor` at all.
func discoverStateDir() string {
	dir, err := os.Getwd()
	if err != nil {
		return ""
	}
	for {
		candidate := filepath.Join(dir, ".conductor")
		if fi, err := os.Stat(candidate); err == nil && fi.IsDir() {
			return candidate
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return ""
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
