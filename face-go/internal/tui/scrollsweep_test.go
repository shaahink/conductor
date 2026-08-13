package tui

// KS2.7's sweep. TestEveryTabFitsEverySize (glitch_sweep_test.go) proves no frame OVERFLOWS its
// window; it says nothing about whether the body you came to read is reachable. Those are different
// bugs, and the second one is the owner's: "I can't read long text". A pane that clips its tail
// silently passes every mechanical check in this package — frameContent's MaxHeight makes sure of
// that, which is exactly how six surfaces shipped with no window at all.
//
// So this file asks the other question, at the same three sizes, through the same real router: drive
// a 500-line body into every surface, press `end` (and separately `G`), and check the LAST line of
// what the surface says its body is actually appears on screen.
//
// It reads the last line off each surface's own `<surface>Viewport()` rather than looking for a
// marker planted in a fixture. A marker proves a string survived; this proves the pane and the
// viewport AGREE about where the body ends, which is the property that broke.

import (
	"fmt"
	"strings"
	"testing"

	"charm.land/bubbles/v2/viewport"
	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// sweepBodyLines is how long "long" is: the plan's own figure, and comfortably past any pane at any
// of the three sizes.
const sweepBodyLines = 500

// scrollSweepCase is one surface: how to reach it, how to make its body long, and which viewport it
// scrolls. The viewport accessor is what makes the assertion honest — it is the SAME builder the
// renderer calls, so a surface that quietly renders something else fails here.
type scrollSweepCase struct {
	name string
	keys []string
	grow func(tea.Model) tea.Model
	vp   func(Model) viewport.Model
}

func longLines(prefix string, n int) []string {
	out := make([]string, 0, n)
	for i := 0; i < n; i++ {
		out = append(out, fmt.Sprintf("%s row %03d of the long body", prefix, i))
	}
	return out
}

// scrollSweepCases builds the surface table with a body of `n` rendered rows. The size is a
// parameter because the two acceptance measurements want different ones: the sweep below wants the
// plan's 500 lines, and the 400-down measurement in panescroll_test.go wants a body a little longer
// than the pane — 400 presses against 500 rows of markdown is thirty seconds of test time proving
// nothing the short body does not.
func scrollSweepCases(n int) []scrollSweepCase {
	return []scrollSweepCase{
		{
			name: "OwnerQueue", keys: []string{"w"},
			grow: func(m tea.Model) tea.Model {
				items := make([]api.OwnerQueueItemDto, 0, n/4+2)
				for i := 0; i < n/4+2; i++ {
					items = append(items, api.OwnerQueueItemDto{
						Id: "g" + itoa(i), Kind: "ownerGate", Title: "Stage K" + itoa(i) + " needs approval",
						Unblocks: "stage K" + itoa(i), Command: "conductor approve K" + itoa(i)})
				}
				m, _ = m.Update(MsgOwnerQueueUpdated{Queue: &api.OwnerQueueDto{
					Count: len(items), GeneratedUtc: "2026-07-15T10:00:00Z", Items: items}})
				return m
			},
			vp: Model.ownerQueueViewport,
		},
		{
			name: "AgentTranscript", keys: []string{"a"},
			grow: func(m tea.Model) tea.Model {
				for i, text := range longLines("transcript", n) {
					m, _ = m.Update(MsgTranscriptLine{Line: api.TranscriptLineDto{
						Seq: int64(1000 + i), SessionId: "1", Kind: "agent", Text: text}})
				}
				return m
			},
			vp: Model.agentTranscriptViewport,
		},
		{
			name: "AgentRaw", keys: []string{"c"},
			grow: func(m tea.Model) tea.Model {
				for i, text := range longLines("raw stdout", n) {
					m, _ = m.Update(MsgConsoleLine{Line: api.ConsoleLineDto{Seq: int64(1000 + i), Text: text}})
				}
				return m
			},
			vp: Model.agentRawViewport,
		},
		{
			name: "HistorySessions", keys: []string{"s"},
			grow: growSessions(n),
			vp:   Model.historySessionsViewport,
		},
		{
			name: "HistorySpine", keys: []string{"t"},
			grow: func(m tea.Model) tea.Model {
				entries := make([]api.TimelineEntryDto, 0, n)
				for i, text := range longLines("spine", n) {
					entries = append(entries, api.TimelineEntryDto{
						Kind: "stage", Utc: "2026-07-15T10:00:00Z",
						Description: fmt.Sprintf("%s #%d", text, i)})
				}
				m, _ = m.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: entries}})
				return m
			},
			vp: Model.historySpineViewport,
		},
		{
			name: "Processes", keys: []string{"o"},
			grow: func(m tea.Model) tea.Model {
				procs := make([]api.ProcessDto, 0, n)
				for i := 0; i < n; i++ {
					procs = append(procs, api.ProcessDto{
						Pid: 1000 + i, Purpose: fmt.Sprintf("gate:test-%03d", i), Alive: false,
						StartedUtc: "2026-07-15T10:00:00Z", ExitedUtc: strPtr("2026-07-15T10:01:00Z")})
				}
				m, _ = m.Update(MsgProcessesUpdated{Procs: &api.ProcessesDto{Processes: procs}})
				return m
			},
			vp: Model.processesViewport,
		},
		{
			name: "Plan", keys: []string{"p"},
			grow: func(m tea.Model) tea.Model {
				doc := fixedPlan()
				doc.Stages = nil
				for i := 0; i < n; i++ {
					doc.Stages = append(doc.Stages, api.PlanStageDto{
						Id: fmt.Sprintf("S%03d", i), Title: fmt.Sprintf("stage %03d of a long plan", i),
						Sessions: 3, Kind: "deliver"})
				}
				m, _ = m.Update(MsgPlanLoaded{Plan: doc})
				return m
			},
			vp: Model.planViewport,
		},
		{
			name: "Report", keys: []string{"r"},
			grow: growSessions(n),
			vp:   Model.reportViewport,
		},
		{
			name: "Knowledge", keys: []string{"k"},
			grow: func(m tea.Model) tea.Model {
				l := fixedLedger()
				s := func(n int) *int { return &n }
				for i, text := range longLines("ledger", n) {
					l.Entries = append(l.Entries, api.LedgerEntryDto{
						Id: int64(1000 + i), SessionNumber: s(1 + i%9), Kind: "note",
						Content: text, CreatedAt: "2026-07-15T10:00:00Z"})
				}
				m, _ = m.Update(MsgKnowledgeUpdated{Ledger: l, Bugs: fixedBugs(), Evidence: fixedEvidence()})
				return m
			},
			vp: Model.knowledgeViewport,
		},
		{
			name: "Telegram", keys: []string{"g"},
			grow: func(m tea.Model) tea.Model {
				st := fixedTelegramStatus()
				st.LastError = strPtr(strings.Join(longLines("poll error", n), "\n"))
				m, _ = m.Update(MsgTelegramStatusUpdated{Status: st})
				return m
			},
			vp: Model.telegramViewport,
		},
		{
			name: "KanbanDetail", keys: nil, // reached by openKanbanDetailGolden, which needs the board first
			grow: func(m tea.Model) tea.Model {
				m = openKanbanDetailGolden(m)
				blocks := *asModel(m).kanban.blocks
				blocks.Blocks = append([]api.PromptBlockDto{}, blocks.Blocks...)
				for i, text := range longLines("block", max(2, n/25)) {
					blocks.Blocks = append(blocks.Blocks, api.PromptBlockDto{
						Kind: fmt.Sprintf("extra%02d", i), Label: text,
						Content: strings.Join(longLines("card note", 25), "\n")})
				}
				m, _ = m.Update(MsgPromptBlocks{Blocks: &blocks})
				return m
			},
			vp: Model.kanbanDetailViewport,
		},
	}
}

// growSessions is the fixture behind both History-sessions and Report: real session rows, each of
// which renders several lines in one view and a table row in the other.
func growSessions(n int) func(tea.Model) tea.Model {
	return func(m tea.Model) tea.Model {
		return growSessionRows(m, max(4, n/3))
	}
}

func growSessionRows(m tea.Model, count int) tea.Model {
	rows := make([]api.SessionRowDto, 0, count)
	for i := count; i > 0; i-- { // newest-first, the wire order (STYLE.md)
		rows = append(rows, api.SessionRowDto{
			Number: i, StageId: fmt.Sprintf("S%02d", i%40), Kind: "Deliver",
			Outcome: strPtr("completed"), Attempt: 1, CommitCount: 1,
			StartedUtc: "2026-07-15T09:00:00Z", EndedUtc: strPtr("2026-07-15T09:30:00Z"),
			CostUsd: 0.05, TokensIn: 1000, TokensOut: 200, TokensThink: i64Ptr(10), TokensCache: 50,
			GateSummary: strPtr(fmt.Sprintf("build ✓ test ✓ (session %d)", i))})
	}
	m, _ = m.Update(MsgSessionsUpdated{Sessions: &api.SessionsDto{Sessions: rows}})
	return m
}

// scrollSweepExemptions are the surfaces this sweep deliberately does NOT cover, each with the
// reason, because an unexplained exemption is how the previous adoption pass stopped half-done.
// They are listed by name so a reader of the failing log can see what was decided rather than
// wondering what was forgotten (see also scrollSurfacesNotConverted in scroll_intent_test.go).
var scrollSweepExemptions = map[string]string{
	"Home (landing)": "STYLE.md: Home owns no keys by design and sheds tiers via fitHome " +
		"(tab_home.go) rather than scrolling. Its scrollable surface is the `w` owner queue, which " +
		"IS in this sweep.",
	"Kanban (board)": "kanbanWindow is a per-COLUMN clip; three columns cannot share one scroll " +
		"position, and three positions behind one key set is worse than the clip. The plan named " +
		"Kanban DETAIL, which is in this sweep.",
	"Templates (list)": "the list is templates.List's fixed prompt-template set — bounded by " +
		"construction, never a long body. The surface here that can outgrow the pane is the preview, " +
		"on previewVp since K6.4 (tab_templates_test.go covers it).",
}

// TestEveryTabScrollsA500LineBodyToItsEnd is the acceptance measurement: a 500-line body in every
// converted surface, at all three sizes, reachable to its last line with one keypress.
func TestEveryTabScrollsA500LineBodyToItsEnd(t *testing.T) {
	if len(scrollSweepExemptions) == 0 {
		t.Error("the exemption list was emptied without adding the surfaces to the sweep")
	}
	for _, size := range glitchSizes {
		for _, tc := range scrollSweepCases(sweepBodyLines) {
			for _, jump := range []string{"end", "G"} {
				t.Run(fmt.Sprintf("%s_%dx%d_%s", tc.name, size.w, size.h, jump), func(t *testing.T) {
					m := tc.grow(newGoldenModel(size.w, size.h))
					for _, k := range tc.keys {
						m = asModel(mustHandle(asModel(m).handleKey(k)))
					}
					// The fixture has to actually outgrow the pane, or every assertion below passes
					// vacuously — the failure mode that let this whole class of bug survive.
					before := tc.vp(asModel(m))
					if before.TotalLineCount() <= before.Height() {
						t.Fatalf("%s body is %d lines in a %d-row pane — it does not scroll, so this "+
							"proves nothing", tc.name, before.TotalLineCount(), before.Height())
					}

					m = asModel(mustHandle(asModel(m).handleKey(jump)))
					vp := tc.vp(asModel(m))
					if !vp.AtBottom() {
						t.Errorf("%s: %q left the pane at %d%% — the end of the body is unreachable",
							tc.name, jump, int(vp.ScrollPercent()*100))
					}
					last := lastReadableLine(stripANSI(vp.GetContent()))
					if last == "" {
						t.Fatalf("%s: could not find a readable last line in the body", tc.name)
					}
					frame := stripANSI(asModel(m).View().Content)
					if !strings.Contains(frame, last) {
						t.Errorf("%s at %dx%d: after %q the body's last line is still off-screen.\n"+
							"want to find: %q\nframe:\n%s", tc.name, size.w, size.h, jump, last, frame)
					}
				})
			}
		}
	}
}

// lastReadableLine is the last line of a body with enough text on it to look for in a frame. Blank
// rows and one-glyph rules are skipped: finding "─" in a frame proves nothing.
func lastReadableLine(body string) string {
	lines := strings.Split(body, "\n")
	for i := len(lines) - 1; i >= 0; i-- {
		if s := strings.TrimSpace(lines[i]); len([]rune(s)) >= 8 {
			return s
		}
	}
	return ""
}
