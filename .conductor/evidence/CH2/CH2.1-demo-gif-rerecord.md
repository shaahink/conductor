# CH2.1 - docs/assets/demo.gif re-recorded against what shipped

Session 3, stage CH2, 2026-08-27. Repo C:/code/conductor, branch feat/charkh.

## 1. Docker, verified FIRST, with the exact output (trap 20 / plan CH2 preamble)

Trap 20 was REPRODUCED, not merely repeated. First command of the session:

    PS> docker version
    Client:
     Version:           29.5.2
     API version:       1.54
     Go version:        go1.26.3
     Git commit:        79eb04c
     Built:             Wed May 20 14:40:41 2026
     OS/Arch:           windows/amd64
     Context:           desktop-linux
    failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine;
    check if the path is correct and if the daemon is running:
    open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.
    EXIT=1

The client is on PATH; the daemon was not running. Docker Desktop is installed at
`C:\Users\shahi\AppData\Local\Programs\DockerDesktop` (NOT `C:\Program Files\Docker`, which is why
a `Program Files` probe finds nothing). Starting it:

    PS> Start-Process "$env:LOCALAPPDATA\Programs\DockerDesktop\Docker Desktop.exe"
    PS> docker version --format '{{.Server.Version}}'
    29.5.2
    EXIT=0

**Docker works.** No GIF was hand-assembled from stills; the recorder ran as designed.

## 2. The pipeline, proven before the tape was touched

A baseline run of the UNCHANGED tape through `tools/demo/make-demo-gif.ps1`
(`.conductor/bg-logs/demogif-20260827-001839271.log`) pulled `ghcr.io/charmbracelet/vhs:latest`,
cross-compiled the Face for linux/amd64 and wrote `docs/assets/demo.gif (736.7 KB)`. So the
recorder, the container, the cross-compile and the mount all work on this machine as shipped.

## 3. What the tour now visits, and why

`docs/assets/demo.tape` predated the courier, the inbox and the run switcher. Extended tour, in the
order the tape drives it - each key is the mnemonic declared in `face-go/internal/tui/model.go:57`
(`tabKey`), not a literal invented here:

| # | key | surface | added by |
|---|-----|---------|----------|
| 1 | (start) | Home | U1.1 |
| 2 | `w` | **the inbox** - owner queue, uncapped pane (`tab_home_owner.go:139`) | SF4.2 |
| 3 | `a` | Agent transcript | - |
| 4 | `b` | Kanban board (the Face's board) | - |
| 5 | Down/Enter | one card + the prompt block it contributes | - |
| 6 | `t` | History spine | SF1.3 fold |
| 7 | `r` | **Report** - the run's own account, the file that gets published | - |
| 8 | `g` | **the courier** - Telegram readiness, computed not claimed | Divan |
| 9 | `p` | Plan editor | - |
| 10 | `:` then `switch` + Enter | **the run switcher** - this machine's other runs, live and finished, swapped into the same process | KS2.4 |

MEASURED, not assumed:

- `switch` is the only verb in `allVerbs` whose Key or Desc contains "switch"
  (`cmdbar.go:91`, filter at `cmdbar.go:290-305`), and it is declared before the theme verbs are
  appended (`cmdbar.go:109`), so `paletteSelected = 0` lands on it. It is `Safe: true`, so Enter
  does not raise a confirm.
- The switcher needs a fleet, and the fleet arrives in `CONDUCTOR_FLEET` even in `--demo`:
  `cmd/conductor-face/main.go:82` parses the envelope BEFORE the source switch, and line 134
  attaches it with `WithFleet`. With no fleet `openSwitcher` (`switcher.go:69-73`) shows a toast
  instead of a screen - which is what the old tape would have recorded.
- So `docs/assets/demo-fleet.json` is the synthetic envelope (3 live runs, 2 past, pastTotal 18),
  exported in the tape's hidden startup line. Bash assignment context does not word-split, so
  `export CONDUCTOR_FLEET=$(cat ...)` needs no quoting.
- Geometry UNCHANGED at 1176x736 = 110x34, the size `internal/tui/testdata/golden/*.golden`
  are rendered at (`home_demo.golden` is exactly 34 lines).

## 4. What a reviewer sees - captured, not asserted

`tour-verify.tape` (kept beside this file) drives the same tour at the same geometry and writes a
PNG at every stop. Log: `.conductor/bg-logs/demoverify-20260827-002326289.log`. All 12 frames
rendered; the three NEW surfaces are kept here as proof:

- `02-inbox.png` - four obligations, each with age, what it unblocks and the command that clears it
- `08-courier.png` - "will not deliver yet", the guided setup, the readiness fields
- `12-switcher.png` - 3 runs answering on this machine, 2 of 18 past runs, the attached row marked

## 5. The recording

    .conductor/bg-logs/demogif2-20260827-002514709.log
    wrote docs/assets/demo.gif  (1476.2 KB)

736.7 KB -> 1476.2 KB for four more surfaces. GitHub's inline README cap is 10 MB.
