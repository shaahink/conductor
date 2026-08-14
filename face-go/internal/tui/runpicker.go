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

// PastRun is one run this machine REMEMBERS but is not serving, read by the engine from the state
// catalogue K3.1 built. It mirrors Conductor.Core.Fleet.FacePastRun. There is no base url and no
// token because no engine is behind a finished run — but since KS2.2 there is still something to
// open: choosing one hands RunID back to the engine, which serves that run's run.db through a
// read-only archive plane and points a Face at it. Read-only is then structural, not a convention:
// the archive carries no write token, so every write affordance in the Face hides itself.
//
// A row can also be one this machine remembers and can no longer READ: a catalogue entry whose run.db
// was deleted or replaced by something that is not a run database. Those are listed too — "that run is
// gone" and "that run was never here" are different answers, and hiding the first leaves only the
// second. Such a row has no RunID (nothing could be read to get one), is named by its Slug, and
// carries the one sentence saying what is wrong in Problem; choosing it hands the slug back and the
// engine answers with that same sentence instead of opening anything.
type PastRun struct {
	Repo            string  `json:"repo"`
	PlanName        string  `json:"planName"`
	RunID           string  `json:"runId"`
	Status          string  `json:"status"`
	Done            int     `json:"done"`
	Total           int     `json:"total"`
	CostUsd         float64 `json:"costUsd"`
	LastActivityUtc string  `json:"lastActivityUtc"`
	RunDb           string  `json:"runDb"`
	Selector        string  `json:"selector"`
	Problem         string  `json:"problem"`
}

// Readable reports whether the engine could open this run's database. A false row still lists.
func (r PastRun) Readable() bool { return r.Problem == "" }

// OpenWith is what the engine is handed to open this row: the run id normally, the catalogue slug for
// a row whose database could not be read and which therefore has no id. Falls back to the run id so an
// envelope written by an older engine (no selector field) still opens.
func (r PastRun) OpenWith() string {
	if r.Selector != "" {
		return r.Selector
	}
	return r.RunID
}

// Fleet is the CONDUCTOR_FLEET envelope.
//
// PastTotal is how many remembered runs the engine's catalogue holds, which is not always how many
// it sent: the list is capped at a screenful (FacePastRuns.DefaultMax). The picker renders the pair
// as "showing N of M", because a screen claiming to list this machine's runs while quietly showing
// its first page is the one way this list can lie — someone looks for a run, does not see it, and
// concludes it is not here.
type Fleet struct {
	Runs      []FleetRun `json:"runs"`
	Past      []PastRun  `json:"past"`
	PastTotal int        `json:"pastTotal"`
}

// RepoLabel and ShortRunID mirror FleetRun's, so a past row is built exactly like a live one.
func (r PastRun) RepoLabel() string {
	return repoLeaf(r.Repo, r.PlanName, "-")
}

// ShortRunID is the first eight characters, the form every other surface prints.
func (r PastRun) ShortRunID() string {
	if len(r.RunID) >= 8 {
		return r.RunID[:8]
	}
	return r.RunID
}

// RepoLabel is the trailing directory name of the repo — the way a human names which run they mean.
// Falls back to the plan name, then to the port, so a row is never blank.
func (r FleetRun) RepoLabel() string {
	return repoLeaf(r.Repo, r.PlanName, fmt.Sprintf("port %d", r.Port))
}

// repoLeaf is the trailing directory name, with two fallbacks so a row is never blank.
func repoLeaf(repo, plan, last string) string {
	trimmed := strings.TrimRight(strings.ReplaceAll(repo, "\\", "/"), "/")
	if i := strings.LastIndex(trimmed, "/"); i >= 0 && i < len(trimmed)-1 {
		trimmed = trimmed[i+1:]
	}
	if trimmed != "" {
		return trimmed
	}
	if plan != "" {
		return plan
	}
	return last
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
	past   []PastRun
	cursor int // spans live rows then past rows: index >= len(runs) is a past row
	width  int
	height int
	chosen int // -1 until enter; index into runs
	// KS2.2: -1 until enter lands on a past row. A past run has no control plane of its own, so the
	// choice is handed BACK to the engine, which opens a read-only archive plane over that run's
	// run.db and points a fresh Face at it. Two fields rather than one signed index because the two
	// choices are answered by different code on the other side and must not be told apart by sign.
	chosenPast int
	// KS2.4: how many remembered runs the catalogue holds, against however many arrived. The heading
	// discloses the difference; see Fleet.PastTotal.
	pastTotal int
	// KS2.4: the base url this Face is ALREADY attached to, when the picker is being shown as the
	// run switcher rather than as the pre-flight screen. Empty at startup — nothing is attached yet —
	// and the two states differ in exactly the things that would otherwise mislead: the title asks to
	// switch rather than to attach, the current run is marked, and `esc` cancels instead of quitting.
	attached string
}

// NewPicker builds the picker over a fleet. The cursor starts on the run in this directory when there
// is one — the row the person who typed `conductor face` here most likely means.
func NewPicker(runs []FleetRun) PickerModel {
	p := PickerModel{runs: runs, chosen: -1, chosenPast: -1, width: 80, height: 24}
	for i, r := range runs {
		if r.Self {
			p.cursor = i
			break
		}
	}
	return p
}

// WithPast adds the runs this machine remembers but is not serving (K3.2). Chained rather than a
// second NewPicker parameter so a caller with no history is unchanged.
//
// KS2.4 made `total` a required argument rather than a second chained setter: it is the number the
// heading has to disclose, and a caller that could forget it would silently present a page as the
// whole machine — which is exactly the failure the disclosure exists to prevent. A total smaller
// than what arrived is nonsense (an older engine sends none at all), so it floors at len(past).
func (p PickerModel) WithPast(past []PastRun, total int) PickerModel {
	p.past = past
	p.pastTotal = max(total, len(past))
	return p
}

// WithAttached turns the pre-flight screen into the run SWITCHER (KS2.4): same list, same keys, but
// shown by a Face that is already attached to `baseURL`. Empty means the startup screen, which is
// what every existing caller passes by not calling this.
func (p PickerModel) WithAttached(baseURL string) PickerModel {
	p.attached = baseURL
	for i, r := range p.runs {
		if r.BaseURL == baseURL {
			p.cursor = i // start on the run you are on, not on the one this directory happens to hold
			break
		}
	}
	return p
}

// rowCount is every navigable row: the live runs, then the remembered ones.
func (p PickerModel) rowCount() int { return len(p.runs) + len(p.past) }

// isPast reports whether a cursor index lands in the history section.
func (p PickerModel) isPast(i int) bool { return i >= len(p.runs) && i < p.rowCount() }

func (p PickerModel) Init() tea.Cmd { return nil }

// Chosen reports the selected run. ok is false when the user quit without choosing — that is a normal
// exit, not a failure: they looked at the fleet and decided not to attach.
func (p PickerModel) Chosen() (FleetRun, bool) {
	if p.chosen < 0 || p.chosen >= len(p.runs) {
		return FleetRun{}, false
	}
	return p.runs[p.chosen], true
}

// ChosenPast reports a selected FINISHED run (KS2.2). It is never a live one: the caller acts on this
// by asking the engine for a read-only archive plane over that run's database, which is a different
// route from Chosen()'s "attach to this URL" — so the two answers stay two methods.
func (p PickerModel) ChosenPast() (PastRun, bool) {
	if p.chosenPast < 0 || p.chosenPast >= len(p.past) {
		return PastRun{}, false
	}
	return p.past[p.chosenPast], true
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
		if p.cursor < p.rowCount()-1 {
			p.cursor++
		}
	case "home", "g":
		p.cursor = 0
	case "end", "G":
		p.cursor = p.rowCount() - 1
	case "enter", " ":
		// KS2.2: a finished run opens too. It has no engine and no control plane of its own, so the
		// choice leaves the Face and the engine serves that run's run.db read-only; before this, enter
		// on a past row printed a note naming another command and did nothing.
		if p.isPast(p.cursor) {
			p.chosenPast = p.cursor - len(p.runs)
			return p, tea.Quit
		}
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

	question := " · attach the face to which run?"
	if p.attached != "" {
		question = " · switch this face to which run?"
	}
	title := accentStyle.Render("conductor") + subtleStyle.Render(question)
	count := fmt.Sprintf("%d runs answering on this machine", len(p.runs))
	if len(p.runs) == 1 {
		count = "1 run answering on this machine"
	}

	lines := []string{title, subtleStyle.Render(count), ""}
	for i, r := range p.runs {
		lines = append(lines, p.renderRow(i, r, inner))
	}
	if len(p.past) > 0 {
		lines = append(lines, "", subtleStyle.Render(truncate(pastHeading(len(p.past), p.pastTotal, inner), inner)))
		for i, r := range p.past {
			lines = append(lines, p.renderPastRow(len(p.runs)+i, r, inner))
		}
	}
	lines = append(lines, "", p.renderDetail(inner))

	box := lipgloss.NewStyle().
		Border(lipgloss.RoundedBorder()).
		BorderForeground(widgets.Surface()).
		Padding(1, 2).
		Render(strings.Join(lines, "\n"))

	// The way out differs by which screen this is, and it is the one hint that must not be wrong: esc
	// on the pre-flight screen ends the Face, esc in the switcher returns to the run it is showing.
	out := " quit"
	if p.attached != "" {
		out = " cancel"
	}
	hint := strings.Join([]string{
		key("↑↓") + subtleStyle.Render(" move"),
		key("1-9") + subtleStyle.Render(" attach"),
		key("enter") + subtleStyle.Render(" attach"),
		key("esc") + subtleStyle.Render(out),
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
	// In the switcher the run you are LOOKING AT outranks the run this directory holds: `*` answers
	// "which one is here", and while a Face is attached the more pressing question is "which one am
	// I on". Same gutter, so no column moves.
	if p.attached != "" && r.BaseURL == p.attached {
		marker = "● "
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

// pastHeading labels the history section. It says read-only in the heading, not only in the detail
// row, because that is the fact which decides whether pressing enter will do anything.
//
// KS2.4: when the engine had more than it sent, the heading says so and says where the rest is. The
// forms are tried longest-first against the width the section actually has, because a heading that
// fits by dropping "conductor history for the rest" has kept its layout and thrown away the only
// sentence in it that helps — and a list silently showing its first page is indistinguishable from a
// machine that has had exactly eight runs.
func pastHeading(shown, total, width int) string {
	if total <= shown {
		if shown == 1 {
			return "— 1 past run on this machine (read-only)"
		}
		return fmt.Sprintf("— %d past runs on this machine (read-only)", shown)
	}
	for _, form := range []string{
		fmt.Sprintf("— %d of %d past runs (read-only) · conductor history for the rest", shown, total),
		fmt.Sprintf("— %d of %d past runs · conductor history", shown, total),
		fmt.Sprintf("— %d of %d past runs", shown, total),
	} {
		if lipgloss.Width(form) <= width {
			return form
		}
	}
	return fmt.Sprintf("— %d of %d", shown, total)
}

// renderPastRow is a live row's twin: same widths, same plain-then-style discipline, no port and no
// number key, so the two halves read as one list of runs split by whether anything is still serving
// them. One column carries a different fact: where a live row shows the stage it is IN, a finished
// run shows the checkpoints it ENDED with, because there is no stage to be in any more.
func (p PickerModel) renderPastRow(index int, r PastRun, width int) string {
	repoW, statusW := 18, 26
	if width < 76 {
		repoW, statusW = 14, 16
	}

	progress := "-"
	if r.Total > 0 {
		progress = fmt.Sprintf("%d/%d", r.Done, r.Total)
	}
	plain := fmt.Sprintf("%s%s  %-*s  %-*s  %-*s  %5s  %8s",
		"  ", " ",
		repoW, truncate(r.RepoLabel(), repoW),
		10, truncate(progress, 10),
		statusW, truncate(r.Status, statusW),
		"-",
		fmt.Sprintf("$%.2f", r.CostUsd))
	plain = truncate(plain, width-1)

	if index == p.cursor {
		return highlightBg.Render("▸" + plain)
	}
	return subtleStyle.Render(" " + plain)
}

// renderDetail is the row under the list: everything about the highlighted run that does not fit in a
// column — where it lives on disk, which process it is, and whether the Face will be able to write.
func (p PickerModel) renderDetail(width int) string {
	if p.isPast(p.cursor) {
		r := p.past[p.cursor-len(p.runs)]
		head := fmt.Sprintf("run %s  ·  finished  ·  read-only archive (served from run.db)", r.ShortRunID())
		tail := r.PlanName
		if r.RunDb != "" {
			tail += "  ·  " + r.RunDb
		}
		// A row the engine could not read says so HERE, where the reader is looking before they press
		// enter — and the sentence is the engine's own refusal, not a second wording of it. Pressing
		// enter anyway is fine: the engine answers with this same line and opens nothing.
		if !r.Readable() {
			head = fmt.Sprintf("run %s  ·  cannot be opened", r.OpenWith())
			tail = r.Problem
		}
		return subtleStyle.Render(truncate(head, width)) + "\n" +
			subtleStyle.Render(truncate(tail, width))
	}
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
	if p.attached != "" && r.BaseURL == p.attached {
		write += "  ·  attached now"
	}
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
