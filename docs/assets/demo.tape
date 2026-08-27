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
#
# WHAT THE TOUR VISITS (CH2.1). Every surface below is reached by the mnemonic declared in
# face-go/internal/tui/model.go's tabKey, so a rebound key is a one-line edit here. The order is a
# story, not the tab strip's order: where am I, what does it want from me, what is it doing, what is
# left, what has happened, what got out, who hears about it, what is it working from, what can I do.
#
# STALENESS: docs/assets/demo.manifest.json records what this tape was recorded from, and the CH2.2
# check fails the build when the Face has moved past the GIF. Re-record (the command at the top),
# then refresh the manifest — never the other way round.

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

# The fleet is what the run switcher offers (KS2.4) and it arrives in CONDUCTOR_FLEET, from the
# engine, even in --demo — cmd/conductor-face/main.go reads the envelope before it picks a source.
# docs/assets/demo-fleet.json is the synthetic one, so the tour can show the machine's other runs
# without an engine anywhere. Assignment context, so no quoting: bash does not word-split there.
Hide
Type "cd face-go && export CONDUCTOR_FLEET=$(cat ../docs/assets/demo-fleet.json)" Enter
Type "./bin/conductor-face --demo" Enter
Sleep 3s
Show

# Home — the landing page: run, budget, gates, workspace, next steps.
Sleep 3s

# The inbox (SF4.2). Home caps the owner queue and hands off with "+N more — w"; `w` is the uncapped
# pane: every obligation, its age, what it unblocks, and the exact command that clears it.
Type "w"
Sleep 4s
Escape
Sleep 500ms

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

# The report — the run's own account of itself, the file that gets published.
Type "r"
Sleep 3s

# The courier (Divan). Telegram takes 'g': 't' is History's spine. Readiness is computed, not
# claimed — a token without a chat id delivers nothing and the tab says so.
Type "g"
Sleep 4s

# Editing the plan without restarting the run.
Type "p"
Sleep 3s

# The ':' command palette — every control verb, destructive ones confirm first.
Type ":"
Sleep 3s

# ...and the one Local verb that changes a screen rather than a setting: the run switcher, this
# machine's other runs, live and finished, swapped into this same process (KS2.4).
Type "switch"
Sleep 1s
Enter
Sleep 4s
Escape
Sleep 500ms

Hide
Type "q"
