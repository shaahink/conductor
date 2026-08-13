package tui

import (
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// KS2.7's ratchet. adr/0006 decision 1 says every body that can outgrow its pane is a
// viewport.Model and "no surface keeps a bare scroll integer" — and that sentence had been true on
// paper and false in the code for three checkpoints running. K6.2 converted Report and Knowledge,
// K6.4 converted the owner queue and the Templates preview, and SIX surfaces were left behind:
// Agent's raw stream, the parsed transcript, both History views, Processes, Telegram and the Kanban
// card detail. Each one re-implemented the same unbounded `++` with the same renderer-side clamp
// that a value receiver can never write back — which is bug #30, once per surface.
//
// A test written after a conversion certifies the conversion. This one is written to OUTLIVE it: it
// reads the module's own AST and fails on any struct field whose NAME says scroll and whose TYPE is
// a bare integer, so the seventh instance cannot be added quietly. It is the same walker shape as
// module_intent_test.go's directImports — measure the claim, never trust the comment.
//
// The regex is deliberately about NAMES, not about every int on the model. A selection cursor
// (`selected`, `sessionSelected`, `fieldIdx`, `cursor`) is not a scroll offset: it addresses a ROW
// of data, it survives a resize, and the viewport follows it (see ensurePaneRow). Naming a field
// `…Scroll` or `…ScrollOffset` or `yOffset` is the author saying "this is a scroll position", and
// that is exactly the thing that must be a viewport.
var scrollishField = regexp.MustCompile(`(?i)scroll|yoffset`)

// scrollIntegerAllowlist names every survivor and why. It is short on purpose: an entry here is a
// standing exception to the ADR, so it has to be worth reading. Keys are "<package>.<Type>.<field>".
var scrollIntegerAllowlist = map[string]string{
	// An editor's caret window, not a read pane. adr/0006 §5 defers the whole TextArea to
	// bubbles/textarea rather than half-converting it: the offset here follows a CARET, so it is
	// driven by insertions and arrow keys in the middle of a buffer, not by a pager key set, and
	// wrapping it in a viewport would leave two things fighting over the same first visible row.
	"widgets.TextArea.scroll": "editor caret window — adr/0006 §5 defers it to bubbles/textarea",
}

// scrollSurfacesNotConverted records the two scrollable-ish surfaces KS2.7 deliberately did NOT put
// behind a single pane viewport, with the reason, because an unexplained exemption is how the
// previous adoption pass stopped half-done and produced this checkpoint. Neither holds a bare scroll
// integer — both derive their window from a selection every frame — so the ratchet above does not
// see them and this list is the only place the decision is written down beside the code.
//
//   - Kanban BOARD (kanbanWindow, tab_kanban.go): a per-COLUMN clip. Three columns side by side
//     cannot share one viewport, and giving each its own would put three scroll positions behind one
//     key set. The board's window follows the selected card; the plan named Kanban DETAIL, which is
//     converted.
//   - Templates LIST (tab_templates.go): the plan's fixed prompt-template set from templates.List —
//     bounded by construction, never a long body. The surface here that CAN outgrow the pane is the
//     preview, which has been on previewVp since K6.4.
var scrollSurfacesNotConverted = []string{"Kanban board (per-column clip)", "Templates list (bounded set)"}

// TestNoSurfaceKeepsABareScrollInteger walks internal/tui and internal/widgets and fails on any
// struct field that names itself a scroll position and types itself as a bare int.
func TestNoSurfaceKeepsABareScrollInteger(t *testing.T) {
	root := moduleRoot(t)
	var offenders []string
	inspected := 0

	for _, pkg := range []string{"tui", "widgets"} {
		dir := filepath.Join(root, "internal", pkg)
		entries, err := os.ReadDir(dir)
		if err != nil {
			t.Fatalf("read %s: %v", dir, err)
		}
		fset := token.NewFileSet()
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".go") || strings.HasSuffix(e.Name(), "_test.go") {
				continue
			}
			f, err := parser.ParseFile(fset, filepath.Join(dir, e.Name()), nil, 0)
			if err != nil {
				t.Fatalf("parse %s: %v", e.Name(), err)
			}
			ast.Inspect(f, func(n ast.Node) bool {
				ts, ok := n.(*ast.TypeSpec)
				if !ok {
					return true
				}
				st, ok := ts.Type.(*ast.StructType)
				if !ok || st.Fields == nil {
					return true
				}
				for _, field := range st.Fields.List {
					ident, ok := field.Type.(*ast.Ident)
					if !ok || (ident.Name != "int" && ident.Name != "int64") {
						continue
					}
					for _, name := range field.Names {
						inspected++
						if !scrollishField.MatchString(name.Name) {
							continue
						}
						key := pkg + "." + ts.Name.Name + "." + name.Name
						if _, allowed := scrollIntegerAllowlist[key]; allowed {
							continue
						}
						offenders = append(offenders, key+" ("+e.Name()+":"+
							itoa(fset.Position(name.Pos()).Line)+") is a bare "+ident.Name)
					}
				}
				return true
			})
		}
	}

	if inspected == 0 {
		t.Fatal("walked no integer struct fields at all — the walker is broken, not the module")
	}
	sort.Strings(offenders)
	for _, o := range offenders {
		t.Errorf("%s — adr/0006 decision 1: a body that scrolls is a viewport.Model, and the offset "+
			"is clamped where it is CHANGED. Convert it (panescroll.go is the only idiom) or add it to "+
			"scrollIntegerAllowlist with the reason it is not a read pane.", o)
	}
}

// The allowlist is a standing exception, so it has to keep naming something real. An entry left
// behind after its field is gone reads as "this is still an exception" to the next reader, which is
// how a list of exceptions becomes a list of folklore.
func TestScrollAllowlistNamesOnlyLiveFields(t *testing.T) {
	root := moduleRoot(t)
	live := map[string]bool{}
	for _, pkg := range []string{"tui", "widgets"} {
		dir := filepath.Join(root, "internal", pkg)
		entries, _ := os.ReadDir(dir)
		fset := token.NewFileSet()
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".go") || strings.HasSuffix(e.Name(), "_test.go") {
				continue
			}
			f, err := parser.ParseFile(fset, filepath.Join(dir, e.Name()), nil, 0)
			if err != nil {
				t.Fatalf("parse %s: %v", e.Name(), err)
			}
			ast.Inspect(f, func(n ast.Node) bool {
				ts, ok := n.(*ast.TypeSpec)
				if !ok {
					return true
				}
				st, ok := ts.Type.(*ast.StructType)
				if !ok || st.Fields == nil {
					return true
				}
				for _, field := range st.Fields.List {
					for _, name := range field.Names {
						live[pkg+"."+ts.Name.Name+"."+name.Name] = true
					}
				}
				return true
			})
		}
	}
	for key, why := range scrollIntegerAllowlist {
		if !live[key] {
			t.Errorf("scrollIntegerAllowlist still excuses %s (%q) — the field is gone, so the "+
				"exception is folklore; delete the entry", key, why)
		}
	}
	if len(scrollSurfacesNotConverted) == 0 {
		t.Error("the not-converted list was emptied without converting anything — see its comment")
	}
}
