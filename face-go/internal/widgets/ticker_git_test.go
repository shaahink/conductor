package widgets

import (
	"regexp"
	"strings"
	"testing"

	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

// The chip's pieces are styled INDIVIDUALLY — the dot is yellow and its count is dim — so "●4" is
// never adjacent in the raw string even when it is adjacent on screen. Assertions about what a
// reader SEES therefore have to be made against the stripped text, or they test the escape codes.
var barAnsiRe = regexp.MustCompile(`\x1b\[[0-9;]*[a-zA-Z]`)

func plainBar(s string) string { return barAnsiRe.ReplaceAllString(s, "") }

// The SF3.3 top-bar chip and FU-OWNER-10's build stamp. Everything here is measured off the
// RENDERED bar rather than off the chip helpers alone, because the failure this checkpoint is
// guarding against is not a wrong chip — it is a right chip the outer MaxWidth ate on the way out.

func strPtr(s string) *string { return &s }
func intPtr(i int) *int       { return &i }

// gitBusy is the state a run is actually in while it works: a tracked feature branch, ahead of its
// remote, behind it, and dirty. A demo that only ever shows main/clean/in-sync exercises none of
// the rendering that matters.
func gitBusy() *api.GitDto {
	return &api.GitDto{
		IsRepo: true, Branch: "feat/gate-caching",
		Upstream: strPtr("origin/feat/gate-caching"), Ahead: intPtr(3), Behind: intPtr(1),
		HeadSha: "9f2c1ab7d4e60b83a5c1e2f0d7b6a94c3e8f1d20", HeadShortSha: "9f2c1ab",
		HeadSubject: "feat(gates): key the cache by (name, tier, sha)",
		Dirty:       true, DirtyCount: 4,
	}
}

func topBarState(g *api.GitDto) *api.StateDto {
	return &api.StateDto{
		Repo: `C:\Code\conductor`, PlanName: "sarban-face", Status: "running",
		StageId: "SF3", StageTitle: "Reading a session becomes cheap",
		DoneCount: 12, TotalCount: 31, TotalCostUsd: 41.05,
		Git:           g,
		EngineVersion: "0.2.3-alpha.0.20", EngineCommit: "7d2b1e378ae3", FaceBuild: "d500f00a1b2c",
	}
}

func liveConn() api.ConnectionState {
	return api.ConnectionState{Mode: api.ModeLive, Connected: true, URL: "http://127.0.0.1:4317"}
}

// The whole reason the chip is fitted by measurement: a bar that overflows is not a bar with a
// wrapped chip, it is a bar with a SILENTLY missing right-hand end. Whatever else changes, the
// rendered row is exactly as wide as it was asked to be, and one row.
func TestTopBarNeverOverflowsWithGitChip(t *testing.T) {
	for _, w := range []int{60, 80, 96, 100, 120, 140, 160, 200, 240} {
		for name, g := range map[string]*api.GitDto{
			"busy":        gitBusy(),
			"no upstream": {IsRepo: true, Branch: "wip/experiment", HeadShortSha: "abc1234", Dirty: true, DirtyCount: 2},
			"clean":       {IsRepo: true, Branch: "master", Upstream: strPtr("origin/master"), Ahead: intPtr(0), Behind: intPtr(0), HeadShortSha: "0000000"},
			"nil":         nil,
		} {
			st := topBarState(g)
			st.AgentActive, st.SessionNumber, st.SessionElapsedSec = true, 18, 640
			bar := RenderTopBar(liveConn(), st, w, 0)
			if got := lipgloss.Width(bar); got > w {
				t.Errorf("%s at width %d: rendered %d columns wide", name, w, got)
			}
			if n := strings.Count(bar, "\n"); n != 0 {
				t.Errorf("%s at width %d: bar wrapped onto %d extra rows", name, w, n)
			}
		}
	}
}

// A nil Git is an engine that predates SF3.3. It knows nothing about the repo, so the Face must say
// nothing — not a blank chip, not a hopeful "clean". Same for the build stamp on an engine that
// serves no version: "" renders nothing, never "unknown".
func TestTopBarSaysNothingGitShapedWithoutTheWire(t *testing.T) {
	st := topBarState(nil)
	st.EngineVersion, st.EngineCommit = "", ""
	bar := RenderTopBar(liveConn(), st, 200, 0)
	for _, forbidden := range []string{"⎇", "≡", "unpushed", "↑", "↓", "unknown", "v0."} {
		if strings.Contains(bar, forbidden) {
			t.Errorf("pre-SF3.3 engine rendered %q in the top bar:\n%s", forbidden, bar)
		}
	}
}

// A non-repo workspace is a different fact from a pre-SF3.3 engine, but neither belongs in the
// status strip — the strip has no room to explain, and a bare "⎇" over a directory that is not a
// repo is worse than silence. Home is where that sentence lands.
func TestTopBarOmitsTheChipWhenTheWorkspaceIsNotARepo(t *testing.T) {
	bar := RenderTopBar(liveConn(), topBarState(&api.GitDto{IsRepo: false}), 200, 0)
	if strings.Contains(bar, "⎇") {
		t.Errorf("non-repo workspace still rendered a branch chip:\n%s", bar)
	}
}

// The chip at the width the owner actually runs. 200 is the wide desk, 120 the laptop, 80 the
// pinched split — and 80 is where the bar was already full before SF3.3 touched it.
func TestTopBarChipAtRealWidths(t *testing.T) {
	cases := []struct {
		width int
		want  []string // substrings that must be present at this width
	}{
		{200, []string{"⎇", "feat/gate-caching", "↑3↓1", "●4", "v0.2.3-alpha.0.20", "7d2b1e3"}},
		{120, []string{"⎇", "↑3↓1", "●"}},
	}
	for _, tc := range cases {
		bar := plainBar(RenderTopBar(liveConn(), topBarState(gitBusy()), tc.width, 0))
		for _, want := range tc.want {
			if !strings.Contains(bar, want) {
				t.Errorf("width %d: chip is missing %q:\n%s", tc.width, want, bar)
			}
		}
	}
}

// No upstream is not zero. This is the single most dangerous confusion the chip can make: a branch
// that has never been pushed must never render the way a branch level with its remote does, because
// the owner reads that strip to decide whether their work is safe off the machine.
func TestNoUpstreamNeverRendersAsInSync(t *testing.T) {
	unpushed := &api.GitDto{IsRepo: true, Branch: "wip/experiment", HeadShortSha: "abc1234"}
	level := &api.GitDto{IsRepo: true, Branch: "wip/experiment", Upstream: strPtr("origin/wip/experiment"),
		Ahead: intPtr(0), Behind: intPtr(0), HeadShortSha: "abc1234"}

	for i, chip := range gitChips(unpushed) {
		if strings.Contains(chip, "≡") {
			t.Errorf("tier %d rendered an unpushed branch as level with a remote: %q", i, chip)
		}
		if strings.Contains(chip, "↑") || strings.Contains(chip, "↓") {
			t.Errorf("tier %d invented an arrow for a branch with no upstream: %q", i, chip)
		}
	}
	// And the two must be distinguishable at EVERY tier the chip renders at — not merely at the
	// widest one, where there is room to spell it out.
	lvl := gitChips(level)
	unp := gitChips(unpushed)
	if len(lvl) != len(unp) {
		t.Fatalf("tier counts differ: %d vs %d", len(lvl), len(unp))
	}
	for i := range lvl {
		if lvl[i] == unp[i] {
			t.Errorf("tier %d renders unpushed and in-sync identically: %q", i, lvl[i])
		}
	}
}

// The dirty dot and the divergence marker are the chip's two readable silences: no dot means clean.
// That reading is only safe if no tier is allowed to drop a dot it had, so the tiering shortens the
// branch name and the count — never the marks themselves.
func TestNoChipTierDropsTheDirtyDotOrTheDivergenceMark(t *testing.T) {
	for i, chip := range gitChips(gitBusy()) {
		if !strings.Contains(chip, "●") {
			t.Errorf("tier %d dropped the dirty dot: %q", i, chip)
		}
		if !strings.Contains(chip, "↑") {
			t.Errorf("tier %d dropped the divergence mark: %q", i, chip)
		}
	}
	clean := &api.GitDto{IsRepo: true, Branch: "master", Upstream: strPtr("origin/master"),
		Ahead: intPtr(0), Behind: intPtr(0), HeadShortSha: "0000000"}
	for i, chip := range gitChips(clean) {
		if strings.Contains(chip, "●") {
			t.Errorf("tier %d put a dirty dot on a clean tree: %q", i, chip)
		}
	}
}

// A detached HEAD has no branch — the engine serves Branch as "" and never the literal "HEAD" — so
// the sha becomes the name rather than leaving the chip reading "⎇ " and nothing else.
func TestDetachedHeadNamesItselfBySha(t *testing.T) {
	chips := gitChips(&api.GitDto{IsRepo: true, Detached: true, HeadShortSha: "9f2c1ab",
		HeadSha: "9f2c1ab7d4e60b83a5c1e2f0d7b6a94c3e8f1d20"})
	if len(chips) == 0 {
		t.Fatal("detached HEAD rendered no chip at all")
	}
	if !strings.Contains(chips[0], "9f2c1ab") {
		t.Errorf("detached chip does not name the sha: %q", chips[0])
	}
}

// FU-OWNER-10: the stamp degrades to the version alone before it disappears, because the version is
// the half that answers "did my reinstall take" — the commit is the half that says which build of
// that version. An engine that serves no commit still gets a stamp.
func TestBuildStampsDegradeVersionLast(t *testing.T) {
	full := buildStamps(&api.StateDto{EngineVersion: "0.2.3-alpha.0.20", EngineCommit: "7d2b1e378ae3"})
	if len(full) != 3 {
		t.Fatalf("stamp tiers are wrong: %q", full)
	}
	if !strings.Contains(full[0], "7d2b1e3") || !strings.Contains(full[0], "0.2.3-alpha.0.20") {
		t.Errorf("widest stamp must carry version AND commit: %q", full[0])
	}
	if strings.Contains(full[1], "7d2b1e3") {
		t.Errorf("middle stamp should be the version alone: %q", full[1])
	}
	// The narrowest tier keeps the COMMIT, not the version: two builds of the same prerelease are
	// exactly the pair "did my reinstall take" cannot distinguish, and only the sha separates them.
	if !strings.Contains(full[2], "7d2b1e3") || strings.Contains(full[2], "0.2.3") {
		t.Errorf("narrowest stamp should be the labelled commit alone: %q", full[2])
	}
	if strings.Contains(full[0], "7d2b1e378ae3") {
		t.Errorf("stamp pasted the full commit rather than the short sha: %q", full[0])
	}
	if got := buildStamps(&api.StateDto{EngineVersion: "0.2.3"}); len(got) != 1 {
		t.Errorf("engine with no commit should still stamp its version, got %q", got)
	}
	if got := buildStamps(&api.StateDto{EngineCommit: "7d2b1e378ae3"}); got != nil {
		t.Errorf("engine with no version must render nothing, got %q", got)
	}
}

// The chip outranks the build stamp for the last columns on the bar: which branch is being written
// to is operational, which build is serving is identity you check occasionally. On a bar with room
// for exactly one of them, the branch wins.
func TestChipOutranksTheBuildStamp(t *testing.T) {
	st := topBarState(gitBusy())
	st.AgentActive, st.SessionNumber, st.SessionElapsedSec = true, 18, 640
	for w := 60; w <= 240; w++ {
		bar := plainBar(RenderTopBar(liveConn(), st, w, 0))
		stamped := strings.Contains(bar, "0.2.3") || strings.Contains(bar, "eng 7d2b1e3")
		if stamped && !strings.Contains(bar, "⎇") {
			t.Fatalf("width %d: build stamp took the space the branch chip needed:\n%s", w, bar)
		}
	}
	// …but a workspace with no chip to render is not competing with anything: the stamp is free to
	// take those columns, and an engine identity is worth having on a bar that has room for it.
	noRepo := topBarState(&api.GitDto{IsRepo: false})
	if bar := plainBar(RenderTopBar(liveConn(), noRepo, 200, 0)); !strings.Contains(bar, "0.2.3") {
		t.Errorf("non-repo workspace suppressed the build stamp too:\n%s", bar)
	}
}
