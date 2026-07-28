// Package templates lists and edits the session-lifecycle prompt/persona templates that
// PromptBuilder/PersonaRegistry re-read from disk on every render (Core/PromptBuilder.cs). The
// Face runs on the same machine as the engine, so this is direct filesystem access — no HTTP
// round-trip and no new engine primitive needed. Saving here takes effect at the next session
// boundary, exactly like hand-editing the file would.
package templates

import (
	"os"
	"path/filepath"
	"strings"
)

// SessionTemplates are the 7 files PromptBuilder.Render() looks for under <planDir>/*.md. If one
// isn't on disk, the engine falls back to its own built-in default.
var SessionTemplates = []string{
	"session.md", "fix.md", "resume.md",
	"advisor.md", "audit.md", "review.md",
	"verify.md",
}

type Entry struct {
	// Label is the display name, e.g. "session.md" or "personas/architect.md".
	Label  string
	Path   string
	Exists bool
}

// List returns the known session templates plus whatever's under <planDir>/personas.
func List(planDir string) []Entry {
	entries := make([]Entry, 0, len(SessionTemplates))
	for _, name := range SessionTemplates {
		path := filepath.Join(planDir, name)
		_, err := os.Stat(path)
		entries = append(entries, Entry{Label: name, Path: path, Exists: err == nil})
	}

	personasDir := filepath.Join(planDir, "personas")
	if fis, err := os.ReadDir(personasDir); err == nil {
		for _, fi := range fis {
			if fi.IsDir() || !strings.HasSuffix(fi.Name(), ".md") {
				continue
			}
			entries = append(entries, Entry{
				Label:  "personas/" + fi.Name(),
				Path:   filepath.Join(personasDir, fi.Name()),
				Exists: true,
			})
		}
	}
	return entries
}

func Read(path string) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	return string(data)
}

func Write(path, content string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	return os.WriteFile(path, []byte(content), 0o644)
}
