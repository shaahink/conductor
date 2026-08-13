package tui

// KS2.8's suite: the reader — one full-screen overlay that opens any truncated cell and shows it
// whole. Everything here drives the REAL router (Update(keyMsg(...)) / press), never a pane handler
// directly: STYLE.md records twice that calling pane handlers directly is how two regression tests
// came to pass on frames that could not exhibit their bug.

import (
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

// squash removes all whitespace, so text that soft-wrapped across rows can be found again in a
// frame: the wrap replaces spaces with row breaks and pads rows, and both are whitespace.
func squash(s string) string {
	return strings.Join(strings.Fields(s), "")
}

// goldenReaderNote is the plan's literal figure: a 300+ character SINGLE-LINE card note, built from
// short words so the soft wrap has boundaries to break at.
var goldenReaderNote = func() string {
	var sb strings.Builder
	for i := 1; sb.Len() < 300; i++ {
		if sb.Len() > 0 {
			sb.WriteString(" ")
		}
		sb.WriteString(fmt.Sprintf("note-segment-%03d", i))
	}
	return sb.String()
}()

// readerOverKanbanNote opens the golden card with `note` as its extra-context block and presses the
// reader key — the fixture behind the 300-char acceptance figure and its golden.
func readerOverKanbanNote(t *testing.T, w, h int, note string) Model {
	t.Helper()
	m := openKanbanDetailGolden(newGoldenModel(w, h))
	blocks := *asModel(m).kanban.blocks
	blocks.Blocks = append([]api.PromptBlockDto{}, blocks.Blocks...)
	for i := range blocks.Blocks {
		if blocks.Blocks[i].Kind == "taskContext" {
			blocks.Blocks[i].Content = note
		}
	}
	m2, _ := m.Update(MsgPromptBlocks{Blocks: &blocks})
	got := press(asModel(m2), readerOpenKey)
	if !got.reader.open {
		t.Fatal("z did not open the reader over the card")
	}
	return got
}

// readerOverSessionResult builds a History sessions view around one session and opens its reader.
func readerOverSessionResult(t *testing.T, w, h int, gate, result string) Model {
	t.Helper()
	m := newGoldenModel(w, h)
	rows := []api.SessionRowDto{{
		Number: 42, StageId: "F7", Kind: "Deliver", Outcome: strPtr("completed"),
		Attempt: 1, CommitCount: 0, StartedUtc: "2026-07-15T09:00:00Z",
		EndedUtc:    strPtr("2026-07-15T09:30:00Z"),
		GateSummary: strPtr(gate), ResultSummary: strPtr(result)}}
	m2, _ := m.Update(MsgSessionsUpdated{Sessions: &api.SessionsDto{Sessions: rows}})
	m3 := press(asModel(m2), "s")
	got := press(m3, readerOpenKey)
	if !got.reader.open {
		t.Fatal("z did not open the reader over the session")
	}
	return got
}

// --- open / esc round trip, one subtest per named surface -------------------------------------

type readerSurfaceCase struct {
	name string
	// open builds the surface with a cell whose tail the pane CANNOT show, reader not yet open.
	open func(t *testing.T) Model
	// hidden is text of that cell the truncated row could not show.
	hidden string
	// same asserts the sub-state survived the round trip untouched.
	same func(t *testing.T, before, after Model)
}

func readerSurfaces() []readerSurfaceCase {
	longTail := func(marker string) string {
		return strings.Repeat("prose word ", 40) + marker
	}
	return []readerSurfaceCase{
		{
			name: "KanbanDetailContextNote",
			open: func(t *testing.T) Model {
				t.Helper()
				m := openKanbanDetailGolden(newGoldenModel(110, 34))
				blocks := *asModel(m).kanban.blocks
				blocks.Blocks = append([]api.PromptBlockDto{}, blocks.Blocks...)
				for i := range blocks.Blocks {
					if blocks.Blocks[i].Kind == "taskContext" {
						// Six lines against renderKanbanBlock's four-row cap: lines five and six are
						// not clipped, they are UNREACHABLE — no scroll position of the pane shows
						// them, which is the defect the reader exists for.
						blocks.Blocks[i].Content = "ctx line 1\nctx line 2\nctx line 3\nctx line 4\n" +
							"ctx line 5\nTHE-SIXTH-CONTEXT-LINE only the reader can show"
					}
				}
				m2, _ := m.Update(MsgPromptBlocks{Blocks: &blocks})
				return asModel(m2)
			},
			hidden: "THE-SIXTH-CONTEXT-LINE",
			same: func(t *testing.T, before, after Model) {
				t.Helper()
				if !after.kanban.detail {
					t.Error("esc closed the card detail instead of just the reader")
				}
				if after.kanban.blocks == nil || after.kanban.blocks.TaskId != before.kanban.blocks.TaskId {
					t.Error("the card under the reader changed across the round trip")
				}
				if after.tab != TabKanban {
					t.Errorf("esc landed on tab %v, want Kanban", tabNames[after.tab])
				}
			},
		},
		{
			name: "HistorySessionResultAndGates",
			open: func(t *testing.T) Model {
				t.Helper()
				m := newGoldenModel(110, 34)
				rows := []api.SessionRowDto{
					{Number: 12, StageId: "F7", Kind: "Deliver", Attempt: 1,
						StartedUtc: "2026-07-15T10:00:00Z"},
					{Number: 11, StageId: "F7", Kind: "Deliver", Outcome: strPtr("needsRetry"), Attempt: 1,
						StartedUtc: "2026-07-15T09:12:30Z", EndedUtc: strPtr("2026-07-15T09:58:04Z"),
						GateSummary:   strPtr("build ✓ test ✗ — " + longTail("THE-GATE-TAIL-MARKER")),
						ResultSummary: strPtr("Wired the **caching layer** but `test` is still red.")},
					{Number: 8, StageId: "F6", Kind: "Deliver", Outcome: strPtr("completed"), Attempt: 1,
						StartedUtc: "2026-07-15T08:30:00Z", EndedUtc: strPtr("2026-07-15T09:11:12Z")},
				}
				m2, _ := m.Update(MsgSessionsUpdated{Sessions: &api.SessionsDto{Sessions: rows}})
				m3 := press(asModel(m2), "s")
				return press(m3, "down") // select #11, the one with the long gate summary
			},
			hidden: "THE-GATE-TAIL-MARKER",
			same: func(t *testing.T, before, after Model) {
				t.Helper()
				if after.history.sessionSelected != before.history.sessionSelected {
					t.Errorf("session selection moved %d → %d across the round trip",
						before.history.sessionSelected, after.history.sessionSelected)
				}
				if after.history.view != historySessions || after.tab != TabHistory {
					t.Error("esc left the sessions view instead of just the reader")
				}
			},
		},
		{
			name: "HistorySpineDescription",
			open: func(t *testing.T) Model {
				t.Helper()
				m := press(asModel(newGoldenModel(110, 34)), "t")
				entries := fixedTimeline()
				entries[1].Description = "session #11 started — " + longTail("THE-SPINE-TAIL-MARKER")
				m2, _ := m.Update(MsgTimelineUpdated{Timeline: &api.TimelineDto{Entries: entries}})
				return press(asModel(m2), "down") // select the long entry
			},
			hidden: "THE-SPINE-TAIL-MARKER",
			same: func(t *testing.T, before, after Model) {
				t.Helper()
				if after.history.selected != before.history.selected {
					t.Errorf("spine selection moved %d → %d across the round trip",
						before.history.selected, after.history.selected)
				}
				if after.history.view != historyTimeline || after.tab != TabHistory {
					t.Error("esc left the spine view instead of just the reader")
				}
			},
		},
		{
			name: "TelegramLastError",
			open: func(t *testing.T) Model {
				t.Helper()
				m := press(asModel(newGoldenModel(110, 34)), "g")
				st := fixedTelegramStatus()
				st.LastError = strPtr("poll failed: " + longTail("THE-POLL-TAIL-MARKER"))
				m2, _ := m.Update(MsgTelegramStatusUpdated{Status: st})
				m3 := press(asModel(m2), "down")
				return press(m3, "down") // land on field 2 — the selection the round trip must keep
			},
			hidden: "THE-POLL-TAIL-MARKER",
			same: func(t *testing.T, before, after Model) {
				t.Helper()
				if after.telegram.fieldIdx != before.telegram.fieldIdx {
					t.Errorf("telegram field selection moved %d → %d across the round trip",
						before.telegram.fieldIdx, after.telegram.fieldIdx)
				}
				if after.telegram.editing || after.tab != TabTelegram {
					t.Error("esc changed the telegram sub-state instead of just closing the reader")
				}
			},
		},
		{
			name: "KnowledgeBugDetail",
			open: func(t *testing.T) Model {
				t.Helper()
				m := press(asModel(newGoldenModel(110, 34)), "k")
				bugs := fixedBugs()
				bugs.Bugs[0].Detail = strPtr("first line of the bug detail\n" + longTail("THE-BUG-DETAIL-MARKER"))
				m2, _ := m.Update(MsgKnowledgeUpdated{Ledger: fixedLedger(), Bugs: bugs, Evidence: fixedEvidence()})
				return asModel(m2)
			},
			hidden: "THE-BUG-DETAIL-MARKER",
			same: func(t *testing.T, before, after Model) {
				t.Helper()
				if after.knowledge.mode != knowledgeBrowse || after.tab != TabKnowledge {
					t.Error("esc changed the knowledge sub-state instead of just closing the reader")
				}
			},
		},
		{
			name: "ProcessLastOutput",
			open: func(t *testing.T) Model {
				t.Helper()
				m := press(asModel(newGoldenModel(110, 34)), "o")
				procs := []api.ProcessDto{
					{Pid: 4512, Purpose: "session", Alive: true, StartedUtc: "2026-07-15T10:00:00Z",
						ExitedUtc: strPtr("2026-07-15T10:04:32Z")},
					{Pid: 8723, Purpose: "gate:test", Alive: false, StartedUtc: "2026-07-15T10:01:00Z",
						ExitedUtc:      strPtr("2026-07-15T10:01:19Z"),
						LastOutputLine: strPtr("gate output: " + longTail("THE-PROC-TAIL-MARKER"))},
				}
				m2, _ := m.Update(MsgProcessesUpdated{Procs: &api.ProcessesDto{Processes: procs}})
				return press(asModel(m2), "down") // select pid 8723
			},
			hidden: "THE-PROC-TAIL-MARKER",
			same: func(t *testing.T, before, after Model) {
				t.Helper()
				if after.processes.selected != before.processes.selected {
					t.Errorf("process selection moved %d → %d across the round trip",
						before.processes.selected, after.processes.selected)
				}
				if after.processes.killing || after.tab != TabProcesses {
					t.Error("esc changed the processes sub-state instead of just closing the reader")
				}
			},
		},
	}
}

// B9 + B6: every named cell opens, shows text the truncated row could not show, and `esc` returns
// to EXACTLY the surface and sub-state it was opened from. `q` inside the reader must neither quit
// the app nor close the overlay — that is the precedence clause (peeled before the esc ladder and
// before `q`), measured rather than described.
func TestReaderOpensAndEscReturnsToTheSameSubState(t *testing.T) {
	for _, tc := range readerSurfaces() {
		t.Run(tc.name, func(t *testing.T) {
			before := tc.open(t)
			if before.reader.open {
				t.Fatal("fixture opened the reader early — the round trip proves nothing")
			}
			if strings.Contains(squash(stripANSI(before.View().Content)), squash(tc.hidden)) {
				t.Fatalf("%q is already visible without the reader — the fixture does not truncate, "+
					"so this proves nothing", tc.hidden)
			}

			m := press(before, readerOpenKey)
			if !m.reader.open {
				t.Fatalf("%q did not open the reader", readerOpenKey)
			}
			frame := squash(stripANSI(m.View().Content))
			if !strings.Contains(frame, squash(tc.hidden)) {
				// The doc can be longer than the overlay: the tail is one `end` away, and being ON
				// screen after `end` is the promise.
				m = press(m, "end")
				frame = squash(stripANSI(m.View().Content))
			}
			if !strings.Contains(frame, squash(tc.hidden)) {
				t.Errorf("the reader does not show %q — the text the pane hid is still hidden:\n%s",
					tc.hidden, stripANSI(m.View().Content))
			}

			// `q` is a dead key in here, not a quit: the reader is peeled before handleKey's quit arm.
			tm, cmd := m.Update(keyMsg("q"))
			m = asModel(tm)
			if cmd != nil {
				t.Error("q inside the reader produced a command — it must not reach the quit arm")
			}
			if !m.reader.open {
				t.Error("q closed the reader — it binds the pane-scroll set and esc, nothing else")
			}

			m = press(m, "esc")
			if m.reader.open {
				t.Fatal("esc did not close the reader")
			}
			tc.same(t, before, m)
		})
	}
}

// B8: the plan's first literal acceptance figure. A 2,000-line report body opened in the reader at
// 80x24 is readable to its LAST line — after `end`, and separately after `G`.
func TestReaderReachesTheLastLineOfA2000LineReport(t *testing.T) {
	var sb strings.Builder
	sb.WriteString("The verifier's full report:\n")
	for i := 1; i <= 2000; i++ {
		sb.WriteString(fmt.Sprintf("\n- report line %04d of the long body", i))
	}
	sb.WriteString("\n- END-OF-REPORT line 2000 reached")
	for _, jump := range []string{"end", "G"} {
		t.Run(jump, func(t *testing.T) {
			m := readerOverSessionResult(t, 80, 24, "build ✓", sb.String())
			vp := m.readerViewport()
			if vp.TotalLineCount() <= vp.Height() {
				t.Fatalf("the report is %d rows in a %d-row reader — it does not scroll, so this "+
					"proves nothing", vp.TotalLineCount(), vp.Height())
			}
			m = press(m, jump)
			if !m.readerViewport().AtBottom() {
				t.Fatalf("%q left the reader at %d%% — the end of the report is unreachable",
					jump, int(m.readerViewport().ScrollPercent()*100))
			}
			frame := squash(stripANSI(m.View().Content))
			if !strings.Contains(frame, squash("END-OF-REPORT line 2000 reached")) {
				t.Errorf("after %q the report's last line is still off-screen:\n%s",
					jump, stripANSI(m.View().Content))
			}
		})
	}
}

// B2's second literal figure: a 300-character SINGLE-LINE card note, at 80x24, occupies multiple
// rows with every character present — soft-wrapped, never clipped, no ellipsis anywhere in the body.
func TestReaderShowsAWhole300CharKanbanNote(t *testing.T) {
	m := readerOverKanbanNote(t, 80, 24, goldenReaderNote)

	// Every character present: walk the document top to bottom accumulating what is actually ON
	// SCREEN, so this measures the render, not the source string.
	m = press(m, "home")
	seen := squash(stripANSI(m.readerViewport().View()))
	for i := 0; i < 40 && !m.readerViewport().AtBottom(); i++ {
		m = press(m, "pgdown")
		seen += squash(stripANSI(m.readerViewport().View()))
	}
	if !strings.Contains(seen, squash(goldenReaderNote)) {
		t.Errorf("not every character of the 300-char note is rendered:\nwant (squashed) %q\nin %q",
			squash(goldenReaderNote), seen)
	}

	// …across multiple rows: the note is wider than the overlay, so the wrap must have broken it.
	m = press(m, "home")
	rows := 0
	for _, row := range strings.Split(stripANSI(m.readerViewport().View()), "\n") {
		if strings.Contains(row, "note-segment-") {
			rows++
		}
	}
	if rows < 2 {
		t.Errorf("a 300-char note in a ~72-col reader occupies %d row(s) — it is not being wrapped", rows)
	}
}

// B2's width half: at the 80x24 floor no rendered reader row exceeds the overlay's inner width and
// no row ends in an ellipsis — the wrap is sized against the OVERLAY, not m.width.
func TestReaderSoftWrapsWithinTheOverlayWidth(t *testing.T) {
	m := readerOverKanbanNote(t, 80, 24, goldenReaderNote)
	inner := m.readerInnerWidth()
	for !m.readerViewport().AtBottom() {
		for i, row := range strings.Split(stripANSI(m.readerViewport().View()), "\n") {
			trimmed := strings.TrimRight(row, " ")
			if n := len([]rune(trimmed)); n > inner {
				t.Errorf("reader row %d is %d cols against an inner width of %d: %q", i, n, inner, trimmed)
			}
			if strings.HasSuffix(trimmed, "…") {
				t.Errorf("reader row %d ends in an ellipsis — the reader clips nothing: %q", i, trimmed)
			}
		}
		m = press(m, "pgdown")
	}
}

// B3: the reader binds the adr/0006 pane-scroll set and ONLY that set. A tab mnemonic, a folded
// mnemonic, a global or a surface key pressed inside it moves nothing, opens nothing, posts nothing
// — and the scroll keys all actually move.
func TestReaderBindsOnlyThePaneScrollSet(t *testing.T) {
	fresh := func(t *testing.T) Model {
		return readerOverKanbanNote(t, 110, 34, goldenReaderNote)
	}

	// Everything that is NOT the scroll set is a no-op: every tab mnemonic, both folded mnemonics,
	// the globals, and the Kanban detail's own semantic keys (the surface under this reader).
	dead := []string{"h", "a", "s", "o", "e", "p", "r", "k", "g", "b", "c", "t", "w", "q",
		":", "i", "?", "/", "\\", "1", "9", "0", "n", "x", "v", "enter", "left", "right"}
	for _, k := range dead {
		m := fresh(t)
		before := m.reader.vp.YOffset()
		tm, cmd := m.Update(keyMsg(k))
		got := asModel(tm)
		if cmd != nil {
			t.Errorf("%q inside the reader produced a command — the key leaked to a surface", k)
		}
		if !got.reader.open || got.tab != m.tab || got.cmd != CmdNone {
			t.Errorf("%q inside the reader changed the surface (open=%v tab=%v cmd=%v)",
				k, got.reader.open, tabNames[got.tab], got.cmd)
		}
		if got.reader.vp.YOffset() != before {
			t.Errorf("%q scrolled the reader — it is not in the pane-scroll set", k)
		}
		if got.kanban.editingTitle || got.kanban.editingCtx || got.kanban.editingPaths ||
			got.kanban.handConfirm || !got.kanban.detail {
			t.Errorf("%q reached the Kanban detail under the reader", k)
		}
	}

	// …and every key the set names moves the pane, from whichever end can prove it.
	for _, k := range []string{"down", "j", "up", "d", "u", "pgdown", "pgup", "end", "G", "home"} {
		m := fresh(t)
		if strings.Contains("up u pgup home", k) {
			m = press(m, "end")
		}
		before := m.reader.vp.YOffset()
		got := press(m, k)
		if got.reader.vp.YOffset() == before {
			t.Errorf("reader: %q moved nothing", k)
		}
	}
}

// B5: markdown is rendered by the memoised, theme-projected renderer — once per (content, width,
// theme), never per frame. A storm of keypresses and frames at a fixed size must not move the
// glamour counter at all after the first render.
func TestReaderRendersMarkdownOncePerContentNotPerFrame(t *testing.T) {
	var sb strings.Builder
	sb.WriteString("# The result\n")
	for i := 0; i < 120; i++ {
		sb.WriteString(fmt.Sprintf("\n- finding %03d of the session", i))
	}
	m := readerOverSessionResult(t, 110, 34, "build ✓", sb.String())
	if !m.reader.isMarkdown {
		t.Fatal("the session result must open as markdown, or this measures the wrong path")
	}
	_ = m.View()
	base := markdownRenders()
	for i := 0; i < 40; i++ {
		m = press(m, []string{"down", "up"}[i%2])
		_ = m.View()
	}
	if got := markdownRenders(); got != base {
		t.Errorf("a 40-keypress storm re-ran glamour %d time(s) — the reader is rendering markdown "+
			"per frame, the exact defect adr/0006 §6 closed", got-base)
	}
}

// B7: at each of the three sweep sizes the frame with the reader open is ≤ the window height, no
// row is wider than the window, and the last visible row is still the bottom bar.
func TestReaderFitsEveryWindow(t *testing.T) {
	for _, size := range glitchSizes {
		t.Run(fmt.Sprintf("%dx%d", size.w, size.h), func(t *testing.T) {
			m := readerOverKanbanNote(t, size.w, size.h, goldenReaderNote)
			frame := stripANSI(m.View().Content)
			rows := strings.Split(strings.TrimRight(frame, "\n"), "\n")
			if len(rows) > size.h {
				t.Errorf("reader frame is %d rows for a %d-row window", len(rows), size.h)
			}
			for n, row := range rows {
				if got := len([]rune(strings.TrimRight(row, " "))); got > size.w {
					t.Errorf("row %d is %d cols wide in a %d-col window: %q", n, got, size.w, row)
				}
			}
			if last := rows[len(rows)-1]; !strings.Contains(last, "quit") && !strings.Contains(last, "cmd") {
				t.Errorf("last visible row is not the bottom bar: %q", last)
			}
		})
	}
}

// --- B10: the enumerated-sites ratchet --------------------------------------------------------

// truncationSites is every function in internal/tui that clips text (a call to `truncate` or
// `evidencePath` — `truncateLines` is deleted), and for each one the answer to "how does a reader
// see the whole thing": the reader route, the visible affordance, or why the clipped string is row
// furniture rather than owner- or agent-authored prose. A new clipping function fails the test
// below until it is added HERE with its answer — that is the ratchet, and it is the enumerated-list
// shape KS2.8 asked for so a truncation site can never again be added in silence.
var truncationSites = map[string]string{
	// --- reachable through the reader (`z` on the surface) ---
	"renderKanbanBlock":    "block content capped at 4 rows; the cap row names the key and z opens the whole card (kanbanReaderDoc)",
	"kanbanDetailBody":     "declared-paths row clipped to the pane; z opens the card with each path on its own line",
	"renderKanbanProposal": "proposal title/context clipped to the trailer; the applied result lands on the card, which z reads whole",
	"renderKanbanSplit":    "proposed child titles clipped to the trailer; applied children become cards, each readable via z",
	"knowledgeLines":       "bug detail and ledger content flattened to one row each; z opens the full ledger (knowledgeReaderDoc)",
	"evidenceLines":        "evidence paths left-elided to the row; z opens the full ledger with whole paths",
	"spineLines":           "spine descriptions truncated to the row; z opens the selected entry whole (spineReaderDoc)",
	"renderSessionCommits": "commit subjects clipped to the pane; z opens the session document with each subject whole (sessionReaderDoc)",
	"processesLines":       "purpose and last-output clipped to the row; z opens the selected process whole (processReaderDoc)",
	"renderReaderOverlay":  "the reader's own TITLE is clipped to make room for the percent — the body it shows is never clipped",

	// --- the board: every clipped card cell is one `enter` from the detail, where z reads it whole ---
	"kanbanWindow":            "per-column clip with its own ↕ note (the sweep's named exemption); cards open with enter",
	"renderKanbanCard":        "card title/meta cells on the board — enter opens the card, z inside reads it whole",
	"renderKanbanColumn":      "column empty-state and shelf rows — same route as the cards",
	"renderKanbanGroupHeader": "stage group label — an id plus a title the sidebar and Plan tab both carry in full",
	"kanbanFeedBanner":        "the tasks-poll error banner — a wire error string, repeated verbatim in the toast that announced it",

	// --- dense-row furniture: labels, ids, paths and one-word cells, not owner/agent prose ---
	"renderSessionDigest":  "digest label column — engine-computed counts and file names; the full session is z-readable in History",
	"renderReportSessions": "Report's per-row digest line — a summary by construction; the full session is one `s` away and z-readable there",
	"renderAgentStrip":     "checkpoint title / attention reason in a one-row strip — a headline; the transcript below is the text",
	"currentTaskSegment":   "live task name in the strip — the Kanban card is the readable surface for it",
	"agentRawViewport":     "raw stdout rows clipped to the pane to keep the live tail honest; the full line is in .conductor/logs/session-NNN.jsonl, which the pane's empty state names",
	"renderHomeRun":        "Home landing rows — fixed-height by design (fitHome sheds tiers, STYLE.md); values are ids, paths and counts",
	"renderHomeGit":        "git chip and dirty summary — shas, branch names and porcelain codes, not prose",
	"renderHomeLastRun":    "last-run card rows — summary values from RUN-SUMMARY.md, whose on-disk path the card itself names",
	"homeEngineLine":       "the one-line engine status — a headline with its age, by design one row",
	"homeBuildLine":        "engine/face version stamp — version strings and short shas",
	"homeWiring":           "Home wiring internals — token presence, seq numbers and urls, not prose",
	"renderPlanStages":     "plan editor stage rows — ids, titles and field values the editor itself edits in full",
	"renderPlanGates":      "plan editor gate rows — same",
	"renderImportDiff":     "import diff rows — field-level old→new values, the editor's own surface",
	"renderRow":            "run picker rows — ids and plan names; KS2.4's pre-attach surface, by design terse",
	"renderPastRow":        "run picker past-run rows — same",
	"renderDetail":         "run picker detail — same",
	"Render":               "run picker frame (PickerModel.Render) — same pre-attach surface",
}

// truncators are the clipping helpers themselves; their DEFINITIONS are not sites.
var readerTruncators = map[string]bool{"truncate": true, "evidencePath": true}

// TestNoSurfaceAnswersLongTextWithSilentTruncation walks internal/tui's AST and fails on any
// function that clips text without an entry in truncationSites saying how the whole text is reached
// (or why it is not prose). Same walker shape as module_intent_test.go / scroll_intent_test.go:
// measure the claim, never trust the comment.
func TestNoSurfaceAnswersLongTextWithSilentTruncation(t *testing.T) {
	root := moduleRoot(t)
	dir := filepath.Join(root, "internal", "tui")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("read %s: %v", dir, err)
	}
	fset := token.NewFileSet()
	found := map[string]string{} // func name → file
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".go") || strings.HasSuffix(e.Name(), "_test.go") {
			continue
		}
		f, err := parser.ParseFile(fset, filepath.Join(dir, e.Name()), nil, 0)
		if err != nil {
			t.Fatalf("parse %s: %v", e.Name(), err)
		}
		for _, decl := range f.Decls {
			fd, ok := decl.(*ast.FuncDecl)
			if !ok || fd.Body == nil || readerTruncators[fd.Name.Name] {
				continue
			}
			clips := false
			ast.Inspect(fd.Body, func(n ast.Node) bool {
				if c, ok := n.(*ast.CallExpr); ok {
					if id, ok := c.Fun.(*ast.Ident); ok && readerTruncators[id.Name] {
						clips = true
					}
				}
				return true
			})
			if clips {
				found[fd.Name.Name] = e.Name()
			}
		}
	}
	if len(found) == 0 {
		t.Fatal("found no clipping call sites at all — the walker is broken, not the module")
	}
	for name, file := range found {
		if _, ok := truncationSites[name]; !ok {
			t.Errorf("%s (%s) clips text and is not in truncationSites — say how the reader reaches "+
				"the full text, name the visible affordance, or record why this is row furniture and "+
				"not prose", name, file)
		}
	}
	for name := range truncationSites {
		if _, ok := found[name]; !ok {
			t.Errorf("truncationSites still names %s, which no longer clips anything — a stale entry "+
				"is folklore; delete it", name)
		}
	}
}
