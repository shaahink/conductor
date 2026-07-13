package tui

import (
	"time"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
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

	toasts []widgets.Toast

	pollCancel chan struct{}

	lastPoll time.Time
	eventSeq int64
	txSeq    int64

	// --- Modal state ---

	// Palette
	paletteQuery      string
	paletteSelected   int
	paletteConfirming bool
	paletteVerbIdx    int

	// Inject
	injectContent string
	injectStageId string
	injectField   int // 0=stage, 1=content

	// Prompt
	promptTemplates []string
	promptSelected  int
	promptContent   string
	promptMode      PromptMode

	// Sessions
	sessionSelected int

	// Report
	reportSQL           string
	reportQuickSelected int
	reportFocusQuery    bool
}

func New(source api.DataSource, isDemo bool, baseURL string) Model {
	m := Model{
		source:      source,
		isDemo:      isDemo,
		baseURL:     baseURL,
		sidebarOpen: false,
		activeModal: ModalNone,
		transcript:  widgets.NewTranscript(),
		sidebar:     widgets.NewSidebar(),
		pollCancel:  make(chan struct{}),
		promptTemplates: []string{
			"session.md", "fix.md", "resume.md",
			"advisor.md", "audit.md", "review.md",
			"verify.md",
		},
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
	return tea.Batch(
		CmdTick(),
		m.doPoll(),
	)
}
