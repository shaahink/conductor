# CH2.2 - staleness becomes a gate

Session 3, stage CH2, 2026-08-27. Branch feat/charkh.

## What was ported, and from where

payesh's `scripts/seo.mjs` section 6: a card is *a picture of numbers, taken once*, and the numbers
underneath keep moving, so `--cards` records the text each card rendered when the PNG was taken and
the check re-renders and compares. It refuses the merge on a mismatch. That is why payesh's cards
were caught going stale and conductor's GIF was not.

The GIF has no text to re-render. It does not need one: **what it depicts is the Face's surfaces**,
and those are declared, in this repo, in `face-go/internal/tui/model.go` - `tabKey`, `tabNames`,
`foldedTabs`. So the manifest records the inventory the GIF was recorded against and the check
recomputes it from the declarations.

## The three pieces

| file | what it is |
|------|------------|
| `docs/assets/demo.manifest.json` | what the GIF was recorded FROM |
| `face-go/internal/tui/demo_tour_test.go` | the check |
| `tools/demo/make-demo-gif.ps1` (tail) | refreshes the manifest, as the recording's LAST step |

**Where the refusal binds.** The check is a Go test in `package tui`, so `go test ./...` runs it -
which is already the `face-full` gate in `plans/charkh/core.plan.json:133` AND both CI jobs
(`.github/workflows/ci.yml:87` windows, `:131` ubuntu). No new runner, no new script to remember.

**In-package, not a script.** The inventory is read from `tabKey`/`tabNames`/`foldedTabs`
themselves. A script outside the module would have to parse Go source or re-type the list, and a
re-typed list is trap 21's failure exactly: it keeps asserting after the vocabulary under it moved.

**Why the recorder writes it.** A manifest that can be refreshed without re-recording is a check
that can be silenced by editing the thing it checks. `-write-demo-manifest` exists so the recorder
has something to call, and says so in its own flag help. A `-Tape` run (the CH2.1 verification tape)
leaves the manifest alone rather than telling it the README's GIF is something it is not.

## What it keys on, and what it deliberately does not

- The tape is hashed by its **commands** - comments, blank lines, trailing whitespace and CR
  stripped. Two reasons, both already paid for here: the tape is text under `core.autocrlf`, so a
  byte hash disagrees between a Windows checkout and CI (CH1.1's carriage return, one file over);
  and the tape is half prose, so a byte hash would demand a re-record for a fixed typo. Seed 4b
  below is the negative control for that.
- The **cell** geometry is measured from a golden (`home_demo.golden` is 34 lines of 110 runes),
  not asserted as 110x34. A golden rebaseline at another terminal size therefore turns this red -
  the GIF would be a recording at a size nothing is test-covered at.
- The **landing frame** comes from `New(...).tab`, a fresh Model asked what tab it opens on, not
  from the comment on `TabHome` saying it is the landing page.
- **No pixels are diffed.** The brief said they need not be, and they are not.

## Proven by seeding each failure

`.conductor/evidence/CH2/CH2.2-seeded-failures.txt` is the full run. Every seed was reverted and the
check is green again at the end (two negative controls, first and last). Summary:

| seed | what was seeded | verdict |
|------|-----------------|---------|
| 0 | nothing | **ok** |
| 1 | the Face grew a tab the GIF predates - Telegram dropped from the recorded inventory, which is literally the state this repo was in before CH2.1 | FAIL: `+ Telegram ("g") is new since the recording` |
| 2 | a mnemonic rebound | FAIL: `~ Kanban moved from "z" to "b"` |
| 3 | a key that was not a tab has since been claimed by one | FAIL: `stop 4: "b" opened some pane when the GIF was recorded, and is now the mnemonic for the Kanban tab` |
| 4 | the tour changed, nobody re-recorded (one more `Sleep`) | FAIL: `48 command lines then, 49 now` |
| 4b | **a comment-only edit to the tape** | **ok** - prose is not a re-record |
| 5 | geometry moved (`Set Width 1400`) | FAIL: `the tape's geometry moved (1176x736 recorded, 1400x736 now)` |
| 6 | the committed GIF is not the one the manifest describes | FAIL: bytes and sha printed both ways |
| 7 | a tab neither toured nor excused (`Knowledge`'s reason deleted) | FAIL: `not on the tour and ... does not say why` |
| 8 | the fleet the switcher renders from changed | FAIL: `the run switcher in the GIF shows runs that are no longer the demo's` |

Every failure message ends with the one command that fixes it:
`Re-record, which refreshes the manifest:  powershell -File tools/demo/make-demo-gif.ps1`

## The payesh leftover check, ported too

payesh fails when a declared card is used by no page. Here: every tab is either **on the tour** or
**excused in writing** in `notVisited`. Three are excused today - Procs, Templates (which renders
built-in defaults in `--demo` on purpose) and Knowledge - each with its reason in the manifest. A
new tab cannot be quietly left out of the GIF; seed 7 is that check firing.
