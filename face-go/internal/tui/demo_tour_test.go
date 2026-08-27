package tui

// CH2.2 — docs/assets/demo.gif is a picture of a product, taken once, and the product keeps moving.
//
// This is payesh's social-card gate ported (scripts/seo.mjs §6). There, each card's rendered text is
// recorded in a manifest when the PNG is taken, and the check re-renders and compares — which is why
// payesh's cards were caught going stale and conductor's GIF was not. The GIF has no text to
// re-render, but it does not need one: what it depicts is the Face's SURFACES, and those are
// declared in this package, in tabKey/tabNames/foldedTabs. So the manifest records the inventory the
// GIF was recorded against, and this test recomputes it from the declarations themselves.
//
// It lives in package tui, and it is a TEST rather than a script, for one reason each:
//
//   - in-package, so the inventory is READ from tabKey and tabNames rather than parsed out of Go
//     source or re-typed as a literal list. A literal list is the failure trap 21 names: it keeps
//     asserting after the vocabulary under it has moved.
//   - a test, because `go test ./...` is already the face-full gate AND both CI jobs
//     (.github/workflows/ci.yml:87 and :131). Refusing a merge is the whole point of the payesh
//     pattern, and this is where a refusal already binds.
//
// It does not diff pixels. It fails when the thing the GIF depicts has moved:
//
//	a tab added, renamed or rebound · a non-tab key the tape types claimed by a tab · the tape's
//	commands changed · the fleet fixture changed · the geometry moved off the size the goldens
//	cover · the committed GIF not being the one the manifest describes · a tab neither visited by
//	the tour nor excused in writing.
//
// TO FIX A RED RUN: re-record, which refreshes the manifest as its last step.
//
//	powershell -File tools/demo/make-demo-gif.ps1
//
// Never the other way round. `-write-demo-manifest` exists so that script has something to call; run
// it by hand and you have only told the manifest to stop noticing.

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"testing"
	"time"
	"unicode/utf8"

	"conductor-face-go/internal/api"
)

var writeDemoManifest = flag.Bool("write-demo-manifest", false,
	"rewrite docs/assets/demo.manifest.json from the current tape, fleet, GIF and Face. "+
		"tools/demo/make-demo-gif.ps1 runs this after a successful recording; do not run it by hand.")

// Everything the tour is made of, relative to the repo root. The test runs in internal/tui, three
// levels down.
const (
	demoRepoRel      = "../../.."
	demoTapeRel      = "docs/assets/demo.tape"
	demoFleetRel     = "docs/assets/demo-fleet.json"
	demoGifRel       = "docs/assets/demo.gif"
	demoManifestRel  = "docs/assets/demo.manifest.json"
	demoRecorderRel  = "tools/demo/make-demo-gif.ps1"
	demoGoldenSample = "home_demo.golden" // the frame the cell geometry is measured from
)

// demoManifest is what the GIF was recorded FROM. Written by the recorder, read by this test.
type demoManifest struct {
	RecordedAtUtc string `json:"recordedAtUtc"`
	Recorder      string `json:"recorder"`
	Note          string `json:"note"`

	Gif   demoBlobFact `json:"gif"`
	Tape  demoTapeFact `json:"tape"`
	Fleet demoTextFact `json:"fleet"`

	Geometry demoGeometry `json:"geometry"`

	// FaceSurfaces is the WHOLE tab inventory at record time, not just the toured part: a tab that
	// exists and is not in the GIF is exactly the staleness this era is paying for.
	FaceSurfaces []demoSurface `json:"faceSurfaces"`
	FoldedKeys   []demoSurface `json:"foldedKeys"`

	Visits     []demoVisit       `json:"visits"`
	NotVisited map[string]string `json:"notVisited"`
}

type demoBlobFact struct {
	Path   string `json:"path"`
	Bytes  int64  `json:"bytes"`
	Sha256 string `json:"sha256"`
}

// demoTapeFact hashes the tape's COMMANDS, not its bytes. Two reasons, and both have bitten this
// repo: the tape is a text file under core.autocrlf, so a byte hash differs between a Windows
// checkout and CI (CH1.1's carriage return, one file over); and the tape is half prose, so a
// byte hash would demand a re-record for a fixed typo in a comment.
type demoTapeFact struct {
	Path           string `json:"path"`
	CommandLines   int    `json:"commandLines"`
	CommandsSha256 string `json:"commandsSha256"`
}

type demoTextFact struct {
	Path   string `json:"path"`
	Sha256 string `json:"sha256"` // CR-stripped, for the same reason
}

// demoGeometry carries both halves because they are different facts that can drift apart. Pixels are
// what the tape asks VHS for; cells are what the goldens actually cover, measured from one.
type demoGeometry struct {
	WidthPx  int `json:"widthPx"`
	HeightPx int `json:"heightPx"`
	Cols     int `json:"cols"`
	Rows     int `json:"rows"`
}

type demoSurface struct {
	Key  string `json:"key"`
	Name string `json:"name"`
}

// demoVisit is one stop on the tour. Tab records the tab name when the key is a tab mnemonic, and is
// empty for the keys that are not tabs (`w`, `:`, `q`) — those carry Name only, and the check on
// them is that they have NOT since been claimed by a tab.
type demoVisit struct {
	Key  string `json:"key"`
	Tab  string `json:"tab,omitempty"`
	Name string `json:"name"`
}

func demoPath(rel string) string { return filepath.Join(demoRepoRel, filepath.FromSlash(rel)) }

// --- the facts, recomputed ------------------------------------------------------

// liveFaceSurfaces is the inventory as this package declares it right now. Derived, never typed: a
// new tab appears here the moment it appears in tabNames.
func liveFaceSurfaces() []demoSurface {
	out := make([]demoSurface, 0, tabCount)
	for i := 0; i < int(tabCount); i++ {
		out = append(out, demoSurface{Key: tabKey[i], Name: tabNames[i]})
	}
	return out
}

func liveFoldedKeys() []demoSurface {
	out := make([]demoSurface, 0, len(foldedTabs))
	for _, f := range foldedTabs {
		out = append(out, demoSurface{Key: f.Key, Name: tabNames[f.Tab]})
	}
	return out
}

// tabNameForKey resolves a key the way the Face's router does: tab mnemonics first, then the folded
// aliases. Anything else is not a tab, which is a fact the check uses rather than an error.
func tabNameForKey(key string) (string, bool) {
	for i := 0; i < int(tabCount); i++ {
		if tabKey[i] == key {
			return tabNames[i], true
		}
	}
	if t, ok := foldedTabKey[key]; ok {
		return tabNames[t], true
	}
	return "", false
}

// demoTapeCommands is the tape with its prose removed: no comments, no blank lines, no trailing
// whitespace, no carriage returns. This is what a re-record is actually keyed to.
func demoTapeCommands(t *testing.T) []string {
	t.Helper()
	raw, err := os.ReadFile(demoPath(demoTapeRel))
	if err != nil {
		t.Fatalf("reading %s: %v", demoTapeRel, err)
	}
	var out []string
	for _, line := range strings.Split(string(raw), "\n") {
		line = strings.TrimRight(strings.ReplaceAll(line, "\r", ""), " \t")
		if line == "" || strings.HasPrefix(strings.TrimSpace(line), "#") {
			continue
		}
		out = append(out, line)
	}
	return out
}

func sha256Hex(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}

func demoTapeFactNow(t *testing.T) demoTapeFact {
	t.Helper()
	cmds := demoTapeCommands(t)
	return demoTapeFact{
		Path:           demoTapeRel,
		CommandLines:   len(cmds),
		CommandsSha256: sha256Hex([]byte(strings.Join(cmds, "\n"))),
	}
}

func demoFleetFactNow(t *testing.T) demoTextFact {
	t.Helper()
	raw, err := os.ReadFile(demoPath(demoFleetRel))
	if err != nil {
		t.Fatalf("reading %s: %v", demoFleetRel, err)
	}
	// It has to parse, or the tour records a Face that fell back to "no other runs in this fleet".
	if _, err := ParseFleet(string(raw)); err != nil {
		t.Fatalf("%s is not a fleet the Face would accept: %v", demoFleetRel, err)
	}
	return demoTextFact{Path: demoFleetRel, Sha256: sha256Hex([]byte(strings.ReplaceAll(string(raw), "\r", "")))}
}

func demoGifFactNow(t *testing.T) demoBlobFact {
	t.Helper()
	raw, err := os.ReadFile(demoPath(demoGifRel))
	if err != nil {
		t.Fatalf("reading %s: %v", demoGifRel, err)
	}
	return demoBlobFact{Path: demoGifRel, Bytes: int64(len(raw)), Sha256: sha256Hex(raw)}
}

// demoGeometryNow reads the pixels the tape asks for and measures the cells the goldens cover. The
// second half is why a golden rebaseline at another terminal size turns this red: the GIF would then
// be a recording at a size nothing renders a golden at.
func demoGeometryNow(t *testing.T) demoGeometry {
	t.Helper()
	g := demoGeometry{}
	for _, line := range demoTapeCommands(t) {
		f := strings.Fields(line)
		if len(f) != 3 || f[0] != "Set" {
			continue
		}
		n, err := strconv.Atoi(f[2])
		if err != nil {
			continue
		}
		switch f[1] {
		case "Width":
			g.WidthPx = n
		case "Height":
			g.HeightPx = n
		}
	}
	if g.WidthPx == 0 || g.HeightPx == 0 {
		t.Fatalf("%s declares no Set Width / Set Height", demoTapeRel)
	}

	golden, err := os.ReadFile(filepath.Join("testdata", "golden", demoGoldenSample))
	if err != nil {
		t.Fatalf("reading the golden the cell geometry is measured from: %v", err)
	}
	lines := strings.Split(strings.TrimSuffix(strings.ReplaceAll(string(golden), "\r", ""), "\n"), "\n")
	g.Rows = len(lines)
	for _, l := range lines {
		if n := utf8.RuneCountInString(l); n > g.Cols {
			g.Cols = n
		}
	}
	return g
}

// demoNonTabKeyNames names the keys the tour presses that are NOT tab mnemonics, so a freshly
// written manifest reads as something rather than as three blanks. These are DISPLAY names and
// nothing is asserted from them: what the check actually enforces about these keys is that they are
// still not tab mnemonics (stop 6 below). A key that stops being used by the tape simply stops
// appearing; one that appears without an entry here says so in the manifest.
var demoNonTabKeyNames = map[string]string{
	"w": "the owner queue's full pane (tab_home_owner.go)",
	":": "the command palette (cmdbar.go)",
	"q": "quit",
}

// demoVisitsNow reads the tour out of the tape: every `Type "x"` whose payload is one character, in
// order, resolved against the live router.
func demoVisitsNow(t *testing.T, recorded []demoVisit) []demoVisit {
	t.Helper()
	named := make(map[string]string, len(recorded))
	for _, v := range recorded {
		named[v.Key] = v.Name
	}
	// Stop zero is the frame the tour opens on, and nothing is typed to reach it. Which tab that is
	// comes from New() — a fresh Model, asked — rather than from the comment on TabHome saying it is
	// the landing page. If the Face ever opens somewhere else, this stop changes and the check says
	// the GIF's first frame is not the product's first frame.
	landing := New(api.NewDemoSource(), true, "(demo)").tab
	out := []demoVisit{{Tab: tabNames[landing], Name: tabNames[landing] + " (the tour opens here; nothing is pressed)"}}

	for _, line := range demoTapeCommands(t) {
		if !strings.HasPrefix(line, "Type ") {
			continue
		}
		payload := strings.TrimSpace(strings.TrimPrefix(line, "Type "))
		payload = strings.Trim(payload, `"`)
		if utf8.RuneCountInString(payload) != 1 {
			continue // a typed word — the palette query, the startup command line
		}
		if tab, ok := tabNameForKey(payload); ok {
			out = append(out, demoVisit{Key: payload, Tab: tab, Name: tab})
			continue
		}
		// Not a tab. The manifest is what names it; the check below is that it is STILL not a tab.
		name := named[payload]
		if name == "" {
			name = demoNonTabKeyNames[payload]
		}
		if name == "" {
			name = "(unnamed - name it in " + demoManifestRel + ")"
		}
		out = append(out, demoVisit{Key: payload, Name: name})
	}
	return out
}

func demoManifestNow(t *testing.T, prev *demoManifest) demoManifest {
	t.Helper()
	recorded := []demoVisit(nil)
	notVisited := map[string]string(nil)
	if prev != nil {
		recorded, notVisited = prev.Visits, prev.NotVisited
	}
	return demoManifest{
		RecordedAtUtc: time.Now().UTC().Format(time.RFC3339),
		Recorder:      demoRecorderRel,
		Note: "What docs/assets/demo.gif was recorded FROM. Refreshed by the recorder, checked by " +
			"face-go/internal/tui/demo_tour_test.go. If the check is red, re-record - do not edit this file.",
		Gif:          demoGifFactNow(t),
		Tape:         demoTapeFactNow(t),
		Fleet:        demoFleetFactNow(t),
		Geometry:     demoGeometryNow(t),
		FaceSurfaces: liveFaceSurfaces(),
		FoldedKeys:   liveFoldedKeys(),
		Visits:       demoVisitsNow(t, recorded),
		NotVisited:   notVisited,
	}
}

// --- the check -------------------------------------------------------------------

func TestDemoGifStillShowsTheFaceItWasRecordedFrom(t *testing.T) {
	path := demoPath(demoManifestRel)

	var prev *demoManifest
	if raw, err := os.ReadFile(path); err == nil {
		var m demoManifest
		if err := json.Unmarshal(raw, &m); err != nil {
			t.Fatalf("%s is not readable JSON: %v", demoManifestRel, err)
		}
		prev = &m
	} else if !*writeDemoManifest {
		t.Fatalf("%s is missing. Record the GIF and it is written for you:\n    powershell -File %s",
			demoManifestRel, demoRecorderRel)
	}

	now := demoManifestNow(t, prev)

	if *writeDemoManifest {
		out, err := json.MarshalIndent(now, "", "  ")
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, append(out, '\n'), 0o644); err != nil {
			t.Fatal(err)
		}
		t.Logf("wrote %s: %d KB GIF, %d tape commands, %d Face surfaces, %d stops on the tour",
			demoManifestRel, now.Gif.Bytes/1024, now.Tape.CommandLines, len(now.FaceSurfaces), len(now.Visits))
		return
	}

	m := *prev
	fix := fmt.Sprintf("\n    Re-record, which refreshes the manifest:  powershell -File %s", demoRecorderRel)

	// 1. The GIF in the repo is the one this manifest describes.
	if m.Gif.Sha256 != now.Gif.Sha256 || m.Gif.Bytes != now.Gif.Bytes {
		t.Errorf("%s is not the GIF %s describes.\n  manifest: %d bytes, sha %s\n  on disk:  %d bytes, sha %s%s",
			demoGifRel, demoManifestRel, m.Gif.Bytes, short(m.Gif.Sha256), now.Gif.Bytes, short(now.Gif.Sha256), fix)
	}

	// 2. The tour the tape drives is the tour that was recorded. Comments are excluded, so this is a
	//    real change to what the GIF shows, not a prose edit.
	if m.Tape.CommandsSha256 != now.Tape.CommandsSha256 {
		t.Errorf("%s has changed since the GIF was recorded (%d command lines then, %d now). The GIF "+
			"still shows the old tour.%s", demoTapeRel, m.Tape.CommandLines, now.Tape.CommandLines, fix)
	}

	// 3. Same for the data the tour renders from.
	if m.Fleet.Sha256 != now.Fleet.Sha256 {
		t.Errorf("%s has changed since the GIF was recorded, so the run switcher in the GIF shows runs "+
			"that are no longer the demo's.%s", demoFleetRel, fix)
	}

	// 4. Geometry: what the tape asks for, and the size the goldens actually cover.
	if m.Geometry.WidthPx != now.Geometry.WidthPx || m.Geometry.HeightPx != now.Geometry.HeightPx {
		t.Errorf("the tape's geometry moved (%dx%d recorded, %dx%d now).%s",
			m.Geometry.WidthPx, m.Geometry.HeightPx, now.Geometry.WidthPx, now.Geometry.HeightPx, fix)
	}
	if m.Geometry.Cols != now.Geometry.Cols || m.Geometry.Rows != now.Geometry.Rows {
		t.Errorf("the goldens no longer cover the size the GIF was recorded at: %dx%d cells when it was "+
			"recorded, %dx%d now (measured from testdata/golden/%s). Either the GIF is a recording at a "+
			"size nothing is test-covered at, or the tape's pixels need re-probing - see the GEOMETRY "+
			"block in %s.%s",
			m.Geometry.Cols, m.Geometry.Rows, now.Geometry.Cols, now.Geometry.Rows, demoGoldenSample,
			demoTapeRel, fix)
	}

	// 5. THE ONE THIS ERA IS PAYING FOR: the Face's surfaces, against the surfaces that existed when
	//    the picture was taken. A tab added, renamed or rebound lands here.
	if diff := surfaceDiff(m.FaceSurfaces, now.FaceSurfaces); diff != "" {
		t.Errorf("the Face's surfaces have moved since the GIF was recorded:\n%s\n  The GIF under the "+
			"README's H1 shows a product that no longer exists.%s", diff, fix)
	}
	if diff := surfaceDiff(m.FoldedKeys, now.FoldedKeys); diff != "" {
		t.Errorf("the folded keys have moved since the GIF was recorded:\n%s%s", diff, fix)
	}

	// 6. The tour itself, stop by stop. A rebound mnemonic makes a recorded stop open something else.
	if len(m.Visits) != len(now.Visits) {
		t.Errorf("the tour has %d stops now and %d when the GIF was recorded.%s", len(now.Visits), len(m.Visits), fix)
	} else {
		for i := range m.Visits {
			was, is := m.Visits[i], now.Visits[i]
			if was.Key != is.Key || was.Tab != is.Tab {
				t.Errorf("stop %d of the tour: the GIF shows %q pressing %q, and %q now opens %q.%s",
					i+1, orDash(was.Tab, was.Name), was.Key, is.Key, orDash(is.Tab, is.Name), fix)
			}
			// A key the tour uses for something that is NOT a tab (`w` for the owner queue pane, `:`
			// for the palette, `q` for quit) must not have been claimed by one since. If it has, the
			// recorded frame shows a surface that keypress no longer reaches.
			if was.Tab == "" && is.Tab != "" {
				t.Errorf("stop %d: %q opened %s when the GIF was recorded, and is now the mnemonic for "+
					"the %s tab.%s", i+1, was.Key, was.Name, is.Tab, fix)
			}
		}
	}

	// 7. payesh's leftover check: every card is used by a page. Here, every tab is either on the tour
	//    or excused in writing. A new tab cannot be quietly left out of the GIF.
	visited := map[string]bool{}
	for _, v := range now.Visits {
		if v.Tab != "" {
			visited[v.Tab] = true
		}
	}
	for _, s := range liveFaceSurfaces() {
		_, excused := m.NotVisited[s.Name]
		switch {
		case visited[s.Name] && excused:
			t.Errorf("%s is both on the tour and listed in notVisited. Drop it from notVisited.", s.Name)
		case !visited[s.Name] && !excused:
			t.Errorf("the %s tab (%q) is not on the tour and %s does not say why. Either add it to %s "+
				"and re-record, or record the reason in notVisited.%s", s.Name, s.Key, demoManifestRel,
				demoTapeRel, fix)
		}
	}
	for name := range m.NotVisited {
		if !tabExists(name) {
			t.Errorf("notVisited records a tab %q that the Face no longer has.%s", name, fix)
		}
	}
}

func tabExists(name string) bool {
	for i := 0; i < int(tabCount); i++ {
		if tabNames[i] == name {
			return true
		}
	}
	return false
}

func keyOfTab(name string) string {
	for i := 0; i < int(tabCount); i++ {
		if tabNames[i] == name {
			return tabKey[i]
		}
	}
	return ""
}

func orDash(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

func short(sha string) string {
	if len(sha) > 12 {
		return sha[:12]
	}
	return sha
}

// surfaceDiff reports what moved between two inventories, keyed by name so a rebound mnemonic reads
// as a rebinding rather than as one removal and one addition.
func surfaceDiff(was, is []demoSurface) string {
	wasBy := map[string]string{}
	for _, s := range was {
		wasBy[s.Name] = s.Key
	}
	isBy := map[string]string{}
	for _, s := range is {
		isBy[s.Name] = s.Key
	}
	var lines []string
	for name, key := range isBy {
		old, had := wasBy[name]
		switch {
		case !had:
			lines = append(lines, fmt.Sprintf("  + %s (%q) is new since the recording", name, key))
		case old != key:
			lines = append(lines, fmt.Sprintf("  ~ %s moved from %q to %q", name, old, key))
		}
	}
	for name, key := range wasBy {
		if _, still := isBy[name]; !still {
			lines = append(lines, fmt.Sprintf("  - %s (%q) is gone since the recording", name, key))
		}
	}
	sort.Strings(lines)
	return strings.Join(lines, "\n")
}
