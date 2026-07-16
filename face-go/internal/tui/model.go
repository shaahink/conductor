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
	TabKnowledge
	TabTelegram
	TabKanban
	tabCount
)

var tabNames = [tabCount]string{"Agent", "Sessions", "Timeline", "Procs", "Console", "Templates", "Plan", "Report", "Knowledge", "Telegram", "Kanban"}

// tabKey is the mnemonic that jumps straight to each tab (also shown in the strip). First-letter where
// it's free; Procs takes o and Telegram takes g (their first letters collide), Plan takes p — freed
// by moving sidebar-collapse to `\` — and Kanban takes b ("board"; k is Knowledge). Kanban is the
// 11th tab: there is no spare digit past 0, so it's reached by b and tab-cycle only. Keep this in
// sync with renderHelpOverlay's Tabs legend.
var tabKey = [tabCount]string{"a", "s", "t", "o", "c", "e", "p", "r", "k", "g", "b"}

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
	planTabPrompt
	planTabCount
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

	tab              MainTab
	cmd              CmdMode
	sidebarCollapsed bool

	transcript widgets.TranscriptModel
	sidebar    widgets.SidebarModel

	toasts     []widgets.Toast
	toastAnims map[int]*toastAnimState

	// Liveness spinner (top bar): ticks only while the engine reports an active agent session.
	spinnerFrame int
	spinnerLive  bool

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
	promptEntries     []templates.Entry
	promptSelected    int
	promptEditor      widgets.TextArea
	promptMode        PromptMode
	promptPreview     *api.PromptPreviewDto
	promptPreviewOn   bool
	promptPreviewErr  string
	promptPreviewKind int // index into previewKinds — which session kind's compiled prompt to show

	// Timeline tab
	timelineEntries  []api.TimelineEntryDto
	timelineSelected int
	timelineLoading  bool
	timelineErr      string

	// Sessions tab
	sessionSelected int

	// Report tab
	reportEditor        widgets.TextArea
	reportQuickSelected int
	reportFocusQuery    bool
	reportHScroll       int      // horizontal scroll (steps) for wide result tables
	reportHistory       []string // recently-run queries, most-recent-first

	// Processes tab
	processSelected int
	processKilling  bool // kill-confirm prompt open for the selected process

	// Knowledge tab (M7: ledger + tracked bugs; write-side: file note/bug, resolve bug)
	knowledgeScroll int
	knowledgeMode   knowledgeInputMode // browse, or entering a note / bug title / bug id to resolve
	knowledgeInput  widgets.TextArea

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
	planEnumCustom   bool // an enum field's "✎ custom…" option is selected → free-text sub-entry
	planStatus       string
	planImportInput  string
	planImportResult *api.PlanImportResultDto
	planImportErr    string
	planImportSource string // what was actually posted (path or prompt) — `a` re-posts it with apply:true
	planImportBusy   bool   // a prompt is at the advisor — block re-submits, show progress
	planPromptEditor widgets.TextArea
	planAdding       bool // add-stage / add-gate form open (id + title/command)
	planAddField     int  // 0 = id/name, 1 = title/command
	planAddIdBuf     string
	planAddValBuf    string
	planDeleting     bool // delete-confirm prompt open for the selected stage/gate

	// Telegram tab (M8.2): guided setup — status, field editor (token/chat ids/poll
	// interval/two-way), and a one-shot "send test message" action row, all in-pane
	// (STYLE.md: a persistent settings form, not the transient bottom command bar).
	telegramStatus     *api.TelegramStatusDto
	telegramFieldIdx   int
	telegramEditing    bool
	telegramEditBuf    string
	telegramEnumIdx    int
	telegramStatusLine string

	// Kanban tab (G2.2): live board of the run's task graph. Selection is by task id so a card
	// keeps focus while it moves between columns and across live refreshes.
	kanbanSelId  string
	kanbanAdding bool
	kanbanAddBuf string
	kanbanStatus string
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
