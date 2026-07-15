package tui

import (
	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/templates"
	"conductor-face-go/internal/widgets"
)

// MainTab selects which view fills the content pane. Everything the old build hid behind a modal is
// now a first-class tab, one keypress away, with the plan sidebar always beside it.
type MainTab int

const (
	TabAgent MainTab = iota
	TabSessions
	TabTimeline
	TabProcesses
	TabConsole
	TabTemplates
	TabPlan
	TabReport
	tabCount
)

var tabNames = [tabCount]string{"Agent", "Sessions", "Timeline", "Procs", "Console", "Templates", "Plan", "Report"}

// tabKey is the mnemonic that jumps straight to each tab (also shown in the strip).
var tabKey = [tabCount]string{"a", "h", "t", "s", "c", "e", "g", "r"}

// CmdMode is a transient bottom-bar input that floats over the dashboard instead of a full modal.
type CmdMode int

const (
	CmdNone CmdMode = iota
	CmdPalette
	CmdInject
	CmdHelp
)

type planTab int

const (
	planTabStages planTab = iota
	planTabGates
	planTabSettings
	planTabImport
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

	tab             MainTab
	cmd             CmdMode
	sidebarCollapsed bool

	transcript widgets.TranscriptModel
	sidebar    widgets.SidebarModel

	toasts     []widgets.Toast
	toastAnims map[int]*toastAnimState

	// Inline transcript search (non-blocking, Agent tab only).
	searchActive bool

	// Streaming channels (see conn.go).
	eventCh      chan api.ConductorEventDto
	txCh         chan api.TranscriptLineDto
	consoleCh    chan api.ConsoleLineDto
	eventsConnCh chan bool
	txConnCh     chan bool

	eventSeq   int64
	txSeq      int64
	consoleSeq int64

	consoleScroll int

	// Palette (bottom command bar)
	paletteQuery      string
	paletteSelected   int
	paletteConfirming bool
	paletteVerbIdx    int
	paletteGotoActive bool
	paletteGotoInput  string

	// Inject (bottom command bar)
	injectContent string
	injectStageId string
	injectField   int

	// Templates tab (list + editor + compiled preview)
	promptEntries    []templates.Entry
	promptSelected   int
	promptContent    string
	promptMode       PromptMode
	promptPreview    *api.PromptPreviewDto
	promptPreviewOn  bool
	promptPreviewErr string

	// Timeline tab
	timelineEntries  []api.TimelineEntryDto
	timelineSelected int
	timelineLoading  bool
	timelineErr      string

	// Sessions tab
	sessionSelected int

	// Report tab
	reportSQL           string
	reportQuickSelected int
	reportFocusQuery    bool

	// Processes tab
	processSelected int

	// Plan tab (M6.3 editor)
	plan             *api.PlanDto
	planTab          planTab
	planStageIdx     int
	planGateIdx      int
	planFieldIdx     int
	planDrill        bool
	planEditing      bool
	planEditBuf      string
	planEnumIdx      int
	planStatus       string
	planImportInput  string
	planImportResult *api.PlanImportResultDto
	planImportErr    string
	planLoadRequested bool
}

func New(source api.DataSource, isDemo bool, baseURL string) Model {
	m := Model{
		source:       source,
		isDemo:       isDemo,
		baseURL:      baseURL,
		tab:          TabAgent,
		cmd:          CmdNone,
		transcript:   widgets.NewTranscript(),
		sidebar:      widgets.NewSidebar(),
		eventCh:      make(chan api.ConductorEventDto, 256),
		txCh:         make(chan api.TranscriptLineDto, 1024),
		consoleCh:    make(chan api.ConsoleLineDto, 1024),
		eventsConnCh: make(chan bool, 8),
		txConnCh:     make(chan bool, 8),
		data: api.AppState{
			Connection: api.ConnectionState{Mode: api.ModeLive, URL: baseURL},
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
		m.cmdFetchPlan(), // the sidebar-adjacent Plan tab is ready without a round-trip on first open
		waitForEvent(m.eventCh),
		waitForTranscript(m.txCh),
		waitForConsole(m.consoleCh),
		waitForEventsConn(m.eventsConnCh),
		waitForTxConn(m.txConnCh),
	)
}
