package tui

import (
	"encoding/json"
	"fmt"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/widgets"
)

// SF5.4 — the run picker.
//
// The Face used to find its run by walking up from the working directory for
// .conductor/control-plane.json. That answered one question ("the run in this repo") and answered it
// wrongly whenever the file was absent — which is not hypothetical: the engine deletes it on control
// plane dispose, so a live run can be serving 4317 with no discovery file at all. It also had no
// answer for the machine the owner actually runs: several websites, several engines, several ports.
//
// So the engine now resolves the target by PROBING the ports (see FleetScan on the C# side) and hands
// the Face what it found. When exactly one run is the obvious target the engine attaches straight to
// it and this picker never appears. When the target is ambiguous — more than one plane answering and
// none of them the run in this directory — or the user asked for it with `--pick`, the fleet arrives
// in the CONDUCTOR_FLEET environment variable and this screen runs FIRST, before the dashboard.
//
// It is a pre-flight screen, not an overlay: nothing is connected yet, so there is no dashboard to
// composite onto (STYLE.md's "never add a full-screen modal" is about views of a run that is already
// attached). The chosen run's baseUrl + write token become the dashboard's connection.
//
// The token travels in the environment and never in argv: a process listing is world-readable on a
// shared machine, and `conductor ps --json` — which anyone may run — carries no token at all.

// FleetRun is one attachable run, as the engine describes it in CONDUCTOR_FLEET. It mirrors
// Conductor.Core.Fleet.FaceFleetRun field for field; Token is the one field `ps --json` does not have.
type FleetRun struct {
	Repo       string  `json:"repo"`
	PlanName   string  `json:"planName"`
	RunID      string  `json:"runId"`
	Status     string  `json:"status"`
	Port       int     `json:"port"`
	Pid        int     `json:"pid"`
	StageID    string  `json:"stageId"`
	StageTitle string  `json:"stageTitle"`
	Attention  string  `json:"attentionReason"`
	Done       int     `json:"done"`
	Total      int     `json:"total"`
	CostUsd    float64 `json:"costUsd"`
	BaseURL    string  `json:"baseUrl"`
	StateDir   string  `json:"stateDir"`
	Token      string  `json:"token"`
	Self       bool    `json:"self"`
}

// Fleet is the CONDUCTOR_FLEET envelope.
type Fleet struct {
	Runs []FleetRun `json:"runs"`
}

// RepoLabel is the trailing directory name of the repo — the way a human names which run they mean.
// Falls back to the plan name, then to the port, so a row is never blank.
func (r FleetRun) RepoLabel() string {
	trimmed := strings.TrimRight(strings.ReplaceAll(r.Repo, "\\", "/"), "/")
	if i := strings.LastIndex(trimmed, "/"); i >= 0 && i < len(trimmed)-1 {
		trimmed = trimmed[i+1:]
	}
	if trimmed != "" {
		return trimmed
	}
	if r.PlanName != "" {
		return r.PlanName
	}
	return fmt.Sprintf("port %d", r.Port)
}

// ShortRunID is the first eight characters, the form every other surface prints.
func (r FleetRun) ShortRunID() string {
	if len(r.RunID) >= 8 {
		return r.RunID[:8]
	}
	return r.RunID
}

// StatusText folds the attention reason into the status, because "Running" and "Running (owner input
// wanted)" are the whole reason someone opens this screen.
func (r FleetRun) StatusText() string {
	if r.Attention != "" {
		return r.Status + ": " + r.Attention
	}
	return r.Status
}

// ParseFleet reads the CONDUCTOR_FLEET envelope. An empty or malformed value is not an error the
// caller should hide — it means the engine meant to offer a choice and the Face cannot show one.
func ParseFleet(raw string) (Fleet, error) {
	var f Fleet
	if strings.TrimSpace(raw) == "" {
		return f, fmt.Errorf("empty fleet")
	}
	if err := json.Unmarshal([]byte(raw), &f); err != nil {
		return Fleet{}, fmt.Errorf("unreadable fleet: %w", err)
	}
	if len(f.Runs) == 0 {
		return f, fmt.Errorf("fleet names no runs")
	}
	return f, nil
}

// PickerModel is the pre-flight screen. It owns no connection and issues no requests: everything it
// shows was measured by the engine's scan before the Face started.
type PickerModel struct {
	runs   []FleetRun
	cursor int
	width  int
	height int
	chosen int // -1 until enter; index into runs
}

// NewPicker builds the picker over a fleet. The cursor starts on the run in this directory when there
// is one — the row the person who typed `conductor face` here most likely means.
func NewPicker(runs []FleetRun) PickerModel {
	p := PickerModel{runs: runs, chosen: -1, width: 80, height: 24}
	for i, r := range runs {
		if r.Self {
			p.cursor = i
			break
		}
	}
	return p
}

func (p PickerModel) Init() tea.Cmd { return nil }

// Chosen reports the selected run. ok is false when the user quit without choosing — that is a normal
// exit, not a failure: they looked at the fleet and decided not to attach.
func (p PickerModel) Chosen() (FleetRun, bool) {
	if p.chosen < 0 || p.chosen >= len(p.runs) {
		return FleetRun{}, false
	}
	return p.runs[p.chosen], true
}

func (p PickerModel) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	switch msg := msg.(type) {
	case tea.WindowSizeMsg:
		p.width, p.height = msg.Width, msg.Height
		return p, nil
	case tea.KeyPressMsg:
		return p.handleKey(msg.String())
	}
	return p, nil
}

// handleKey is exported through Update but kept separate so tests drive real key strings rather than
// poking fields — the same discipline STYLE.md asks of the dashboard's handleKey.
func (p PickerModel) handleKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "up", "k", "shift+tab":
		if p.cursor > 0 {
			p.cursor--
		}
	case "down", "j", "tab":
		if p.cursor < len(p.runs)-1 {
			p.cursor++
		}
	case "home", "g":
		p.cursor = 0
	case "end", "G":
		p.cursor = len(p.runs) - 1
	case "enter", " ":
		p.chosen = p.cursor
		return p, tea.Quit
	case "q", "esc", "ctrl+c":
		p.chosen = -1
		return p, tea.Quit
	default:
		// 1-9 jump straight to a row and attach: on a two-run machine the whole interaction is one key.
		if len(key) == 1 && key[0] >= '1' && key[0] <= '9' {
			if i := int(key[0] - '1'); i < len(p.runs) {
				p.cursor = i
				p.chosen = i
				return p, tea.Quit
			}
		}
	}
	return p, nil
}

func (p PickerModel) View() tea.View {
	return tea.NewView(p.Render())
}

// Render paints the picker. Split from View so tests (and the evidence capture) can read a frame
// without a terminal.
func (p PickerModel) Render() string {
	inner := p.width - 8
	if inner < 30 {
		inner = 30
	}

	title := accentStyle.Render("conductor") + subtleStyle.Render(" · attach the face to which run?")
	count := fmt.Sprintf("%d runs answering on this machine", len(p.runs))
	if len(p.runs) == 1 {
		count = "1 run answering on this machine"
	}

	lines := []string{title, subtleStyle.Render(count), ""}
	for i, r := range p.runs {
		lines = append(lines, p.renderRow(i, r, inner))
	}
	lines = append(lines, "", p.renderDetail(inner))

	box := lipgloss.NewStyle().
		Border(lipgloss.RoundedBorder()).
		BorderForeground(widgets.Surface()).
		Padding(1, 2).
		Render(strings.Join(lines, "\n"))

	hint := strings.Join([]string{
		key("↑↓") + subtleStyle.Render(" move"),
		key("1-9") + subtleStyle.Render(" attach"),
		key("enter") + subtleStyle.Render(" attach"),
		key("esc") + subtleStyle.Render(" quit"),
	}, subtleStyle.Render(" · "))

	return lipgloss.NewStyle().MaxWidth(p.width).MaxHeight(p.height).
		Render(box + "\n " + hint)
}

// renderRow builds the whole row as PLAIN text first and styles it once. Padding a string that
// already carries escape bytes pads the escapes (STYLE.md), and this row is nothing but columns.
func (p PickerModel) renderRow(i int, r FleetRun, width int) string {
	repoW, statusW := 18, 26
	if width < 76 { // narrow terminal: the repo name is what identifies a run, the status can give
		repoW, statusW = 14, 16
	}

	marker := "  "
	if r.Self {
		marker = "* "
	}
	port := fmt.Sprintf("%d", r.Port)
	if r.Port <= 0 {
		port = "-"
	}
	plain := fmt.Sprintf("%s%d  %-*s  %-*s  %-*s  %5s  %8s",
		marker, i+1,
		repoW, truncate(r.RepoLabel(), repoW),
		10, truncate(stageLabel(r), 10),
		statusW, truncate(r.StatusText(), statusW),
		port,
		fmt.Sprintf("$%.2f", r.CostUsd))
	plain = truncate(plain, width-1)

	// The cursor gets its own column so the `*` self-marker survives being highlighted.
	if i == p.cursor {
		return highlightBg.Render("▸" + plain)
	}
	return textStyle.Render(" " + plain)
}

// renderDetail is the row under the list: everything about the highlighted run that does not fit in a
// column — where it lives on disk, which process it is, and whether the Face will be able to write.
func (p PickerModel) renderDetail(width int) string {
	if p.cursor < 0 || p.cursor >= len(p.runs) {
		return ""
	}
	r := p.runs[p.cursor]
	write := "read-only (no token)"
	if r.Token != "" {
		write = "read/write"
	}
	progress := ""
	if r.Total > 0 {
		progress = fmt.Sprintf("  ·  %d/%d checkpoints", r.Done, r.Total)
	}
	// Identity and reachability lead; the plan name and the path are the long, clippable half. Put
	// them first and an 80-column terminal loses "read-only" — which is the fact that decides whether
	// this Face can do anything once it attaches.
	head := fmt.Sprintf("run %s  ·  pid %d  ·  %s%s", r.ShortRunID(), r.Pid, write, progress)
	tail := r.PlanName
	if r.Repo != "" {
		tail += "  ·  " + r.Repo
	}
	return subtleStyle.Render(truncate(head, width)) + "\n" +
		subtleStyle.Render(truncate(tail, width))
}

// stageLabel is the stage id, or its title when the run reports no id, or a dash.
func stageLabel(r FleetRun) string {
	if r.StageID != "" {
		return r.StageID
	}
	if r.StageTitle != "" {
		return r.StageTitle
	}
	return "-"
}
