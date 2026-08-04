package tui

import (
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// K1.3: go.mod used to misdescribe its own graph -- glamour was imported directly by markdown.go and
// harmonica by anim.go, yet both were listed `// indirect`. A stale `// indirect` is not cosmetic: it
// says "nothing here imports this", so a reader pruning dependencies deletes a package the Face is
// compiled against. These tests measure the claim instead of trusting the comment: they read the real
// import graph out of the sources and compare it to what go.mod says about each module.
//
// They walk the module from this package's directory (internal/tui) up two levels, so they cover
// cmd/ and every internal/ package, not just this one.

func moduleRoot(t *testing.T) string {
	t.Helper()
	wd, err := os.Getwd()
	if err != nil {
		t.Fatalf("getwd: %v", err)
	}
	root := filepath.Clean(filepath.Join(wd, "..", ".."))
	if _, err := os.Stat(filepath.Join(root, "go.mod")); err != nil {
		t.Fatalf("expected go.mod at %s: %v", root, err)
	}
	return root
}

// directImports returns every external (non-stdlib, non-own-module) import path in the module, mapped
// to the source file that imports it -- the ground truth go.mod is supposed to describe.
func directImports(t *testing.T, root string) map[string]string {
	t.Helper()
	found := map[string]string{}
	fset := token.NewFileSet()
	err := filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			// testdata holds golden frames, not compiled Go.
			if d.Name() == "testdata" || d.Name() == "vendor" || strings.HasPrefix(d.Name(), ".") && d.Name() != "." {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(path, ".go") {
			return nil
		}
		f, err := parser.ParseFile(fset, path, nil, parser.ImportsOnly)
		if err != nil {
			return err
		}
		for _, imp := range f.Imports {
			p := strings.Trim(imp.Path.Value, `"`)
			// stdlib has no dot in its first path element; own-module imports are not dependencies.
			first := p
			if i := strings.Index(p, "/"); i >= 0 {
				first = p[:i]
			}
			if !strings.Contains(first, ".") || strings.HasPrefix(p, "conductor-face-go/") {
				continue
			}
			if _, seen := found[p]; !seen {
				rel, _ := filepath.Rel(root, path)
				found[p] = filepath.ToSlash(rel)
			}
		}
		return nil
	})
	if err != nil {
		t.Fatalf("walk: %v", err)
	}
	if len(found) == 0 {
		t.Fatal("no external imports found -- the walk is broken, not the module")
	}
	return found
}

// goModIndirect maps each required module path to whether go.mod marks it `// indirect`.
func goModIndirect(t *testing.T, root string) map[string]bool {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join(root, "go.mod"))
	if err != nil {
		t.Fatalf("read go.mod: %v", err)
	}
	mods := map[string]bool{}
	for _, line := range strings.Split(string(raw), "\n") {
		line = strings.TrimSpace(strings.TrimSuffix(line, "\r"))
		if line == "" || strings.HasPrefix(line, "//") || strings.HasPrefix(line, "module ") ||
			strings.HasPrefix(line, "go ") || line == "require (" || line == ")" {
			continue
		}
		line = strings.TrimPrefix(line, "require ")
		indirect := strings.Contains(line, "// indirect")
		if i := strings.Index(line, "//"); i >= 0 {
			line = strings.TrimSpace(line[:i])
		}
		fields := strings.Fields(line)
		if len(fields) < 2 {
			continue
		}
		mods[fields[0]] = indirect
	}
	if len(mods) == 0 {
		t.Fatal("parsed no requirements out of go.mod")
	}
	return mods
}

// owningModule picks the longest required-module prefix of an import path, which is how the go
// command resolves it: charm.land/lipgloss/v2/... belongs to charm.land/lipgloss/v2.
func owningModule(mods map[string]bool, importPath string) (string, bool) {
	best := ""
	for m := range mods {
		if importPath == m || strings.HasPrefix(importPath, m+"/") {
			if len(m) > len(best) {
				best = m
			}
		}
	}
	return best, best != ""
}

// TestGoModDoesNotCallDirectImportsIndirect is the ratchet: any module a source file imports must sit
// in go.mod's direct block. This is the exact bug K1.3 closed, so it is pinned rather than described.
func TestGoModDoesNotCallDirectImportsIndirect(t *testing.T) {
	root := moduleRoot(t)
	mods := goModIndirect(t, root)
	var wrong []string
	for imp, file := range directImports(t, root) {
		mod, ok := owningModule(mods, imp)
		if !ok {
			wrong = append(wrong, imp+" (imported by "+file+") is in no go.mod require block")
			continue
		}
		if mods[mod] {
			wrong = append(wrong, mod+" is marked // indirect but "+file+" imports "+imp+" directly")
		}
	}
	sort.Strings(wrong)
	for _, w := range wrong {
		t.Error(w)
	}
}

// TestLipglossV1StaysIndirect pins the deliberate two-major split. glamour v1.0.0 pulls
// github.com/charmbracelet/lipgloss v1 in transitively; the Face styles with charm.land/lipgloss/v2.
// Importing v1 anywhere in the Face would hand one widget two incompatible Style types.
func TestLipglossV1StaysIndirect(t *testing.T) {
	root := moduleRoot(t)
	const v1 = "github.com/charmbracelet/lipgloss"
	for imp, file := range directImports(t, root) {
		if imp == v1 || strings.HasPrefix(imp, v1+"/") {
			t.Errorf("%s imports lipgloss v1 (%s) -- the Face styles with charm.land/lipgloss/v2", file, imp)
		}
	}
	mods := goModIndirect(t, root)
	if indirect, ok := mods[v1]; ok && !indirect {
		t.Errorf("%s is in go.mod's DIRECT block; it should arrive only through glamour", v1)
	}
}
