package tui

import (
	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/lastrun"
	"conductor-face-go/internal/templates"
	"conductor-face-go/internal/widgets"
)

// MainTab selects which view fills the content pane. Everything the old build hid behind a modal is
// now a first-class tab, one keypress away, with the plan sidebar always beside it.
type MainTab int

const (
	// TabHome is the landing page (U1.1) and the tab the Face opens on: where am I, what is running,
	// in which directory, what does it cost, what next — answerable before pressing anything.
	TabHome MainTab = iota
	TabAgent
	// TabHistory is SF1.3's merge of the old Sessions and Timeline tabs: they were one question asked
	// twice, because a session IS a timeline span (the spine's `session` entries carry the very
	// SessionNumber /sessions lists). Two views, one surface — see historyView.
	TabHistory
	TabProcesses
	TabTemplates
	TabPlan
	TabReport
	TabKnowledge
	TabTelegram
	TabKanban
	// SF1.2: TabDev is gone. It was the developer screen (U2.3) built around the SQL console that used
	// to BE Report, and the owner's verdict was "delete this stupid sql query report and its traces".
	// Its two non-SQL panels were not deleted with it — they were re-homed to the surfaces that already
	// answer their question: the wiring internals to Home's Server/Workspace panels, and the per-session
	// token/cost table to the Report tab.
	//
	// SF1.3: TabConsole is gone too, but FOLDED rather than deleted — the raw agent stdout it rendered
	// is now the Agent tab's raw-stream mode (`agentRaw`), strip and all. See docs/dev/adr/0004.
	tabCount
)

var tabNames = [tabCount]string{"Home", "Agent", "History", "Procs", "Templates", "Plan", "Report", "Knowledge", "Telegram", "Kanban"}

// tabKey is the mnemonic that jumps straight to each tab (also shown in the strip). First-letter where
// it's free; Procs takes o and Telegram takes g (their first letters collide), Plan takes p — freed
// by moving sidebar-collapse to `\` — and Kanban takes b ("board"; k is Knowledge). Home takes h, and
// History keeps Sessions' `s` rather than claiming `h`: Home is the landing page every user hits
// first, and `s`/`t` both still reach History anyway (see foldedTabKey), which is worth more than a
// first-letter match. At ten tabs the digit row addresses ALL of them — 1–9 then 0 — so unlike every
// earlier version of this comment there is no "and the last few have no digit" caveat to make.
//
// SF1.2 freed `d` when the Dev tab went. It is deliberately left UNBOUND rather than reassigned: `d`
// meant "the SQL console" to anyone who used this Face, and quietly landing them somewhere else is
// worse than a keypress that does nothing. The Plan editor's delete stays on `x`, where U2.3 moved it.
//
// Keep in sync with renderHelpOverlay's Tabs legend — that legend is hand-maintained, so a mnemonic
// changed here and not there makes the help lie.
var tabKey = [tabCount]string{"h", "a", "s", "o", "e", "p", "r", "k", "g", "b"}

// foldedTabKey is the second half of the mnemonic story, and the reason SF1.3 is not SF1.2. `d` went
// dead because its surface was DELETED — landing that user anywhere would be a lie. `c` and `t` name
// surfaces that still EXIST, one level in, so they keep their meaning: each opens the tab that
// absorbed it, already showing the absorbed view. Nothing a user of this Face has learned stops
// working. Declared as a map, not scattered `case` arms, so TestTabMnemonicsAreUnique can pin these
// and tabKey as ONE namespace — an alias colliding with a tab mnemonic is unreachable in exactly the
// way a duplicate tabKey entry is. Keep in sync with the help legend's folded row.
var foldedTabKey = map[string]MainTab{
	"c": TabAgent,   // the old Console tab — Agent's raw stream (and a toggle once Agent is up)
	"t": TabHistory, // the old Timeline tab — History's spine view
}

// historyView selects which of TabHistory's two views fills the pane. `←/→` switches them, which is
// this codebase's sub-section idiom (planTab), and `s`/`t` jump straight to one from anywhere.
type historyView int

const (
	historySessions historyView = iota
	historyTimeline
)

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

	// SF2.1: where this run keeps its state on disk, and the summary the engine left there when it
	// finished. Learned from /state while the engine lives, from discovery when it does not, and read
	// only when the link drops — Home's answer to "what happened?" once there is nothing to poll.
	stateDir string
	lastRun  *lastrun.Summary

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

	// ctrl+c is a double-tap to quit (Claude Code's convention): the first tap arms this and shows a
	// hint toast, the second quits. Any other keypress disarms it, so a stray ctrl+c never leaves the
	// app one keystroke from exit. `q` remains the unguarded, explicit quit.
	quitArmed bool

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

	// agentRaw swaps the Agent tab's body from the parsed transcript to the raw agent stdout that used
	// to be the Console tab (SF1.3). The strip stays in both modes — keeping mission control on screen
	// while reading raw output is the whole reason this folded instead of staying its own tab.
	agentRaw bool

	// historyView selects TabHistory's view: the sessions list or the run's spine.
	historyView historyView

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
	// timelineHistoryCount is how many events already existed when this Face attached; everything at
	// or past that index arrived live, and the pane rules a "live" line there (dogfood appendix 6).
	// timelineHistorySet distinguishes "attached to a run with zero events" from "never fetched" —
	// a plain 0 count cannot.
	timelineHistoryCount int
	timelineHistorySet   bool

	// Sessions tab
	sessionSelected int

	// Report tab (U2.2: the rendered run report — scroll is its only interaction). SF1.2 deleted the
	// Dev console's state that used to sit below this block (reportEditor, reportQuickSelected,
	// reportFocusQuery, reportHScroll, reportHistory, devScroll) — the console is gone, so none of it
	// has a reader.
	reportScroll int
	// reportScores holds the verifier scores from GET /scores (SF1.1). It is deliberately NOT
	// data.ReportResult: that field belongs to the Dev console, and sharing it would make opening
	// Report silently wipe the developer's last query result.
	reportScores *api.ScoresDto
	// reportScoresErr is the fetch failure, kept beside the data rather than smuggled into it — a
	// ScoresDto has no error field, because a typed endpoint's failure is an HTTP failure.
	reportScoresErr string

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
	// W4.3: the pending add is a STAGE-level card (a checkpoint the engine will schedule),
	// not a subtask under an existing checkpoint.
	kanbanAddStage bool
	kanbanStatus   string
	// tasksErr / tasksLoaded exist so an empty board can say WHY it is empty (dogfood appendix 5).
	// Three states read identically without them — never fetched, fetch failed, genuinely no cards —
	// and the pane confidently claimed the third.
	tasksErr    string
	tasksLoaded bool

	// Kanban card detail (P3): the selected card's prompt building-blocks, the structured
	// title/context editors, and the advisor-refine preview→confirm state.
	kanbanDetail       bool
	kanbanBlocks       *api.PromptBlocksDto
	kanbanBlocksErr    string
	kanbanEditingTitle bool
	kanbanTitleBuf     string
	kanbanEditingCtx   bool
	kanbanCtxEditor    widgets.TextArea
	// PF3: the declared-paths editor (comma-separated single line; empty save clears the claims).
	kanbanEditingPaths bool
	kanbanPathsBuf     string
	kanbanRefining     bool
	kanbanProposal     *api.TaskRefineResultDto
	// W4.3: the advisor's proposed split — a list of children, applied one /tasks/add at a time.
	kanbanSplitting    bool
	kanbanSplit        *api.TaskSplitResultDto
	kanbanSplitPending []api.TaskSplitChildDto
	kanbanHandConfirm  bool
}

func New(source api.DataSource, isDemo bool, baseURL string) Model {
	m := Model{
		source:       source,
		isDemo:       isDemo,
		baseURL:      baseURL,
		tab:          TabHome,
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

// WithStateDir tells the Face which directory holds this run's state, discovered on disk before any
// connection is attempted (main.go). It is how a Face started AFTER the engine exited can still read
// the run summary — a setter rather than a New parameter so the eleven test call sites of New, and
// anyone embedding the model, keep working unchanged.
func (m Model) WithStateDir(dir string) Model {
	m.stateDir = dir
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
