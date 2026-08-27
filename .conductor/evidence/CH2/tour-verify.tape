# CH2.1 verification tape: the same tour as docs/assets/demo.tape, but it writes a PNG at every
# surface so a reviewer (and the session that recorded it) can see each frame without a TTY.
# Same geometry as the real tape - 1176x736 = 110x34 - or the frames prove nothing about the GIF.
Output .conductor/evidence/CH2/tour-verify.gif

Set Shell        bash
Set FontFamily   "JetBrainsMono Nerd Font Mono"
Set FontSize     15
Set Width        1176
Set Height       736
Set Padding      16
Set Theme        "Catppuccin Mocha"
Set TypingSpeed  10ms
Set Framerate    24

Hide
Type "cd face-go && export CONDUCTOR_FLEET=$(cat ../docs/assets/demo-fleet.json)" Enter
Type "./bin/conductor-face --demo" Enter
Sleep 3s
Show

Sleep 2s
Screenshot .conductor/evidence/CH2/01-home.png

Type "w"
Sleep 2s
Screenshot .conductor/evidence/CH2/02-inbox.png
Escape
Sleep 500ms

Type "a"
Sleep 2s
Screenshot .conductor/evidence/CH2/03-agent.png

Type "b"
Sleep 2s
Screenshot .conductor/evidence/CH2/04-board.png

Down
Sleep 500ms
Enter
Sleep 2s
Screenshot .conductor/evidence/CH2/05-card.png
Escape
Sleep 500ms

Type "t"
Sleep 2s
Screenshot .conductor/evidence/CH2/06-history.png

Type "r"
Sleep 2s
Screenshot .conductor/evidence/CH2/07-report.png

Type "g"
Sleep 2s
Screenshot .conductor/evidence/CH2/08-courier.png

Type "p"
Sleep 2s
Screenshot .conductor/evidence/CH2/09-plan.png

Type ":"
Sleep 1s
Screenshot .conductor/evidence/CH2/10-palette.png
Type "switch"
Sleep 1s
Screenshot .conductor/evidence/CH2/11-palette-switch.png
Enter
Sleep 2s
Screenshot .conductor/evidence/CH2/12-switcher.png
Escape
Sleep 500ms

Hide
Type "q"
