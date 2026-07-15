package tui

import (
	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/templates"
	"conductor-face-go/internal/widgets"
)

type ModalKind int

const (
	ModalNone ModalKind = iota
	ModalPalette
	ModalInject
	ModalPrompt
	ModalSessions
	ModalReport
	ModalProcesses
	ModalTimeline
	ModalConsole
	ModalHelp
)

type PromptMode int

const (
	PromptList PromptMode = iota
	PromptEdit
)

type Model struct {
	source  api.DataSource
	isDemo  bool
	baseURL string

	width  int
	height int

	data api.AppState

	sidebarOpen bool
	activeModal ModalKind

	transcript widgets.TranscriptModel
	sidebar    widgets.SidebarModel

	toasts     []widgets.Toast
	toastAnims map[int]*toastAnimState

	// Inline transcript search (not a modal — mirrors Ink's non-blocking agent-pane search).
	searchActive bool

	// Streaming: SSE callbacks (running on background goroutines started in subscribeStreams)
	// push onto these channels; waitForX commands re-arm themselves after each read so the
	// program keeps listening for the life of the process.
	eventCh      chan api.ConductorEventDto
	txCh         chan api.TranscriptLineDto
	consoleCh    chan api.ConsoleLineDto
	eventsConnCh chan bool
	txConnCh     chan bool

	eventSeq   int64
	txSeq      int64
	consoleSeq int64

	// Native console (M5.3): raw agent stdout, scrolled independently of the transcript.
	consoleScroll int

	// --- Modal state ---

	// Palette
	paletteQuery      string
	paletteSelected   int
	paletteConfirming bool
	paletteVerbIdx    int
	paletteGotoActive bool
	paletteGotoInput  string

	// Inject
	injectContent string
	injectStageId string
	injectField   int // 0=stage, 1=content

	// Prompt (template editor + compiled preview, M5.5)
	promptEntries    []templates.Entry
	promptSelected   int
	promptContent    string
	promptMode       PromptMode
	promptPreview    *api.PromptPreviewDto
	promptPreviewOn  bool
	promptPreviewErr string

	// Timeline (M5.1)
	timelineEntries  []api.TimelineEntryDto
	timelineSelected int
	timelineLoading  bool
	timelineErr      string

	// Sessions
	sessionSelected int

	// Report
	reportSQL           string
	reportQuickSelected int
	reportFocusQuery    bool

	// Processes
	processSelected int
}

func New(source api.DataSource, isDemo bool, baseURL string) Model {
	m := Model{
		source:       source,
		isDemo:       isDemo,
		baseURL:      baseURL,
		sidebarOpen:  false,
		activeModal:  ModalNone,
		transcript:   widgets.NewTranscript(),
		sidebar:      widgets.NewSidebar(),
		eventCh:      make(chan api.ConductorEventDto, 256),
		txCh:         make(chan api.TranscriptLineDto, 1024),
		consoleCh:    make(chan api.ConsoleLineDto, 1024),
		eventsConnCh: make(chan bool, 8),
		txConnCh:     make(chan bool, 8),
		data: api.AppState{
			Connection: api.ConnectionState{
				Mode: api.ModeLive,
				URL:  baseURL,
			},
		},
	}

	if isDemo {
		m.data.Connection.Mode = api.ModeDemo
		m.data.Connection.Connected = true
	}

	return m
}

func (m Model) Init() tea.Cmd {
	m.subscribeStreams()
	return tea.Batch(
		CmdTick(),
		m.doPoll(),
		waitForEvent(m.eventCh),
		waitForTranscript(m.txCh),
		waitForConsole(m.consoleCh),
		waitForEventsConn(m.eventsConnCh),
		waitForTxConn(m.txConnCh),
	)
}
