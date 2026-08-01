# SF6 fix session 36 — the three reds the battery found, and what each one actually was

Conductor ran `engine-full` after session 35 and it failed twice, 3 tests of 1756. None of the three
was a flake and none was fixed by weakening what it measured. Numbers below are from this tree.

## 1. `SF6_1TemplateLessonsTests.TheLessonsFitTheCommandLineBudgetEvenOnAMultiRepoPlan`

> built-in deliver prompt is 8000 chars — bug #15 drops the agent past ~8191

**Not caused by the diff that preceded it.** Measured, not assumed:

```
$ git show HEAD~1:src/Conductor/Core/PromptBuilder.cs > old.cs
$ awk '/"session.md" => """/,/^            """,$/' old.cs | wc -c        # 2585
$ awk '/"session.md" => """/,/^            """,$/' src/Conductor/Core/PromptBuilder.BuiltIns.cs | wc -c
2585
$ diff old_tpl.txt new_tpl.txt && echo IDENTICAL
IDENTICAL
$ git log --oneline -1 -- src/Conductor/Core/ToolContract.cs
8dd1aa3 feat(prompts): the built-ins carry the field lessons (SF6.1)
```

The session template is byte-identical across the SF6.3 split and the tools block has not changed
since SF6.1 — the same commit that added the guard. So the prompt the battery measured at 8000 is
the prompt that passed at "just under 8000" when it was written.

The one term of a composed prompt that changes between runs is `Environment.ProcessId`, rendered in
the tools block's never-kill-a-pid paragraph. The guard had roughly **one character** of margin
against a value whose width changes every run: a four-digit test-host pid passed, a five- or
six-digit one failed. That is a real budget defect rather than a flake — the prompt was sitting
~190 chars from the ~8191 argv cliff of bug #15, where the agent silently never starts.

**Fix: buy real headroom by compressing prose, and tighten the guard rather than relax it.**
Four paragraphs of `ToolContract.cs` were rewritten shorter with every lesson kept — the claim
channel, the mark-in-progress incident, the 56-minute wall of TODO, claim-before-handoff, the
killed-conductor story, the blocked-until contract. Nothing was deleted. Measured after:

| prompt | before | after | budget |
|---|---|---|---|
| deliver, multi-repo | 8000 | **7807** | 7900 |
| deliver, single-repo | ~7597 | **7404** | 7900 |
| fix, multi-repo | — | **7784** | 7900 |
| tools block, multi-repo | ~5754 | **5561** | 6000 |

The guard's ceiling moved **8000 → 7900**, i.e. stricter, and its remarks now say why the margin
exists so nobody re-creates a flush-fit. It also measures both prompts before asserting either, so
a future fix session reads every over-budget number in one run instead of one per rebuild.

## 2. `SC4_4Tests.InjectionRendersDirectlyUnderTheRoleLineOfADeliverPrompt` (bug #22)

`Assert.InRange(LineOf(prompt, "PRE-SESSION RITUAL"), 4, …)` → `-1`. Pre-existing, confirmed by
session 35 and again here: SF6.1 rewrote the built-in session template and the heading became step 1
of `Do, in order:`. The test's intent — nothing the prompt used to put above an injection may sit
above it — is still correct; only its string anchors were stale, so the anchors were re-pointed at
headings the current built-in has (`Do, in order:`, `ORIENT, THEN SAY WHAT YOU ARE TAKING`,
`## Conductor tools`) and the tools contract was added as a fourth. No assertion was removed; the
test now covers more of the prompt than it did before.

## 3. `SF6_3InitScaffoldTests.BuiltInNamesEnumeratesEveryCaseOfTheBuiltInSwitch`

`Substring(-1)`. Session 35 wrote the test against `src/Conductor/Core/PromptBuilder.cs`, then split
the switch out to `PromptBuilder.BuiltIns.cs` in the same commit to stay under the 500-line
architecture ceiling. The path constant went stale silently and failed as an index crash.

Fixed by **locating** the switch instead of naming its file: the test scans every
`src/Conductor/Core/PromptBuilder*.cs` for the signature and asserts exactly one declares it, so the
next split fails with a sentence naming the subject rather than an `ArgumentOutOfRangeException`.

## Verification

```
tools/scratch/sf6-fix-scoped.ps1   # dotnet test Conductor.slnx
```

Scoped run first (SF6_1 + SC4_4 + SF6_3 + SF0_3 + SF6_2 + InitCommand + a scratch probe that prints
the sizes): **65 passed, 1 failed — the probe, which fails by design to print its measurements.**
The probe was deleted; the numbers it printed are the table above.

Full suite: see `## Full suite` below.

## Full suite

`dotnet test Conductor.slnx`, this tree, after all three fixes — the whole suite rather than a
filter because `ToolContract.cs` prose rides in every composed prompt and several suites assert
sentences from it:

```
Passed!  - Failed:     0, Passed:  1756, Skipped:     0, Total:  1756, Duration: 3 m 6 s
```

Same 1756 tests the battery ran; the three it failed now pass and nothing else moved.
Log: `.conductor/bg-logs/powershell-20260801-155303730.log`. Committed as `be0394d`.

## 4. The fix template ordered a move the engine refuses (second commit)

Measured here, in the engine's own output, while trying to follow this session's own step 1:

```
$ conductor task --in-progress SF6.1
refused: SF6.1 is DONE and stayed DONE — --in-progress starts a TODO checkpoint
and will not reopen a claimed one; use `conductor task --todo SF6.1` if you really mean to
```

A fix session by definition repairs a checkpoint an earlier session already claimed, so this
refusal is guaranteed rather than incidental — and the escape the message offers, `--todo`,
downgrades a real delivery to TODO, which is exactly what a fix session that dies before
re-claiming would leave behind. `fix.md` step 1 now says what to do when the board refuses, and
`SF6_1TemplateLessonsTests.TheFixTemplateSaysWhatToDoWhenTheBoardRefusesToReopenAClaimedCheckpoint`
pins it, including that a delivering session does not pay bytes for a fix-only lesson.

Paid for in bytes, not added to the bill: step 1's own wording, steps 3–5 and the locked-by-conductor
paragraph were compressed, and one clause added to the tools block earlier was taken back out.

| prompt | battery red | after commit 1 | after commit 2 | budget |
|---|---|---|---|---|
| deliver, multi-repo | 8000 | 7807 | **7803** | 7900 |
| deliver, single-repo | — | 7404 | **7400** | 7900 |
| fix, multi-repo | — | 7784 | **7731** | 7900 |
| fix, single-repo | — | 7381 | **7328** | 7900 |
| tools block, multi-repo | ~5754 | 5561 | **5557** | 6000 |

Scoped verification of every suite that asserts prompt prose — `SF6_1`, `SF6_2`, `SF6_3`, `SC4_4`,
`SF0_3`, `W2OnePrompt`, `Architecture` — plus the probe:

```
Failed!  - Failed:     1, Passed:    68, Total:    69, Duration: 7 s
```

The one failure is the scratch probe, which fails by design to print the row above; it was deleted
after the run. Log: `.conductor/bg-logs/powershell-20260801-160114116.log`.
