package widgets

import "testing"

// RepoBase feeds the top bar's workspace chip (U1.2). It must not use filepath.Base: the repo path
// arrives over the wire from whatever OS the ENGINE runs on, so a Windows path has to shorten
// identically whether this binary was built for Windows or Linux — otherwise golden frames would
// differ per platform.
func TestRepoBase(t *testing.T) {
	cases := []struct {
		name string
		in   string
		want string
	}{
		{"windows path", `C:\Code\conductor-baton`, "…/conductor-baton"},
		{"posix path", "/home/dev/code/conductor", "…/conductor"},
		{"mixed separators", `C:\Code/conductor-baton`, "…/conductor-baton"},
		{"trailing separator", `C:\Code\conductor-baton\`, "…/conductor-baton"},
		{"trailing posix separator", "/home/dev/conductor/", "…/conductor"},
		{"bare name has nothing to trim", "conductor-baton", "conductor-baton"},
		{"empty stays empty", "", ""},
		{"separators only", `\\`, ""},
		{"drive root", `C:\`, "C:"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := RepoBase(tc.in); got != tc.want {
				t.Errorf("RepoBase(%q) = %q, want %q", tc.in, got, tc.want)
			}
		})
	}
}
