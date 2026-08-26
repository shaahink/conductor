# CH1.1 — the rendered board page is one document whatever the checkout did to the source

Session 1, stage CH1, 2026-08-26. Branch `feat/charkh`, working tree checked out with
`core.autocrlf` producing CRLF (`file src/Conductor.Core/Publishing/BoardSnapshotHtml.cs` →
`HTML document, Unicode text, UTF-8 text, with CRLF line terminators`).

## The cause, measured rather than read off the brief

`src/Conductor.Core/Publishing/BoardSnapshotHtml.cs:277` (pre-fix) declared the inline CSS as a
C# **raw string literal**, which inherits the line endings of its *source file*. Every other line
of the renderer appends an explicit `\n`, so on a CRLF checkout the CSS block alone carried CRLF.
Measured directly by the new test's failure message under the negative control below: the first
carriage return in the rendered document is at **offset 315**, inside the CSS, between the
`:root{...}` rule and the `@media(prefers-color-scheme:dark)` rule.

`BoardSnapshotPublisher.Publish` writes with `AtomicFile.Write` →
`File.WriteAllText(..., new UTF8Encoding(false))`, which does **not** normalise; the normalisation
is in the test, which reads the file back through `.Replace("\r\n", "\n")` and compares to
`Render()`. So the two sides disagreed by exactly the CSS block's line endings.

## The fix

`BoardSnapshotHtml.cs`: the literal is wrapped at its own site —

    private static readonly string Css = Lf("""…""");

with `Lf` a private helper that collapses `\r\n` and bare `\r` to `\n` and returns the input
unchanged when there is no carriage return (so an LF checkout allocates nothing).

**Deliberately not done:** normalising the finished document at the seam (`return Lf(sb.ToString())`
at the end of `Render`). It fixes the bug too, and it makes the property test below unable to fail —
a guard that cannot go red is not a guard on the *next* raw string. That reasoning is in the doc
comment on `Lf` so the next reader does not "simplify" it back.

## The property, not the symptom

New `tests/Conductor.Tests/CH1_1BoardPageLineEndingsTests.cs` asserts that the rendered document
carries **no carriage return at all**, over both shapes of the page — the full board
(`DV6_3BoardPageTests.Snapshot()`, promoted `private`→`internal` so there is one fixture, not two)
and the empty board, which between them take both sides of every "is there anything here" branch in
the renderer. The failure message names the cause and prints the bytes around the offending offset.
A third test pins that `Render()` output is byte-identical to its own LF normalisation — the same
equality `DV6_3BoardPageTests` makes across a file round-trip, asserted where it can name the cause.

## Negative control — RED on the pre-fix source, same machine, same checkout

`git stash push -- src/Conductor.Core/Publishing/BoardSnapshotHtml.cs`, rebuild, same filter:

    Failed  CH1_1BoardPageLineEndingsTests.The_full_board_renders_without_a_single_carriage_return
      the full board carries a carriage return at offset 315 — … Around it:
      655f;--line:#dedcd5;--card:#fff;--ok:#1a7f37;--warn:#9a3412}<CR><LF>@media(prefers-color-scheme:dark){…
    Failed  CH1_1BoardPageLineEndingsTests.The_empty_board_renders_without_a_single_carriage_return
    Failed  CH1_1BoardPageLineEndingsTests.Rendering_is_byte_identical_to_its_own_LF_normalisation
    Failed  DV6_3BoardPageTests.Publishing_writes_one_file_atomically_and_hands_back_what_it_rendered
    Failed!  - Failed: 4, Passed: 13, Skipped: 0, Total: 17

Full output: `.conductor/evidence/CH1/ch1-1-red.txt`.

The fourth failure is the checkpoint's premise confirmed: the DV6.3 publish test fails on this
checkout for exactly this cause, and no other.

## GREEN with the fix, same commands

    Passed!  - Failed: 0, Passed: 17, Skipped: 0, Total: 17

Full output: `.conductor/evidence/CH1/ch1-1-green.txt`. Nothing was weakened to get there — the
count went 13→17 passing with four tests added, none removed, skipped or relaxed.

## Not in scope, recorded

The class is wider than this file: 20 files under `src/` hold `"""` raw string literals, and any of
them whose bytes are written to disk or compared byte-wise carries the same latent dependency on how
the repository was cloned. Nothing else has a failing test today, so nothing else was touched;
recorded in the ledger for whoever meets the second instance.
