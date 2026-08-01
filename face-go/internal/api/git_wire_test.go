package api

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
)

// Both fixtures are REAL captures: `GET /state` off a fresh build of this working tree's engine,
// serving a scratch git rig under %TEMP%/sarban-proofs/sf33face (SF3.3, session 17). Nothing was
// renamed and nothing was added — which is the point. A hand-written fixture only proves the decoder
// agrees with whoever typed the fixture, and this repo has already paid for that once: the Face went
// five stages decoding no digest at all while the engine served one on every row.
//
//   - state_git_wire.json            — a branch that TRACKS a remote and is ahead of it, dirty tree.
//   - state_git_no_upstream_wire.json — the same repo on a branch with NO upstream, so the wire
//     genuinely omits upstream/ahead/behind rather than sending zeros.
func loadWireState(t *testing.T, name string) StateDto {
	t.Helper()
	raw, err := os.ReadFile("testdata/" + name)
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	var dto StateDto
	if err := json.Unmarshal(raw, &dto); err != nil {
		t.Fatalf("decode /state: %v", err)
	}
	return dto
}

func TestStateWireCarriesTheGitBlock(t *testing.T) {
	st := loadWireState(t, "state_git_wire.json")
	if st.Git == nil {
		t.Fatal("git block dropped on decode — the wire carries one")
	}
	g := st.Git
	if !g.IsRepo {
		t.Error("isRepo decoded false against a real git repo")
	}
	if g.Branch != "feat/rig-branch" || g.Detached {
		t.Errorf("branch: got %q detached=%v", g.Branch, g.Detached)
	}
	if !g.HasUpstream() || *g.Upstream != "origin/feat/rig-branch" {
		t.Errorf("upstream decoded as %v", g.Upstream)
	}
	// Ahead 2 is a real count off a real tracking branch: the rig pushed, then committed twice more.
	if g.Ahead == nil || *g.Ahead != 2 {
		t.Errorf("ahead: got %v, want 2", g.Ahead)
	}
	if g.Behind == nil || *g.Behind != 0 {
		t.Errorf("behind: got %v, want 0", g.Behind)
	}
	if len(g.HeadSha) != 40 || len(g.HeadShortSha) != 7 {
		t.Errorf("head sha lengths: %d / %d", len(g.HeadSha), len(g.HeadShortSha))
	}
	if !strings.HasPrefix(g.HeadSha, g.HeadShortSha) {
		t.Errorf("short sha %q is not a prefix of %q", g.HeadShortSha, g.HeadSha)
	}
	if !g.Dirty || g.DirtyCount == 0 || g.DirtySummary == "" {
		t.Errorf("dirty state decoded empty: %v %d %q", g.Dirty, g.DirtyCount, g.DirtySummary)
	}
	if len(g.RecentCommits) == 0 {
		t.Fatal("recentCommits decoded empty")
	}
	if g.RecentCommits[0].Subject != g.HeadSubject {
		t.Errorf("first recent commit %q is not HEAD %q", g.RecentCommits[0].Subject, g.HeadSubject)
	}
	for _, c := range g.RecentCommits {
		if c.Sha == "" || c.Subject == "" {
			t.Errorf("commit decoded blank: %+v", c)
		}
	}
}

// Nil, not zero. This is the whole reason Upstream/Ahead/Behind are pointers: the engine OMITS them
// for a branch with no upstream, and `int` would decode the absence as 0 — turning "you have never
// pushed this branch" into a confident "you are level with your remote".
func TestStateWireOmitsAheadBehindWithoutAnUpstream(t *testing.T) {
	g := loadWireState(t, "state_git_no_upstream_wire.json").Git
	if g == nil {
		t.Fatal("git block dropped on decode")
	}
	if !g.IsRepo || g.Branch != "detached-experiment" {
		t.Fatalf("wrong fixture: isRepo=%v branch=%q", g.IsRepo, g.Branch)
	}
	if g.Upstream != nil || g.Ahead != nil || g.Behind != nil {
		t.Errorf("absent fields decoded non-nil: upstream=%v ahead=%v behind=%v",
			g.Upstream, g.Ahead, g.Behind)
	}
	if g.HasUpstream() {
		t.Error("HasUpstream true for a branch that tracks nothing")
	}
	// The rest of the block is still fully populated — no upstream is not no git.
	if g.HeadShortSha == "" || len(g.RecentCommits) == 0 {
		t.Error("the non-upstream half of the block decoded empty")
	}
}

// FU-OWNER-10 rides the same payload. The values are the engine's own stamp, so the test asserts
// their SHAPE rather than a literal that would rot on the next build.
func TestStateWireNamesTheBuildItIsServedBy(t *testing.T) {
	st := loadWireState(t, "state_git_wire.json")
	if st.EngineVersion == "" {
		t.Error("engineVersion decoded empty")
	}
	if st.EngineCommit == "" {
		t.Error("engineCommit decoded empty")
	}
	// The capture was taken against a dirty working tree, and the engine says so rather than quoting
	// a sha that does not describe what is running — the exact failure FU-OWNER-10 was filed for.
	if !strings.HasSuffix(st.EngineCommit, ".dirty") {
		t.Errorf("engineCommit %q lost its dirty marker", st.EngineCommit)
	}
	if st.FaceBuild == "" {
		t.Error("faceBuild decoded empty — an absent field and 'no Face built here' must not read the same")
	}
}

// A payload from an engine that predates SF3.3 has no `git` key at all. Nil is the honest decode:
// the Face must be able to tell "this engine cannot tell me" from "this is not a git repo", because
// the first says nothing about the repo and the second says something definite about it.
func TestStateWithoutAGitBlockDecodesNil(t *testing.T) {
	var st StateDto
	if err := json.Unmarshal([]byte(`{"planName":"old","status":"Running"}`), &st); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if st.Git != nil {
		t.Errorf("older engine invented a git block: %+v", st.Git)
	}
	if st.Git.HasUpstream() {
		t.Error("HasUpstream on a nil block must be false, not a panic")
	}
	if st.EngineVersion != "" || st.FaceBuild != "" {
		t.Error("older engine invented a build identity")
	}
}

// isRepo:false with an empty block is the OTHER absence: the engine looked and the workspace is not
// a git repo. It arrives present-but-empty precisely so it cannot be confused with the nil above.
func TestStateWithANonRepoWorkspaceDecodesPresentButEmpty(t *testing.T) {
	var st StateDto
	raw := `{"planName":"p","git":{"isRepo":false,"branch":"","detached":false,"headSha":"",` +
		`"headShortSha":"","headSubject":"","dirty":false,"dirtyCount":0,"dirtySummary":"","recentCommits":[]}}`
	if err := json.Unmarshal([]byte(raw), &st); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if st.Git == nil {
		t.Fatal("present-but-empty git block decoded as nil — that is the older-engine case")
	}
	if st.Git.IsRepo {
		t.Error("isRepo decoded true")
	}
}

// The session row's commits are the SUBJECTS, not a second copy of the count.
func TestSessionRowDecodesCommitSubjects(t *testing.T) {
	var row SessionRowDto
	raw := `{"number":11,"commitCount":2,"commits":["4b81d33 test(gates): joins attempts",` +
		`"c07e5a9 refactor(store): one place that opens run.db"]}`
	if err := json.Unmarshal([]byte(raw), &row); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if len(row.Commits) != row.CommitCount {
		t.Errorf("got %d subjects for commitCount %d", len(row.Commits), row.CommitCount)
	}
	if !strings.HasPrefix(row.Commits[0], "4b81d33 ") {
		t.Errorf("commit line lost its sha: %q", row.Commits[0])
	}
	// A session that predates the field carries the count and no subjects — and must not decode as
	// "landed nothing", which is what a reader would conclude from an empty list plus a zeroed count.
	var old SessionRowDto
	if err := json.Unmarshal([]byte(`{"number":8,"commitCount":1}`), &old); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if old.Commits != nil || old.CommitCount != 1 {
		t.Errorf("older row: commits=%v commitCount=%d", old.Commits, old.CommitCount)
	}
}
