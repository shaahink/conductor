# VHS tape for the README demo — a full-colour recording of the LIVE Face binary.
#
#   vhs docs/assets/demo.tape
#
# Requires vhs + ttyd + ffmpeg (https://github.com/charmbracelet/vhs). ttyd has no Windows build,
# so on Windows use the committed generator instead, which builds the same tour out of face-go's
# golden frames (real renders, but ANSI-stripped, so monochrome):
#
#   powershell -File tools/demo/make-demo-gif.ps1
#
# Build the binary first:
#
#   cd face-go && go build -o bin/conductor-face ./cmd/conductor-face/
#
# --demo runs fully offline against synthetic data: no engine, no credentials, no spend.

Output docs/assets/demo.gif

Set Shell        bash
Set FontFamily   "CaskaydiaCove Nerd Font Mono"
Set FontSize     15
Set Width        1040
Set Height       680
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

# The work board.
Type "k"
Sleep 3s

# One card, and the prompt block it contributes to the next session.
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
