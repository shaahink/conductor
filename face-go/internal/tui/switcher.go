package tui

// KS2.4 — switching runs without restarting the Face.
//
// The picker was a pre-flight screen: it ran once, before anything was connected, and the only way
// back to it was to quit the Face and type `conductor face --pick` again. On the machine this ships
// to that is the common case, not the exotic one — several websites, several engines, several ports —
// and quitting is not a neutral way to change which one you are looking at: the process restarts
// into defaults. Whatever scheme was chosen for this launch, whichever tab was open, whether the
// sidebar was collapsed: all of it is a fresh `New` away from being forgotten.
//
// So the SAME picker is shown again, in the same process, over the run already attached, and the
// choice swaps the data source instead of the process. What survives is what a person would call
// their session — theme (a package-level palette this file never touches), tab, sidebar, window —
// and what does not survive is everything that belonged to the OLD run: its state, its transcript,
// its plan, its cursors, and above all its write token.
//
// The token is the reason the swap is a whole-model rebuild rather than a few assignments. Every
// per-tab model holds something fetched from the run it was attached to, and a switch that reset the
// fields someone remembered to reset would leave the rest describing a run nobody is looking at. New
// builds all of them, so the list of things that carry over is written down HERE, in one place, and
// is short enough to read.

import (
	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

// switchVerb is the palette key that opens the switcher. It lives in the palette's Face group — the
// group for verbs that change THIS Face and never reach the engine — rather than claiming a global
// letter: the switcher is not a per-pane action, and the letters left free after ten tab mnemonics,
// two folded ones, the pane-scroll set and the reader are too few to spend on a screen opened once
// a sitting.
const switchVerb = "switch"

// switcherModel is the whole state of the overlay: is it up, and the picker it is showing. The
// picker is a value like every other sub-model, so opening one takes nothing away from the
// dashboard underneath — cancelling returns to exactly the surface it was opened from.
type switcherModel struct {
	open   bool
	picker PickerModel
}

// WithFleet hands the Face the runs the engine found (CONDUCTOR_FLEET). It is what the switcher has
// to offer, and it arrives at startup because the Face cannot probe ports itself — that scan is the
// engine's, and repeating it here would be a second opinion about which runs exist.
func (m Model) WithFleet(f Fleet) Model {
	m.fleet = f
	return m
}

// Handoff reports a FINISHED run chosen from the switcher. A past run has no control plane and the
// Face cannot start one, so this leaves by the same door the pre-flight picker uses: main.go writes
// the selector to the file named in CONDUCTOR_PICK and the engine serves that run's run.db read-only.
// Exported because the file protocol belongs to main.go, not to this package — the same separation
// the startup picker already keeps.
func (m Model) Handoff() (PastRun, bool) {
	if m.handoff == nil {
		return PastRun{}, false
	}
	return *m.handoff, true
}

// openSwitcher shows the picker over the current run. Nothing is fetched and nothing is closed: the
// dashboard underneath is untouched until a different run is actually chosen.
func (m Model) openSwitcher() (tea.Model, tea.Cmd) {
	if len(m.fleet.Runs) == 0 && len(m.fleet.Past) == 0 {
		// A Face started with `--url` and no envelope (or an archive Face, whose credentials the
		// engine strips) knows of no other run. Say that, rather than opening an empty list.
		return m, m.addToast("no other runs in this Face's fleet — try conductor face --pick", widgets.ToastInfo)
	}
	m.switcher = switcherModel{
		open: true,
		picker: NewPicker(m.fleet.Runs).
			WithPast(m.fleet.Past, m.fleet.PastTotal).
			WithAttached(m.baseURL),
	}
	return m, nil
}

// switcherPicker sizes the picker to the live window. Sizing at RENDER rather than at open is the
// same rule the pane viewports follow: a terminal resized while the switcher is up must not paint a
// screen measured against the old one.
func (m Model) switcherPicker() PickerModel {
	p := m.switcher.picker
	p.width, p.height = m.width, m.height
	return p
}

// handleSwitcherKey owns every key while the switcher is up, peeled in Update at the same precedence
// as the command bar and the reader.
//
// It does NOT simply forward to PickerModel.handleKey and honour its command: in the pre-flight
// screen `esc` and `q` mean "end this process", and forwarding that tea.Quit from inside a running
// dashboard would kill the Face on the key that is supposed to cancel. So the two exits are answered
// here, and the picker's own quit command is read only as "the list has an answer".
func (m Model) handleSwitcherKey(key string) (tea.Model, tea.Cmd) {
	if key == "ctrl+c" {
		// The one exception, by the rule handleKey states: the global quit affordance must not be
		// swallowable by a sub-state.
		return m.handleKey(key)
	}
	m.quitArmed = false
	if key == "esc" || key == "q" {
		m.switcher = switcherModel{}
		return m, nil
	}

	next, cmd := m.switcher.picker.handleKey(key)
	p, ok := next.(PickerModel)
	if !ok {
		return m, nil
	}
	m.switcher.picker = p
	if cmd == nil {
		return m, nil // still browsing
	}

	m.switcher = switcherModel{}
	if run, chosen := p.Chosen(); chosen {
		return m.attachTo(run)
	}
	if past, opened := p.ChosenPast(); opened {
		// A finished run needs an engine to serve it. Quit with the choice recorded; main.go hands it
		// back and the engine opens the read-only archive plane over it (KS2.2).
		m.handoff = &past
		return m, tea.Quit
	}
	return m, nil
}

// attachTo points this Face at another live run, in this process.
//
// The old source is CLOSED first (its cancel stops the SSE readers), then the channels are drained,
// and only then is the new source subscribed — otherwise a line the previous run emitted a moment
// ago arrives after the switch and is rendered as the new run's. The channels themselves carry over
// on purpose: the tea commands blocked on them were issued at Init and are still outstanding, so
// replacing the channels would leave those goroutines waiting on a queue nobody writes to while the
// new one is never read.
func (m Model) attachTo(run FleetRun) (tea.Model, tea.Cmd) {
	if run.BaseURL == "" {
		return m, m.addToast("that run has no control plane to attach to", widgets.ToastError)
	}
	if run.BaseURL == m.baseURL {
		return m, m.addToast("already attached to "+run.RepoLabel(), widgets.ToastInfo)
	}

	old := m.source
	old.Close()
	drainChan(m.eventCh)
	drainChan(m.txCh)
	drainChan(m.consoleCh)
	drainChan(m.eventsConnCh)
	drainChan(m.txConnCh)

	// The token is the chosen run's own or none at all. It never travels in argv and it is never
	// carried over from the run being left: FaceTarget.LookupToken matched it to this run's state dir
	// on the engine side, and a Face that kept the previous token would POST it at a plane that has
	// every right to refuse — and reads need none, so the refusal would be the first anyone heard.
	next := New(api.NewLiveSourceWithToken(run.BaseURL, run.Token), false, run.BaseURL)
	next.eventCh, next.txCh, next.consoleCh = m.eventCh, m.txCh, m.consoleCh
	next.eventsConnCh, next.txConnCh = m.eventsConnCh, m.txConnCh

	// What a session is, written down: the window, the surface you were on, the chrome you set, and
	// the fleet you are switching within. Everything else belongs to the run and is gone with it.
	next.width, next.height = m.width, m.height
	next.tab = m.tab
	next.sidebarCollapsed = m.sidebarCollapsed
	next.fleet = m.fleet
	next.toasts, next.toastAnims = m.toasts, m.toastAnims
	next.stateDir = run.StateDir
	next.recalcDimensions()

	// addToast mutates the model it is called on, so its command is taken BEFORE the model is handed
	// back — the evaluation order of a return statement's operands is not something to bet a toast on.
	toast := next.addToast("attached to "+run.RepoLabel(), widgets.ToastSuccess)
	next.subscribeStreams()
	return next, tea.Batch(next.doPoll(), next.cmdFetchPlan(), toast)
}

// drainChan empties a buffered channel without blocking. What is in it belongs to the run being left.
func drainChan[T any](ch chan T) {
	for {
		select {
		case <-ch:
		default:
			return
		}
	}
}
