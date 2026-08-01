package tui

import (
	"strings"
	"testing"
	"unicode/utf8"

	tea "charm.land/bubbletea/v2"

	"conductor-face-go/internal/api"
)

// SF3.3 — the commits a session landed, in the session's own detail.
//
// The list row above the detail has carried a commit COUNT since the first version of this pane, and
// a count is the one fact about a session's commits nobody can check: "2 commits" is equally true of
// the session that fixed the bug and the session that rewrote a comment twice. The subjects are what
// make the number readable.

// The three shapes the wire actually serves, each rendering a different thing on purpose.
func TestRenderSessionCommitsHasThreeShapes(t *testing.T) {
	t.Run("a session that landed nothing renders nothing", func(t *testing.T) {
		got := renderSessionCommits(api.SessionRowDto{Number: 12, CommitCount: 0}, 80)
		if len(got) != 0 {
			t.Errorf("no commits, so no block — got %q", got)
		}
	})

	// The count-without-subjects case is an ENGINE older than SF3.3, not an empty session: the
	// subjects are read from an event the engine only started writing then. Rendering nothing here
	// would silently contradict the "1 commit" on the row three lines up.
	t.Run("a count with no subjects says the subjects are missing", func(t *testing.T) {
		got := strings.Join(renderSessionCommits(api.SessionRowDto{Number: 8, CommitCount: 1}, 80), "\n")
		if !strings.Contains(got, "Commits:") {
			t.Errorf("the block is still owed to a session that landed something:\n%s", got)
		}
		if !strings.Contains(got, "1 commit") || !strings.Contains(got, "not recorded") {
			t.Errorf("the fallback must state the count AND why the subjects are absent:\n%s", got)
		}
	})

	t.Run("subjects render one per line, sha first", func(t *testing.T) {
		got := renderSessionCommits(api.SessionRowDto{Number: 11, CommitCount: 2, Commits: []string{
			"4b81d33 test(gates): the last-passing lookup joins attempts",
			"c07e5a9 refactor(store): one place that opens run.db",
		}}, 80)
		if len(got) != 3 {
			t.Fatalf("want a header and two commit lines, got %d: %q", len(got), got)
		}
		if !strings.Contains(stripANSI(got[1]), "4b81d33 test(gates): the last-passing lookup joins attempts") {
			t.Errorf("the first commit did not render whole: %q", stripANSI(got[1]))
		}
		if !strings.Contains(stripANSI(got[2]), "c07e5a9 refactor(store)") {
			t.Errorf("the second commit did not render: %q", stripANSI(got[2]))
		}
		if strings.Contains(stripANSI(strings.Join(got, "\n")), "more") {
			t.Errorf("nothing was held back, so nothing says it was:\n%s", strings.Join(got, "\n"))
		}
	})
}

// Two ways the list can be shorter than the truth — the pane's own cap, and an engine that served
// fewer subjects than it counted commits. Both have to announce themselves, and against the COUNT:
// a silently-cut list reads as a complete one, which is the whole complaint the count already had.
func TestRenderSessionCommitsNamesWhatItHeldBack(t *testing.T) {
	many := make([]string, 9)
	for i := range many {
		many[i] = "abc123" + string(rune('a'+i)) + " feat: change number " + string(rune('1'+i))
	}
	got := strings.Join(stripLines(renderSessionCommits(
		api.SessionRowDto{CommitCount: 9, Commits: many}, 80)), "\n")
	if strings.Count(got, "feat:") != historyCommitsMax {
		t.Errorf("want %d subjects shown, got:\n%s", historyCommitsMax, got)
	}
	if !strings.Contains(got, "+4 more") {
		t.Errorf("the pane's own cap must say what it cut:\n%s", got)
	}

	// The engine caps how many subjects it serves. Four subjects under a count of twenty is sixteen
	// commits the reader would otherwise never learn about.
	short := strings.Join(stripLines(renderSessionCommits(api.SessionRowDto{
		CommitCount: 20, Commits: many[:4]}, 80)), "\n")
	if !strings.Contains(short, "+16 more") {
		t.Errorf("the overflow counts against the count, not the list that arrived:\n%s", short)
	}
}

// STYLE.md: rune-slice plain text, never byte-slice. A commit subject is the most likely place in
// this pane to hold a multi-byte glyph — conventional-commit subjects in this very repo carry them.
func TestRenderSessionCommitsTruncatesByRuneNotByte(t *testing.T) {
	const subject = "9f2c1ab feat(face): the wire's git block — decoded ✓ 日本語のコミット"
	for _, w := range []int{12, 20, 34, 41, 60} {
		for _, line := range stripLines(renderSessionCommits(
			api.SessionRowDto{CommitCount: 1, Commits: []string{subject}}, w)) {
			if !utf8.ValidString(line) {
				t.Errorf("width %d sliced a rune in half: %q", w, line)
			}
			if n := len([]rune(line)); n > w {
				t.Errorf("width %d produced a %d-rune line: %q", w, n, line)
			}
		}
	}
}

// The block's PLACE in the detail is part of the claim: what the session did (the digest), then what
// it landed, then what it said about itself. Landed-between-the-two is the order a reviewer reads in
// — the commits are the checkable half of the result summary right under them.
func TestSessionDetailPutsCommitsBetweenTheDigestAndTheResult(t *testing.T) {
	m := demoModel(t, 140, 44)
	view := asModel(mustHandle(asModel(m).handleKey("s"))) // History · sessions
	// Session 11 is the demo row carrying both subjects and a result summary.
	next, _ := view.Update(specialKey(tea.KeyDown))
	body := stripANSI(mustBody(asModel(next)))
	t.Logf("--demo History · sessions, session 11 selected:\n%s", indentBlock(body))

	iDigest := strings.Index(body, "Did:")
	iCommits := strings.Index(body, "Commits:")
	iResult := strings.Index(body, "Result:")
	if iDigest < 0 || iCommits < 0 || iResult < 0 {
		t.Fatalf("detail is missing one of digest/commits/result (%d/%d/%d):\n%s", iDigest, iCommits, iResult, body)
	}
	if !(iDigest < iCommits && iCommits < iResult) {
		t.Errorf("want digest → commits → result, got offsets %d/%d/%d:\n%s", iDigest, iCommits, iResult, body)
	}
	for _, want := range []string{
		"4b81d33 test(gates): the last-passing lookup joins attempts",
		"c07e5a9 refactor(store): one place that opens run.db",
	} {
		if !strings.Contains(body, want) {
			t.Errorf("the detail does not show %q:\n%s", want, body)
		}
	}

	// Two rows down is session 8: a commit count with no subjects, from before the engine read them.
	next2, _ := next.Update(specialKey(tea.KeyDown))
	old := stripANSI(mustBody(asModel(next2)))
	t.Logf("--demo History · sessions, session 8 (count, no subjects):\n%s", indentBlock(old))
	if !strings.Contains(old, "1 commit") || !strings.Contains(old, "not recorded") {
		t.Errorf("a pre-SF3.3 session must fall back to the count it has always had:\n%s", old)
	}
}

func stripLines(lines []string) []string {
	out := make([]string, 0, len(lines))
	for _, l := range lines {
		out = append(out, stripANSI(l))
	}
	return out
}
