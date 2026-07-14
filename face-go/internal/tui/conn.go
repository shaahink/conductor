package tui

import (
	"sync/atomic"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

func (m Model) doPoll() tea.Cmd {
	return func() tea.Msg {
		if m.data.Connection.Mode == api.ModeDemo {
			return m.pollDemo()
		}
		return m.pollLive()
	}
}

var demoLineIdx atomic.Int64

func (m Model) pollDemo() tea.Msg {
	state, err := m.source.FetchState()
	if err != nil || state == nil {
		return nil
	}

	idx := demoLineIdx.Add(1)

	demoLines := []struct {
		kind string
		text string
	}{
		{"system", "Session #12 started \u00B7 Deliver \u00B7 Stage F7 \u00B7 Attempt 2"},
		{"thinking", "Let me examine the GateCache implementation to understand the caching pattern..."},
		{"tool", "read src/Conductor/Core/Gating/GateCache.cs"},
		{"result", "GateCache.cs:142 lines \u2014 caches by (name, tier, sha)"},
		{"thinking", "I see. GateResult is stored with a composite key. I need to expose the last-passing result via RunDb."},
		{"agent", "Found the caching layer. Adding GetLastPassingGateResult to RunDb."},
		{"tool", "write src/Conductor/Core/Store/RunDb.Gates.cs"},
		{"result", "Created RunDb.Gates.cs with GetLastPassingGateResult query"},
		{"thinking", "The query needs to join gates with attempts to find the most recent pass."},
		{"tool", "run dotnet test --filter GateCacheTests"},
		{"result", "12/12 tests pass. 0w/0e. 2.3s elapsed"},
		{"agent", "All tests pass. Ready for the next checkpoint."},
		{"system", "Gate build \u2713 (2.3s)"},
		{"tool", "run dotnet build Conductor.slnx"},
		{"result", "Build succeeded. 0 Error(s), 0 Warning(s)"},
		{"system", "Gate test \u2713 (4.1s)"},
		{"agent", "Running gate battery: build \u2713, test \u2713, lint is next."},
	}

	lineIdx := int(idx) % len(demoLines)
	dl := demoLines[lineIdx]

	tx := api.TranscriptLineDto{
		Seq:       idx,
		SessionId: "s12",
		Kind:      dl.kind,
		Text:      dl.text,
	}

	return MsgPollResult{
		State:       state,
		Transcripts: []api.TranscriptLineDto{tx},
	}
}

func (m Model) pollLive() tea.Msg {
	state, err := m.source.FetchState()
	if err != nil {
		return MsgFetchError{Err: err.Error()}
	}
	return MsgStateUpdated{State: state}
}

func init() {
	_ = widgets.MsgAppendLine{}
}
