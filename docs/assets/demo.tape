# VHS tape for the README demo — a full-colour recording of the LIVE Face binary.
#
# Windows (the dev box) — one command, no local vhs/ttyd install:
#
#   powershell -File tools/demo/make-demo-gif.ps1
#
# That cross-compiles the Face for linux and runs this tape inside the official VHS container
# (ghcr.io/charmbracelet/vhs), which bundles vhs + ttyd + ffmpeg. ttyd has no Windows build, which
# is why the container is the Windows path rather than a local install.
#
# macOS/Linux with vhs on PATH:
#
#   cd face-go && go build -o bin/conductor-face ./cmd/conductor-face/
#   vhs docs/assets/demo.tape
#
# --demo runs fully offline against synthetic data: no engine, no credentials, no spend.
#
# GEOMETRY: 1176x736 at font size 15 gives the shell exactly 110x34 — the same terminal size
# face-go/internal/tui/golden_test.go renders every golden at. Change either number and the Face
# reflows to a layout no test covers, so re-probe if you touch them:
#   Type "echo $(tput cols)x$(tput lines)"
#
# FONT: JetBrainsMono Nerd Font Mono ships inside the VHS image, so the container needs no font
# mounting. The Face uses no Nerd Font private-use glyphs — only box-drawing and common symbols —
# so any monospace font with box-drawing coverage substitutes cleanly.

Output docs/assets/demo.gif

Set Shell        bash
Set FontFamily   "JetBrainsMono Nerd Font Mono"
Set FontSize     15
Set Width        1176
Set Height       736
Set Padding      16
Set Theme        "Catppuccin Mocha"
Set TypingSpeed  60ms
Set Framerate    24

Hide
Type "cd face-go && ./bin/conductor-face --demo" Enter
Sleep 3s
Show

# Home — the landing page: run, budget, gates, workspace, next steps.
Sleep 3s

# The live agent transcript.
Type "a"
Sleep 3s

# The work board. 'b' for board — 'k' is Knowledge (internal/tui/model.go:41).
Type "b"
Sleep 3s

# One card, and the prompt block it contributes to the next session. Down first: Enter on the
# board with nothing selected is a no-op, which is what made the old 'k' + Enter pair silently
# record two identical Knowledge frames.
Down
Sleep 1s
Enter
Sleep 3s
Escape
Sleep 500ms

# What happened, when.
Type "t"
Sleep 3s

# Editing the plan without restarting the run.
Type "p"
Sleep 3s

# The ':' command palette — every control verb, destructive ones confirm first.
Type ":"
Sleep 3s
Escape

Hide
Type "q"
