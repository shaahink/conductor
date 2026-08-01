package tui

import (
	"strings"
	"testing"

	"conductor-face-go/internal/api"
)

// Home's SF3.3 Git panel and FU-OWNER-10 build line. The rows are asserted through the panel that
// actually renders them, so a row that is composed correctly but never reaches the page still fails.

func homeGitRows(t *testing.T, s *api.StateDto) string {
	t.Helper()
	m := Model{}
	m.data.Plan = s
	var b strings.Builder
	for _, l := range m.renderHomeGit(100) {
		b.WriteString(stripANSI(l.text) + "\n")
	}
	return b.String()
}

func gitFixture() *api.GitDto {
	return &api.GitDto{
		IsRepo: true, Branch: "feat/gate-caching",
		Upstream: strPtr("origin/feat/gate-caching"), Ahead: intPtr(3), Behind: intPtr(1),
		HeadShortSha: "9f2c1ab", HeadSubject: "feat(gates): key the cache by (name, tier, sha)",
		Dirty: true, DirtyCount: 4,
		DirtySummary: "M src/Core/GateCache.cs, ?? notes.md",
	}
}

// A pre-SF3.3 engine knows nothing about the repo. The panel must render no ROWS at all, so that
// fitHome drops the orphaned header and Home stays silent — rather than painting a hopeful "clean"
// over a tree it has never looked at.
func TestHomeGitIsSilentForAnEngineThatServesNoBlock(t *testing.T) {
	m := Model{}
	m.data.Plan = &api.StateDto{Repo: `C:\Code\conductor`}
	if rows := m.renderHomeGit(100); len(rows) != 1 {
		t.Errorf("pre-SF3.3 engine rendered %d Git rows, want header only", len(rows))
	}
	// And with no state at all.
	if rows := (Model{}).renderHomeGit(100); len(rows) != 1 {
		t.Errorf("nil state rendered %d Git rows, want header only", len(rows))
	}
}

// A present block with IsRepo false is a DIFFERENT fact: the engine looked and found no repo. That
// is worth a sentence, because it is the reason no commit will ever be attributed to this run.
func TestHomeGitSaysNotARepoOutLoud(t *testing.T) {
	got := homeGitRows(t, &api.StateDto{Git: &api.GitDto{IsRepo: false}})
	if !strings.Contains(got, "not a git repository") {
		t.Errorf("non-repo workspace did not say so:\n%s", got)
	}
}

func TestHomeGitRendersTheBusyRepo(t *testing.T) {
	got := homeGitRows(t, &api.StateDto{Git: gitFixture()})
	for _, want := range []string{
		"feat/gate-caching", "origin/feat/gate-caching", "3 ahead", "1 behind",
		"9f2c1ab", "key the cache", "4 changes uncommitted", "?? notes.md",
	} {
		if !strings.Contains(got, want) {
			t.Errorf("Git panel is missing %q:\n%s", want, got)
		}
	}
}

// The one lie this panel must never tell: a branch that has never been pushed is one machine
// failure away from gone, and it must not read like a branch safely mirrored on a remote.
func TestHomeGitNeverPushedIsNotInSync(t *testing.T) {
	g := gitFixture()
	g.Upstream, g.Ahead, g.Behind = nil, nil, nil
	got := homeGitRows(t, &api.StateDto{Git: g})
	if !strings.Contains(got, "never been pushed") {
		t.Errorf("unpushed branch did not say so:\n%s", got)
	}
	for _, forbidden := range []string{"in sync", "0 ahead", "0 behind"} {
		if strings.Contains(got, forbidden) {
			t.Errorf("unpushed branch rendered %q:\n%s", forbidden, got)
		}
	}
	// A branch that IS level says so explicitly, so the two never share a rendering.
	level := gitFixture()
	level.Ahead, level.Behind = intPtr(0), intPtr(0)
	if lg := homeGitRows(t, &api.StateDto{Git: level}); !strings.Contains(lg, "in sync") {
		t.Errorf("branch level with its upstream did not say so:\n%s", lg)
	}
}

func TestHomeGitCleanTreeSaysClean(t *testing.T) {
	g := gitFixture()
	g.Dirty, g.DirtyCount, g.DirtySummary = false, 0, ""
	got := homeGitRows(t, &api.StateDto{Git: g})
	if !strings.Contains(got, "clean") {
		t.Errorf("clean tree did not say so:\n%s", got)
	}
	if strings.Contains(got, "uncommitted") {
		t.Errorf("clean tree claimed uncommitted work:\n%s", got)
	}
}

// A detached HEAD has no branch — the engine serves Branch as "" and never the literal "HEAD".
func TestHomeGitDetachedHead(t *testing.T) {
	g := gitFixture()
	g.Branch, g.Detached = "", true
	got := homeGitRows(t, &api.StateDto{Git: g})
	if !strings.Contains(got, "detached") || !strings.Contains(got, "9f2c1ab") {
		t.Errorf("detached HEAD row is wrong:\n%s", got)
	}
}

// FU-OWNER-10: both binaries are named, because they are installed separately and "did my reinstall
// take" has been answered wrongly by looking at only one of them. An engine that predates the field
// renders NOTHING — never "unknown", which reads as a lookup that failed rather than an old engine.
func TestHomeBuildLine(t *testing.T) {
	got := stripANSI(homeBuildLine(&api.StateDto{
		EngineVersion: "0.2.3-alpha.0.20", EngineCommit: "7d2b1e378ae3", FaceBuild: "d500f00a1b2c"}, 80))
	for _, want := range []string{"v0.2.3-alpha.0.20", "7d2b1e378ae3", "face d500f00a1b2c"} {
		if !strings.Contains(got, want) {
			t.Errorf("build line is missing %q: %q", want, got)
		}
	}
	if s := homeBuildLine(&api.StateDto{}, 80); s != "" {
		t.Errorf("engine with no version must render nothing, got %q", s)
	}
	if s := homeBuildLine(nil, 80); s != "" {
		t.Errorf("nil state must render nothing, got %q", s)
	}
}

// The build row has to actually reach the Server panel, not merely compose correctly.
func TestHomeServerCarriesTheBuildRow(t *testing.T) {
	m := Model{}
	m.data.Connection = api.ConnectionState{Mode: api.ModeLive, Connected: true, URL: "http://127.0.0.1:4317"}
	m.data.Plan = &api.StateDto{EngineVersion: "0.2.3-alpha.0.20", EngineCommit: "7d2b1e378ae3", FaceBuild: "d500f00a1b2c"}
	var b strings.Builder
	for _, l := range m.renderHomeServer(100) {
		b.WriteString(stripANSI(l.text) + "\n")
	}
	if !strings.Contains(b.String(), "v0.2.3-alpha.0.20") {
		t.Errorf("Server panel does not carry the build row:\n%s", b.String())
	}
}
