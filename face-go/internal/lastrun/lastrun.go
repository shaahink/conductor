// Package lastrun reads the one artifact a finished run leaves behind: RUN-SUMMARY.md, written into
// the state dir by the engine's RunSummary when a run completes (src/Conductor/Core/RunSummary.cs).
//
// SF2.1 needs it because the Face's honesty problem when the engine is gone is not "say disconnected
// louder" — it is that a dead run is invisible. The control plane dies with the process, /state stops
// answering, and Home used to answer "what happened?" with a dial error and instructions to start a
// run it had just been watching. The summary is rebuilt from run.db by the engine precisely so it
// outlives that process, so the Face reads it from disk rather than inventing a cache of its own.
//
// This is the ONLY file the Face reads out of the state dir, and it is read-only. Everything else
// still comes from the control plane.
package lastrun

import (
	"bufio"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// FileName is the engine's own name for the artifact (RunSummary.SummaryPath).
const FileName = "RUN-SUMMARY.md"

// Summary is the handful of lines a landing page can show: what ran, how it ended, how far it got,
// what it cost. Values are kept as the engine phrased them (markup stripped) rather than re-derived,
// so the card and the file can never disagree.
type Summary struct {
	Plan        string
	RunId       string
	Outcome     string
	Repo        string
	EndedUtc    time.Time
	Sessions    string
	Checkpoints string
	Spend       string
	Path        string
}

// Load reads stateDir/RUN-SUMMARY.md. A missing file is not an error worth surfacing — a run that
// never completed here simply has no summary — so it returns (nil, nil) and lets Home say nothing.
// Only a file that exists and cannot be read or parsed returns an error.
func Load(stateDir string) (*Summary, error) {
	if stateDir == "" {
		return nil, nil
	}
	path := filepath.Join(stateDir, FileName)
	f, err := os.Open(path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	defer f.Close()

	s := &Summary{Path: path}
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64*1024), 1<<20)
	first := true
	for sc.Scan() {
		line := sc.Text()
		if first {
			// The engine writes the file with a UTF-8 BOM (Reporter.Utf8Bom); Go does not strip it.
			// Written as an escape because a literal BOM is a compile error in a Go source file.
			line = strings.TrimPrefix(line, "\ufeff")
			first = false
		}
		key, val, ok := bullet(line)
		if !ok {
			// The bullets are the header block: the first table ends it, and the stage rows below are
			// not this card's business.
			if strings.HasPrefix(line, "## ") {
				break
			}
			continue
		}
		switch key {
		case "Plan":
			s.Plan, s.RunId = splitOnDot(val, "run ")
		case "Repo":
			s.Repo, _ = splitOnDot(val, "")
		case "Outcome":
			s.Outcome = val
		case "Wall clock":
			s.EndedUtc = parseEnd(val)
		case "Sessions":
			s.Sessions = val
		case "Checkpoints":
			s.Checkpoints = val
		case "Spend":
			s.Spend = val
		}
	}
	if err := sc.Err(); err != nil {
		return nil, err
	}
	if s.Plan == "" && s.Outcome == "" {
		// A file in the state dir that carries none of the summary's own fields is not a summary.
		return nil, nil
	}
	return s, nil
}

// bullet matches the engine's "- **Key:** value" header lines and returns the key and the value with
// markdown emphasis and code ticks removed — the card is text, not markdown.
func bullet(line string) (key, val string, ok bool) {
	rest, ok := strings.CutPrefix(strings.TrimSpace(line), "- **")
	if !ok {
		return "", "", false
	}
	key, val, ok = strings.Cut(rest, ":**")
	if !ok {
		return "", "", false
	}
	return strings.TrimSpace(key), strings.TrimSpace(strings.ReplaceAll(val, "`", "")), true
}

// splitOnDot splits one of the engine's "a · b" values, returning the head and the tail with the
// given label removed ("run 20260801-x" → "20260801-x").
func splitOnDot(val, label string) (head, tail string) {
	head, rest, found := strings.Cut(val, "·")
	head = strings.TrimSpace(head)
	if !found {
		return head, ""
	}
	rest = strings.TrimSpace(rest)
	if label != "" {
		if t, ok := strings.CutPrefix(rest, label); ok {
			rest, _, _ = strings.Cut(t, "·")
			return head, strings.TrimSpace(rest)
		}
		return head, ""
	}
	return head, rest
}

// parseEnd pulls the END of the wall-clock line ("<start> UTC → <end> UTC · 27h 59m"). The end is
// when the run stopped, which is the age the card reports; the start is already implied by the
// duration the engine wrote next to it.
func parseEnd(val string) time.Time {
	_, after, found := strings.Cut(val, "→")
	if !found {
		return time.Time{}
	}
	stamp, _, _ := strings.Cut(strings.TrimSpace(after), "·")
	stamp = strings.TrimSuffix(strings.TrimSpace(stamp), " UTC")
	t, err := time.ParseInLocation("2006-01-02 15:04", strings.TrimSpace(stamp), time.UTC)
	if err != nil {
		return time.Time{}
	}
	return t
}
