package lastrun

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

// testdata/RUN-SUMMARY.md is not a hand-written fixture: it is a real file the engine wrote at the
// end of a scratch run (%TEMP%/sarban-proofs/sf1-2), BOM and all. Parsing a mock-up of the format is
// how a reader ends up agreeing with itself and disagreeing with the writer.
func TestParsesARealEngineWrittenSummary(t *testing.T) {
	s, err := Load("testdata")
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	if s == nil {
		t.Fatal("Load returned no summary for a directory that has one")
	}

	if s.Plan != "nosql" {
		t.Errorf("Plan = %q, want nosql (the BOM must not end up inside the first field)", s.Plan)
	}
	if s.RunId != "57d95596e175451c978557813cb5b889" {
		t.Errorf("RunId = %q", s.RunId)
	}
	if s.Outcome != "Completed" {
		t.Errorf("Outcome = %q, want Completed", s.Outcome)
	}
	if s.Repo != "C:/Users/shahi/AppData/Local/Temp/sarban-proofs/sf1-2" {
		t.Errorf("Repo = %q", s.Repo)
	}
	want := time.Date(2026, 7, 31, 22, 48, 0, 0, time.UTC)
	if !s.EndedUtc.Equal(want) {
		t.Errorf("EndedUtc = %v, want %v", s.EndedUtc, want)
	}
	if s.Sessions != "2 (1 deliver, 1 verify)" {
		t.Errorf("Sessions = %q", s.Sessions)
	}
	if s.Checkpoints != "1/1 done" {
		t.Errorf("Checkpoints = %q", s.Checkpoints)
	}
	if s.Spend != "$0.0000 total (agent $0.0000 + gates $0.0000) · cap $1.00 (0% used)" {
		t.Errorf("Spend = %q", s.Spend)
	}
	// Code ticks are the engine's markdown, not content — a card that prints them shows `main`.
	if s.RunId[0] == '`' {
		t.Error("run id kept its backticks")
	}
}

// A state dir with no summary is the normal case for a run that never finished. It must read as
// "nothing to show", never as an error the landing page has to explain.
func TestNoSummaryIsNotAnError(t *testing.T) {
	s, err := Load(t.TempDir())
	if err != nil || s != nil {
		t.Fatalf("Load(empty dir) = %v, %v; want nil, nil", s, err)
	}
	if s, err := Load(""); err != nil || s != nil {
		t.Fatalf("Load(\"\") = %v, %v; want nil, nil", s, err)
	}
}

// A file that exists but carries none of the summary's fields is not a summary — refuse it rather
// than rendering an empty card that claims a run happened.
func TestAFileThatIsNotASummaryIsRefused(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, FileName), []byte("# something else\n\nprose\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	s, err := Load(dir)
	if err != nil || s != nil {
		t.Fatalf("Load(non-summary) = %v, %v; want nil, nil", s, err)
	}
}

// The stage table below the header repeats "Sessions" as a column; the parser must stop at the first
// section heading rather than letting a table row overwrite a header value.
func TestStageTableDoesNotOverwriteTheHeader(t *testing.T) {
	dir := t.TempDir()
	body := "# Run summary — x\n\n- **Outcome:** Completed\n- **Sessions:** 2 (2 deliver)\n\n" +
		"## Stages\n\n- **Sessions:** 99 (nonsense)\n"
	if err := os.WriteFile(filepath.Join(dir, FileName), []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	s, err := Load(dir)
	if err != nil || s == nil {
		t.Fatalf("Load: %v, %v", s, err)
	}
	if s.Sessions != "2 (2 deliver)" {
		t.Errorf("Sessions = %q, want the header's value", s.Sessions)
	}
}
