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
	"os"
	"path/filepath"
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
//
// `raw` reads the viewport where the MODEL stores it, not through the builder. The two answer
// different questions and only the second one is bug #30's: the builder re-sizes and re-loads, so it
// re-clamps, and an offset clamped only there would pass every assertion made through `vp` while
// still running away in Update. TestPaneOffsetIsClampedInUpdateNotInTheRenderer reads `raw`.
type scrollSweepCase struct {
	name string
	keys []string
	grow func(*testing.T, tea.Model) tea.Model
	vp   func(Model) viewport.Model
	raw  func(Model) viewport.Model
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
			grow: func(t *testing.T, m tea.Model) tea.Model {
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
			vp: Model.ownerQueueViewport, raw: func(m Model) viewport.Model { return m.home.queueVp },
		},
		{
			name: "AgentTranscript", keys: []string{"a"},
			grow: func(t *testing.T, m tea.Model) tea.Model {
				for i, text := range longLines("transcript", n) {
					m, _ = m.Update(MsgTranscriptLine{Line: api.TranscriptLineDto{
						Seq: int64(1000 + i), SessionId: "1", Kind: "agent", Text: text}})
				}
				return m
			},
			vp: Model.agentTranscriptViewport, raw: func(m Model) viewport.Model { return m.transcript.Vp },
		},
		{
			name: "AgentRaw", keys: []string{"c"},
			grow: func(t *testing.T, m tea.Model) tea.Model {
				for i, text := range longLines("raw stdout", n) {
					m, _ = m.Update(MsgConsoleLine{Line: api.ConsoleLineDto{Seq: int64(1000 + i), Text: text}})
				}
				return m
			},
			vp: Model.agentRawViewport, raw: func(m Model) viewport.Model { return m.agent.rawVp },
		},
		{
			name: "HistorySessions", keys: []string{"s"},
			grow: growSessions(n),
			vp:   Model.historySessionsViewport,
			raw:  func(m Model) viewport.Model { return m.history.sessionsVp },
		},
		{
			name: "HistorySpine", keys: []string{"t"},
			grow: func(t *testing.T, m tea.Model) tea.Model {
				entries := make([]api.TimelineEntryDto, 0, n)
				for i, text := range longLines("spine", n) {
					entries = append(entries, api.TimelineEntryDto{
						Kind: "stage", Utc: "2026-07-15T10:00:00Z",
						Description: fmt.Sprintf("%s #%d", text, i)})
				}
				m, _ = m.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: entries}})
				return m
			},
			vp: Model.historySpineViewport, raw: func(m Model) viewport.Model { return m.history.spineVp },
		},
		{
			name: "Processes", keys: []string{"o"},
			grow: func(t *testing.T, m tea.Model) tea.Model {
				procs := make([]api.ProcessDto, 0, n)
				for i := 0; i < n; i++ {
					procs = append(procs, api.ProcessDto{
						Pid: 1000 + i, Purpose: fmt.Sprintf("gate:test-%03d", i), Alive: false,
						StartedUtc: "2026-07-15T10:00:00Z", ExitedUtc: strPtr("2026-07-15T10:01:00Z")})
				}
				m, _ = m.Update(MsgProcessesUpdated{Procs: &api.ProcessesDto{Processes: procs}})
				return m
			},
			vp: Model.processesViewport, raw: func(m Model) viewport.Model { return m.processes.vp },
		},
		{
			name: "Plan", keys: []string{"p"},
			grow: func(t *testing.T, m tea.Model) tea.Model {
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
			vp: Model.planViewport, raw: func(m Model) viewport.Model { return m.plan.vp },
		},
		{
			name: "Report", keys: []string{"r"},
			grow: growSessions(n),
			vp:   Model.reportViewport,
			raw:  func(m Model) viewport.Model { return m.report.vp },
		},
		{
			name: "Knowledge", keys: []string{"k"},
			grow: func(t *testing.T, m tea.Model) tea.Model {
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
			vp: Model.knowledgeViewport, raw: func(m Model) viewport.Model { return m.knowledge.vp },
		},
		{
			name: "Telegram", keys: []string{"g"},
			grow: func(t *testing.T, m tea.Model) tea.Model {
				st := fixedTelegramStatus()
				st.LastError = strPtr(strings.Join(longLines("poll error", n), "\n"))
				m, _ = m.Update(MsgTelegramStatusUpdated{Status: st})
				return m
			},
			vp: Model.telegramViewport, raw: func(m Model) viewport.Model { return m.telegram.vp },
		},
		{
			name: "KanbanDetail", keys: nil, // reached by openKanbanDetailGolden, which needs the board first
			grow: func(t *testing.T, m tea.Model) tea.Model {
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
			vp: Model.kanbanDetailViewport, raw: func(m Model) viewport.Model { return m.kanban.detailVp },
		},
		{
			name: "TemplatesList", keys: []string{"e"},
			grow: sweepPersonas,
			vp:   Model.templatesListViewport,
			raw:  func(m Model) viewport.Model { return m.tmpl.listVp },
		},
	}
}

// growSessions is the fixture behind both History-sessions and Report: real session rows, each of
// which renders several lines in one view and a table row in the other.
func growSessions(n int) func(*testing.T, tea.Model) tea.Model {
	return func(_ *testing.T, m tea.Model) tea.Model {
		return growSessionRows(m, max(4, n/3))
	}
}

// sweepPersonas is the Templates list's fixture, and it is the measurement that retired this
// surface's exemption. templates.List returns the seven fixed session templates PLUS every `*.md`
// under <planDir>/personas — a directory the OWNER fills — so "bounded by construction" was never
// true of it. Forty personas is 47 rows against an 18-row pane at 80x24.
//
// It is 40 rather than the sweep's 500 because these are real files on disk: the fixture has to
// outgrow the pane at every size, and past that each extra file buys nothing but I/O.
func sweepPersonas(t *testing.T, m tea.Model) tea.Model {
	t.Helper()
	dir := t.TempDir()
	personas := filepath.Join(dir, "personas")
	if err := os.MkdirAll(personas, 0o755); err != nil {
		t.Fatalf("persona dir: %v", err)
	}
	for i := 0; i < 40; i++ {
		f := filepath.Join(personas, fmt.Sprintf("persona-%02d.md", i))
		if err := os.WriteFile(f, []byte("# persona "+itoa(i)+"\n"), 0o644); err != nil {
			t.Fatalf("write persona: %v", err)
		}
	}
	mm := asModel(m)
	if mm.data.Plan == nil {
		mm.data.Plan = &api.StateDto{}
	}
	mm.data.Plan.PlanDir = dir
	return mm
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
	// The Templates LIST used to be a third entry here, excused as "templates.List's fixed
	// prompt-template set — bounded by construction, never a long body". That reason was false:
	// templates.List (templates.go:30-50) returns the seven session templates PLUS every `*.md` under
	// <planDir>/personas, which the owner fills. Measured at 80x24 with twenty personas: 27 rows in an
	// 18-row pane, the tail clipped in silence, `end` moving nothing. An exemption is only worth
	// having while its REASON is true, so the surface was converted instead and is now in the sweep
	// as TemplatesList.
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
					m := tc.grow(t, newGoldenModel(size.w, size.h))
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

// kanbanDetailSubStates are the six transient states the card detail can be in, and how to get into
// each one. `t`/`c`/`p`/`h` are keys; a proposal and a split arrive as advisor MESSAGES, so they are
// injected the way the engine delivers them.
func kanbanDetailSubStates() []struct {
	name  string
	enter func(tea.Model) tea.Model
} {
	str := func(s string) *string { return &s }
	return []struct {
		name  string
		enter func(tea.Model) tea.Model
	}{
		{"ctxEditor", func(m tea.Model) tea.Model { m, _ = m.Update(keyMsg("c")); return m }},
		{"titleEditor", func(m tea.Model) tea.Model { m, _ = m.Update(keyMsg("t")); return m }},
		{"pathsEditor", func(m tea.Model) tea.Model { m, _ = m.Update(keyMsg("p")); return m }},
		{"handConfirm", func(m tea.Model) tea.Model { m, _ = m.Update(keyMsg("h")); return m }},
		{"proposal", func(m tea.Model) tea.Model {
			m, _ = m.Update(MsgTaskRefined{Result: &api.TaskRefineResultDto{Ok: true,
				Interpreter: str("advisor"), Title: str("a sharper title"),
				Context: str("and a longer note to go with it")}})
			return m
		}},
		{"split", func(m tea.Model) tea.Model {
			m, _ = m.Update(MsgTaskSplit{Result: &api.TaskSplitResultDto{Ok: true,
				Interpreter: str("advisor"), CheckpointId: str("F7.4"),
				Subtasks: []api.TaskSplitChildDto{
					{Title: "first child of the split"}, {Title: "second child of the split"}}}})
			return m
		}},
	}
}

// A6/A4, the half that shipped broken. The card detail's transient rows — an open editor, an advisor
// proposal, the hand-off confirm — render OUTSIDE the viewport and are DEDUCTED from its height.
// That is the right call (a confirm you have to scroll to is a confirm you cannot answer) and it has
// a price nobody paid: the moment a sub-state opens, rows of the card leave the window, and if the
// sub-state also eats every scroll key those rows are not clipped, they are gone.
//
// Measured before the fix, at 100x30 through the real router: pressing `c` on the golden card
// removed both the declared-paths value and the whole `✎ qa` section — including the
// "press q to override" that row exists to advertise — and sixty `down` presses and `end` brought
// back neither. This drives every sub-state at every size and asserts the card's own last line is
// still one keypress away.
func TestKanbanDetailSubStatesKeepTheCardReachable(t *testing.T) {
	for _, size := range glitchSizes {
		for _, sub := range kanbanDetailSubStates() {
			t.Run(fmt.Sprintf("%s_%dx%d", sub.name, size.w, size.h), func(t *testing.T) {
				m := growKanbanCard(t, newGoldenModel(size.w, size.h))
				m = sub.enter(m)
				if !asModel(m).kanbanDetailIsOpenInASubState() {
					t.Fatalf("%s: the sub-state did not open, so this proves nothing", sub.name)
				}
				vp := asModel(m).kanbanDetailViewport()
				if vp.TotalLineCount() <= vp.Height() {
					t.Fatalf("%s: the card is %d lines in a %d-row pane — it does not scroll",
						sub.name, vp.TotalLineCount(), vp.Height())
				}

				// `end` twice, because in the ctx editor the first one belongs to the CARET (it moves
				// to the end of the current line) and only a key the editor cannot use falls through.
				// Everywhere else the second press is a no-op, so one assertion covers all six.
				m, _ = m.Update(keyMsg("end"))
				m, _ = m.Update(keyMsg("end"))

				vp = asModel(m).kanbanDetailViewport()
				if !vp.AtBottom() {
					t.Errorf("%s: `end` left the card at %d%% — the tail the trailer pushed off the "+
						"pane cannot be reached at all", sub.name, int(vp.ScrollPercent()*100))
				}
				last := lastReadableLine(stripANSI(vp.GetContent()))
				if last == "" {
					t.Fatalf("%s: no readable last line in the card body", sub.name)
				}
				if frame := stripANSI(asModel(m).View().Content); !strings.Contains(frame, last) {
					t.Errorf("%s at %dx%d: the card's last line is still off-screen.\nwant: %q\n%s",
						sub.name, size.w, size.h, last, frame)
				}
			})
		}
	}
}

// The two rows the regression was reported on, by name. The declared-paths line is the only place
// the Face says a card HAS declared paths, and the qa line is the only place it names the key that
// changes them — losing either to an editor's blank padding is losing the affordance, not a row.
//
// Two claims, because "fits" and "reaches" are different promises and only one of them is always
// keepable: where the card FITS beside the editor it is all on screen (the golden's own 110x34, and
// the frame kanban_detail_ctx_edit pins), and where it does not (100x30 — a 21-line card against an
// 18-row remainder) one keypress brings the tail back. What may never happen again is the third
// case: gone, with no key that returns it.
func TestKanbanCtxEditorKeepsTheCardsOwnRows(t *testing.T) {
	want := []string{
		"src/Conductor/Core/Gating/GateCache.cs", // the declared-paths VALUE, not just its header
		"✎ qa",
		"press q to override",
		"✎ extra context", // …and the editor itself is on screen, which is what it is for
	}

	t.Run("fits_110x34", func(t *testing.T) {
		m := openKanbanDetailGolden(newGoldenModel(110, 34))
		m, _ = m.Update(keyMsg("c"))
		frame := stripANSI(asModel(m).View().Content)
		for _, w := range want {
			if !strings.Contains(frame, w) {
				t.Errorf("opening the context editor lost %q from a frame with room for it:\n%s", w, frame)
			}
		}
	})

	t.Run("scrolls_100x30", func(t *testing.T) {
		m := openKanbanDetailGolden(newGoldenModel(100, 30))
		m, _ = m.Update(keyMsg("c"))
		// `end` twice: the first belongs to the caret (end of the current line), the second is a key
		// the editor cannot use and falls through to the card.
		m, _ = m.Update(keyMsg("end"))
		m, _ = m.Update(keyMsg("end"))
		frame := stripANSI(asModel(m).View().Content)
		for _, w := range []string{"✎ qa", "press q to override", "✎ extra context"} {
			if !strings.Contains(frame, w) {
				t.Errorf("with the editor open at 100x30, `end` does not bring %q back:\n%s", w, frame)
			}
		}
	})
}

// W4.3's split proposal had NO renderer at all until KS2.7: `renderKanbanSplit` was written, tested
// by nothing, and called by nobody (`git grep renderKanbanSplit e840d56^` finds only its own
// definition), so `s` asked the advisor for a breakdown and the answer arrived invisibly — the
// bottom bar said "enter apply · esc discard" over a card that showed no proposal to apply. KS2.7's
// trailer switch gave it the arm it was missing; this is the test that was missing with it.
func TestKanbanSplitProposalIsActuallyRendered(t *testing.T) {
	str := func(s string) *string { return &s }
	m := openKanbanDetailGolden(newGoldenModel(120, 40))
	m, _ = m.Update(MsgTaskSplit{Result: &api.TaskSplitResultDto{Ok: true,
		Interpreter: str("advisor"), CheckpointId: str("F7.4"),
		Subtasks: []api.TaskSplitChildDto{
			{Title: "cache the gate result", Context: str("keyed by name, tier and sha")},
			{Title: "expire it on a source change"}}}})
	body, help := asModel(m).paneView()
	body = stripANSI(body)
	for _, want := range []string{
		"split proposed by advisor",
		"cache the gate result",
		"keyed by name, tier and sha",
		"expire it on a source change",
		"nothing is added until you confirm",
	} {
		if !strings.Contains(body, want) {
			t.Errorf("the split proposal does not render %q:\n%s", want, body)
		}
	}
	if !strings.Contains(stripANSI(help), "enter apply") {
		t.Errorf("the bottom bar offers a confirm for a proposal it must therefore show; help = %q", help)
	}
}

// growKanbanCard opens the golden card and makes it comfortably longer than any pane, so a sub-state
// that trims the body has something to trim.
func growKanbanCard(t *testing.T, m tea.Model) tea.Model {
	t.Helper()
	m = openKanbanDetailGolden(m)
	blocks := *asModel(m).kanban.blocks
	blocks.Blocks = append([]api.PromptBlockDto{}, blocks.Blocks...)
	for i, text := range longLines("block", 12) {
		blocks.Blocks = append(blocks.Blocks, api.PromptBlockDto{
			Kind: fmt.Sprintf("extra%02d", i), Label: text,
			Content: strings.Join(longLines("card note", 4), "\n")})
	}
	m, _ = m.Update(MsgPromptBlocks{Blocks: &blocks})
	return m
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
