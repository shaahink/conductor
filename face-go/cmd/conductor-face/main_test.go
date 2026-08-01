package main

import (
	"os"
	"path/filepath"
	"testing"
)

// chdir moves the process into dir for the duration of one test.
func chdir(t *testing.T, dir string) {
	t.Helper()
	prev, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Chdir(dir); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chdir(prev) })
}

// The state dir has to be findable AFTER the run that owns it has finished — that is the entire
// point of the last-run card. The engine deletes control-plane.json on the way out
// (ControlPlaneServer.Dispose, so no client is pointed at a dead port), so this walk must key on the
// DIRECTORY. Asserting through a file planted inside the returned path proves the property that
// matters — the summary is reachable — without depending on how the OS spells the temp root.
func TestDiscoverStateDirFindsAFinishedRunWithNoControlPlaneFile(t *testing.T) {
	root := t.TempDir()
	state := filepath.Join(root, ".conductor")
	deep := filepath.Join(root, "src", "Conductor", "Core")
	for _, d := range []string{state, deep} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			t.Fatal(err)
		}
	}
	// Exactly the on-disk shape of a completed run: a summary, and NO discovery file.
	const marker = "- **Outcome:** Completed\n"
	if err := os.WriteFile(filepath.Join(state, "RUN-SUMMARY.md"), []byte(marker), 0o644); err != nil {
		t.Fatal(err)
	}

	chdir(t, deep)
	got := discoverStateDir()
	if got == "" {
		t.Fatal("walking up from inside the repo found no state dir; the last-run card can never render")
	}
	if filepath.Base(got) != ".conductor" {
		t.Errorf("discovered %q, which is not a state dir", got)
	}
	body, err := os.ReadFile(filepath.Join(got, "RUN-SUMMARY.md"))
	if err != nil {
		t.Fatalf("the discovered dir does not hold the run summary: %v", err)
	}
	if string(body) != marker {
		t.Errorf("read %q from the discovered dir, want the summary we planted", body)
	}
}

// Nested runs exist (a scratch rig inside a checkout). The nearest state dir is the one you are in.
func TestDiscoverStateDirTakesTheNearestOne(t *testing.T) {
	root := t.TempDir()
	inner := filepath.Join(root, "scratch")
	deep := filepath.Join(inner, "a", "b")
	for _, d := range []string{filepath.Join(root, ".conductor"), filepath.Join(inner, ".conductor"), deep} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			t.Fatal(err)
		}
	}
	if err := os.WriteFile(filepath.Join(inner, ".conductor", "mine"), []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}

	chdir(t, deep)
	got := discoverStateDir()
	if _, err := os.Stat(filepath.Join(got, "mine")); err != nil {
		t.Errorf("discovered %q, want the NEAREST state dir (the one holding 'mine'): %v", got, err)
	}
}

// A plain file named .conductor is not a state dir, and must not stop the walk.
func TestDiscoverStateDirIgnoresAFileOfThatName(t *testing.T) {
	root := t.TempDir()
	sub := filepath.Join(root, "sub")
	if err := os.MkdirAll(sub, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(sub, ".conductor"), []byte("not a dir"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(root, ".conductor"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, ".conductor", "mine"), []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}

	chdir(t, sub)
	got := discoverStateDir()
	if _, err := os.Stat(filepath.Join(got, "mine")); err != nil {
		t.Errorf("discovered %q; a FILE named .conductor must not end the walk: %v", got, err)
	}
}
